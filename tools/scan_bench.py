"""Synthetic scanned documents, and what the optimizer saves on each kind.

Development tool only. Nothing in src/ depends on it, and nothing it writes is committed (the
repository ignores *.pdf). It writes one PDF per kind of scanner output, every page a letter
size image of generated text drawn over the whole page, and runs the pdfnetstack command line
tool on each with the corpus benchmark's two profiles:

    lossless    the defaults
    lossy       --jpeg-quality 75 --max-dpi 150 --to-jpeg --min-ssim 0.95 --progressive

It prints the size of every output, the saving, and what the image passes did. The kinds:

    bilevel-flate-300   1 bit pages at 300 dpi under Flate, as many scan and conversion tools write them
    bilevel-lzw-300     the same under LZW, as older tools write them
    g3-fax-200          CCITT Group 3, two dimensional with EOL codes, 1728 columns at 200 dpi
    g4-600              CCITT Group 4 at 600 dpi
    jbig2-600           JBIG2 at 600 dpi, one generic region coded with MMR
    jpx-gray-300        gray pages (paper texture, soft text edges) as JPEG 2000 at 300 dpi
    jpx-color-300       color pages with a photograph as JPEG 2000 at 300 dpi

Text is drawn with Pillow's built in font. Scanner noise is modelled by blurring, adding noise
and thresholding (bilevel) or keeping the gray levels (JPEG 2000). CCITT data comes from libtiff
through Pillow; the JBIG2 page wraps the same T.6 data in a generic region (T.88, 6.2.6), which
is written here from the standard. JPEG 2000 comes from OpenJPEG through Pillow.

    pip install pillow numpy
    dotnet build src/PdfNetStack.Cli -c Release
    python tools/scan_bench.py [--out artifacts/scans] [--pages 2] [--tool path/to/PdfNetStack.Tool.dll]
"""

import argparse
import io
import json
import os
import struct
import subprocess
import sys
import zlib

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DEFAULT_TOOL = os.path.join(ROOT, "src", "PdfNetStack.Cli", "bin", "Release", "net10.0", "PdfNetStack.Tool.dll")
DEFAULT_OUT = os.path.join(ROOT, "artifacts", "scans")
PROFILES = {
    "lossless": [],
    "lossy": ["--jpeg-quality", "75", "--max-dpi", "150", "--to-jpeg", "--min-ssim", "0.95", "--progressive"],
}
WORDS = ("the of and to in is that for it as was with be by on not he this are or his from at which "
         "but have an they you were her she there their one all we can has been if more when will "
         "would who so no document scanned page archive record letter report office paper").split()


def text_page(width, height, dpi, seed, photo=False):
    """A gray page of black text on white at the given resolution: 10 point type, ragged lines."""
    rng = np.random.default_rng(seed)
    page = Image.new("L", (width, height), 255)
    draw = ImageDraw.Draw(page)
    size = int(dpi * 10 / 72)
    font = ImageFont.load_default(size=size)
    margin, leading = int(dpi), int(size * 1.35)
    y = margin
    if photo:
        # A photograph-like block on the top third of the page.
        h = height // 3
        yy, xx = np.mgrid[0:h, 0:width - 2 * margin].astype(np.float64)
        block = 128 + 80 * np.sin(xx / 97 + yy / 53) + 30 * np.cos(yy / 31) + rng.normal(0, 9, yy.shape)
        page.paste(Image.fromarray(np.clip(block, 0, 255).astype(np.uint8)), (margin, y))
        y += h + leading
    while y + leading < height - margin:
        words, line = rng.integers(8, 14), []
        for _ in range(words):
            line.append(WORDS[rng.integers(len(WORDS))])
        draw.text((margin, y), " ".join(line), fill=0, font=font)
        y += leading
        if rng.random() < 0.12:
            y += leading
    return page


def scanned_bilevel(page, seed):
    """Blur, noise and a threshold: ragged edges and speckle as a scanner leaves them. 1 is white."""
    rng = np.random.default_rng(seed)
    a = np.array(page.filter(ImageFilter.GaussianBlur(0.8)), dtype=np.float64)
    a += rng.normal(0, 18, a.shape)
    return a > 128


def scanned_gray(page, seed):
    rng = np.random.default_rng(seed)
    a = np.array(page.filter(ImageFilter.GaussianBlur(0.9)), dtype=np.float64)
    a = 30 + a * 0.85 + rng.normal(0, 6, a.shape)
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def pack(white):
    """1 bit rows padded to whole bytes, 1 for white: DeviceGray samples."""
    return np.packbits(white, axis=1).tobytes()


def tiff_strip(image, compression, extra=None):
    """The single strip of a TIFF Pillow writes with libtiff, and its fill order checked."""
    buffer = io.BytesIO()
    image.save(buffer, "TIFF", compression=compression, tiffinfo={278: image.height, **(extra or {})})
    tiff = buffer.getvalue()
    assert tiff[:4] == b"II*\0"
    (ifd,) = struct.unpack_from("<I", tiff, 4)
    (count,) = struct.unpack_from("<H", tiff, ifd)
    tags = {}
    for i in range(count):
        tag, kind, n, value = struct.unpack_from("<HHII", tiff, ifd + 2 + 12 * i)
        tags[tag] = value & 0xFFFF if kind == 3 and n == 1 else value
    return tiff[tags[273]:tags[273] + tags[279]]


def fax(white, compression, extra=None):
    """CCITT data that decodes with BlackIs1 false: libtiff codes 0 bits as white runs, so the
    page goes in inverted, white pixels as 0 bits."""
    return tiff_strip(Image.fromarray(~white), compression, extra)


def jbig2_mmr(white):
    """A JBIG2 page (T.88, 7.4.8 and 7.4.6): page information, then one immediate lossless generic
    region with MMR set, holding T.6 data whose white runs are the 0 (white) pixels, then end of
    page. libtiff codes 0 bits as white, so the page goes in with black as 1 bits."""
    h, w = white.shape

    def segment(number, kind, data):
        return struct.pack(">IBBBI", number, kind, 0, 1, len(data)) + data

    page_info = struct.pack(">IIIIBH", w, h, 0, 0, 0x40, 0)
    region = struct.pack(">IIIIB", w, h, 0, 0, 0) + b"\x01"
    mmr = tiff_strip(Image.fromarray(~white), "group4")
    return segment(0, 48, page_info) + segment(1, 38, region + mmr) + segment(2, 49, b"")


def lzw(data):
    """LZW with EarlyChange 1 (ISO 32000-1, 7.4.4), written from the specification."""
    out, bits, nbits = bytearray(), 0, 0

    def put(code, width):
        nonlocal bits, nbits
        bits = (bits << width) | code
        nbits += width
        while nbits >= 8:
            nbits -= 8
            out.append((bits >> nbits) & 0xFF)

    table, width = {bytes([i]): i for i in range(256)}, 9
    put(256, width)
    current = b""
    for byte in data:
        candidate = current + bytes([byte])
        if candidate in table:
            current = candidate
            continue
        put(table[current], width)
        table[candidate] = len(table) + 2
        # The decoder adds each entry one code later, and widens one code early.
        if len(table) + 2 >= 1 << width and width < 12:
            width += 1
        if len(table) + 2 >= 4094:
            put(256, width)
            table, width = {bytes([i]): i for i in range(256)}, 9
        current = bytes([byte])
    if current:
        put(table[current], width)
        if len(table) + 2 >= (1 << width) - 1 and width < 12:
            width += 1
    put(257, width)
    if nbits:
        out.append((bits << (8 - nbits)) & 0xFF)
    return bytes(out)


def write_pdf(path, images):
    """One page per image, drawn over the whole letter page. images: (dictionary, data)."""
    objects = [b"<</Type/Catalog/Pages 2 0 R>>", None]
    kids = []
    for dictionary, data in images:
        image = len(objects) + 1
        objects.append(b"<<" + dictionary.encode() + b"/Length %d>>stream\n" % len(data) + data + b"\nendstream")
        content = b"q 612 0 0 792 0 0 cm /Im0 Do Q"
        objects.append(b"<</Length %d>>stream\n" % len(content) + content + b"\nendstream")
        objects.append(b"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 %d 0 R>>>>/Contents %d 0 R>>" % (image, image + 1))
        kids.append(len(objects))
    objects[1] = b"<</Type/Pages/Kids[" + b" ".join(b"%d 0 R" % k for k in kids) + b"]/Count %d>>" % len(kids)
    out = bytearray(b"%PDF-1.5\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for number, body in enumerate(objects, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    out += b"".join(b"%010d 00000 n \n" % o for o in offsets)
    out += b"trailer\n<</Size %d/Root 1 0 R>>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    with open(path, "wb") as f:
        f.write(out)


def bilevel_dictionary(w, h, extra):
    return f"/Type/XObject/Subtype/Image/Width {w}/Height {h}/ColorSpace/DeviceGray/BitsPerComponent 1{extra}"


def make(kind, pages):
    """The images of one sample: a list of (dictionary, data)."""
    images = []
    for p in range(pages):
        seed = (KINDS.index(kind) * 100) + p
        if kind in ("bilevel-flate-300", "bilevel-lzw-300"):
            white = scanned_bilevel(text_page(2550, 3300, 300, p), seed)
            data = pack(white)
            if kind == "bilevel-flate-300":
                images.append((bilevel_dictionary(2550, 3300, "/Filter/FlateDecode"), zlib.compress(data, 9)))
            else:
                images.append((bilevel_dictionary(2550, 3300, "/Filter/LZWDecode"), lzw(data)))
        elif kind == "g3-fax-200":
            white = scanned_bilevel(text_page(1728, 2200, 200, p), seed)
            parms = "/DecodeParms<</K 4/Columns 1728/Rows 2200/EndOfLine true>>"
            images.append((bilevel_dictionary(1728, 2200, "/Filter/CCITTFaxDecode" + parms), fax(white, "group3", {292: 1})))
        elif kind == "g4-600":
            white = scanned_bilevel(text_page(5100, 6600, 600, p), seed)
            parms = "/DecodeParms<</K -1/Columns 5100/Rows 6600>>"
            images.append((bilevel_dictionary(5100, 6600, "/Filter/CCITTFaxDecode" + parms), fax(white, "group4")))
        elif kind == "jbig2-600":
            white = scanned_bilevel(text_page(5100, 6600, 600, p), seed)
            images.append((bilevel_dictionary(5100, 6600, "/Filter/JBIG2Decode"), jbig2_mmr(white)))
        elif kind in ("jpx-gray-300", "jpx-color-300"):
            color = kind == "jpx-color-300"
            gray = scanned_gray(text_page(2550, 3300, 300, p, photo=color), seed)
            image = Image.merge("RGB", (gray, gray.point(lambda v: min(255, v + 12)), gray.point(lambda v: max(0, v - 10)))) if color else gray
            buffer = io.BytesIO()
            image.save(buffer, "JPEG2000", irreversible=True, quality_mode="rates", quality_layers=[24])
            space = "/DeviceRGB" if color else "/DeviceGray"
            images.append((f"/Type/XObject/Subtype/Image/Width 2550/Height 3300/ColorSpace{space}/BitsPerComponent 8/Filter/JPXDecode", buffer.getvalue()))
        else:
            raise ValueError(kind)
    return images


KINDS = ["bilevel-flate-300", "bilevel-lzw-300", "g3-fax-200", "g4-600", "jbig2-600", "jpx-gray-300", "jpx-color-300"]


def optimize(tool, source, target, flags):
    command = ["dotnet", tool, "optimize", source, target, "--min-saving", "0", "--json"] + flags
    done = subprocess.run(command, capture_output=True, text=True)
    if done.returncode != 0:
        raise RuntimeError(" ".join(command) + "\n" + done.stdout + done.stderr)
    return json.loads(done.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--out", default=DEFAULT_OUT)
    parser.add_argument("--pages", type=int, default=2)
    parser.add_argument("--tool", default=DEFAULT_TOOL)
    parser.add_argument("--only", default="", help="comma separated kinds")
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    kinds = [k for k in KINDS if not args.only or k in args.only.split(",")]

    print(f"{'kind':20} {'input':>10} {'lossless':>10} {'saved':>7} {'lossy':>10} {'saved':>7}  image passes")
    failed = False
    for kind in kinds:
        source = os.path.join(args.out, kind + ".pdf")
        write_pdf(source, make(kind, args.pages))
        size = os.path.getsize(source)
        row, notes = [f"{kind:20} {size:>10,}"], []
        for profile, flags in PROFILES.items():
            target = os.path.join(args.out, f"{kind}.{profile}.pdf")
            result = optimize(args.tool, source, target, flags)
            out = result["OutputBytes"]
            row.append(f"{out:>10,} {100.0 * (size - out) / size:>6.1f}%")
            s = result["Statistics"]
            if result["Disposition"] != "Optimized" and "below" not in result["Reason"]:
                failed = True
                notes.append(f"{profile}: {result['Reason']}")
            notes.append(f"{profile}: G4 {s['ImagesRecodedAsGroup4']}, downsampled {s['ImagesDownsampled']}, "
                         f"to JPEG {s['ImagesConvertedToJpeg']}, kept by gate {s['ImagesKeptByQualityGate']}")
        print(" ".join(row) + "  " + "; ".join(notes))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
