"""Regenerates the TIFF test fixtures and their reference pixels.

Pillow (with libtiff) writes the fax, JPEG, multi-page and BigTIFF files. A small writer below
covers what Pillow does not: tiles, big endian files, planar samples, 16 bit LZW with the
predictor, 4 bit palettes, associated alpha, LSB first fill order and an orientation tag.

Each name.tif has name.png, the RGBA pixels Pillow decodes from it, except where Pillow does not
read the file faithfully (16 bit color, CMYK, associated alpha): there name.raw holds the samples
the decoder must produce, rows top to bottom, 16 bit values big endian. orientation.png holds the
pixels as stored, since Pillow turns that page as it reads it.

    pip install pillow numpy
    python tools/make_tiff_fixtures.py
"""

import struct
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Tiff"


def card(width, height):
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    r = 128 + 100 * np.sin(x / 7.0) * np.cos(y / 11.0)
    g = 255 * x / max(1, width - 1)
    b = 255 * ((x // 12 + y // 12) % 2)
    return np.clip(np.stack([r, g, b], axis=-1), 0, 255).astype(np.uint8)


def art(width, height):
    """1 bit line art: stripes, a box and a diagonal."""
    y, x = np.mgrid[0:height, 0:width]
    on = ((x // 5 + y // 7) % 3 == 0) | ((x > 10) & (x < 30) & (y > 5) & (y < 20)) | (abs(x - 2 * y) < 2)
    return on


def noise16(width, height, channels, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    base = (x * 900 + y * 500)[..., None] + np.arange(channels) * 7000
    return ((base + rng.integers(0, 300, (height, width, channels))) % 65536).astype(np.uint16)


# ---------------------------------------------------------------- encoders


def lzw(data):
    """TIFF LZW: MSB first codes of 9 to 12 bits, the code size growing one code early."""
    out, acc, nbits = bytearray(), 0, 0

    def emit(code, size):
        nonlocal acc, nbits
        acc = (acc << size) | code
        nbits += size
        while nbits >= 8:
            nbits -= 8
            out.append((acc >> nbits) & 0xFF)

    table, nxt, size = {bytes([i]): i for i in range(256)}, 258, 9
    emit(256, size)
    w = b""
    for byte in data:
        c = bytes([byte])
        if w + c in table:
            w += c
            continue
        emit(table[w], size)
        table[w + c] = nxt
        nxt += 1
        w = c
        if nxt == 4094:
            emit(256, size)
            table, nxt, size = {bytes([i]): i for i in range(256)}, 258, 9
        elif nxt >= 1 << size:
            size += 1
    if w:
        emit(table[w], size)
        if nxt >= (1 << size) - 1 and size < 12:
            size += 1
    emit(257, size)
    if nbits:
        out.append((acc << (8 - nbits)) & 0xFF)
    return bytes(out)


def packbits(data):
    out, i = bytearray(), 0
    while i < len(data):
        run = 1
        while i + run < len(data) and data[i + run] == data[i] and run < 128:
            run += 1
        if run >= 3:
            out += bytes([257 - run, data[i]])
            i += run
            continue
        j = i
        while j < len(data) and j - i < 128 and not (j + 2 < len(data) and data[j] == data[j + 1] == data[j + 2]):
            j += 1
        out += bytes([j - i - 1]) + data[i:j]
        i = j
    return bytes(out)


def differenced(array):
    """Each sample minus the same channel of the pixel to its left, per row."""
    d = array.astype(np.int64)
    d[:, 1:] -= array[:, :-1].astype(np.int64)
    return (d % (256 if array.dtype == np.uint8 else 65536)).astype(array.dtype)


# ---------------------------------------------------------------- writer

SHORT, LONG, RATIONAL, UNDEFINED = 3, 4, 5, 7


def write_tiff(name, width, height, tags, chunks, order="<", tiled=False):
    """A one page TIFF: tags as {tag: (type, values)}, chunks the strips or tiles in order."""
    data = bytearray(b"II*\0" if order == "<" else b"MM\0*")
    data += b"\0\0\0\0"
    offsets = []
    for chunk in chunks:
        offsets.append(len(data))
        data += chunk
        if len(data) % 2:
            data += b"\0"
    tags = dict(tags)
    tags[324 if tiled else 273] = (LONG, offsets)
    tags[325 if tiled else 279] = (LONG, [len(c) for c in chunks])
    tags[256] = (LONG, [width])
    tags[257] = (LONG, [height])

    ifd = len(data)
    struct.pack_into(order + "I", data, 4, ifd)
    entries = sorted(tags.items())
    extra = ifd + 2 + 12 * len(entries) + 4
    body, tail = bytearray(struct.pack(order + "H", len(entries))), bytearray()
    for tag, (kind, values) in entries:
        if kind == RATIONAL:
            raw = b"".join(struct.pack(order + "II", n, d) for n, d in values)
        elif kind == UNDEFINED:
            raw = bytes(values)
        else:
            raw = b"".join(struct.pack(order + ("H" if kind == SHORT else "I"), v) for v in values)
        count = len(values)
        if len(raw) <= 4:
            field = raw + bytes(4 - len(raw))
        else:
            field = struct.pack(order + "I", extra + len(tail))
            tail += raw
            if len(tail) % 2:
                tail += b"\0"
        body += struct.pack(order + "HHI", tag, kind, count) + field
    body += b"\0\0\0\0"
    (OUT / f"{name}.tif").write_bytes(bytes(data + body + tail))


def strips(array, rows_per_strip):
    return [array[y:y + rows_per_strip] for y in range(0, array.shape[0], rows_per_strip)]


def tiles(array, tw, th):
    h, w = array.shape[:2]
    out = []
    for ty in range(0, h, th):
        for tx in range(0, w, tw):
            tile = np.zeros((th, tw) + array.shape[2:], array.dtype)
            part = array[ty:ty + th, tx:tx + tw]
            tile[:part.shape[0], :part.shape[1]] = part
            out.append(tile)
    return out


def be(array):
    """Samples as the decoder outputs them: 16 bit values big endian."""
    return array.astype(">u2").tobytes() if array.dtype == np.uint16 else array.tobytes()


def pillow_reference(name):
    Image.open(OUT / f"{name}.tif").convert("RGBA").save(OUT / f"{name}.png")


# ---------------------------------------------------------------- fixtures


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    one = Image.fromarray(art(61, 45)).convert("1")

    # Fax: Group 4, Group 3 one and two dimensional, modified Huffman. One strip each.
    one.save(OUT / "g4.tif", compression="group4")
    one.save(OUT / "g4-strips.tif", compression="group4", tiffinfo={278: 8})
    one.save(OUT / "g3.tif", compression="group3")
    one.save(OUT / "g3-2d.tif", compression="group3", tiffinfo={292: 5})
    one.save(OUT / "mh.tif", compression="tiff_ccitt")

    # The Group 4 strip again, bits reversed (fill order 2) and read as WhiteIsZero.
    g4 = Image.open(OUT / "g4.tif")
    offset, count = g4.tag_v2[273][0], g4.tag_v2[279][0]
    raw = (OUT / "g4.tif").read_bytes()[offset:offset + count]
    reversed_bits = bytes(int(f"{b:08b}"[::-1], 2) for b in raw)
    write_tiff("g4-lsb", 61, 45, {258: (SHORT, [1]), 259: (SHORT, [4]), 262: (SHORT, [0]), 266: (SHORT, [2]), 277: (SHORT, [1]), 278: (LONG, [45])}, [reversed_bits])

    rgb = card(97, 61)
    image = Image.fromarray(rgb)
    image.save(OUT / "jpeg-rgb.tif", compression="jpeg")
    image.convert("YCbCr").save(OUT / "jpeg-ycc.tif", compression="jpeg")
    image.save(OUT / "jpeg-strips.tif", compression="jpeg", tiffinfo={278: 16})
    image.save(OUT / "lzw-predictor.tif", compression="tiff_lzw", tiffinfo={317: 2})
    image.convert("L").save(OUT / "deflate-gray.tif", compression="tiff_adobe_deflate")
    image.quantize(64).save(OUT / "palette8.tif", compression="packbits")
    image.save(OUT / "bigtiff.tif", big_tiff=True, compression="tiff_lzw")

    cmyk = image.convert("CMYK")
    cmyk.save(OUT / "cmyk.tif", compression="tiff_lzw")
    (OUT / "cmyk.raw").write_bytes(np.array(cmyk).tobytes())

    first = Image.fromarray(card(40, 30))
    first.save(OUT / "multipage.tif", save_all=True, append_images=[Image.fromarray(card(20, 10)).convert("L"), one], compression="tiff_lzw")
    for i in range(3):
        page = Image.open(OUT / "multipage.tif")
        page.seek(i)
        page.convert("RGBA").save(OUT / f"multipage-{i}.png")

    # Tiles, big endian, Deflate with the predictor, partial tiles at the right and bottom.
    w, h = 50, 37
    rgb = card(w, h)
    chunks = [zlib.compress(differenced(t.reshape(16, -1, 3)).tobytes()) for t in tiles(rgb, 16, 16)]
    write_tiff("tiles", w, h, {258: (SHORT, [8, 8, 8]), 259: (SHORT, [8]), 262: (SHORT, [2]), 277: (SHORT, [3]), 284: (SHORT, [1]), 317: (SHORT, [2]), 322: (SHORT, [16]), 323: (SHORT, [16])}, chunks, order=">", tiled=True)

    # Planar RGBA, unassociated alpha, uncompressed, 7 rows a strip.
    w, h = 23, 19
    rgba = np.dstack([card(w, h), (np.arange(w)[None, :] * 255 // (w - 1)).repeat(h, 0).astype(np.uint8)])
    chunks = [s.tobytes() for c in range(4) for s in strips(np.ascontiguousarray(rgba[..., c]), 7)]
    write_tiff("planar", w, h, {258: (SHORT, [8] * 4), 259: (SHORT, [1]), 262: (SHORT, [2]), 277: (SHORT, [4]), 278: (LONG, [7]), 284: (SHORT, [2]), 338: (SHORT, [2])}, chunks)

    # 16 bit gray, big endian, PackBits.
    w, h = 31, 17
    gray16 = noise16(w, h, 1, 1)[..., 0]
    chunks = [packbits(s.astype(">u2").tobytes()) for s in strips(gray16, 6)]
    write_tiff("gray16", w, h, {258: (SHORT, [16]), 259: (SHORT, [32773]), 262: (SHORT, [1]), 277: (SHORT, [1]), 278: (LONG, [6])}, chunks, order=">")
    (OUT / "gray16.raw").write_bytes(be(gray16))

    # 16 bit RGB, little endian, LZW with the predictor.
    w, h = 29, 21
    rgb16 = noise16(w, h, 3, 2)
    chunks = [lzw(differenced(s.reshape(s.shape[0], -1, 3)).astype("<u2").tobytes()) for s in strips(rgb16, 8)]
    write_tiff("rgb16", w, h, {258: (SHORT, [16] * 3), 259: (SHORT, [5]), 262: (SHORT, [2]), 277: (SHORT, [3]), 278: (LONG, [8]), 317: (SHORT, [2])}, chunks)
    (OUT / "rgb16.raw").write_bytes(be(rgb16))

    # 8 bit gray, big endian, plain LZW long enough to fill the table and clear it.
    w, h = 120, 90
    rng = np.random.default_rng(3)
    gray = (card(w, h)[..., 0] // 2 + rng.integers(0, 64, (h, w))).astype(np.uint8)
    write_tiff("lzw-gray", w, h, {258: (SHORT, [8]), 259: (SHORT, [5]), 262: (SHORT, [1]), 277: (SHORT, [1]), 278: (LONG, [h])}, [lzw(gray.tobytes())], order=">")

    # 4 bit palette with a 16 bit color map.
    w, h = 37, 23
    quantized = Image.fromarray(card(w, h)).quantize(16)
    idx = np.array(quantized)
    pal = np.array(quantized.getpalette()[:48]).reshape(16, 3)
    rows = b"".join(bytes(int(r[i]) << 4 | (int(r[i + 1]) if i + 1 < w else 0) for i in range(0, w, 2)) for r in idx)
    colormap = [int(v) * 257 for c in range(3) for v in pal[:, c]]
    write_tiff("palette4", w, h, {258: (SHORT, [4]), 259: (SHORT, [1]), 262: (SHORT, [3]), 277: (SHORT, [1]), 278: (LONG, [h]), 320: (SHORT, colormap)}, [rows])

    # 1 bit WhiteIsZero, PackBits: a set bit is black.
    w, h = 45, 27
    bits = art(w, h)
    rows = b"".join(np.packbits(r).tobytes() for r in bits)
    write_tiff("bilevel", w, h, {258: (SHORT, [1]), 259: (SHORT, [32773]), 262: (SHORT, [0]), 277: (SHORT, [1]), 278: (LONG, [h])}, [packbits(rows)])

    # Associated (premultiplied) alpha.
    w, h = 21, 13
    color = card(w, h).astype(np.int64)
    alpha = np.tile(np.array([0, 64, 128, 255] * 6)[:w], (h, 1)).astype(np.int64)
    premultiplied = (color * alpha[..., None] + 127) // 255
    stored = np.dstack([premultiplied, alpha]).astype(np.uint8)
    write_tiff("associated", w, h, {258: (SHORT, [8] * 4), 259: (SHORT, [1]), 262: (SHORT, [2]), 277: (SHORT, [4]), 278: (LONG, [h]), 338: (SHORT, [1])}, [stored.tobytes()])
    a = alpha[..., None]
    expected = np.where(a == 0, 0, np.minimum(255, (premultiplied * 255 + a // 2) // np.maximum(a, 1)))
    (OUT / "associated.raw").write_bytes(np.dstack([expected, alpha]).astype(np.uint8).tobytes())

    # Orientation 6 (rotate 90 degrees clockwise to display), 300 by 150 pixels per inch.
    w, h = 24, 10
    write_tiff("orientation", w, h, {258: (SHORT, [8] * 3), 259: (SHORT, [1]), 262: (SHORT, [2]), 274: (SHORT, [6]), 277: (SHORT, [3]), 278: (LONG, [h]), 282: (RATIONAL, [(300, 1)]), 283: (RATIONAL, [(150, 1)]), 296: (SHORT, [2])}, [card(w, h).tobytes()])

    # Pillow turns this page upright as it reads it. The decoder keeps the stored order and leaves
    # the turn to the page placement, so the reference is the stored pixels.
    Image.fromarray(card(w, h)).convert("RGBA").save(OUT / "orientation.png")

    for name in ["g4", "g4-strips", "g3", "g3-2d", "mh", "g4-lsb", "jpeg-rgb", "jpeg-ycc", "jpeg-strips", "lzw-predictor", "deflate-gray",
                 "palette8", "bigtiff", "tiles", "planar", "lzw-gray", "palette4", "bilevel"]:
        pillow_reference(name)


if __name__ == "__main__":
    main()
