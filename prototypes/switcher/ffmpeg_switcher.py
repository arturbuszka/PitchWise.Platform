#!/usr/bin/env python3
"""Candidate 1: one ffmpeg, streamselect and astreamselect, commands through its stdin."""
import argparse, os, subprocess, sys, time

# default: what ffmpeg does on its own. quick: open inputs as fast as it can.
# wallclock: put every input on one clock, the moment each packet arrives.
# <mode>_copyaudio: leave astreamselect out, to tell which of the two filters ends the program.
MODE = os.environ.get("FF_MODE", "wallclock")
COPY_AUDIO = MODE.endswith("_copyaudio")
MODE = MODE.removesuffix("_copyaudio")
EPOCH = time.time()

p = argparse.ArgumentParser()
p.add_argument("--inputs", required=True, help="comma separated RTSP URLs")
p.add_argument("--output", required=True)
p.add_argument("--size", default="1280x720")
p.add_argument("--bitrate", default="4M")
a = p.parse_args()

urls = a.inputs.split(",")
w, h = a.size.split("x")
args = ["ffmpeg", "-hide_banner", "-loglevel", "warning", "-nostats"]
for u in urls:
    args += ["-rtsp_transport", "tcp", "-timeout", "5000000", "-fflags", "nobuffer", "-thread_queue_size", "64"]
    if MODE in ("quick", "wallclock", "wallclock_nofps"):
        args += ["-analyzeduration", "0", "-probesize", "65536"]
    if MODE in ("wallclock", "wallclock_nofps"):
        args += ["-use_wallclock_as_timestamps", "1"]
    args += ["-i", u]
if MODE in ("wallclock", "wallclock_nofps"):
    args += ["-copyts"]
vt = f"setpts=PTS-{EPOCH:.3f}/TB," if MODE in ("wallclock", "wallclock_nofps") else ""
at = f"asetpts=PTS-{EPOCH:.3f}/TB," if MODE in ("wallclock", "wallclock_nofps") else ""
chains = []
for i in range(len(urls)):
    rate = "" if MODE == "wallclock_nofps" else ",fps=25"
    chains.append(f"[{i}:v]{vt}scale={w}:{h},format=yuv420p{rate}[v{i}]")
    if not COPY_AUDIO:
        chains.append(f"[{i}:a]{at}aresample=48000:async=1,aformat=sample_rates=48000:channel_layouts=stereo[a{i}]")
vin = "".join(f"[v{i}]" for i in range(len(urls)))
ain = "".join(f"[a{i}]" for i in range(len(urls)))
chains.append(f"{vin}streamselect@v=inputs={len(urls)}:map=0[vout]")
if not COPY_AUDIO:
    chains.append(f"{ain}astreamselect@a=inputs={len(urls)}:map=0[aout]")
args += ["-filter_complex", ";".join(chains), "-map", "[vout]", "-map", "0:a" if COPY_AUDIO else "[aout]",
         "-fps_mode", "cfr", "-r", "25",
         "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-g", "50",
         "-b:v", a.bitrate, "-maxrate", a.bitrate, "-bufsize", a.bitrate,
         *(["-c:a", "copy"] if COPY_AUDIO else ["-c:a", "aac", "-b:a", "128k"]), "-f", "flv", a.output]
ff = subprocess.Popen(args, stdin=subprocess.PIPE)
for line in sys.stdin:
    parts = line.split()
    if parts and parts[0] == "cut":
        ff.stdin.write(f"cstreamselect@v -1 map {parts[1]}\n".encode())
        ff.stdin.flush()
    elif parts and parts[0] == "audio":
        ff.stdin.write(f"castreamselect@a -1 map {parts[1]}\n".encode())
        ff.stdin.flush()
ff.wait()
