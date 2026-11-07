# Switcher prototype: ffmpeg `streamselect` or GStreamer `input-selector`

**Decision: GStreamer.** With ffmpeg, losing any one camera stops the program, even a camera that
isn't on air. That rules it out, because a failed input must not stop the broadcast. GStreamer keeps the
program going in every failure tested.

This is a measurement rig. The tables below compare the two prototypes as they were when the decision was made. What
the platform runs is [`broadcast-dotnet/switcher/switcher.py`](../../broadcast-dotnet/switcher/switcher.py), measured
with the same rig under [the switcher as built](#the-switcher-as-built).

## What was tested

Three generated signals (no real cameras), published to MediaMTX 1.21.3 over RTMP. The switcher reads them
over RTSP and publishes the program back over RTMP, as the real design would. Both candidates ran in one image
on the broadcast service's base, with its distro packages: ffmpeg 6.1.1 and GStreamer 1.24.2, both encoding
with libx264 (veryfast, zerolatency, 4 Mbit/s at 720p25, 6 Mbit/s at 1080p25).

- Camera A: no B-frames, keyframe every 2 s.
- Camera B: B-frames and x264 lookahead, keyframe every 4 s. Its own encoder adds about 0.6 s, as real ones can.
- Camera C: no B-frames, keyframe every 1 s.

Every frame carries a barcode (source, frame number, checksum) and otherwise deterministic content, so the
analyser can tell which frame arrived and rebuild the frame that should have arrived. Audio has a click on
every second of the camera's clock.

Three variants were measured:

- **ffmpeg, stream timestamps** (`quick`): inputs opened as fast as ffmpeg allows. Out of the box, delays were worse.
- **ffmpeg, arrival timestamps** (`wallclock`): `-use_wallclock_as_timestamps` puts all inputs on one clock.
- **GStreamer**: `input-selector` with a black slate and silence as always-present inputs, a watchdog that
  switches to the slate when the input on air stops, and each camera as a branch that is rebuilt when it dies.

## Normal operation

10 minutes with a cut about every 2 s (241 cuts), then 1 minute of a cut about every 0.2 s (306 cuts).
The arrival-timestamps variant ran 3 minutes instead of 8, then the same burst, and its cut time covers 91 cuts.

| | ffmpeg, stream timestamps | ffmpeg, arrival timestamps | GStreamer |
|---|---|---|---|
| Cut to visible, median / p95 / max | 159 / 227 / 263 ms | 160 / 213 / 261 ms | 113 / 163 / 174 ms |
| Wrong camera after a cut | 0 frames | 0 frames | 0 frames |
| On the right camera after the burst | yes | yes | yes |
| Delay of cameras A / B / C | 1114 / 1314 / 554 ms | 588 / 1232 / 588 ms | 190 / 868 / 191 ms |
| Repeated / skipped frames within a camera | 0 / 0 of 13263 | **1770 / 1568 of 5907** | 0 / 0 of 13308 |
| Timestamp steps within 40 ±5 ms | 100% | about 100% | **59%** |
| Longest wait between frames, at a viewer | 109 ms | 133 ms | 124 ms |
| Damaged frames (bad barcode, under 25 dB) | 0 | 0 | 0 |
| Picture quality at cuts, worst / elsewhere, median | 27.3 / 29.1 dB | 27.7 / 29.0 dB | 27.6 / 29.0 dB |
| Picture vs sound from A: A / B / C | −2 / +198 / −562 ms | +12 / +656 / +15 ms | −61 / +611 / −60 ms |
| Change of that offset over the run, camera A | −0.05 ms/min | −1.61 ms/min | −0.07 ms/min |
| CPU, 720p, cores | 0.75 | 0.75 | 0.94 |
| CPU, 1080p, cores | 1.50 | not measured | 1.80 |
| Memory, 720p / 1080p | 272 / 403 MB | 277 MB / not measured | 216 / 285 MB |

Positive offset: the picture is later than the sound. Offsets include up to a few milliseconds of AAC delay.

- **Many cuts don't accumulate an offset** in any variant.
- **ffmpeg with stream timestamps** gives each camera its own start of time, set by the order and speed of
  opening inputs. Camera C ends up 0.56 s ahead of the sound, and the amount changes per start.
- **ffmpeg with arrival timestamps** lines the cameras up, but 30% of frames repeat and about as many get
  skipped, which is visible stutter.
- **GStreamer** lines the cameras up with the lowest delay. Camera B's offset comes mostly from its own encoder.

## Failures

| What happens | ffmpeg, stream timestamps | GStreamer |
|---|---|---|
| Camera not on air dies | **Program ends in 0.6 s** | No effect, longest wait 97 ms. Usable again once back |
| Camera on air dies | **Program ends in 0.4 s** | Slate, longest wait 108 ms. Usable again once back |
| Camera not on air hangs (connected, sends nothing) | **Program stops at once**, ends after 10 s | No effect, longest wait 94 ms. Usable again once back |
| Camera on air hangs | **Program stops**, ends after 10 s | 1.1 s gap, then slate until the operator cuts away |
| Camera giving the sound dies | **Program ends** | Picture continues in silence, sound back on choosing another source |
| Camera missing at start | **Won't start** | Starts. Camera usable once it appears |
| Switcher killed and restarted | 8.9 s gap | 5.2 s gap |

Without `astreamselect`, ffmpeg behaves no better: the process stays alive and sends nothing, which is
harder to detect. So the cause isn't the audio filter. ffmpeg also can't reopen an input while running, so
every loss would mean a restart. The failure scenarios were only run with stream timestamps, not arrival
timestamps, because that variant had already failed on stutter.

## Clocks that disagree

Camera B 1000 ppm slow and camera C 1000 ppm fast for 3 minutes, far more than real encoders should differ.

| Delay, first → last 30 s | A | B (slow) | C (fast) |
|---|---|---|---|
| ffmpeg, stream timestamps | 1196 → 1243 ms | 1374 → 1262 ms | 552 → 591 ms |
| ffmpeg, arrival timestamps | 556 → 558 ms | 1244 → 1247 ms | 570 → 570 ms |
| GStreamer | 189 → 189 ms | 867 → 867 ms | **194 → 337 ms** |

In GStreamer only the fast camera falls behind, about 50 ms a minute at this exaggerated rate. Cutting to and from
it left gaps of up to 287 ms in the program. Sound also moves against the picture of an off-clock camera, by
51–66 ms a minute at this rate: camera B in GStreamer, camera C with stream timestamps, both with arrival timestamps.

## What became of the open problems

1. **Uneven timestamps: solved.** The prototype had only 59% of timestamp steps within 40 ±5 ms, most others being
   multiples of an AAC frame. `flvmux` is an aggregator that stops waiting for a late stream when its latency runs
   out. That latency was worked out when only the slate existed, so it wrote sound ahead of pictures, and then pulled
   each picture's time up to the last sound written. With `flvmux latency=300ms`, 97.4% of steps are in range over the
   same 11 minutes, and what is left is one step per cut (548 for 547 cuts). It adds no delay. A camera's delay
   stayed at 193 ms.
2. **Picture ahead of the sound: accepted.** Between 52 and 77 ms across runs, steady within a run. Sound arriving
   this much later than the picture is well inside what viewers tolerate, so nothing corrects it.
3. **Memory:** LONG_RUN_RESULT
4. **A program around it: accepted.** About 300 lines of Python, run and supervised by the .NET broadcast service.

## The switcher as built

`run.py product` drives `broadcast-dotnet/switcher/switcher.py` over its real protocol, with the same scenarios. It
has `flvmux latency=300ms`, and `scenecut=0` so that keyframes come every 2 s exactly. With x264's scene detection,
they came at cuts too, and MediaMTX's HLS segments grew to 4 s. Quality at cuts didn't suffer: 28.3 dB at worst
against 27.6 dB, on this test picture.

| | Result |
|---|---|
| Cut to visible, median / p95 / max | 116 / 160 / 166 ms in the rig. 130 / 185 / 198 ms from the API call, through the whole stack |
| Delay of cameras A / B / C | 194 / 872 / 195 ms |
| Timestamp steps | Only cuts differ: 31 of 1541 over 60 s |
| Repeated / skipped / damaged frames | 0 |
| Camera not on air dies or hangs | No effect, longest wait 101 ms |
| Camera on air dies | Slate, longest wait 101 ms |
| Camera on air hangs | 1056 ms gap, then the slate |
| Camera giving the sound dies | Picture carries on, longest wait 87 ms |
| Camera missing at start | Starts, longest wait 91 ms |
| Killed and restarted by hand | 3.5 s gap |
| CPU, 720p / 1080p | About 1.0 / 1.77 cores |

## Not checked

Real cameras, phones, OBS or vMix. A real network: every loss here was a killed or frozen process on one machine.
Sending the program to YouTube or Twitch. Keeping ffmpeg alive through a relay per camera that never ends. Authentication on MediaMTX.

## Running it

```bash
docker build -t pw-switcher-rig prototypes/switcher
prototypes/switcher/measure.sh ~/.cache/pitchwise/switcher-results/run1   # about an hour
```

Each scenario leaves `summary.json`, `analysis.txt` and raw data in `<results>/<variant>/<candidate>/<scenario>/`.
The rig uses at most 7 CPUs and 6 GB, and a private Docker network, so it doesn't clash with a running stack.
