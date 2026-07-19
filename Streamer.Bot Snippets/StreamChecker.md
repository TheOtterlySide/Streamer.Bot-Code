# StreamChecker – Streamer.Bot Setup

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

| Trigger | Wann |
|---------|------|
| **Stream Offline** (Twitch) | Nach jedem Stream Ende, nach StreamArchiver in der Queue |
| **Streamer.Bot Started** | Beim Start des Bots |
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
3. Trigger 1: **Stream Offline** (Twitch) → nach StreamArchiver in der Queue
4. Trigger 2: **Streamer.Bot Started**
5. Trigger 3: Chat Command `!checkstreams` (optional)

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

StreamChecker öffnet das Dashboard automatisch, aber **nur einmal pro Kalendertag** – der Zeitpunkt wird sich mit YoutubeUploader geteilt (`dashboard_last_opened`), damit nicht beide Scripts unabhängig voneinander Tabs aufmachen.

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