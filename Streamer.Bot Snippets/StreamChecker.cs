using System;
using System.IO;
using System.Linq;
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

        if (!File.Exists(csvPath))
        {
            CPH.LogWarn("[StreamChecker] Keine streams.csv gefunden – noch keine Streams archiviert.");
            return true;
        }

        var lines = File.ReadAllLines(csvPath)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
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

        var missingFiles = new List<string>();

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
                CPH.LogWarn($"[StreamChecker] ⚠️  #{streamNr} [{date}] {fileName} – KORRUPT, kein Backup vorhanden!");
            }
            else if (!fileExists)
            {
                missing++;
                missingFiles.Add($"#{streamNr} [{date}] {fileName}");
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

        if (missing > 0 || noBackup > 0)
        {
            CPH.LogWarn("[StreamChecker] ⚠️  Handlungsbedarf! Folgende Streams fehlen oder haben kein Backup:");
            foreach (var f in missingFiles)
                CPH.LogWarn($"[StreamChecker]    → {f}");
        }
        else
        {
            CPH.LogInfo("[StreamChecker] ✅ Alle Streams vorhanden!");
        }

        CPH.LogInfo("[StreamChecker] ── Check abgeschlossen ─────────────────");
        return true;
    }
}