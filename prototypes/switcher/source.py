#!/usr/bin/env python3
"""One camera: generates frames and audio in real time and publishes them to MediaMTX over RTMP.

Frame n is due at T0 + n / (fps * (1 + ppm/1e6)), but carries the timestamp n / fps: a camera whose
clock runs fast or slow believes its own timestamps, which is exactly how real encoders drift.
"""
import argparse, os, subprocess, sys, threading, time
from common import *

p = argparse.ArgumentParser()
p.add_argument("source", type=int)
p.add_argument("--t0", type=float, required=True)
p.add_argument("--ppm", type=float, default=0)
p.add_argument("--size", default="1280x720")
p.add_argument("--bitrate", default="4M")
p.add_argument("--url", required=True)
p.add_argument("--x264", default="-preset veryfast -tune zerolatency -g 50")
a = p.parse_args()

w, h = map(int, a.size.split("x"))
pic = Picture(a.source, w, h)
rate = FPS * (1 + a.ppm / 1e6)
# A restarted camera carries on with the frame the clock says is due, the way a real one has moved on.
n = max(0, int((time.time() - a.t0) * rate) + 1)

ar, aw = os.pipe()
os.set_inheritable(ar, True)
ff = subprocess.Popen(
    ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin",
     "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", a.size, "-r", str(FPS), "-i", "pipe:0",
     "-f", "s16le", "-ar", str(RATE), "-ac", "2", "-i", f"pipe:{ar}",
     "-c:v", "libx264", *a.x264.split(), "-threads", "3", "-b:v", a.bitrate, "-maxrate", a.bitrate, "-bufsize", a.bitrate,
     "-c:a", "aac", "-b:a", "128k", "-f", "flv", a.url],
    stdin=subprocess.PIPE, close_fds=False)
os.close(ar)


def video():
    k = n
    while True:
        wait = a.t0 + k / rate - time.time()
        if wait > 0:
            time.sleep(wait)
        ff.stdin.write(pic.frame(k))
        k += 1


def audio():
    # On its own thread: ffmpeg opens its inputs one at a time and would otherwise wait for us forever.
    k = n
    while True:
        wait = a.t0 + k / rate - time.time()
        if wait > 0:
            time.sleep(wait)
        os.write(aw, audio_chunk(a.source, k))
        k += 1


def guard(work):
    try:
        work()
    except (BrokenPipeError, OSError):
        os._exit(1)


threading.Thread(target=guard, args=(audio,), daemon=True).start()
guard(video)
