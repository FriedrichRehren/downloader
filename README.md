# Downloader

Eine Weboberfläche für `yt-dlp`: URL einfügen, **Einlesen**, aus den
angebotenen Auflösungen und Formaten wählen (voreingestellt ist jeweils die
beste Qualität), Datei herunterladen. Jeder Download läuft in einem eigenen, kurzlebigen
Container aus [`friedrichrehren/yt-dlp`](https://github.com/FriedrichRehren/yt-dlp-docker);
die fertige Datei bleibt eine Stunde abrufbar und wird dann gelöscht.

Deployt wird die Anwendung aus dem dockops-Repository
(`apps/internal/downloader`); dort steht auch, wie Socket-Proxy und Volume
zusammenspielen.

## Ablauf

1. `POST /api/probe` mit `{ "url": "..." }` liest ein, was die URL anbietet.
   Die App startet dafür einen yt-dlp-Container ohne Volume, der nur Titel,
   Vorschaubild und die Formatliste ausgibt (`--print`, also ohne Download),
   und antwortet nach wenigen Sekunden mit:
   - `best`: was „Beste Qualität“ lädt, so wie yt-dlp selbst es auswählt:
     Auflösung, fps, HDR, Codecs, Größe
   - `resolutions`: alle Auflösungsstufen, höchste zuerst, jeweils mit fps,
     HDR und ob es H.264 gibt
   - `audioCodecs`: welche Audio-Codecs die Quelle hat, damit die Seite sagen
     kann, welches Format ohne Umwandlung auskommt

   Höchstens `MaxConcurrentProbes` laufen gleichzeitig, sonst 503. Eine URL,
   die yt-dlp nicht lesen kann, ergibt 422 mit dessen Fehlermeldung.
2. `POST /api/jobs` mit `{ "url", "mode", "format", "resolution" }` legt einen
   Job an (siehe [Formate](#formate)). Er gehört dem Browser, der ihn angelegt
   hat (siehe [Sicherheit](#sicherheit)).
3. Sobald einer der `MaxConcurrentJobs` Plätze frei ist, zieht die App
   `JobImage` neu, damit jeder Download mit dem aktuellen yt-dlp läuft; das
   Einlesen tut das auch. Gezogen wird höchstens alle fünf Minuten, sonst zöge
   Einlesen und Download kurz danach zweimal, und Docker Hub zählt jeden Pull.
   Ist die Registry nicht erreichbar, nimmt die App die lokale Kopie. Ein
   Image, das dabei ersetzt wurde, entfernt sie, sobald kein Job mehr darin
   läuft.
4. Dann startet sie über die Docker-API einen yt-dlp-Container. Er bekommt vom
   gemeinsamen Volume nur das Unterverzeichnis `files/<id>` als `/downloads` zu
   sehen (Volume-Subpath, Docker Engine ab 26).
5. Die App liest im Sekundentakt Status und Logs des Containers. yt-dlp gibt
   dafür eigene Markerzeilen aus (`downloader:progress`, `:title`, `:file`),
   damit nichts an seiner menschenlesbaren Ausgabe hängt.
6. Nach dem Ende wird der Container entfernt. Die Datei ist über
   `GET /api/jobs/<id>/file` abrufbar, mit Range-Unterstützung.
7. Nach Ablauf von `Retention` löscht die App Datei und Metadaten.
   `DELETE /api/jobs/<id>` bricht ab oder löscht sofort.

Die Job-Metadaten liegen als `jobs/<id>.json` auf demselben Volume. Ein Neustart
verliert also keine fertigen Dateien; Jobs, die zu dem Zeitpunkt liefen, werden
als abgebrochen markiert, und verwaiste Container räumt die App beim Start über
das Label `io.rehren.downloader.job` ab.

### Formate

| `mode` | `format` | yt-dlp | Ergebnis |
|---|---|---|---|
| `video` | `original` | `-f bv*+ba/b --merge-output-format mp4/webm/mkv` | beste Video- und Audiospur, ohne Neukodierung zusammengeführt; bei YouTube meist AV1/Opus als `.webm` |
| `video` | `mp4` | wie `-t mp4`: `-S vcodec:h264,…,acodec:aac --merge-output-format mp4 --remux-video mp4` | H.264 und AAC als `.mp4`, ohne Neukodierung; läuft in jedem Player, bei YouTube aber höchstens 1080p |
| `video` | `mkv` | wie `-t mkv`: `--merge-output-format mkv --remux-video mkv` | dieselben Spuren wie `original`, immer als `.mkv` |
| `audio` | `original` | `-f ba/b -x --audio-format best` | Originalspur ohne Neukodierung, meist `.opus` oder `.m4a`; bietet die Quelle verlustfreies Audio an, bleibt es verlustfrei, etwa als `.flac` |
| `audio` | `mp3` | wie `-t mp3`, aber `--audio-quality 0` | MP3 mit der besten VBR-Stufe (LAME V0, bei Musik um 245 kbit/s) |
| `audio` | `m4a` | `-f ba[acodec^=mp4a]/ba/b -x --audio-format m4a --audio-quality 0` | AAC als `.m4a`; hat die Quelle AAC, wird nur umverpackt |
| `audio` | `flac` | `-x --audio-format flac` | verlustfrei gespeichert, aber nur so gut wie die Quelle |
| `audio` | `wav` | `-x --audio-format wav` | unkomprimiertes PCM, etwa für Schnittprogramme |

`format` ist optional und steht ohne Angabe auf `original`. Konvertiert wird
jeweils nur, wenn die Quelle nicht schon im Zielcodec vorliegt. Die
yt-dlp-Presets sind ausgeschrieben statt per `-t` gesetzt, weil yt-dlp sich
vorbehält, sie in neuen Versionen anzupassen.

`resolution` gibt es nur für `video` und ist optional. Es begrenzt die
Auflösung, gezählt wie bei „1080p“ an der kürzeren Bildseite, und kommt als
`-S res:<n>` vor alle anderen Sortierkriterien; bei `mp4` direkt hinter den
Codec. Die Seite bietet dafür die Stufen aus dem Einlesen an (144p bis 4320p).
Liegt eine Quelle zwischen zwei Stufen, etwa 854×470, zählt sie zur nächsten,
hier 480p, und die Grenze ist ihre echte Auflösung.

Playlists werden nicht aufgelöst: `--no-playlist` und `--playlist-items 1`
sorgen dafür, dass höchstens ein Video geladen wird.

## Als App

Die Seite ist eine installierbare PWA (`manifest.webmanifest`, `sw.js`, Icons
unter `icons/`).

- **Nachfrage:** Beim Öffnen fragt sie, ob sie als App installiert werden
  soll. **Installieren** öffnet den Installationsdialog des Browsers.
  **Nein** fragt beim nächsten Öffnen wieder, gemerkt in `sessionStorage`.
  **Nie wieder fragen** gilt dauerhaft für diesen Browser, gemerkt in
  `localStorage`. Läuft die Seite schon als App, fragt sie nicht.
- **Browser:** Den Dialog gibt es nur in Chromium-Browsern, also Chrome und
  Edge auf Android und am Desktop, und nur, wenn der Browser die Seite für
  installierbar hält. Den Browser-eigenen Hinweis unterdrückt die Seite
  immer, damit „Nie wieder fragen“ auch für ihn gilt. Auf iPhone und iPad
  erklärt „Installieren“ stattdessen den Weg über „Teilen“ und „Zum
  Home-Bildschirm“, denn Safari bietet keinen Dialog an. Firefox am Desktop
  kann keine PWAs installieren und fragt nicht.
- **Teilen-Ziel:** Installiert auf Android erscheint der Downloader im
  Teilen-Menü anderer Apps. Ein geteilter Link öffnet `/?text=…` oder
  `/?url=…`, die Seite übernimmt die erste URL daraus und liest sie sofort
  ein.
- **Service Worker:** Er arbeitet „Network first“ und hält nur die Seite
  selbst vor, damit sie ohne Verbindung wenigstens startet. `/api/` reicht er
  unberührt durch, also auch die Downloads.

## Konfiguration

Alle Werte über Umgebungsvariablen mit dem Präfix `Downloader__`.

| Variable | Standard | Bedeutung |
|---|---|---|
| `DockerEndpoint` | `unix:///var/run/docker.sock` | Docker-API, auch `tcp://host:port` für einen Socket-Proxy |
| `JobImage` | `friedrichrehren/yt-dlp:latest` | Image der Job-Container, mit Tag; wird vor Jobs und beim Einlesen neu gezogen, höchstens alle fünf Minuten |
| `JobUser` | `65532:65532` | muss dem Benutzer der App entsprechen |
| `JobNetwork` | `bridge` | Netz der Job-Container, braucht Internetzugang |
| `JobMemoryBytes` | 2 GiB | Speicherlimit pro Job |
| `JobTimeout` | `01:00:00` | danach wird ein Job abgebrochen |
| `Retention` | `01:00:00` | Aufbewahrung ab Ende des Jobs |
| `MaxConcurrentJobs` | `2` | gleichzeitig laufende Container |
| `MaxActiveJobs` | `10` | wartende plus laufende Jobs; darüber antwortet die API mit 429 |
| `ProbeTimeout` | `00:01:00` | danach wird das Einlesen abgebrochen |
| `MaxConcurrentProbes` | `3` | gleichzeitiges Einlesen; darüber antwortet die API mit 503 |
| `DataPath` | `/downloads` | Mountpunkt des Volumes |
| `Volume` | leer | Name des Volumes; leer heißt, die App liest ihn aus ihrem eigenen Mount |

Die API benötigt die Docker-Bereiche `containers` (anlegen, starten, prüfen,
Logs, entfernen) und `images` (prüfen, ziehen, ersetzte entfernen). `exec`,
`stop`, `kill` und alles andere braucht sie nicht.

## Sicherheit

Die Anwendung hat keine Anmeldung. Wer sie erreicht, kann den Server beliebige
URLs laden lassen; begrenzt ist das nur durch `MaxActiveJobs`,
`MaxConcurrentJobs`, `MaxConcurrentProbes` und die beiden Timeouts.

Damit trotzdem niemand die Downloads anderer sieht, gehört jeder Job dem
Browser, der ihn angelegt hat. Die App vergibt dafür beim ersten Aufruf ein
zufälliges Cookie `downloader-client` (128 Bit, `HttpOnly`, `SameSite=Strict`)
und zeigt, liefert und löscht nur die Jobs mit derselben Kennung. Ein Job, der
auf dem Handy gestartet wurde, ist am Rechner also nicht zu sehen.
Cross-Site-Requests scheitern zusätzlich daran, dass die API JSON verlangt.

Die URL geht als einzelnes Argument hinter `--` an yt-dlp, nie durch eine Shell.
Die Job-Container laufen ohne Capabilities, mit schreibgeschütztem Dateisystem
und `no-new-privileges`.

## Entwicklung

```sh
dotnet test Downloader.slnx
docker build -t downloader:dev .
```

Die App erwartet, in einem Container zu laufen: Sie liest den Namen ihres
Volumes aus der eigenen Container-Inspektion. Lokal am einfachsten mit dem
dockops-Stack und einem Override, der den Port freigibt.

## Release

Jeder Push auf `main` (außer reinen Markdown-Änderungen) testet, baut das Image
für `linux/amd64` und `linux/arm64` und veröffentlicht es als
`ghcr.io/friedrichrehren/downloader:latest` und `:sha-<commit>`. Danach ruft die
CI den Dockhand-Webhook des Stacks auf, der mit Force Redeploy das neue
`latest` zieht.

Dafür braucht das Repository zwei Secrets:

| Secret | Inhalt |
|---|---|
| `DEPLOY_WEBHOOK_URL` | `https://<dockhand-host>/api/git/stacks/<id>/webhook` |
| `DEPLOY_WEBHOOK_SECRET` | das am Stack konfigurierte Webhook-Secret |

Ohne `DEPLOY_WEBHOOK_URL` wird nur veröffentlicht, nicht deployt. Rollback:
in dockops `DOWNLOADER_TAG` auf ein `sha-`-Tag setzen.
