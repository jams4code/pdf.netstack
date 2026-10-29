"""Writes the synthetic images the code samples read: photo.jpg and logo.png.

Development tool only. Requires Pillow (see scripts/requirements.txt).
Usage: python scripts/make_sample_inputs.py <output-dir>
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw


def photo(path):
    """A 640x400 gradient with a few shapes, saved as a baseline JPEG."""
    image = Image.new("RGB", (640, 400))
    pixels = image.load()
    for y in range(400):
        for x in range(640):
            pixels[x, y] = (40 + x * 150 // 640, 90 + y * 120 // 400, 200 - x * 80 // 640)
    draw = ImageDraw.Draw(image)
    draw.ellipse((380, 60, 560, 240), fill=(250, 204, 21))
    draw.polygon([(0, 400), (220, 180), (420, 400)], fill=(22, 101, 52))
    draw.polygon([(260, 400), (470, 210), (640, 400)], fill=(21, 128, 61))
    image.save(path, "JPEG", quality=88)


def logo(path):
    """A 300x100 logo with a transparent background."""
    image = Image.new("RGBA", (300, 100), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((4, 4, 296, 96), radius=20, fill=(37, 99, 235, 255))
    draw.rectangle((30, 30, 70, 70), fill=(255, 255, 255, 255))
    draw.ellipse((100, 30, 140, 70), fill=(147, 197, 253, 255))
    draw.polygon([(170, 70), (190, 30), (210, 70)], fill=(255, 255, 255, 255))
    draw.rectangle((230, 30, 270, 70), fill=(255, 255, 255, 128))
    image.save(path, "PNG")


if __name__ == "__main__":
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    photo(out / "photo.jpg")
    logo(out / "logo.png")
