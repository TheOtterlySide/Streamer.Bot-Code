# StreamChecker – Streamer.Bot Setup (optional)

> **Hinweis:** `StreamArchiver.cs` prüft seit einiger Zeit direkt im Anschluss ans Archivieren selbst, ob alle Streams physisch vorhanden sind – ganz ohne diese Datei. Wenn du nur den **Stream-Offline**-Trigger nutzt, brauchst du `StreamChecker.cs` **nicht** und kannst die Action in Streamer.bot komplett löschen bzw. gar nicht erst anlegen.
>
> Diese Datei lohnt sich nur, wenn du den Check **zusätzlich** an andere Trigger hängen willst – z.B. direkt nach einem Neustart von Streamer.bot, oder manuell per Chat-Command.

Prüft, ob die letzten 15 in `streams.csv` eingetragenen Streams physisch vorhanden sind, und öffnet das Dashboard automatisch.

---

## Was der Code (aus `StreamChecker.cs`) tatsächlich macht

```
Execute()
 ├─ Global Variables lesen: RecordsRootDir, CsvPath
 ├─ Dashboard im Browser öffnen (bei jedem Lauf, kein Cooldown)
 ├─ streams.csv existiert nicht / ist leer → Log-Warnung, Ende
 ├─ Letzte 15 (nicht-leere) CSV-Zeilen einlesen
 ├─ Für jede Zeile:
 │   Status == KORRUPT_KEIN_BACKUP        → zählt als "Kein Backup"
 │   Datei existiert nicht unter
 │     RecordsRootDir\Spielname\Dateiname → zählt als "Fehlend"
 │   Status == KORRUPT_VOD_GEZOGEN        → zählt als "VOD Backup" (OK, Datei da)
 │   sonst                                 → zählt als "OK"
 ├─ Ergebnis in Global Variables: checker_total, checker_ok,
 │   checker_missing, checker_nobackup, checker_lastcheck (UTC-Zeitstempel)
 ├─ Prüft, ob StreamArchiver gerade parallel läuft
 │   (RecordsRootDir\.locks\archiver.lock existiert?) – falls ja, Hinweis
 │   an die Meldung anhängen, dass das Ergebnis gerade veraltet sein könnte
 └─ Bei Problemen: EIN Toast mit den konkreten Dateinamen
     (max. 3 aufgelistet, Rest als "+N weitere"), sonst ein "Alles OK"-Toast
```

---

## Trigger

Da `StreamArchiver.cs` den Check selbst macht, ist **Stream Offline** hier kein sinnvoller Trigger mehr (würde nur doppelt prüfen). Sinnvoll:

| Trigger | Wann |
|---------|------|
| **Streamer.Bot Started** | Sofort nach dem Bot-Start sehen, ob alles passt |
| **Chat Command** `!checkstreams` | Manuell bei Bedarf |

---

## Global Variables

Gelesen (müssen existieren, z.B. über `Setup.cs` oder von StreamArchiver mitgesetzt):

| Variable | Bedeutung |
|----------|-----------|
| `RecordsRootDir` | Basisordner fürs Archiv, z.B. `D:\Stream\Records` |
| `CsvPath` | Pfad zur `streams.csv` |

Geschrieben (fürs Dashboard, alle `persisted:false`):

| Variable | Bedeutung |
|----------|-----------|
| `checker_total`, `checker_ok`, `checker_missing`, `checker_nobackup` | Zahlen aus dem letzten Check |
| `checker_lastcheck` | UTC-Zeitstempel (ISO-Format) des letzten Checks |

---

## Einrichten in Streamer.Bot

1. Neue Action anlegen: `StreamChecker`
2. Sub-Action: Execute C# Code → Inhalt von `StreamChecker.cs` → Compile
3. Trigger: **Streamer.Bot Started** und/oder Chat Command `!checkstreams`

---

## Toast-Meldungen

| Situation | Toast |
|-----------|-------|
| Alles OK | ✅ `Alle X Streams vorhanden.` |
| Etwas fehlt | ⚠️ `X fehlend, X ohne Backup: #5 Datei.mp4 (fehlt), ... – ` ggf. + Hinweis, falls StreamArchiver parallel läuft |

---

## Troubleshooting

**"Keine streams.csv gefunden"**
→ Es muss vorher mindestens einmal erfolgreich `StreamArchiver.cs` gelaufen sein

**Dateien werden als fehlend markiert, obwohl sie da sind**
→ `RecordsRootDir` in Global Variables prüfen – erwartete Struktur ist `RecordsRootDir\Spielname\Dateiname.mp4`

**Dashboard öffnet sich nicht**
→ Kein Cooldown mehr, sollte bei jedem Lauf öffnen. Manuell geht `dashboard.html` (im selben Ordner wie `CsvPath`) jederzeit per Doppelklick. Bei mehreren Läufen kurz hintereinander stapeln sich entsprechend viele Tabs im Browser – normal, kein Bug.
