using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Globalization;

public class CPHInline
{
    // ── Sensitiv – hier im Script belassen ────────────────────────
    private const string ClientId     = "DEINE_CLIENT_ID";
    private const string ClientSecret = "DEIN_CLIENT_SECRET";
    // ──────────────────────────────────────────────────────────────

    private const string RedirectUri = "http://localhost:8080/";
    private const string Scope       = "https://www.googleapis.com/auth/youtube.upload";

    private static readonly HttpClient Http = new HttpClient();

    public bool Execute()
    {
        // ── Konfiguration aus Streamer.Bot Global Variables ────────
        string tempDownloadDir = CPH.GetGlobalVar<string>("TempDownloadDir", true);
        string youtubeCsvPath  = CPH.GetGlobalVar<string>("YoutubeCsvPath",  true);
        string twitchDLPath    = CPH.GetGlobalVar<string>("TwitchDLPath",    true);
        string twitchChannel   = CPH.GetGlobalVar<string>("TwitchChannel",   true);
        string tokenPath       = CPH.GetGlobalVar<string>("TokenPath",       true);
        // ──────────────────────────────────────────────────────────

        CPH.LogInfo("[YTUploader] ── Upload gestartet ─────────────────────");

        // 1. Twitch Metadaten aus Streamer.Bot
        CPH.TryGetArg("streamTitle", out string streamTitle);
        CPH.TryGetArg("gameName",    out string gameName);

        if (string.IsNullOrWhiteSpace(streamTitle))
            streamTitle = string.IsNullOrWhiteSpace(gameName) ? "Stream" : gameName;

        string safeTitle = Regex.Replace(streamTitle, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[YTUploader] Titel: {safeTitle}");

        // 2. Temp Ordner anlegen
        if (!Directory.Exists(tempDownloadDir))
            Directory.CreateDirectory(tempDownloadDir);

        // 3. Neueste Twitch VOD ID holen
        string vodId = GetLatestTwitchVodId(twitchDLPath, twitchChannel);
        if (string.IsNullOrWhiteSpace(vodId))
        {
            CPH.LogError("[YTUploader] Konnte keine VOD ID von Twitch holen – Abbruch.");
            return false;
        }
        CPH.LogInfo($"[YTUploader] Twitch VOD ID: {vodId}");

        // Bereits hochgeladen? CSV prüfen
        if (AlreadyUploaded(youtubeCsvPath, vodId))
        {
            CPH.LogInfo($"[YTUploader] VOD {vodId} wurde bereits hochgeladen – überspringe.");
            return true;
        }

        // 4. VOD downloaden
        string outputFile = Path.Combine(tempDownloadDir, $"{vodId}.mp4");
        bool downloaded = DownloadVod(twitchDLPath, vodId, outputFile);
        if (!downloaded)
        {
            CPH.LogError("[YTUploader] Download fehlgeschlagen – Abbruch.");
            return false;
        }

        // 5. OAuth Token holen
        string accessToken = GetAccessToken(tokenPath);
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
            WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "FEHLGESCHLAGEN", "");
            return false;
        }

        CPH.LogInfo($"[YTUploader] ✅ Erfolgreich hochgeladen! YouTube ID: {youtubeVideoId}");
        WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "OK", youtubeVideoId);

        // 7. Temp Datei aufräumen
        try { File.Delete(outputFile); } catch { }

        CPH.LogInfo("[YTUploader] ── Upload abgeschlossen ──────────────────");
        return true;
    }

    // ── Twitch VOD ────────────────────────────────────────────────

    private string GetLatestTwitchVodId(string twitchDLPath, string twitchChannel)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = twitchDLPath,
                Arguments              = $"videos {twitchChannel} --limit 1 --json",
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

    private bool DownloadVod(string twitchDLPath, string vodId, string outputPath)
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Downloade VOD {vodId}...");
            var psi = new ProcessStartInfo
            {
                FileName               = twitchDLPath,
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

    private string GetAccessToken(string tokenPath)
    {
        if (File.Exists(tokenPath))
        {
            var lines = File.ReadAllLines(tokenPath);
            if (lines.Length >= 2)
            {
                string savedAccessToken = lines[0].Trim();
                string refreshToken     = lines[1].Trim();
                string expiryStr        = lines.Length >= 3 ? lines[2].Trim() : "";

                if (DateTime.TryParse(expiryStr, out DateTime expiry) && expiry > DateTime.UtcNow.AddMinutes(5))
                {
                    CPH.LogInfo("[YTUploader] OAuth Token noch gültig.");
                    return savedAccessToken;
                }

                CPH.LogInfo("[YTUploader] Token abgelaufen – erneuere...");
                return RefreshAccessToken(tokenPath, refreshToken);
            }
        }

        CPH.LogInfo("[YTUploader] Kein Token gefunden – starte Browser OAuth Flow...");
        return AuthorizeNewToken(tokenPath);
    }

    private string AuthorizeNewToken(string tokenPath)
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

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            CPH.LogInfo("[YTUploader] Browser geöffnet – warte auf Google Login...");

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(RedirectUri);
                listener.Start();

                var context = listener.GetContext();
                string code = context.Request.QueryString["code"];

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

                return ExchangeCodeForToken(tokenPath, code);
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] AuthorizeNewToken Exception: {ex.Message}");
            return null;
        }
    }

    private string ExchangeCodeForToken(string tokenPath, string code)
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

            return ParseAndSaveToken(tokenPath, json);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] ExchangeCodeForToken Exception: {ex.Message}");
            return null;
        }
    }

    private string RefreshAccessToken(string tokenPath, string refreshToken)
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

            return ParseAndSaveToken(tokenPath, json, refreshToken);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] RefreshAccessToken Exception: {ex.Message}");
            return null;
        }
    }

    private string ParseAndSaveToken(string tokenPath, string json, string existingRefreshToken = null)
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

        File.WriteAllLines(tokenPath, new[] { accessToken, refreshToken ?? "", expiry.ToString("O") });
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

            using (var fileStream = File.OpenRead(filePath))
            {
                var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                uploadRequest.Content = new StreamContent(fileStream);
                uploadRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                uploadRequest.Content.Headers.ContentLength = fileSize;

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

    private bool AlreadyUploaded(string csvPath, string vodId)
    {
        if (!File.Exists(csvPath)) return false;
        return File.ReadAllLines(csvPath).Any(l => l.StartsWith(vodId + ","));
    }

    private void WriteCsvEntry(string csvPath, string vodId, string title, string status, string youtubeId)
    {
        try
        {
            bool fileExists = File.Exists(csvPath);
            using (var writer = new StreamWriter(csvPath, append: true))
            {
                if (!fileExists)
                    writer.WriteLine("VodId,Datum,Titel,Status,YouTubeId");

                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteLine($"{vodId},{date},{title},{status},{youtubeId}");
            }
            CPH.LogInfo($"[YTUploader] CSV Eintrag geschrieben: {vodId} [{status}]");
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] CSV Fehler: {ex.Message}");
        }
    }

    private string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}