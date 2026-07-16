using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Collections.Generic;

public class CPHInline
{
    public bool Execute()
    {
        string recordBaseDir  = CPH.GetGlobalVar<string>("RecordBaseDir",  true);
        string recordsRootDir = CPH.GetGlobalVar<string>("RecordsRootDir", true);
        string ffmpegPath     = CPH.GetGlobalVar<string>("FFmpegPath",     true);
        string twitchDLPath   = CPH.GetGlobalVar<string>("TwitchDLPath",   true);
        string twitchChannel  = CPH.GetGlobalVar<string>("TwitchChannel",  true);
        string csvPath        = CPH.GetGlobalVar<string>("CsvPath",        true);

        // 1. Spielname vom Trigger
        CPH.TryGetArg("game", out string gameName);
        if (string.IsNullOrWhiteSpace(gameName))
        {
            CPH.LogWarn("[StreamArchiver] Kein Spielname gefunden – Abbruch.");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ❌", "Kein Spielname gefunden.", "", "");
            WriteStatus("error", "Kein Spielname gefunden");
            return false;
        }

        string safeGameName = Regex.Replace(gameName, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[StreamArchiver] Spielname: {safeGameName}");

        // 2. Neueste MP4 finden
        var mp4Files = Directory.GetFiles(recordBaseDir, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .ToList();

        if (mp4Files.Count == 0)
        {
            CPH.LogWarn($"[StreamArchiver] Keine MP4 in {recordBaseDir} gefunden.");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ❌", "Keine MP4 Datei gefunden.", "", "");
            WriteStatus("error", "Keine MP4 gefunden");
            return false;
        }

        FileInfo latestFile = mp4Files[0];
        CPH.LogInfo($"[StreamArchiver] Gefundene Datei: {latestFile.FullName}");

        // 3. FFmpeg Check
        CPH.LogInfo("[StreamArchiver] Starte FFmpeg Check...");
        WriteStatus("ffmpeg", $"Prüfe {latestFile.Name}");
        bool fileIsHealthy = CheckFileWithFFmpeg(latestFile.FullName, ffmpegPath);
        CPH.LogInfo($"[StreamArchiver] FFmpeg Ergebnis: {fileIsHealthy}");

        // 4. Zielordner + Nummer
        string targetDir = Path.Combine(recordsRootDir, safeGameName);
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            CPH.LogInfo($"[StreamArchiver] Ordner angelegt: {targetDir}");
        }

        int nextNumber      = GetNextFileNumber(targetDir);
        string numberedName = $"{nextNumber:D2}_{safeGameName}.mp4";
        string targetPath   = Path.Combine(targetDir, numberedName);

        if (fileIsHealthy)
        {
            WriteStatus("copy", $"Kopiere {numberedName}");
            string renamedSourcePath = Path.Combine(recordBaseDir, numberedName);
            File.Move(latestFile.FullName, renamedSourcePath);
            CPH.LogInfo($"[StreamArchiver] ✅ Umbenannt: {renamedSourcePath}");

            var copyPsi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = $"-i \"{renamedSourcePath}\" -map 0 -c copy -movflags faststart \"{targetPath}\"",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using (var copyProc = Process.Start(copyPsi))
            {
                copyProc.BeginErrorReadLine();
                copyProc.BeginOutputReadLine();
                copyProc.WaitForExit();
                if (copyProc.ExitCode == 0)
                    CPH.LogInfo($"[StreamArchiver] ✅ Kopie erstellt: {targetPath}");
                else
                    CPH.LogError($"[StreamArchiver] ❌ FFmpeg Kopie fehlgeschlagen (Exit: {copyProc.ExitCode})");
            }
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ✅", $"Archiviert: {numberedName}", "", "");
            WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "OK");
            WriteStatus("done", $"{numberedName} archiviert");
        }
        else
        {
            CPH.LogWarn($"[StreamArchiver] ❌ Datei korrupt: {latestFile.FullName}");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ⚠️", "Aufnahme korrupt! Ziehe VOD von Twitch...", "", "");
            WriteStatus("download", "Datei korrupt – lade Twitch VOD");

            bool vodDownloaded = DownloadLatestTwitchVOD(twitchDLPath, twitchChannel, targetPath);
            if (vodDownloaded)
            {
                CPH.LogInfo($"[StreamArchiver] ✅ Twitch VOD gesichert: {targetPath}");
                CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ✅", $"VOD gesichert: {numberedName}", "", "");
                WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "KORRUPT_VOD_GEZOGEN");
                WriteStatus("done", $"VOD Backup: {numberedName}");
            }
            else
            {
                CPH.LogError("[StreamArchiver] ❌ VOD Download fehlgeschlagen!");
                CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ❌", "VOD Download fehlgeschlagen!", "", "");
                WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "KORRUPT_KEIN_BACKUP");
                WriteStatus("error", "VOD Download fehlgeschlagen");
            }
        }

        return true;
    }

    private bool CheckFileWithFFmpeg(string filePath, string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = $"-v error -i \"{filePath}\" -t 10 -f null -",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var proc = Process.Start(psi))
            {
                var errorsBuilder = new StringBuilder();
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errorsBuilder.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();

                bool finished = proc.WaitForExit(30000);
                if (!finished)
                {
                    proc.Kill();
                    CPH.LogWarn("[StreamArchiver] FFmpeg Timeout");
                    return false;
                }

                string errors = errorsBuilder.ToString();
                CPH.LogInfo($"[StreamArchiver] FFmpeg ExitCode: {proc.ExitCode}");
                if (string.IsNullOrWhiteSpace(errors))
                {
                    CPH.LogInfo("[StreamArchiver] FFmpeg: Datei OK ✅");
                    return true;
                }
                else
                {
                    CPH.LogWarn($"[StreamArchiver] FFmpeg Fehler: {errors}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[StreamArchiver] FFmpeg Exception: {ex.Message}");
            return false;
        }
    }

    private int GetNextFileNumber(string targetDir)
    {
        var existingNumbers = Directory.GetFiles(targetDir, "*.mp4")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(name => {
                var match = Regex.Match(name, @"^(\d+)_");
                return match.Success ? (int?)int.Parse(match.Groups[1].Value) : null;
            })
            .Where(n => n.HasValue)
            .Select(n => n.Value)
            .ToList();

        return existingNumbers.Count > 0 ? existingNumbers.Max() + 1 : 1;
    }

    private bool DownloadLatestTwitchVOD(string twitchDLPath, string twitchChannel, string outputPath)
    {
        try
        {
            var listPsi = new ProcessStartInfo
            {
                FileName               = twitchDLPath,
                Arguments              = $"videos {twitchChannel} --limit 1 --json",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            string vodId = null;
            using (var listProc = Process.Start(listPsi))
            {
                string output = listProc.StandardOutput.ReadToEnd();
                listProc.WaitForExit();
                var match = Regex.Match(output, @"""id""\s*:\s*""?(\d+)""?");
                if (match.Success) vodId = match.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(vodId))
            {
                CPH.LogError("[StreamArchiver] Keine VOD ID gefunden.");
                return false;
            }

            CPH.LogInfo($"[StreamArchiver] VOD ID: {vodId}");

            var dlPsi = new ProcessStartInfo
            {
                FileName               = twitchDLPath,
                Arguments              = $"download {vodId} --output \"{outputPath}\" --quality source",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var dlProc = Process.Start(dlPsi))
            {
                dlProc.WaitForExit();
                bool success = dlProc.ExitCode == 0 && File.Exists(outputPath);
                CPH.LogInfo($"[StreamArchiver] twitch-dl ExitCode: {dlProc.ExitCode}");
                return success;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[StreamArchiver] Twitch DL Exception: {ex.Message}");
            return false;
        }
    }

    private void WriteCsvEntry(string csvPath, int streamNr, string gameName, string fileName, string status)
    {
        try
        {
            bool fileExists = File.Exists(csvPath);
            using (var writer = new StreamWriter(csvPath, append: true))
            {
                if (!fileExists)
                    writer.WriteLine("StreamNr,Datum,Spielname,Dateiname,Status");
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteLine($"{streamNr},{date},{gameName},{fileName},{status}");
            }
            CPH.LogInfo($"[StreamArchiver] CSV: #{streamNr} {fileName} [{status}]");
        }
        catch (Exception ex)
        {
            CPH.LogError($"[StreamArchiver] CSV Fehler: {ex.Message}");
        }
    }

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
                    lastName = last[3].Trim();
                    lastDate = last[1].Trim();
                    lastGame = last[2].Trim();
                }
            }

            string cTotal   = CPH.GetGlobalVar<string>("checker_total",    false) ?? "–";
            string cOk      = CPH.GetGlobalVar<string>("checker_ok",       false) ?? "–";
            string cMissing = CPH.GetGlobalVar<string>("checker_missing",   false) ?? "–";
            string cNoBack  = CPH.GetGlobalVar<string>("checker_nobackup",  false) ?? "–";
            string cTime    = CPH.GetGlobalVar<string>("checker_lastcheck", false) ?? "";

            string dlMB  = downloadMB >= 0 ? downloadMB.ToString() : (CPH.GetGlobalVar<string>("dl_mb",      false) ?? "0");
            string ytSt  = !string.IsNullOrEmpty(ytStatus) ? ytStatus : (CPH.GetGlobalVar<string>("yt_status", false) ?? "–");
            string ytDet = !string.IsNullOrEmpty(ytDetail) ? ytDetail : (CPH.GetGlobalVar<string>("yt_detail", false) ?? "–");

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
                string dlTotal = CPH.GetGlobalVar<string>("dl_mb_total", false) ?? "0";
                int totalMBInt = 0;
                int.TryParse(dlTotal, out totalMBInt);
                string pctStr = totalMBInt > 0 ? $"{Math.Min(99, mbInt * 100 / totalMBInt)}%" : "";
                progressHtml = $@"<div class=""progress-wrap""><div class=""progress-meta""><span>Download</span><span>{dlMB} MB {pctStr}</span></div><div class=""progress-bar""><div class=""progress-fill"" style=""width:{( totalMBInt > 0 ? Math.Min(99, mbInt * 100 / totalMBInt) : 50)}%""></div></div></div>";
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
                string st        = p.Length > 4 ? p[4].Trim() : "OK";
                string pillClass = st == "OK" ? "ok" : st.Contains("KEIN") ? "error" : "warn";
                logRows.Append($@"<div class=""log-entry""><span class=""log-time"">{p[1].Trim()}</span><span class=""log-nr"">#{p[0].Trim()}</span><span class=""log-name"">{p[3].Trim()}</span><span class=""pill {pillClass}"">{st}</span></div>");
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
            string ytPillHtml  = $@"<span class=""pill {ytPillClass}"">{ytSt}</span>";
            string now         = DateTime.Now.ToString("HH:mm:ss");
            string badgeClass  = isActive ? "live-badge active" : "live-badge";
            string badgeText   = isActive ? "AKTIV" : "IDLE";

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
