"""Writes a small synthetic corpus for the lossy image options and measures them.

    pip install numpy pillow
    python tools/make_lossy_corpus.py ./lossy-corpus
    python tools/make_lossy_corpus.py ./lossy-corpus --measure src/PdfNetStack.Cli/bin/Release/net10.0/PdfNetStack.Tool.exe

Every picture is generated here from fixed seeds, so the corpus is the same on every machine and
contains nothing from a third party:

    photo.pdf        a 2400 by 1800 RGB photograph-like image (smooth fields, texture, sensor
                     noise) stored raw under Flate and drawn 4 by 3 inches: 600 dpi.
    photo-jpeg.pdf   the same picture as a quality 92 JPEG (written by Pillow), same placement.
    scan.pdf         two 8 bit gray letter pages at 300 dpi: paper texture, noise and lines of
                     dark text-like strokes, drawn full page.
    lineart.pdf      an 8 bit RGB technical drawing at 400 dpi: white ground, a few ink colors.
    screenshot.pdf   a 1920 by 1080 RGB user interface capture drawn 8 inches wide: 240 dpi.
    text.pdf         twenty pages of text and rules as Flate content streams and no images, to
                     show what --exhaustive does on content.

With --measure the given pdfnetstack executable optimizes each file under several option sets
(lossless, --exhaustive, --max-dpi 150, --to-jpeg, both with --min-ssim 0.95, --progressive) and
prints sizes, times and what the image passes did, from the tool's JSON output.
"""

import argparse
import json
import subprocess
import sys
import time
import zlib
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image


def photo(width, height, seed=1):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    u, v = x / width, y / height
    r = 128 + 90 * np.sin(u * 7 + v * 3) + 30 * np.cos(v * 11) + 12 * np.sin(x / 9.0) * np.cos(y / 13.0)
    g = 40 + 170 * u * v + 25 * np.sin(x / 5.0 + v * 4)
    b = 200 - 150 * v + 20 * np.cos((u - v) * 9) + 10 * np.sin((x + y) / 17.0)
    rgb = np.stack([r, g, b], axis=-1) + rng.normal(0, 4, (height, width, 3))
    return np.clip(np.rint(rgb), 0, 255).astype(np.uint8)


def scan(width, height, seed):
    rng = np.random.default_rng(seed)
    page = 236 + rng.normal(0, 3, (height, width)) + 4 * np.sin(np.arange(width) / 97.0)[None, :]
    margin, line, pitch = width // 10, height // 70, height // 40
    for top in range(height // 12, height - height // 12, pitch):
        x = margin
        while x < width - margin:
            word = int(rng.integers(width // 60, width // 14))
            if x + word > width - margin:
                break
            ink = 40 + rng.normal(0, 10, (line, word))
            # Strokes, not solid bars: gaps between letters.
            ink[:, (np.arange(word) // max(1, width // 300)) % 3 == 2] = 236
            page[top:top + line, x:x + word] = np.minimum(page[top:top + line, x:x + word], ink)
            x += word + width // 80
    return np.clip(np.rint(page), 0, 255).astype(np.uint8)


def lineart(width, height):
    img = np.full((height, width, 3), 255, np.uint8)
    inks = [(0, 0, 0), (200, 30, 30), (20, 60, 180)]
    for k in range(0, width, width // 24):
        img[:, k:k + 3] = inks[0] if k % (width // 6) else inks[1]
    for k in range(0, height, height // 18):
        img[k:k + 3, :] = inks[2]
    y, x = np.mgrid[0:height, 0:width]
    for cx, cy, radius in [(0.3, 0.4, 0.2), (0.7, 0.6, 0.25), (0.5, 0.5, 0.1)]:
        d = np.hypot(x - cx * width, y - cy * height)
        img[np.abs(d - radius * height) < 2.5] = inks[0]
    return img


def screenshot(width, height):
    img = np.full((height, width, 3), 250, np.uint8)
    img[: height // 14] = (45, 52, 64)
    img[height // 14:, : width // 6] = (236, 238, 242)
    for row in range(height // 8, height - 40, 28):
        for col in range(width // 5, width - 80, 90):
            img[row:row + 12, col:col + 70:3] = (30, 30, 30)
    img[height // 3: height // 3 + 300, width // 2: width // 2 + 500] = (66, 133, 244)
    return img


WORDS = ["invoice", "total", "amount", "the", "of", "delivery", "page", "number", "date", "reference",
         "quantity", "price", "order", "customer", "item", "description", "unit", "tax", "net", "due"]


def text_page(seed):
    rng = np.random.default_rng(seed)
    lines = []
    y = 760
    while y > 40:
        words = " ".join(WORDS[i] for i in rng.integers(0, len(WORDS), int(rng.integers(3, 12))))
        lines.append(f"BT /F1 {int(rng.integers(8, 12))} Tf 1 0 0 1 {int(rng.integers(36, 80))} {y} Tm ({words}) Tj ET")
        if rng.random() < 0.2:
            lines.append(f"q 0.5 w 36 {y - 4} m 576 {y - 4} l S Q")
        y -= int(rng.integers(12, 18))
    return "\n".join(lines).encode("ascii")


def deflate(data):
    return zlib.compress(data, 9)


def write_text_pdf(path, pages):
    objects = [b"<</Type/Catalog/Pages 2 0 R>>", None, b"<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"]
    kids = []
    for content in pages:
        data = deflate(content)
        objects.append(b"<</Length %d/Filter/FlateDecode>>\nstream\n" % len(data) + data + b"\nendstream")
        objects.append(b"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents %d 0 R>>"
                       % len(objects))
        kids.append(len(objects))
    objects[1] = b"<</Type/Pages/Kids[" + b" ".join(b"%d 0 R" % k for k in kids) + b"]/Count %d>>" % len(kids)
    finish(path, objects, 1)


def write_pdf(path, pages):
    """pages: list of lists of (dictionary, data, width_pt, height_pt, x, y); one image per entry."""
    objects = []

    def add(body):
        objects.append(body)
        return len(objects)

    catalog = add(None)
    tree = add(None)
    kids = []
    for images in pages:
        names, draws = [], []
        for i, (dictionary, data, w, h, x, y) in enumerate(images):
            number = add(b"<<" + dictionary + b"/Length %d>>\nstream\n" % len(data) + data + b"\nendstream")
            names.append(b"/Im%d %d 0 R" % (i, number))
            draws.append(b"q %.2f 0 0 %.2f %.2f %.2f cm /Im%d Do Q" % (w, h, x, y, i))
        content = b"\n".join(draws)
        contents = add(b"<</Length %d>>\nstream\n" % len(content) + content + b"\nendstream")
        kids.append(add(b"<</Type/Page/Parent %d 0 R/MediaBox[0 0 612 792]/Resources<</XObject<<" % tree
                        + b"".join(names) + b">>>>/Contents %d 0 R>>" % contents))
    objects[catalog - 1] = b"<</Type/Catalog/Pages %d 0 R>>" % tree
    objects[tree - 1] = b"<</Type/Pages/Kids[" + b" ".join(b"%d 0 R" % k for k in kids) + b"]/Count %d>>" % len(kids)
    finish(path, objects, catalog)


def finish(path, objects, catalog):
    """Numbers the objects from 1, then writes them with a classic xref table."""
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for n, body in enumerate(objects, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % n + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for o in offsets:
        out += b"%010d 00000 n \n" % o
    out += b"trailer\n<</Size %d/Root %d 0 R>>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, catalog, xref)
    path.write_bytes(bytes(out))


def raw(img, *extra):
    h, w = img.shape[:2]
    space = b"/DeviceGray" if img.ndim == 2 else b"/DeviceRGB"
    return (b"/Type/XObject/Subtype/Image/Width %d/Height %d/ColorSpace%s/BitsPerComponent 8/Filter/FlateDecode" % (w, h, space)
            + b"".join(extra), deflate(img.tobytes()))


def make(out):
    out.mkdir(parents=True, exist_ok=True)
    picture = photo(2400, 1800)
    d, data = raw(picture)
    write_pdf(out / "photo.pdf", [[(d, data, 288, 216, 72, 432)]])

    buffer = BytesIO()
    Image.fromarray(picture).save(buffer, "JPEG", quality=92)
    jpeg = buffer.getvalue()
    d = b"/Type/XObject/Subtype/Image/Width 2400/Height 1800/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode"
    write_pdf(out / "photo-jpeg.pdf", [[(d, jpeg, 288, 216, 72, 432)]])

    pages = []
    for seed in (2, 3):
        d, data = raw(scan(2550, 3300, seed))
        pages.append([(d, data, 612, 792, 0, 0)])
    write_pdf(out / "scan.pdf", pages)

    d, data = raw(lineart(2400, 1800))
    write_pdf(out / "lineart.pdf", [[(d, data, 432, 324, 90, 300)]])

    d, data = raw(screenshot(1920, 1080))
    write_pdf(out / "screenshot.pdf", [[(d, data, 576, 324, 18, 400)]])

    write_text_pdf(out / "text.pdf", [text_page(seed) for seed in range(20)])


PROFILES = [
    ("lossless", []),
    ("exhaustive", ["--exhaustive"]),
    ("max-dpi 150", ["--max-dpi", "150"]),
    ("to-jpeg q75", ["--to-jpeg", "--jpeg-quality", "75"]),
    ("dpi+jpeg, gate .95", ["--max-dpi", "150", "--to-jpeg", "--jpeg-quality", "75", "--min-ssim", "0.95"]),
    ("dpi+jpeg+progressive", ["--max-dpi", "150", "--to-jpeg", "--jpeg-quality", "75", "--progressive"]),
]


def measure(out, cli):
    target = out / "optimized"
    target.mkdir(exist_ok=True)
    print(f"{'file':<16} {'profile':<22} {'input':>10} {'output':>10} {'saved':>7} {'ms':>7}  images")
    for pdf in sorted(out.glob("*.pdf")):
        for name, flags in PROFILES:
            result_path = target / f"{pdf.stem}.{name.replace(' ', '_').replace(',', '').replace('+', '_')}.pdf"
            started = time.perf_counter()
            run = subprocess.run([str(Path(cli).resolve()), "optimize", str(pdf), str(result_path), "--min-saving", "0", "--json", *flags],
                                 capture_output=True, text=True, check=False)
            wall = (time.perf_counter() - started) * 1000
            if run.returncode != 0:
                print(f"{pdf.name:<16} {name:<22} failed: {run.stderr.strip() or run.stdout.strip()}")
                continue
            r = json.loads(run.stdout)
            s = r.get("Statistics") or {}
            images = (f"downsampled {s.get('ImagesDownsampled', 0)}, jpeg {s.get('ImagesConvertedToJpeg', 0)}, "
                      f"gate kept {s.get('ImagesKeptByQualityGate', 0)}, requantized {s.get('JpegImagesRequantized', 0)}")
            print(f"{pdf.name:<16} {name:<22} {r['OriginalBytes']:>10} {r['OutputBytes']:>10} "
                  f"{r['SavingPercent']:>6.1f}% {wall:>7.0f}  {r['Disposition']}; {images}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("out", type=Path)
    parser.add_argument("--measure", metavar="PDFNETSTACK", help="pdfnetstack executable to measure with")
    args = parser.parse_args()
    make(args.out)
    if args.measure:
        measure(args.out, args.measure)
    return 0


if __name__ == "__main__":
    sys.exit(main())
