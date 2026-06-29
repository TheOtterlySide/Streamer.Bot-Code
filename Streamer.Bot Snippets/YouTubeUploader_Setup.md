# YouTube Uploader – Setup Anleitung

## Schritt 1: Google Cloud Console (einmalig, ~5 Minuten)

1. Gehe zu https://console.cloud.google.com
2. Oben links auf **"Projekt auswählen"** → **"Neues Projekt"**
   - Name: z.B. `StreamUploader`
   - Auf **"Erstellen"** klicken

3. Im linken Menü: **"APIs & Dienste"** → **"Bibliothek"**
   - Suche nach `YouTube Data API v3`
   - Klicke drauf → **"Aktivieren"**

4. Im linken Menü: **"APIs & Dienste"** → **"Anmeldedaten"**
   - Klicke **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
   - Wenn nach Einwilligung gefragt: **"Einwilligungsbildschirm konfigurieren"**
     - User Type: **Extern** → Erstellen
     - App Name: `StreamUploader`
     - Support E-Mail: deine Gmail Adresse
     - Unten auf **"Speichern und fortfahren"** (restliche Seiten einfach weiterklicken)
   - Zurück zu Anmeldedaten → **"Anmeldedaten erstellen"** → **"OAuth-Client-ID"**
     - Anwendungstyp: **Desktopanwendung**
     - Name: `StreamUploader`
     - **"Erstellen"**

5. Du siehst jetzt:
   - **Client-ID** → kopieren
   - **Clientschlüssel** → kopieren

6. Im Script `YoutubeUploader.cs` eintragen:
   ```
   private const string ClientId     = "HIER_DEINE_CLIENT_ID";
   private const string ClientSecret = "HIER_DEIN_CLIENT_SECRET";
   ```

7. Unter **"APIs & Dienste"** → **"OAuth-Zustimmungsbildschirm"** → **"Testnutzer"**
   - Deine Gmail Adresse als Testnutzer hinzufügen
   (solange die App nicht verifiziert ist, muss der Account als Testnutzer eingetragen sein)

---

## Schritt 2: twitch-dl installieren

1. Gehe zu https://github.com/ihabunek/twitch-dl/releases
2. Lade die neueste `twitch-dl.exe` herunter
3. Lege sie ab unter: `G:\Twitch DL Tool\twitch-dl.exe`

---

## Schritt 3: Streamer.Bot einrichten

1. Neue **Action** anlegen: `YouTube Upload`
2. **Trigger:** Stream Offline (Twitch)
3. **Sub-Action:** Execute C# Code → Inhalt von `YoutubeUploader.cs` reinkopieren → **Compile**

---

## Schritt 4: Erster Login (einmalig)

Beim ersten Stream Ende nach dem Setup:
- Ein Browser-Fenster öffnet sich automatisch
- Mit dem Google Account einloggen
- Zugriff bestätigen
- Browser zeigt: **"✅ Login erfolgreich!"**
- Ab sofort läuft alles automatisch – Token wird lokal gespeichert und automatisch erneuert

---

## Was passiert nach jedem Stream?

```
Stream endet
→ Twitch VOD wird heruntergeladen (nach D:\Stream\YoutubeQueue)
→ Automatisch auf YouTube hochgeladen (als PRIVAT)
→ Eintrag in D:\Stream\Records\youtube_uploads.csv
→ Temp Datei wird gelöscht
```

> **Hinweis:** Videos werden als **PRIVAT** hochgeladen.
> Du kannst sie danach in YouTube Studio manuell auf Öffentlich stellen
> oder im Script `"privacyStatus": "public"` setzen wenn du das automatisch willst.

---

## CSV Übersicht

Die Datei `D:\Stream\Records\youtube_uploads.csv` trackt alle Uploads:

```
VodId,Datum,Titel,Status,YouTubeId
123456789,2024-03-15,Elden Ring Stream,OK,dQw4w9WgXcQ
987654321,2024-03-16,Dark Souls,FEHLGESCHLAGEN,
```

---

## Troubleshooting

**"Kein gültiges OAuth Token"**
→ Token Datei löschen: `D:\Stream\Records\.youtube_token` → Script neu starten → Browser Login wiederholen

**"Konnte keine VOD ID von Twitch holen"**
→ twitch-dl Pfad prüfen: `G:\Twitch DL Tool\twitch-dl.exe`
→ Prüfen ob VOD auf Twitch aktiviert ist (Einstellungen → Kanal → VODs speichern)

**Upload sehr langsam**
→ Normal bei großen Dateien – das Script hat ein Timeout von 6 Stunden
→ Uploadgeschwindigkeit hängt von der Internetverbindung ab
