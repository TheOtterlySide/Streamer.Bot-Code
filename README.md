# StreamArchiver / StreamChecker / YoutubeUploader – Streamer.Bot Setup

Automatisches Archivieren, Prüfen, Hochladen auf YouTube und Live-Dashboard für Stream-Aufnahmen.

Zwei Scripts fürs Standard-Setup, ein optionales Extra:

| Script | Macht | Nötig? |
|---|---|---|
| `StreamArchiver.cs` | Aufnahme prüfen (FFmpeg), umbenennen, ins Archiv kopieren, Twitch-VOD-Fallback bei korrupter Datei – **und direkt im Anschluss den Check** (siehe unten) | ✅ Ja |
| `YoutubeUploader.cs` | Lädt den Twitch-VOD herunter und auf YouTube hoch | ✅ Ja |
| `StreamChecker.cs` | Dieselbe Prüf-Logik wie in StreamArchiver eingebaut, aber als **eigenständige Action** für zusätzliche Trigger (Bot-Start, manuelles `!checkstreams`) | ⭕ Optional |

**Warum zwei Stellen mit derselben Prüf-Logik?** `StreamArchiver.cs` prüft ab sofort direkt nach dem Archivieren, ohne Umweg über die Streamer.bot-Queue – kein zusätzlicher Trigger, keine Sperr-Datei-Krücke nötig, weil beides in derselben Ausführung läuft. Wer aber *zusätzlich* beim Bot-Start oder per Chat-Command prüfen möchte (z.B. um nach einem Neustart sofort den Stand zu sehen, ohne auf den nächsten Stream zu warten), richtet `StreamChecker.cs` separat mit genau diesen Triggern ein. Hängt `StreamChecker.cs` an keinem Trigger, passiert schlicht nichts – kostet nichts, kann aber auch komplett gelöscht werden, wenn du wie die meisten nur den Stream-Offline-Trigger nutzt.

Details zu StreamChecker (falls gewünscht) und YoutubeUploader stehen in eigenen READMEs (`README_StreamChecker.md`, `README_YoutubeUploader.md`). Dieses Dokument deckt das Gesamtsystem, Setup und StreamArchiver ab.

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
D:\Stream\                                  ← OBS Aufnahmen landen hier (MP4)
D:\Stream\Records\                          ← Archiv-Zielordner
D:\Stream\Records\Spielname\                ← Pro Spiel ein Unterordner
D:\Stream\Records\streams.csv               ← Tracking CSV
D:\Stream\Records\processed_originals.txt   ← Interne Merkliste (siehe unten), reiner Text
D:\Stream\Records\.locks\                   ← Interne Sperr-Dateien gegen Doppel-Trigger
D:\Stream\Records\dashboard.html            ← Dashboard-Einstiegspunkt (siehe Dashboard-Abschnitt)
```

**Wichtig:** Keines der Scripts löscht oder verschiebt jemals eine Aufnahme oder Original-Videodatei. Die einzigen Dateien, die die Scripts selbst anlegen/löschen, sind die o.g. internen Hilfsdateien (Textdatei, Sperr-Marker, Dashboard-HTML) sowie – bei YoutubeUploader – twitch-dls eigener Download-Zwischenspeicher unter `%LOCALAPPDATA%\twitch-dl\videos\<VodId>\`.

---

## Setup – einmalig, in dieser Reihenfolge

### 1. `Setup.cs` ausführen

Legt alle benötigten Global Variables mit sinnvollen Defaults an – **überschreibt nie** bereits vorhandene Werte, ist also auch nach künftigen Updates gefahrlos erneut ausführbar.

1. Neue Action anlegen, z.B. `Setup` (kein Trigger, oder ein manueller Command wie `!setup`)
2. Sub-Action: **Execute C# Code** → Inhalt von `Setup.cs` reinkopieren → **Compile** → einmal manuell ausführen
3. Log prüfen: alle mit ❗ markierten Werte (Twitch-Kanal, FFmpeg-Pfad, YouTube Client-ID/Secret) unter **Settings → Global Variables** auf deine echten Werte anpassen

Danach angelegte/erwartete Variablen im Überblick:

| Variable | Default | Anpassen nötig? |
|----------|---------|------|
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

### 2. Scripts einrichten

**StreamArchiver.cs**
1. Neue Action anlegen: `StreamArchiver`
2. Trigger: **Stream Offline** (Twitch)
3. Sub-Action: **Execute C# Code** → Inhalt von `StreamArchiver.cs` reinkopieren → **Compile**

**YoutubeUploader.cs**: siehe eigenes README. Queue-Reihenfolge beim Stream-Offline-Trigger im Standard-Setup:

```
1. StreamArchiver   ← Aufnahme prüfen, umbenennen, kopieren – UND direkt den Check
2. YoutubeUploader  ← VOD auf YouTube hochladen
```

`StreamChecker.cs` gehört **nicht** in diese Queue (würde die Prüfung nur doppelt machen) – siehe `README_StreamChecker.md`, falls du sie trotzdem zusätzlich für andere Trigger einrichten willst.

---

## Ablauf nach jedem Stream (StreamArchiver)

```
Stream endet (Twitch)
    → Streamer.Bot: Stream Offline Event
    → StreamArchiver startet
        → Sperre prüfen (verhindert doppelte/parallele Läufe bei Doppel-Trigger)
        → Neueste, noch nicht archivierte MP4 in D:\Stream finden
          (bereits archivierte/behandelte Dateien werden anhand der CSV bzw.
           einer Merkliste übersprungen – ohne dass eine Datei angefasst wird)
        → FFmpeg Check (erste 10 Sekunden)
        → Datei OK?
            JA  → Umbenennen (NN_Spielname.mp4) + blockweises Kopieren nach Records
                  mit laufender Fortschrittsanzeige (Log + Dashboard-Balken)
            NEIN → Twitch VOD herunterladen als Fallback
                   (bei beschädigtem twitch-dl-Cache: Cache wird automatisch
                    geleert und der Download einmal wiederholt)
        → CSV Eintrag schreiben
        → Direkt im Anschluss: CSV gegen physische Dateien prüfen (dieselbe
          Logik wie StreamChecker.cs, aber ohne Umweg über die Queue)
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

## CSV Format (streams.csv)

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

## Dashboard

`dashboard.html` liegt **immer im gleichen Ordner wie `CsvPath`** – kein fixer Pfad, alle drei Scripts leiten ihn aus der Global Variable ab.

Aufbau (mehrere Dateien statt einer, damit möglichst wenig flackert):

| Datei | Inhalt | Aktualisierung |
|---|---|---|
| `dashboard.html` | Statische Shell mit drei Iframes | lädt sich selbst nie neu |
| `dashboard-step.html` | Header + aktueller Schritt | alle 5s |
| `dashboard-stats.html` | Statistiken, Checker, letzte Streams | alle 10s |
| `dashboard-log.html` | Gemeinsames Live-Log aller drei Scripts | ~alle 2,5s, **pausiert automatisch**, solange Text markiert ist (zum Kopieren) |
| `dashboard.css` | Gemeinsame Styles | wird bei jedem Lauf mit aktualisiert |

Das Live-Log zeigt Datum, Uhrzeit und ein farbiges Badge pro Script (Archiver/Checker/Uploader), damit auf einen Blick klar ist, was von wem kommt.

`StreamArchiver.cs` und `YoutubeUploader.cs` öffnen das Dashboard beim Start automatisch im Standardbrowser – aber jeweils nur **einmal pro Kalendertag** (nicht bei jedem einzelnen Lauf), damit nicht ständig neue Tabs aufgehen. `StreamChecker.cs` kann das ebenfalls, falls du es zusätzlich eigenständig einsetzt.

---

## Log prüfen

**View → Log** in Streamer.Bot. Alle Einträge beginnen mit `[StreamArchiver]` oder `[YTUploader]` (plus `[StreamChecker]`, falls du die optionale eigenständige Action zusätzlich eingerichtet hast). Zusätzlich landen die wichtigsten Meilensteine im Dashboard-Live-Log (siehe oben) sowie dauerhaft in `D:\Stream\Records\logs\activity_yyyy-MM-dd.log`.

---

## Troubleshooting

**"Kein Spielname gefunden"**
→ Passiert beim manuellen Test – `gameName` kommt nur vom echten Stream Offline Trigger mit den richtigen Trigger-Argumenten

**"FFmpeg Exception: The system cannot find the file"**
→ `FFmpegPath` in Global Variables prüfen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` in Global Variables prüfen
→ Testen: `twitch-dl videos DeinKanalname --limit 1 --json` im CMD

**"The process cannot access the file"**
→ FFmpeg oder ein anderer Prozess hat die Datei noch offen – kurz warten und nochmal triggern

**Download/Upload bricht mit "Joining files failed" bzw. "Invalid data found" ab**
→ Wird automatisch erkannt: der twitch-dl-Cache für die betroffene VOD wird geleert und der Download einmal automatisch wiederholt. Tritt es danach weiterhin auf, manuell prüfen: `%LOCALAPPDATA%\twitch-dl\videos\<VodId>\`

**Läuft ein Script nach einem Absturz von Streamer.bot nicht mehr an ("läuft bereits eine Instanz")**
→ Die Sperr-Datei unter `D:\Stream\Records\.locks\` einfach löschen (löst sich sonst nach 4 Stunden von selbst)
