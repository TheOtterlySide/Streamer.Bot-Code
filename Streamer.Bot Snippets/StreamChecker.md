# StreamChecker – Streamer.Bot Setup (optional)

> **Hinweis:** Seit dem letzten Update prüft `StreamArchiver.cs` direkt im Anschluss ans Archivieren selbst, ob alle Streams physisch vorhanden sind – ganz ohne diese Datei. Wenn du wie die meisten nur den **Stream-Offline**-Trigger nutzt, brauchst du `StreamChecker.cs` **nicht** und kannst die Action in Streamer.bot komplett löschen bzw. gar nicht erst anlegen.
>
> Diese Datei lohnt sich nur, wenn du den Check **zusätzlich** an andere Trigger hängen willst – z.B. direkt nach einem Neustart von Streamer.bot, oder manuell per Chat-Command, ohne auf den nächsten Stream zu warten.

Prüft ob alle archivierten Streams physisch vorhanden sind und öffnet das Dashboard automatisch.

---

## Was der StreamChecker macht

- Liest die letzten 15 Einträge aus `streams.csv`
- Prüft ob jede Datei physisch im Records-Ordner existiert
- Loggt fehlende oder problematische Streams (auch einzeln im Dashboard-Live-Log)
- Schreibt Ergebnis als Toast-Notification
- Speichert Checker-Ergebnis in Global Variables für das Dashboard
- Öffnet `dashboard.html` im Browser – **einmal pro Kalendertag**, nicht bei jedem Lauf

---

## Trigger

Da `StreamArchiver.cs` den Check jetzt selbst nach dem Archivieren macht, ist **Stream Offline** hier kein empfohlener Trigger mehr (würde nur doppelt prüfen). Sinnvoll bleibt:

| Trigger | Wann |
|---------|------|
| **Streamer.Bot Started** | Beim Start des Bots – sofort sehen, ob alles passt, ohne auf den nächsten Stream zu warten |
| **Chat Command** `!checkstreams` | Manuell bei Bedarf |

---

## Global Variables

Werden von StreamArchiver bzw. `Setup.cs` gesetzt und hier nur gelesen:

| Variable | Wert |
|----------|------|
| `RecordsRootDir` | `D:\Stream\Records` |
| `CsvPath` | `D:\Stream\Records\streams.csv` |

Werden vom StreamChecker selbst gesetzt (für Dashboard):

| Variable | Bedeutung |
|----------|-----------|
| `checker_total` | Anzahl geprüfter Streams |
| `checker_ok` | Anzahl OK |
| `checker_missing` | Anzahl fehlender Dateien |
| `checker_nobackup` | Anzahl ohne Backup |
| `checker_lastcheck` | Zeitstempel des letzten Checks |
| `dashboard_last_opened` | Datum des letzten automatischen Browser-Opens (gemeinsam mit YoutubeUploader) |

---

## Einrichten in Streamer.Bot

1. Neue Action anlegen: `StreamChecker`
2. Sub-Action: **Execute C# Code** → Inhalt von `StreamChecker.cs` reinkopieren → **Compile**
3. Trigger: **Streamer.Bot Started** und/oder Chat Command `!checkstreams` – je nachdem, was du brauchst

---

## CSV Format

StreamChecker liest `streams.csv`, die vom StreamArchiver befüllt wird:

```
StreamNr,Datum,Spielname,Dateiname,Status
1,2024-03-15,Elden Ring,01_Elden Ring.mp4,OK
2,2024-03-16,Elden Ring,02_Elden Ring.mp4,KORRUPT_VOD_GEZOGEN
```

Es werden immer nur die **letzten 15 Einträge** geprüft (~1 Monat bei 3 Streams/Woche).

---

## Status Werte

| Status | Was StreamChecker tut |
|--------|----------------------|
| `OK` | Prüft ob Datei existiert |
| `KORRUPT_VOD_GEZOGEN` | Prüft ob VOD-Backup existiert, loggt als Warnung |
| `KORRUPT_KEIN_BACKUP` | Loggt als kritische Warnung |

Fehlende Dateien werden sowohl in der Zusammenfassung als auch einzeln (`❌ Fehlt: ...`) ins gemeinsame Dashboard-Live-Log geschrieben.

---

## Dashboard

`dashboard.html` liegt im selben Ordner wie `CsvPath` – kein fixer Pfad. Aufgebaut aus mehreren Dateien (Shell + drei unabhängig aktualisierende Iframes), damit möglichst wenig flackert. Details siehe Haupt-README.

StreamChecker öffnet das Dashboard automatisch, aber **nur einmal pro Kalendertag** – der Zeitpunkt wird sich mit StreamArchiver und YoutubeUploader geteilt (`dashboard_last_opened`), damit nicht mehrere Scripts unabhängig voneinander Tabs aufmachen.

---

## Toast Notifications

| Situation | Toast |
|-----------|-------|
| Alle Streams OK | ✅ `Alle X Streams vorhanden` |
| Streams fehlen | ⚠️ `X fehlend, X ohne Backup – Log prüfen!` |

---

## Troubleshooting

**StreamChecker findet keine CSV**
→ StreamArchiver muss mindestens einmal erfolgreich gelaufen sein

**Dateien werden als fehlend markiert, obwohl sie da sind**
→ `RecordsRootDir` in Global Variables prüfen
→ Ordnerstruktur muss `RecordsRootDir\Spielname\Dateiname.mp4` sein

**Dashboard öffnet sich nicht**
→ `CsvPath` prüfen – Dashboard liegt im gleichen Ordner wie die CSV
→ Prüfen ob `dashboard.html` existiert (wird beim ersten StreamArchiver/StreamChecker-Run erstellt)
→ Wurde heute schon einmal automatisch geöffnet? Dann öffnet sich's erst wieder morgen – manuell öffnen geht natürlich trotzdem jederzeit