using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Globalization;

public class CPHInline
{
    // ── Konfiguration ──────────────────────────────────────────────
    private const string RecordBaseDir  = @"D:\Stream";          // Wo OBS aufnimmt
    private const string RecordsRootDir = @"D:\Stream\Records";  // Zielordner
    private const string FFmpegPath     = @"C:\ffmpeg\bin\ffmpeg.exe"; // Pfad zu ffmpeg.exe
    private const string TwitchDLPath   = @"G:\Twitch DL Tool\twitch-dl.exe"; // Pfad zu twitch-dl
    private const string CsvPath        = @"D:\Stream\Records\streams.csv";
    // ──────────────────────────────────────────────────────────────

    public bool Execute()
    {
        // 1. Spielname von Streamer.Bot holen
        CPH.TryGetArg("gameName", out string gameName);

        if (string.IsNullOrWhiteSpace(gameName))
        {
            CPH.LogWarn("[StreamArchiver] Kein Spielname gefunden – Abbruch.");
            return false;
        }

        // Spielname für Dateisystem bereinigen (Sonderzeichen entfernen)
        string safeGameName = Regex.Replace(gameName, @"[\\/:*?""<>|]", "").Trim();
        CPH.LogInfo($"[StreamArchiver] Spielname: {safeGameName}");

        // 2. Neueste MP4 in D:\Stream finden (nur direkt im Ordner, nicht in Unterordnern)
        var mp4Files = Directory.GetFiles(RecordBaseDir, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .ToList();

        if (mp4Files.Count == 0)
        {
            CPH.LogWarn("[StreamArchiver] Keine MP4 Datei in D:\\Stream gefunden.");
            return false;
        }

        FileInfo latestFile = mp4Files[0];
        CPH.LogInfo($"[StreamArchiver] Gefundene Datei: {latestFile.FullName}");

        // 3. FFmpeg Check
        bool fileIsHealthy = CheckFileWithFFmpeg(latestFile.FullName);

        // 4. Zielordner vorbereiten
        string targetDir = Path.Combine(RecordsRootDir, safeGameName);
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            CPH.LogInfo($"[StreamArchiver] Ordner angelegt: {targetDir}");
        }

        // 5. Nächste Nummer ermitteln
        int nextNumber = GetNextFileNumber(targetDir, safeGameName);
        string numberedName = $"{nextNumber:D2}_{safeGameName}.mp4";
        string targetPath   = Path.Combine(targetDir, numberedName);

        if (fileIsHealthy)
        {
            // Original im Root Verzeichnis umbenennen
            string renamedSourcePath = Path.Combine(RecordBaseDir, numberedName);
            File.Move(latestFile.FullName, renamedSourcePath);
            CPH.LogInfo($"[StreamArchiver] ✅ Original umbenannt zu: {renamedSourcePath}");

            // Kopie in den Zielordner
            File.Copy(renamedSourcePath, targetPath);
            CPH.LogInfo($"[StreamArchiver] ✅ Kopie erstellt: {targetPath}");
            CPH.SendMessage($"✅ Stream archiviert: {numberedName} (Original in D:\\Stream, Kopie in Records)", true);
            WriteCsvEntry(nextNumber, safeGameName, numberedName, "OK");
        }
        else
        {
            // Datei ist korrupt → von Twitch ziehen
            CPH.LogWarn($"[StreamArchiver] ❌ Datei korrupt: {latestFile.FullName}");
            CPH.SendMessage($"⚠️ Aufnahme korrupt! Ziehe VOD von Twitch als Fallback...", true);

            bool vodDownloaded = DownloadLatestTwitchVOD(targetPath);

            if (vodDownloaded)
            {
                CPH.LogInfo($"[StreamArchiver] ✅ Twitch VOD gesichert: {targetPath}");
                CPH.SendMessage($"✅ Twitch VOD gesichert: {numberedName}", true);
                WriteCsvEntry(nextNumber, safeGameName, numberedName, "KORRUPT_VOD_GEZOGEN");
            }
            else
            {
                CPH.LogError("[StreamArchiver] ❌ Twitch VOD Download fehlgeschlagen!");
                CPH.SendMessage("❌ VOD Download von Twitch fehlgeschlagen – bitte manuell prüfen!", true);
                WriteCsvEntry(nextNumber, safeGameName, numberedName, "KORRUPT_KEIN_BACKUP");
            }
        }

        return true;
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────

    private bool CheckFileWithFFmpeg(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = FFmpegPath,
                Arguments              = $"-v error -i \"{filePath}\" -f null -",
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var proc = Process.Start(psi))
            {
                string errors = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

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

    private int GetNextFileNumber(string targetDir, string gameName)
    {
        // Dateien im Ordner suchen die mit einer Zahl + _ anfangen
        var existingFiles = Directory.GetFiles(targetDir, "*.mp4")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(name =>
            {
                // Zahl vor dem ersten _ extrahieren
                var match = Regex.Match(name, @"^(\d+)_");
                return match.Success ? (int?)int.Parse(match.Groups[1].Value) : null;
            })
            .Where(n => n.HasValue)
            .Select(n => n.Value)
            .ToList();

        return existingFiles.Count > 0 ? existingFiles.Max() + 1 : 1;
    }

    private bool DownloadLatestTwitchVOD(string outputPath)
    {
        try
        {
            // twitch-dl braucht zuerst die VOD ID – wir holen die neueste VOD des eigenen Kanals
            // Dafür rufen wir twitch-dl videos auf um die letzte VOD ID zu finden
            var listPsi = new ProcessStartInfo
            {
                FileName              = TwitchDLPath,
                Arguments             = "videos --limit 1 --json",
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

                // Einfache JSON-Extraktion der ersten VOD ID
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

            // VOD downloaden
            string outputDir  = Path.GetDirectoryName(outputPath);
            string outputName = Path.GetFileNameWithoutExtension(outputPath);

            var dlPsi = new ProcessStartInfo
            {
                FileName              = TwitchDLPath,
                Arguments             = $"download {vodId} --output \"{outputPath}\" --quality source",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using (var dlProc = Process.Start(dlPsi))
            {
                dlProc.WaitForExit();
                return dlProc.ExitCode == 0 && File.Exists(outputPath);
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[StreamArchiver] Twitch DL Exception: {ex.Message}");
            return false;
        }
    }

    private void WriteCsvEntry(int streamNr, string gameName, string fileName, string status)
    {
        try
        {
            bool fileExists = File.Exists(CsvPath);
            using (var writer = new StreamWriter(CsvPath, append: true))
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
