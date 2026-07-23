# StreamArchiver / StreamChecker / YoutubeUploader – Streamer.Bot Setup

Automatisches Archivieren, Prüfen, Hochladen auf YouTube und Live-Dashboard für Stream-Aufnahmen.

## Die Scripts

| Script | Pflicht? | Macht |
|---|---|---|
| `StreamArchiver.cs` | ✅ Ja | Aufnahme prüfen (FFmpeg), umbenennen, ins Archiv kopieren, Twitch-VOD-Fallback bei korrupter Datei – **und direkt im Anschluss den Archiv-Check** (dieselbe Logik wie StreamChecker, ohne Umweg über die Queue) |
| `YoutubeUploader.cs` | ✅ Ja | Lädt die neueste Twitch-VOD herunter und auf YouTube hoch |
| `StreamChecker.cs` | ⭕ Optional | Dieselbe Prüf-Logik wie in StreamArchiver eingebaut, als eigenständige Action für zusätzliche Trigger (Bot-Start, `!checkstreams`) |
| `Setup.cs` | Einmalig | Legt alle benötigten Global Variables mit Defaults an (überschreibt nie vorhandene Werte) |
| `StatusWriter.cs` | Keine Action | Reine Referenzdatei – enthält nur die `WriteStatus`-Methode zum Nachschlagen, wird nirgends selbst eingefügt |

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **FFmpeg** | https://ffmpeg.org/download.html | `ffmpeg -version` |
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" anhaken | `python --version` |

---

## Setup

### 1. `Setup.cs` einmalig ausführen

Neue Action anlegen (kein Stream-Trigger, z.B. manueller Command), Inhalt von `Setup.cs` als Execute-C#-Code rein, compilen, einmal manuell ausführen. Legt diese Global Variables an, sofern noch nicht vorhanden:

| Variable | Default | Anpassen? |
|----------|---------|-----------|
| `RecordBaseDir` | `D:\Stream` | nur falls anderer Pfad |
| `RecordsRootDir` | `D:\Stream\Records` | nur falls anderer Pfad |
| `FFmpegPath` | `C:\ffmpeg\bin\ffmpeg.exe` | ✅ ja |
| `TwitchDLPath` | `twitch-dl` | nur falls nicht im PATH |
| `TwitchChannel` | Platzhalter | ✅ ja |
| `CsvPath` | `D:\Stream\Records\streams.csv` | nur falls anderer Pfad |
| `TokenPath` | `D:\Stream\Records\.youtube_token` | nur falls anderer Pfad |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` | nur falls anderer Pfad |
| `YoutubeCsvPath` | `D:\Stream\Records\youtube_uploads.csv` | nur falls anderer Pfad |
| `YouTubeClientId` | Platzhalter | ✅ ja, siehe `README_YoutubeUploader.md` |
| `YouTubeClientSecret` | Platzhalter | ✅ ja, siehe `README_YoutubeUploader.md` |

Danach unter **Settings → Global Variables** alle mit ❗ markierten Platzhalter auf echte Werte setzen.

### 2. StreamArchiver einrichten

1. Neue Action: `StreamArchiver`
2. Trigger: **Stream Offline** (Twitch)
3. Sub-Action: Execute C# Code → Inhalt von `StreamArchiver.cs` → Compile

### 3. YoutubeUploader einrichten

Siehe `README_YoutubeUploader.md` (braucht zusätzlich einen einmaligen Google-OAuth-Schritt).

### Queue-Reihenfolge

```
Stream Offline
    1. StreamArchiver   ← archiviert UND prüft direkt im Anschluss
    2. YoutubeUploader  ← lädt VOD auf YouTube hoch
```

`StreamChecker.cs` gehört **nicht** in diese Queue – siehe `README_StreamChecker.md`.

---

## Was StreamArchiver bei jedem Lauf macht (aus dem Code)

```
Execute()
 ├─ Sperre prüfen (.locks\archiver.lock, 4h Timeout gegen verwaiste Sperren)
 │   └─ Sperre aktiv → Log-Eintrag, Abbruch, return true (kein Fehler)
 ├─ Dashboard im Browser öffnen (bei jedem Lauf, kein Cooldown)
 ├─ Spielname aus Trigger-Argument "game" lesen
 │   └─ leer → Abbruch
 ├─ Neueste, noch nicht verarbeitete MP4 in RecordBaseDir suchen
 │   (ausgeschlossen: alles was schon in der CSV als Dateiname steht,
 │    UND alles was in processed_originals.txt steht – so werden liegen
 │    gebliebene, absichtlich nicht gelöschte Quelldateien nicht erneut
 │    aufgegriffen)
 │   └─ keine gefunden → Abbruch
 ├─ FFmpeg-Check (erste 10 Sekunden, 30s Timeout)
 ├─ Datei gesund?
 │   JA  → umbenennen (NN_Spiel.mp4) im Basisordner, dann blockweise
 │         (8-MB-Blöcke) nach Records\Spiel\ kopieren, Fortschritt alle
 │         5s geloggt, CSV-Eintrag Status=OK
 │   NEIN → Original-Dateiname in processed_originals.txt eintragen
 │          (Datei selbst bleibt unangetastet liegen), Twitch-VOD als
 │          Fallback herunterladen:
 │            - VOD-ID von Twitch holen
 │            - Download; bei "Joining files failed" o.ä. (Hinweis auf
 │              beschädigten twitch-dl-Cache): Cache unter
 │              %LOCALAPPDATA%\twitch-dl\videos\<VodId>\ löschen,
 │              ein Versuch wird wiederholt
 │            - Erfolg → CSV Status=KORRUPT_VOD_GEZOGEN
 │            - Misserfolg → CSV Status=KORRUPT_KEIN_BACKUP
 ├─ Direkt im Anschluss: dieselbe Prüf-Logik wie StreamChecker
 │   (letzte 15 CSV-Einträge gegen physische Dateien), Ergebnis in
 │   checker_*-Global-Variables
 └─ Genau EIN Toast am Ende (Archiv-Ergebnis, ggf. + Check-Warnung
     mit konkreten Dateinamen dran)
```

Nichts davon löscht oder verschiebt jemals eine Original-Aufnahme oder Archiv-Datei. Einzige selbst angelegte/veränderte Dateien: `.locks\*.lock`, `processed_originals.txt`, `streams.csv`, die Dashboard-Dateien und `logs\activity_*.log`.

---

## Dateinamensschema

```
01_Spielname.mp4
02_Spielname.mp4
```
Nummer zählt pro Spiel-Unterordner automatisch hoch (höchste vorhandene Nummer + 1).

---

## CSV Format (streams.csv)

```
StreamNr,Datum,Spielname,Dateiname,Status
1,2024-03-15,Elden Ring,01_Elden Ring.mp4,OK
2,2024-03-16,Elden Ring,02_Elden Ring.mp4,KORRUPT_VOD_GEZOGEN
3,2024-03-17,Dark Souls,01_Dark Souls.mp4,KORRUPT_KEIN_BACKUP
```

| Status | Bedeutung |
|--------|-----------|
| `OK` | Aufnahme war gesund, erfolgreich archiviert |
| `KORRUPT_VOD_GEZOGEN` | Aufnahme war korrupt, Twitch-VOD als Backup gesichert |
| `KORRUPT_KEIN_BACKUP` | Aufnahme war korrupt, VOD-Download fehlgeschlagen – manuell prüfen |

---

## Dashboard

`dashboard.html` liegt immer im selben Ordner wie `CsvPath` (kein fixer Pfad – wird zur Laufzeit daraus abgeleitet). Aufbau, mehrere Dateien statt einer, damit möglichst wenig flackert:

| Datei | Inhalt | Aktualisierung |
|---|---|---|
| `dashboard.html` | Statische Shell mit drei Iframes | lädt sich selbst nie neu |
| `dashboard-step.html` | Header + aktueller Schritt | alle 5s |
| `dashboard-stats.html` | Statistiken, Checker-Zahlen, letzte Streams | alle 10s |
| `dashboard-log.html` | Gemeinsames Live-Log aller Scripts | ~alle 2s, **pausiert automatisch** solange Text markiert ist |
| `dashboard.css` | Gemeinsame Styles | bei jedem Lauf mit erzeugt |

Live-Log-Zeilen zeigen Datum, Uhrzeit und ein farbiges Badge pro Script (Archiver = lila, Checker = blau, Uploader = grün).

`StreamArchiver.cs` und `YoutubeUploader.cs` öffnen das Dashboard bei **jedem Lauf** automatisch im Standardbrowser – kein Cooldown. `Process.Start(...)` fokussiert dabei i.d.R. keinen vorhandenen Tab, sondern öffnet einen neuen – bei mehreren Läufen am Tag sammeln sich entsprechend Tabs, die man ab und zu selbst schließen muss.

---

## Log

Drei Ebenen:
1. **Streamer.bot View → Log** – alles, `[StreamArchiver]`/`[YTUploader]`/`[StreamChecker]`-Prefix.
2. **Dashboard Live-Log** – die letzten 80 Einträge, alle Scripts gemeinsam, farbig, im Speicher (nicht persistent, weg nach Streamer.bot-Neustart).
3. **`RecordsRootDir\logs\activity_yyyy-MM-dd.log`** – dieselben Einträge dauerhaft auf Platte, eine Datei pro Kalendertag, wächst unbegrenzt mit.

---

## Troubleshooting

**"Kein Spielname gefunden"**
→ `game`-Trigger-Argument kommt leer an – passiert typischerweise bei manuellem Testen ohne echten Stream-Offline-Trigger

**"FFmpeg Exception" / Timeout**
→ `FFmpegPath` in Global Variables prüfen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` prüfen, testen mit `twitch-dl videos DeinKanalname --limit 1 --json`

**Download/VOD-Backup bricht mit "Joining files failed" o.ä. ab**
→ Wird automatisch erkannt: twitch-dl-Cache für die betroffene VOD wird geleert, ein Versuch automatisch wiederholt. Bleibt es bestehen: `%LOCALAPPDATA%\twitch-dl\videos\<VodId>\` manuell prüfen

**Script läuft nach einem Absturz nicht mehr an ("läuft bereits eine Instanz")**
→ Sperr-Datei unter `RecordsRootDir\.locks\` löschen (löst sich sonst nach 4h von selbst)

**Dashboard öffnet sich nicht automatisch**
→ Kein Cooldown mehr, sollte bei jedem Lauf einen Tab öffnen. Prüfen, ob `dashboard.html` überhaupt existiert (wird von `WriteStatus` angelegt) und ob im Log `[StreamArchiver] Konnte Dashboard nicht öffnen: ...` steht.
