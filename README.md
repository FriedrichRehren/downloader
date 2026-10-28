# Downloader

Eine Weboberfläche für `yt-dlp`: URL einfügen, **Video** oder **Audio** wählen,
Datei herunterladen. Jeder Download läuft in einem eigenen, kurzlebigen
Container aus [`friedrichrehren/yt-dlp`](https://github.com/FriedrichRehren/yt-dlp-docker);
die fertige Datei bleibt eine Stunde abrufbar und wird dann gelöscht.

Deployt wird die Anwendung aus dem dockops-Repository
(`apps/internal/downloader`); dort steht auch, wie Socket-Proxy und Volume
zusammenspielen.

## Ablauf

1. `POST /api/jobs` mit `{ "url": "...", "mode": "video" | "audio" }` legt einen
   Job an. Er gehört dem Browser, der ihn angelegt hat (siehe
   [Sicherheit](#sicherheit)).
2. Sobald einer der `MaxConcurrentJobs` Plätze frei ist, zieht die App
   `JobImage` neu, damit jeder Download mit dem aktuellen yt-dlp läuft. Ist die
   Registry nicht erreichbar, nimmt sie die lokale Kopie. Ein Image, das dabei
   ersetzt wurde, entfernt sie, sobald kein Job mehr darin läuft.
3. Dann startet sie über die Docker-API einen yt-dlp-Container. Er bekommt vom
   gemeinsamen Volume nur das Unterverzeichnis `files/<id>` als `/downloads` zu
   sehen (Volume-Subpath, Docker Engine ab 26).
4. Die App liest im Sekundentakt Status und Logs des Containers. yt-dlp gibt
   dafür eigene Markerzeilen aus (`downloader:progress`, `:title`, `:file`),
   damit nichts an seiner menschenlesbaren Ausgabe hängt.
5. Nach dem Ende wird der Container entfernt. Die Datei ist über
   `GET /api/jobs/<id>/file` abrufbar, mit Range-Unterstützung.
6. Nach Ablauf von `Retention` löscht die App Datei und Metadaten.
   `DELETE /api/jobs/<id>` bricht ab oder löscht sofort.

Die Job-Metadaten liegen als `jobs/<id>.json` auf demselben Volume. Ein Neustart
verliert also keine fertigen Dateien; Jobs, die zu dem Zeitpunkt liefen, werden
als abgebrochen markiert, und verwaiste Container räumt die App beim Start über
das Label `io.rehren.downloader.job` ab.

### Formate

| Button | API (`mode`, `format`) | yt-dlp | Ergebnis |
|---|---|---|---|
| Video | `video`, `original` | `-f bv*+ba/b --merge-output-format mp4/webm/mkv` | beste Video- und Audiospur, ohne Neukodierung zusammengeführt; bei YouTube meist AV1/Opus als `.webm` |
| MP4 | `video`, `mp4` | wie `-t mp4`: `-S vcodec:h264,…,acodec:aac --merge-output-format mp4 --remux-video mp4` | H.264 und AAC als `.mp4`, ohne Neukodierung; läuft in jedem Player, bei YouTube aber höchstens 1080p |
| Audio | `audio`, `original` | `-f ba/b -x --audio-format best` | Originalspur ohne Neukodierung, meist `.opus` oder `.m4a`; bietet die Quelle verlustfreies Audio an, bleibt es verlustfrei, etwa als `.flac` |
| MP3 | `audio`, `mp3` | wie `-t mp3`, aber `--audio-quality 0` | in MP3 umgewandelt, beste VBR-Stufe (LAME V0, bei Musik um 245 kbit/s) |

Video und Audio sind die höchste Qualität, die die Quelle hergibt. MP4 und MP3
tauschen etwas davon gegen Kompatibilität; MP3 ist die einzige Variante, die
neu kodiert. Die yt-dlp-Presets sind ausgeschrieben statt per `-t` gesetzt,
weil yt-dlp sich vorbehält, sie in neuen Versionen anzupassen. Eine Umwandlung
nach FLAC oder WAV gibt es bewusst nicht: aus einer verlustbehafteten Spur wird
dadurch nur eine größere Datei mit demselben Klang.

`format` ist optional und steht ohne Angabe auf `original`.

Playlists werden nicht aufgelöst: `--no-playlist` und `--playlist-items 1`
sorgen dafür, dass höchstens ein Video geladen wird.

## Konfiguration

Alle Werte über Umgebungsvariablen mit dem Präfix `Downloader__`.

| Variable | Standard | Bedeutung |
|---|---|---|
| `DockerEndpoint` | `unix:///var/run/docker.sock` | Docker-API, auch `tcp://host:port` für einen Socket-Proxy |
| `JobImage` | `friedrichrehren/yt-dlp:latest` | Image der Job-Container, mit Tag; wird vor jedem Job neu gezogen |
| `JobUser` | `65532:65532` | muss dem Benutzer der App entsprechen |
| `JobNetwork` | `bridge` | Netz der Job-Container, braucht Internetzugang |
| `JobMemoryBytes` | 2 GiB | Speicherlimit pro Job |
| `JobTimeout` | `01:00:00` | danach wird ein Job abgebrochen |
| `Retention` | `01:00:00` | Aufbewahrung ab Ende des Jobs |
| `MaxConcurrentJobs` | `2` | gleichzeitig laufende Container |
| `MaxActiveJobs` | `10` | wartende plus laufende Jobs; darüber antwortet die API mit 429 |
| `DataPath` | `/downloads` | Mountpunkt des Volumes |
| `Volume` | leer | Name des Volumes; leer heißt, die App liest ihn aus ihrem eigenen Mount |

Die API benötigt die Docker-Bereiche `containers` (anlegen, starten, prüfen,
Logs, entfernen) und `images` (prüfen, ziehen, ersetzte entfernen). `exec`,
`stop`, `kill` und alles andere braucht sie nicht.

## Sicherheit

Die Anwendung hat keine Anmeldung. Wer sie erreicht, kann den Server beliebige
URLs laden lassen; begrenzt ist das nur durch `MaxActiveJobs`,
`MaxConcurrentJobs` und `JobTimeout`.

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
