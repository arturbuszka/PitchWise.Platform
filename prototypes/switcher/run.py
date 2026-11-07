#!/usr/bin/env python3
"""Runs one scenario against one candidate and writes what happened to <out>/ for analyse.py."""
import argparse, json, os, random, signal, socket, subprocess, sys, threading, time
import numpy as np
from common import *

p = argparse.ArgumentParser()
p.add_argument("candidate", choices=["ffmpeg", "gst", "product"])
p.add_argument("scenario")
p.add_argument("out")
p.add_argument("--seconds", type=int, default=0, help="shorten or lengthen a timed scenario")
a = p.parse_args()
os.makedirs(a.out, exist_ok=True)

RTMP, RTSP = "rtmp://pw-switcher-mtx:11935", "rtsp://pw-switcher-mtx:18554"
HERE = os.path.dirname(os.path.abspath(__file__))
SIZE, BITRATE = ("1920x1080", "6M") if a.scenario == "cpu1080" else ("1280x720", "4M")
W, H = map(int, SIZE.split("x"))
PPM = {1: -1000, 2: 1000} if a.scenario == "drift" else {}
# Different cameras behave differently: B has B-frames and a long keyframe interval, C a short one.
# At 1080p the cameras use a faster preset, or generating them would fall behind and spoil the measurement.
PRESET = "ultrafast" if W > 1280 else "veryfast"
X264 = {0: f"-preset {PRESET} -tune zerolatency -g 50",
        1: f"-preset {PRESET} -bf 2 -g 100",
        2: f"-preset {PRESET} -tune zerolatency -g 25"}
T0 = time.time() + 3
events = open(f"{a.out}/events.jsonl", "w")


def event(kind, **kw):
    events.write(json.dumps({"t": time.time(), "kind": kind, **kw}) + "\n")
    events.flush()
    print(f"{time.time() - T0:8.2f} {kind} {kw}", flush=True)


event("rig", t0=T0, ppm=PPM, size=SIZE, scenario=a.scenario, candidate=a.candidate)
cams = {}


def cam_start(i):
    cams[i] = subprocess.Popen(
        [sys.executable, f"{HERE}/source.py", str(i), "--t0", str(T0), "--ppm", str(PPM.get(i, 0)),
         "--size", SIZE, "--bitrate", BITRATE, "--url", f"{RTMP}/cam/{i}", "--x264", X264[i]],
        start_new_session=True, stdin=subprocess.DEVNULL)
    event("camera_start", source=i)


def cam_signal(i, sig, name):
    os.killpg(cams[i].pid, sig)
    event(name, source=i)


switcher = None


def product_send(**state):
    switcher.stdin.write((json.dumps(state) + "\n").encode())
    switcher.stdin.flush()


def product_start():
    """The switcher the broadcast service runs, in place of the prototype, talking the way the service does."""
    global switcher
    log = open(f"{a.out}/switcher.log", "a")
    switcher = subprocess.Popen(
        [sys.executable, "/product/switcher.py"], stdin=subprocess.PIPE, stderr=log, stdout=log, start_new_session=True)
    product_send(width=W, height=H, video_kbps=int(BITRATE.rstrip("M")) * 1000, rtmp_host="pw-switcher-mtx", rtmp_port=11935,
                 rtmp_application="prog", rtmp_stream="main", rtsp_user="", rtsp_password="")
    product_send(inputs=[{"id": str(i), "url": f"{RTSP}/cam/{i}"} for i in range(3)], video="0", audio="0")
    event("switcher_start")


def switcher_start():
    global switcher
    if a.candidate == "product":
        return product_start()
    script = {"ffmpeg": "ffmpeg_switcher.py", "gst": "gst_switcher.py"}[a.candidate]
    urls = ",".join(f"{RTSP}/cam/{i}" for i in range(3))
    log = open(f"{a.out}/switcher.log", "a")
    switcher = subprocess.Popen(
        [sys.executable, f"{HERE}/{script}", "--inputs", urls, "--output", f"{RTMP}/prog/main",
         "--size", SIZE, "--bitrate", BITRATE],
        stdin=subprocess.PIPE, stderr=log, stdout=log, start_new_session=True)
    event("switcher_start")


def cut(i):
    event("cut", source=i)
    if a.candidate == "product":
        return product_send(video=str(i))
    switcher.stdin.write(f"cut {i}\n".encode())
    switcher.stdin.flush()


def audio(i):
    event("audio", source=i)
    if a.candidate == "product":
        return product_send(audio=str(i))
    switcher.stdin.write(f"audio {i}\n".encode())
    switcher.stdin.flush()


stop = threading.Event()


def probe():
    """What a viewer of the program gets, stamped with the moment it arrived. Reconnects like a viewer."""
    sh = strip_height(H)
    with open(f"{a.out}/probe.csv", "w") as f:
        while not stop.is_set():
            ff = subprocess.Popen(
                ["ffmpeg", "-hide_banner", "-loglevel", "quiet", "-nostdin", "-rtsp_transport", "tcp",
                 "-fflags", "nobuffer", "-flags", "low_delay", "-i", f"{RTSP}/prog/main", "-an",
                 "-vf", f"crop={W}:{sh}:0:0,format=gray", "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"],
                stdout=subprocess.PIPE)
            while not stop.is_set():
                buf = ff.stdout.read(W * sh)
                if len(buf) < W * sh:
                    break
                s = read_stamp(np.frombuffer(buf, np.uint8).reshape(sh, W))
                f.write(f"{time.time():.3f},{s[0] if s else ''},{s[1] if s else ''}\n")
                f.flush()  # A stalled program must not cost the lines before the stall.
            ff.kill(); ff.wait()
            time.sleep(0.2)


def recorder():
    k = 0
    while not stop.is_set():
        path = f"{a.out}/rec_{k}.mkv"
        ff = subprocess.Popen(
            ["ffmpeg", "-hide_banner", "-loglevel", "quiet", "-nostdin", "-rtsp_transport", "tcp",
             "-i", f"{RTSP}/prog/main", "-c", "copy", "-copyts", "-f", "matroska", path])
        while ff.poll() is None and not stop.is_set():
            time.sleep(0.2)
        if ff.poll() is None:
            ff.send_signal(signal.SIGINT)
            try:
                ff.wait(5)
            except subprocess.TimeoutExpired:
                ff.kill()
        if os.path.exists(path) and os.path.getsize(path) > 0:
            k += 1
        time.sleep(0.2)


def tree(root):
    kids = {}
    for d in os.listdir("/proc"):
        if d.isdigit():
            try:
                parts = open(f"/proc/{d}/stat").read().rsplit(")", 1)[1].split()
                kids.setdefault(int(parts[1]), []).append(int(d))
            except OSError:
                pass
    out, todo = [], [root]
    while todo:
        x = todo.pop()
        out.append(x)
        todo += kids.get(x, [])
    return out


def cpu_sampler():
    hz = os.sysconf("SC_CLK_TCK")
    with open(f"{a.out}/cpu.csv", "w") as f:
        while not stop.is_set():
            ticks = rss = 0
            if switcher:
                for pid in tree(switcher.pid):
                    try:
                        parts = open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()
                        ticks += int(parts[11]) + int(parts[12])
                        rss += int(parts[21]) * 4096
                    except OSError:
                        pass
            f.write(f"{time.time():.2f},{ticks / hz:.2f},{rss / 1e6:.1f}\n")
            f.flush()
            time.sleep(2)


def wait(seconds):
    time.sleep(seconds)


def cycle(rng, every, seconds, cur=0):
    """Cuts to a random other camera, at random moments around the given pace."""
    end = time.time() + seconds
    while time.time() < end:
        cur = rng.choice([i for i in range(3) if i != cur])
        cut(cur)
        wait(rng.uniform(every * 0.75, every * 1.25))
    return cur


rng = random.Random(7)


def published(i):
    """Asks MediaMTX over plain RTSP whether a camera's stream is there yet."""
    try:
        with socket.create_connection(("pw-switcher-mtx", 18554), 2) as c:
            c.sendall(f"DESCRIBE {RTSP}/cam/{i} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n".encode())
            return b" 200 " in c.recv(200)
    except OSError:
        return False


def wait_for_cameras(present):
    end = time.time() + 40
    while time.time() < end and not all(published(i) for i in present):
        time.sleep(0.25)
    event("cameras_ready", present=[i for i in present if published(i)])


def scenario():
    s = a.scenario
    if s == "smoke":
        cycle(rng, 2, a.seconds or 20)
    elif s == "clean":
        event("phase", name="normal")
        cur = cycle(rng, 2.0, a.seconds or 480)
        event("phase", name="burst")
        cycle(rng, 0.2, 60, cur)
        cut(0); wait(10)
    elif s == "long":                  # a match and a bit: memory, and whether anything drifts
        cycle(rng, 30, a.seconds or 7200)
    elif s == "drift":
        cycle(rng, 5, a.seconds or 180)
    elif s == "cpu1080":
        wait(20)
        event("phase", name="measure")
        cycle(rng, 5, a.seconds or 60)
    elif s == "loss_offair":           # camera B dies while A is on air
        cycle(rng, 2, 10); cut(0); wait(5)
        cam_signal(1, signal.SIGKILL, "camera_kill"); wait(15)
        cam_start(1); wait(15); cut(1); wait(10); cut(0); wait(5)
    elif s == "loss_onair":            # camera C dies on air
        cycle(rng, 2, 10); cut(2); wait(5)
        cam_signal(2, signal.SIGKILL, "camera_kill"); wait(15)
        cut(0); wait(5); cam_start(2); wait(15); cut(2); wait(10)
    elif s == "hang_offair":           # camera B stays connected and stops sending
        cycle(rng, 2, 10); cut(0); wait(5)
        cam_signal(1, signal.SIGSTOP, "camera_freeze"); wait(25)
        cam_signal(1, signal.SIGKILL, "camera_kill"); wait(2)
        cam_start(1); wait(15); cut(1); wait(10)
    elif s == "hang_onair":            # the camera on air stays connected and stops sending
        cycle(rng, 2, 10); cut(2); wait(5)
        cam_signal(2, signal.SIGSTOP, "camera_freeze"); wait(8); cut(0); wait(17)
        cam_signal(2, signal.SIGKILL, "camera_kill"); wait(2)
        cam_start(2); wait(15); cut(2); wait(10)
    elif s == "loss_audio":            # the camera that gives the sound dies
        cut(1); wait(10)
        cam_signal(0, signal.SIGKILL, "camera_kill"); wait(15); audio(1); wait(10)
        cam_start(0); wait(15); audio(0); wait(10)
    elif s == "restart":               # what restarting the switcher itself costs
        cycle(rng, 2, 10); cut(0); wait(5)
        os.killpg(switcher.pid, signal.SIGKILL); event("switcher_kill")
        switcher_start(); wait(20)
    else:
        sys.exit(f"unknown scenario {s}")


missing_at_start = a.scenario == "start_missing"
for i in range(3):
    if not (missing_at_start and i == 1):
        cam_start(i)
wait(max(0, T0 - time.time()))
wait_for_cameras([i for i in range(3) if not (missing_at_start and i == 1)])
wait(2)
threading.Thread(target=cpu_sampler, daemon=True).start()
switcher_start()
threads = [threading.Thread(target=f, daemon=True) for f in (probe, recorder)]
for t in threads:
    t.start()
wait(8)
event("start")
try:
    if missing_at_start:               # camera B isn't there when the switcher starts
        wait(10)
        event("switcher_state", running=switcher.poll() is None)
        cam_start(1); wait(10)
        if switcher.poll() is None:
            cut(1); wait(10)
    else:
        scenario()
finally:
    event("end", switcher_running=switcher.poll() is None)
    stop.set()
    for t in threads:
        t.join(8)
    for proc in [switcher, *cams.values()]:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
