using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Generic;

public class CPHInline
{
    public bool Execute()
    {
        // ── Konfiguration aus Streamer.Bot Global Variables ────────
        string recordsRootDir = CPH.GetGlobalVar<string>("RecordsRootDir", true);
        string csvPath        = CPH.GetGlobalVar<string>("CsvPath",        true);
        // ──────────────────────────────────────────────────────────

        CPH.LogInfo("[StreamChecker] ── Check gestartet ──────────────────────");
        AppendLog("StreamChecker", "🔎 Check gestartet...");
        WriteStatus("checker", "Überprüfe Archiv...");
        OpenDashboardOnce();

        if (!File.Exists(csvPath))
        {
            CPH.LogWarn("[StreamChecker] Keine streams.csv gefunden – noch keine Streams archiviert.");
            return true;
        }

        var lines = File.ReadAllLines(csvPath)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Reverse()
            .Take(15)
            .Reverse()
            .ToList();

        if (lines.Count == 0)
        {
            CPH.LogWarn("[StreamChecker] CSV ist leer – noch keine Einträge.");
            return true;
        }

        int total    = 0;
        int ok       = 0;
        int missing  = 0;
        int corrupt  = 0;
        int noBackup = 0;

        var missingFiles = new List<string>();  // ausführlich, fürs Log
        var problemShort = new List<string>();  // kurz, für Toast + Dashboard-Summary

        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length < 5) continue;

            string streamNr = parts[0].Trim();
            string date     = parts[1].Trim();
            string gameName = parts[2].Trim();
            string fileName = parts[3].Trim();
            string status   = parts[4].Trim();

            total++;

            string expectedPath = Path.Combine(recordsRootDir, gameName, fileName);
            bool fileExists = File.Exists(expectedPath);

            if (status == "KORRUPT_KEIN_BACKUP")
            {
                noBackup++;
                problemShort.Add($"#{streamNr} {fileName} (kein Backup)");
                CPH.LogWarn($"[StreamChecker] ⚠️  #{streamNr} [{date}] {fileName} – KORRUPT, kein Backup vorhanden!");
            }
            else if (!fileExists)
            {
                missing++;
                missingFiles.Add($"#{streamNr} [{date}] {fileName}");
                problemShort.Add($"#{streamNr} {fileName} (fehlt)");
                CPH.LogWarn($"[StreamChecker] ❌ #{streamNr} [{date}] {fileName} – DATEI FEHLT! (Erwartet: {expectedPath})");
            }
            else if (status == "KORRUPT_VOD_GEZOGEN")
            {
                corrupt++;
                CPH.LogInfo($"[StreamChecker] ⚠️  #{streamNr} [{date}] {fileName} – VOD Backup (war korrupt, Datei OK)");
            }
            else
            {
                ok++;
                CPH.LogInfo($"[StreamChecker] ✅ #{streamNr} [{date}] {fileName} – OK");
            }
        }

        CPH.LogInfo("[StreamChecker] ── Zusammenfassung ─────────────────────");
        CPH.LogInfo($"[StreamChecker] Gesamt:      {total}");
        CPH.LogInfo($"[StreamChecker] OK:          {ok}");
        CPH.LogInfo($"[StreamChecker] VOD Backup:  {corrupt}");
        CPH.LogInfo($"[StreamChecker] Fehlend:     {missing}");
        CPH.LogInfo($"[StreamChecker] Kein Backup: {noBackup}");

        // Checker Ergebnis in Global Variables für Dashboard speichern
        CPH.SetGlobalVar("checker_total",     total.ToString(),   false);
        CPH.SetGlobalVar("checker_ok",        ok.ToString(),      false);
        CPH.SetGlobalVar("checker_missing",   missing.ToString(), false);
        CPH.SetGlobalVar("checker_nobackup",  noBackup.ToString(),false);
        CPH.SetGlobalVar("checker_lastcheck", DateTime.UtcNow.ToString("O"), false);

        // Läuft StreamArchiver gerade noch (z.B. VOD-Backup-Download nach korrupter Aufnahme)?
        // Ohne diesen Hinweis wirkt "kein Backup" wie ein endgültiges Ergebnis, obwohl gerade
        // noch an genau diesem Backup gearbeitet wird.
        string archiverLockPath = Path.Combine(recordsRootDir, ".locks", "archiver.lock");
        bool archiverBusy = File.Exists(archiverLockPath);
        string busyNote = archiverBusy
            ? " (StreamArchiver läuft aktuell noch – falls das gerade den betroffenen Stream betrifft, gleich nochmal prüfen)"
            : "";

        if (missing > 0 || noBackup > 0)
        {
            string fileList = string.Join(", ", problemShort.Take(3));
            if (problemShort.Count > 3)
                fileList += $" +{problemShort.Count - 3} weitere";
            string summary = $"{missing} fehlend, {noBackup} ohne Backup: {fileList}{busyNote}";
            CPH.LogWarn($"[StreamChecker] ⚠️  Handlungsbedarf! {summary}");
            foreach (var f in missingFiles)
                CPH.LogWarn($"[StreamChecker]    → {f}");

            CPH.ShowToastNotification("StreamChecker", "StreamChecker ⚠️", summary, "", "");
            AppendLog("StreamChecker", $"⚠️ {summary}");
            if (archiverBusy)
                AppendLog("StreamChecker", "⏳ Hinweis: StreamArchiver läuft parallel noch – Ergebnis kann veraltet sein.");
            foreach (var f in missingFiles)
                AppendLog("StreamChecker", $"❌ Fehlt: {f}");
            WriteStatus("idle", summary);
        }
        else
        {
            CPH.LogInfo("[StreamChecker] ✅ Alle Streams vorhanden!");
            CPH.ShowToastNotification("StreamChecker", "StreamChecker ✅", $"Alle {total} Streams vorhanden.", "", "");
            AppendLog("StreamChecker", $"✅ Alle {total} Streams vorhanden.");
            WriteStatus("idle", $"Alle {total} Streams OK");
        }

        CPH.LogInfo("[StreamChecker] ── Check abgeschlossen ─────────────────");
        return true;
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────

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
                CPH.LogInfo($"[StreamChecker] Dashboard im Browser geöffnet: {dashboardPath}");
            }
            else
            {
                CPH.LogInfo($"[StreamChecker] Dashboard-Datei noch nicht vorhanden ({dashboardPath}) – wird gleich von WriteStatus angelegt.");
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[StreamChecker] Konnte Dashboard nicht öffnen: {ex.Message}");
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
