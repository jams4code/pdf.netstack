"""Writes synthetic PDFs that exercise the optimizer's structural passes.

Development tool only. Nothing in src/ depends on it, and its output is not committed: the
files are built from scratch here, from PDF syntax and the synthetic test font that
tools/make_font_fixtures.py generates, so they hold no third party content.

Usage:
  python tools/make_optimizer_samples.py <output-dir>

Files written:
  unused-resources.pdf  pages whose resource dictionaries list fonts, images and graphics
                        states their content never names
  verbose-content.pdf   content streams with comments, redundant white space and long numbers
  font-subsets.pdf      one TrueType font embedded once per page as a separate subset that
                        keeps the original glyph ids, as merged documents often carry it
  linearize.pdf         pages inheriting their media box and resources from the page tree,
                        with bookmarks, a shared font and an image per page
  combined.pdf          all of the above in one document
"""

import os
import random
import struct
import sys
import zlib

FONT = os.path.join(os.path.dirname(__file__), "..", "tests", "PdfNetStack.Tests", "Fixtures", "Fonts", "Test-Regular.ttf")


class Pdf:
    """Collects numbered objects and writes them with a classic cross-reference table."""

    def __init__(self):
        self.objects = {}

    def reserve(self):
        number = len(self.objects) + 1
        self.objects[number] = None
        return number

    def add(self, body, number=None):
        number = number or self.reserve()
        self.objects[number] = body.encode("latin-1") if isinstance(body, str) else body
        return number

    def stream(self, dictionary, data, number=None, compress=False):
        if compress:
            data = zlib.compress(data, 9)
            dictionary += "/Filter/FlateDecode"
        body = ("<<%s/Length %d>>stream\n" % (dictionary, len(data))).encode("latin-1") + data + b"\nendstream"
        return self.add(body, number)

    def write(self, path, root, info=None):
        out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
        offsets = {}
        for number in sorted(self.objects):
            offsets[number] = len(out)
            out += b"%d 0 obj\n" % number + self.objects[number] + b"\nendobj\n"
        xref = len(out)
        size = max(self.objects) + 1
        out += b"xref\n0 %d\n0000000000 65535 f \n" % size
        for number in range(1, size):
            out += b"%010d 00000 n \n" % offsets[number]
        trailer = "<</Size %d/Root %d 0 R%s>>" % (size, root, "/Info %d 0 R" % info if info else "")
        out += b"trailer\n" + trailer.encode("latin-1") + b"\nstartxref\n%d\n%%%%EOF\n" % xref
        with open(path, "wb") as f:
            f.write(out)


def page_tree(pdf, pages, extra=""):
    """A Pages node over the given page numbers; returns the catalog's number."""
    tree = pdf.reserve()
    for page in pages:
        pdf.objects[page] = pdf.objects[page].replace(b"/Parent 0 0 R", b"/Parent %d 0 R" % tree)
    pdf.add("<</Type/Pages/Kids[%s]/Count %d%s>>" % (" ".join("%d 0 R" % p for p in pages), len(pages), extra), tree)
    return tree


def gray_image(pdf, rng, side):
    pixels = bytes(rng.getrandbits(8) for _ in range(side * side))
    return pdf.stream("/Type/XObject/Subtype/Image/Width %d/Height %d/ColorSpace/DeviceGray/BitsPerComponent 8" % (side, side), pixels, compress=True)


def unused_resources(pdf, rng, count):
    standard = ["Helvetica", "Times-Roman", "Courier", "Helvetica-Bold", "Times-Bold", "Courier-Bold", "Symbol", "ZapfDingbats"]
    fonts = [pdf.add("<</Type/Font/Subtype/Type1/BaseFont/%s>>" % name) for name in standard]
    pages = []
    for i in range(count):
        images = [gray_image(pdf, rng, 48) for _ in range(4)]
        states = "".join("/GS%d<</CA %.2f/ca %.2f>>" % (k, 0.2 + k * 0.1, 0.2 + k * 0.1) for k in range(6))
        font_entries = "".join("/F%d %d 0 R" % (k, f) for k, f in enumerate(fonts))
        image_entries = "".join("/Im%d %d 0 R" % (k, im) for k, im in enumerate(images))
        content = pdf.stream("", ("q /GS1 gs 48 0 0 48 72 600 cm /Im0 Do Q BT /F0 18 Tf 72 700 Td (Unused resources, page %d) Tj ET" % (i + 1)).encode("latin-1"), compress=True)
        pages.append(pdf.add("<</Type/Page/Parent 0 0 R/MediaBox[0 0 612 792]/Contents %d 0 R/Resources<</Font<<%s>>/XObject<<%s>>/ExtGState<<%s>>>>>>"
                             % (content, font_entries, image_entries, states)))
    return pages


def verbose_content(pdf, rng, count):
    font = pdf.add("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>")
    pages = []
    for i in range(count):
        lines = ["% generated drawing, page " + str(i + 1)]
        for k in range(120):
            x, y = rng.uniform(0, 600), rng.uniform(0, 780)
            lines.append("q   1.000000  0.000000  0.000000  1.000000  %010.4f  %010.4f  cm   %% box %d" % (x, y, k))
            lines.append("   0.500000 0.250000 0.750000   rg    0.000000 0.000000   +20.000000  +10.000000  re   f   Q")
        lines.append("BT   /F1   12.000000   Tf   72.000000   740.000000   Td   (Verbose content, page %d)   Tj   ET" % (i + 1))
        content = pdf.stream("", "\r\n".join(lines).encode("latin-1"))
        pages.append(pdf.add("<</Type/Page/Parent 0 0 R/MediaBox[0 0 612 792]/Contents %d 0 R/Resources<</Font<</F1 %d 0 R>>>>>>" % (content, font)))
    return pages


def tables(font):
    count = struct.unpack(">H", font[4:6])[0]
    result = {}
    for i in range(count):
        record = 12 + 16 * i
        tag = font[record:record + 4].decode("latin-1")
        offset, length = struct.unpack(">II", font[record + 8:record + 16])
        result[tag] = font[offset:offset + length]
    return result


def subset(font, keep):
    """The font with every glyph outside keep emptied; glyph ids do not change."""
    t = tables(font)
    long_loca = struct.unpack(">h", t["head"][50:52])[0] == 1
    glyphs = struct.unpack(">H", t["maxp"][4:6])[0]
    if long_loca:
        loca = list(struct.unpack(">%dI" % (glyphs + 1), t["loca"][:4 * (glyphs + 1)]))
    else:
        loca = [2 * v for v in struct.unpack(">%dH" % (glyphs + 1), t["loca"][:2 * (glyphs + 1)])]

    # Composite glyphs need their components.
    pending = list(keep)
    keep = set(keep)
    while pending:
        gid = pending.pop()
        data = t["glyf"][loca[gid]:loca[gid + 1]]
        if len(data) < 10 or struct.unpack(">h", data[:2])[0] >= 0:
            continue
        at = 10
        while True:
            flags, component = struct.unpack(">HH", data[at:at + 4])
            if component not in keep:
                keep.add(component)
                pending.append(component)
            at += 4 + (4 if flags & 1 else 2) + (8 if flags & 0x80 else 4 if flags & 0x40 else 2 if flags & 8 else 0)
            if not flags & 0x20:
                break

    glyf, new_loca = bytearray(), []
    for gid in range(glyphs):
        new_loca.append(len(glyf))
        if gid in keep:
            glyf += t["glyf"][loca[gid]:loca[gid + 1]]
            glyf += b"\0" * (-len(glyf) % 4)
    new_loca.append(len(glyf))
    t["glyf"] = bytes(glyf)
    t["loca"] = struct.pack(">%dI" % len(new_loca), *new_loca)
    t["head"] = t["head"][:50] + struct.pack(">h", 1) + t["head"][52:]

    tags = sorted(t)
    out = bytearray(struct.pack(">IHHHH", 0x00010000, len(tags), 0, 0, 0))
    offset = 12 + 16 * len(tags)
    body = bytearray()
    for tag in tags:
        data = t[tag]
        out += tag.encode("latin-1") + struct.pack(">III", 0, offset + len(body), len(data))
        body += data + b"\0" * (-len(data) % 4)
    return bytes(out + body)


def font_subsets(pdf, rng, count):
    with open(FONT, "rb") as f:
        font = f.read()
    glyphs = struct.unpack(">H", tables(font)["maxp"][4:6])[0]
    pages = []
    for i in range(count):
        keep = {0} | set(rng.sample(range(1, glyphs), 4))
        program = subset(font, keep)
        stream = pdf.stream("/Length1 %d" % len(program), program, compress=True)
        descriptor = pdf.add("<</Type/FontDescriptor/FontName/AAAAA%c+Test/Flags 32/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 %d 0 R>>" % (chr(65 + i % 26), stream))
        cid = pdf.add("<</Type/Font/Subtype/CIDFontType2/BaseFont/AAAAA%c+Test/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor %d 0 R/DW 600/CIDToGIDMap/Identity>>" % (chr(65 + i % 26), descriptor))
        type0 = pdf.add("<</Type/Font/Subtype/Type0/BaseFont/AAAAA%c+Test/Encoding/Identity-H/DescendantFonts[%d 0 R]>>" % (chr(65 + i % 26), cid))
        codes = "".join("%04X" % g for g in sorted(keep - {0}))
        content = pdf.stream("", ("BT /T1 24 Tf 72 700 Td <%s> Tj ET" % codes).encode("latin-1"), compress=True)
        pages.append(pdf.add("<</Type/Page/Parent 0 0 R/MediaBox[0 0 612 792]/Contents %d 0 R/Resources<</Font<</T1 %d 0 R>>>>>>" % (content, type0)))
    return pages


def linearize(pdf, rng, count):
    font = pdf.add("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>")
    shared = gray_image(pdf, rng, 32)
    resources = pdf.add("<</Font<</F1 %d 0 R>>/XObject<</Logo %d 0 R>>>>" % (font, shared))
    pages = []
    for i in range(count):
        if i % 2 == 0:
            # Inherits its resources and media box from the page tree.
            content = pdf.stream("", ("q 32 0 0 32 500 740 cm /Logo Do Q BT /F1 16 Tf 72 700 Td (Linearized page %d) Tj ET" % (i + 1)).encode("latin-1"), compress=True)
            pages.append(pdf.add("<</Type/Page/Parent 0 0 R/Contents %d 0 R>>" % content))
            continue
        own = gray_image(pdf, rng, 96)
        content = pdf.stream("", ("q 32 0 0 32 500 740 cm /Logo Do Q q 96 0 0 96 72 500 cm /Im%d Do Q BT /F1 16 Tf 72 700 Td (Linearized page %d) Tj ET" % (i, i + 1)).encode("latin-1"), compress=True)
        pages.append(pdf.add("<</Type/Page/Parent 0 0 R/Contents %d 0 R/Resources<</Font<</F1 %d 0 R>>/XObject<</Logo %d 0 R/Im%d %d 0 R>>>>>>" % (content, font, shared, i, own)))
    return pages, resources


def bookmarks(pdf, pages):
    outline = pdf.reserve()
    items = [pdf.reserve() for _ in pages]
    for k, (item, page) in enumerate(zip(items, pages)):
        links = ("/Prev %d 0 R" % items[k - 1] if k else "") + ("/Next %d 0 R" % items[k + 1] if k + 1 < len(items) else "")
        pdf.add("<</Title(Page %d)/Parent %d 0 R/Dest[%d 0 R/Fit]%s>>" % (k + 1, outline, page, links), item)
    pdf.add("<</Type/Outlines/First %d 0 R/Last %d 0 R/Count %d>>" % (items[0], items[-1], len(items)), outline)
    return outline


def build(path, parts, seed):
    rng = random.Random(seed)
    pdf = Pdf()
    pages, inherited = [], ""
    for part in parts:
        if part == "linearize":
            linear, resources = linearize(pdf, rng, 12)
            pages += linear
            inherited = "/MediaBox[0 0 612 792]/Resources %d 0 R" % resources
        else:
            pages += {"unused": unused_resources, "verbose": verbose_content, "fonts": font_subsets}[part](pdf, rng, 12)
    tree = page_tree(pdf, pages, inherited)
    outline = bookmarks(pdf, pages)
    info = pdf.add("<</Title(Synthetic optimizer sample)/Producer(tools/make_optimizer_samples.py)>>")
    root = pdf.add("<</Type/Catalog/Pages %d 0 R/Outlines %d 0 R>>" % (tree, outline))
    pdf.write(path, root, info)


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    samples = {
        "unused-resources.pdf": ["unused"],
        "verbose-content.pdf": ["verbose"],
        "font-subsets.pdf": ["fonts"],
        "linearize.pdf": ["linearize"],
        "combined.pdf": ["unused", "verbose", "fonts", "linearize"],
    }
    for seed, (name, parts) in enumerate(samples.items()):
        build(os.path.join(out, name), parts, seed)
        print("wrote", os.path.join(out, name))
    return 0


if __name__ == "__main__":
    sys.exit(main())
