using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Globalization;

public class CPHInline
{
    // ── Konfiguration ──────────────────────────────────────────────
    private const string TwitchDLPath    = @"G:\Twitch DL Tool\twitch-dl.exe";
    private const string TempDownloadDir = @"D:\Stream\YoutubeQueue";   // Temporärer Ordner für Downloads
    private const string CsvPath         = @"D:\Stream\Records\youtube_uploads.csv";

    // Google OAuth – aus Google Cloud Console
    private const string ClientId        = "DEINE_CLIENT_ID";
    private const string ClientSecret    = "DEIN_CLIENT_SECRET";
    private const string TokenPath       = @"D:\Stream\Records\.youtube_token"; // Gespeichertes Token

    private const string RedirectUri     = "http://localhost:8080/";
    private const string Scope           = "https://www.googleapis.com/auth/youtube.upload";
    // ──────────────────────────────────────────────────────────────

    private static readonly HttpClient Http = new HttpClient();

    public bool Execute()
    {
        CPH.LogInfo("[YTUploader] ── Upload gestartet ─────────────────────");

        // 1. Twitch Metadaten aus Streamer.Bot
        CPH.TryGetArg("gameName",    out string gameName);
        CPH.TryGetArg("streamTitle", out string streamTitle);

        if (string.IsNullOrWhiteSpace(streamTitle))
            streamTitle = string.IsNullOrWhiteSpace(gameName) ? "Stream" : gameName;

        string safeTitle = Regex.Replace(streamTitle, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[YTUploader] Titel: {safeTitle}");

        // 2. Temp Ordner anlegen
        if (!Directory.Exists(TempDownloadDir))
            Directory.CreateDirectory(TempDownloadDir);

        // 3. Neueste Twitch VOD ID holen
        string vodId = GetLatestTwitchVodId();
        if (string.IsNullOrWhiteSpace(vodId))
        {
            CPH.LogError("[YTUploader] Konnte keine VOD ID von Twitch holen – Abbruch.");
            return false;
        }
        CPH.LogInfo($"[YTUploader] Twitch VOD ID: {vodId}");

        // Bereits hochgeladen? CSV prüfen
        if (AlreadyUploaded(vodId))
        {
            CPH.LogInfo($"[YTUploader] VOD {vodId} wurde bereits hochgeladen – überspringe.");
            return true;
        }

        // 4. VOD downloaden
        string outputFile = Path.Combine(TempDownloadDir, $"{vodId}.mp4");
        bool downloaded = DownloadVod(vodId, outputFile);
        if (!downloaded)
        {
            CPH.LogError("[YTUploader] Download fehlgeschlagen – Abbruch.");
            return false;
        }

        // 5. OAuth Token holen (oder erneuern)
        string accessToken = GetAccessToken();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            CPH.LogError("[YTUploader] Kein gültiges OAuth Token – Abbruch.");
            return false;
        }

        // 6. Auf YouTube hochladen
        string youtubeVideoId = UploadToYoutube(outputFile, safeTitle, accessToken);
        if (string.IsNullOrWhiteSpace(youtubeVideoId))
        {
            CPH.LogError("[YTUploader] YouTube Upload fehlgeschlagen.");
            WriteCsvEntry(vodId, safeTitle, "FEHLGESCHLAGEN", "");
            return false;
        }

        CPH.LogInfo($"[YTUploader] ✅ Erfolgreich hochgeladen! YouTube ID: {youtubeVideoId}");
        WriteCsvEntry(vodId, safeTitle, "OK", youtubeVideoId);

        // 7. Temp Datei aufräumen
        try { File.Delete(outputFile); } catch { }

        CPH.LogInfo("[YTUploader] ── Upload abgeschlossen ──────────────────");
        return true;
    }

    // ── Twitch VOD ────────────────────────────────────────────────

    private string GetLatestTwitchVodId()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = TwitchDLPath,
                Arguments              = "videos --limit 1 --json",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var proc = Process.Start(psi))
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var match = Regex.Match(output, @"""id""\s*:\s*""?(\d+)""?");
                return match.Success ? match.Groups[1].Value : null;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] GetLatestTwitchVodId Exception: {ex.Message}");
            return null;
        }
    }

    private bool DownloadVod(string vodId, string outputPath)
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Downloade VOD {vodId}...");
            var psi = new ProcessStartInfo
            {
                FileName               = TwitchDLPath,
                Arguments              = $"download {vodId} --output \"{outputPath}\" --quality source",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var proc = Process.Start(psi))
            {
                proc.WaitForExit();
                bool success = proc.ExitCode == 0 && File.Exists(outputPath);
                if (success)
                    CPH.LogInfo($"[YTUploader] ✅ Download abgeschlossen: {outputPath}");
                else
                    CPH.LogError($"[YTUploader] ❌ Download fehlgeschlagen (Exit: {proc.ExitCode})");
                return success;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] DownloadVod Exception: {ex.Message}");
            return false;
        }
    }

    // ── OAuth ─────────────────────────────────────────────────────

    private string GetAccessToken()
    {
        // Gespeichertes Token laden
        if (File.Exists(TokenPath))
        {
            var lines = File.ReadAllLines(TokenPath);
            if (lines.Length >= 2)
            {
                string savedAccessToken  = lines[0].Trim();
                string refreshToken      = lines[1].Trim();
                string expiryStr         = lines.Length >= 3 ? lines[2].Trim() : "";

                // Prüfen ob Token noch gültig (mit 5 Min Puffer)
                if (DateTime.TryParse(expiryStr, out DateTime expiry) && expiry > DateTime.UtcNow.AddMinutes(5))
                {
                    CPH.LogInfo("[YTUploader] OAuth Token noch gültig.");
                    return savedAccessToken;
                }

                // Token erneuern
                CPH.LogInfo("[YTUploader] Token abgelaufen – erneuere...");
                return RefreshAccessToken(refreshToken);
            }
        }

        // Erstes Mal: Browser Login
        CPH.LogInfo("[YTUploader] Kein Token gefunden – starte Browser OAuth Flow...");
        return AuthorizeNewToken();
    }

    private string AuthorizeNewToken()
    {
        try
        {
            string authUrl = $"https://accounts.google.com/o/oauth2/auth" +
                $"?client_id={ClientId}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&access_type=offline" +
                $"&prompt=consent";

            // Browser öffnen
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            CPH.LogInfo("[YTUploader] Browser geöffnet – warte auf Google Login...");

            // Lokalen HTTP Listener starten um den Code aufzufangen
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(RedirectUri);
                listener.Start();

                var context = listener.GetContext();
                string code = context.Request.QueryString["code"];

                // Antwort an Browser schicken
                string responseHtml = "<html><body><h2>✅ Login erfolgreich! Du kannst dieses Fenster schließen.</h2></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                listener.Stop();

                if (string.IsNullOrWhiteSpace(code))
                {
                    CPH.LogError("[YTUploader] Kein Auth Code erhalten.");
                    return null;
                }

                return ExchangeCodeForToken(code);
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] AuthorizeNewToken Exception: {ex.Message}");
            return null;
        }
    }

    private string ExchangeCodeForToken(string code)
    {
        try
        {
            var body = $"code={Uri.EscapeDataString(code)}" +
                       $"&client_id={Uri.EscapeDataString(ClientId)}" +
                       $"&client_secret={Uri.EscapeDataString(ClientSecret)}" +
                       $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                       $"&grant_type=authorization_code";

            var response = Http.PostAsync("https://oauth2.googleapis.com/token",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")).Result;
            string json = response.Content.ReadAsStringAsync().Result;

            return ParseAndSaveToken(json);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] ExchangeCodeForToken Exception: {ex.Message}");
            return null;
        }
    }

    private string RefreshAccessToken(string refreshToken)
    {
        try
        {
            var body = $"refresh_token={Uri.EscapeDataString(refreshToken)}" +
                       $"&client_id={Uri.EscapeDataString(ClientId)}" +
                       $"&client_secret={Uri.EscapeDataString(ClientSecret)}" +
                       $"&grant_type=refresh_token";

            var response = Http.PostAsync("https://oauth2.googleapis.com/token",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")).Result;
            string json = response.Content.ReadAsStringAsync().Result;

            return ParseAndSaveToken(json, refreshToken);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] RefreshAccessToken Exception: {ex.Message}");
            return null;
        }
    }

    private string ParseAndSaveToken(string json, string existingRefreshToken = null)
    {
        var accessMatch  = Regex.Match(json, @"""access_token""\s*:\s*""([^""]+)""");
        var refreshMatch = Regex.Match(json, @"""refresh_token""\s*:\s*""([^""]+)""");
        var expiryMatch  = Regex.Match(json, @"""expires_in""\s*:\s*(\d+)");

        if (!accessMatch.Success)
        {
            CPH.LogError($"[YTUploader] Token Parse Fehler: {json}");
            return null;
        }

        string accessToken  = accessMatch.Groups[1].Value;
        string refreshToken = refreshMatch.Success ? refreshMatch.Groups[1].Value : existingRefreshToken;
        int expiresIn       = expiryMatch.Success ? int.Parse(expiryMatch.Groups[1].Value) : 3600;
        DateTime expiry     = DateTime.UtcNow.AddSeconds(expiresIn);

        File.WriteAllLines(TokenPath, new[] { accessToken, refreshToken ?? "", expiry.ToString("O") });
        CPH.LogInfo("[YTUploader] Token gespeichert.");
        return accessToken;
    }

    // ── YouTube Upload ────────────────────────────────────────────

    private string UploadToYoutube(string filePath, string title, string accessToken)
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Starte YouTube Upload: {title}");

            long fileSize = new FileInfo(filePath).Length;

            // Metadata
            string metadata = $@"{{
                ""snippet"": {{
                    ""title"": ""{EscapeJson(title)}"",
                    ""description"": ""Aufgezeichnet auf Twitch."",
                    ""categoryId"": ""20""
                }},
                ""status"": {{
                    ""privacyStatus"": ""private""
                }}
            }}";

            // Resumable Upload initiieren
            var initRequest = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
            initRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            initRequest.Headers.Add("X-Upload-Content-Type", "video/mp4");
            initRequest.Headers.Add("X-Upload-Content-Length", fileSize.ToString());
            initRequest.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

            var initResponse = Http.SendAsync(initRequest).Result;
            if (!initResponse.IsSuccessStatusCode)
            {
                CPH.LogError($"[YTUploader] Upload Init fehlgeschlagen: {initResponse.StatusCode}");
                return null;
            }

            string uploadUrl = initResponse.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(uploadUrl))
            {
                CPH.LogError("[YTUploader] Keine Upload URL erhalten.");
                return null;
            }

            // Datei hochladen
            using (var fileStream = File.OpenRead(filePath))
            {
                var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                uploadRequest.Content = new StreamContent(fileStream);
                uploadRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                uploadRequest.Content.Headers.ContentLength = fileSize;

                // Timeout auf 6 Stunden setzen für große Dateien
                Http.Timeout = TimeSpan.FromHours(6);

                var uploadResponse = Http.SendAsync(uploadRequest).Result;
                string responseBody = uploadResponse.Content.ReadAsStringAsync().Result;

                if (!uploadResponse.IsSuccessStatusCode)
                {
                    CPH.LogError($"[YTUploader] Upload fehlgeschlagen: {uploadResponse.StatusCode} – {responseBody}");
                    return null;
                }

                var idMatch = Regex.Match(responseBody, @"""id""\s*:\s*""([^""]+)""");
                return idMatch.Success ? idMatch.Groups[1].Value : null;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] UploadToYoutube Exception: {ex.Message}");
            return null;
        }
    }

    // ── CSV ───────────────────────────────────────────────────────

    private bool AlreadyUploaded(string vodId)
    {
        if (!File.Exists(CsvPath)) return false;
        return File.ReadAllLines(CsvPath).Any(l => l.StartsWith(vodId + ","));
    }

    private void WriteCsvEntry(string vodId, string title, string status, string youtubeId)
    {
        try
        {
            bool fileExists = File.Exists(CsvPath);
            using (var writer = new StreamWriter(CsvPath, append: true))
            {
                if (!fileExists)
                    writer.WriteLine("VodId,Datum,Titel,Status,YouTubeId");

                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteLine($"{vodId},{date},{title},{status},{youtubeId}");
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] CSV Fehler: {ex.Message}");
        }
    }

    private string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
