"""Regenerates the font test fixtures with fontTools.

The fonts are synthetic: a dozen glyphs drawn as simple shapes, with known advances, metrics and
kerning, so tests can check exact numbers without shipping any third party font.

    pip install fonttools
    python tools/make_font_fixtures.py

Fixtures (tests/PdfNetStack.Tests/Fixtures/Fonts):
    Test-Regular.ttf     TrueType, 2048 units per em, cmap formats 4 and 12, a composite glyph,
                         GPOS kerning (a glyph pair and a class pair, in separate subtables)
    Test.ttc             a collection of Test-Regular and Test-Bold (USE_TYPO_METRICS, weight 700)
    Test-Regular.woff    WOFF 1.0 of Test-Regular.ttf
    Test-Restricted.ttf  Test-Regular with fsType 2, which forbids embedding
    Test-Regular.otf     name-keyed CFF, 1000 units per em, legacy 'kern' table
    Test-CID.otf         CID-keyed CFF whose CIDs differ from glyph ids (CID = 100 + 3 * glyph id)
"""

from io import BytesIO
from pathlib import Path

from fontTools.cffLib import FDArrayIndex, FDSelect, FontDict
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTCollection, TTFont, newTable
from fontTools.ttLib.tables._k_e_r_n import KernTable_format_0

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Fonts"

# name, code point, advance in 1000 units, drawing as (x0, y0, x1, y1) boxes in 1000 units
GLYPHS = [
    (".notdef", None, 500, [(50, 0, 450, 700)]),
    ("space", 0x20, 250, []),
    ("A", 0x41, 600, [(50, 0, 550, 700)]),
    ("V", 0x56, 600, [(60, 0, 540, 700)]),
    ("T", 0x54, 550, [(40, 600, 510, 700), (225, 0, 325, 600)]),
    ("o", 0x6F, 500, [(50, 0, 450, 480)]),
    ("H", 0x48, 650, [(60, 0, 160, 700), (490, 0, 590, 700), (160, 300, 490, 400)]),
    ("x", 0x78, 480, [(40, 0, 440, 500)]),
    ("acute", 0xB4, 300, [(100, 750, 200, 900)]),
    ("Aacute", 0xC1, 600, None),  # composite: A + acute
    ("smile", 0x1F600, 800, [(50, 0, 750, 700)]),
    ("check", 0x2713, 700, [(50, 0, 650, 650)]),
]

KERN_FEA = """
languagesystem DFLT dflt;
languagesystem latn dflt;
@LEFT = [T];
@RIGHT = [o x];
feature kern {
    pos A V -164;
    subtable;
    pos @LEFT @RIGHT -123;
} kern;
"""


def boxes_ttf(boxes, scale):
    pen = TTGlyphPen(None)
    for x0, y0, x1, y1 in boxes:
        x0, y0, x1, y1 = (round(v * scale) for v in (x0, y0, x1, y1))
        pen.moveTo((x0, y0))
        pen.lineTo((x0, y1))
        pen.lineTo((x1, y1))
        pen.lineTo((x1, y0))
        pen.closePath()
    return pen.glyph()


def build_ttf(family, style, weight, typo_metrics):
    scale = 2.048
    names = [g[0] for g in GLYPHS]
    fb = FontBuilder(2048, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: name for name, cp, _, _ in GLYPHS if cp is not None})

    glyphs = {}
    for name, _, _, boxes in GLYPHS:
        if boxes is None:
            pen = TTGlyphPen(glyphs)
            pen.addComponent("A", (1, 0, 0, 1, 0, 0))
            pen.addComponent("acute", (1, 0, 0, 1, round(150 * scale), 0))
            glyphs[name] = pen.glyph()
        else:
            glyphs[name] = boxes_ttf(boxes, scale)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics({name: (round(adv * scale), 0) for name, _, adv, _ in GLYPHS})

    # hhea 1900/-500/0, typo 1600/-400/200: tests can tell which set was used.
    fb.setupHorizontalHeader(ascent=1900, descent=-500, lineGap=0)
    ps = f"PdfNetStackTest-{style}"
    fb.setupNameTable({"familyName": family, "styleName": style, "psName": ps, "fullName": f"{family} {style}"})
    fb.setupOS2(
        sTypoAscender=1600, sTypoDescender=-400, sTypoLineGap=200, usWinAscent=2100, usWinDescent=600,
        usWeightClass=weight, sxHeight=round(500 * scale), sCapHeight=round(700 * scale),
        yStrikeoutPosition=round(300 * scale), yStrikeoutSize=round(50 * scale), fsType=0,
        fsSelection=(0x80 if typo_metrics else 0) | (0x20 if weight >= 700 else 0x40),
    )
    fb.setupPost(underlinePosition=-round(100 * scale), underlineThickness=round(50 * scale))
    font = fb.font
    font["OS/2"].version = 4
    font["head"].macStyle = 1 if weight >= 700 else 0
    addOpenTypeFeaturesFromString(font, KERN_FEA)
    return font


def build_cff(cid_keyed):
    names = [g[0] for g in GLYPHS]
    fb = FontBuilder(1000, isTTF=False)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: name for name, cp, _, _ in GLYPHS if cp is not None})
    charstrings = {}
    for name, _, adv, boxes in GLYPHS:
        pen = T2CharStringPen(adv, None)
        for x0, y0, x1, y1 in boxes if boxes is not None else [(50, 0, 550, 700), (250, 750, 350, 900)]:
            pen.moveTo((x0, y0))
            pen.lineTo((x0, y1))
            pen.curveTo((x0 + 10, y1 + 5), (x1 - 10, y1 + 5), (x1, y1))
            pen.lineTo((x1, y0))
            pen.closePath()
        charstrings[name] = pen.getCharString()
    style = "CID" if cid_keyed else "Regular"
    ps = f"PdfNetStackTest-{style}"
    fb.setupCFF(ps, {"FullName": f"PdfNetStack Test {style}"}, charstrings, {})
    fb.setupHorizontalMetrics({name: (adv, 0) for name, _, adv, _ in GLYPHS})
    fb.setupHorizontalHeader(ascent=900, descent=-250, lineGap=50)
    fb.setupNameTable({"familyName": "PdfNetStack Test", "styleName": style, "psName": ps})
    fb.setupOS2(sTypoAscender=800, sTypoDescender=-200, sTypoLineGap=0, usWinAscent=950, usWinDescent=260,
                sxHeight=500, sCapHeight=700, fsType=0)
    fb.setupPost(underlinePosition=-100, underlineThickness=50)
    font = fb.font

    kern = newTable("kern")
    kern.version = 0
    sub = KernTable_format_0()
    sub.version, sub.coverage, sub.format = 0, 1, 0
    sub.kernTable = {("A", "V"): -80, ("T", "o"): -60}
    kern.kernTables = [sub]
    font["kern"] = kern

    if cid_keyed:
        make_cid_keyed(font)
    return font


def make_cid_keyed(font):
    """Turns a name-keyed CFF into a CID-keyed one with CID = 100 + 3 * glyph id."""
    cff = font["CFF "].cff
    top = cff.topDictIndex[0]
    order = font.getGlyphOrder()
    rename = {name: ".notdef" if i == 0 else f"cid{100 + 3 * i:05d}" for i, name in enumerate(order)}

    fd = FontDict()
    fd.setCFF2(False)
    fd.Private = top.Private
    fd.FontName = "PdfNetStackTest-CID-Generic"
    top.FDArray = FDArrayIndex()
    top.FDArray.append(fd)
    top.FDSelect = FDSelect()
    top.FDSelect.format = 3
    top.FDSelect.gidArray = [0] * len(order)
    top.ROS = ("Adobe", "Identity", 0)
    top.CIDCount = 100 + 3 * len(order)
    top.rawDict.pop("Private", None)
    top.__dict__.pop("Private", None)

    charstrings = top.CharStrings
    old = dict(charstrings.charStrings)
    charstrings.charStrings = {rename[k]: v for k, v in old.items()}
    for cs in charstrings.values():
        cs.private = fd.Private
    font.setGlyphOrder([rename[n] for n in order])
    top.charset = [rename[n] for n in order]
    cff.fontNames = ["PdfNetStackTest-CID"]

    # Tables keyed by glyph name follow the rename.
    hmtx = font["hmtx"]
    hmtx.metrics = {rename[k]: v for k, v in hmtx.metrics.items()}
    for table in font["cmap"].tables:
        table.cmap = {cp: rename[name] for cp, name in table.cmap.items()}
    kern = font["kern"].kernTables[0]
    kern.kernTable = {(rename[a], rename[b]): v for (a, b), v in kern.kernTable.items()}
    font["post"].formatType = 3.0


def save(font, name, flavor=None):
    font.flavor = flavor
    buffer = BytesIO()
    font.save(buffer)
    (OUT / name).write_bytes(buffer.getvalue())
    print(f"{name}: {len(buffer.getvalue())} bytes")


def reload(font):
    buffer = BytesIO()
    font.save(buffer)
    buffer.seek(0)
    return TTFont(buffer)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    regular = build_ttf("PdfNetStack Test", "Regular", 400, typo_metrics=False)
    save(regular, "Test-Regular.ttf")
    save(reload(regular), "Test-Regular.woff", flavor="woff")

    restricted = reload(regular)
    restricted["OS/2"].fsType = 2
    save(restricted, "Test-Restricted.ttf")

    bold = build_ttf("PdfNetStack Test", "Bold", 700, typo_metrics=True)
    collection = TTCollection()
    collection.fonts = [reload(regular), reload(bold)]
    buffer = BytesIO()
    collection.save(buffer)
    (OUT / "Test.ttc").write_bytes(buffer.getvalue())
    print(f"Test.ttc: {len(buffer.getvalue())} bytes")

    save(build_cff(cid_keyed=False), "Test-Regular.otf")
    save(build_cff(cid_keyed=True), "Test-CID.otf")


if __name__ == "__main__":
    main()
