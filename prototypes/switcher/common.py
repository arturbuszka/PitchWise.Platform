"""What the three test signals look like, so that the analyser can recognise every frame.

Each frame carries a barcode (source, frame number, checksum) and otherwise deterministic content,
which lets the analyser rebuild the frame that should have arrived and compare it pixel by pixel.
"""
import numpy as np

FPS = 25
RATE = 48000
SAMPLES_PER_FRAME = RATE // FPS
BLOCKS = 40          # barcode blocks across the frame
STRIP = 24           # barcode height in pixels at 720p
CLICK = 0.02         # seconds of tone per click
TONES = [600, 900, 1200, 1500]  # second % 4 picks the tone, so a click can't be taken for its neighbour


def _bits(source, n):
    body = (source & 0xF) << 26 | (n & 0x3FFFFFF)
    check = sum((body >> i) & 0x3F for i in range(0, 30, 6)) & 0x3F
    return [1, 0] + [(body >> (29 - i)) & 1 for i in range(30)] + [(check >> (5 - i)) & 1 for i in range(6)] + [1, 0]


def strip_height(h):
    return max(8, STRIP * h // 720)


def stamp(y, source, n):
    h, w = y.shape
    bw, sh = w // BLOCKS, strip_height(h)
    for i, b in enumerate(_bits(source, n)):
        y[:sh, i * bw:(i + 1) * bw] = 235 if b else 16


def read_stamp(strip):
    """Returns (source, n), or None when the barcode doesn't add up, which means a damaged frame."""
    h, w = strip.shape
    bw = w // BLOCKS
    bits = [1 if strip[h // 2, i * bw + bw // 2] > 128 else 0 for i in range(BLOCKS)]
    if bits[:2] != [1, 0] or bits[38:] != [1, 0]:
        return None
    body = 0
    for b in bits[2:32]:
        body = body << 1 | b
    check = 0
    for b in bits[32:38]:
        check = check << 1 | b
    if check != sum((body >> i) & 0x3F for i in range(0, 30, 6)) & 0x3F:
        return None
    return body >> 26, body & 0x3FFFFFF


class Picture:
    """Moving texture, different per source. Cheap enough to generate three at once in real time."""

    def __init__(self, source, w, h):
        rng = np.random.default_rng(1000 + source)
        cw, ch = w // 16 + 1, h // 16 + 1
        coarse = rng.integers(40, 216, (ch, cw), dtype=np.uint8)
        tex = np.kron(coarse, np.ones((16, 16), dtype=np.uint8))[:h, :w]
        tex = (tex.astype(np.int16) + rng.integers(-6, 7, tex.shape, dtype=np.int16)).clip(16, 235).astype(np.uint8)
        self.tex = np.concatenate([np.concatenate([tex, tex], 1)] * 2, 0)
        self.w, self.h, self.source = w, h, source
        self.u = np.full((h // 2, w // 2), [90, 170, 128, 60][source % 4], np.uint8)
        self.v = np.full((h // 2, w // 2), [170, 90, 60, 128][source % 4], np.uint8)

    def luma(self, n):
        x, y = (n * 6) % self.w, (n * 2) % self.h
        out = self.tex[y:y + self.h, x:x + self.w].copy()
        bx = (n * 11) % (self.w - 80)
        out[self.h // 2:self.h // 2 + 80, bx:bx + 80] = 235
        return out

    def frame(self, n):
        y = self.luma(n)
        stamp(y, self.source, n)
        return y.tobytes() + self.u.tobytes() + self.v.tobytes()


def audio_chunk(source, frame):
    """Stereo 16-bit audio for one frame. Each whole second of the frame clock starts with a click."""
    start = frame * SAMPLES_PER_FRAME
    t = np.arange(start, start + SAMPLES_PER_FRAME) / RATE
    second = (start // RATE)
    within = t - second
    tone = TONES[second % 4]
    a = np.where(within < CLICK, 12000 * np.sin(2 * np.pi * tone * t), 0).astype(np.int16)
    return np.stack([a, a], 1).tobytes()
