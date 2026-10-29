"""Regenerates the WebP test fixtures, and the RGBA pixels libwebp decodes from each.

Pillow writes most files. ImageMagick (`magick` on the path) writes the lossy variants Pillow
cannot ask for: the simple loop filter, eight token partitions, sharpness, one segment, and
uncompressed or filtered alpha. Every reference is decoded by Pillow, that is by libwebp with its
default settings (fancy upsampling), so lossy pixels must match exactly too.

    lossless*.webp     VP8L: plain, with alpha, palettes of 2, 4, 16 and 200 colors (pixel
                       bundling), a larger noisy image (color cache, meta prefix codes), method 0
    lossy*.webp        VP8 at several qualities, odd and tiny sizes, a noisy image (all intra
                       modes), with lossless and raw alpha, simple filter, partitions; alpha
                       filtered horizontally, vertically and by gradient (written here by hand)
    animated*.webp     two frames; the first is what loads

    pip install pillow numpy
    python tools/make_webp_fixtures.py
"""

import struct
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "WebP"


def card(width, height):
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    r = 128 + 100 * np.sin(x / 7.0) * np.cos(y / 11.0)
    g = 255 * x / max(1, width - 1)
    b = 255 * ((x // 12 + y // 12) % 2)
    return np.clip(np.stack([r, g, b], axis=-1), 0, 255).astype(np.uint8)


def noisy(width, height, seed):
    rng = np.random.default_rng(seed)
    base = card(width, height).astype(np.int64)
    blocks = rng.integers(0, 2, (height // 8 + 1, width // 8 + 1, 1)).repeat(8, 0).repeat(8, 1)[:height, :width] * 90
    return np.clip(base + blocks + rng.integers(-40, 40, (height, width, 3)), 0, 255).astype(np.uint8)


def alpha_ramp(width, height):
    y, x = np.mgrid[0:height, 0:width]
    a = np.clip(255 - 4 * np.hypot(x - width / 2, y - height / 2), 0, 255)
    a[(x // 9) % 4 == 0] = 0
    return a.astype(np.uint8)


def with_alpha(rgb, alpha):
    return Image.fromarray(np.dstack([rgb, alpha]))


def chunk(fourcc, payload):
    return fourcc + struct.pack("<I", len(payload)) + payload + bytes(len(payload) & 1)


def filtered_alpha(name, vp8_source, method):
    """A lossy image's VP8 chunk with a raw alpha chunk filtered by hand: 1 horizontal, 2 vertical, 3 gradient."""
    data = (OUT / f"{vp8_source}.webp").read_bytes()
    vp8 = data[12:]
    width = (vp8[14] | vp8[15] << 8) & 0x3FFF
    height = (vp8[16] | vp8[17] << 8) & 0x3FFF
    a = alpha_ramp(width, height).astype(np.int64)
    pred = np.zeros_like(a)
    pred[0, 1:] = a[0, :-1]
    pred[1:, 0] = a[:-1, 0]
    if method == 1:
        pred[1:, 1:] = a[1:, :-1]
    elif method == 2:
        pred[1:, 1:] = a[:-1, 1:]
    else:
        pred[1:, 1:] = np.clip(a[1:, :-1] + a[:-1, 1:] - a[:-1, :-1], 0, 255)
    residual = ((a - pred) % 256).astype(np.uint8)
    vp8x = bytes([0x10, 0, 0, 0]) + (width - 1).to_bytes(3, "little") + (height - 1).to_bytes(3, "little")
    body = b"WEBP" + chunk(b"VP8X", vp8x) + chunk(b"ALPH", bytes([method << 2]) + residual.tobytes()) + vp8
    (OUT / f"{name}.webp").write_bytes(b"RIFF" + struct.pack("<I", len(body)) + body)


def magick(name, source, *options):
    source.save(OUT / f"{name}.png")
    subprocess.run(["magick", str(OUT / f"{name}.png"), *options, str(OUT / f"{name}.webp")], check=True)


def reference(name):
    Image.open(OUT / f"{name}.webp").convert("RGBA").save(OUT / f"{name}.png")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    rgb = Image.fromarray(card(97, 61))
    rgb.save(OUT / "lossless.webp", lossless=True, method=6)
    rgb.save(OUT / "lossless-fast.webp", lossless=True, method=0, quality=0)
    with_alpha(card(53, 41), alpha_ramp(53, 41)).save(OUT / "lossless-alpha.webp", lossless=True, exact=True)
    for colors in [2, 4, 16, 200]:
        rgb.quantize(colors).convert("RGB").save(OUT / f"lossless-palette{colors}.webp", lossless=True)
    Image.fromarray(noisy(150, 110, 1)).save(OUT / "lossless-noisy.webp", lossless=True, quality=100, method=4)

    for quality in [10, 50, 90]:
        rgb.save(OUT / f"lossy-q{quality}.webp", quality=quality)
    Image.fromarray(card(1, 1)).save(OUT / "lossy-1x1.webp", quality=80)
    Image.fromarray(card(18, 33)).save(OUT / "lossy-small.webp", quality=70)
    Image.fromarray(noisy(128, 96, 2)).save(OUT / "lossy-noisy.webp", quality=75, method=6)
    with_alpha(card(53, 41), alpha_ramp(53, 41)).save(OUT / "lossy-alpha.webp", quality=60, alpha_quality=100)

    magick("lossy-simple", Image.fromarray(noisy(70, 50, 3)), "-quality", "40", "-define", "webp:filter-type=0", "-define", "webp:filter-strength=60")
    magick("lossy-partitions", Image.fromarray(noisy(130, 90, 4)), "-quality", "80", "-define", "webp:partitions=3", "-define", "webp:filter-sharpness=5", "-define", "webp:segments=1")
    magick("lossy-alpha-raw", with_alpha(card(45, 30), alpha_ramp(45, 30)), "-quality", "70", "-define", "webp:alpha-compression=0")
    magick("lossy-alpha-filtered", with_alpha(card(61, 37), alpha_ramp(61, 37)), "-quality", "70", "-define", "webp:alpha-filtering=2")

    for method, kind in [(1, "horizontal"), (2, "vertical"), (3, "gradient")]:
        filtered_alpha(f"lossy-alpha-{kind}", "lossy-small", method)

    frames = [Image.fromarray(card(60, 40)).convert("RGBA"), Image.fromarray(noisy(60, 40, 5)).convert("RGBA")]
    frames[0].save(OUT / "animated.webp", save_all=True, append_images=frames[1:], duration=100, lossless=True)
    frames[0].save(OUT / "animated-lossy.webp", save_all=True, append_images=frames[1:], duration=100, quality=70)

    for path in sorted(OUT.glob("*.webp")):
        reference(path.stem)


if __name__ == "__main__":
    main()
