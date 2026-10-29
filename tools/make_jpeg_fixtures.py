"""Regenerates the JPEG test fixtures with libjpeg-turbo through Pillow.

Files in one group come from the same pixels and quantization tables, so they hold identical
DCT coefficients and differ only in entropy coding. The tests use that to check the decoder
against libjpeg rather than against itself.

    pip install pillow numpy
    python tools/make_jpeg_fixtures.py
"""

from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Jpeg"


def photo(width: int, height: int, seed: int) -> np.ndarray:
    """A photograph-like RGB test card: smooth gradients, hard edges and sensor noise."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    r = 128 + 100 * np.sin(x / 9.0) * np.cos(y / 13.0)
    g = 255 * x / max(1, width - 1)
    b = 255 * ((x // 16 + y // 16) % 2)
    rgb = np.stack([r, g, b], axis=-1) + rng.normal(0, 6, (height, width, 3))
    return np.clip(rgb, 0, 255).astype(np.uint8)


def save(image: Image.Image, name: str, **options) -> None:
    image.save(OUT / name, "JPEG", **options)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    # Odd sizes leave partial MCUs on both edges.
    rgb = Image.fromarray(photo(61, 45, 1))
    for subsampling, tag in ((2, "420"), (1, "422"), (0, "444")):
        common = {"quality": 75, "subsampling": subsampling}
        save(rgb, f"ycc{tag}-baseline.jpg", **common)
        save(rgb, f"ycc{tag}-optimized.jpg", optimize=True, **common)
        save(rgb, f"ycc{tag}-progressive.jpg", progressive=True, **common)
        save(rgb, f"ycc{tag}-restart.jpg", restart_marker_blocks=3, **common)

    gray = rgb.convert("L")
    save(gray, "gray-baseline.jpg", quality=80)
    save(gray, "gray-progressive.jpg", quality=80, progressive=True)

    # Adobe CMYK as print workflows produce it: APP14 with transform 0, inverted ink values.
    cmyk = rgb.convert("CMYK")
    save(cmyk, "cmyk-baseline.jpg", quality=85)
    save(cmyk, "cmyk-progressive.jpg", quality=85, progressive=True)

    # RGB without color conversion: Adobe transform 0 and component ids R, G, B.
    save(rgb, "rgb-baseline.jpg", quality=85, keep_rgb=True, subsampling=0)

    # A larger, high quality photograph, where requantization has real savings to find.
    large = Image.fromarray(photo(320, 240, 2))
    save(large, "photo-q95.jpg", quality=95, subsampling=2)

    references()


def references() -> None:
    """The pixels libjpeg decodes from each small fixture, for the pixel decoder's tests.

    Pillow reads Adobe CMYK inverted (raw mode CMYK;I); the references keep the samples as
    stored, so CMYK is inverted back and written as RGBA, one channel per ink.
    """
    out = OUT / "Reference"
    out.mkdir(exist_ok=True)
    for path in sorted(OUT.glob("*.jpg")):
        if path.stem == "photo-q95":
            continue
        image = Image.open(path)
        image.load()
        if image.mode == "CMYK":
            stored = 255 - np.asarray(image)
            image = Image.fromarray(stored.astype(np.uint8), "RGBA")
        image.save(out / f"{path.stem}.png", optimize=True)


if __name__ == "__main__":
    main()
