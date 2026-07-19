// ────────────────────────────────────────────────────────────────
// StreamArchiver / StreamChecker / YoutubeUploader – Setup
// ────────────────────────────────────────────────────────────────
// Einmalig manuell ausführen (z.B. über einen eigenen Button/Command
// in Streamer.bot, NICHT über den "Stream Offline"-Trigger).
//
// Legt alle Global Variables an, die die drei Scripts brauchen –
// ABER NUR, wenn sie noch nicht existieren. Bereits gesetzte Werte
// werden nie überschrieben, das Script ist also gefahrlos mehrfach
// ausführbar (z.B. nach einem Update, das eine neue Variable braucht).
//
// Nach dem Ausführen: Log/Konsole prüfen und alle als "❗ BITTE ANPASSEN"
// markierten Werte in Streamer.bot unter Settings → Global Variables
// auf deine echten Werte setzen (Twitch-Kanal, YouTube Client-ID/Secret,
// eigene Pfade falls D:\Stream bei dir nicht passt).
// ────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

public class CPHInline
{
    public bool Execute()
    {
        CPH.LogInfo("[Setup] ── Setup gestartet ─────────────────────────");

        // (Name, Default-Wert, Muss der Nutzer zwingend anpassen?)
        var defaults = new List<(string Name, string Value, bool NeedsAttention)>
        {
            ("RecordBaseDir",       @"D:\Stream",                              false),
            ("RecordsRootDir",      @"D:\Stream\Records",                      false),
            ("FFmpegPath",          @"C:\ffmpeg\bin\ffmpeg.exe",                true),
            ("TwitchDLPath",        "twitch-dl",                               false),
            ("TwitchChannel",       "DEIN_TWITCH_KANALNAME",                   true),
            ("CsvPath",             @"D:\Stream\Records\streams.csv",          false),
            ("TokenPath",           @"D:\Stream\Records\.youtube_token",       false),
            ("TempDownloadDir",     @"D:\Stream\YoutubeQueue",                  false),
            ("YoutubeCsvPath",      @"D:\Stream\Records\youtube_uploads.csv",  false),
            ("YouTubeClientId",     "DEINE_CLIENT_ID.apps.googleusercontent.com", true),
            ("YouTubeClientSecret", "GOCSPX-DEIN-SECRET",                      true),
        };

        int created = 0, skipped = 0;
        var needsAttention = new List<string>();

        foreach (var (name, value, attention) in defaults)
        {
            string existing = CPH.GetGlobalVar<string>(name, true);

            if (!string.IsNullOrWhiteSpace(existing))
            {
                CPH.LogInfo($"[Setup] ✓ {name} existiert bereits – wird nicht verändert.");
                skipped++;
                continue;
            }

            CPH.SetGlobalVar(name, value, true);
            CPH.LogInfo($"[Setup] + {name} angelegt = \"{value}\"");
            created++;

            if (attention)
                needsAttention.Add(name);
        }

        CPH.LogInfo("[Setup] ── Zusammenfassung ───────────────────────────");
        CPH.LogInfo($"[Setup] {created} neu angelegt, {skipped} bereits vorhanden.");

        if (needsAttention.Count > 0)
        {
            CPH.LogWarn("[Setup] ❗ Folgende Werte sind nur Platzhalter und müssen unter " +
                        "Settings → Global Variables noch angepasst werden:");
            foreach (var name in needsAttention)
                CPH.LogWarn($"[Setup]    → {name}");

            CPH.ShowToastNotification("Setup", "Setup ⚠️",
                $"{needsAttention.Count} Werte müssen noch angepasst werden (siehe Log).", "", "");
        }
        else
        {
            CPH.ShowToastNotification("Setup", "Setup ✅",
                "Alle Global Variables sind vorhanden.", "", "");
        }

        CPH.LogInfo("[Setup] ── Setup abgeschlossen ───────────────────────");
        return true;
    }
}