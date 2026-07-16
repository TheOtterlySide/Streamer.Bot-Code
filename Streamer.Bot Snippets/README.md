# StreamArchiver – Streamer.Bot Setup

Automatisches Archivieren, Prüfen und Umbenennen von Stream-Aufnahmen nach dem Stream.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **FFmpeg** | https://ffmpeg.org/download.html → "Add to PATH" | `ffmpeg -version` |
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" | `python --version` |

---

## Ordnerstruktur

```
D:\Stream\                          ← OBS Aufnahmen landen hier (MP4)
D:\Stream\Records\                  ← Archiv Zielordner
D:\Stream\Records\Spielname\        ← Pro Spiel ein Unterordner
D:\Stream\Records\streams.csv       ← Tracking CSV (StreamArchiver + YoutubeUploader)
D:\Stream\Records\dashboard.html    ← Live Dashboard (wird automatisch generiert)
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
| `TokenPath` | `D:\Stream\Records\.youtube_token` |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` |
| `YouTubeClientId` | `deine-client-id.apps.googleusercontent.com` |
| `YouTubeClientSecret` | `GOCSPX-...` |

---

## Scripts einrichten

### StreamArchiver.cs
1. Neue Action anlegen: `StreamArchiver`
2. Sub-Action 1: **Twitch → Get Channel Info For Target** (Broadcaster)
3. Sub-Action 2: **Execute C# Code** → `StreamArchiver.cs` reinkopieren → **Compile**
4. Trigger: **Stream Offline** (Twitch)

### StreamChecker.cs
1. Neue Action anlegen: `StreamChecker`
2. Sub-Action: **Execute C# Code** → `StreamChecker.cs` reinkopieren → **Compile**
3. Trigger 1: **Stream Offline** → nach StreamArchiver in der Queue
4. Trigger 2: **Streamer.Bot Started**
5. Trigger 3: Chat Command `!checkstreams` (optional)

### YoutubeUploader.cs
1. Neue Action anlegen: `YoutubeUploader`
2. Sub-Action 1: **Twitch → Get Channel Info For Target** (Broadcaster)
3. Sub-Action 2: **Execute C# Code** → `YoutubeUploader.cs` reinkopieren → **Compile**
4. Trigger: **Stream Offline** → nach StreamChecker in der Queue

---

## Action Queue Reihenfolge

```
Stream Offline
    1. StreamArchiver   ← Aufnahme prüfen, umbenennen, kopieren
    2. StreamChecker    ← CSV gegen Dateien prüfen
    3. YoutubeUploader  ← VOD auf YouTube hochladen
```

---

## Ablauf nach jedem Stream

```
Stream endet
    → Get Channel Info (Spielname, Titel)
    → Neueste MP4 in D:\Stream finden
    → FFmpeg Check (erste 10 Sekunden)
    → Datei OK?
        JA  → Umbenennen + Kopie nach Records
        NEIN → Twitch VOD als Backup downloaden
    → CSV Eintrag schreiben
    → StreamChecker: CSV gegen Dateien prüfen
    → YoutubeUploader: VOD downloaden + auf YouTube hochladen
    → Dashboard aktualisieren
```

---

## Dateinamensschema

```
01_Spielname.mp4
02_Spielname.mp4
03_Spielname.mp4
```

Nummer zählt pro Spiel-Ordner hoch, wird automatisch ermittelt.

---

## CSV Format (streams.csv)

```
StreamNr,Datum,Spielname,Dateiname,Status
1,2024-03-15,Elden Ring,01_Elden Ring.mp4,OK
2,2024-03-16,Elden Ring,02_Elden Ring.mp4,KORRUPT_VOD_GEZOGEN
```

| Status | Bedeutung |
|--------|-----------|
| `OK` | Datei gesund, erfolgreich archiviert |
| `KORRUPT_VOD_GEZOGEN` | Datei korrupt, Twitch VOD als Backup |
| `KORRUPT_KEIN_BACKUP` | Datei korrupt, VOD Download fehlgeschlagen |

---

## Dashboard

`D:\Stream\Records\dashboard.html` wird automatisch vom Script generiert und aktualisiert sich alle 3 Sekunden per Meta-Refresh. Einfach im Browser öffnen.

---

## Troubleshooting

**"Kein Spielname gefunden"**
→ Get Channel Info Sub-Action fehlt oder ist falsch konfiguriert

**"FFmpeg Exception: file not found"**
→ `FFmpegPath` in Global Variables prüfen

**"Konnte keine VOD ID holen"**
→ `TwitchChannel` prüfen → `twitch-dl videos DeinKanal --limit 1 --json` im CMD testen
→ VODs auf Twitch aktivieren: Einstellungen → Kanal → "VODs speichern"

**StreamChecker findet Dateien nicht**
→ `RecordsRootDir` in Global Variables prüfen
