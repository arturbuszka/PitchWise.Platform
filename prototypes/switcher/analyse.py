#!/usr/bin/env python3
"""Turns one run's raw data into numbers: what a viewer received, and what the recorded program contains."""
import glob, json, os, subprocess, sys
import numpy as np
from common import *

out = sys.argv[1]
events = [json.loads(l) for l in open(f"{out}/events.jsonl")]
rig = next(e for e in events if e["kind"] == "rig")
T0, PPM = rig["t0"], {int(k): v for k, v in rig["ppm"].items()}
W, H = map(int, rig["size"].split("x"))
start = next(e["t"] for e in events if e["kind"] == "start")
end = next((e["t"] for e in events if e["kind"] == "end"), None)
result = {"candidate": rig["candidate"], "scenario": rig["scenario"]}


def stats(values, unit=1):
    if not len(values):
        return None
    v = np.asarray(values) * unit
    return {"n": len(v), "median": round(float(np.median(v)), 1), "p95": round(float(np.percentile(v, 95)), 1),
            "min": round(float(v.min()), 1), "max": round(float(v.max()), 1)}


def capture(source, n):
    return T0 + n / (FPS * (1 + PPM.get(source, 0) / 1e6))


# ---- What a viewer got, live
rows = []
for line in open(f"{out}/probe.csv"):
    t, s, n = line.strip().split(",")
    rows.append((float(t), int(s) if s else -1, int(n) if n else -1))
arr = np.array(rows) if rows else np.zeros((0, 3))


def segments():
    """Contiguous stretches: a source advancing, a source frozen, no barcode (black or damaged), or nothing."""
    segs, cur = [], None
    for i, (t, s, n) in enumerate(rows):
        prev = rows[i - 1] if i else None
        if s < 0:
            state = "nostamp"
        elif prev and prev[1] == s and prev[2] == n:
            state = "frozen"
        else:
            state = "live"
        gap = t - prev[0] if prev else 0
        if cur and (cur["s"], cur["state"]) == (s, state) and gap < 0.5:
            cur["to"] = t
        else:
            if cur and gap >= 0.5:
                segs.append(cur)
                segs.append({"s": None, "state": "nothing", "from": prev[0], "to": t})
                cur = None
            elif cur:
                segs.append(cur)
            cur = {"s": s, "state": state, "from": t, "to": t}
    if cur:
        segs.append(cur)
    return segs


segs = segments()
result["timeline"] = [
    f"{x['from'] - start:7.2f} {x['to'] - x['from']:6.2f}s {x['state']} {x['s'] if x['s'] is not None else ''}"
    for x in segs if x["to"] - x["from"] >= 0.2 or x["state"] != "live"]
result["timeline"] = result["timeline"][:400]

cuts = [e for e in events if e["kind"] == "cut"]
burst = next((e["t"] for e in events if e.get("name") == "burst"), None)
lat, flash, missed = [], 0, 0
for i, c in enumerate(cuts):
    if burst and c["t"] >= burst:
        continue
    upto = cuts[i + 1]["t"] if i + 1 < len(cuts) else c["t"] + 5
    idx = np.nonzero((arr[:, 0] >= c["t"]) & (arr[:, 0] < upto + 3) & (arr[:, 1] == c["source"]))[0] if len(arr) else []
    if not len(idx):
        missed += 1
        continue
    first = idx[0]
    lat.append(arr[first, 0] - c["t"])
    # Anything but the chosen camera after its first frame, up to the next cut's own delay, is a flash of the wrong one.
    window = (arr[:, 0] > arr[first, 0]) & (arr[:, 0] < upto)
    flash += int(np.sum(window & (arr[:, 1] != c["source"])))
result["cut_ms"] = stats(lat, 1000)
result["cuts"] = {"sent": len(cuts), "not_seen": missed, "wrong_frames_after_cut": flash}
if burst:
    final = next(e for e in reversed(cuts))
    result["after_burst_on_air"] = int(arr[-1, 1]) if len(arr) else None
    result["after_burst_expected"] = final["source"]

if len(arr):
    good = arr[arr[:, 1] >= 0]
    delay = np.array([t - capture(int(s), int(n)) for t, s, n in good])
    per = {}
    for s in range(3):
        mask = good[:, 1] == s
        per[s] = stats(delay[mask], 1000)
    result["delay_ms_by_source"] = per
    a0 = good[:, 0] < good[0, 0] + 60
    a1 = good[:, 0] > good[-1, 0] - 60
    result["delay_ms_first_vs_last_minute"] = [round(float(np.median(delay[a0])) * 1000), round(float(np.median(delay[a1])) * 1000)]
    gaps = np.diff(arr[:, 0])
    result["arrival_gap_ms"] = {"max": round(float(gaps.max()) * 1000), "over_200": int(np.sum(gaps > .2)), "over_500": int(np.sum(gaps > .5))}
    result["frames_without_barcode"] = int(np.sum(arr[:, 1] < 0))
    same = (np.diff(arr[:, 2]) == 0) & (np.diff(arr[:, 1]) == 0) & (arr[1:, 1] >= 0)
    result["repeated_frames_live"] = int(np.sum(same))

# ---- CPU and memory of the switcher alone, once it has settled
cpu = np.loadtxt(f"{out}/cpu.csv", delimiter=",", ndmin=2)
phase = next((e["t"] for e in events if e.get("name") == "measure"), start)
if len(cpu):
    w = cpu[(cpu[:, 0] > phase + 3) & (cpu[:, 0] < (end or cpu[-1, 0]))]
    if len(w) > 2:
        d = np.diff(w[:, 1]) / np.diff(w[:, 0])
        d = d[d >= 0]
        result["cpu_cores"] = {"median": round(float(np.median(d)), 2), "max": round(float(d.max()), 2)}
        result["memory_mb"] = {"first": round(float(w[0, 2])), "last": round(float(w[-1, 2])), "max": round(float(w[:, 2].max()))}

# ---- The recorded program, frame by frame
recs = sorted(glob.glob(f"{out}/rec_*.mkv"), key=os.path.getsize)
if recs and os.environ.get("NO_FILE") != "1":
    rec = recs[-1]
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v", "-show_entries", "packet=pts_time",
                            "-of", "csv=p=0", rec], capture_output=True, text=True).stdout
    pts = np.sort(np.array([float(x.strip(",")) for x in probe.split() if x.strip(",") not in ("", "N/A")]))
    dec = subprocess.Popen(["ffmpeg", "-v", "error", "-nostdin", "-copyts", "-i", rec, "-an", "-fps_mode", "passthrough",
                            "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"], stdout=subprocess.PIPE)
    pics = {i: Picture(i, W, H) for i in range(3)}
    sh = strip_height(H)
    src, num, psnr = [], [], []
    while True:
        buf = dec.stdout.read(W * H)
        if len(buf) < W * H:
            break
        y = np.frombuffer(buf, np.uint8).reshape(H, W)
        s = read_stamp(y[:sh])
        if s is None or s[0] not in pics:
            src.append(-1); num.append(-1); psnr.append(0.0)
            continue
        ref = pics[s[0]].luma(s[1])
        mse = np.mean((y[sh::2, ::2].astype(np.float32) - ref[sh::2, ::2].astype(np.float32)) ** 2)
        src.append(s[0]); num.append(s[1]); psnr.append(99.0 if mse == 0 else 10 * np.log10(255 ** 2 / mse))
    dec.wait()
    src, num, psnr = np.array(src), np.array(num), np.array(psnr)
    m = min(len(pts), len(src))
    src, num, psnr, pts = src[:m], num[:m], psnr[:m], pts[:m]
    f = {"frames": int(m), "seconds": round(float(pts[-1] - pts[0]), 2), "fps": round(float((m - 1) / (pts[-1] - pts[0])), 4),
         "without_barcode": int(np.sum(src < 0))}
    dp = np.diff(pts)
    f["pts_step_ms"] = {"min": round(float(dp.min()) * 1000, 1), "max": round(float(dp.max()) * 1000, 1), "not_40": int(np.sum(np.abs(dp - .04) > .002))}
    change = np.nonzero((src[1:] != src[:-1]) & (src[1:] >= 0) & (src[:-1] >= 0))[0] + 1
    f["cuts_in_file"] = len(change)
    same = (src[1:] == src[:-1]) & (src[1:] >= 0)
    dn = np.diff(num)[same]
    f["within_camera"] = {"steps": int(len(dn)), "repeats": int(np.sum(dn == 0)), "skips": int(np.sum(dn > 1)),
                          "backwards": int(np.sum(dn < 0)), "largest_skip": int(dn.max()) if len(dn) else 0}
    # How far time jumps when the picture changes camera. With a perfect switcher, one frame.
    jump = np.array([(capture(int(src[i]), int(num[i])) - capture(int(src[i - 1]), int(num[i - 1]))) for i in change])
    f["time_jump_at_cut_ms"] = stats(jump, 1000)
    mask = np.zeros(m, bool)
    for i in change:
        mask[i:i + 3] = True
    valid = src >= 0
    f["psnr_at_cuts"] = stats(psnr[mask & valid])
    f["psnr_elsewhere"] = stats(psnr[~mask & valid])
    f["frames_below_25db"] = int(np.sum(psnr[valid] < 25))
    result["file"] = f

    # ---- Sound against picture
    aprobe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "packet=pts_time", "-of", "csv=p=0", rec],
                            capture_output=True, text=True).stdout.split()
    apts = np.array([float(x.strip(",")) for x in aprobe if x.strip(",") not in ("", "N/A")])
    if len(apts):
        raw = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-copyts", "-i", rec, "-vn", "-ac", "1", "-ar", str(RATE),
                              "-af", "aresample=async=1", "-f", "s16le", "pipe:1"], capture_output=True).stdout
        x = np.frombuffer(raw, np.int16).astype(np.float32)
        loud = np.abs(x) > 2500
        idx = np.nonzero(loud)[0]
        clicks = []
        last = -RATE
        for i in idx:
            if i - last > RATE * 0.3:
                seg = x[i:i + int(RATE * CLICK)]
                if len(seg) > 200:
                    spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg)), 8192))
                    fr = np.fft.rfftfreq(8192, 1 / RATE)
                    tone = fr[int(np.argmax(spec))]
                    j = int(np.argmin([abs(tone - t) for t in TONES]))
                    clicks.append((apts[0] + i / RATE, j))
            last = i
        cl = np.array(clicks) if clicks else np.zeros((0, 2))
        steps = np.diff(cl[:, 0]) if len(cl) > 1 else np.array([])
        offs = {0: [], 1: [], 2: []}
        when = {0: [], 1: [], 2: []}
        for i in np.nonzero((num >= 0) & (num % FPS == 0))[0]:
            k = num[i] // FPS
            same_tone = cl[cl[:, 1] == k % 4, 0] if len(cl) else np.array([])
            if len(same_tone):
                d = pts[i] - same_tone[np.argmin(np.abs(same_tone - pts[i]))]
                if abs(d) < 2:
                    offs[int(src[i])].append(d)
                    when[int(src[i])].append(pts[i])
        av = {"clicks": len(cl), "click_step_ms": stats(steps, 1000) if len(steps) else None}
        for s in range(3):
            if len(offs[s]) > 3:
                o, t = np.array(offs[s]), np.array(when[s])
                slope = np.polyfit(t - t[0], o, 1)[0] * 60 * 1000 if t[-1] - t[0] > 20 else None
                av[str(s)] = {**stats(o, 1000), "ms_per_minute": round(float(slope), 2) if slope is not None else None}
        result["sound_vs_picture_ms"] = av

json.dump(result, open(f"{out}/summary.json", "w"), indent=1, default=str)
tl = result.pop("timeline")
print(json.dumps(result, indent=1, default=str))
if os.environ.get("TIMELINE") == "1":
    print("\n".join(tl))
