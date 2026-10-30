"""Regenerates the JPEG 2000 test fixtures, decoded by OpenJPEG through Pillow for reference.

Pillow's encoder (OpenJPEG) covers the common shapes: lossless 5/3 and lossy 9/7, gray, RGB,
RGBA and 16 bit, tiles, offsets, precincts, code-block sizes, quality layers, every progression
order, PLT and raw codestreams.

Pillow cannot ask OpenJPEG for code-block styles, SOP and EPH, packed headers (PPM, PPT),
progression order changes, tile-parts, ROI, subsampled components, palettes or odd bit depths.
For those this script carries a small lossless encoder of its own, written from T.800: 5/3
wavelet, RCT, MQ and raw coding with every code-block style, tag trees and packet headers, and
the five progression orders as the standard's walk over the reference grid. Every file it
writes is decoded by OpenJPEG and must give back the source samples, so the encoder is checked
against OpenJPEG rather than against the decoder under test.

The reference for each fixture is OpenJPEG's decode, Reference/<name>.png (8 or 16 bits per
channel). Where Pillow cannot hand the decode over as it is (planes of different sizes, 1 and 12
bit samples it rescales, signed samples it offsets, a minimal ROI shift OpenJPEG misreads), the
lossless source is the reference, one PNG per channel: Reference/<name>.c<k>.png.

Everything here is written from the standard; no code comes from OpenJPEG or other decoders.

    pip install pillow numpy
    python tools/make_jpx_fixtures.py

A large image for measuring memory and time, written where asked and never committed:

    python tools/make_jpx_fixtures.py --large scan.jp2 4812x7541 [--lossy] [--tile 1024] [--gray]
"""

import argparse
import io
import struct
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Jpx"


def card(width: int, height: int, seed: int, channels: int = 3) -> np.ndarray:
    """A photograph-like test card: smooth gradients, hard edges and sensor noise."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    planes = [
        128 + 100 * np.sin(x / 9.0) * np.cos(y / 13.0),
        255 * x / max(1, width - 1),
        255 * ((x // 16 + y // 16) % 2),
        255 * y / max(1, height - 1),
    ]
    stack = np.stack(planes[:channels], axis=-1) + rng.normal(0, 6, (height, width, channels))
    return np.clip(stack, 0, 255).astype(np.uint8)


def rgb(width: int = 61, height: int = 45, seed: int = 1) -> Image.Image:
    return Image.fromarray(card(width, height, seed))


def gray(width: int = 61, height: int = 45, seed: int = 1) -> Image.Image:
    return rgb(width, height, seed).convert("L")


PILLOW = []


def save(image: Image.Image, name: str, **options) -> None:
    image.save(OUT / name, "JPEG2000", **options)
    PILLOW.append(name)


def pillow_fixtures() -> None:
    save(gray(), "gray-53.jp2")
    save(rgb(), "rgb-53.jp2", mct=1)
    save(rgb(), "rgb-97.jp2", irreversible=True, mct=1, quality_mode="rates", quality_layers=[12])
    save(gray(), "gray-97.j2k", irreversible=True, quality_mode="rates", quality_layers=[8], no_jp2=True)
    save(gray(), "gray-plt.j2k", plt=True, codeblock_size=(16, 16), no_jp2=True)
    save(gray(), "gray-1res.jp2", num_resolutions=1)
    save(gray(), "gray-3res.jp2", num_resolutions=3, codeblock_size=(8, 32))

    # Every progression order, over tiles, precincts and three layers, the last one lossless.
    for order in ("LRCP", "RLCP", "RPCL", "PCRL", "CPRL"):
        save(rgb(), f"rgb-{order.lower()}.jp2", mct=1, progression=order, tile_size=(32, 32), precinct_size=(16, 16),
             codeblock_size=(8, 8), num_resolutions=3, quality_mode="rates", quality_layers=[40, 15, 0])

    # An image and a tile grid that do not start at the origin.
    save(rgb(), "rgb-offset.jp2", mct=1, offset=(7, 5), tile_offset=(3, 2), tile_size=(24, 20), num_resolutions=3)

    # Lossy with tiles and layers.
    save(rgb(80, 60, 3), "rgb-97-tiles.jp2", irreversible=True, mct=1, tile_size=(32, 32), num_resolutions=4,
         quality_mode="rates", quality_layers=[30, 10])

    # 16 bit gray and RGBA with an opacity channel definition.
    wide = (card(61, 45, 4, 1)[:, :, 0].astype(np.uint16) * 257) ^ np.random.default_rng(5).integers(0, 256, (45, 61), dtype=np.uint16)
    save(Image.fromarray(wide), "gray16-53.jp2")
    save(Image.fromarray(card(61, 45, 6, 4)), "rgba-53.jp2", mct=1)


# ---------------------------------------------------------------------------------------------
# A lossless encoder, T.800 part 1, for the features Pillow does not expose.

# Table C.2: Qe, next index after MPS, after LPS, and the MPS switch.
QE = [0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
      0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
      0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
      0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601]
NMPS = [1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46]
NLPS = [1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
        22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46]
SWITCH = [1, 0, 0, 0, 0, 0, 1] + [0] * 7 + [1] + [0] * 32

RL, UNI = 17, 18
BYPASS, RESET, TERMALL, VSC, PTERM, SEGSYM = 1, 2, 4, 8, 16, 32


class MQ:
    """The MQ encoder of C.2 with the flush of C.2.9."""

    def __init__(self):
        self.reset()
        self.start()

    def reset(self):
        self.index = [0] * 19
        self.mps = [0] * 19
        self.index[0], self.index[RL], self.index[UNI] = 4, 3, 46

    def start(self):
        self.a, self.c, self.ct = 0x8000, 0, 12
        self.out = bytearray([0])

    def encode(self, cx, d):
        i = self.index[cx]
        qe = QE[i]
        self.a -= qe
        if d == self.mps[cx]:
            if self.a & 0x8000:
                self.c += qe
                return
            if self.a < qe:
                self.a = qe
            else:
                self.c += qe
            self.index[cx] = NMPS[i]
        else:
            if self.a < qe:
                self.c += qe
            else:
                self.a = qe
            if SWITCH[i]:
                self.mps[cx] = 1 - self.mps[cx]
            self.index[cx] = NLPS[i]
        while True:
            self.a <<= 1
            self.c <<= 1
            self.ct -= 1
            if self.ct == 0:
                self.byteout()
            if self.a & 0x8000:
                break

    def byteout(self):
        if self.out[-1] == 0xFF:
            self.out.append((self.c >> 20) & 0xFF)
            self.c &= 0xFFFFF
            self.ct = 7
        elif self.c < 0x8000000:
            self.out.append((self.c >> 19) & 0xFF)
            self.c &= 0x7FFFF
            self.ct = 8
        else:
            self.out[-1] += 1
            if self.out[-1] == 0xFF:
                self.c &= 0x7FFFFFF
                self.out.append((self.c >> 20) & 0xFF)
                self.c &= 0xFFFFF
                self.ct = 7
            else:
                self.out.append((self.c >> 19) & 0xFF)
                self.c &= 0x7FFFF
                self.ct = 8

    def flush(self) -> bytes:
        temp = self.c + self.a
        self.c |= 0xFFFF
        if self.c >= temp:
            self.c -= 0x8000
        self.c <<= self.ct
        self.byteout()
        self.c <<= self.ct
        self.byteout()
        data = bytes(self.out[1:])
        return data[:-1] if data.endswith(b"\xff") else data


class Raw:
    """Raw bits for arithmetic coding bypass: seven bits follow a 0xFF byte (D.6)."""

    def __init__(self):
        self.out, self.cur, self.n, self.cap = bytearray(), 0, 0, 8

    def put(self, bit):
        self.cur = (self.cur << 1) | bit
        self.n += 1
        if self.n == self.cap:
            self.out.append(self.cur)
            self.cap = 7 if self.cur == 0xFF else 8
            self.cur, self.n = 0, 0

    def flush(self) -> bytes:
        if self.n:
            self.out.append(self.cur << (self.cap - self.n))
        if self.out.endswith(b"\xff"):
            self.out.append(0)
        return bytes(self.out)


def zero_context(n, orientation):
    h = bool(n & 4) + bool(n & 8)
    v = bool(n & 1) + bool(n & 2)
    d = bool(n & 16) + bool(n & 32) + bool(n & 64) + bool(n & 128)
    if orientation == 3:
        hv = h + v
        if d >= 3:
            return 8
        if d == 2:
            return 7 if hv >= 1 else 6
        if d == 1:
            return 5 if hv >= 2 else 4 if hv == 1 else 3
        return 2 if hv >= 2 else hv
    if orientation == 1:
        h, v = v, h
    if h == 2:
        return 8
    if h == 1:
        return 7 if v >= 1 else 6 if d >= 1 else 5
    return 4 if v == 2 else 3 if v == 1 else 2 if d >= 2 else d


def sign_context(f):
    def part(sig, neg):
        return 0 if not f & sig else -1 if f & neg else 1
    h = max(-1, min(1, part(4, 1024) + part(8, 2048)))
    v = max(-1, min(1, part(1, 256) + part(2, 512)))
    if h == 0:
        return (9 if v == 0 else 10), (1 if v == -1 else 0)
    return 12 + h * v, (1 if h == -1 else 0)


SIG, VISIT, REFINED = 1 << 12, 1 << 13, 1 << 14
CAUSAL = ~(2 | 64 | 128 | 512)


def max_passes(first, style):
    if style & TERMALL:
        return 1
    if not style & BYPASS:
        return 1 << 30
    if first < 10:
        return 10 - first
    return 2 if (first - 10) % 3 == 0 else 1


def encode_block(coefs: np.ndarray, orientation: int, style: int):
    """Tier-1 coding of one code-block: returns the bit-planes and the segments (passes, bytes)."""
    h, w = coefs.shape
    mag = np.abs(coefs).astype(np.int64)
    neg = coefs < 0
    planes = int(mag.max()).bit_length() if mag.size else 0
    if planes == 0:
        return 0, []
    s = w + 2
    flags = [0] * (s * (h + 2))
    mq, raw = MQ(), None
    segments, seg_passes, seg_first = [], 0, 0
    causal = bool(style & VSC)

    def setsig(f, negative):
        flags[f] |= SIG
        flags[f - s] |= 2 | (512 if negative else 0)
        flags[f + s] |= 1 | (256 if negative else 0)
        flags[f - 1] |= 8 | (2048 if negative else 0)
        flags[f + 1] |= 4 | (1024 if negative else 0)
        flags[f - s - 1] |= 128
        flags[f - s + 1] |= 64
        flags[f + s - 1] |= 32
        flags[f + s + 1] |= 16

    def emit_sign(state, negative):
        cx, xor = sign_context(state)
        mq.encode(cx, int(negative) ^ xor)

    total = 3 * planes - 2
    for p_index in range(total):
        kind = 2 if p_index == 0 else (p_index - 1) % 3
        plane = planes - 1 - (0 if p_index == 0 else (p_index + 2) // 3)
        if seg_passes == 0:
            seg_first = p_index
            israw = bool(style & BYPASS) and p_index >= 10 and kind != 2
            raw = Raw() if israw else None
            if not israw:
                mq.start()
        for y0 in range(0, h, 4):
            rows = min(4, h - y0)
            for x in range(w):
                start = 0
                if kind == 2 and rows == 4:
                    states = [flags[(y0 + k + 1) * s + x + 1] for k in range(4)]
                    if causal:
                        states[3] &= CAUSAL
                    if all((st & (SIG | VISIT | 0xFF)) == 0 for st in states):
                        bits = [(int(mag[y0 + k, x]) >> plane) & 1 for k in range(4)]
                        if not any(bits):
                            mq.encode(RL, 0)
                            continue
                        mq.encode(RL, 1)
                        r = bits.index(1)
                        mq.encode(UNI, r >> 1)
                        mq.encode(UNI, r & 1)
                        f = (y0 + r + 1) * s + x + 1
                        emit_sign(states[r], neg[y0 + r, x])
                        setsig(f, neg[y0 + r, x])
                        start = r + 1
                for dy in range(start, rows):
                    f = (y0 + dy + 1) * s + x + 1
                    state = flags[f]
                    masked = state & CAUSAL if causal and dy == 3 else state
                    bit = (int(mag[y0 + dy, x]) >> plane) & 1
                    if kind == 0:
                        if state & SIG or not masked & 0xFF:
                            continue
                        if raw:
                            raw.put(bit)
                            if bit:
                                raw.put(int(neg[y0 + dy, x]))
                        else:
                            mq.encode(zero_context(masked & 0xFF, orientation), bit)
                            if bit:
                                emit_sign(masked, neg[y0 + dy, x])
                        if bit:
                            setsig(f, neg[y0 + dy, x])
                        flags[f] |= VISIT
                    elif kind == 1:
                        if state & (SIG | VISIT) != SIG:
                            continue
                        if raw:
                            raw.put(bit)
                        else:
                            mq.encode(16 if state & REFINED else 15 if masked & 0xFF else 14, bit)
                        flags[f] |= REFINED
                    else:
                        if state & (SIG | VISIT):
                            continue
                        mq.encode(zero_context(masked & 0xFF, orientation), bit)
                        if bit:
                            emit_sign(masked, neg[y0 + dy, x])
                            setsig(f, neg[y0 + dy, x])
            if kind == 2:
                for dy in range(rows):
                    for x in range(w):
                        flags[(y0 + dy + 1) * s + x + 1] &= ~VISIT
        if kind == 2 and style & SEGSYM:
            for b in (1, 0, 1, 0):
                mq.encode(UNI, b)
        if style & RESET:
            mq.reset()
        seg_passes += 1
        if seg_passes == max_passes(seg_first, style) or p_index == total - 1:
            segments.append((seg_passes, raw.flush() if raw else mq.flush()))
            seg_passes = 0
    return planes, segments


def forward53(x: np.ndarray, parity: int) -> np.ndarray:
    """F.4.8.2 along axis 0; position k is even when k + parity is."""
    n = x.shape[0]
    y = x.astype(np.int64).copy()
    if n == 1:
        return y * 2 if parity else y

    def at(k):
        k = -k if k < 0 else k
        return y[2 * (n - 1) - k if k >= n else k]
    for k in range(1 - parity, n, 2):
        y[k] = y[k] - ((at(k - 1) + at(k + 1)) >> 1)
    for k in range(parity, n, 2):
        y[k] = y[k] + ((at(k - 1) + at(k + 1) + 2) >> 2)
    return y


def split(y: np.ndarray, parity: int):
    n = y.shape[0]
    lows = [k for k in range(n) if (k + parity) % 2 == 0]
    highs = [k for k in range(n) if (k + parity) % 2 == 1]
    return y[lows], y[highs]


def ceil_div(a, b):
    return -(-a // b)


def bands_53(plane: np.ndarray, x0: int, y0: int, levels: int):
    """Subbands per resolution, coarsest first: [LL], then [HL, LH, HH], each with its origin.

    Columns first, then rows (F.4.2), so the decoder's rows-then-columns inverse undoes it.
    """
    out = []
    current, cx0, cy0 = plane.astype(np.int64), x0, y0
    for _ in range(levels):
        lo, hi = split(forward53(current, cy0 & 1), cy0 & 1)
        ll, hl = split(forward53(lo.T, cx0 & 1), cx0 & 1)
        lh, hh = split(forward53(hi.T, cx0 & 1), cx0 & 1)
        lx0, ly0, hx0, hy0 = ceil_div(cx0, 2), ceil_div(cy0, 2), cx0 // 2, cy0 // 2
        out.append([(hl.T, hx0, ly0), (lh.T, lx0, hy0), (hh.T, hx0, hy0)])
        current, cx0, cy0 = ll.T, lx0, ly0
    out.append([(current, cx0, cy0)])
    return out[::-1]


class TagTree:
    def __init__(self, w, h):
        self.levels = []
        while True:
            self.levels.append((w, h))
            if w == 1 and h == 1:
                break
            w, h = (w + 1) // 2, (h + 1) // 2
        self.value = [[1 << 30] * (lw * lh) for lw, lh in self.levels]
        self.low = [[0] * (lw * lh) for lw, lh in self.levels]
        self.known = [[False] * (lw * lh) for lw, lh in self.levels]

    def set(self, leaf, v):
        w0 = self.levels[0][0]
        x, y = leaf % w0, leaf // w0
        for level, (lw, lh) in enumerate(self.levels):
            i = y * lw + x
            self.value[level][i] = min(self.value[level][i], v)
            x, y = x // 2, y // 2

    def encode(self, bits, leaf, threshold):
        w0 = self.levels[0][0]
        x, y = leaf % w0, leaf // w0
        path = []
        for level, (lw, lh) in enumerate(self.levels):
            path.append((level, y * lw + x))
            x, y = x // 2, y // 2
        low = 0
        for level, i in reversed(path):
            if low > self.low[level][i]:
                self.low[level][i] = low
            else:
                low = self.low[level][i]
            while low < threshold:
                if low >= self.value[level][i]:
                    if not self.known[level][i]:
                        bits.put(1)
                        self.known[level][i] = True
                    break
                bits.put(0)
                low += 1
            self.low[level][i] = low


class Bits:
    """Packet header bits (B.10.1)."""

    def __init__(self):
        self.out, self.cur, self.n, self.cap = bytearray(), 0, 0, 8

    def put(self, bit, count=1):
        for k in range(count - 1, -1, -1):
            self.cur = (self.cur << 1) | ((bit >> k) & 1)
            self.n += 1
            if self.n == self.cap:
                self.out.append(self.cur)
                self.cap = 7 if self.cur == 0xFF else 8
                self.cur, self.n = 0, 0

    def flush(self) -> bytes:
        if self.n:
            self.out.append(self.cur << (self.cap - self.n))
        if self.out.endswith(b"\xff"):
            self.out.append(0)
        return bytes(self.out)


class Block:
    def __init__(self, coefs, orientation, style, mb, layers):
        self.planes, self.segments = encode_block(coefs, orientation, style)
        assert self.planes <= mb, (self.planes, mb)
        self.zero = mb - self.planes
        n = len(self.segments)
        if n == 0:
            self.by_layer = [[] for _ in range(layers)]
        elif n == 1 or layers == 1:
            self.by_layer = [list(range(n))] + [[] for _ in range(layers - 1)]
        else:
            # Segments spread over the layers; a block may first appear in a later one.
            self.by_layer = [list(range(l * n // layers, (l + 1) * n // layers)) for l in range(layers)]
        self.first = next((l for l, s in enumerate(self.by_layer) if s), layers)
        self.lblock = 3
        self.included = False


def pass_code(bits, n):
    if n == 1:
        bits.put(0)
    elif n == 2:
        bits.put(0b10, 2)
    elif n <= 5:
        bits.put(0b11, 2)
        bits.put(n - 3, 2)
    elif n <= 36:
        bits.put(0b1111, 4)
        bits.put(n - 6, 5)
    else:
        bits.put(0b111111111, 9)
        bits.put(n - 37, 7)


def encode_lossless(planes, comps, *, x0=0, y0=0, tile=None, tile_origin=(0, 0), levels=3, block=(4, 4), style=0,
                    layers=1, order=0, precincts=None, sop=False, eph=False, mct=False, roi=None, packed=None,
                    tile_parts=1, poc=None, comment=b"PdfNetStack test fixture"):
    roi_shift = 0
    """A codestream. planes: per component, a 2D array at the component's own resolution.

    comps: (depth, signed, dx, dy) each. roi: a shift; the top left quarter of every subband is
    the region of interest. packed: None, "ppm" or "ppt".
    """
    nc = len(comps)
    width = max((plane.shape[1] - 1) * dx + 1 for plane, (_, _, dx, _) in zip(planes, comps))
    height = max((plane.shape[0] - 1) * dy + 1 for plane, (_, _, _, dy) in zip(planes, comps))
    x1, y1 = x0 + width, y0 + height
    for plane, (_, _, dx, dy) in zip(planes, comps):
        assert plane.shape == (ceil_div(y1, dy) - ceil_div(y0, dy), ceil_div(x1, dx) - ceil_div(x0, dx)), "plane size"
    tw, th = tile or (x1, y1)
    tox, toy = tile_origin
    across, down = ceil_div(x1 - tox, tw), ceil_div(y1 - toy, th)
    guard = 2
    gain = [0, 1, 1, 2]

    def exponent(c, r, b):
        depth = comps[c][0]
        return depth + gain[0 if r == 0 else b + 1] + (1 if mct and c < 3 else 0)

    def mb(c, r, b):
        return guard + exponent(c, r, b) - 1

    def ppx(r):
        return 15 if precincts is None else precincts[r][0]

    def ppy(r):
        return 15 if precincts is None else precincts[r][1]

    def packet(precinct, layer):
        bits = Bits()
        body = bytearray()
        if not any(blk.by_layer[layer] for bw, bh, blocks, _, _ in precinct for blk in blocks):
            bits.put(0)
        else:
            bits.put(1)
            for bw, bh, blocks, incl, zero in precinct:
                for i, blk in enumerate(blocks):
                    segs = blk.by_layer[layer]
                    if not blk.included:
                        incl.encode(bits, i, layer + 1)
                        if not segs:
                            continue
                        zero.encode(bits, i, 1 << 20)
                        blk.included = True
                    else:
                        bits.put(1 if segs else 0)
                        if not segs:
                            continue
                    passes = sum(blk.segments[s][0] for s in segs)
                    pass_code(bits, passes)
                    need = max(len(blk.segments[s][1]).bit_length() - (blk.segments[s][0].bit_length() - 1) for s in segs)
                    grow = max(0, need - blk.lblock)
                    bits.put((1 << (grow + 1)) - 2, grow + 1)
                    blk.lblock += grow
                    for s in segs:
                        n_passes, data = blk.segments[s]
                        bits.put(len(data), blk.lblock + n_passes.bit_length() - 1)
                        body.extend(data)
        header = bits.flush() + (b"\xff\x92" if eph else b"")
        return header, bytes(body)

    tiles = []
    for t in range(across * down):
        p, q = t % across, t // across
        tx0, ty0 = max(x0, tox + p * tw), max(y0, toy + q * th)
        tx1, ty1 = min(x1, tox + (p + 1) * tw), min(y1, toy + (q + 1) * th)
        comp_data = []
        samples = []
        for c, (plane, (depth, signed, dx, dy)) in enumerate(zip(planes, comps)):
            cx0, cy0 = ceil_div(tx0, dx), ceil_div(ty0, dy)
            cx1, cy1 = ceil_div(tx1, dx), ceil_div(ty1, dy)
            ox, oy = ceil_div(x0, dx), ceil_div(y0, dy)
            part = plane[cy0 - oy:cy1 - oy, cx0 - ox:cx1 - ox].astype(np.int64)
            if not signed:
                part = part - (1 << (depth - 1))
            samples.append((part, cx0, cy0, cx1, cy1))
        if mct:
            r_, g_, b_ = samples[0][0], samples[1][0], samples[2][0]
            ys = [(r_ + 2 * g_ + b_) >> 2, b_ - g_, r_ - g_]
            samples = [(ys[i],) + samples[i][1:] for i in range(3)] + samples[3:]
        all_bands = [bands_53(part, cx0, cy0, levels) for part, cx0, cy0, _, _ in samples]
        if roi:
            # The shift must lift the region above every background magnitude (H.1).
            assert across * down == 1, "ROI fixtures have one tile"
            background = []
            for bands in all_bands:
                for res in bands:
                    for band, _, _ in res:
                        hb, wb = band.shape
                        mask = np.ones(band.shape, bool)
                        mask[:(hb + 1) // 2, :(wb + 1) // 2] = False
                        background.append(int(np.abs(band[mask]).max(initial=0)))
            roi_shift = max(background).bit_length() + (1 if roi == "margin" else 0)
            for bands in all_bands:
                for res in bands:
                    for band, _, _ in res:
                        hb, wb = band.shape
                        band[:(hb + 1) // 2, :(wb + 1) // 2] <<= roi_shift
        for c, (part, cx0, cy0, cx1, cy1) in enumerate(samples):
            bands = all_bands[c]
            resolutions = []
            for r in range(levels + 1):
                n = levels - r
                rx0, ry0, rx1, ry1 = ceil_div(cx0, 1 << n), ceil_div(cy0, 1 << n), ceil_div(cx1, 1 << n), ceil_div(cy1, 1 << n)
                xcb = min(block[0], ppx(r) if r == 0 else ppx(r) - 1)
                ycb = min(block[1], ppy(r) if r == 0 else ppy(r) - 1)
                pw = ceil_div(rx1, 1 << ppx(r)) - (rx0 >> ppx(r)) if rx1 > rx0 else 0
                ph = ceil_div(ry1, 1 << ppy(r)) - (ry0 >> ppy(r)) if ry1 > ry0 else 0
                precinct_list = []
                for k in range(pw * ph):
                    i, j = k % pw, k // pw
                    left, top = ((rx0 >> ppx(r)) + i) << ppx(r), ((ry0 >> ppy(r)) + j) << ppy(r)
                    right, bottom = left + (1 << ppx(r)), top + (1 << ppy(r))
                    if r > 0:
                        left, top, right, bottom = left // 2, top // 2, right // 2, bottom // 2
                    pbands = []
                    for b, (band, bx0, by0) in enumerate(bands[r]):
                        hb, wb = band.shape
                        px0, py0 = max(bx0, left), max(by0, top)
                        px1, py1 = min(bx0 + wb, right), min(by0 + hb, bottom)
                        if px0 >= px1 or py0 >= py1:
                            pbands.append((0, 0, [], None, None))
                            continue
                        gx0, gy0 = px0 >> xcb, py0 >> ycb
                        gx1, gy1 = ceil_div(px1, 1 << xcb), ceil_div(py1, 1 << ycb)
                        blocks = []
                        for gy in range(gy0, gy1):
                            for gx in range(gx0, gx1):
                                bx_0, by_0 = max(px0, gx << xcb), max(py0, gy << ycb)
                                bx_1, by_1 = min(px1, (gx + 1) << xcb), min(py1, (gy + 1) << ycb)
                                coefs = band[by_0 - by0:by_1 - by0, bx_0 - bx0:bx_1 - bx0]
                                orientation = 0 if r == 0 else b + 1
                                blocks.append(Block(coefs, orientation, style, mb(c, r, b) + roi_shift, layers))
                        bw, bh = gx1 - gx0, gy1 - gy0
                        incl, zero = TagTree(bw, bh), TagTree(bw, bh)
                        for i_b, blk in enumerate(blocks):
                            incl.set(i_b, blk.first)
                            zero.set(i_b, blk.zero)
                        pbands.append((bw, bh, blocks, incl, zero))
                    precinct_list.append(pbands)
                resolutions.append(dict(x0=rx0, y0=ry0, x1=rx1, y1=ry1, pw=pw, ph=ph, precincts=precinct_list,
                                        next=[0] * (pw * ph)))
            comp_data.append(dict(resolutions=resolutions, x0=cx0, y0=cy0))

        # Packets in progression order (B.12), the reference grid walked as the standard does.
        packets = []
        volumes = poc or [(0, 0, layers, levels + 1, nc, order)]

        def emit(c, r, k, layer):
            res = comp_data[c]["resolutions"][r]
            if res["next"][k] != layer:
                return
            res["next"][k] += 1
            packets.append(packet(res["precincts"][k], layer))

        def precinct_at(c, r, x, y):
            dx, dy = comps[c][2], comps[c][3]
            res = comp_data[c]["resolutions"][r]
            n = levels - r
            if res["x0"] == res["x1"] or res["y0"] == res["y1"]:
                return None
            if not (y % (dy << (ppy(r) + n)) == 0 or (y == ty0 and ((res["y0"] << n) % (1 << (ppy(r) + n))) != 0)):
                return None
            if not (x % (dx << (ppx(r) + n)) == 0 or (x == tx0 and ((res["x0"] << n) % (1 << (ppx(r) + n))) != 0)):
                return None
            kx = (ceil_div(x, dx << n) >> ppx(r)) - (res["x0"] >> ppx(r))
            ky = (ceil_div(y, dy << n) >> ppy(r)) - (res["y0"] >> ppy(r))
            return ky * res["pw"] + kx

        for rs, cs, lye, re, ce, prog in volumes:
            rng_c = range(cs, min(ce, nc))
            rng_r = range(rs, min(re, levels + 1))
            if prog in (0, 1):
                outer = [(l, r) for l in range(lye) for r in rng_r] if prog == 0 else [(l, r) for r in rng_r for l in range(lye)]
                for l, r in outer:
                    for c in rng_c:
                        res = comp_data[c]["resolutions"][r]
                        for k in range(res["pw"] * res["ph"]):
                            emit(c, r, k, l)
                continue
            if prog == 2:
                walk = [(r, y, x, c) for r in rng_r for y in range(ty0, ty1) for x in range(tx0, tx1) for c in rng_c]
            elif prog == 3:
                walk = [(r, y, x, c) for y in range(ty0, ty1) for x in range(tx0, tx1) for c in rng_c for r in rng_r]
            else:
                walk = [(r, y, x, c) for c in rng_c for y in range(ty0, ty1) for x in range(tx0, tx1) for r in rng_r]
            for r, y, x, c in walk:
                k = precinct_at(c, r, x, y)
                if k is not None:
                    for l in range(lye):
                        emit(c, r, k, l)
        tiles.append(packets)

    # Main header.
    def segment(marker, payload):
        return struct.pack(">HH", marker, len(payload) + 2) + payload

    siz = struct.pack(">HIIIIIIIIH", 0, x1, y1, x0, y0, tw, th, tox, toy, nc)
    for depth, signed, dx, dy in comps:
        siz += bytes([(depth - 1) | (0x80 if signed else 0), dx, dy])
    scod = (1 if precincts else 0) | (2 if sop else 0) | (4 if eph else 0)
    cod = struct.pack(">BBHB", scod, order, layers, 1 if mct else 0)
    cod += bytes([levels, block[0] - 2, block[1] - 2, style, 1])
    if precincts:
        cod += bytes((py << 4) | px for px, py in precincts)
    main = struct.pack(">H", 0xFF4F) + segment(0xFF51, siz) + segment(0xFF52, cod)

    def qcd_payload(c):
        return bytes([guard << 5]) + bytes(exponent(c, r, b) << 3 for r in range(levels + 1) for b in range(1 if r == 0 else 3))
    main += segment(0xFF5C, qcd_payload(0))
    for c in range(1, nc):
        if qcd_payload(c) != qcd_payload(0):
            main += segment(0xFF5D, bytes([c]) + qcd_payload(c))
    if roi:
        for c in range(nc):
            main += segment(0xFF5E, bytes([c, 0, roi_shift]))
    if poc:
        main += segment(0xFF5F, b"".join(struct.pack(">BBHBBB", *v) for v in poc))
    main += segment(0xFF64, struct.pack(">H", 1) + comment)

    # Tile-parts: each tile's packets cut into tile_parts runs.
    parts = []
    for t, packets in enumerate(tiles):
        n = len(packets)
        cuts = [i * n // tile_parts for i in range(tile_parts + 1)]
        for tp in range(tile_parts):
            parts.append((t, tp, cuts[tp], packets[cuts[tp]:cuts[tp + 1]]))
    if packed == "ppm":
        chunks = b"".join(struct.pack(">I", sum(len(h) for h, _ in pk)) + b"".join(h for h, _ in pk) for _, _, _, pk in parts)
        for z, at in enumerate(range(0, len(chunks), 60000)):
            main += segment(0xFF60, bytes([z]) + chunks[at:at + 60000])

    stream = bytearray(main)
    for t, tp, first, pk in parts:
        header = b""
        if packed == "ppt":
            header = segment(0xFF61, bytes([tp]) + b"".join(h for h, _ in pk))
        data = bytearray()
        for index, (h, b) in enumerate(pk, first):
            if sop:
                data += struct.pack(">HHH", 0xFF91, 4, index & 0xFFFF)
            data += (b if packed else h + b)
        psot = 12 + len(header) + 2 + len(data)
        stream += segment(0xFF90, struct.pack(">HIBB", t, psot, tp, tile_parts)) + header + b"\xff\x93" + data
    stream += b"\xff\xd9"
    return bytes(stream)


def box(kind: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", 8 + len(payload)) + kind + payload


def jp2(codestream: bytes, width: int, height: int, comps, colour: int, palette=None, definitions=None) -> bytes:
    """A JP2 file (Annex I): signature, file type, header (ihdr, colr, pclr, cmap, cdef), codestream."""
    depths = {(d - 1) | (0x80 if s else 0) for d, s, _, _ in comps}
    ihdr = box(b"ihdr", struct.pack(">IIHBBBB", height, width, len(comps), depths.pop() if len(depths) == 1 else 255, 7, 0, 0))
    header = ihdr + box(b"colr", bytes([1, 0, 0]) + struct.pack(">I", colour))
    if palette is not None:
        entries, columns = palette.shape
        body = struct.pack(">HB", entries, columns) + bytes([7] * columns) + palette.astype(np.uint8).tobytes()
        header += box(b"pclr", body)
        header += box(b"cmap", b"".join(struct.pack(">HBB", 0, 1, i) for i in range(columns)))
    if definitions:
        header += box(b"cdef", struct.pack(">H", len(definitions)) + b"".join(struct.pack(">HHH", *d) for d in definitions))
    return (box(b"jP  ", b"\r\n\x87\n") + box(b"ftyp", b"jp2 " + struct.pack(">I", 0) + b"jp2 ")
            + box(b"jp2h", header) + box(b"jp2c", codestream))


def openjpeg(data: bytes) -> Image.Image:
    image = Image.open(io.BytesIO(data))
    image.load()
    return image


def write_planes(name: str, planes, offset: int = 0) -> None:
    """A reference as one PNG per channel, Reference/<name>.c<k>.png, 16 bits when 8 do not hold it."""
    for k, plane in enumerate(planes):
        values = np.asarray(plane).astype(np.int64) + offset
        assert values.min() >= 0
        kind = np.uint16 if values.max() > 255 else np.uint8
        Image.fromarray(values.astype(kind)).save(OUT / "Reference" / f"{name}.c{k}.png")


def checked(name: str, data: bytes, source: np.ndarray) -> None:
    """Writes a fixture after OpenJPEG gives back the source exactly; the reference is its decode."""
    decoded = openjpeg(data)
    pixels = np.asarray(decoded)
    assert pixels.shape == source.shape and (pixels.astype(np.int64) == source.astype(np.int64)).all(), f"{name}: OpenJPEG disagrees"
    (OUT / name).write_bytes(data)
    decoded.save(OUT / "Reference" / (Path(name).stem + ".png"))


def encoder_fixtures() -> None:
    plain = np.asarray(gray())
    one = [(8, False, 1, 1)]
    for name, style in [("bypass", BYPASS), ("reset", RESET), ("termall", TERMALL), ("causal", VSC),
                        ("predictable", PTERM), ("segmark", SEGSYM), ("allstyles", 63)]:
        layers = 3 if style & (BYPASS | TERMALL) else 1
        checked(f"style-{name}.j2k", encode_lossless([plain], one, style=style, layers=layers, levels=3), plain)

    # SOP and EPH over three layers and two tiles, RGB with the RCT.
    color = card(61, 45, 2)
    planes = [color[:, :, i] for i in range(3)]
    three = [(8, False, 1, 1)] * 3
    checked("rgb-sop-eph.j2k", encode_lossless(planes, three, mct=True, sop=True, eph=True, style=TERMALL, layers=3,
                                               tile=(32, 45)), color)

    # Packed packet headers with two tile-parts per tile, and tile-parts of their own.
    checked("rgb-ppm.j2k", encode_lossless(planes, three, mct=True, packed="ppm", tile=(32, 24), tile_parts=2,
                                           style=TERMALL, layers=2, sop=True), color)
    checked("rgb-ppt.j2k", encode_lossless(planes, three, mct=True, packed="ppt", tile=(32, 24), tile_parts=3,
                                           style=BYPASS, layers=2, eph=True), color)

    # Progression order changes: resolutions 0 and 1 layer by layer, then the rest by position.
    precincts = [(4, 4), (3, 3), (3, 3), (4, 4)]
    checked("rgb-poc.j2k", encode_lossless(planes, three, mct=True, precincts=precincts, block=(3, 3), style=TERMALL,
                                           layers=2, poc=[(0, 0, 2, 2, 3, 0), (0, 0, 2, 4, 3, 2), (0, 0, 2, 4, 3, 4)]),
            color)

    # Subsampled chroma at an odd origin, in each position-driven order with small precincts.
    # Pillow cannot hand over planes of different sizes, so the lossless source is the reference.
    # OpenJPEG checks the packet order: with neutral chroma it must give back the luma exactly,
    # which a wrong order (one stream for all three components) would not survive. Pillow reads
    # chroma past its buffer at the edges of an odd origin, so only gray pixels are compared.
    luma = card(61, 45, 7, 1)[:, :, 0]
    x0, y0 = 3, 1
    chroma = [card(ceil_div(x0 + 61, 2) - ceil_div(x0, 2), ceil_div(y0 + 45, 2) - ceil_div(y0, 2), 8 + i, 1)[:, :, 0] for i in range(2)]
    neutral = [np.full_like(c, 128) for c in chroma]
    sub = [(8, False, 1, 1), (8, False, 2, 2), (8, False, 2, 2)]
    for name, order in (("rpcl", 2), ("pcrl", 3), ("cprl", 4), ("lrcp", 0)):
        options = dict(x0=x0, y0=y0, order=order, precincts=precincts, block=(3, 3), tile=(40, 30), layers=2, style=TERMALL)
        twin = np.asarray(openjpeg(encode_lossless([luma] + neutral, sub, **options)))
        gray_pixels = (twin[:, :, 0] == twin[:, :, 1]) & (twin[:, :, 1] == twin[:, :, 2])
        assert gray_pixels.mean() > 0.95 and (twin[:, :, 0][gray_pixels] == luma[gray_pixels]).all(), f"sub-{name}: OpenJPEG disagrees"
        (OUT / f"sub-{name}.j2k").write_bytes(encode_lossless([luma] + chroma, sub, **options))
        write_planes(f"sub-{name}", [luma] + chroma)

    # The maximum shift ROI: the top left quarter of every subband ahead of the rest. T.800 H.1
    # lets the shift be the background's bit count; OpenJPEG 2.5 then takes background values in
    # the top half of that range for region and shifts them, so its reference uses one bit more
    # and the minimal shift is checked against the source.
    checked("gray-roi.j2k", encode_lossless([plain], one, roi="margin", levels=2), plain)
    minimal = encode_lossless([plain], one, roi=True, levels=2)
    assert (np.asarray(openjpeg(minimal)) != plain).any(), "OpenJPEG now reads the minimal shift as T.800 does"
    (OUT / "gray-roi-min.j2k").write_bytes(minimal)
    write_planes("gray-roi-min", [plain])

    # A palette: indexes in the codestream, colors in the JP2 header.
    rng = np.random.default_rng(11)
    colors = rng.integers(0, 256, (40, 3))
    index = (np.asarray(gray()) // 7).astype(np.int64)
    data = jp2(encode_lossless([index], one, levels=2), 61, 45, one, 16, palette=colors)
    decoded = np.asarray(openjpeg(data).convert("RGB"))
    assert (decoded == colors[index]).all(), "palette: OpenJPEG disagrees"
    (OUT / "palette.jp2").write_bytes(data)
    Image.fromarray(decoded.astype(np.uint8)).save(OUT / "Reference" / "palette.png")
    write_planes("palette-index", [index])

    # Odd depths: 1 bit, 12 bit, and signed 8 bit samples (stored offset by 128). Pillow scales
    # what OpenJPEG decodes to 8 or 16 bits and offsets signed samples; undone, it is the source.
    bilevel = (np.asarray(gray()) > 128).astype(np.int64)
    deep = (np.asarray(gray()).astype(np.int64) * 16) ^ rng.integers(0, 16, (45, 61))
    signed = np.asarray(gray()).astype(np.int64) - 128
    for name, plane, comp, levels, seen in (("gray-1bit", bilevel, (1, False, 1, 1), 2, bilevel << 7),
                                            ("gray-12bit", deep, (12, False, 1, 1), 3, deep << 4),
                                            ("gray-signed", signed, (8, True, 1, 1), 3, signed + 128)):
        data = encode_lossless([plane], [comp], levels=levels)
        assert (np.asarray(openjpeg(data)).astype(np.int64) == seen).all(), f"{name}: OpenJPEG disagrees"
        (OUT / f"{name}.j2k").write_bytes(data)
        write_planes(name, [plane], offset=128 if comp[1] else 0)


def references() -> None:
    out = OUT / "Reference"
    out.mkdir(exist_ok=True)
    for name in PILLOW:
        path = OUT / name
        image = Image.open(path)
        image.load()
        image.save(out / f"{path.stem}.png")


def page(width: int, height: int, channels: int, seed: int = 21) -> np.ndarray:
    """A scanned book page: paper with grain, lines of dark strokes, and a photograph-like plate."""
    rng = np.random.default_rng(seed)
    out = np.empty((height, width, channels), dtype=np.uint8)
    x = np.arange(width, dtype=np.float32)
    for top in range(0, height, 256):
        rows = min(256, height - top)
        y = np.arange(top, top + rows, dtype=np.float32)[:, None]
        paper = 232 + 6 * np.sin(x / 211.0) + rng.normal(0, 4, (rows, width)).astype(np.float32)
        ink = ((y.astype(np.int64) % 96) < 40) & (((x.astype(np.int64) * 7 + y.astype(np.int64) * 3) % 23) < 9)
        plate = (y > height * 0.55) & (y < height * 0.85) & (x > width * 0.1) & (x < width * 0.9)
        photo = 128 + 90 * np.sin(x / 37.0) * np.cos(y / 53.0)
        for c in range(channels):
            tint = paper - 8 * c
            value = np.where(ink, 40 + 10 * c, tint)
            value = np.where(plate, photo + 20 * np.sin((x + 40 * c) / 17.0), value)
            out[top:top + rows, :, c] = np.clip(value, 0, 255).astype(np.uint8)
    return out


def large(path: str, size: str, lossy: bool, tile: int | None, grey: bool) -> None:
    width, height = (int(v) for v in size.lower().split("x"))
    pixels = page(width, height, 1 if grey else 3)
    image = Image.fromarray(pixels[:, :, 0] if grey else pixels)
    options = dict(num_resolutions=6, irreversible=lossy, mct=0 if grey else 1)
    if lossy:
        options.update(quality_mode="rates", quality_layers=[20])
    if tile:
        options["tile_size"] = (tile, tile)
    image.save(path, "JPEG2000", **options)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--large", nargs=2, metavar=("PATH", "WIDTHxHEIGHT"), help="write one large image instead")
    parser.add_argument("--lossy", action="store_true")
    parser.add_argument("--tile", type=int)
    parser.add_argument("--gray", action="store_true")
    arguments = parser.parse_args()
    if arguments.large:
        large(arguments.large[0], arguments.large[1], arguments.lossy, arguments.tile, arguments.gray)
        return

    OUT.mkdir(parents=True, exist_ok=True)
    pillow_fixtures()
    references()
    encoder_fixtures()


if __name__ == "__main__":
    main()
