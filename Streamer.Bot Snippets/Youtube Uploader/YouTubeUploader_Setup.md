# YoutubeUploader – Streamer.Bot Setup

Automatisches Hochladen von Twitch VODs auf YouTube nach dem Stream.

---

## Voraussetzungen

| Tool | Installation | Prüfen |
|------|-------------|--------|
| **twitch-dl** | `pip install twitch-dl` | `twitch-dl --version` |
| **Python 3.x** | https://www.python.org/downloads/ → "Add to PATH" anhaken | `python --version` |

> **Wichtig:** VODs müssen auf Twitch aktiviert sein!
> Twitch → Einstellungen → Kanal → **"VODs speichern"** aktivieren

---

## Ordnerstruktur

```
D:\Stream\YoutubeQueue\                     ← Temporärer Download Ordner (wird automatisch angelegt)
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
     - Support E-Mail: Gmail Adresse eintragen
     - Restliche Seiten einfach durchklicken → **"Speichern und fortfahren"**
   - Zurück zu Anmeldedaten → **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
     - Anwendungstyp: **Desktopanwendung**
     - Name: `StreamUploader` → **"Erstellen"**

5. Du siehst jetzt:
   - **Client-ID** → kopieren
   - **Clientschlüssel** → kopieren

6. Im Script `YoutubeUploader.cs` oben eintragen:
   ```csharp
   private const string ClientId     = "HIER_DEINE_CLIENT_ID";
   private const string ClientSecret = "HIER_DEIN_CLIENT_SECRET";
   ```

7. **"APIs & Dienste"** → **"OAuth-Zustimmungsbildschirm"** → **"Testnutzer"**
   - Gmail Adresse als Testnutzer hinzufügen
   - Solange die App nicht verifiziert ist muss der Account als Testnutzer eingetragen sein

---

## Schritt 2: Global Variables in Streamer.Bot

**Settings → Global Variables** – folgende Einträge anlegen:

| Variable | Wert |
|----------|------|
| `TwitchDLPath` | `twitch-dl` |
| `TwitchChannel` | `DeinTwitchKanalname` |
| `TempDownloadDir` | `D:\Stream\YoutubeQueue` |
| `YoutubeCsvPath` | `D:\Stream\Records\youtube_uploads.csv` |
| `TokenPath` | `D:\Stream\Records\.youtube_token` |

> **Hinweis:** `TwitchDLPath` und `TwitchChannel` werden auch vom StreamArchiver genutzt – falls bereits angelegt einfach überspringen.

---

## Schritt 3: Streamer.Bot einrichten

1. Neue Action anlegen: `YoutubeUploader`
2. Trigger: **Stream Offline** (Twitch) → nach StreamArchiver und StreamChecker in der Queue
3. Sub-Action: **Execute C# Code** → Inhalt von `YoutubeUploader.cs` reinkopieren → **Compile**

---

## Schritt 4: Erster Login (einmalig)

Beim ersten Stream Ende nach dem Setup:
- Ein Browser-Fenster öffnet sich automatisch
- Mit dem Google Account einloggen
- Zugriff bestätigen
- Browser zeigt: **"✅ Login erfolgreich! Du kannst dieses Fenster schließen."**
- Token wird lokal gespeichert und automatisch erneuert – kein erneuter Login nötig

---

## Ablauf nach jedem Stream

```
Stream endet (Twitch)
    → YoutubeUploader startet
        → streamTitle + gameName aus Streamer.Bot
        → Neueste VOD ID von Twitch holen
        → Bereits hochgeladen? → Überspringen
        → VOD downloaden nach D:\Stream\YoutubeQueue
        → OAuth Token prüfen / erneuern
        → Auf YouTube hochladen (als Privat)
        → CSV Eintrag schreiben
        → Temp Datei löschen
```

---

## Action Queue Reihenfolge

```
Stream Offline
    1. StreamArchiver   ← Aufnahme prüfen, umbenennen, kopieren
    2. StreamChecker    ← CSV gegen Dateien prüfen
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

Videos werden als **Privat** hochgeladen. In YouTube Studio können sie danach manuell auf Öffentlich gestellt werden. Um das zu ändern im Script:

```csharp
"privacyStatus": "public"
```

---

## Troubleshooting

**Browser öffnet sich nicht beim ersten Login**
→ Token Datei löschen und Script neu starten

**"Kein gültiges OAuth Token"**
→ Token Datei löschen: `D:\Stream\Records\.youtube_token` → Script neu starten → Browser Login wiederholen

**"Konnte keine VOD ID von Twitch holen"**
→ `TwitchChannel` in Global Variables prüfen
→ Testen: `twitch-dl videos DeinKanalname --limit 1 --json` im CMD
→ Prüfen ob VODs auf Twitch aktiviert sind: Twitch → Einstellungen → Kanal → **"VODs speichern"**

**"VOD bereits hochgeladen"**
→ Normales Verhalten – der Duplikat-Check greift, kein erneuter Upload

**Upload sehr langsam**
→ Normal bei großen Dateien – Timeout ist auf 6 Stunden gesetzt
→ Abhängig von der Uploadgeschwindigkeit der Internetverbindung