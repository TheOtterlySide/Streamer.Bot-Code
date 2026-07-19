# YoutubeUploader – Streamer.Bot Setup

Automatisches Hochladen von Twitch-VODs auf YouTube nach dem Stream.

> Dieses Dokument ersetzt das ältere, separate `YouTubeUploader_Setup.md` – die Inhalte sind hier zusammengeführt.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" anhaken | `python --version` |

---

## Ordnerstruktur

```
D:\Stream\YoutubeQueue\                     ← Temporärer Download-Ordner (wird automatisch angelegt)
D:\Stream\Records\youtube_uploads.csv       ← Tracking CSV
D:\Stream\Records\.youtube_token            ← OAuth Token (wird automatisch angelegt)
```

---

## Schritt 1: Google Cloud Console (einmalig, ~5 Minuten)

1. Gehe zu https://console.cloud.google.com
2. Oben links **"Projekt auswählen"** → **"Neues Projekt"**
   - Name: z.B. `StreamUploader` → **"Erstellen"**
3. Im linken Menü: **"APIs & Dienste"** → **"Bibliothek"**
   - Suche nach `YouTube Data API v3` → **"Aktivieren"**
4. Im linken Menü: **"APIs & Dienste"** → **"Anmeldedaten"**
   - **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
   - Falls nach Einwilligung gefragt: **"Einwilligungsbildschirm konfigurieren"**
      - User Type: **Extern** → Erstellen
      - App Name: `StreamUploader`
      - Support E-Mail: Gmail-Adresse eintragen
      - Restliche Seiten einfach durchklicken → **"Speichern und fortfahren"**
   - Zurück zu Anmeldedaten → **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
      - Anwendungstyp: **Desktopanwendung**
      - Name: `StreamUploader` → **"Erstellen"**
5. Du siehst jetzt: **Client-ID** und **Clientschlüssel** → beide kopieren
6. **Nicht** ins Script eintragen – stattdessen als Global Variables (siehe Schritt 2). Das Script liest sie zur Laufzeit, es gibt keine Platzhalter-Konstanten mehr im Code.
7. **"APIs & Dienste"** → **"OAuth-Zustimmungsbildschirm"** → **"Testnutzer"**
   - Gmail-Adresse als Testnutzer hinzufügen (solange die App nicht verifiziert ist, Pflicht)

---

## Schritt 2: Global Variables in Streamer.Bot

Am einfachsten per `Setup.cs` (siehe Haupt-README) – legt alle Variablen mit Platzhaltern an, die du danach nur noch ausfüllen musst. Manuell unter **Settings → Global Variables**:

| Variable | Wert |
|----------|------|
| `TwitchDLPath` | `twitch-dl` |
| `TwitchChannel` | `DeinTwitchKanalname` |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` |
| `YoutubeCsvPath` | `D:\Stream\Records\youtube_uploads.csv` |
| `TokenPath` | `D:\Stream\Records\.youtube_token` |
| `YouTubeClientId` | Client-ID aus Schritt 1 |
| `YouTubeClientSecret` | Clientschlüssel aus Schritt 1 |

> **Hinweis:** `TwitchDLPath` und `TwitchChannel` werden auch vom StreamArchiver genutzt – falls bereits angelegt, einfach überspringen. Fehlen `YouTubeClientId`/`YouTubeClientSecret`, bricht das Script mit einer klaren Fehlermeldung ab (Log, Toast, Dashboard), statt sinnlos gegen Google zu laufen.

---

## Schritt 3: Streamer.Bot einrichten

1. Neue Action anlegen: `YoutubeUploader`
2. Trigger: **Stream Offline** (Twitch) → nach StreamArchiver und StreamChecker in der Queue
3. Sub-Action: **Execute C# Code** → Inhalt von `YoutubeUploader.cs` reinkopieren → **Compile**

### Trigger-Argumente

Das Script liest `targetChannelTitle` und `game` aus den Trigger-Argumenten (`CPH.TryGetArg`) für Video-Titel und -Beschreibung. Kommen die leer an, fällt es auf einen generischen Titel mit Zeitstempel zurück – prüfe im Zweifel, ob dein Stream-Offline-Trigger diese Argumente tatsächlich mitliefert (ggf. über eine vorgeschaltete Sub-Action, die z.B. per "Get Broadcaster Info" Titel/Kategorie holt und per `CPH.SetArgument(...)` setzt).

---

## Schritt 4: Erster Login (einmalig)

Beim ersten Stream-Ende nach dem Setup:
- Ein Browser-Fenster öffnet sich automatisch (eigenes lokales Login-Fenster für den OAuth-Flow, unabhängig vom Dashboard)
- Mit dem Google-Account einloggen, Zugriff bestätigen
- Browser zeigt: **"✅ Login erfolgreich! Du kannst dieses Fenster schließen."**
- Token wird lokal gespeichert und automatisch erneuert – kein erneuter Login nötig

---

## Ablauf nach jedem Stream

```
Stream endet (Twitch)
    → YoutubeUploader startet
        → Sperre prüfen (verhindert doppelte/parallele Läufe bei Doppel-Trigger)
        → Dashboard im Browser öffnen (einmal pro Kalendertag)
        → streamTitle + gameName aus Streamer.Bot-Trigger-Argumenten
        → Neueste VOD-ID von Twitch holen
        → Bereits hochgeladen? → Überspringen
        → VOD downloaden nach D:\Stream\YoutubeQueue
          (Fortschritt kommt direkt aus twitch-dls eigener Ausgabe, nicht erst
           am Ende – inkl. Live-Balken im Dashboard; bei beschädigtem
           twitch-dl-Cache: automatisch bereinigen + einmal wiederholen)
        → OAuth Token prüfen / erneuern
        → Auf YouTube hochladen (als Privat), in 16-MB-Blöcken mit Fortschritt
          und automatischem Retry pro Block (kein Hängenbleiben mehr bei
          großen Dateien wie früher bei einem einzelnen Riesen-Request)
        → CSV-Eintrag schreiben
        → Temp-Datei löschen (nur die temporäre Download-Kopie, nie das Original)
```

---

## Action Queue Reihenfolge

```
Stream Offline
    1. StreamArchiver   ← Aufnahme prüfen, umbenennen, kopieren
    2. StreamChecker    ← CSV gegen Dateien prüfen, Dashboard öffnen
    3. YoutubeUploader  ← VOD auf YouTube hochladen
```

---

## CSV Format

```
VodId,Datum,Titel,Status,YouTubeId
2803581008,2024-03-15,Elden Ring Stream,OK,dQw4w9WgXcQ
2803581009,2024-03-16,Dark Souls,FEHLGESCHLAGEN,
```

### Status Werte
| Status | Bedeutung |
|--------|-----------|
| `OK` | Erfolgreich auf YouTube hochgeladen |
| `FEHLGESCHLAGEN` | Upload fehlgeschlagen – manuell prüfen |

---

## Datenschutz

Videos werden als **Privat** hochgeladen, mit automatisch generierter Beschreibung (Spiel + Upload-Zeitpunkt). In YouTube Studio können sie danach manuell auf Öffentlich gestellt werden. Um das automatisch zu ändern, im Script (`UploadToYoutube`) anpassen:

```csharp
"privacyStatus": "public"
```

---

## Troubleshooting

**Browser öffnet sich nicht beim ersten Login**
→ Token-Datei manuell prüfen/löschen: `D:\Stream\Records\.youtube_token` → Script neu starten

**"YouTubeClientId/YouTubeClientSecret nicht als Global Variable gesetzt"**
→ Genau diese beiden Global Variables fehlen oder sind leer – siehe Schritt 2

**"Kein gültiges OAuth Token"**
→ Token-Datei löschen: `D:\Stream\Records\.youtube_token` → Script neu starten → Browser-Login wiederholen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` in Global Variables prüfen
→ Testen: `twitch-dl videos DeinKanalname --limit 1 --json` im CMD

**Download/Upload bricht mit "Joining files failed" o.ä. ab**
→ Wird automatisch erkannt und einmal mit geleertem twitch-dl-Cache wiederholt. Bleibt es bestehen: `%LOCALAPPDATA%\twitch-dl\videos\<VodId>\` manuell prüfen/löschen

**Upload sehr langsam**
→ Normal bei großen Dateien – Timeout ist auf 6 Stunden gesetzt, Upload läuft blockweise mit Fortschrittsanzeige im Log/Dashboard
→ Abhängig von der Uploadgeschwindigkeit der Internetverbindung

**Video-Titel ist nur generisch "Stream ..."**
→ `targetChannelTitle`/`game` kamen leer vom Trigger – siehe "Trigger-Argumente" oben

**"VOD bereits hochgeladen"**
→ Normales Verhalten – der Duplikat-Check greift, kein erneuter Upload