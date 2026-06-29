# StreamArchiver – Streamer.Bot Setup

Automatisches Archivieren, Prüfen und Umbenennen von Stream-Aufnahmen nach dem Stream.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **FFmpeg** | https://ffmpeg.org/download.html | `ffmpeg -version` |
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" anhaken | `python --version` |

---

## Ordnerstruktur

```
D:\Stream\                          ← OBS Aufnahmen landen hier (MP4)
D:\Stream\Records\                  ← Archiv Zielordner
D:\Stream\Records\Spielname\        ← Pro Spiel ein Unterordner
D:\Stream\Records\streams.csv       ← Tracking CSV
```

---

## Global Variables in Streamer.Bot

**Settings → Global Variables** – folgende Einträge anlegen:

| Variable | Wert |
|----------|------|
| `RecordBaseDir` | `D:\Stream` |
| `RecordsRootDir` | `D:\Stream\Records` |
| `FFmpegPath` | `C:\ffmpeg\bin\ffmpeg.exe` |
| `TwitchDLPath` | `twitch-dl` |
| `TwitchChannel` | `DeinTwitchKanalname` |
| `CsvPath` | `D:\Stream\Records\streams.csv` |

---

## Scripts einrichten

### StreamArchiver.cs
Archiviert die Aufnahme nach dem Stream.

1. Neue Action anlegen: `StreamArchiver`
2. Trigger: **Stream Offline** (Twitch)
3. Sub-Action: **Execute C# Code** → Inhalt von `StreamArchiver.cs` reinkopieren → **Compile**

### StreamChecker.cs
Prüft ob alle Streams physisch vorhanden sind.

1. Neue Action anlegen: `StreamChecker`
2. Trigger 1: **Stream Offline** (Twitch) → nach StreamArchiver in der Queue
3. Trigger 2: **Streamer.Bot Started**
4. Trigger 3: Chat Command `!checkstreams` (optional)
5. Sub-Action: **Execute C# Code** → Inhalt von `StreamChecker.cs` reinkopieren → **Compile**

---

## Ablauf nach jedem Stream

```
Stream endet (Twitch)
    → Streamer.Bot: Stream Offline Event
    → StreamArchiver startet
        → Neueste MP4 in D:\Stream finden
        → FFmpeg Check (erste 10 Sekunden)
        → Datei OK?
            JA  → Umbenennen (01_Spielname.mp4) + Kopie nach Records
            NEIN → Twitch VOD herunterladen als Fallback
        → CSV Eintrag schreiben
    → StreamChecker startet
        → CSV gegen physische Dateien prüfen
        → Fehlende oder problematische Einträge ins Log schreiben
```

---

## Dateinamensschema

```
01_Spielname.mp4
02_Spielname.mp4
03_Spielname.mp4
...
```

Die Nummer zählt pro Spiel-Ordner hoch. Wird automatisch ermittelt.

---

## CSV Format

```
StreamNr,Datum,Spielname,Dateiname,Status
1,2024-03-15,Elden Ring,01_Elden Ring.mp4,OK
2,2024-03-16,Elden Ring,02_Elden Ring.mp4,KORRUPT_VOD_GEZOGEN
3,2024-03-17,Dark Souls,01_Dark Souls.mp4,KORRUPT_KEIN_BACKUP
```

### Status Werte
| Status | Bedeutung |
|--------|-----------|
| `OK` | Datei war gesund, erfolgreich archiviert |
| `KORRUPT_VOD_GEZOGEN` | Datei war korrupt, Twitch VOD als Backup gesichert |
| `KORRUPT_KEIN_BACKUP` | Datei war korrupt, Twitch VOD Download fehlgeschlagen |

---

## Log prüfen

**View → Log** in Streamer.Bot. Alle Einträge beginnen mit `[StreamArchiver]` oder `[StreamChecker]`.

---

## Troubleshooting

**"Kein Spielname gefunden"**
→ Passiert beim manuellen Test – `gameName` kommt nur vom echten Stream Offline Trigger

**"FFmpeg Exception: The system cannot find the file"**
→ `FFmpegPath` in Global Variables prüfen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` in Global Variables prüfen
→ Testen: `twitch-dl videos DeinKanalname --limit 1 --json` im CMD

**"The process cannot access the file"**
→ FFmpeg oder ein anderer Prozess hat die Datei noch offen – kurz warten und nochmal triggern
