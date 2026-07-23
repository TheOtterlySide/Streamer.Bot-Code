# YoutubeUploader – Streamer.Bot Setup

Lädt die neueste Twitch-VOD nach jedem Stream automatisch auf YouTube hoch.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" anhaken | `python --version` |

---

## Schritt 1: Google Cloud Console (einmalig, ~5 Minuten)

1. https://console.cloud.google.com → Neues Projekt anlegen
2. **APIs & Dienste → Bibliothek** → `YouTube Data API v3` aktivieren
3. **APIs & Dienste → Anmeldedaten** → **Anmeldedaten erstellen → OAuth-Client-ID**
   - Falls nötig: Einwilligungsbildschirm konfigurieren (User Type: Extern, App-Name, Support-E-Mail)
   - Anwendungstyp: **Desktopanwendung**
4. Client-ID und Clientschlüssel notieren (siehe Schritt 2 – kommen als Global Variables rein, **nicht** in den Code)
5. **APIs & Dienste → OAuth-Zustimmungsbildschirm → Testnutzer**: deine Gmail-Adresse hinzufügen (Pflicht, solange die App nicht verifiziert ist)

---

## Schritt 2: Global Variables

Das Script bricht mit einer klaren Fehlermeldung ab (Log, Toast, Dashboard), wenn `YouTubeClientId` oder `YouTubeClientSecret` fehlen – es gibt keine Platzhalter-Konstanten mehr im Code.

| Variable | Wert |
|----------|------|
| `TwitchDLPath` | `twitch-dl` |
| `TwitchChannel` | Dein Twitch-Kanalname |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` |
| `YoutubeCsvPath` | `D:\Stream\Records\youtube_uploads.csv` |
| `TokenPath` | `D:\Stream\Records\.youtube_token` |
| `YouTubeClientId` | Client-ID aus Schritt 1 |
| `YouTubeClientSecret` | Clientschlüssel aus Schritt 1 |

`TwitchDLPath`/`TwitchChannel` werden auch von StreamArchiver genutzt – falls schon vorhanden, nicht doppelt anlegen. Am einfachsten per `Setup.cs` (siehe Haupt-README).

---

## Schritt 3: Streamer.Bot einrichten

1. Neue Action: `YoutubeUploader`
2. Trigger: **Stream Offline** (Twitch), nach StreamArchiver in der Queue
3. Sub-Action: Execute C# Code → Inhalt von `YoutubeUploader.cs` → Compile

### Trigger-Argumente

Das Script liest `targetChannelTitle` und `game` per `CPH.TryGetArg` für Video-Titel/-Beschreibung. Kommen die leer an, wird der Titel `Stream <Datum> <Uhrzeit>`. Falls deine Trigger-Argumente anders heißen oder über eine vorgeschaltete Sub-Action gesetzt werden müssen (z.B. per "Get Broadcaster Info"), das entsprechend anpassen.

---

## Schritt 4: Erster Login (einmalig)

Beim ersten Durchlauf öffnet sich automatisch ein Browser-Fenster für den Google-Login (eigener lokaler OAuth-Redirect auf `http://localhost:8080/`, unabhängig vom Dashboard). Nach Bestätigung: "✅ Login erfolgreich! Du kannst dieses Fenster schließen." Token wird unter `TokenPath` gespeichert und automatisch erneuert.

---

## Ablauf bei jedem Lauf (aus dem Code)

```
Execute()
 ├─ YouTubeClientId/Secret gesetzt? Nein → Abbruch (Log + Toast + Dashboard)
 ├─ Sperre prüfen (.locks\uploader.lock, 4h Timeout gegen verwaiste Sperren)
 ├─ Dashboard im Browser öffnen (bei jedem Lauf, kein Cooldown)
 ├─ Titel/Spielname aus Trigger-Argumenten lesen
 ├─ Neueste Twitch-VOD-ID holen (twitch-dl videos <Kanal> --limit 1 --json)
 │   └─ keine ID → Abbruch
 ├─ Bereits in youtube_uploads.csv mit dieser VOD-ID? → überspringen
 ├─ VOD nach TempDownloadDir herunterladen
 │   - Fortschritt kommt direkt aus twitch-dls eigener Ausgabe (Prozent,
 │     Geschwindigkeit, ETA) – nicht erst am Ende beim ffmpeg-Zusammenfügen
 │   - "Joining files failed" o.ä. → twitch-dl-Cache unter
 │     %LOCALAPPDATA%\twitch-dl\videos\<VodId>\ wird automatisch gelöscht,
 │     ein Versuch automatisch wiederholt
 │   └─ Download fehlgeschlagen → Abbruch
 ├─ OAuth-Token holen/erneuern
 │   └─ kein gültiges Token → Abbruch
 ├─ Auf YouTube hochladen (Privat, Kategorie "Gaming")
 │   - Resumable-Upload-Session starten (Metadaten: Titel + Beschreibung
 │     mit Spielname und Upload-Zeitpunkt)
 │   - Datei in 16-MB-Blöcken hochladen, pro Block bis zu 3 Versuche bei
 │     Fehlern, nach jedem Block Fortschritt in Log + Dashboard
 │   └─ keine Video-ID am Ende erhalten → Abbruch
 ├─ CSV-Eintrag schreiben (OK + YouTube-ID, oder FEHLGESCHLAGEN)
 └─ Temp-Datei löschen (nur die heruntergeladene Kopie, nie ein Original)
```

---

## CSV Format (youtube_uploads.csv)

```
VodId,Datum,Titel,Status,YouTubeId
2803581008,2024-03-15,Elden Ring Stream,OK,dQw4w9WgXcQ
2803581009,2024-03-16,Dark Souls,FEHLGESCHLAGEN,
```

| Status | Bedeutung |
|--------|-----------|
| `OK` | Erfolgreich hochgeladen |
| `FEHLGESCHLAGEN` | Upload fehlgeschlagen – manuell prüfen |

---

## Datenschutz

Videos werden als **Privat** hochgeladen. Um das zu ändern: in `UploadToYoutube` das JSON-Feld `"privacyStatus": "private"` anpassen (z.B. auf `"public"`).

---

## Troubleshooting

**"YouTubeClientId/YouTubeClientSecret nicht als Global Variable gesetzt"**
→ Beide fehlen oder sind leer – siehe Schritt 2

**Browser öffnet sich nicht beim ersten Login**
→ Token-Datei löschen (`TokenPath`) → Script neu starten

**"Kein gültiges OAuth Token"**
→ Token-Datei löschen → Script neu starten → Browser-Login wiederholen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` prüfen, testen mit `twitch-dl videos DeinKanalname --limit 1 --json`

**Download/Upload bricht mit "Joining files failed" o.ä. ab**
→ Wird automatisch erkannt und mit geleertem twitch-dl-Cache einmal wiederholt. Bleibt es bestehen: `%LOCALAPPDATA%\twitch-dl\videos\<VodId>\` manuell prüfen

**Video-Titel ist nur generisch "Stream ..."**
→ `targetChannelTitle`/`game` kamen leer vom Trigger – Trigger-Argumente prüfen (siehe Schritt 3)

**"VOD bereits hochgeladen"**
→ Normales Verhalten, der Duplikat-Check greift

**Script läuft nach einem Absturz nicht mehr an ("läuft bereits eine Instanz")**
→ Sperr-Datei unter `RecordsRootDir\.locks\uploader.lock` löschen (löst sich sonst nach 4h von selbst)
