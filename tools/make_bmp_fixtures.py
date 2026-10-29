"""Regenerates the BMP test fixtures, and the RGBA pixels Pillow decodes from each.

    rgb24.bmp      24 bit, bottom up, rows padded (Pillow)
    pal8.bmp       8 bit palette (Pillow)
    mono1.bmp      1 bit (Pillow)
    pal4.bmp       4 bit palette
    pal2.bmp       2 bit palette (Pillow does not read these: the reference is computed)
    rle8.bmp       RLE8 with literal runs, a jump and an early line end
    rle4.bmp       RLE4 with odd literal runs and a jump
    rgb565.bmp     16 bit bit fields, 5-6-5
    rgb555.bmp     16 bit, no masks: 5-5-5
    rgba32.bmp     32 bit, version 5 header with an alpha mask
    topdown.bmp    24 bit, negative height
    core24.bmp     24 bit with the OS/2 core header

Run length references are the palette applied to the indexes the encoder wrote, not Pillow's
decode: Pillow reads four bytes for a jump where the format has two, and drops the last pixel of
an odd RLE4 literal run. Pixels a run length image never reaches decode as transparent, so their
reference pixel is transparent too.

    pip install pillow numpy
    python tools/make_bmp_fixtures.py
"""

import struct
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Bmp"


def card(width, height):
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    r = 128 + 100 * np.sin(x / 7.0) * np.cos(y / 11.0)
    g = 255 * x / max(1, width - 1)
    b = 255 * ((x // 12 + y // 12) % 2)
    return np.clip(np.stack([r, g, b], axis=-1), 0, 255).astype(np.uint8)


def bmp(width, height, bits, rows, compression=0, palette=None, masks=None, header=40, top_down=False, ppm=2835):
    """A bitmap file: rows already encoded, palette as RGB triples, masks inside a v5 header or after a v1 one."""
    if header == 12:
        info = struct.pack("<IHHHH", 12, width, height, 1, bits)
        table = b"".join(bytes([b, g, r]) for r, g, b in palette or [])
    else:
        info = struct.pack("<IiiHHIIiiII", header, width, -height if top_down else height, 1, bits, compression,
                           len(rows), ppm, ppm, len(palette or []), 0)
        extra = b""
        if header == 124:
            red, green, blue, alpha = masks
            extra = struct.pack("<IIII", red, green, blue, alpha) + b"BGRs" + bytes(36) + bytes(12) + struct.pack("<IIII", 4, 0, 0, 0)
            masks = None
        info += extra
        if masks:
            info += b"".join(struct.pack("<I", m) for m in masks)
        table = b"".join(bytes([b, g, r, 0]) for r, g, b in palette or [])
    offset = 14 + len(info) + len(table)
    return b"BM" + struct.pack("<IHHI", offset + len(rows), 0, 0, offset) + info + table + rows


def pad(row):
    return row + bytes((-len(row)) % 4)


def pack(indexes, bits):
    out = bytearray()
    per = 8 // bits
    for i in range(0, len(indexes), per):
        v = 0
        for k in range(per):
            v <<= bits
            if i + k < len(indexes):
                v |= int(indexes[i + k])
        out.append(v)
    return bytes(out)


def quantized(width, height, colors):
    image = Image.fromarray(card(width, height)).quantize(colors)
    return np.array(image), image.getpalette()[: 3 * colors]


def triples(flat):
    return [tuple(flat[i:i + 3]) for i in range(0, len(flat), 3)]


def save_reference(name, rgba):
    Image.fromarray(np.ascontiguousarray(rgba, dtype=np.uint8)).save(OUT / f"{name}.png")


def pillow_reference(name):
    save_reference(name, np.array(Image.open(OUT / f"{name}.bmp").convert("RGBA")))


def packed_image(name, width, height, bits):
    idx, pal = quantized(width, height, 1 << bits)
    rows = b"".join(pad(pack(idx[y], bits)) for y in reversed(range(height)))
    (OUT / f"{name}.bmp").write_bytes(bmp(width, height, bits, rows, palette=triples(pal)))
    if bits == 2:
        # Pillow does not read 2 bit bitmaps; the reference is the palette applied to the indexes.
        colors = np.array(triples(pal), np.uint8)[idx]
        save_reference(name, np.dstack([colors, np.full(idx.shape, 255, np.uint8)]))
    else:
        pillow_reference(name)


def rle8(width, height):
    idx, pal = quantized(width, height, 64)
    out = bytearray()
    covered = np.zeros((height, width), bool)
    for y in reversed(range(height)):
        row = idx[y]
        line = height - 1 - y
        if line == 3:
            # Leave the rest of this line and two columns of the next untouched.
            out += bytes([0, 2, 2, 1])
            covered[y, :0] = True
            continue
        start = 2 if line == 4 else 0
        end = width - 7 if line == 6 else width
        x = start
        while x < end:
            run = 1
            while x + run < end and row[x + run] == row[x] and run < 255:
                run += 1
            if run >= 3:
                out += bytes([run, row[x]])
                x += run
                continue
            lit = 0
            while x + lit < end and lit < 255 and not (x + lit + 2 < end and row[x + lit] == row[x + lit + 1] == row[x + lit + 2]):
                lit += 1
            if lit < 3:
                for k in range(lit):
                    out += bytes([1, row[x + k]])
            else:
                out += bytes([0, lit]) + bytes(row[x:x + lit]) + bytes(lit & 1)
            x += lit
        covered[y, start:end] = True
        out += bytes([0, 0])
    out += bytes([0, 1])
    return idx, pal, bytes(out), covered


def rle4(width, height):
    idx, pal = quantized(width, height, 16)
    out = bytearray()
    covered = np.zeros((height, width), bool)
    for y in reversed(range(height)):
        row = [int(v) for v in idx[y]]
        line = height - 1 - y
        x = 0
        if line == 2:
            # Jump three pixels right and stay on the line.
            out += bytes([0, 2, 3, 0])
            x = 3
        while x < width:
            # Two alternating colors repeat as a run, else up to 9 pixels go as a literal.
            a, b = row[x], row[x + 1] if x + 1 < width else row[x]
            run = 1
            while x + run < width and row[x + run] == (a if run % 2 == 0 else b) and run < 255:
                run += 1
            if run >= 4:
                out += bytes([run, a << 4 | b])
                x += run
                continue
            lit = min(9 if x % 2 else 7, width - x)
            if lit < 3:
                out += bytes([lit, row[x] << 4 | (row[x + 1] if lit > 1 else 0)])
            else:
                chunk = row[x:x + lit] + [0]
                data = bytes(chunk[i] << 4 | chunk[i + 1] for i in range(0, lit, 2))
                out += bytes([0, lit]) + data + bytes(len(data) & 1)
            x += lit
        covered[y, 3 if line == 2 else 0:] = True
        out += bytes([0, 0])
    out += bytes([0, 1])
    return idx, pal, bytes(out), covered


def run_length(name, width, height, encoder, bits):
    idx, pal, rows, covered = encoder(width, height)
    (OUT / f"{name}.bmp").write_bytes(bmp(width, height, bits, rows, compression=1 if bits == 8 else 2, palette=triples(pal)))
    colors = np.array(triples(pal), np.uint8)[idx]
    rgba = np.dstack([colors, np.full(idx.shape, 255, np.uint8)])
    rgba[~covered] = 0
    save_reference(name, rgba)


def sixteen(name, width, height, masks):
    rgb = card(width, height).astype(np.uint32)
    if masks:
        v = (rgb[..., 0] >> 3) << 11 | (rgb[..., 1] >> 2) << 5 | rgb[..., 2] >> 3
    else:
        v = (rgb[..., 0] >> 3) << 10 | (rgb[..., 1] >> 3) << 5 | rgb[..., 2] >> 3
    rows = b"".join(pad(v[y].astype("<u2").tobytes()) for y in reversed(range(height)))
    (OUT / f"{name}.bmp").write_bytes(bmp(width, height, 16, rows, compression=3 if masks else 0, masks=masks))
    pillow_reference(name)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    rgb = card(97, 61)
    Image.fromarray(rgb).save(OUT / "rgb24.bmp")
    pillow_reference("rgb24")
    Image.fromarray(rgb).quantize(200).save(OUT / "pal8.bmp")
    pillow_reference("pal8")
    Image.fromarray(rgb).convert("1").save(OUT / "mono1.bmp")
    pillow_reference("mono1")

    packed_image("pal4", 37, 23, 4)
    packed_image("pal2", 29, 17, 2)
    run_length("rle8", 61, 23, rle8, 8)
    run_length("rle4", 45, 19, rle4, 4)
    sixteen("rgb565", 41, 27, [0xF800, 0x07E0, 0x001F])
    sixteen("rgb555", 41, 27, None)

    # 32 bit BGRA with a version 5 header naming the alpha mask.
    w, h = 33, 21
    rgba = np.dstack([card(w, h), (np.arange(w)[None, :] * 255 // (w - 1)).repeat(h, 0).astype(np.uint8)])
    rows = b"".join(rgba[y][:, [2, 1, 0, 3]].tobytes() for y in reversed(range(h)))
    (OUT / "rgba32.bmp").write_bytes(bmp(w, h, 32, rows, compression=3, header=124, masks=[0xFF0000, 0xFF00, 0xFF, 0xFF000000]))
    pillow_reference("rgba32")

    w, h = 27, 13
    rgb = card(w, h)
    rows = b"".join(pad(rgb[y][:, ::-1].tobytes()) for y in range(h))
    (OUT / "topdown.bmp").write_bytes(bmp(w, h, 24, rows, top_down=True))
    pillow_reference("topdown")

    rows = b"".join(pad(rgb[y][:, ::-1].tobytes()) for y in reversed(range(h)))
    (OUT / "core24.bmp").write_bytes(bmp(w, h, 24, rows, header=12))
    pillow_reference("core24")


if __name__ == "__main__":
    main()
