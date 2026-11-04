# PitchWise Platform

[![CI](https://github.com/arturbuszka/PitchWise.Platform/actions/workflows/ci.yml/badge.svg)](https://github.com/arturbuszka/PitchWise.Platform/actions/workflows/ci.yml)

A backend service for sports video. A customer sends raw footage, live or recorded, and gets it back
as secure, cacheable HLS, with highlight reels built from moments in the match. Analysis and other
features plug in as separate workers.

![Live streams page](docs/screenshot.png)

Built with ASP.NET Core 10, EF Core, PostgreSQL, Redis, MediaMTX, nginx and Next.js.

## What it does

- **Live streaming:** create a channel through the API, publish to it over RTMP, watch it over HLS.
- **Recordings:** upload a match, mark moments, and render them into a highlight reel with ffmpeg.
- **Secure delivery:** viewers get signed, expiring URLs. An nginx edge checks them and caches the
  video, so the API never carries video bytes.
- **Multi-tenant:** each customer has an API key and can only reach their own data.
- **Share links:** public, time-limited links to a highlight.

## Architecture

```
                         control (JSON)
  Customer / web console ───────────────► API ◄──────► PostgreSQL
                                           │  ▲
                              render jobs  │  │ who may publish? what is live?
                                           ▼  │
                               Redis ──► Worker (ffmpeg)   MediaMTX ◄── RTMP ── Encoder
                                           │                  │
                                           ▼                  ▼ HLS
                                        Storage ──────► nginx edge ──► Viewers
                                                     signature check + cache
```

Decisions worth explaining:

- **The video path and the control path are separate.** Viewers talk only to the edge. A slow API
  does not stall a broadcast, and a thousand viewers do not load the API.
- **The signature is in the URL path of live streams.** Relative playlist and segment URLs inherit
  it, so nothing is rewritten on the way. The cache key leaves it out, so all viewers share one
  entry per segment, and playlists are cached for a second.
- **Stream status is polled from MediaMTX.** A missed callback or a crash on either side can't leave
  a stream showing "live" forever. A dropped encoder goes back to `idle` and can reconnect; only the
  customer can end a stream for good.
- **Tenant checks live in one filter.** Every route under an analysis names `{analysisId}`, so a
  global filter covers them all. Other tenants get a 404, so ids can't be probed.
- **API keys and stream keys are stored hashed**, and shown only once.

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

### Try a live stream

```bash
# Create a channel. The demo tenant's key is set up by docker-compose.yml.
curl -s -X POST localhost:8000/api/streams \
  -H 'X-Api-Key: demo-key' -H 'Content-Type: application/json' -d '{"name":"Saturday final"}'

# Publish a test signal to the ingest_url it returned. OBS works as well.
ffmpeg -re -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=frequency=440 \
  -c:v libx264 -preset veryfast -g 50 -pix_fmt yuv420p -c:a aac -f flv '<ingest_url>'
```

Then press **Watch** at http://localhost:3000/streams, or ask for a signed URL yourself with
`GET /api/streams/{id}/playback`.

Use an encoder keyframe interval of about 2 seconds, as with `-g 50` here. Segments can't be shorter
than the distance between keyframes, and longer segments add delay for viewers.

### Add a customer

Set `ADMIN_TOKEN` (see [.env.example](.env.example)) and then:

```bash
curl -s -X POST localhost:8000/api/admin/tenants \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H 'Content-Type: application/json' -d '{"name":"Acme FC"}'
```

## API

Send `X-Api-Key` on everything except health checks, share links and admin calls.

| Endpoint | Purpose |
|---|---|
| `POST /api/streams` | Create a channel; returns the ingest URL and stream key once |
| `GET /api/streams`, `GET /api/streams/{id}` | List channels and read their status |
| `GET /api/streams/{id}/playback` | Signed HLS URL (`409` unless live) |
| `POST /api/streams/{id}/end` | Disconnect the encoder and close the channel |
| `POST /api/analyses` | Create a match |
| `POST /api/analyses/{id}/videos` | Upload a recording |
| `POST /api/analyses/{id}/events` | Mark a moment |
| `POST /api/analyses/{id}/highlights` | Render a reel from selected moments |
| `GET /api/analyses/{id}/highlights/{hid}/hls` | Signed HLS URL for the reel |
| `POST /api/analyses/{id}/highlights/{hid}/share` | Create a public link |

## Tests

```bash
dotnet test PitchWise.slnx
```

Integration tests run the real API and migrations against Postgres and Redis containers from
Testcontainers, so they need Docker. On rootless Podman, set
`DOCKER_HOST=unix://$XDG_RUNTIME_DIR/podman/podman.sock` and `TESTCONTAINERS_RYUK_DISABLED=true`.
MediaMTX is faked in these tests. CI also builds every image.

To change the schema:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project shared/PitchWise.Data
```

## Roadmap: live analysis

The goal is match statistics during the broadcast, without ever putting the broadcast at risk. The
analysis comes from [PitchWise.Lab](https://github.com/arturbuszka/PitchWise.Lab), a .NET computer
vision pipeline, which will run as its own process and report to this service. None of this is built
here yet.

1. **Analysis after the match:** a worker runs the Lab pipeline on a recording and stores the results.
2. **Live analysis in Lab:** turn its batch modules into streaming ones. Measured on a GTX 1660 Ti, the
   full pipeline takes about 0.87 s per frame, but detection, re-identification and tracking only
   about 0.15 s. Most of the rest is pitch calibration, which needn't run on every frame.
3. **Statistics and overlays in the player:** results are sent as data and drawn by the browser, in
   sync with the video by timestamp. This also needs GPU capacity to be scheduled per stream.

## Known limitations

- **Jobs can be lost.** A worker crash loses the job it popped from Redis, and a crash between the
  database write and the push leaves a job that never starts. A reliable queue and an outbox would
  close both gaps.
- **Shared disk.** The API, worker and edge exchange files through one volume, so they can't run on
  separate machines without object storage.
- **One quality level.** MediaMTX repackages what the encoder sends, so there is no adaptive bitrate
  yet. That would be a transcoding step.
- **Key management is minimal.** Keys can't be rotated or revoked through the API yet.
- **The web console acts as one tenant.** It adds the demo key on the server, so it needs a login
  before being exposed.
- **MD5 signatures.** nginx `secure_link` requires MD5. A commercial CDN's token auth would replace it.
- **Live streams aren't recorded.** A broadcast is gone once it leaves the playlist.

## License

[MIT](LICENSE)
