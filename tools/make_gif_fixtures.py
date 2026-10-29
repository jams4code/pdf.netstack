"""Regenerates the GIF test fixtures with Pillow, and the RGBA pixels Pillow decodes from each.

    photo.gif        256 colors from a photo-like card: long LZW runs and dictionary resets
    interlaced.gif   the same, interlaced
    transparent.gif  a palette image with a transparent color index
    two-color.gif    1 bit art: the smallest LZW code size

    pip install pillow numpy
    python tools/make_gif_fixtures.py
"""

from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Gif"


def card(width: int, height: int) -> Image.Image:
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    r = 128 + 100 * np.sin(x / 7.0) * np.cos(y / 11.0)
    g = 255 * x / max(1, width - 1)
    b = 255 * ((x // 12 + y // 12) % 2)
    return Image.fromarray(np.clip(np.stack([r, g, b], axis=-1), 0, 255).astype(np.uint8))


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    # Pillow interlaces by default from 16 pixels on; the plain fixture asks for rows in order.
    photo = card(97, 61).quantize(256)
    photo.save(OUT / "photo.gif", interlace=False)
    photo.save(OUT / "interlaced.gif", interlace=True)

    marked = card(40, 30).quantize(16)
    marked.paste(15, (5, 5, 25, 15))
    marked.save(OUT / "transparent.gif", transparency=15, interlace=False)

    art = Image.new("1", (33, 17), 1)
    art.paste(0, (4, 4, 20, 12))
    art.convert("P").save(OUT / "two-color.gif", interlace=False)

    for name in ["photo", "interlaced", "transparent", "two-color"]:
        Image.open(OUT / f"{name}.gif").convert("RGBA").save(OUT / f"{name}.png")


if __name__ == "__main__":
    main()
