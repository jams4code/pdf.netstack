"""Writes the synthetic images the code samples read: photo.jpg, photo.webp, photo-large.png,
logo.png, logo.bmp and scan.tif (two pages of bilevel fax data).

Development tool only. Requires Pillow (see scripts/requirements.txt).
Usage: python scripts/make_sample_inputs.py <output-dir>
"""

import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw


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


def large_photo(source, path):
    """The photo at 1600x1000 with grain, as a lossless PNG: what a raw scan or screenshot of a
    photograph looks like inside a PDF, for the lossy image options."""
    image = Image.open(source).convert("RGB").resize((1600, 1000), Image.Resampling.BICUBIC)
    grain = Image.effect_noise((1600, 1000), 12).convert("RGB")
    image = ImageChops.add(image, grain, scale=1.0, offset=-24)
    image.save(path, "PNG")


def scan(path):
    """Two A4 pages at 100 dpi, bilevel, CCITT Group 4 compressed, one strip per page, in one
    TIFF file: the layout scanners write and PdfImage embeds without decoding."""
    pages = []
    for number in (1, 2):
        page = Image.new("1", (827, 1169), 1)
        draw = ImageDraw.Draw(page)
        draw.rectangle((80, 80, 747, 180), outline=0, width=4)
        for line in range(24):
            y = 240 + line * 36
            draw.rectangle((80, y, 80 + (line * 97 + number * 131) % 500 + 160, y + 12), fill=0)
        pages.append(page)
    pages[0].save(path, "TIFF", compression="group4", save_all=True, append_images=pages[1:], dpi=(100, 100),
                  tiffinfo={278: 1169})


if __name__ == "__main__":
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    photo(out / "photo.jpg")
    logo(out / "logo.png")
    Image.open(out / "photo.jpg").save(out / "photo.webp", "WEBP", quality=80)
    Image.open(out / "logo.png").save(out / "logo.bmp", "BMP")
    large_photo(out / "photo.jpg", out / "photo-large.png")
    scan(out / "scan.tif")
