#!/usr/bin/env python3
"""The switcher of one match: reads its cameras, cuts between them, and publishes one program.

The broadcast service runs one of these per match, and talks to it in JSON lines:

  stdin, first line   the settings, so that no secret shows up in the process list
  stdin, later lines  what is wanted: {"inputs": [{"id", "url"}], "video": id, "audio": id}
  stdout              what is happening: on_air, status every second
  stderr              free text, for whoever reads the logs

Closing stdin ends the program, so a switcher can't outlive the service that started it.

Why it is built the way it is, measured in prototypes/switcher/RESULTS.md:
- Each camera is a branch that can die and be rebuilt alone, because a failed input must not stop the program.
- A black picture and silence are always-present inputs, which keep the encoder fed when no camera can.
- The muxer has latency to wait for pictures, or it writes sound first and pulls pictures' times up to the sound's.
"""
import json
import sys
import threading
import time

import gi

gi.require_version("Gst", "1.0")
from gi.repository import GLib, Gst  # noqa: E402

STALL_SECONDS = 1.0  # without a picture from the camera on air, before the slate takes over
REBUILD_MS = 1000  # between losing a camera and trying it again
MUX_LATENCY = 300 * Gst.MSECOND
# Right after a cut the new camera may offer a frame no later than the last one sent.
MIN_FRAME_STEP = 20 * Gst.MSECOND
ACAPS = "audio/x-raw,format=S16LE,rate=48000,channels=2,layout=interleaved"
REQUIRED_ELEMENTS = (
    "input-selector", "identity", "queue", "x264enc", "h264parse", "flvmux", "rtmp2sink", "audioconvert",
    "avenc_aac", "aacparse", "videotestsrc", "audiotestsrc", "rtspsrc", "rtph264depay", "avdec_h264",
    "videoconvert", "videoscale", "videorate", "rtpmp4gdepay", "avdec_aac", "audioresample", "audiorate", "fakesink",
)


def log(message):
    print(f"[switcher] {message}", file=sys.stderr, flush=True)


def report(**event):
    print(json.dumps(event), flush=True)


class Program:
    def __init__(self, settings):
        self.settings = settings
        self.loop = GLib.MainLoop()
        self.frames = 0
        self.last_time = -MIN_FRAME_STEP
        self.branches = {}
        self.wanted = {"video": None, "audio": None}
        self.on_air = {"video": None, "audio": None}
        self.unsupported = {}
        self.pending_state = None
        self.accepting_inputs = False
        self.exit_code = 0

        width, height = settings["width"], settings["height"]
        self.vcaps = f"video/x-raw,format=I420,width={width},height={height},framerate=25/1,pixel-aspect-ratio=1/1"
        self.pipe = Gst.parse_launch(
            f"input-selector name=vsel sync-streams=true ! identity single-segment=true ! queue max-size-buffers=2 ! "
            f"x264enc tune=zerolatency speed-preset=veryfast bitrate={settings['video_kbps']} key-int-max=50 option-string=scenecut=0 ! h264parse ! "
            f"flvmux name=mux streamable=true latency={MUX_LATENCY} ! rtmp2sink name=out sync=false "
            f"input-selector name=asel sync-streams=true ! identity single-segment=true ! queue ! audioconvert ! "
            f"avenc_aac bitrate=128000 ! aacparse ! mux. "
            f"videotestsrc is-live=true pattern=black ! {self.vcaps} ! vsel.sink_100 "
            f"audiotestsrc is-live=true wave=silence ! {ACAPS} ! asel.sink_100")

        # Set one by one, as the stream name holds the key and a location would have to be escaped around it.
        out = self.pipe.get_by_name("out")
        out.set_property("host", settings["rtmp_host"])
        out.set_property("port", settings["rtmp_port"])
        out.set_property("application", settings["rtmp_application"])
        out.set_property("stream", settings["rtmp_stream"])

        self.selectors = {"video": self.pipe.get_by_name("vsel"), "audio": self.pipe.get_by_name("asel")}
        self.slate = {kind: sel.get_static_pad("sink_100") for kind, sel in self.selectors.items()}
        for kind, sel in self.selectors.items():
            sel.set_property("active-pad", self.slate[kind])
        self.selectors["video"].get_static_pad("src").add_probe(Gst.PadProbeType.BUFFER, self.forward_only)

    def run(self):
        bus = self.pipe.get_bus()
        bus.add_signal_watch()
        bus.connect("message", self.on_message)
        threading.Thread(target=self.read_commands, daemon=True).start()
        self.pipe.set_state(Gst.State.PLAYING)
        report(type="on_air", kind="video", input=None)
        report(type="on_air", kind="audio", input=None)
        # Only once the slate is on the way out, so that a camera that isn't there can't hold up the program.
        GLib.timeout_add(1000, self.start_accepting_inputs)
        GLib.timeout_add(200, self.watch)
        GLib.timeout_add(1000, self.report_status)
        self.loop.run()
        self.pipe.set_state(Gst.State.NULL)
        return self.exit_code

    def forward_only(self, pad, info):
        """Time in the program only moves forward, whichever camera it comes from."""
        buffer = info.get_buffer()
        segment = pad.get_sticky_event(Gst.EventType.SEGMENT, 0)
        if segment is None or buffer.pts == Gst.CLOCK_TIME_NONE:
            return Gst.PadProbeReturn.OK
        running = segment.parse_segment().to_running_time(Gst.Format.TIME, buffer.pts)
        if running < self.last_time + MIN_FRAME_STEP:
            return Gst.PadProbeReturn.DROP
        self.last_time = running
        self.frames += 1
        return Gst.PadProbeReturn.OK

    def start_accepting_inputs(self):
        self.accepting_inputs = True
        if self.pending_state is not None:
            self.apply(self.pending_state)
        return False

    def apply(self, state):
        if not self.accepting_inputs:
            self.pending_state = state
            return False
        for item in state.get("inputs", []):
            if item["id"] not in self.branches:
                self.branches[item["id"]] = Branch(self, item["id"], item["url"])
        for kind in ("video", "audio"):
            if kind in state:
                self.wanted[kind] = state[kind]
        self.watch()
        return False

    def watch(self):
        """A camera can stay connected and stop sending, and no error tells us. Its pictures simply stop."""
        now = time.monotonic()
        for branch in self.branches.values():
            branch.check(now)
        for kind, selector in self.selectors.items():
            branch = self.branches.get(self.wanted[kind])
            fresh = branch is not None and branch.alive and now - branch.seen[kind] < STALL_SECONDS
            source = branch.id if fresh else None
            if self.on_air[kind] != source:
                self.on_air[kind] = source
                selector.set_property("active-pad", branch.pads[kind] if fresh else self.slate[kind])
                report(type="on_air", kind=kind, input=source)
        return True

    def report_status(self):
        report(type="status", frames=self.frames, unsupported=self.unsupported)
        return True

    def on_message(self, bus, message):
        if message.type == Gst.MessageType.ERROR:
            error, debug = message.parse_error()
            source = message.src
            while source is not None:
                for branch in self.branches.values():
                    if source in branch.elements:
                        branch.lost(error.message)
                        return
                source = source.get_parent()
            log(f"fatal: {error.message}")
            self.exit_code = 1
            self.loop.quit()
        elif message.type == Gst.MessageType.EOS:
            log("the program got end of stream")
            self.exit_code = 1
            self.loop.quit()

    def read_commands(self):
        for line in sys.stdin:
            try:
                state = json.loads(line)
            except ValueError:
                log("ignored a line that isn't JSON")
                continue
            GLib.idle_add(self.apply, state)
        GLib.idle_add(self.loop.quit)


class Branch:
    """One camera. Its end of stream must not reach a selector, or it would end the whole program."""

    def __init__(self, program, input_id, url):
        self.program, self.id, self.url = program, input_id, url
        self.elements, self.rates = [], {}
        self.alive = self.rebuilding = False
        self.seen = {"video": 0.0, "audio": 0.0}
        self.counts = {"video": -1, "audio": -1}
        self.pads = {kind: sel.request_pad_simple("sink_%u") for kind, sel in program.selectors.items()}
        self.build()

    def build(self):
        reader = self.program.settings
        source = Gst.ElementFactory.make("rtspsrc")
        source.set_property("location", self.url)
        source.set_property("user-id", reader["rtsp_user"])
        source.set_property("user-pw", reader["rtsp_password"])
        source.set_property("protocols", "tcp")
        source.set_property("latency", 200)
        source.set_property("timeout", 3000000)
        source.set_property("tcp-timeout", 3000000)
        source.connect("pad-added", self.on_pad)
        self.elements = [source]
        self.program.pipe.add(source)
        source.sync_state_with_parent()

    def on_pad(self, source, pad):
        caps = (pad.get_current_caps() or pad.query_caps(None)).get_structure(0)
        media, encoding = caps.get_string("media"), (caps.get_string("encoding-name") or "").upper()
        if media == "video" and encoding == "H264":
            description = (f"rtph264depay ! h264parse ! avdec_h264 ! videoconvert ! videoscale ! videorate name=rate ! "
                           f"{self.program.vcaps} ! queue max-size-buffers=4 leaky=downstream")
        elif media == "audio" and encoding == "MPEG4-GENERIC":
            description = (f"rtpmp4gdepay ! aacparse ! avdec_aac ! audioconvert ! audioresample ! audiorate name=rate ! "
                           f"{ACAPS} ! queue leaky=downstream")
        else:
            self.refuse(pad, media, encoding)
            return
        branch = Gst.parse_bin_from_description(description, True)
        self.program.pipe.add(branch)
        self.elements.append(branch)
        self.rates[media] = branch.get_by_name("rate")
        out = branch.get_static_pad("src")
        out.add_probe(Gst.PadProbeType.EVENT_DOWNSTREAM, self.on_event)
        out.link(self.pads[media])
        branch.sync_state_with_parent()
        pad.link(branch.get_static_pad("sink"))
        self.alive = True
        log(f"input {self.id}: {media} connected")

    def refuse(self, pad, media, encoding):
        """A track nobody takes would end the camera's whole connection, so it goes nowhere instead."""
        if media in ("video", "audio"):
            self.program.unsupported[f"{self.id}:{media}"] = encoding
        sink = Gst.ElementFactory.make("fakesink")
        sink.set_property("sync", False)
        self.program.pipe.add(sink)
        self.elements.append(sink)
        sink.sync_state_with_parent()
        pad.link(sink.get_static_pad("sink"))

    def check(self, now):
        """Reads videorate's and audiorate's own counters, which costs nothing per frame."""
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
        log(f"input {self.id} lost: {why}")
        self.program.watch()
        # Stopping elements can block on their own threads, so not here.
        threading.Thread(target=self.teardown, daemon=True).start()
        return False

    def teardown(self):
        selectors = list(self.program.selectors.values())
        for element in self.elements:
            for pad in element.pads:
                peer = pad.get_peer()
                if peer and peer.get_parent_element() in selectors:
                    pad.unlink(peer)
            element.set_state(Gst.State.NULL)
            self.program.pipe.remove(element)
        self.elements, self.rates = [], {}
        self.counts = {"video": -1, "audio": -1}
        GLib.timeout_add(REBUILD_MS, self.retry)

    def retry(self):
        self.rebuilding = False
        self.unsupported_clear()
        self.build()
        return False

    def unsupported_clear(self):
        for media in ("video", "audio"):
            self.program.unsupported.pop(f"{self.id}:{media}", None)


def check():
    """Whether this machine has everything a program needs, so that a bad image fails its build, not a match."""
    missing = [name for name in REQUIRED_ELEMENTS if Gst.ElementFactory.find(name) is None]
    if missing:
        print("missing GStreamer elements: " + ", ".join(missing), file=sys.stderr)
        return 1
    print("ok")
    return 0


def main():
    Gst.init(None)
    if "--check" in sys.argv:
        return check()
    line = sys.stdin.readline()
    if not line:
        return 0
    return Program(json.loads(line)).run()


if __name__ == "__main__":
    sys.exit(main())
