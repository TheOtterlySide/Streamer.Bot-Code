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

    private static readonly HttpClient Http = new HttpClient();

    public bool Execute()
    {
        // ── Konfiguration aus Streamer.Bot Global Variables ────────
        string tempDownloadDir   = CPH.GetGlobalVar<string>("TempDownloadDir",    true);
        string youtubeCsvPath    = CPH.GetGlobalVar<string>("YoutubeCsvPath",     true);
        string twitchDLPath      = CPH.GetGlobalVar<string>("TwitchDLPath",      true);
        string twitchChannel     = CPH.GetGlobalVar<string>("TwitchChannel",     true);
        string tokenPath         = CPH.GetGlobalVar<string>("TokenPath",         true);
        string youtubeClientId     = CPH.GetGlobalVar<string>("YouTubeClientId",     true);
        string youtubeClientSecret = CPH.GetGlobalVar<string>("YouTubeClientSecret", true);
        // ──────────────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(youtubeClientId) || string.IsNullOrWhiteSpace(youtubeClientSecret))
        {
            CPH.LogError("[YTUploader] YouTubeClientId/YouTubeClientSecret nicht als Global Variable gesetzt – Abbruch.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "YouTubeClientId/Secret fehlt in den Global Variables.", "", "");
            AppendLog("YTUploader", "❌ YouTubeClientId/YouTubeClientSecret fehlt in den Global Variables.");
            WriteStatus("error", "YouTubeClientId/Secret fehlt in den Global Variables");
            return false;
        }

        // 0. Sperre setzen – verhindert doppelte/parallele Läufe (z.B. durch doppelt gefeuerte Trigger)
        if (!TryAcquireLock("uploader", TimeSpan.FromHours(4), out string uploaderLockPath))
        {
            CPH.LogWarn("[YTUploader] ⏭️ Es läuft bereits eine Instanz – übersprungen (Sperre aktiv).");
            AppendLog("YTUploader", "⏭️ Übersprungen – läuft bereits (Doppel-Trigger verhindert)");
            return true;
        }

        try
        {

        CPH.LogInfo("[YTUploader] ── Upload gestartet ─────────────────────");
        Http.Timeout = TimeSpan.FromHours(6);
        OpenDashboardOnce();

        // 1. Twitch Metadaten aus Streamer.Bot
        CPH.TryGetArg("targetChannelTitle", out string streamTitle);
        CPH.TryGetArg("game",    out string gameName);

        if (string.IsNullOrWhiteSpace(streamTitle))
            streamTitle = string.IsNullOrWhiteSpace(gameName) ? $"Stream {DateTime.Now:yyyy-MM-dd HH-mm}" : gameName;

        string safeTitle = Regex.Replace(streamTitle, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[YTUploader] Titel: {safeTitle}");
        AppendLog("YTUploader", $"Verarbeite: {safeTitle}");

        // 2. Temp Ordner anlegen
        if (!Directory.Exists(tempDownloadDir))
            Directory.CreateDirectory(tempDownloadDir);

        // 3. Neueste Twitch VOD ID holen
        string vodId = GetLatestTwitchVodId(twitchDLPath, twitchChannel);
        if (string.IsNullOrWhiteSpace(vodId))
        {
            CPH.LogError("[YTUploader] Konnte keine VOD ID von Twitch holen – Abbruch.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Konnte keine VOD ID von Twitch holen.", "", "");
            AppendLog("YTUploader", "❌ Konnte keine VOD ID von Twitch holen – Abbruch.");
            return false;
        }
        CPH.LogInfo($"[YTUploader] Twitch VOD ID: {vodId}");
        AppendLog("YTUploader", $"VOD ID: {vodId}");

        // Bereits hochgeladen? CSV prüfen
        if (AlreadyUploaded(youtubeCsvPath, vodId))
        {
            CPH.LogInfo($"[YTUploader] VOD {vodId} wurde bereits hochgeladen – überspringe.");
            AppendLog("YTUploader", $"VOD {vodId} bereits hochgeladen – übersprungen.");
            return true;
        }

        // 4. VOD downloaden
        string outputFile = Path.Combine(tempDownloadDir, $"{vodId}.mp4");
        bool downloaded = DownloadVod(twitchDLPath, vodId, outputFile);
        if (!downloaded)
        {
            CPH.LogError("[YTUploader] Download fehlgeschlagen – Abbruch.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Twitch VOD Download fehlgeschlagen.", "", "");
            AppendLog("YTUploader", $"❌ Download fehlgeschlagen für VOD {vodId}.");
            return false;
        }
        AppendLog("YTUploader", $"✅ Download abgeschlossen für VOD {vodId}.");

        // 5. OAuth Token holen
        string accessToken = GetAccessToken(tokenPath, youtubeClientId, youtubeClientSecret);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            CPH.LogError("[YTUploader] Kein gültiges OAuth Token – Abbruch.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", "Kein gültiges OAuth Token – bitte manuell einloggen.", "", "");
            AppendLog("YTUploader", "❌ Kein gültiges OAuth Token – manueller Login nötig.");
            return false;
        }

        // 6. Auf YouTube hochladen
        WriteStatus("upload", $"Lade hoch: {safeTitle}");
        string youtubeVideoId = UploadToYoutube(outputFile, safeTitle, gameName, accessToken);
        if (string.IsNullOrWhiteSpace(youtubeVideoId))
        {
            CPH.LogError("[YTUploader] YouTube Upload fehlgeschlagen.");
            CPH.ShowToastNotification("YTUploader", "YoutubeUploader ❌", $"Upload fehlgeschlagen: {safeTitle}", "", "");
            WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "FEHLGESCHLAGEN", "");
            AppendLog("YTUploader", $"❌ Upload fehlgeschlagen: {safeTitle}");
            WriteStatus("error", $"Upload fehlgeschlagen: {safeTitle}");
            return false;
        }

        CPH.LogInfo($"[YTUploader] ✅ Erfolgreich hochgeladen! YouTube ID: {youtubeVideoId}");
        CPH.ShowToastNotification("YTUploader", "YoutubeUploader ✅", $"Hochgeladen: {safeTitle}", "", "");
        WriteCsvEntry(youtubeCsvPath, vodId, safeTitle, "OK", youtubeVideoId);
        AppendLog("YTUploader", $"✅ Hochgeladen: {safeTitle} (YouTube ID: {youtubeVideoId})");
        WriteStatus("done", $"Hochgeladen: {safeTitle}");

        // 7. Temp Datei aufräumen
        try { File.Delete(outputFile); } catch { }

        CPH.LogInfo("[YTUploader] ── Upload abgeschlossen ──────────────────");
        return true;
        }
        finally
        {
            ReleaseLock(uploaderLockPath);
        }
    }

    // ── Twitch VOD ────────────────────────────────────────────────

    // Gemeinsames Live-Log fürs Dashboard (alle Scripts)
    private const string DashboardLogVar      = "dashboard_log";
    private const int    DashboardLogMaxLines = 80;

    private void AppendLog(string scriptTag, string message)
    {
        try
        {
            string existing = CPH.GetGlobalVar<string>(DashboardLogVar, false) ?? "";
            var lines = existing.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string logDate = DateTime.Now.ToString("yyyy-MM-dd");
            string logTime = DateTime.Now.ToString("HH:mm:ss");
            lines.Add($"{logDate}|{logTime}|{scriptTag}|{message}");
            if (lines.Count > DashboardLogMaxLines)
                lines = lines.Skip(lines.Count - DashboardLogMaxLines).ToList();
            CPH.SetGlobalVar(DashboardLogVar, string.Join("\n", lines), false);

            // Zusätzlich dauerhaft auf Platte sichern (eine Datei pro Tag) – im Gegensatz
            // zum In-Memory-Dashboard-Log (nur die letzten Zeilen, weg nach Neustart)
            // bleibt das hier vollständig erhalten.
            try
            {
                string logRoot = CPH.GetGlobalVar<string>("RecordsRootDir", true) ?? @"D:\Stream\Records";
                string logDir  = Path.Combine(logRoot, "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, $"activity_{logDate}.log");
                File.AppendAllText(logFile, $"{logDate} {logTime} [{scriptTag}] {message}{Environment.NewLine}");
            }
            catch { /* Datei-Logging ist best-effort, darf den Ablauf nie stören */ }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[DashboardLog] {ex.Message}");
        }
    }

    // Öffnet das Dashboard bei jedem Lauf im Standardbrowser (kein Cooldown mehr –
    // Process.Start macht dabei aber i.d.R. immer einen neuen Tab auf statt einen
    // vorhandenen zu fokussieren, also lieber gelegentlich alte Tabs selbst schließen).
    // Der Pfad wird exakt so aus CsvPath abgeleitet wie in WriteStatus, NICHT hart codiert.
    private void OpenDashboardOnce()
    {
        try
        {
            string mainCsvPath  = CPH.GetGlobalVar<string>("CsvPath", true) ?? @"D:\Stream\Records\streams.csv";
            string dashboardPath = mainCsvPath.Replace("streams.csv", "dashboard.html");

            if (File.Exists(dashboardPath))
            {
                Process.Start(new ProcessStartInfo(dashboardPath) { UseShellExecute = true });
                CPH.LogInfo($"[YTUploader] Dashboard im Browser geöffnet: {dashboardPath}");
            }
            else
            {
                CPH.LogInfo($"[YTUploader] Dashboard-Datei noch nicht vorhanden ({dashboardPath}) – wird beim ersten WriteStatus-Aufruf angelegt.");
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[YTUploader] Konnte Dashboard nicht öffnen: {ex.Message}");
        }
    }

    // Ausführungs-Sperre – verhindert doppelte/parallele Läufe (z.B. Doppel-Trigger)
    private bool TryAcquireLock(string lockName, TimeSpan staleAfter, out string lockPath)
    {
        string root = CPH.GetGlobalVar<string>("RecordsRootDir", true) ?? @"D:\Stream\Records";
        string locksDir = Path.Combine(root, ".locks");
        try { Directory.CreateDirectory(locksDir); } catch { }
        lockPath = Path.Combine(locksDir, lockName + ".lock");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using (var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs))
                    sw.WriteLine(DateTime.UtcNow.ToString("O"));
                return true;
            }
            catch (IOException)
            {
                if (attempt == 0)
                {
                    try
                    {
                        var info = new FileInfo(lockPath);
                        if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc > staleAfter)
                        {
                            CPH.LogWarn($"[Lock] Verwaiste Sperre '{lockName}' ist älter als {staleAfter.TotalMinutes:F0}min – wird entfernt.");
                            File.Delete(lockPath);
                            continue;
                        }
                    }
                    catch { }
                }
                return false;
            }
        }
        return false;
    }

    private void ReleaseLock(string lockPath)
    {
        try { if (!string.IsNullOrEmpty(lockPath) && File.Exists(lockPath)) File.Delete(lockPath); }
        catch (Exception ex) { CPH.LogWarn($"[Lock] Konnte Sperre nicht entfernen: {ex.Message}"); }
    }

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
        bool success = DownloadVodAttempt(twitchDLPath, vodId, outputPath, out string stderrText);

        if (!success && LooksLikeCorruptedTwitchDlCache(stderrText))
        {
            CPH.LogWarn($"[YTUploader] ⚠️ Download-Cache für VOD {vodId} scheint beschädigt (Segmente lassen sich nicht zusammenfügen) – Cache wird geleert, ein Versuch wird wiederholt.");
            AppendLog("YTUploader", $"⚠️ twitch-dl-Cache für VOD {vodId} beschädigt – wird bereinigt, neuer Versuch...");

            ClearTwitchDlCache(vodId);

            // Falls vom fehlgeschlagenen Versuch eine unvollständige Ausgabedatei liegen geblieben ist,
            // vor dem erneuten Versuch entfernen (twitch-dl würde sonst ggf. verwirrt sein).
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }

            success = DownloadVodAttempt(twitchDLPath, vodId, outputPath, out stderrText);
        }

        return success;
    }

    // Erkennt das typische Fehlerbild eines beschädigten twitch-dl-Zwischenspeichers:
    // Segmente werden aus dem Cache "wiederverwendet" (Download wirkt unrealistisch schnell
    // abgeschlossen), aber der finale ffmpeg-Join scheitert an einem kaputten Segment.
    private bool LooksLikeCorruptedTwitchDlCache(string stderrText)
    {
        if (string.IsNullOrEmpty(stderrText)) return false;
        return stderrText.Contains("Joining files failed")
            || stderrText.Contains("Invalid data found when processing input")
            || stderrText.Contains("Error opening input file");
    }

    // Löscht ausschließlich twitch-dls eigenen Download-Zwischenspeicher für eine VOD-ID
    // (Segment-Dateien unter %LOCALAPPDATA%\twitch-dl\videos\<id>\) – niemals Stream-Aufnahmen.
    private void ClearTwitchDlCache(string vodId)
    {
        try
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "twitch-dl", "videos", vodId);

            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                CPH.LogInfo($"[YTUploader] 🗑️ twitch-dl-Cache gelöscht: {cacheDir}");
                AppendLog("YTUploader", $"🗑️ twitch-dl-Cache für VOD {vodId} gelöscht.");
            }
            else
            {
                CPH.LogInfo($"[YTUploader] Kein twitch-dl-Cache unter {cacheDir} gefunden – nichts zu löschen.");
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[YTUploader] Konnte twitch-dl-Cache nicht löschen: {ex.Message}");
        }
    }

    private bool DownloadVodAttempt(string twitchDLPath, string vodId, string outputPath, out string stderrText)
    {
        var stderrBuilder = new StringBuilder();
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
                var lastProgressWrite = DateTime.MinValue;
                var progressRegex = new Regex(@"Downloaded\s+\d+/\d+\s+VODs\s+(\d+)%\s+of\s+~([\d.]+)GB(?:\s+at\s+([\d.]+)MB/s)?(?:\s+ETA\s+([\d:]+))?");
                bool joinAnnounced = false;

                // Stdout und Stderr asynchron loggen damit der Fortschritt sichtbar ist
                proc.OutputDataReceived += (s, e) => {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    CPH.LogInfo($"[YTUploader] twitch-dl: {e.Data}");

                    // twitch-dl lädt Segmente in einen Cache-Ordner – die finale mp4 entsteht
                    // erst ganz am Ende beim ffmpeg-Join. Ohne diese Auswertung bliebe das
                    // Dashboard für die gesamte Download-Phase auf "Datei noch nicht angelegt"
                    // stehen, obwohl twitch-dl hier längst echten Fortschritt meldet.
                    var match = progressRegex.Match(e.Data);
                    if (match.Success && (DateTime.UtcNow - lastProgressWrite).TotalSeconds >= 3)
                    {
                        lastProgressWrite = DateTime.UtcNow;
                        int pct = int.Parse(match.Groups[1].Value);
                        string speed = match.Groups[3].Success ? $"{match.Groups[3].Value} MB/s" : "–";
                        string eta   = match.Groups[4].Success ? match.Groups[4].Value : "–";
                        double totalGB = double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : 0;
                        int approxMB = (int)(pct / 100.0 * totalGB * 1024);

                        CPH.SetGlobalVar("dl_mb", approxMB.ToString(), false);
                        CPH.SetGlobalVar("yt_status", "DOWNLOADING", false);
                        CPH.SetGlobalVar("yt_detail", $"{pct}% · {speed} · ETA {eta}", false);
                        WriteStatus("download", $"Segmente werden geladen – {pct}% · {speed} · ETA {eta}", approxMB, pct);
                    }
                };
                proc.ErrorDataReceived += (s, e) => {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        CPH.LogWarn($"[YTUploader] twitch-dl stderr: {e.Data}");
                        stderrBuilder.AppendLine(e.Data);

                        if (!joinAnnounced && e.Data.Contains("Joining files"))
                        {
                            joinAnnounced = true;
                            AppendLog("YTUploader", "🔗 Segmente werden zur mp4 zusammengefügt (ffmpeg)...");
                            WriteStatus("download", "Verbinde Segmente (ffmpeg)...");
                        }
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Alle 30 Sekunden zusätzlich die Dateigröße loggen (greift v.a. während
                // der ffmpeg-Join-Phase, wenn die finale Datei bereits existiert)
                var timer = new System.Timers.Timer(30000);
                timer.Elapsed += (s, e) => {
                    try {
                        if (File.Exists(outputPath))
                        {
                            long mb = new FileInfo(outputPath).Length / 1024 / 1024;
                            CPH.LogInfo($"[YTUploader] ⏳ Verbinde Segmente... bisher {mb} MB geschrieben");
                            CPH.SetGlobalVar("dl_mb", mb.ToString(), false);
                            CPH.SetGlobalVar("yt_status", "DOWNLOADING", false);
                            CPH.SetGlobalVar("yt_detail", $"{mb} MB (ffmpeg-Join)", false);
                            WriteStatus("download", $"Verbinde Segmente (ffmpeg) – {mb} MB geschrieben", (int)mb);
                        }
                    } catch { }
                };
                timer.Start();

                proc.WaitForExit();
                timer.Stop();
                timer.Dispose();

                CPH.LogInfo($"[YTUploader] twitch-dl beendet mit ExitCode: {proc.ExitCode}");

                bool success = proc.ExitCode == 0 && File.Exists(outputPath);
                if (success)
                {
                    long mb = new FileInfo(outputPath).Length / 1024 / 1024;
                    CPH.LogInfo($"[YTUploader] ✅ Download abgeschlossen: {outputPath} ({mb} MB)");
                }
                else
                {
                    CPH.LogError($"[YTUploader] ❌ Download fehlgeschlagen (Exit: {proc.ExitCode}, Datei existiert: {File.Exists(outputPath)})");
                }
                stderrText = stderrBuilder.ToString();
                return success;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] DownloadVod Exception: {ex.Message}");
            stderrText = stderrBuilder.ToString();
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
                string savedAccessToken = lines[0].Trim();
                string refreshToken     = lines[1].Trim();
                string expiryStr        = lines.Length >= 3 ? lines[2].Trim() : "";

                if (DateTime.TryParse(expiryStr, out DateTime expiry) && expiry > DateTime.UtcNow.AddMinutes(5))
                {
                    CPH.LogInfo("[YTUploader] OAuth Token noch gültig.");
                    return savedAccessToken;
                }

                CPH.LogInfo("[YTUploader] Token abgelaufen – erneuere...");
                return RefreshAccessToken(tokenPath, refreshToken, clientId, clientSecret);
            }
        }

        CPH.LogInfo("[YTUploader] Kein Token gefunden – starte Browser OAuth Flow...");
        return AuthorizeNewToken(tokenPath, clientId, clientSecret);
    }

    private string AuthorizeNewToken(string tokenPath, string clientId, string clientSecret)
    {
        try
        {
            string authUrl = $"https://accounts.google.com/o/oauth2/auth" +
                $"?client_id={clientId}" +
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
            var body = $"code={Uri.EscapeDataString(code)}" +
                       $"&client_id={Uri.EscapeDataString(clientId)}" +
                       $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
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

    private string RefreshAccessToken(string tokenPath, string refreshToken, string clientId, string clientSecret)
    {
        try
        {
            var body = $"refresh_token={Uri.EscapeDataString(refreshToken)}" +
                       $"&client_id={Uri.EscapeDataString(clientId)}" +
                       $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
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

    // Liest exakt `count` Bytes (oder bis zum Dateiende), da Stream.Read
    // nicht garantiert, den Puffer in einem Aufruf vollständig zu füllen.
    private int ReadFully(System.IO.Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private string UploadToYoutube(string filePath, string title, string gameName, string accessToken)
    {
        try
        {
            CPH.LogInfo($"[YTUploader] Starte YouTube Upload: {title}");

            long fileSize = new FileInfo(filePath).Length;

            string description = $"Aufgezeichnet auf Twitch.\n\nSpiel: {(string.IsNullOrWhiteSpace(gameName) ? "Unbekannt" : gameName)}\nHochgeladen: {DateTime.Now:yyyy-MM-dd HH:mm}";

            string metadata = $@"{{
                ""snippet"": {{
                    ""title"": ""{EscapeJson(title)}"",
                    ""description"": ""{EscapeJson(description)}"",
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

            long totalMB = fileSize / 1024 / 1024;
            CPH.LogInfo($"[YTUploader] Upload-Session gestartet – lade {totalMB} MB in Blöcken hoch...");
            AppendLog("YTUploader", $"⬆️ Upload gestartet: {totalMB} MB");

            // Große Dateien werden NICHT mehr in einem einzigen PUT-Request hochgeladen
            // (das kann bei mehreren GB in einem Rutsch hängen bleiben, ohne je zurückzukehren
            // oder einen Fehler zu werfen). Stattdessen: blockweise mit Content-Range + Retry,
            // dazu echte Fortschrittsanzeige fürs Dashboard.
            const int chunkSize = 16 * 1024 * 1024; // 16 MB pro Block
            byte[] buffer = new byte[chunkSize];
            long uploaded = 0;
            int lastLoggedPct = -1;
            string videoId = null;

            using (var fileStream = File.OpenRead(filePath))
            {
                while (uploaded < fileSize)
                {
                    int bytesToRead = (int)Math.Min(chunkSize, fileSize - uploaded);
                    int bytesRead = ReadFully(fileStream, buffer, bytesToRead);
                    if (bytesRead <= 0) break;

                    bool chunkOk = false;
                    bool isFinalSuccess = false;
                    string chunkResponseBody = "";
                    System.Net.HttpStatusCode chunkStatus = 0;

                    for (int attempt = 0; attempt < 3 && !chunkOk; attempt++)
                    {
                        if (attempt > 0)
                        {
                            CPH.LogWarn($"[YTUploader] Chunk-Upload wird wiederholt (Versuch {attempt + 1}/3) bei Byte {uploaded}...");
                            System.Threading.Thread.Sleep(2000 * attempt);
                        }

                        using (var chunkContent = new ByteArrayContent(buffer, 0, bytesRead))
                        {
                            var chunkRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                            chunkRequest.Content = chunkContent;
                            chunkRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                            chunkRequest.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                                uploaded, uploaded + bytesRead - 1, fileSize);

                            var chunkResponse = Http.SendAsync(chunkRequest).Result;
                            chunkStatus = chunkResponse.StatusCode;
                            chunkResponseBody = chunkResponse.Content.ReadAsStringAsync().Result;

                            if ((int)chunkStatus == 308)
                            {
                                chunkOk = true;
                                isFinalSuccess = false;
                            }
                            else if (chunkResponse.IsSuccessStatusCode)
                            {
                                chunkOk = true;
                                isFinalSuccess = true;
                            }
                        }
                    }

                    if (!chunkOk)
                    {
                        CPH.LogError($"[YTUploader] Chunk-Upload endgültig fehlgeschlagen bei Byte {uploaded}: {chunkStatus} – {chunkResponseBody}");
                        AppendLog("YTUploader", $"❌ Chunk-Upload fehlgeschlagen bei {uploaded / 1024 / 1024} MB.");
                        return null;
                    }

                    uploaded += bytesRead;
                    int pct = (int)Math.Min(99, (uploaded * 100L) / fileSize);
                    long uploadedMB = uploaded / 1024 / 1024;

                    CPH.LogInfo($"[YTUploader] ⬆️ Upload... {uploadedMB}/{totalMB} MB ({pct}%)");
                    CPH.SetGlobalVar("yt_status", "UPLOADING", false);
                    CPH.SetGlobalVar("yt_detail", $"{uploadedMB}/{totalMB} MB hochgeladen", false);

                    if (pct / 10 > lastLoggedPct / 10)
                    {
                        lastLoggedPct = pct;
                        AppendLog("YTUploader", $"⬆️ Upload... {uploadedMB}/{totalMB} MB ({pct}%)");
                    }

                    WriteStatus("upload", $"Lade hoch: {title} – {uploadedMB}/{totalMB} MB", -1, -1, pct);

                    if (isFinalSuccess)
                    {
                        var idMatch = Regex.Match(chunkResponseBody, @"""id""\s*:\s*""([^""]+)""");
                        videoId = idMatch.Success ? idMatch.Groups[1].Value : null;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(videoId))
            {
                CPH.LogError("[YTUploader] Upload-Schleife beendet, aber keine Video-ID in der letzten Antwort gefunden.");
                AppendLog("YTUploader", "❌ Upload abgeschlossen, aber keine Video-ID erhalten – bitte manuell auf YouTube prüfen.");
                return null;
            }

            CPH.SetGlobalVar("yt_status", "DONE", false);
            CPH.SetGlobalVar("yt_detail", $"Video-ID: {videoId}", false);
            CPH.LogInfo($"[YTUploader] ✅ Upload abgeschlossen. Video-ID: {videoId}");
            return videoId;
        }
        catch (Exception ex)
        {
            CPH.LogError($"[YTUploader] UploadToYoutube Exception: {ex.Message}");
            AppendLog("YTUploader", $"❌ UploadToYoutube Exception: {ex.Message}");
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

    // ── Hilfsmethoden ─────────────────────────────────────────────

    private void WriteStatus(string step, string detail = "",
    int downloadMB = -1, int downloadPct = -1, int uploadPct = -1,
    string ytStatus = "", string ytDetail = "")
{
    try
    {
        string csvPath    = CPH.GetGlobalVar<string>("CsvPath", true) ?? @"D:\Stream\Records\streams.csv";
        string statusPath = csvPath.Replace("streams.csv", "dashboard.html");

        // CSV lesen
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
                lastName = last[3].Trim();
                lastDate = last[1].Trim();
                lastGame = last[2].Trim();
            }
        }

        // Checker aus Global Vars
        string cTotal   = CPH.GetGlobalVar<string>("checker_total",     false) ?? "–";
        string cOk      = CPH.GetGlobalVar<string>("checker_ok",        false) ?? "–";
        string cMissing = CPH.GetGlobalVar<string>("checker_missing",    false) ?? "–";
        string cNoBack  = CPH.GetGlobalVar<string>("checker_nobackup",   false) ?? "–";
        string cTime    = CPH.GetGlobalVar<string>("checker_lastcheck",  false) ?? "";

        string dlMB  = downloadMB  >= 0 ? downloadMB.ToString()  : (CPH.GetGlobalVar<string>("dl_mb",     false) ?? "0");
        string ytSt  = !string.IsNullOrEmpty(ytStatus) ? ytStatus : (CPH.GetGlobalVar<string>("yt_status", false) ?? "–");
        string ytDet = !string.IsNullOrEmpty(ytDetail) ? ytDetail : (CPH.GetGlobalVar<string>("yt_detail", false) ?? "–");

        // Gemeinsames Live-Log aller Scripts (chronologisch, älteste zuerst)
        string sharedLogRaw = CPH.GetGlobalVar<string>("dashboard_log", false) ?? "";
        var liveLogHtml = new System.Text.StringBuilder();
        var allLogLines = sharedLogRaw.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var logLines = allLogLines.Skip(Math.Max(0, allLogLines.Length - 60));
        foreach (var line in logLines)
        {
            // Format: yyyy-MM-dd|HH:mm:ss|ScriptTag|Nachricht – mit Fallback fürs alte
            // Format (falls noch alte Zeilen aus einer vorherigen Version im Log liegen).
            var parts = line.Split(new[] { '|' }, 4);
            if (parts.Length == 4)
            {
                string pDate = parts[0], pTime = parts[1], pTag = parts[2], pMsg = parts[3];
                string rowCls = pTag == "StreamArchiver" ? "row-archiver"
                              : pTag == "StreamChecker"  ? "row-checker"
                              : pTag == "YTUploader"      ? "row-uploader"
                              : "row-other";
                liveLogHtml.Append($@"<div class=""livelog-line {rowCls}"">
                  <span class=""livelog-date"">{System.Net.WebUtility.HtmlEncode(pDate)}</span>
                  <span class=""livelog-time"">{System.Net.WebUtility.HtmlEncode(pTime)}</span>
                  <span class=""livelog-badge"">{System.Net.WebUtility.HtmlEncode(pTag)}</span>
                  <span class=""livelog-msg"">{System.Net.WebUtility.HtmlEncode(pMsg)}</span>
                </div>");
            }
            else
            {
                // Altes Format ohne Pipes (z.B. "HH:mm:ss [Script] Nachricht") – Script-Tag
                // trotzdem per Klammer-Suche erkennen, damit auch alte Zeilen farbig bleiben.
                var legacyMatch = Regex.Match(line, @"^(\d{2}:\d{2}:\d{2})\s*\[([^\]]+)\]\s*(.*)$");
                string lDate = "–", lTime = line, lTag = "", lMsg = line;
                if (legacyMatch.Success)
                {
                    lTime = legacyMatch.Groups[1].Value;
                    lTag  = legacyMatch.Groups[2].Value;
                    lMsg  = legacyMatch.Groups[3].Value;
                }
                string legacyRowCls = lTag == "StreamArchiver" ? "row-archiver"
                                    : lTag == "StreamChecker"  ? "row-checker"
                                    : lTag == "YTUploader"      ? "row-uploader"
                                    : "row-other";
                if (legacyMatch.Success)
                {
                    liveLogHtml.Append($@"<div class=""livelog-line {legacyRowCls}"">
                      <span class=""livelog-date"">{System.Net.WebUtility.HtmlEncode(lDate)}</span>
                      <span class=""livelog-time"">{System.Net.WebUtility.HtmlEncode(lTime)}</span>
                      <span class=""livelog-badge"">{System.Net.WebUtility.HtmlEncode(lTag)}</span>
                      <span class=""livelog-msg"">{System.Net.WebUtility.HtmlEncode(lMsg)}</span>
                    </div>");
                }
                else
                {
                    liveLogHtml.Append($"<div class=\"livelog-line\"><span class=\"livelog-msg\">{System.Net.WebUtility.HtmlEncode(line)}</span></div>");
                }
            }
        }
        if (liveLogHtml.Length == 0)
            liveLogHtml.Append("<div class=\"livelog-line\"><span class=\"livelog-msg\">Noch keine Log-Einträge</span></div>");

        // Step → Anzeige
        var stepIcons = new Dictionary<string,string> {
            {"idle","💤"}, {"ffmpeg","🔍"}, {"copy","📋"},
            {"download","⬇️"}, {"upload","⬆️"}, {"checker","🔎"},
            {"done","✅"}, {"error","❌"}
        };
        var stepNames = new Dictionary<string,string> {
            {"idle","Warte auf nächsten Stream"}, {"ffmpeg","FFmpeg Dateiprüfung"},
            {"copy","Umbenennen & Kopieren"}, {"download","Twitch VOD Download"},
            {"upload","YouTube Upload"}, {"checker","StreamChecker läuft"},
            {"done","Fertig"}, {"error","Fehler aufgetreten"}
        };
        var stepColors = new Dictionary<string,string> {
            {"idle","#53535F"}, {"ffmpeg","#9147FF"}, {"copy","#9147FF"},
            {"download","#9147FF"}, {"upload","#9147FF"}, {"checker","#9147FF"},
            {"done","#1DB954"}, {"error","#E91916"}
        };

        bool isActive = step != "idle" && step != "done" && step != "error";
        string icon  = stepIcons.ContainsKey(step)  ? stepIcons[step]  : "⚙️";
        string sName = stepNames.ContainsKey(step)  ? stepNames[step]  : step;
        string sColor= stepColors.ContainsKey(step) ? stepColors[step] : "#ADADB8";

        // Progress bar
        string progressHtml = "";
        int mbInt = 0;
        bool mbIntParsed = int.TryParse(dlMB, out mbInt);
        if (step == "download" && (downloadPct >= 0 || (mbIntParsed && mbInt > 0)))
        {
            int pct = downloadPct >= 0 ? downloadPct : Math.Min(99, mbInt / 30); // echter Wert, sonst grobe Schätzung als Fallback
            progressHtml = $@"
            <div class=""progress-wrap"">
              <div class=""progress-meta""><span>Download</span><span>{dlMB} MB · {pct}%</span></div>
              <div class=""progress-bar""><div class=""progress-fill"" style=""width:{pct}%""></div></div>
            </div>";
        }
        else if (step == "copy" && downloadPct >= 0)
        {
            progressHtml = $@"
            <div class=""progress-wrap"">
              <div class=""progress-meta""><span>Kopiere ins Archiv</span><span>{dlMB} MB ({downloadPct}%)</span></div>
              <div class=""progress-bar""><div class=""progress-fill"" style=""width:{downloadPct}%""></div></div>
            </div>";
        }
        else if (step == "upload" && uploadPct >= 0)
        {
            progressHtml = $@"
            <div class=""progress-wrap"">
              <div class=""progress-meta""><span>Upload zu YouTube</span><span>{uploadPct}%</span></div>
              <div class=""progress-bar""><div class=""progress-fill green"" style=""width:{uploadPct}%""></div></div>
            </div>";
        }

        // Recent streams rows
        var logRows = new System.Text.StringBuilder();
        foreach (var p in recentStreams)
        {
            string st = p.Length > 4 ? p[4].Trim() : "OK";
            string pillClass = st == "OK" ? "ok" : st.Contains("KEIN") ? "error" : "warn";
            logRows.Append($@"<div class=""log-entry"">
              <span class=""log-time"">{p[1].Trim()}</span>
              <span class=""log-nr"">#{p[0].Trim()}</span>
              <span class=""log-name"">{p[3].Trim()}</span>
              <span class=""pill {pillClass}"">{st}</span>
            </div>");
        }
        if (logRows.Length == 0)
            logRows.Append(@"<div style=""font-family:var(--mono);font-size:11px;color:var(--text-muted);padding:8px 10px"">Keine Einträge</div>");

        // Checker time
        string checkerAgo = "–";
        // RoundtripKind ist Pflicht hier: ohne das interpretiert TryParse das "Z" am
        // Ende zwar korrekt, wandelt den Wert aber in lokale Zeit um (Kind=Local) –
        // die anschliessende Subtraktion von DateTime.UtcNow vergleicht dann UTC mit
        // Lokalzeit und liefert je nach Zeitzone einen falschen (z.T. negativen) Wert.
        if (!string.IsNullOrEmpty(cTime) &&
            DateTime.TryParse(cTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime ct))
        {
            int diff = Math.Max(0, (int)(DateTime.UtcNow - ct).TotalSeconds);
            checkerAgo = diff < 60 ? $"vor {diff}s" : diff < 3600 ? $"vor {diff/60}min" : $"vor {diff/3600}h";
        }

        // YT pill
        string ytPillClass = ytSt == "OK" ? "ok" : ytSt == "FEHLGESCHLAGEN" ? "error" :
                             ytSt.Contains("ING") ? "running" : "idle";
        string ytPillHtml = $@"<span class=""pill {ytPillClass}"">{ytSt}</span>";

        string now = DateTime.Now.ToString("HH:mm:ss");
        string badgeClass = isActive ? "live-badge active" : "live-badge";
        string badgeText  = isActive ? "AKTIV" : "IDLE";

        string dashboardDir = Path.GetDirectoryName(statusPath) ?? ".";
        string cssPath   = Path.Combine(dashboardDir, "dashboard.css");
        string stepPath  = Path.Combine(dashboardDir, "dashboard-step.html");
        string statsPath = Path.Combine(dashboardDir, "dashboard-stats.html");
        string logPath   = Path.Combine(dashboardDir, "dashboard-log.html");
        string cacheBust = DateTime.UtcNow.Ticks.ToString();

        // ── dashboard.css – gemeinsame Styles für Shell + alle Fragmente ──
        string css = @"
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{--bg:#0E0E10;--surface:#18181B;--surface2:#1F1F23;--border:#2A2A2E;--purple:#9147FF;--purple-dim:#6441A4;--green:#1DB954;--red:#E91916;--yellow:#F59E0B;--text:#EFEFF1;--text-dim:#ADADB8;--text-muted:#53535F;--mono:Consolas,'Cascadia Mono','SFMono-Regular',Menlo,monospace;--sans:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif}
html,body{background:radial-gradient(ellipse 1200px 800px at 15% -10%,rgba(145,71,255,.06),transparent),var(--bg);color:var(--text);font-family:var(--sans)}
body{padding:12px}
.header{display:flex;align-items:center;justify-content:space-between;margin-bottom:20px}
.header-left{display:flex;align-items:center;gap:14px}
.logo-icon{width:38px;height:38px;background:linear-gradient(135deg,var(--purple),var(--purple-dim));border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:18px;box-shadow:0 2px 8px rgba(145,71,255,.35)}
.header h1{font-family:var(--mono);font-size:16px;font-weight:700;letter-spacing:.08em;text-transform:uppercase}
.header-sub{font-size:11px;color:var(--text-muted);font-family:var(--mono);margin-top:2px}
.live-badge{display:flex;align-items:center;gap:7px;padding:5px 12px;border-radius:4px;background:var(--surface2);border:1px solid var(--border);font-family:var(--mono);font-size:11px;font-weight:700;letter-spacing:.1em;color:var(--text-muted);text-transform:uppercase}
.live-badge.active{border-color:var(--purple);color:var(--purple)}
.live-dot{width:7px;height:7px;border-radius:50%;background:var(--text-muted)}
.live-badge.active .live-dot{background:var(--purple);animation:pulse 1.5s ease-in-out infinite}
@keyframes pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:.4;transform:scale(.7)}}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-bottom:16px}
.grid-3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:16px;margin-bottom:16px}
.card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;box-shadow:0 1px 2px rgba(0,0,0,.25),inset 0 1px 0 rgba(255,255,255,.02)}
.card-label{font-size:10px;font-weight:600;letter-spacing:.12em;text-transform:uppercase;color:var(--text-muted);margin-bottom:14px;font-family:var(--mono)}
.step-card{grid-column:span 2;border-color:var(--purple-dim);background:linear-gradient(180deg,var(--surface),rgba(145,71,255,.03))}
.step-inner{display:flex;align-items:center;gap:16px}
.step-icon{width:44px;height:44px;border-radius:10px;background:var(--surface2);border:1px solid var(--step-color,var(--border));display:flex;align-items:center;justify-content:center;font-size:22px;flex-shrink:0}
.step-name{font-size:18px;font-weight:600;color:var(--text);margin-bottom:4px}
.step-detail{font-family:var(--mono);font-size:12px;color:var(--text-dim)}
.progress-wrap{margin-top:14px}
.progress-meta{display:flex;justify-content:space-between;font-family:var(--mono);font-size:11px;color:var(--text-muted);margin-bottom:6px}
.progress-bar{height:4px;background:var(--surface2);border-radius:2px;overflow:hidden}
.progress-fill{height:100%;background:var(--purple);border-radius:2px}
.progress-fill.green{background:var(--green)}
.stat-value{font-family:var(--mono);font-size:28px;font-weight:700;line-height:1;margin-bottom:5px;letter-spacing:-.01em}
.stat-sub{font-size:12px;color:var(--text-muted)}
.pill{display:inline-flex;align-items:center;gap:5px;padding:3px 9px;border-radius:20px;font-size:11px;font-weight:600;font-family:var(--mono)}
.pill.ok{background:rgba(29,185,84,.15);color:var(--green)}
.pill.error{background:rgba(233,25,22,.15);color:var(--red)}
.pill.warn{background:rgba(245,158,11,.15);color:var(--yellow)}
.pill.idle{background:var(--surface2);color:var(--text-muted)}
.pill.running{background:rgba(145,71,255,.15);color:var(--purple)}
.log-entry{display:grid;grid-template-columns:80px 50px 1fr auto;gap:10px;padding:6px 10px;border-radius:5px;font-family:var(--mono);font-size:11px;align-items:center}
.log-entry:hover{background:var(--surface2)}
.log-time{color:var(--text-muted)}
.log-nr{color:var(--purple);font-weight:700}
.log-name{color:var(--text-dim);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.checker-row{display:flex;justify-content:space-between;align-items:center;padding:8px 0;border-bottom:1px solid var(--border)}
.checker-row:last-child{border-bottom:none}
.checker-label{font-family:var(--mono);font-size:12px;color:var(--text-dim)}
.checker-val{font-family:var(--mono);font-size:14px;font-weight:700}
.ticker{text-align:center;font-family:var(--mono);font-size:10px;color:var(--text-muted);margin-top:14px}
.ticker span{color:var(--purple)}
.livelog-box{max-height:480px;overflow-y:auto;font-family:var(--mono);font-size:11px}
.livelog-line{display:flex;align-items:baseline;gap:9px;padding:6px 10px;line-height:1.6;border-left:3px solid var(--border);border-bottom:1px solid rgba(255,255,255,.04)}
.livelog-line:last-child{border-bottom:none}
.livelog-line:hover{background:var(--surface2)}
.livelog-date{color:var(--text-muted);opacity:.65;white-space:nowrap;font-size:10px}
.livelog-time{color:var(--text-muted);white-space:nowrap;font-weight:600}
.livelog-badge{flex-shrink:0;padding:2px 9px;border-radius:20px;font-size:9px;font-weight:700;letter-spacing:.04em;white-space:nowrap;background:var(--surface2);color:var(--text-muted)}
.livelog-msg{color:var(--text-dim);word-break:break-word;flex:1;min-width:0}
.row-archiver{border-left-color:var(--purple);background:rgba(145,71,255,.06)}
.row-archiver .livelog-badge{background:rgba(145,71,255,.22);color:#c4a3ff}
.row-archiver .livelog-msg{color:#e4d9ff}
.row-checker{border-left-color:#3FA9F5;background:rgba(63,169,245,.06)}
.row-checker .livelog-badge{background:rgba(63,169,245,.22);color:#8fcdfb}
.row-checker .livelog-msg{color:#dbeefe}
.row-uploader{border-left-color:var(--green);background:rgba(29,185,84,.06)}
.row-uploader .livelog-badge{background:rgba(29,185,84,.22);color:#7be3a0}
.row-uploader .livelog-msg{color:#d6f5e1}
.livelog-hint{color:var(--text-muted);font-weight:400;text-transform:none;letter-spacing:0;font-size:10px}
";
        try { File.WriteAllText(cssPath, css); } catch { }

        // ── dashboard-step.html – Header + aktueller Schritt (Refresh alle 3s) ──
        string stepHtml = $@"<!DOCTYPE html>
<html lang=""de"">
<head>
<meta charset=""UTF-8"">
<meta http-equiv=""refresh"" content=""5"">
<meta http-equiv=""Cache-Control"" content=""no-cache"">
<link rel=""stylesheet"" href=""dashboard.css?v={cacheBust}"">
<style>:root{{--step-color:{sColor}}}</style>
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
<div class=""ticker"">Aktualisiert: <span>{now}</span></div>
</body>
</html>";
        try { File.WriteAllText(stepPath, stepHtml); } catch { }

        // ── dashboard-stats.html – Statistiken, Checker, letzte Streams (Refresh alle 3s) ──
        string statsHtml = $@"<!DOCTYPE html>
<html lang=""de"">
<head>
<meta charset=""UTF-8"">
<meta http-equiv=""refresh"" content=""10"">
<meta http-equiv=""Cache-Control"" content=""no-cache"">
<link rel=""stylesheet"" href=""dashboard.css?v={cacheBust}"">
</head>
<body>
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
    <div style=""margin-top:4px"">{ytPillHtml}</div>
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
</body>
</html>";
        try { File.WriteAllText(statsPath, statsHtml); } catch { }

        // ── dashboard-log.html – Live Log, pausiert automatisch während Textauswahl ──
        string logHtml = $@"<!DOCTYPE html>
<html lang=""de"">
<head>
<meta charset=""UTF-8"">
<meta http-equiv=""Cache-Control"" content=""no-cache, no-store, must-revalidate"">
<meta http-equiv=""Pragma"" content=""no-cache"">
<meta http-equiv=""Expires"" content=""0"">
<link rel=""stylesheet"" href=""dashboard.css?v={cacheBust}"">
</head>
<body>
<div class=""card"">
  <div class=""card-label"">Live Log <span class=""livelog-hint"">· alle Scripts · <span id=""refreshState"">🟢 live</span></span></div>
  <div class=""livelog-box"">{liveLogHtml}</div>
</div>
<script>
(function(){{
  var stateEl = document.getElementById('refreshState');
  function hasSelection(){{
    var sel = window.getSelection();
    return !!(sel && sel.toString().length > 0);
  }}
  function reloadFresh(){{
    // Cache-Buster in der URL erzwingt eine ECHTE Neuanfrage der Datei von der
    // Platte statt einer evtl. gecachten Kopie – reines location.reload() hat
    // das nicht zuverlässig genug getan.
    var base = location.pathname.split('?')[0];
    location.href = base + '?t=' + Date.now();
  }}
  function tick(){{
    if (hasSelection()) {{
      if (stateEl) stateEl.textContent = '⏸ pausiert (Auswahl aktiv)';
      setTimeout(tick, 400);
    }} else {{
      reloadFresh();
    }}
  }}
  setTimeout(tick, 2000);
}})();
</script>
</body>
</html>";
        try { File.WriteAllText(logPath, logHtml); } catch { }

        // ── dashboard.html – statische Shell, lädt sich selbst NIE neu ──
        string shellHtml = $@"<!DOCTYPE html>
<html lang=""de"">
<head>
<meta charset=""UTF-8"">
<title>StreamArchiver</title>
<style>
html,body{{background:#0E0E10;margin:0;padding:16px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif}}
iframe{{border:0;width:100%;display:block;background:transparent}}
.f-step{{height:220px;margin-bottom:16px}}
.row{{display:flex;gap:16px;align-items:stretch}}
.f-stats{{flex:1 1 60%;min-width:0;height:600px}}
.f-log{{flex:1 1 40%;min-width:0;height:600px}}
.hint{{text-align:center;font-family:Consolas,'Cascadia Mono',monospace;font-size:10px;color:#53535F;margin-top:14px}}
@media (max-width:900px){{
  .row{{flex-direction:column}}
  .f-stats,.f-log{{height:480px}}
}}
</style>
</head>
<body>
<iframe class=""f-step"" src=""dashboard-step.html?t={cacheBust}""></iframe>
<div class=""row"">
  <iframe class=""f-stats"" src=""dashboard-stats.html?t={cacheBust}""></iframe>
  <iframe class=""f-log"" src=""dashboard-log.html?t={cacheBust}""></iframe>
</div>
<div class=""hint"">Schritt &amp; Statistik aktualisieren sich alle 3s · Live-Log pausiert automatisch während Textauswahl</div>
</body>
</html>";
        File.WriteAllText(statusPath, shellHtml);
    }
    catch (Exception ex)
    {
        CPH.LogWarn($"[StatusWriter] {ex.Message}");
    }
}
}
