"""Regenerates the CCITT fax test fixtures with libtiff through Pillow.

One synthetic 1 bit page (text, rules, a filled box, noise) is written as TIFF in each CCITT
flavour libtiff has, with one strip so the strip is exactly a CCITTFaxDecode stream:

    g4              K -1
    g3-1d           K 0, EndOfLine
    g3-2d           K 4, EndOfLine
    g3-1d-aligned   K 0, EndOfLine, EncodedByteAlign (fill bits before each EOL)
    mh              K 0, EncodedByteAlign (TIFF compression 2, rows byte aligned)

Fax codes white runs and black runs, and libtiff codes 0 bits as white. Pillow stores mode 1
as BlackIsZero, so the page is inverted before encoding: white pixels become the 0 bits, white
runs stay white, and the streams read with the PDF default BlackIs1 false, as scanners write
them. Each stream is checked to decode back through libtiff exactly. page.png is the page.

    pip install pillow numpy
    python tools/make_ccitt_fixtures.py
"""

import io
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Ccitt"

# (name, Pillow compression, extra TIFF tags: 292 is T4Options, 1 for 2D rows, 4 for fill bits)
FLAVOURS = [
    ("g4", "group4", {}),
    ("g3-1d", "group3", {}),
    ("g3-2d", "group3", {292: 1}),
    ("g3-1d-aligned", "group3", {292: 4}),
    ("mh", "tiff_ccitt", {}),
]


def page(width: int, height: int) -> Image.Image:
    """Black on white, with runs of every length class: single pixels, words, long rules."""
    image = Image.new("1", (width, height), 1)
    draw = ImageDraw.Draw(image)
    for row, text in enumerate(["CCITT fax test, line one.", "0123456789 ABCDEFGHIJKLMNOP", "the quick brown fox jumps"]):
        draw.text((6, 4 + 14 * row), text, fill=0)
    draw.rectangle((4, 48, width - 5, 50), fill=0)
    draw.rectangle((width - 60, 56, width - 8, 90), fill=0)
    draw.line((0, height - 1, width - 1, 60), fill=0)
    rng = np.random.default_rng(3)
    pixels = np.array(image)
    noise = rng.random((height, width)) < 0.01
    pixels[noise] = ~pixels[noise]
    return Image.fromarray(pixels)


def strip(tiff: bytes) -> tuple[bytes, int]:
    """The one strip's bytes and the photometric interpretation of a little endian TIFF."""
    assert tiff[:4] == b"II*\0", "little endian TIFF expected"
    (ifd,) = struct.unpack_from("<I", tiff, 4)
    (count,) = struct.unpack_from("<H", tiff, ifd)
    tags = {}
    for i in range(count):
        tag, kind, n, value = struct.unpack_from("<HHII", tiff, ifd + 2 + 12 * i)
        if kind == 3 and n == 1:
            value &= 0xFFFF
        tags[tag] = (n, value)
    assert tags[273][0] == 1, "one strip expected"
    offset, length = tags[273][1], tags[279][1]
    return tiff[offset:offset + length], tags[262][1]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    width, height = 301, 97
    source = page(width, height)
    source.save(OUT / "page.png", optimize=True)
    inverted = Image.fromarray(~np.array(source))
    for name, compression, extra in FLAVOURS:
        buffer = io.BytesIO()
        inverted.save(buffer, "TIFF", compression=compression, tiffinfo={278: height, **extra})
        data, photometric = strip(buffer.getvalue())
        assert photometric == 1, "BlackIsZero expected: 0 bits are the white runs"
        decoded = Image.open(io.BytesIO(buffer.getvalue()))
        decoded.load()
        assert (np.array(decoded) == np.array(inverted)).all(), f"{name} does not round trip"
        (OUT / f"{name}.bin").write_bytes(data)


if __name__ == "__main__":
    main()
