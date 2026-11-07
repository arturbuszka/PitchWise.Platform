#!/usr/bin/env python3
"""Candidate 2: GStreamer input-selector, with a small program around it, because switching needs code.

Each camera is a branch that can die and be rebuilt on its own. A black test picture and silence are
always-present inputs, so the encoder keeps being fed when the camera on air is gone.
"""
import argparse, sys, threading, time
import gi
gi.require_version("Gst", "1.0")
from gi.repository import Gst, GLib

p = argparse.ArgumentParser()
p.add_argument("--inputs", required=True)
p.add_argument("--output", required=True)
p.add_argument("--size", default="1280x720")
p.add_argument("--bitrate", default="4M")
a = p.parse_args()
Gst.init(None)
w, h = a.size.split("x")
kbps = int(a.bitrate.rstrip("M")) * 1000
urls = a.inputs.split(",")
VCAPS = f"video/x-raw,format=I420,width={w},height={h},framerate=25/1,pixel-aspect-ratio=1/1"
STALL = 1.0  # seconds without a picture before the slate takes over
ACAPS = "audio/x-raw,format=S16LE,rate=48000,channels=2,layout=interleaved"


def log(msg):
    print(f"[gst {time.time():.3f}] {msg}", file=sys.stderr, flush=True)


pipe = Gst.parse_launch(
    f"input-selector name=vsel sync-streams=true ! identity single-segment=true ! queue max-size-buffers=2 ! "
    f"x264enc tune=zerolatency speed-preset=veryfast bitrate={kbps} key-int-max=50 ! h264parse ! "
    f"flvmux name=mux streamable=true ! rtmp2sink location={a.output} sync=false "
    f"input-selector name=asel sync-streams=true ! identity single-segment=true ! queue ! audioconvert ! avenc_aac bitrate=128000 ! aacparse ! mux. "
    f"videotestsrc is-live=true pattern=black ! {VCAPS} ! vsel.sink_100 "
    f"audiotestsrc is-live=true wave=silence ! {ACAPS} ! asel.sink_100")
vsel, asel = pipe.get_by_name("vsel"), pipe.get_by_name("asel")
vpads, apads = {"slate": vsel.get_static_pad("sink_100")}, {"slate": asel.get_static_pad("sink_100")}


class Branch:
    """One camera. Its end-of-stream must not reach the selector, or it would end the whole program."""

    def __init__(self, i):
        self.i, self.elements, self.alive, self.rebuilding = i, [], False, False
        self.seen = {"video": 0.0, "audio": 0.0}
        self.counts = {"video": -1, "audio": -1}
        self.rates = {}
        self.vpad = vsel.request_pad_simple("sink_%u")
        self.apad = asel.request_pad_simple("sink_%u")
        vpads[i], apads[i] = self.vpad, self.apad
        self.build()

    def build(self):
        s = Gst.ElementFactory.make("rtspsrc", f"src{self.i}")
        s.set_property("location", urls[self.i])
        s.set_property("protocols", "tcp")
        s.set_property("latency", 200)
        s.set_property("timeout", 3000000)
        s.set_property("tcp-timeout", 3000000)
        s.connect("pad-added", self.on_pad)
        self.src = s
        self.elements = [s]
        pipe.add(s)
        s.sync_state_with_parent()

    def on_pad(self, src, pad):
        caps = pad.get_current_caps() or pad.query_caps(None)
        media = caps.get_structure(0).get_string("media")
        if media == "video":
            desc = f"rtph264depay ! h264parse ! avdec_h264 ! videoconvert ! videoscale ! videorate name=rate ! {VCAPS} ! queue max-size-buffers=4 leaky=downstream"
            target = self.vpad
        elif media == "audio":
            desc = f"rtpmp4gdepay ! aacparse ! avdec_aac ! audioconvert ! audioresample ! audiorate name=rate ! {ACAPS} ! queue leaky=downstream"
            target = self.apad
        else:
            return
        bin_ = Gst.parse_bin_from_description(desc, True)
        pipe.add(bin_)
        self.elements.append(bin_)
        self.rates[media] = bin_.get_by_name("rate")
        out = bin_.get_static_pad("src")
        out.add_probe(Gst.PadProbeType.EVENT_DOWNSTREAM, self.on_event)
        out.link(target)
        bin_.sync_state_with_parent()
        pad.link(bin_.get_static_pad("sink"))
        self.alive = True
        log(f"input {self.i} {media} connected")

    def check(self, now):
        """Reads videorate's and audiorate's own counters. Doing this per buffer in Python disturbed timing."""
        for media, rate in self.rates.items():
            count = rate.get_property("in")
            if count != self.counts[media]:
                self.counts[media], self.seen[media] = count, now

    def on_event(self, pad, info):
        if info.get_event().type == Gst.EventType.EOS:
            GLib.idle_add(self.lost, "end of stream")
            return Gst.PadProbeReturn.DROP
        return Gst.PadProbeReturn.OK

    def lost(self, why):
        if self.rebuilding:
            return False
        self.rebuilding, self.alive = True, False
        log(f"input {self.i} lost: {why}")
        watchdog()
        threading.Thread(target=self.teardown, daemon=True).start()
        return False

    def teardown(self):
        for e in self.elements:
            for pad in e.pads:
                peer = pad.get_peer()
                if peer and peer.get_parent_element() in (vsel, asel):
                    pad.unlink(peer)
            e.set_state(Gst.State.NULL)
            pipe.remove(e)
        self.elements, self.rates = [], {}
        self.counts = {"video": -1, "audio": -1}
        GLib.timeout_add(1000, self.retry)

    def retry(self):
        self.rebuilding = False
        self.build()
        return False


# Right after a cut the new camera may offer a frame no later than the last one sent. Drop it, so that
# time in the program only moves forward.
MIN_STEP = 20 * Gst.MSECOND
last_time = [-MIN_STEP]


def forward_only(pad, info):
    buf = info.get_buffer()
    seg = pad.get_sticky_event(Gst.EventType.SEGMENT, 0)
    if seg is None or buf.pts == Gst.CLOCK_TIME_NONE:
        return Gst.PadProbeReturn.OK
    t = seg.parse_segment().to_running_time(Gst.Format.TIME, buf.pts)
    if t < last_time[0] + MIN_STEP:
        return Gst.PadProbeReturn.DROP
    last_time[0] = t
    return Gst.PadProbeReturn.OK


vsel.get_static_pad("src").add_probe(Gst.PadProbeType.BUFFER, forward_only)
active = {"video": 0, "audio": 0}
on_air = {"video": "slate", "audio": "slate"}
branches = []
vsel.set_property("active-pad", vpads["slate"])
asel.set_property("active-pad", apads["slate"])


def add_cameras():
    # Only once the slate is on the way out, so that a camera that isn't there can't hold up the program.
    branches.extend(Branch(i) for i in range(len(urls)))
    return False


def watchdog():
    """A camera can stay connected and stop sending, and no error tells us. Its pictures simply stop."""
    now = time.monotonic()
    for b in branches:
        b.check(now)
    for kind, sel, pads in (("video", vsel, vpads), ("audio", asel, apads)):
        i = active[kind]
        b = branches[i] if i < len(branches) else None
        fresh = b is not None and b.alive and now - b.seen[kind] < STALL
        want = pads[i] if fresh else pads["slate"]
        source = i if fresh else "slate"
        if on_air[kind] != source:
            on_air[kind] = source
            sel.set_property("active-pad", want)
            log(f"{kind}: {'on input ' + str(i) if fresh else 'nothing from input ' + str(i) + ', on the slate'}")
    return True


def on_message(bus, msg):
    if msg.type == Gst.MessageType.ERROR:
        err, dbg = msg.parse_error()
        src = msg.src
        while src is not None:
            for b in branches:
                if src in b.elements:
                    if not b.rebuilding:
                        log(f"input {b.i} error: {err.message}")
                    b.lost(err.message)
                    return
            src = src.get_parent()
        log(f"fatal: {err.message} {dbg}")
        loop.quit()
    elif msg.type == Gst.MessageType.EOS:
        log("program got end of stream")
        loop.quit()


def do(cmd):
    parts = cmd.split()
    if len(parts) == 2 and parts[0] in ("cut", "audio"):
        i = int(parts[1])
        kind, sel, pads = ("video", vsel, vpads) if parts[0] == "cut" else ("audio", asel, apads)
        active[kind] = i
        watchdog()
    return False


def read_commands():
    for line in sys.stdin:
        GLib.idle_add(do, line)
    GLib.idle_add(loop.quit)


loop = GLib.MainLoop()
bus = pipe.get_bus()
bus.add_signal_watch()
bus.connect("message", on_message)
threading.Thread(target=read_commands, daemon=True).start()
pipe.set_state(Gst.State.PLAYING)
GLib.timeout_add(1000, add_cameras)
GLib.timeout_add(200, watchdog)
loop.run()
pipe.set_state(Gst.State.NULL)
