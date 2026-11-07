# PitchWise Platform

[![CI](https://github.com/arturbuszka/PitchWise.Platform/actions/workflows/ci.yml/badge.svg)](https://github.com/arturbuszka/PitchWise.Platform/actions/workflows/ci.yml)

A club in a lower league has cameras, but no way to broadcast from them. PitchWise is the service
behind that broadcast: the club creates a match, points its encoder at the address it gets, and shares
a public page, or embeds the player on its own site. With several cameras, an operator cuts between them in the
console. The program can go on to YouTube and Twitch. When the match ends, the same page plays the recording.

![Match console](docs/screenshot.png)

Built with ASP.NET Core 10, EF Core, PostgreSQL, Redis, MediaMTX, nginx and Next.js.

## What it does

- **Matches:** a match has one or more inputs, usually one per camera. Each gets its own address and
  key for the encoder, over SRT or RTMP. One input is on air.
- **Switcher:** an operator watches every camera with little delay and cuts with buttons or number keys, with
  the sound chosen by itself. Viewers, the recording and every destination get one program, and losing a
  camera doesn't stop it. A club with its own OBS or vMix can send a ready program instead.
- **Signal status:** for each input, whether data is arriving, the bitrate and, over SRT, packet loss
  and round trip time.
- **Public page and embed:** viewers need no account. A dropped encoder shows a message and picks up
  again on reconnect.
- **Secure delivery:** viewers get signed, expiring URLs. An nginx edge checks them and caches the
  video, so the API never carries video bytes.
- **Recording:** every input is recorded by itself. After the match, each one is a video in the
  match's analysis, and the public page and embed play the one that was on air.
- **YouTube and Twitch:** each match can send what is on air to any number of RTMP destinations, without
  re-encoding. Each runs and fails by itself, and retries with growing waits. Their keys are stored encrypted
  and never returned.
- **Analyses:** every match has one for marking moments and rendering a highlight reel with ffmpeg,
  from its recordings or from uploads.
- **Multi-tenant:** each customer has an API key and can only reach their own data.
- **Share links:** public, time-limited links to a highlight.

Not built yet: transitions, frames and the score over the picture, and live statistics. See the
[roadmap](#roadmap).

## Architecture

```
                         control (JSON)
  Customer / web console ───────────────► API ◄──────► PostgreSQL
                                           │  ▲
                              render jobs  │  │ who may publish? who is publishing?
                                           ▼  │
                               Redis ──► Worker (ffmpeg)   MediaMTX ◄── SRT / RTMP ── Encoders
                                           ▲ │                │ │
                                           │ ▼   segments     │ ▼ HLS
                                        Storage ◄─────────────┘ nginx edge ──► Viewers
                                           │  finished videos │  ▲  signature check + cache
                                           └──────────────────┼──┘
                                                              │ RTSP, internal, with a secret
                 PostgreSQL ◄── what is wanted ── Broadcast ─ one ffmpeg per destination ──► YouTube, Twitch
                                                      └─ one switcher per match: cameras in, program back to MediaMTX
```

Decisions worth explaining:

- **The video path and the control path are separate.** Video goes from encoder to edge without
  touching the API, so a slow API does not stall a broadcast. Viewers do ask the API about the match:
  every 30 seconds while watching and 10 while waiting, so that cost grows with the audience.
- **The signature is in the URL path of live streams.** Relative playlist and segment URLs inherit
  it, so nothing is rewritten on the way. The cache key leaves it out, so all viewers share one
  entry per segment, and playlists are cached for a second.
- **Reading a signal takes either the edge or a secret on an internal port.** MediaMTX's RTMP and SRT ports
  are open to the internet for encoders, and reading a stream through them would skip the edge and its
  signature check, so no credentials open them for reading. Our own services read over RTSP, whose port isn't
  published, with a secret that also works nowhere else.
- **Destinations have their own service.** Restarting the worker to deploy a change to highlights doesn't cut
  a broadcast. It keeps one ffmpeg process per destination, and works from what the database says is wanted, so
  a restart picks up where it stopped. See [sending to YouTube and Twitch](#sending-to-youtube-and-twitch).
- **The program is an input.** A switcher publishes to a hidden input of the match, like any encoder, with a key
  that is new each time it starts. The public page, recording and outputs already follow "the input on air", so
  none of them had to change or learn about the switcher. See [cutting between cameras](#cutting-between-cameras).
- **Match status is polled from MediaMTX.** A missed callback or a crash on either side can't leave
  a match showing "live" forever. A dropped encoder puts the match back to `waiting` and can
  reconnect; only the customer can end a match for good.
- **Signal readings live in memory, not in the database.** They change every two seconds and mean
  nothing after a restart. Each API instance polls for itself, so instances share nothing. A connected
  encoder that stopped sending still looks ready to MediaMTX, so "stalled" is worked out from whether
  its byte counter is moving.
- **A match always has an analysis.** That is where its recordings land, so a highlight from a live
  match works the same way as one from an upload.
- **One match timeline.** A time `T` means `T` seconds after the match started by the clock, with signal
  gaps counted. The start is when the signal on air first arrived, and it is kept across reconnects.
  `Event.TimestampSeconds` is a `T`, and so is a position in any recording of the match, so nothing that
  uses them has to convert anything. See [how a recording is made](#how-a-recording-is-made).
- **Recording never touches the broadcast.** Each MediaMTX output is separate: with the recording disk
  full, the live stream carried on.
- **Finished videos reach viewers through the edge too.** The API signs a URL, and nginx serves the file
  with Range requests. The edge has only the finished videos mounted, not uploads or raw segments.
- **Tenant checks live in one filter.** Every route under an analysis names `{analysisId}`, so a
  global filter covers them all. Other tenants get a 404, so ids can't be probed.
- **API keys and stream keys are stored hashed**, and shown only once. Keys for destinations have to be sent
  on, so they are encrypted instead (AES-256-GCM, tied to their output's id), and never returned.

## Run it

Needs Docker and Docker Compose. No GPU or model files.

```bash
docker compose up -d --build
```

| What | Where |
|---|---|
| Web console | http://localhost:3000 |
| API | http://localhost:8000 |
| HLS edge | http://localhost:8080 |
| RTMP ingest | rtmp://localhost:1935 |
| SRT ingest | srt://localhost:8890 (UDP) |
| Operator's previews | http://localhost:8889, and 8189 (UDP) |

If port 8080 is taken, set another one for the edge: `EDGE_PORT=8081 docker compose up -d --build`.

### Broadcast a match

Open http://localhost:3000 and create a match, or use the API. The demo tenant's key is set up by
`docker-compose.yml`.

```bash
curl -s -X POST localhost:8000/api/matches \
  -H 'X-Api-Key: demo-key' -H 'Content-Type: application/json' -d '{"name":"Saturday final"}'
```

The response has what you need under `access`, and the stream key isn't shown again. Publish a test
signal to `srt_url` (this needs an ffmpeg built with SRT):

```bash
ffmpeg -re -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=frequency=440 \
  -c:v libx264 -preset veryfast -g 50 -pix_fmt yuv420p -c:a aac -f mpegts '<srt_url>'
```

Or to `rtmp_url`, with the same command ending in `-f flv '<rtmp_url>'`.

Then open `public_url` from the response to watch, as a viewer would. The page at `/matches/{id}` in the
console shows the state of the signal. To put the player on another site, use `embed_html`.

Use an encoder keyframe interval of about 2 seconds, as with `-g 50` here. Segments can't be shorter
than the distance between keyframes, and longer segments add delay for viewers.

End the match from the console, or with `POST /api/matches/{id}/end`. The recording is usually ready
seconds later, as joining it re-encodes nothing: `public_url` then plays it, and the analysis has a
video for each input.

### Add a customer

Set `ADMIN_TOKEN` (see [.env.example](.env.example)) and then:

```bash
curl -s -X POST localhost:8000/api/admin/tenants \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' -d '{"name":"Acme FC"}'
```

## Cutting between cameras

Without a switcher, viewers get one input, which suits a ready program from OBS or vMix. To cut between cameras,
create a match as [above](#broadcast-a-match), add a camera, and publish to both:

```bash
curl -s -X POST localhost:8000/api/matches/<match_id>/inputs \
  -H 'X-Api-Key: demo-key' -H 'Content-Type: application/json' -d '{"name":"Wide"}'
```

Keep B-frames off in the encoders, as with `-tune zerolatency` here. The switcher takes them, but WebRTC previews can't
show them, and the panel says so:

```bash
ffmpeg -re -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=frequency=440 \
  -c:v libx264 -preset veryfast -tune zerolatency -g 50 -pix_fmt yuv420p -c:a aac -f flv '<rtmp_url>'
```

Then press **Use the switcher** on the match's page and **Open the switcher**, or:

```bash
curl -s -X POST localhost:8000/api/matches/<match_id>/switcher -H 'X-Api-Key: demo-key'
curl -s -X POST localhost:8000/api/matches/<match_id>/switcher/cut \
  -H 'X-Api-Key: demo-key' -H 'Content-Type: application/json' -d '{"input_id":"<input_id>"}'
```

![The switcher](docs/switcher.png)

Keys `1` to `9` cut to a camera, and `Shift` with a number takes the sound from it. The history keeps each cut with
its position on the match timeline, and marks what the switcher did by itself.

**How it works.** The broadcast service keeps one Python process per match around a GStreamer pipeline, restarted for as
long as the match lasts. It starts when the first camera arrives, so that time zero isn't a black screen. A cut goes
through the API into the database, and a Postgres notification gets it to the process at once. The service also
checks the database every second, so a lost notification costs a second, not the cut.
[`prototypes/switcher`](prototypes/switcher/RESULTS.md) has the comparison with ffmpeg, which stops the whole program
when any one camera is lost, and how it was measured.

**Only cuts for now.** The picture is re-encoded to 1280x720 at 25 fps and 4 Mbit/s (`PROGRAM_WIDTH`, `PROGRAM_HEIGHT`,
`PROGRAM_VIDEO_KBPS`), with a keyframe every 2 seconds, which is what YouTube asks for.

**What was measured,** with generated signals on one machine, and no real cameras or network:

| What | Result |
|---|---|
| Cut, from the API call to a viewer reading the program over RTSP | Median about 130 ms, at most 198 ms over 120 cuts. No frame of the wrong camera in the rig's runs. |
| Delay the switcher adds | About 190 ms, on top of the camera's own encoder. |
| Timestamps | 97% of steps are 40 ±5 ms. The other 3% are the single steps at cuts. |
| Sound against picture, same camera | Sound 50 to 80 ms later. Steady through a run, and it varies between runs. Cuts don't add to it. |
| Camera not on air dies or hangs | No effect on the program. |
| Camera on air dies | Slate at once: black picture, and silence if it gave the sound. |
| Camera on air hangs | A 1.1 second wait, then the slate. No error tells us it hung. |
| Camera gives the sound and dies | Picture carries on in silence. The panel says so. |
| Camera comes back | Usable again, and back on air by itself if it is still the chosen one. |
| Camera missing when the switcher starts | Starts anyway. Usable once it appears. |
| Switcher process is killed | The program is back on MediaMTX about 4 seconds later, with the chosen camera. Destinations reconnect by themselves. It shows as a gap in the recording, and the timeline holds. |
| Switcher process hangs | Killed and restarted after 10 seconds without a frame, leaving a gap of about 14 seconds. |
| CPU per match | About 1 core at 720p, 1.8 at 1080p. |

## Sending to YouTube and Twitch

Outputs need an encryption key for the destinations' keys, which has no default, as a key in this repo would
protect nothing. Without it everything else works, and adding an output says what is missing.

To try it without an account, use the stand-in destination included here. Private addresses like this one are
refused unless allowed, so that customers can't aim an output at something inside the deployment:

```bash
printf 'OUTPUT_ENCRYPTION_KEY=%s\nOUTPUTS_ALLOW_PRIVATE_TARGETS=true\n' "$(openssl rand -base64 32)" >> .env
docker compose --profile test-destination up -d --build
```

Create a match and publish to it as [above](#broadcast-a-match), then open it in the console and add a
destination under **Destinations**, with `rtmp://test-destination:1935/live` as the server and anything as the
key, and press **Start**. Or with the API:

```bash
curl -s -X POST localhost:8000/api/matches/<match_id>/outputs \
  -H 'X-Api-Key: demo-key' -H 'Content-Type: application/json' \
  -d '{"name":"Test","url":"rtmp://test-destination:1935/live","stream_key":"test-key"}'
curl -s -X POST localhost:8000/api/matches/<match_id>/outputs/<output_id>/start -H 'X-Api-Key: demo-key'
```

To see what arrived, and then what a failed destination looks like:

```bash
docker compose exec api curl -s http://test-destination:9997/v3/paths/list
docker compose --profile test-destination kill test-destination
```

For the real thing, leave `OUTPUTS_ALLOW_PRIVATE_TARGETS` out, and enter the server address and stream key from
YouTube Studio or the Twitch dashboard. This has only been tested against a local server, not against YouTube or
Twitch themselves.

**The signal is sent as it arrives,** so the encoder has to use what the destination takes: H264 video and AAC
audio. Anything else is reported with the codec's name and isn't sent, as ffmpeg would pass H265 along without
complaint, leaving the outcome to the destination. YouTube also lists H265 and AV1, but on terms that don't hold
for every channel. A signal with no audio is sent, with a warning.

**What happens when something goes wrong,** as measured with ffmpeg as the encoder and a local server:

| What happens | Result |
|---|---|
| Encoder disconnects, or freezes | `waiting_for_signal`, which isn't counted as a failure. Sending starts again by itself about 4 seconds after the signal returns. |
| Another input goes on air, without a switcher | The destination has the new input about 4 seconds later, after a break. With one, a cut changes nothing for the destination. |
| Destination refuses or drops the connection | `retrying`, after 2, 4, 8, 16, 32 and then 60 seconds, five times over. Then `failed` until someone acts, between 6 and 7 minutes on, as each attempt takes a few seconds too. A minute of steady sending clears the count. |
| Destination hangs without closing | ffmpeg would wait forever, so it is killed when no data has moved for 15 seconds. Detected about 24 seconds in, as network buffers take data for a while. |
| ffmpeg is killed | As for a refused connection: back in about 5 seconds, with nothing else affected. |
| Broadcast service crashes | Restarted by Docker, with every destination back within about 7 seconds. Nothing else is affected. |
| Broadcast service is down | Outputs show `offline` after 15 seconds, rather than their last state. |
| Worker, edge, console or API stop | Sending carries on. |
| Two broadcast services | One sends and the other stands by, taking over when the first stops. A destination takes one sender per key. |
| `OUTPUT_ENCRYPTION_KEY` missing | Adding an output returns `503`, and running ones go to `failed`, naming the variable. Nothing else is affected. |
| `OUTPUT_ENCRYPTION_KEY` changes | Outputs go to `failed`, asking for the destination's key to be entered again. |

Copying costs almost nothing: under 1% of a core per destination in testing, and about 15 MB.

## How a recording is made

MediaMTX writes each input to files of about a minute in `storage/recordings/`. When the match ends, the
worker joins each input's files into one MP4 in `storage/vod/` without re-encoding, then deletes them.

**When the signal breaks,** MediaMTX starts a new file when it returns. The joined file keeps the break as
a hole in its timestamps, at its place on the match timeline, and the video records where its gaps are:

| What happened | Result |
|---|---|
| Encoder drops and reconnects | A gap of that length. Players skip it, and highlight clips stop short of it. |
| Encoder stalls but stays connected | No hole, as MediaMTX goes by the encoder's own timestamps. The next file returns to the clock, and until then the timeline can be off by the length of the stall. |
| MediaMTX crashes | The file stays readable, losing about the last second. |
| Recording disk fills up | The broadcast carries on. The recording has a gap for as long as it lasts, and the only sign is in the MediaMTX log. |
| Input started before the match | Cut at its first keyframe after the start. |
| Input started after the match | Its video starts with a gap, so its positions still match the others. |

**How exact the timeline is:** a file's time is when its first data reached MediaMTX, not when the camera
filmed it, so the delay of the encoder and protocol is built in. In testing with ffmpeg as the encoder,
SRT inputs lined up within the second we could read off the picture, about 2 seconds behind the clock, and
an RTMP input was a steady 2 seconds behind them. That is plenty for highlight clips, but inputs aren't
frame-accurate against each other.

**Disk space:** about 0.45 GB per hour for each 1 Mbps, per input, as every input is recorded. Measured
overhead is about 2%. Two hours from three cameras at 6 Mbps come to about 16 GB, and about twice that for
the moment while they are joined, since the segments are deleted afterwards. The worker checks for that
space first. Nothing is ever deleted automatically: a match that is never ended keeps its segments, and
videos stay until someone removes them.

If joining fails, the match shows why and its raw segments are kept, so it can be retried with
`POST /api/matches/{id}/recording/retry` or from the console. A job lost to a crash is picked up when the
worker next starts.

## API

Send `X-Api-Key` on everything except health checks, public match data, share links and admin calls.

| Endpoint | Purpose |
|---|---|
| `POST /api/matches` | Create a match with one input; returns its encoder addresses and key once |
| `GET /api/matches`, `GET /api/matches/{id}` | List matches and read their status, inputs and signal |
| `POST /api/matches/{id}/inputs` | Add an input; returns its addresses and key once |
| `POST /api/matches/{id}/inputs/{inputId}/rotate-key` | Replace a lost or leaked key, disconnecting the encoder |
| `PUT /api/matches/{id}/program` | Choose which input is on air, without a switcher (`409` with one) |
| `POST /api/matches/{id}/switcher` | Make the switcher's program what is on air, starting with the current input |
| `DELETE /api/matches/{id}/switcher` | Go back to one input on air, the one last cut to |
| `POST /api/matches/{id}/switcher/cut` | Cut to an input |
| `POST /api/matches/{id}/switcher/audio` | Take the sound from an input |
| `GET /api/matches/{id}/switcher/cuts` | History of cuts, kept after the match |
| `GET /api/matches/{id}/switcher/preview` | Short-lived WebRTC addresses for each input and the program |
| `GET /api/matches/{id}/playback` | Signed HLS URL of what is on air (`409` unless live) |
| `POST /api/matches/{id}/end` | Disconnect the encoders, close the match and start joining its recordings |
| `POST /api/matches/{id}/recording/retry` | Try again after a recording failed |
| `POST /api/matches/{id}/outputs` | Add a destination; its key is never returned |
| `PUT /api/matches/{id}/outputs/{outputId}` | Change its name, address or key |
| `POST /api/matches/{id}/outputs/{outputId}/start` | Start sending, or try again at once after a failure |
| `POST /api/matches/{id}/outputs/{outputId}/stop` | Stop sending |
| `DELETE /api/matches/{id}/outputs/{outputId}` | Remove a destination and its key |
| `GET /api/public/matches/{publicId}` | What the public page reads, including a signed URL of the recording; needs no key |
| `POST /api/analyses` | Create a match |
| `GET /api/analyses/{id}` | An analysis, with its videos and their gaps |
| `POST /api/analyses/{id}/videos` | Upload a recording |
| `POST /api/analyses/{id}/events` | Mark a moment; for a match, its time is on the match timeline |
| `POST /api/analyses/{id}/highlights` | Render a reel from selected moments |
| `GET /api/analyses/{id}/highlights/{hid}/hls` | Signed HLS URL for the reel |
| `POST /api/analyses/{id}/highlights/{hid}/share` | Create a public link |

## Develop

Run the backing services in Docker and the code on your machine, so you can set breakpoints. Needs
the .NET 10 SDK, Node 22, ffmpeg and Docker. Stop the full stack first with `docker compose down`,
as both use the same ports.

**Infrastructure:** Postgres, Redis, MediaMTX and the nginx edge.

```bash
docker compose -f docker-compose.infra.yml up -d --wait
```

**API**, in its own terminal. It applies migrations on startup and creates the tenant for `demo-key`.

```bash
dotnet run --project api-dotnet
```

**Worker**, in its own terminal. It renders highlight reels and joins recordings with the local ffmpeg.

```bash
dotnet run --project worker-dotnet/PitchWise.Worker
```

**Web console**, in its own terminal, at http://localhost:3000. `WEB_API_KEY` is the key that the
console adds to its calls to the API. Without it, every call gets a `401`.

```bash
cd web
npm ci
WEB_API_KEY=demo-key npm run dev
```

**Broadcast service**, in its own terminal, if you need outputs. It runs ffmpeg locally, and reads signals from
MediaMTX on `127.0.0.1:8554`. The API and this service get the same development-only encryption key from their
`launchSettings.json`, along with permission to send to private addresses.

```bash
dotnet run --project broadcast-dotnet/PitchWise.Broadcast
```

The switcher it runs needs Python and the GStreamer packages listed in its [Dockerfile](broadcast-dotnet/Dockerfile),
with `SWITCHER_SCRIPT` set to the full path of `broadcast-dotnet/switcher/switcher.py`. That hasn't been tried outside
its container.

The [broadcast steps](#broadcast-a-match) above work the same way against this setup. Files go to
`.dev-storage/`, not `storage/`, because the full stack creates `storage/` as root.

**Debugging in VS Code:** with the C# extension, open the Run and Debug view and start **API**, then
**Worker**, **Broadcast** and **Web console**. **API** brings the infrastructure up and builds first. The tasks
`infra: up`, `infra: down`, `build` and `test` are in `.vscode/tasks.json`.

Good to know:

- MediaMTX runs in a container and calls the API to check stream keys, so the API listens on
  `0.0.0.0:8000`. On a machine your network can reach, anyone there can reach this API too.
- If streams are refused with the API running, a firewall may be blocking containers from reaching
  the host on port 8000.
- If port 8080 is taken, start the infrastructure with `EDGE_PORT=8081` and the API with
  `HLS_BASE_URL=http://localhost:8081`.
- MediaMTX runs as user 1000 here, so the worker on your machine can delete the segments it writes. If
  your user id is different, start the infrastructure with `DEV_UID=$(id -u) DEV_GID=$(id -g)`.
- `docker compose -f docker-compose.infra.yml down -v` also deletes the development database. The
  project name in that file is fixed, so every clone of this repo shares that one database.

### Tests

```bash
dotnet test PitchWise.slnx
```

Integration tests run the real API and migrations against Postgres and Redis containers from
Testcontainers, so they need Docker, but not the infrastructure above. MediaMTX is faked in these
tests. The worker and broadcast tests run real ffmpeg, so they need it too. The switcher's process is faked in the
tests as well, and its image build checks that every GStreamer element it uses is there. What it does with video is
measured by the rig in `prototypes/switcher`, which CI doesn't run. CI also builds every image. On a small machine, run one project at a time, as each starts its own containers.

To change the schema:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project shared/PitchWise.Data
```

## Roadmap

1. **Frames and the score over the program,** and transitions. The picture is already being encoded, so this adds
   little.
2. **Analysis after the match:** a worker runs [PitchWise.Lab](https://github.com/arturbuszka/PitchWise.Lab),
   a .NET computer vision pipeline, on a recording and stores the results. Lab is a separate process
   that receives a copy of the video and returns only data, so its failure can't stop a broadcast. It
   gets the wide input rather than the program. Lab will need to read each frame's timestamp, because
   counting frames doesn't survive a gap, or frames lost on the way.
3. **Live analysis:** turn Lab's batch modules into streaming ones. Measured on a GTX 1660 Ti, the full
   pipeline takes about 0.87 s per frame, but detection, re-identification and tracking only about
   0.15 s. Most of the rest is pitch calibration, which needn't run on every frame.

## Known limitations

- **Highlight jobs can be lost.** A worker crash loses the job it popped from Redis, and a crash between
  the database write and the push leaves a job that never starts. A reliable queue and an outbox would
  close both gaps. Recordings get around this: the worker picks up unfinished ones when it starts.
- **Shared disk.** The API, worker and edge exchange files through one volume, so they can't run on
  separate machines without object storage.
- **One quality level.** MediaMTX repackages what the encoder sends, so there is no adaptive bitrate
  yet. That would be a transcoding step.
- **API keys can't be rotated or revoked** through the API yet. Stream keys can be replaced.
- **Inputs can't be removed** from a match once added.
- **Keys travel unencrypted.** Encoders send the stream key in the clear over both RTMP and SRT, as
  there's no RTMPS or SRT passphrase set up. Use a VPN on an untrusted network.
- **Without the switcher, changing the input on air reloads the player.** Viewers move over within about 30 seconds,
  with a break in the picture. Each camera needs its own connection and its own bitrate either way.
- **The switcher has only met generated signals.** Real cameras, phones, OBS, vMix, a real network and bad links
  are untested. Every lost camera in testing was a killed or frozen process on the same machine.
- **Cameras must send H264 and AAC to the switcher.** Anything else is reported by the camera's name and not used.
- **Previews are the cameras' full streams.** An operator's browser downloads and decodes all of them, which a venue's
  connection may not carry. Reduced copies for previews would fix it, at the cost of encoding them.
- **Previews need a camera without B-frames, and an address browsers can reach.** Set `PUBLIC_WEBRTC_URL` and
  `PUBLIC_WEBRTC_HOST` away from localhost, and allow UDP 8189. There is no TURN server, so a network that blocks
  that port gets no previews. A console on HTTPS needs HTTPS for previews too. Tested in Chromium only: Playwright's
  Firefox has no H264 for WebRTC.
- **Restarting the broadcast service cuts every program,** which it runs as child processes. With one match, that
  left a gap of about 3.5 seconds. A second instance only stands by.
- **A switcher started after a crash waits for a camera.** With every camera gone, a running program stays on the
  slate, but a restarted one doesn't start until the first camera returns.
- **A match costs a core.** There is no limit on how many switchers run on one machine, and nothing warns when it
  falls behind.
- **Switching on the switcher during a match leaves the earlier part out of the program's recording,** as a gap.
  Each camera's own recording still has it.
- **Packet loss is only known over SRT.** RTMP runs over TCP, which hides it.
- **The web console acts as one tenant.** It adds the demo key on the server, so it needs a login
  before being exposed.
- **MD5 signatures.** nginx `secure_link` requires MD5. A commercial CDN's token auth would replace it.
- **Without the switcher, the public recording is one input.** Viewers get whichever input was on air when the
  match ended, whatever was on air before that.
- **Changing encoder settings during a match stops its recording from joining.** Files joined without
  re-encoding must match, so a new resolution after a reconnect ends in a failed recording, with its raw
  segments kept. Trying again gives the same result, as there is no re-encoding path yet. Reconnecting
  with the same settings is fine.
- **Inputs aren't frame-accurate against each other.** See
  [how exact the timeline is](#how-a-recording-is-made).
- **A full disk isn't reported.** Only the MediaMTX log shows that recording has stopped.
- **Destinations have only been tested against a local server.** How YouTube and Twitch treat a reconnect, a
  signal with no audio or a stream that isn't H264 is untested, and so is RTMPS.
- **Without the switcher, changing the input on air breaks the stream to destinations for about 4 seconds,** as the
  copy has to restart.
- **Destination keys are visible to `ps` inside the broadcast container,** on ffmpeg's command line, along with
  the secret it reads signals with. Only that container sees them, and it runs no one else's code.
- **The check on private addresses happens before ffmpeg connects.** A name that resolves differently for
  ffmpeg's own lookup moments later gets past it.
- **The encryption key can't be rotated in place.** Changing it means entering each destination's key again.
- **One broadcast service sends everything.** A second one only stands by, so its capacity is one machine's.
- **Keys go to the destination as the address says.** Use `rtmps://` where it is offered, as plain RTMP sends
  them unencrypted.
- **Tested in Chromium and Firefox with ffmpeg as the encoder.** Safari, iOS, OBS and vMix haven't been
  tried with recordings. Neither has anything much longer than a few minutes.

## License

[MIT](LICENSE)
