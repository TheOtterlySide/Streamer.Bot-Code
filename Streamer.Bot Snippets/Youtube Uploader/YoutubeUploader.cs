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
using System.Timers;

public class CPHInline
{
    private const string RedirectUri = "http://localhost:8080/";
    private const string Scope       = "https://www.googleapis.com/auth/youtube.upload";

    private static readonly HttpClient Http = new HttpClient() { Timeout = TimeSpan.FromHours(6) };

    public bool Execute()
    {
        string tempDownloadDir = CPH.GetGlobalVar<string>("TempDownloadDir",      true);
        string youtubeCsvPath  = CPH.GetGlobalVar<string>("YoutubeCsvPath", true);
        string twitchDLPath    = CPH.GetGlobalVar<string>("TwitchDLPath",         true);
        string twitchChannel   = CPH.GetGlobalVar<string>("TwitchChannel",        true);
        string tokenPath       = CPH.GetGlobalVar<string>("TokenPath",            true);
        string clientId        = CPH.GetGlobalVar<string>("YouTubeClientId",      true);
        string clientSecret    = CPH.GetGlobalVar<string>("YouTubeClientSecret",  true);

        CPH.LogInfo("[YTUploader] ── Upload gestartet ─────────────────────");

        // 1. Titel + Spielname
        CPH.TryGetArg("targetChannelTitle", out string streamTitle);
        CPH.TryGetArg("game",               out string gameName);
        if (string.IsNullOrWhiteSpace(gameName))
            gameName = "Unbekannt";
        CPH.LogInfo($"[YTUploader] streamTitle='{streamTitle}' gameName='{gameName}'");
        if (string.IsNullOrWhiteSpace(streamTitle))
            streamTitle = string.IsNullOrWhiteSpace(gameName) ? "Stream" : gameName;

        string safeTitle = Regex.Replace(streamTitle, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[YTUploader] Titel: {safeTitle}");
        WriteStatus("download", $"Starte für: {safeTitle}");

        // 2. Temp Ordner
        if (!Directory.Exists(tempDownloadDir))
            Directory.CreateDirectory(tempDownloadDir);

        // 3. VOD ID holen
        string vodId = GetLatestTwitchVodId(twitchDLPath, twitchChannel);
        if (string.IsNullOrWhiteSpace(vodId))
        {
            CPH.LogError("[YTUploader] Keine VOD ID gefunden – Abbruch.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Keine VOD ID von Twitch.", "", "");
            WriteStatus("error", "Keine VOD ID gefunden");
            return false;
        }
        CPH.LogInfo($"[YTUploader] VOD ID: {vodId}");

        // Duplikat Check
        if (AlreadyUploaded(youtubeCsvPath, vodId))
        {
            CPH.LogInfo($"[YTUploader] VOD {vodId} bereits hochgeladen – überspringe.");
            WriteStatus("idle", "Bereits hochgeladen");
            return true;
        }

        // 4. Download – prüfen ob Datei bereits lokal vorhanden
        string outputFile = Path.Combine(tempDownloadDir, $"{vodId}.mp4");
        if (File.Exists(outputFile))
        {
            long mb = new FileInfo(outputFile).Length / 1024 / 1024;
            CPH.LogInfo($"[YTUploader] Datei bereits lokal vorhanden ({mb} MB) – überspringe Download.");
            WriteStatus("upload", $"Datei bereits vorhanden ({mb} MB)");
        }
        else
        {
            bool downloaded = DownloadVod(twitchDLPath, vodId, outputFile);
            if (!downloaded)
            {
                CPH.LogError("[YTUploader] Download fehlgeschlagen.");
                CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Twitch VOD Download fehlgeschlagen.", "", "");
                WriteStatus("error", "Download fehlgeschlagen");
                return false;
            }
        }

        // 5. OAuth Token
        string accessToken = GetAccessToken(tokenPath, clientId, clientSecret);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            CPH.LogError("[YTUploader] Kein OAuth Token.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Kein OAuth Token – bitte einloggen.", "", "");
            WriteStatus("error", "Kein OAuth Token");
            return false;
        }

        // 6. YouTube Upload
        WriteStatus("upload", $"Lade hoch: {safeTitle}");
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string youtubeVideoId = UploadToYoutube(outputFile, safeTitle, accessToken, gameName, date);
        if (string.IsNullOrWhiteSpace(youtubeVideoId))
        {
            CPH.LogError("[YTUploader] Upload fehlgeschlagen.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", $"Upload fehlgeschlagen: {safeTitle}", "", "");
            WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "FEHLGESCHLAGEN", "");
            CPH.SetGlobalVar("yt_status", "FEHLGESCHLAGEN", false);
            CPH.SetGlobalVar("yt_detail", safeTitle, false);
            WriteStatus("error", $"Upload fehlgeschlagen: {safeTitle}", ytStatus: "FEHLGESCHLAGEN", ytDetail: safeTitle);
            return false;
        }

        CPH.LogInfo($"[YTUploader] ✅ Hochgeladen! YouTube ID: {youtubeVideoId}");
        CPH.ShowToastNotification("YTUploader", "YoutubeUploader ✅", $"Hochgeladen: {safeTitle}", "", "");
        WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "OK", youtubeVideoId);
        CPH.SetGlobalVar("yt_status", "OK", false);
        CPH.SetGlobalVar("yt_detail", safeTitle, false);
        WriteStatus("done", $"Hochgeladen: {safeTitle}", ytStatus: "OK", ytDetail: safeTitle);

        // 7. Aufräumen
        try { File.Delete(outputFile); } catch { }

        CPH.LogInfo("[YTUploader] ── Upload abgeschlossen ──────────────────");
        return true;
    }

    // ── Twitch ────────────────────────────────────────────────────

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
            CPH.LogError($"[YTUploader] GetVodId Exception: {ex.Message}");
            return null;
        }
    }

    private bool DownloadVod(string twitchDLPath, string vodId, string outputPath)
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Starte Download VOD {vodId}...");
            CPH.LogInfo($"[YTUploader] Ausgabepfad: {outputPath}");

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
                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CPH.LogInfo($"[YTUploader] twitch-dl: {e.Data}"); };
                proc.ErrorDataReceived  += (s, e) => {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        CPH.LogWarn($"[YTUploader] twitch-dl stderr: {e.Data}");
                        // Versuche Gesamtgröße zu parsen z.B. "Downloading 10.2 GiB"
                        var sizeMatch = Regex.Match(e.Data, @"(\d+[\.,]\d+)\s*(GiB|MiB|GB|MB)", RegexOptions.IgnoreCase);
                        if (sizeMatch.Success)
                        {
                            double size = double.Parse(sizeMatch.Groups[1].Value.Replace(",", "."), CultureInfo.InvariantCulture);
                            string unit = sizeMatch.Groups[2].Value.ToUpper();
                            int totalMB = unit.StartsWith("G") ? (int)(size * 1024) : (int)size;
                            CPH.SetGlobalVar("dl_mb_total", totalMB.ToString(), false);
                            CPH.LogInfo($"[YTUploader] Geschätzte Gesamtgröße: {totalMB} MB");
                        }
                    }
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var timer = new Timer(30000);
                timer.Elapsed += (s, e) => {
                    try {
                        if (File.Exists(outputPath))
                        {
                            long mb = new FileInfo(outputPath).Length / 1024 / 1024;
                            CPH.LogInfo($"[YTUploader] ⏳ Download läuft... {mb} MB");
                            CPH.SetGlobalVar("dl_mb", mb.ToString(), false);
                            WriteStatus("download", $"VOD Download läuft – {mb} MB", (int)mb);
                        }
                        else CPH.LogInfo("[YTUploader] ⏳ Download startet...");
                    } catch { }
                };
                timer.Start();

                proc.WaitForExit();
                timer.Stop();
                timer.Dispose();

                CPH.LogInfo($"[YTUploader] twitch-dl ExitCode: {proc.ExitCode}");
                bool success = proc.ExitCode == 0 && File.Exists(outputPath);
                if (success)
                {
                    long mb = new FileInfo(outputPath).Length / 1024 / 1024;
                    CPH.LogInfo($"[YTUploader] ✅ Download fertig: {outputPath} ({mb} MB)");
                }
                else
                {
                    CPH.LogError($"[YTUploader] ❌ Download fehlgeschlagen (Exit: {proc.ExitCode})");
                }
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

    private string GetAccessToken(string tokenPath, string clientId, string clientSecret)
    {
        if (File.Exists(tokenPath))
        {
            var lines = File.ReadAllLines(tokenPath);
            if (lines.Length >= 2)
            {
                string savedToken   = lines[0].Trim();
                string refreshToken = lines[1].Trim();
                string expiryStr    = lines.Length >= 3 ? lines[2].Trim() : "";

                if (DateTime.TryParse(expiryStr, out DateTime expiry) && expiry > DateTime.UtcNow.AddMinutes(5))
                {
                    CPH.LogInfo("[YTUploader] Token noch gültig.");
                    return savedToken;
                }
                CPH.LogInfo("[YTUploader] Token abgelaufen – erneuere...");
                return RefreshAccessToken(tokenPath, refreshToken, clientId, clientSecret);
            }
        }
        CPH.LogInfo("[YTUploader] Kein Token – starte Browser OAuth...");
        return AuthorizeNewToken(tokenPath, clientId, clientSecret);
    }

    private string AuthorizeNewToken(string tokenPath, string clientId, string clientSecret)
    {
        try
        {
            string authUrl = "https://accounts.google.com/o/oauth2/auth" +
                $"?client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&access_type=offline&prompt=consent";

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            CPH.LogInfo("[YTUploader] Browser geöffnet – warte auf Login...");

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(RedirectUri);
                listener.Start();
                var context = listener.GetContext();
                string code = context.Request.QueryString["code"];

                string responseHtml = "<html><body><h2>✅ Login erfolgreich! Fenster schließen.</h2></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                listener.Stop();

                if (string.IsNullOrWhiteSpace(code)) { CPH.LogError("[YTUploader] Kein Auth Code."); return null; }
                return ExchangeCodeForToken(tokenPath, code, clientId, clientSecret);
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] AuthorizeNewToken Exception: {ex.Message}");
            return null;
        }
    }

    private string ExchangeCodeForToken(string tokenPath, string code, string clientId, string clientSecret)
    {
        try
        {
            var body = $"code={Uri.EscapeDataString(code)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&grant_type=authorization_code";
            var response = Http.PostAsync("https://oauth2.googleapis.com/token", new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")).Result;
            return ParseAndSaveToken(tokenPath, response.Content.ReadAsStringAsync().Result);
        }
        catch (Exception ex) { CPH.LogError($"[YTUploader] ExchangeCode Exception: {ex.Message}"); return null; }
    }

    private string RefreshAccessToken(string tokenPath, string refreshToken, string clientId, string clientSecret)
    {
        try
        {
            var body = $"refresh_token={Uri.EscapeDataString(refreshToken)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}&grant_type=refresh_token";
            var response = Http.PostAsync("https://oauth2.googleapis.com/token", new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")).Result;
            return ParseAndSaveToken(tokenPath, response.Content.ReadAsStringAsync().Result, refreshToken);
        }
        catch (Exception ex) { CPH.LogError($"[YTUploader] RefreshToken Exception: {ex.Message}"); return null; }
    }

    private string ParseAndSaveToken(string tokenPath, string json, string existingRefreshToken = null)
    {
        var accessMatch  = Regex.Match(json, @"""access_token""\s*:\s*""([^""]+)""");
        var refreshMatch = Regex.Match(json, @"""refresh_token""\s*:\s*""([^""]+)""");
        var expiryMatch  = Regex.Match(json, @"""expires_in""\s*:\s*(\d+)");

        if (!accessMatch.Success) { CPH.LogError($"[YTUploader] Token Parse Fehler: {json}"); return null; }

        string accessToken  = accessMatch.Groups[1].Value;
        string refreshToken = refreshMatch.Success ? refreshMatch.Groups[1].Value : existingRefreshToken;
        int expiresIn       = expiryMatch.Success ? int.Parse(expiryMatch.Groups[1].Value) : 3600;
        DateTime expiry     = DateTime.UtcNow.AddSeconds(expiresIn);

        File.WriteAllLines(tokenPath, new[] { accessToken, refreshToken ?? "", expiry.ToString("O") });
        CPH.LogInfo("[YTUploader] Token gespeichert.");
        return accessToken;
    }

    // ── YouTube Upload ────────────────────────────────────────────

    private string UploadToYoutube(string filePath, string title, string accessToken, string gameName = "", string date = "")
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Starte YouTube Upload: {title}");
            long fileSize = new FileInfo(filePath).Length;

            string description = $"Spiel: {gameName}\nDatum: {date}";
            string metadata = $@"{{""snippet"":{{""title"":""{EscapeJson(title)}"",""description"":""{EscapeJson(description)}"",""categoryId"":""20""}},""status"":{{""privacyStatus"":""private""}}}}";

            // Schritt 1: Resumable Upload Session starten
            var initRequest = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
            initRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            initRequest.Headers.Add("X-Upload-Content-Type", "video/mp4");
            initRequest.Headers.Add("X-Upload-Content-Length", fileSize.ToString());
            initRequest.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

            var initResponse = Http.SendAsync(initRequest).Result;
            if (!initResponse.IsSuccessStatusCode)
            {
                string reason = initResponse.StatusCode.ToString();
                CPH.LogError($"[YTUploader] Upload Init fehlgeschlagen: {reason}");
                WriteStatus("error", $"Upload Init fehlgeschlagen: {reason}");
                return null;
            }

            string uploadUrl = initResponse.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(uploadUrl))
            {
                CPH.LogError("[YTUploader] Keine Upload URL erhalten.");
                return null;
            }

            // Schritt 2: Chunked Upload
            const int chunkSize = 16 * 1024 * 1024; // 16MB pro Chunk (YouTube Empfehlung: Vielfaches von 256KB)
            var buffer = new byte[chunkSize];
            long offset = 0;
            int lastLoggedPct = -1;

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                while (offset < fileSize)
                {
                    int bytesRead = fileStream.Read(buffer, 0, chunkSize);
                    if (bytesRead == 0) break;

                    long rangeEnd = offset + bytesRead - 1;

                    // Neuen HttpClient Request pro Chunk
                    var chunkContent = new ByteArrayContent(buffer, 0, bytesRead);
                    chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                    chunkContent.Headers.Add("Content-Range", $"bytes {offset}-{rangeEnd}/{fileSize}");

                    var chunkRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                    chunkRequest.Content = chunkContent;

                    var chunkResponse = Http.SendAsync(chunkRequest).Result;
                    int statusCode = (int)chunkResponse.StatusCode;

                    // 308 Resume Incomplete = Chunk OK, weiter
                    // 200/201 = Upload komplett
                    if (statusCode == 308)
                    {
                        offset += bytesRead;
                        int pct = (int)(offset * 100 / fileSize);
                        if (pct != lastLoggedPct)
                        {
                            lastLoggedPct = pct;
                            long uploadedMB = offset / 1024 / 1024;
                            long totalMB    = fileSize / 1024 / 1024;
                            CPH.LogInfo($"[YTUploader] Upload: {pct}% ({uploadedMB} MB / {totalMB} MB)");
                            CPH.SetGlobalVar("yt_status",  "UPLOADING",           false);
                            CPH.SetGlobalVar("yt_detail",  $"{pct}% hochgeladen", false);
                            CPH.SetGlobalVar("upload_pct", pct.ToString(),         false);
                            WriteStatus("upload", $"Upload läuft – {pct}% ({uploadedMB}/{totalMB} MB)", uploadPct: pct, ytStatus: "UPLOADING", ytDetail: $"{pct}%");
                        }
                    }
                    else if (statusCode == 200 || statusCode == 201)
                    {
                        string responseBody = chunkResponse.Content.ReadAsStringAsync().Result;
                        CPH.LogInfo($"[YTUploader] Upload abgeschlossen!");
                        var idMatch = Regex.Match(responseBody, @"""id""\s*:\s*""([^""]+)""");
                        return idMatch.Success ? idMatch.Groups[1].Value : "unknown";
                    }
                    else
                    {
                        string responseBody = chunkResponse.Content.ReadAsStringAsync().Result;
                        CPH.LogError($"[YTUploader] Chunk Fehler {statusCode}: {responseBody}");
                        WriteStatus("error", $"Chunk Upload Fehler {statusCode}");
                        return null;
                    }
                }
            }

            CPH.LogError("[YTUploader] Upload Loop beendet ohne Erfolg.");
            return null;
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
                if (!fileExists) writer.WriteLine("VodId,Datum,Titel,Status,YouTubeId");
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteLine($"{vodId},{date},{title},{status},{youtubeId}");
            }
            CPH.LogInfo($"[YTUploader] CSV: {vodId} [{status}]");
        }
        catch (Exception ex) { CPH.LogError($"[YTUploader] CSV Fehler: {ex.Message}"); }
    }

    private string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Dashboard ─────────────────────────────────────────────────

    private void WriteStatus(string step, string detail = "",
        int downloadMB = -1, int downloadPct = -1, int uploadPct = -1,
        string ytStatus = "", string ytDetail = "")
    {
        try
        {
            string csvPath    = CPH.GetGlobalVar<string>("CsvPath", true) ?? @"D:\Stream\Records\streams.csv";
            string statusPath = csvPath.Replace("streams.csv", "dashboard.html");

            var recentStreams = new List<string[]>();
            int totalStreams  = 0;
            string lastName = "–", lastDate = "–", lastGame = "–";

            if (File.Exists(csvPath))
            {
                var allLines = File.ReadAllLines(csvPath)
                    .Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                totalStreams = allLines.Length;
                recentStreams = allLines.Reverse().Take(5).Reverse()
                    .Select(l => l.Split(',')).Where(p => p.Length >= 5).ToList();
                if (recentStreams.Count > 0)
                {
                    var last = recentStreams[recentStreams.Count - 1];
                    lastName = last[3].Trim(); lastDate = last[1].Trim(); lastGame = last[2].Trim();
                }
            }

            string cTotal   = CPH.GetGlobalVar<string>("checker_total",    false) ?? "–";
            string cOk      = CPH.GetGlobalVar<string>("checker_ok",       false) ?? "–";
            string cMissing = CPH.GetGlobalVar<string>("checker_missing",   false) ?? "–";
            string cNoBack  = CPH.GetGlobalVar<string>("checker_nobackup",  false) ?? "–";
            string cTime    = CPH.GetGlobalVar<string>("checker_lastcheck", false) ?? "";
            string dlMB     = downloadMB >= 0 ? downloadMB.ToString() : (CPH.GetGlobalVar<string>("dl_mb",      false) ?? "0");
            string ytSt     = !string.IsNullOrEmpty(ytStatus) ? ytStatus : (CPH.GetGlobalVar<string>("yt_status", false) ?? "–");
            string ytDet    = !string.IsNullOrEmpty(ytDetail) ? ytDetail : (CPH.GetGlobalVar<string>("yt_detail", false) ?? "–");

            var stepIcons  = new Dictionary<string,string> { {"idle","💤"},{"ffmpeg","🔍"},{"copy","📋"},{"download","⬇️"},{"upload","⬆️"},{"checker","🔎"},{"done","✅"},{"error","❌"} };
            var stepNames  = new Dictionary<string,string> { {"idle","Warte auf nächsten Stream"},{"ffmpeg","FFmpeg Dateiprüfung"},{"copy","Umbenennen & Kopieren"},{"download","Twitch VOD Download"},{"upload","YouTube Upload"},{"checker","StreamChecker läuft"},{"done","Fertig"},{"error","Fehler aufgetreten"} };
            var stepColors = new Dictionary<string,string> { {"idle","#53535F"},{"ffmpeg","#9147FF"},{"copy","#9147FF"},{"download","#9147FF"},{"upload","#9147FF"},{"checker","#9147FF"},{"done","#1DB954"},{"error","#E91916"} };

            bool   isActive = step != "idle" && step != "done" && step != "error";
            string icon     = stepIcons.ContainsKey(step)  ? stepIcons[step]  : "⚙️";
            string sName    = stepNames.ContainsKey(step)  ? stepNames[step]  : step;
            string sColor   = stepColors.ContainsKey(step) ? stepColors[step] : "#ADADB8";

            string progressHtml = "";
            if (step == "download" && int.TryParse(dlMB, out int mbInt) && mbInt > 0)
            {
                // Schätze Prozent basierend auf dl_mb_total falls vorhanden, sonst zeige nur MB
                string dlTotal = CPH.GetGlobalVar<string>("dl_mb_total", false) ?? "0";
                int totalMBInt = 0;
                int.TryParse(dlTotal, out totalMBInt);
                string pctStr = totalMBInt > 0 ? $"{Math.Min(99, mbInt * 100 / totalMBInt)}%" : "";
                progressHtml = $@"<div class=""progress-wrap""><div class=""progress-meta""><span>Download</span><span>{dlMB} MB {pctStr}</span></div><div class=""progress-bar""><div class=""progress-fill"" style=""width:{(totalMBInt > 0 ? Math.Min(99, mbInt * 100 / totalMBInt) : 50)}%""></div></div></div>";
            }
            else if (step == "upload")
            {
                string pctGlobal = CPH.GetGlobalVar<string>("upload_pct", false) ?? "0";
                int.TryParse(pctGlobal, out int pctInt);
                int displayPct = uploadPct >= 0 ? uploadPct : pctInt;
                progressHtml = $@"<div class=""progress-wrap""><div class=""progress-meta""><span>Upload zu YouTube</span><span>{displayPct}%</span></div><div class=""progress-bar""><div class=""progress-fill green"" style=""width:{displayPct}%""></div></div></div>";
            }

            var logRows = new StringBuilder();
            foreach (var p in recentStreams)
            {
                string st = p.Length > 4 ? p[4].Trim() : "OK";
                string pc = st == "OK" ? "ok" : st.Contains("KEIN") ? "error" : "warn";
                logRows.Append($@"<div class=""log-entry""><span class=""log-time"">{p[1].Trim()}</span><span class=""log-nr"">#{p[0].Trim()}</span><span class=""log-name"">{p[3].Trim()}</span><span class=""pill {pc}"">{st}</span></div>");
            }
            if (logRows.Length == 0)
                logRows.Append(@"<div style=""font-family:var(--mono);font-size:11px;color:var(--text-muted);padding:8px 10px"">Keine Einträge</div>");

            string checkerAgo = "–";
            if (!string.IsNullOrEmpty(cTime) && DateTime.TryParse(cTime, out DateTime ct))
            {
                int diff = (int)(DateTime.UtcNow - ct).TotalSeconds;
                checkerAgo = diff < 60 ? $"vor {diff}s" : diff < 3600 ? $"vor {diff/60}min" : $"vor {diff/3600}h";
            }

            string ytPillClass = ytSt == "OK" ? "ok" : ytSt == "FEHLGESCHLAGEN" ? "error" : ytSt.Contains("ING") ? "running" : "idle";
            string now        = DateTime.Now.ToString("HH:mm:ss");
            string badgeClass = isActive ? "live-badge active" : "live-badge";
            string badgeText  = isActive ? "AKTIV" : "IDLE";

            string html = $@"<!DOCTYPE html>
<html lang=""de"">
<head>
<meta charset=""UTF-8"">
<meta http-equiv=""refresh"" content=""3"">
<title>StreamArchiver</title>
<link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@400;500;700&display=swap"" rel=""stylesheet"">
<style>
*,*::before,*::after{{box-sizing:border-box;margin:0;padding:0}}
:root{{--bg:#0E0E10;--surface:#18181B;--surface2:#1F1F23;--border:#2A2A2E;--purple:#9147FF;--green:#1DB954;--red:#E91916;--yellow:#F59E0B;--text:#EFEFF1;--text-dim:#ADADB8;--text-muted:#53535F;--mono:'JetBrains Mono',monospace;--sans:'Inter',sans-serif}}
body{{background:var(--bg);color:var(--text);font-family:var(--sans);min-height:100vh;padding:24px}}
.header{{display:flex;align-items:center;justify-content:space-between;margin-bottom:28px;padding-bottom:20px;border-bottom:1px solid var(--border)}}
.header-left{{display:flex;align-items:center;gap:14px}}
.logo-icon{{width:36px;height:36px;background:var(--purple);border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:18px}}
.header h1{{font-family:var(--mono);font-size:16px;font-weight:700;letter-spacing:.08em;text-transform:uppercase}}
.header-sub{{font-size:11px;color:var(--text-muted);font-family:var(--mono);margin-top:2px}}
.live-badge{{display:flex;align-items:center;gap:7px;padding:5px 12px;border-radius:4px;background:var(--surface2);border:1px solid var(--border);font-family:var(--mono);font-size:11px;font-weight:700;letter-spacing:.1em;color:var(--text-muted);text-transform:uppercase}}
.live-badge.active{{border-color:var(--purple);color:var(--purple)}}
.live-dot{{width:7px;height:7px;border-radius:50%;background:var(--text-muted)}}
.live-badge.active .live-dot{{background:var(--purple);animation:pulse 1.5s ease-in-out infinite}}
@keyframes pulse{{0%,100%{{opacity:1;transform:scale(1)}}50%{{opacity:.4;transform:scale(.7)}}}}
.grid{{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-bottom:16px}}
.grid-3{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:16px;margin-bottom:16px}}
.card{{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:20px}}
.card-label{{font-size:10px;font-weight:600;letter-spacing:.12em;text-transform:uppercase;color:var(--text-muted);margin-bottom:14px;font-family:var(--mono)}}
.step-card{{grid-column:span 2;border-color:#6441A4}}
.step-inner{{display:flex;align-items:center;gap:16px}}
.step-icon{{width:44px;height:44px;border-radius:10px;background:var(--surface2);border:1px solid {sColor};display:flex;align-items:center;justify-content:center;font-size:22px;flex-shrink:0}}
.step-name{{font-size:18px;font-weight:600;color:var(--text);margin-bottom:4px}}
.step-detail{{font-family:var(--mono);font-size:12px;color:var(--text-dim)}}
.progress-wrap{{margin-top:14px}}
.progress-meta{{display:flex;justify-content:space-between;font-family:var(--mono);font-size:11px;color:var(--text-muted);margin-bottom:6px}}
.progress-bar{{height:4px;background:var(--surface2);border-radius:2px;overflow:hidden}}
.progress-fill{{height:100%;background:var(--purple);border-radius:2px}}
.progress-fill.green{{background:var(--green)}}
.stat-value{{font-family:var(--mono);font-size:28px;font-weight:700;line-height:1;margin-bottom:4px}}
.stat-sub{{font-size:12px;color:var(--text-muted)}}
.pill{{display:inline-flex;align-items:center;padding:3px 9px;border-radius:20px;font-size:11px;font-weight:600;font-family:var(--mono)}}
.pill.ok{{background:rgba(29,185,84,.15);color:var(--green)}}
.pill.error{{background:rgba(233,25,22,.15);color:var(--red)}}
.pill.warn{{background:rgba(245,158,11,.15);color:var(--yellow)}}
.pill.idle{{background:var(--surface2);color:var(--text-muted)}}
.pill.running{{background:rgba(145,71,255,.15);color:var(--purple)}}
.log-entry{{display:grid;grid-template-columns:80px 50px 1fr auto;gap:10px;padding:6px 10px;border-radius:5px;font-family:var(--mono);font-size:11px;align-items:center}}
.log-entry:hover{{background:var(--surface2)}}
.log-time{{color:var(--text-muted)}}
.log-nr{{color:var(--purple);font-weight:700}}
.log-name{{color:var(--text-dim);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}
.checker-row{{display:flex;justify-content:space-between;align-items:center;padding:8px 0;border-bottom:1px solid var(--border)}}
.checker-row:last-child{{border-bottom:none}}
.checker-label{{font-family:var(--mono);font-size:12px;color:var(--text-dim)}}
.checker-val{{font-family:var(--mono);font-size:14px;font-weight:700}}
.ticker{{text-align:center;font-family:var(--mono);font-size:10px;color:var(--text-muted);margin-top:20px}}
.ticker span{{color:var(--purple)}}
</style>
</head>
<body>
<div class=""header"">
  <div class=""header-left"">
    <div class=""logo-icon"">🎬</div>
    <div><h1>StreamArchiver</h1><div class=""header-sub"">Streamer.Bot Dashboard</div></div>
  </div>
  <div class=""{badgeClass}""><div class=""live-dot""></div><span>{badgeText}</span></div>
</div>
<div class=""grid"">
  <div class=""card step-card"">
    <div class=""card-label"">Aktueller Schritt</div>
    <div class=""step-inner"">
      <div class=""step-icon"">{icon}</div>
      <div><div class=""step-name"">{sName}</div><div class=""step-detail"">{detail}</div></div>
    </div>
    {progressHtml}
  </div>
</div>
<div class=""grid-3"">
  <div class=""card"">
    <div class=""card-label"">Letzter Stream</div>
    <div class=""stat-value"" style=""font-size:16px;margin-top:4px"">{lastName}</div>
    <div class=""stat-sub"">{lastDate} · {lastGame}</div>
  </div>
  <div class=""card"">
    <div class=""card-label"">Archiviert</div>
    <div class=""stat-value"">{totalStreams}</div>
    <div class=""stat-sub"">Streams gesamt</div>
  </div>
  <div class=""card"">
    <div class=""card-label"">YouTube Status</div>
    <div style=""margin-top:4px""><span class=""pill {ytPillClass}"">{ytSt}</span></div>
    <div class=""stat-sub"" style=""margin-top:8px"">{ytDet}</div>
  </div>
</div>
<div class=""grid"">
  <div class=""card"">
    <div class=""card-label"">StreamChecker</div>
    <div class=""checker-row""><span class=""checker-label"">Gesamt</span><span class=""checker-val"">{cTotal}</span></div>
    <div class=""checker-row""><span class=""checker-label"">OK</span><span class=""checker-val"" style=""color:var(--green)"">{cOk}</span></div>
    <div class=""checker-row""><span class=""checker-label"">Fehlend</span><span class=""checker-val"" style=""color:var(--red)"">{cMissing}</span></div>
    <div class=""checker-row""><span class=""checker-label"">Kein Backup</span><span class=""checker-val"" style=""color:var(--yellow)"">{cNoBack}</span></div>
    <div class=""checker-row""><span class=""checker-label"">Letzter Check</span><span class=""checker-val"" style=""color:var(--text-muted);font-size:11px"">{checkerAgo}</span></div>
  </div>
  <div class=""card"">
    <div class=""card-label"">Letzte Streams</div>
    {logRows}
  </div>
</div>
<div class=""ticker"">Auto-Refresh alle 3s · Zuletzt aktualisiert: <span>{now}</span></div>
</body>
</html>";

            File.WriteAllText(statusPath, html);
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[StatusWriter] {ex.Message}");
        }
    }
}
