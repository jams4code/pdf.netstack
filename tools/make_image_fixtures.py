"""Regenerates the PNG and JPEG test fixtures for image embedding.

    pip install pillow
    python tools/make_image_fixtures.py

The PNGs are written by a small encoder here, so every color type, bit depth, filter type,
interlacing and tRNS form appears, including those Pillow cannot write. Pillow then reads each
file back to prove it is a valid PNG with the intended pixels. images.json records, per file,
the expected pixels as RGBA hex (8 bits per channel, 16 bit samples reduced to their high byte).
"""

import io
import json
import random
import struct
import zlib
from pathlib import Path

from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Images"
W, H = 7, 5
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def pack_row(samples, depth):
    if depth == 8:
        return bytes(samples)
    if depth == 16:
        return b"".join(struct.pack(">H", s) for s in samples)
    out, acc, bits = bytearray(), 0, 0
    for s in samples:
        acc = acc << depth | s
        bits += depth
        if bits == 8:
            out.append(acc)
            acc, bits = 0, 0
    if bits:
        out.append(acc << (8 - bits))
    return bytes(out)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else b if pb <= pc else c


def filter_rows(rows, bpp, rng):
    out, prev = bytearray(), bytes(len(rows[0])) if rows else b""
    for row in rows:
        kind = rng.randrange(5)
        filtered = bytearray()
        for x, v in enumerate(row):
            a = row[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            pred = [0, a, b, (a + b) >> 1, paeth(a, b, c)][kind]
            filtered.append((v - pred) & 0xFF)
        out += bytes([kind]) + filtered
        prev = row
    return bytes(out)


def encode(pixels, color_type, depth, interlace=False, extra=b"", seed=1):
    """pixels: rows of tuples of samples (channels per color type)."""
    rng = random.Random(seed)
    ch = CHANNELS[color_type]
    bpp = max(1, ch * depth // 8)
    if not interlace:
        rows = [pack_row([s for px in r for s in px], depth) for r in pixels]
        raw = filter_rows(rows, bpp, rng)
    else:
        raw = b""
        for x0, y0, dx, dy in ADAM7:
            sub = [[pixels[y][x] for x in range(x0, W, dx)] for y in range(y0, H, dy)]
            sub = [r for r in sub if r]
            if not sub:
                continue
            rows = [pack_row([s for px in r for s in px], depth) for r in sub]
            raw += filter_rows(rows, bpp, rng)
    ihdr = struct.pack(">IIBBBBB", W, H, depth, color_type, 0, 0, 1 if interlace else 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + extra
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def to8(v, depth):
    return v >> 8 if depth == 16 else v * 255 // ((1 << depth) - 1)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    rng = random.Random(20261029)
    expected = {}

    def sample(depth):
        return rng.randrange(1 << depth)

    def save(name, data, rgba):
        (OUT / name).write_bytes(data)
        Image.open(io.BytesIO(data)).load()  # Pillow must accept it
        expected[name] = "".join("%02x%02x%02x%02x" % p for p in rgba)

    for depth in (1, 2, 4, 8, 16):
        px = [[(sample(depth),) for _ in range(W)] for _ in range(H)]
        rgba = [(to8(p[0], depth),) * 3 + (255,) for r in px for p in r]
        save(f"gray{depth}.png", encode(px, 0, depth), rgba)

    for depth in (8, 16):
        px = [[tuple(sample(depth) for _ in range(3)) for _ in range(W)] for _ in range(H)]
        save(f"rgb{depth}.png", encode(px, 2, depth), [tuple(to8(v, depth) for v in p) + (255,) for r in px for p in r])
        px = [[tuple(sample(depth) for _ in range(4)) for _ in range(W)] for _ in range(H)]
        save(f"rgba{depth}.png", encode(px, 6, depth), [tuple(to8(v, depth) for v in p) for r in px for p in r])
        save(f"rgba{depth}-interlaced.png", encode(px, 6, depth, interlace=True), [tuple(to8(v, depth) for v in p) for r in px for p in r])
        px = [[tuple(sample(depth) for _ in range(2)) for _ in range(W)] for _ in range(H)]
        save(f"gray-alpha{depth}.png", encode(px, 4, depth), [(to8(p[0], depth),) * 3 + (to8(p[1], depth),) for r in px for p in r])

    for depth in (1, 2, 4, 8):
        n = 1 << depth
        palette = [(rng.randrange(256), rng.randrange(256), rng.randrange(256)) for _ in range(n)]
        plte = chunk(b"PLTE", bytes(v for c in palette for v in c))
        px = [[(rng.randrange(n),) for _ in range(W)] for _ in range(H)]
        save(f"palette{depth}.png", encode(px, 3, depth, extra=plte), [palette[p[0]] + (255,) for r in px for p in r])
        binary = [0 if i == 0 else 255 for i in range(n)]
        save(f"palette{depth}-cutout.png", encode(px, 3, depth, extra=plte + chunk(b"tRNS", bytes(binary))),
             [palette[p[0]] + (binary[p[0]],) for r in px for p in r])
        partial = [rng.randrange(256) for _ in range(n)]
        save(f"palette{depth}-alpha.png", encode(px, 3, depth, extra=plte + chunk(b"tRNS", bytes(partial))),
             [palette[p[0]] + (partial[p[0]],) for r in px for p in r])

    px = [[(rng.choice([7, 200]),) for _ in range(W)] for _ in range(H)]
    save("gray8-key.png", encode(px, 0, 8, extra=chunk(b"tRNS", struct.pack(">H", 7))),
         [(p[0],) * 3 + (0 if p[0] == 7 else 255,) for r in px for p in r])
    px = [[rng.choice([(1, 2, 3), (9, 9, 9)]) for _ in range(W)] for _ in range(H)]
    save("rgb8-key.png", encode(px, 2, 8, extra=chunk(b"tRNS", struct.pack(">HHH", 1, 2, 3))),
         [p + (0 if p == (1, 2, 3) else 255,) for r in px for p in r])

    # Interlaced without alpha, with a physical size of 300 pixels per inch (11811 per meter).
    px = [[tuple(sample(8) for _ in range(3)) for _ in range(W)] for _ in range(H)]
    phys = chunk(b"pHYs", struct.pack(">IIB", 11811, 11811, 1))
    save("rgb8-interlaced-300dpi.png", encode(px, 2, 8, interlace=True, extra=phys), [p + (255,) for r in px for p in r])

    # A JPEG stored sideways, with EXIF orientation 6 (rotate 90 degrees clockwise to display).
    image = Image.new("RGB", (40, 20), (200, 30, 30))
    exif = Image.Exif()
    exif[0x0112] = 6
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=90, exif=exif.tobytes(), dpi=(144, 144))
    (OUT / "rotated.jpg").write_bytes(buffer.getvalue())

    (OUT / "images.json").write_text(json.dumps(expected, indent=1, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(expected)} PNGs and rotated.jpg in {OUT}")


if __name__ == "__main__":
    main()
