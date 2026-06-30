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
        // ── Konfiguration aus Streamer.Bot Global Variables ────────
        string recordBaseDir  = CPH.GetGlobalVar<string>("RecordBaseDir",  true);
        string recordsRootDir = CPH.GetGlobalVar<string>("RecordsRootDir", true);
        string ffmpegPath     = CPH.GetGlobalVar<string>("FFmpegPath",     true);
        string twitchDLPath   = CPH.GetGlobalVar<string>("TwitchDLPath",   true);
        string twitchChannel  = CPH.GetGlobalVar<string>("TwitchChannel",  true);
        string csvPath        = CPH.GetGlobalVar<string>("CsvPath",        true);
        // ──────────────────────────────────────────────────────────

        // 1. Spielname vom Stream Offline Trigger
        CPH.TryGetArg("gameName", out string gameName);

        if (string.IsNullOrWhiteSpace(gameName))
        {
            CPH.LogWarn("[StreamArchiver] Kein Spielname gefunden – Abbruch.");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver", "❌ Kein Spielname gefunden – Abbruch.", "", "");
            return false;
        }

        string safeGameName = Regex.Replace(gameName, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[StreamArchiver] Spielname: {safeGameName}");

        // 2. Neueste MP4 in RecordBaseDir finden
        var mp4Files = Directory.GetFiles(recordBaseDir, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .ToList();

        if (mp4Files.Count == 0)
        {
            CPH.LogWarn($"[StreamArchiver] Keine MP4 Datei in {recordBaseDir} gefunden.");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver", $"❌ Keine MP4 Datei in {recordBaseDir} gefunden.", "", "");
            return false;
        }

        FileInfo latestFile = mp4Files[0];
        CPH.LogInfo($"[StreamArchiver] Gefundene Datei: {latestFile.FullName}");

        // 3. FFmpeg Check (nur erste 10 Sekunden)
        CPH.LogInfo("[StreamArchiver] Starte FFmpeg Check...");
        bool fileIsHealthy = CheckFileWithFFmpeg(latestFile.FullName, ffmpegPath);
        CPH.LogInfo($"[StreamArchiver] FFmpeg Check fertig: {fileIsHealthy}");

        // 4. Zielordner vorbereiten
        string targetDir = Path.Combine(recordsRootDir, safeGameName);
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            CPH.LogInfo($"[StreamArchiver] Ordner angelegt: {targetDir}");
        }

        // 5. Nächste Nummer ermitteln
        int nextNumber      = GetNextFileNumber(targetDir);
        string numberedName = $"{nextNumber:D2}_{safeGameName}.mp4";
        string targetPath   = Path.Combine(targetDir, numberedName);

        if (fileIsHealthy)
        {
            // Original umbenennen im Root, dann Kopie in Records
            string renamedSourcePath = Path.Combine(recordBaseDir, numberedName);
            File.Move(latestFile.FullName, renamedSourcePath);
            CPH.LogInfo($"[StreamArchiver] ✅ Original umbenannt: {renamedSourcePath}");

            File.Copy(renamedSourcePath, targetPath);
            CPH.LogInfo($"[StreamArchiver] ✅ Kopie erstellt: {targetPath}");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ✅", $"Stream archiviert: {numberedName}", "", "");
            WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "OK");
        }
        else
        {
            CPH.LogWarn($"[StreamArchiver] ❌ Datei korrupt: {latestFile.FullName}");
            CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ⚠️", "Aufnahme korrupt! Ziehe VOD von Twitch als Fallback...", "", "");

            bool vodDownloaded = DownloadLatestTwitchVOD(twitchDLPath, twitchChannel, targetPath);

            if (vodDownloaded)
            {
                CPH.LogInfo($"[StreamArchiver] ✅ Twitch VOD gesichert: {targetPath}");
                CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ✅", $"Twitch VOD gesichert: {numberedName}", "", "");
                WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "KORRUPT_VOD_GEZOGEN");
            }
            else
            {
                CPH.LogError("[StreamArchiver] ❌ Twitch VOD Download fehlgeschlagen!");
                CPH.ShowToastNotification("StreamArchiver", "StreamArchiver ❌", "VOD Download fehlgeschlagen – bitte manuell prüfen!", "", "");
                WriteCsvEntry(csvPath, nextNumber, safeGameName, numberedName, "KORRUPT_KEIN_BACKUP");
            }
        }

        return true;
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────

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
                    CPH.LogWarn("[StreamArchiver] FFmpeg Timeout – Prozess gekillt");
                    return false;
                }

                string errors = errorsBuilder.ToString();
                CPH.LogInfo($"[StreamArchiver] FFmpeg ExitCode: {proc.ExitCode}");

                if (string.IsNullOrWhiteSpace(errors))
                {
                    CPH.LogInfo("[StreamArchiver] FFmpeg: Datei ist in Ordnung ✅");
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
            .Select(name =>
            {
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
                if (match.Success)
                    vodId = match.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(vodId))
            {
                CPH.LogError("[StreamArchiver] Konnte keine VOD ID von Twitch holen.");
                return false;
            }

            CPH.LogInfo($"[StreamArchiver] Twitch VOD ID: {vodId}");

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
            CPH.LogInfo($"[StreamArchiver] CSV Eintrag geschrieben: #{streamNr} {fileName} [{status}]");
        }
        catch (Exception ex)
        {
            CPH.LogError($"[StreamArchiver] CSV Fehler: {ex.Message}");
        }
    }
}