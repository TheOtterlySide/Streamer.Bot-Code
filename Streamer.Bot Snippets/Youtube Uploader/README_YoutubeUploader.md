# YoutubeUploader – Streamer.Bot Setup

Automatisches Hochladen von Twitch VODs auf YouTube nach dem Stream.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" | `python --version` |

> **Wichtig:** VODs müssen auf Twitch aktiviert sein!
> Twitch → Einstellungen → Kanal → **"VODs speichern"** aktivieren

---

## Ordnerstruktur

```
D:\Stream\YoutubeQueue\                     ← Temporärer Download Ordner (automatisch angelegt)
D:\Stream\Records\streams.csv               ← Tracking CSV (geteilt mit StreamArchiver)
D:\Stream\Records\.youtube_token            ← OAuth Token (automatisch angelegt)
```

---

## Schritt 1: Google Cloud Console (einmalig, ~5 Minuten)

1. Gehe zu https://console.cloud.google.com
2. **"Projekt auswählen"** → **"Neues Projekt"** → Name: `StreamUploader` → **"Erstellen"**
3. **"APIs & Dienste"** → **"Bibliothek"** → `YouTube Data API v3` suchen → **"Aktivieren"**
4. **"APIs & Dienste"** → **"Anmeldedaten"** → **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
   - Falls Einwilligung nötig: User Type **Extern** → App Name `StreamUploader` → durchklicken
   - Anwendungstyp: **Desktopanwendung** → Name: `StreamUploader` → **"Erstellen"**
5. **Client-ID** und **Clientschlüssel** kopieren
6. Unter **"Autorisierte Weiterleitungs-URIs"** eintragen: `http://localhost:8080/` (mit trailing Slash!)
7. **"OAuth-Zustimmungsbildschirm"** → **"Testnutzer"** → Gmail Adresse hinzufügen

---

## Schritt 2: Global Variables in Streamer.Bot

| Variable | Wert |
|----------|------|
| `TwitchDLPath` | `twitch-dl` |
| `TwitchChannel` | `DeinTwitchKanalname` |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` |
| `CsvPath` | `D:\Stream\Records\streams.csv` |
| `TokenPath` | `D:\Stream\Records\.youtube_token` |
| `YouTubeClientId` | `deine-client-id.apps.googleusercontent.com` |
| `YouTubeClientSecret` | `GOCSPX-...` |

---

## Schritt 3: Streamer.Bot einrichten

1. Neue Action anlegen: `YoutubeUploader`
2. Sub-Action 1: **Twitch → Get Channel Info For Target** (Broadcaster)
3. Sub-Action 2: **Execute C# Code** → `YoutubeUploader.cs` reinkopieren → **Compile**
4. Trigger: **Stream Offline** → nach StreamArchiver und StreamChecker in der Queue

---

## Schritt 4: Erster Login (einmalig)

Beim ersten Run nach dem Setup:
- Browser öffnet sich automatisch
- Mit YouTube Account einloggen
- Zugriff bestätigen
- Browser zeigt: **"✅ Login erfolgreich! Fenster schließen."**
- Token wird gespeichert und automatisch erneuert

---

## Ablauf nach jedem Stream

```
Stream endet
    → Spielname + Titel von Twitch (Get Channel Info)
    → VOD ID holen
    → Bereits hochgeladen? → Überspringen
    → Datei bereits lokal? → Download überspringen
    → VOD von Twitch downloaden
    → OAuth Token prüfen / erneuern
    → Chunked Upload zu YouTube (mit Progress im Log + Dashboard)
    → CSV Eintrag schreiben
    → Temp Datei löschen
```

---

## YouTube Video Metadaten

```
Titel:       [Stream Titel von Twitch]
Beschreibung: Spiel: SIGNALIS
              Datum: 2026-07-16
Sichtbarkeit: Privat (manuell auf Öffentlich stellen)
```

---

## CSV Format

```
StreamNr,Datum,Spielname,Dateiname,Status
2803581008,2024-03-15,Elden Ring Stream,OK,dQw4w9WgXcQ
2803581009,2024-03-16,Dark Souls,FEHLGESCHLAGEN,
```

| Status | Bedeutung |
|--------|-----------|
| `OK` | Erfolgreich hochgeladen |
| `FEHLGESCHLAGEN` | Upload fehlgeschlagen – manuell prüfen |

---

## Troubleshooting

**"redirect_uri_mismatch"**
→ In Google Cloud Console unter Redirect URIs prüfen: `http://localhost:8080/` mit trailing Slash!

**"Unauthorized" beim Upload**
→ Token löschen: `D:\Stream\Records\.youtube_token` → Script neu triggern → neu einloggen

**"invalid_client"**
→ `YouTubeClientId` und `YouTubeClientSecret` in Global Variables prüfen

**"Kein Token – starte Browser OAuth" aber Browser öffnet sich nicht**
→ Token Datei löschen und nochmal triggern

**Download läuft nochmal obwohl Datei schon da**
→ Datei liegt in `D:\Stream\YoutubeQueue\[VodId].mp4` – Script prüft automatisch ob sie existiert
