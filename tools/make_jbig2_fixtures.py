"""Regenerates the JBIG2 test fixtures and their PDFium reference bitmaps.

Two sources of JBIG2 data:

    annexh-p1, p2, p3   The test datastream of ITU-T T.88 (2000), Annex H.1, split into the
                        embedded form PDF uses: segment 0 (no page) becomes JBIG2Globals, each
                        page's segments become one image stream. Page 1 codes its symbol
                        dictionary, text region, generic region, pattern dictionary and halftone
                        region with Huffman tables and MMR; page 2 codes the same bitmap with
                        arithmetic coding; page 3 refines and aggregates symbols.

    everything else     Coded here by a small test-only encoder written from T.88 (MQ coder,
                        Annex E.2; integer coding, Annex A; generic, refinement, symbol and text
                        region coding, clauses 6.2 to 6.5). It only has to be valid, not good.

Each stream is wrapped in a one page PDF (JBIG2Decode image, DeviceGray, 1 bit) and rendered by
PDFium through pypdfium2 at one pixel per sample; that rendering is the reference, written as a
1 bit PNG (0 is black). For the synthetic fixtures the encoder's own page must equal PDFium's,
which checks the encoder and PDFium against each other. Files per fixture:

    NAME.jb2          the page's segments
    NAME.globals.jb2  the JBIG2Globals segments, when there are any
    NAME.png          PDFium's rendering

    pip install pypdfium2 pillow numpy
    python tools/make_jbig2_fixtures.py
"""

import io
import struct
from pathlib import Path

import numpy as np
import pypdfium2 as pdfium
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Jbig2"

# ITU-T T.88 (02/2000), Annex H.1: the test datastream, as printed (offset: bytes).
ANNEX_H = """
0000: 97 4A 42 32 0D 0A 1A 0A 01 00 00 00 03 00 00 00
0010: 00 00 01 00 00 00 00 18 00 01 00 00 00 01 00 00
0020: 00 01 E9 CB F4 00 26 AF 04 BF F0 78 2F E0 00 40
0030: 00 00 00 01 30 00 01 00 00 00 13 00 00 00 40 00
0040: 00 00 38 00 00 00 00 00 00 00 00 01 00 00 00 00
0050: 00 02 00 01 01 00 00 00 1C 00 01 00 00 00 02 00
0060: 00 00 02 E5 CD F8 00 79 E0 84 10 81 F0 82 10 86
0070: 10 79 F0 00 80 00 00 00 03 07 42 00 02 01 00 00
0080: 00 31 00 00 00 25 00 00 00 08 00 00 00 04 00 00
0090: 00 01 00 0C 09 00 10 00 00 00 05 01 10 00 00 00
00A0: 00 00 00 00 00 00 00 00 00 00 00 00 0C 40 07 08
00B0: 70 41 D0 00 00 00 04 27 00 01 00 00 00 2C 00 00
00C0: 00 36 00 00 00 2C 00 00 00 04 00 00 00 0B 00 01
00D0: 26 A0 71 CE A7 FF FF FF FF FF FF FF FF FF FF FF
00E0: FF FF FF FF FF FF FF FF F8 F0 00 00 00 05 10 01
00F0: 01 00 00 00 2D 01 04 04 00 00 00 0F 20 D1 84 61
0100: 18 45 F2 F9 7C 8F 11 C3 9E 45 F2 F9 7D 42 85 0A
0110: AA 84 62 2F EE EC 44 62 22 35 2A 0A 83 B9 DC EE
0120: 77 80 00 00 00 06 17 20 05 01 00 00 00 57 00 00
0130: 00 20 00 00 00 24 00 00 00 10 00 00 00 0F 00 01
0140: 00 00 00 08 00 00 00 09 00 00 00 00 00 00 00 00
0150: 04 00 00 00 AA AA AA AA 80 08 00 80 36 D5 55 6B
0160: 5A D4 00 40 04 2E E9 52 D2 D2 D2 8A A5 4A 00 20
0170: 02 23 E0 95 24 B4 92 8A 4A 92 54 92 D2 4A 29 2A
0180: 49 40 04 00 40 00 00 00 07 31 00 01 00 00 00 00
0190: 00 00 00 08 30 00 02 00 00 00 13 00 00 00 40 00
01A0: 00 00 38 00 00 00 00 00 00 00 00 01 00 00 00 00
01B0: 00 09 00 01 02 00 00 00 1B 08 00 02 FF 00 00 00
01C0: 02 00 00 00 02 4F E7 8C 20 0E 1D C7 CF 01 11 C4
01D0: B2 6F FF AC 00 00 00 0A 07 40 00 09 02 00 00 00
01E0: 1F 00 00 00 25 00 00 00 08 00 00 00 04 00 00 00
01F0: 01 00 0C 08 00 00 00 05 8D 6E 5A 12 40 85 FF AC
0200: 00 00 00 0B 27 00 02 00 00 00 23 00 00 00 36 00
0210: 00 00 2C 00 00 00 04 00 00 00 0B 00 08 03 FF FD
0220: FF 02 FE FE FE 04 EE ED 87 FB CB 2B FF AC 00 00
0230: 00 0C 10 01 02 00 00 00 1C 06 04 04 00 00 00 0F
0240: 90 71 6B 6D 99 A7 AA 49 7D F2 E5 48 1F DC 68 BC
0250: 6E 40 BB FF AC 00 00 00 0D 17 20 0C 02 00 00 00
0260: 3E 00 00 00 20 00 00 00 24 00 00 00 10 00 00 00
0270: 0F 00 02 00 00 00 08 00 00 00 09 00 00 00 00 00
0280: 00 00 00 04 00 00 00 87 CB 82 1E 66 A4 14 EB 3C
0290: 4A 15 FA CC D6 F3 B1 6F 4C ED BF A7 BF FF AC 00
02A0: 00 00 0E 31 00 02 00 00 00 00 00 00 00 0F 30 00
02B0: 03 00 00 00 13 00 00 00 25 00 00 00 08 00 00 00
02C0: 00 00 00 00 00 01 00 00 00 00 00 10 00 01 00 00
02D0: 00 00 16 08 00 02 FF 00 00 00 01 00 00 00 01 4F
02E0: E7 8D 68 1B 14 2F 3F FF AC 00 00 00 11 00 21 10
02F0: 03 00 00 00 20 08 02 02 FF FF FF FF FF 00 00 00
0300: 03 00 00 00 02 4F E9 D7 D5 90 C3 B5 26 A7 FB 6D
0310: 14 98 3F FF AC 00 00 00 12 07 20 11 03 00 00 00
0320: 25 00 00 00 25 00 00 00 08 00 00 00 00 00 00 00
0330: 00 00 8C 12 00 00 00 04 A9 5C 8B F4 C3 7D 96 6A
0340: 28 E5 76 8F FF AC 00 00 00 13 31 00 03 00 00 00
0350: 00 00 00 00 14 33 00 00 00 00 00 00
"""


def annex_h() -> bytes:
    data = bytearray()
    for line in ANNEX_H.strip().splitlines():
        offset, values = line.split(":")
        assert int(offset, 16) == len(data), f"gap at {offset}"
        data += bytes(int(v, 16) for v in values.split())
    return bytes(data)


def segments(data: bytes):
    """(number, type, page, raw segment bytes) of a sequential datastream, after the file header (7.2)."""
    position = 13 if data[8] & 2 == 0 else 9
    while position < len(data):
        start = position
        number, flags = struct.unpack_from(">IB", data, position)
        position += 5
        count = data[position] >> 5
        assert count < 5
        position += 1
        size = 1 if number <= 256 else 2 if number <= 65536 else 4
        position += count * size
        if flags & 0x40:
            (page,) = struct.unpack_from(">I", data, position)
            position += 4
        else:
            page = data[position]
            position += 1
        (length,) = struct.unpack_from(">I", data, position)
        position += 4 + length
        yield number, flags & 0x3F, page, data[start:position]


# --- The MQ encoder (T.88, E.2) -------------------------------------------------------------

QE = [0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
      0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
      0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
      0x0015, 0x0009, 0x0005, 0x0001, 0x5601]
NMPS = [1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46]
NLPS = [1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46]
SWITCH = {0, 6, 14}


class MQ:
    def __init__(self):
        self.a, self.c, self.ct = 0x8000, 0, 12
        self.out = bytearray([0])  # the byte before the output, B = 0 (H.2)

    def encode(self, contexts, cx: int, d: int) -> None:
        i, mps = contexts[cx] >> 1, contexts[cx] & 1
        qe = QE[i]
        self.a -= qe
        if d == mps:
            if self.a & 0x8000 == 0:
                if self.a < qe:
                    self.a = qe
                else:
                    self.c += qe
                i = NMPS[i]
                self.renorm()
            else:
                self.c += qe
        else:
            if self.a < qe:
                self.c += qe
            else:
                self.a = qe
            if i in SWITCH:
                mps = 1 - mps
            i = NLPS[i]
            self.renorm()
        contexts[cx] = (i << 1) | mps

    def renorm(self) -> None:
        while True:
            self.a <<= 1
            self.c <<= 1
            self.ct -= 1
            if self.ct == 0:
                self.byte_out()
            if self.a & 0x8000:
                break

    def byte_out(self) -> None:
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
        self.byte_out()
        self.c <<= self.ct
        self.byte_out()
        if self.out[-1] != 0xFF:
            self.out.append(0xFF)
        self.out.append(0xAC)
        return bytes(self.out[1:])


def check_annex_h2() -> None:
    """T.88, H.2: the arithmetic coder test sequence."""
    decisions = bytes.fromhex("00 02 00 51 00 00 00 C0 03 52 87 2A AA AA AA AA 82 C0 20 00 FC D7 9E F6 BF 7F ED 90 4F 46 A3 BF")
    expected = bytes.fromhex("84C73BFCE1A1430402200000410DBB86F4317FFF88FF37471ADB6ADFFFAC")
    mq, contexts = MQ(), bytearray(1)
    for byte in decisions:
        for k in range(8):
            mq.encode(contexts, 0, (byte >> (7 - k)) & 1)
    assert mq.flush() == expected, "the MQ encoder fails T.88 H.2"


class Ints:
    """The integer and IAID coding procedures (Annex A), one context set per procedure name."""

    def __init__(self, mq: MQ):
        self.mq = mq
        self.contexts = {}

    def _ctx(self, name, size=512):
        return self.contexts.setdefault(name, bytearray(size))

    def int(self, name: str, value) -> None:
        contexts, prev = self._ctx(name), 1

        def bit(d):
            nonlocal prev
            self.mq.encode(contexts, prev, d)
            prev = (prev << 1) | d if prev < 256 else (((prev << 1) | d) & 511) | 256

        if value is None:  # OOB: sign 1, magnitude 0
            for d in (1, 0, 0, 0):
                bit(d)
            return
        bit(1 if value < 0 else 0)
        v = abs(value)
        for low, prefix, n in ((0, [0], 2), (4, [1, 0], 4), (20, [1, 1, 0], 6), (84, [1, 1, 1, 0], 8), (340, [1, 1, 1, 1, 0], 12), (4436, [1, 1, 1, 1, 1], 32)):
            if v < low + (1 << n) or n == 32:
                for d in prefix:
                    bit(d)
                for k in range(n - 1, -1, -1):
                    bit(((v - low) >> k) & 1)
                return

    def id(self, length: int, value: int) -> None:
        contexts, prev = self._ctx("IAID", 1 << length), 1
        for k in range(length - 1, -1, -1):
            d = (value >> k) & 1
            self.mq.encode(contexts, prev, d)
            prev = (prev << 1) | d


# --- Generic and refinement region coding (6.2, 6.3), in the decoder's pixel order ----------

NOMINAL_AT = {0: [(3, -1), (-3, -1), (2, -2), (-2, -2)], 1: [(3, -1)], 2: [(2, -1)], 3: [(2, -1)]}
TYPICAL = {0: 0x9B25, 1: 0x0795, 2: 0x00E5, 3: 0x0195}


def pixel(b, x, y):
    return int(b[y, x]) if 0 <= y < b.shape[0] and 0 <= x < b.shape[1] else 0


def generic_context(b, x, y, template, at):
    g = lambda dx, dy: pixel(b, x + dx, y + dy)  # noqa: E731
    a = [g(ax, ay) for ax, ay in at]
    if template == 0:
        return (g(-1, 0) | g(-2, 0) << 1 | g(-3, 0) << 2 | g(-4, 0) << 3 | a[0] << 4 | g(2, -1) << 5 | g(1, -1) << 6 | g(0, -1) << 7
                | g(-1, -1) << 8 | g(-2, -1) << 9 | a[1] << 10 | a[2] << 11 | g(1, -2) << 12 | g(0, -2) << 13 | g(-1, -2) << 14 | a[3] << 15)
    if template == 1:
        return (g(-1, 0) | g(-2, 0) << 1 | g(-3, 0) << 2 | a[0] << 3 | g(2, -1) << 4 | g(1, -1) << 5 | g(0, -1) << 6 | g(-1, -1) << 7
                | g(-2, -1) << 8 | g(2, -2) << 9 | g(1, -2) << 10 | g(0, -2) << 11 | g(-1, -2) << 12)
    if template == 2:
        return (g(-1, 0) | g(-2, 0) << 1 | a[0] << 2 | g(1, -1) << 3 | g(0, -1) << 4 | g(-1, -1) << 5 | g(-2, -1) << 6
                | g(1, -2) << 7 | g(0, -2) << 8 | g(-1, -2) << 9)
    return (g(-1, 0) | g(-2, 0) << 1 | g(-3, 0) << 2 | g(-4, 0) << 3 | a[0] << 4 | g(1, -1) << 5 | g(0, -1) << 6
            | g(-1, -1) << 7 | g(-2, -1) << 8 | g(-3, -1) << 9)


def encode_generic(mq, contexts, b, template, at, typical=False):
    """6.2.5.7 in reverse: TPGD rows equal to the row above are skipped."""
    ltp = 0
    for y in range(b.shape[0]):
        if typical:
            above = b[y - 1] if y > 0 else np.zeros(b.shape[1], dtype=b.dtype)
            same = int(np.array_equal(b[y], above))
            mq.encode(contexts, TYPICAL[template], same ^ ltp)
            ltp = same
            if same:
                continue
        for x in range(b.shape[1]):
            mq.encode(contexts, generic_context(b, x, y, template, at), int(b[y, x]))


def refinement_context(g, r, x, y, dx, dy, template, at):
    rx, ry = x - dx, y - dy
    G = lambda i, j: pixel(g, x + i, y + j)  # noqa: E731
    R = lambda i, j: pixel(r, rx + i, ry + j)  # noqa: E731
    if template == 0:
        return (R(1, 1) | R(0, 1) << 1 | R(-1, 1) << 2 | R(1, 0) << 3 | R(0, 0) << 4 | R(-1, 0) << 5 | R(1, -1) << 6 | R(0, -1) << 7
                | R(at[1][0], at[1][1]) << 8 | G(-1, 0) << 9 | G(1, -1) << 10 | G(0, -1) << 11 | G(at[0][0], at[0][1]) << 12)
    return (R(1, 1) | R(0, 1) << 1 | R(1, 0) << 2 | R(0, 0) << 3 | R(-1, 0) << 4 | R(0, -1) << 5 | G(-1, 0) << 6 | G(1, -1) << 7
            | G(0, -1) << 8 | G(-1, -1) << 9)


def encode_refinement(mq, contexts, g, r, dx, dy, template, at, typical=False):
    """6.3.5.6 in reverse: on a typical row, pixels with a uniform 3 by 3 reference are not coded."""
    ltp = 0

    def uniform(x, y):
        values = {pixel(r, x - dx + i, y - dy + j) for i in (-1, 0, 1) for j in (-1, 0, 1)}
        return values.pop() if len(values) == 1 else None

    for y in range(g.shape[0]):
        if typical:
            row = int(all(uniform(x, y) in (None, int(g[y, x])) for x in range(g.shape[1])))
            mq.encode(contexts, 0x0010 if template == 0 else 0x0008, row ^ ltp)
            ltp = row
        for x in range(g.shape[1]):
            if ltp and uniform(x, y) is not None:
                continue
            mq.encode(contexts, refinement_context(g, r, x, y, dx, dy, template, at), int(g[y, x]))


# --- Segments (7.2, 7.4) ----------------------------------------------------------------------

def segment(number, kind, data, referred=(), page=1, length=None):
    header = struct.pack(">IB", number, kind) + bytes([len(referred) << 5])
    header += bytes(referred) if number <= 256 else b"".join(struct.pack(">H", r) for r in referred)
    header += bytes([page]) + struct.pack(">I", len(data) if length is None else length)
    return header + data


def page_info(width, height, default=0, override=True, striped=0):
    flags = (default << 2) | (0x40 if override else 0)
    return struct.pack(">IIIIBH", width, height, 0, 0, flags, striped)


def region_info(w, h, x, y, op=0):
    return struct.pack(">IIIIB", w, h, x, y, op)


def at_bytes(at):
    return b"".join(struct.pack(">bb", x, y) for x, y in at)


def generic_segment(b, x, y, template=0, at=None, typical=False, op=0):
    at = NOMINAL_AT[template] if at is None else at
    mq = MQ()
    encode_generic(mq, bytearray(1 << 16), b, template, at, typical)
    flags = (template << 1) | (8 if typical else 0)
    return region_info(b.shape[1], b.shape[0], x, y, op) + bytes([flags]) + at_bytes(at) + mq.flush()


# --- Huffman coding (Annex B) -----------------------------------------------------------------

class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        self.bits += [(value >> (count - 1 - k)) & 1 for k in range(count)]

    def align(self):
        while len(self.bits) % 8:
            self.bits.append(0)

    def bytes(self):
        self.align()
        return bytes(int("".join(map(str, self.bits[i:i + 8])), 2) for i in range(0, len(self.bits), 8))


class Table:
    """A table as B.2 lays it out: normal lines from low, then lower, upper and OOB lines, codes by B.3."""

    def __init__(self, low, ranges, lower, upper, oob):
        self.lines, current = [], low
        for prefix, rng in ranges:
            self.lines.append(("normal", current, prefix, rng))
            current += 1 << rng
        self.lines += [("lower", low - 1, lower, 32), ("upper", current, upper, 32), ("oob", 0, oob, 0)]
        self.low, self.high = low, current
        counts = [0] * 40
        for line in self.lines:
            if line[2]:
                counts[line[2]] += 1
        first, code, self.codes = [0] * 40, 0, {}
        for length in range(1, 33):
            first[length] = (first[length - 1] + (counts[length - 1] if length > 1 else 0)) << 1
            code = first[length]
            for index, line in enumerate(self.lines):
                if line[2] == length:
                    self.codes[index] = code
                    code += 1

    def put(self, bits, value):
        for index, (kind, low, prefix, rng) in enumerate(self.lines):
            if not prefix:
                continue
            if kind == "oob" and value is None:
                bits.put(self.codes[index], prefix)
                return
            if value is None:
                continue
            if kind == "normal" and low <= value < low + (1 << rng):
                bits.put(self.codes[index], prefix)
                bits.put(value - low, rng)
                return
            if kind == "lower" and value < self.low:
                bits.put(self.codes[index], prefix)
                bits.put(low - value, 32)
                return
            if kind == "upper" and value >= self.high:
                bits.put(self.codes[index], prefix)
                bits.put(value - low, 32)
                return
        raise ValueError(f"{value} cannot be coded")


B1 = Table(0, [(1, 4), (2, 8), (3, 16)], 0, 3, 0)
B4 = Table(1, [(1, 0), (2, 0), (3, 0), (4, 3), (5, 6)], 0, 5, 0)
B6 = Table(-2048, [(5, 10), (4, 9), (4, 8), (4, 7), (5, 6), (5, 5), (4, 5), (2, 7), (3, 7), (3, 8), (4, 9), (4, 10)], 6, 6, 0)
B8 = Table(-15, [(8, 3), (9, 1), (8, 1), (9, 0), (7, 0), (4, 0), (2, 1), (5, 0), (6, 0), (3, 4), (6, 1), (4, 4), (4, 5), (5, 6), (5, 7), (6, 7), (7, 8), (6, 10)], 9, 9, 2)
B11 = Table(1, [(1, 0), (2, 1), (4, 0), (4, 1), (5, 1), (5, 2), (6, 2), (7, 2), (7, 3), (7, 4), (7, 5), (7, 6)], 0, 7, 0)

# A custom delta width table with OOB (B.2): prefix lengths in 3 bits, range lengths in 2.
CUSTOM_DW = Table(-8, [(4, 3), (2, 2), (2, 2), (3, 3), (4, 3)], 5, 5, 3)


def table_segment(table, prefix_bits, range_bits):
    """7.4.13: the code table segment for a table built by Table."""
    flags = 1 | ((prefix_bits - 1) << 1) | ((range_bits - 1) << 4)
    bits = Bits()
    for kind, _, prefix, rng in table.lines:
        if kind == "normal":
            bits.put(prefix, prefix_bits)
            bits.put(rng, range_bits)
    for kind, _, prefix, _ in table.lines:
        if kind != "normal":
            bits.put(prefix, prefix_bits)
    return bytes([flags]) + struct.pack(">ii", table.low, table.high) + bits.bytes()


# --- Symbols and text --------------------------------------------------------------------------

def glyphs(text, size=1):
    """Black glyph bitmaps of PIL's default font, trimmed: the symbols."""
    out = {}
    for ch in dict.fromkeys(text.replace(" ", "")):
        image = Image.new("1", (12, 14), 0)
        ImageDraw.Draw(image).text((1, 0), ch, fill=1)
        a = np.array(image, dtype=np.uint8)
        ys, xs = np.nonzero(a)
        a = a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
        if size > 1:
            a = np.kron(a, np.ones((size, size), dtype=np.uint8))
        out[ch] = a
    return out


def symbol_dictionary_arith(symbols, template=0, at=None, exported=None):
    """7.4.2 with SDHUFF 0, SDREFAGG 0: height classes of directly coded symbols (6.5.8.1)."""
    at = NOMINAL_AT[template] if at is None else at
    order = sorted(range(len(symbols)), key=lambda i: (symbols[i].shape[0], -symbols[i].shape[1]))
    mq = MQ()
    ints, gb = Ints(mq), bytearray(1 << 16)
    height, k = 0, 0
    while k < len(order):
        h = symbols[order[k]].shape[0]
        ints.int("IADH", h - height)
        height, width = h, 0
        while k < len(order) and symbols[order[k]].shape[0] == h:
            s = symbols[order[k]]
            ints.int("IADW", s.shape[1] - width)  # widths fall within the class: negative deltas
            width = s.shape[1]
            encode_generic(mq, gb, s, template, at)
            k += 1
        ints.int("IADW", None)
    total = len(symbols)
    runs = [0, total] if exported is None else exported
    for run in runs:
        ints.int("IAEX", run)
    flags = template << 10
    body = struct.pack(">H", flags) + at_bytes(at) + struct.pack(">II", total if exported is None else sum(runs[1::2]), total)
    return body + mq.flush(), [symbols[i] for i in order]


def symbol_dictionary_huffman(symbols):
    """7.4.2 with SDHUFF 1: DH from B.4, DW from a custom table, collective bitmaps stored uncompressed (6.5.9)."""
    order = sorted(range(len(symbols)), key=lambda i: (symbols[i].shape[0], symbols[i].shape[1]))
    bits, height, k = Bits(), 0, 0
    while k < len(order):
        h = symbols[order[k]].shape[0]
        B4.put(bits, h - height)
        height, width, members = h, 0, []
        while k < len(order) and symbols[order[k]].shape[0] == h:
            s = symbols[order[k]]
            CUSTOM_DW.put(bits, s.shape[1] - width)
            width = s.shape[1]
            members.append(s)
            k += 1
        CUSTOM_DW.put(bits, None)
        B1.put(bits, 0)  # BMSIZE 0: uncompressed
        bits.align()
        collective = np.concatenate(members, axis=1)
        for row in collective:
            for v in row:
                bits.put(int(v), 1)
            bits.align()
    B1.put(bits, 0)
    B1.put(bits, len(symbols))
    flags = 1 | (3 << 4)  # SDHUFF, SDHUFFDW custom, SDHUFFDH B.4, BMSIZE and AGGINST B.1
    return struct.pack(">HII", flags, len(symbols), len(symbols)) + bits.bytes(), [symbols[i] for i in order]


def place(page, b, x, y, op=0):
    """Draws b with its top left at (x, y), clipped, combined as 6.4.5 and 7.4.1.5 say."""
    h, w = b.shape
    for j in range(h):
        for i in range(w):
            X, Y = x + i, y + j
            if 0 <= X < page.shape[1] and 0 <= Y < page.shape[0]:
                d, s = int(page[Y, X]), int(b[j, i])
                page[Y, X] = [d | s, d & s, d ^ s, 1 - (d ^ s), s][op]


def text_instances(line_specs, symbols):
    """(symbol index, bitmap, x, y) for strings laid out at given origins, symbols keyed by character."""
    keys = list(symbols)
    out = []
    for text, x0, y0 in line_specs:
        x = x0
        for ch in text:
            if ch == " ":
                x += 4
                continue
            b = symbols[ch]
            out.append([keys.index(ch), b, x, y0 + 10 - b.shape[0]])
            x += b.shape[1] + 1
    return out


def text_region_arith(w, h, instances, symbol_count, strips=1, corner=1, refine_template=None, refine_at=((-1, -1), (-1, -1)), ds_offset=0, op=0):
    """7.4.3 with SBHUFF 0. instances: (id, bitmap placed, x, y, refinement or None), where a
    refinement is (reference bitmap, rdw, rdh, rdx, rdy)."""
    mq = MQ()
    ints, gr = Ints(mq), bytearray(1 << 13)
    id_length = max(0, (symbol_count - 1).bit_length())
    right, bottom = bool(corner & 2), not corner & 1
    log_strips = strips.bit_length() - 1

    def coords(item):
        _, b, x, y, _ = item
        return (x + b.shape[1] - 1 if right else x), (y + b.shape[0] - 1 if bottom else y)

    groups = {}
    for item in instances:
        s, t = coords(item)
        groups.setdefault(t - t % strips, []).append((s, t, item))
    ints.int("IADT", 0)
    strip_t, first_s = 0, 0
    for base in sorted(groups):
        ints.int("IADT", (base - strip_t) // strips)
        strip_t = base
        members = sorted(groups[base], key=lambda m: m[0])
        cur_s = None
        for s, t, (sid, b, _, _, refinement) in members:
            size = b.shape[1] - 1
            pre = s - size if right else s
            if cur_s is None:
                ints.int("IAFS", pre - first_s)
                first_s = pre
            else:
                ints.int("IADS", pre - cur_s - ds_offset)
            if strips > 1:
                ints.int("IAIT", t - strip_t)
            ints.id(id_length, sid)
            if refine_template is not None:
                ints.int("IARI", 1 if refinement else 0)
                if refinement:
                    ref, rdw, rdh, rdx, rdy = refinement
                    for name, v in (("IARDW", rdw), ("IARDH", rdh), ("IARDX", rdx), ("IARDY", rdy)):
                        ints.int(name, v)
                    encode_refinement(mq, gr, b, ref, (rdw >> 1) + rdx, (rdh >> 1) + rdy, refine_template, list(refine_at))
            cur_s = s if right else s + size
        ints.int("IADS", None)
    flags = (1 if refine_template is not None else 0) << 1 | log_strips << 2 | corner << 4 | op << 7 | (ds_offset & 0x1F) << 10
    if refine_template is not None:
        flags |= refine_template << 15
    body = region_info(w, h, 0, 0) + struct.pack(">H", flags)
    if refine_template == 0:
        body += at_bytes(refine_at)
    return body + struct.pack(">I", len(instances)) + mq.flush()


def text_region_huffman(w, h, instances, symbol_count):
    """7.4.3 with SBHUFF 1: FS B.6, DS B.8, DT B.11, symbol IDs through a run coded code length table (7.4.3.1.7)."""
    bits = Bits()
    length = max(1, (symbol_count - 1).bit_length())
    # Run codes: the code length itself gets code '0', run code 32 (repeat the last length) gets '1'.
    for runcode in range(35):
        bits.put(1 if runcode in (length, 32) else 0, 4)
    i = 0
    while i < symbol_count:
        if i > 0 and symbol_count - i >= 3:
            n = min(6, symbol_count - i)
            bits.put(1, 1)
            bits.put(n - 3, 2)
            i += n
        else:
            bits.put(0, 1)
            i += 1
    bits.align()
    # Every symbol has the same length, so the codes are the IDs (B.3).
    groups = {}
    for item in instances:
        groups.setdefault(item[3], []).append(item)
    B11.put(bits, 1)
    strip_t, first_s = -1, 0
    for t in sorted(groups):
        B11.put(bits, t - strip_t)
        strip_t = t
        cur_s = None
        for sid, b, x, _, _ in sorted(groups[t], key=lambda m: m[2]):
            if cur_s is None:
                B6.put(bits, x - first_s)
                first_s = x
            else:
                B8.put(bits, x - cur_s)
            bits.put(sid, length)
            cur_s = x + b.shape[1] - 1
        B8.put(bits, None)
    flags = 1 | (1 << 4)  # SBHUFF, top left corner
    return region_info(w, h, 0, 0) + struct.pack(">HHI", flags, 0, len(instances)) + bits.bytes()


# --- PDF and PDFium ---------------------------------------------------------------------------

def pdf(width, height, data, globals_data, mask=False):
    objects = []

    def add(body):
        objects.append(body)
        return len(objects)

    globals_ref = add(b"<</Length %d>>\nstream\n" % len(globals_data) + globals_data + b"\nendstream") if globals_data else None
    parms = b"/DecodeParms<</JBIG2Globals %d 0 R>>" % globals_ref if globals_ref else b""
    kind = b"/ImageMask true" if mask else b"/ColorSpace/DeviceGray/BitsPerComponent 1"
    image = add(b"<</Type/XObject/Subtype/Image/Width %d/Height %d%s/Filter/JBIG2Decode%s/Length %d>>\nstream\n" % (width, height, kind, parms, len(data)) + data + b"\nendstream")
    content = b"q %d 0 0 %d 0 0 cm /Im Do Q" % (width, height)
    contents = add(b"<</Length %d>>\nstream\n" % len(content) + content + b"\nendstream")
    page = add(b"<</Type/Page/Parent %d 0 R/MediaBox[0 0 %d %d]/Resources<</XObject<</Im %d 0 R>>>>/Contents %d 0 R>>" % (len(objects) + 2, width, height, image, contents))
    pages = add(b"<</Type/Pages/Kids[%d 0 R]/Count 1>>" % page)
    catalog = add(b"<</Type/Catalog/Pages %d 0 R>>" % pages)
    out = bytearray(b"%PDF-1.5\n")
    offsets = []
    for number, body in enumerate(objects, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<</Size %d/Root %d 0 R>>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, catalog, xref)
    return bytes(out)


def render(width, height, data, globals_data, mask=False):
    """PDFium's page at one pixel per sample: True for black."""
    document = pdfium.PdfDocument(pdf(width, height, data, globals_data, mask))
    bitmap = document[0].render(scale=1, grayscale=True, no_smoothimage=True, no_smoothpath=True, prefer_bgrx=False)
    pixels = bitmap.to_numpy()
    pixels = pixels[:, :, 0] if pixels.ndim == 3 else pixels
    assert pixels.shape == (height, width), pixels.shape
    assert set(np.unique(pixels)) <= {0, 255}, f"PDFium shades: {np.unique(pixels)}"
    return pixels == 0


def write(name, width, height, data, globals_data=b"", expected=None):
    black = render(width, height, data, globals_data)
    assert (render(width, height, data, globals_data, mask=True) == black).all(), f"{name}: mask differs"
    if expected is not None:
        assert (black == expected.astype(bool)).all(), f"{name}: PDFium disagrees with the encoder, {(black != expected.astype(bool)).sum()} pixels"
    assert black.any() and not black.all(), f"{name}: blank"
    (OUT / f"{name}.jb2").write_bytes(data)
    if globals_data:
        (OUT / f"{name}.globals.jb2").write_bytes(globals_data)
    Image.fromarray(~black).convert("1").save(OUT / f"{name}.png", optimize=True)
    print(f"{name}: {width}x{height}, {len(data)} + {len(globals_data)} bytes, {black.sum()} black")


# --- Fixtures ---------------------------------------------------------------------------------

def scene(width, height, seed):
    """Black on white: text, a rule, a box, a little noise."""
    image = Image.new("1", (width, height), 0)
    draw = ImageDraw.Draw(image)
    draw.text((4, 3), "JBIG2 generic region", fill=1)
    draw.text((4, 18), "0123456789 ABCDEFGHIJ", fill=1)
    draw.rectangle((4, 34, width - 5, 35), fill=1)
    draw.rectangle((width - 40, 40, width - 8, height - 6), fill=1)
    draw.line((0, height - 1, width // 2, 40), fill=1)
    a = np.array(image, dtype=np.uint8)
    rng = np.random.default_rng(seed)
    noise = rng.random(a.shape) < 0.004
    a[noise] ^= 1
    return a


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    check_annex_h2()

    # Annex H, split into globals and pages.
    stream = annex_h()
    assert len(stream) == 0x35C
    parts = {}
    for number, kind, page, raw in segments(stream):
        if kind != 51:
            parts.setdefault(page, b"")
            parts[page] += raw
    sizes = {1: (64, 56), 2: (64, 56), 3: (37, 8)}
    for page in (1, 2, 3):
        write(f"annexh-p{page}", *sizes[page], parts[page], parts[0])
    assert (np.array(Image.open(OUT / "annexh-p1.png")) == np.array(Image.open(OUT / "annexh-p2.png"))).all(), "H.1: pages 1 and 2 differ"

    width, height = 160, 72
    page = scene(width, height, 1)

    def generic_page(**kwargs):
        return segment(0, 48, page_info(width, height)) + segment(1, 38, generic_segment(page, 0, 0, **kwargs)) + segment(2, 49, b"")

    # Generic regions: each template, moved AT pixels, TPGD.
    write("generic-t0", width, height, generic_page(template=0), expected=page)
    write("generic-t0-at", width, height, generic_page(template=0, at=[(-1, -1), (-4, -1), (1, -2), (-5, -2)]), expected=page)
    write("generic-t1-tpgd", width, height, generic_page(template=1, typical=True), expected=page)
    write("generic-t2-at", width, height, generic_page(template=2, at=[(-2, -2)]), expected=page)
    write("generic-t3-tpgd", width, height, generic_page(template=3, at=[(1, -1)], typical=True), expected=page)

    # MMR: the same page as T.6 through libtiff, which codes 0 bits white like JBIG2.
    buffer = io.BytesIO()
    Image.fromarray(page.astype(bool)).save(buffer, "TIFF", compression="group4", tiffinfo={278: height})
    tiff = buffer.getvalue()
    (ifd,) = struct.unpack_from("<I", tiff, 4)
    (count,) = struct.unpack_from("<H", tiff, ifd)
    tags = {}
    for i in range(count):
        tag, kind, n, value = struct.unpack_from("<HHII", tiff, ifd + 2 + 12 * i)
        tags[tag] = value & 0xFFFF if kind == 3 and n == 1 else value
    mmr = tiff[tags[273]:tags[273] + tags[279]]
    write("generic-mmr", width, height, segment(0, 48, page_info(width, height)) + segment(1, 38, region_info(width, height, 0, 0) + b"\x01" + mmr) + segment(2, 49, b""), expected=page)

    # Composition: default pixel 1, regions drawn with AND, XOR and REPLACE, partly off the page.
    composed = np.ones((height, width), dtype=np.uint8)
    a, b = page[:40, :100].copy(), np.flipud(page[20:60, 40:150]).copy()
    place(composed, a, 10, 5, 1)
    place(composed, b, 70, 40, 2)
    place(composed, a[:20, :50], width - 30, 50, 4)
    data = segment(0, 48, page_info(width, height, default=1))
    data += segment(1, 38, generic_segment(a, 10, 5, template=1, op=1))
    data += segment(2, 38, generic_segment(b, 70, 40, template=2, op=2))
    data += segment(3, 38, generic_segment(a[:20, :50], width - 30, 50, template=3, op=4)) + segment(4, 49, b"")
    write("compose", width, height, data, expected=composed)

    # Striped page of unknown height, ended by end of stripe segments (7.4.8.2, 7.4.10).
    data = segment(0, 48, page_info(width, 0xFFFFFFFF, striped=0x8000 | 40))
    data += segment(1, 38, generic_segment(page[:36], 0, 0)) + segment(2, 50, struct.pack(">I", 35))
    data += segment(3, 38, generic_segment(page[36:], 0, 36, template=1)) + segment(4, 50, struct.pack(">I", height - 1)) + segment(5, 49, b"")
    write("striped", width, height, data, expected=page)

    # Refinement: an intermediate generic region of a damaged page, refined into the page with
    # TPGR (template 0, moved AT), then the page area refined in place (template 1).
    damaged = page.copy()
    damaged[10:14, 20:60] ^= 1
    damaged[50:52, :] = 0
    mq = MQ()
    encode_refinement(mq, bytearray(1 << 13), page, damaged, 0, 0, 0, [(-1, -1), (1, 1)], typical=True)
    first = region_info(width, height, 0, 0, 4) + bytes([0x02]) + at_bytes([(-1, -1), (1, 1)]) + mq.flush()
    final = page.copy()
    final[30:40, 100:130] = 1
    mq = MQ()
    encode_refinement(mq, bytearray(1 << 13), final[28:44, 96:136], page[28:44, 96:136], 0, 0, 1, None)
    second = region_info(40, 16, 96, 28, 4) + bytes([0x01]) + mq.flush()
    data = segment(0, 48, page_info(width, height)) + segment(1, 36, generic_segment(damaged, 0, 0))
    data += segment(2, 42, first, referred=[1]) + segment(3, 42, second) + segment(4, 49, b"")
    write("refine", width, height, data, expected=final)

    # Symbols and text, arithmetic: the dictionary in JBIG2Globals, the text region on the page,
    # two strips, bottom left corner, a DS offset, and refined instances.
    symbols = glyphs("The quick brown fox jumps over 13 lazy dogs.")
    order_text = "The quick brown fox jumps over 13 lazy dogs."
    keys = list(symbols)
    dictionary, ordered = symbol_dictionary_arith([symbols[k] for k in keys], template=0)
    by_id = {id(s): i for i, s in enumerate(ordered)}
    tw, th = 220, 40
    expected = np.zeros((th, tw), dtype=np.uint8)
    instances = []
    for sid, bmp, x, y in text_instances([(order_text[:22], 3, 2), (order_text[22:], 3, 20)], symbols):
        index = by_id[id(symbols[keys[sid]])]
        instances.append((index, bmp, x, y, None))
        place(expected, bmp, x, y)
    gdata = segment(0, 0, dictionary, page=0)
    text = text_region_arith(tw, th, instances, len(ordered), strips=2, corner=0, ds_offset=1)
    data = segment(1, 48, page_info(tw, th)) + segment(2, 6, text, referred=[0]) + segment(3, 49, b"")
    write("text-arith", tw, th, data, gdata, expected=expected)

    # Refined instances: some glyphs changed in place, some grown, template 0 and 1.
    for template in (0, 1):
        expected = np.zeros((th, tw), dtype=np.uint8)
        refined = []
        for n, (index, bmp, x, y, _) in enumerate(instances):
            if n % 3 == 0:
                grown = np.zeros((bmp.shape[0] + 1, bmp.shape[1] + 2), dtype=np.uint8)
                grown[1:, 1:-1] = bmp
                grown[0, :] = 1
                refined.append((index, grown, x, y, (bmp, 2, 1, 0, 1)))
                place(expected, grown, x, y)
            elif n % 3 == 1:
                changed = bmp.copy()
                changed[-1, :] ^= 1
                refined.append((index, changed, x, y, (bmp, 0, 0, 0, 0)))
                place(expected, changed, x, y)
            else:
                refined.append((index, bmp, x, y, None))
                place(expected, bmp, x, y)
        text = text_region_arith(tw, th, refined, len(ordered), refine_template=template, refine_at=((-1, -1), (-1, -1)) if template == 0 else ())
        data = segment(1, 48, page_info(tw, th)) + segment(2, 6, text, referred=[0]) + segment(3, 49, b"")
        write(f"text-refine-t{template}", tw, th, data, gdata, expected=expected)

    # Symbols and text, Huffman: a custom table segment, the dictionary and the region on the page.
    dictionary, ordered = symbol_dictionary_huffman([symbols[k] for k in keys])
    by_id = {id(s): i for i, s in enumerate(ordered)}
    expected = np.zeros((th, tw), dtype=np.uint8)
    instances = []
    for sid, bmp, x, y in text_instances([(order_text[:22], 3, 2), (order_text[22:], 3, 20)], symbols):
        instances.append((by_id[id(symbols[keys[sid]])], bmp, x, y, None))
        place(expected, bmp, x, y)
    data = segment(0, 48, page_info(tw, th)) + segment(1, 53, table_segment(CUSTOM_DW, 3, 2))
    data += segment(2, 0, dictionary, referred=[1]) + segment(3, 6, text_region_huffman(tw, th, instances, len(ordered)), referred=[2])
    data += segment(4, 49, b"")
    write("text-huffman", tw, th, data, expected=expected)


if __name__ == "__main__":
    main()
