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
    Test-Arabic.ttf      Arabic joining forms, a lam-alef ligature, a contextual substitution,
                         marks on bases, ligature components and other marks, kerning, and a
                         Latin fi ligature
    Test-Arabic.shaping.jsonl  HarfBuzz's shaping of sample texts in Test-Arabic.ttf, which the
                         tests compare against (needs uharfbuzz to regenerate)
"""

import json

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


ARABIC_GLYPHS = [
    # name, code point, advance, boxes
    (".notdef", None, 500, [(50, 0, 450, 700)]),
    ("space", 0x20, 250, []),
    ("period", 0x2E, 250, [(100, 0, 150, 50)]),
    ("f", 0x66, 300, [(50, 0, 150, 700), (50, 500, 300, 560)]),
    ("i", 0x69, 250, [(80, 0, 170, 500), (80, 600, 170, 680)]),
    ("f_i", None, 520, [(50, 0, 150, 700), (50, 500, 300, 560), (350, 0, 440, 500)]),
    ("T", 0x54, 550, [(40, 600, 510, 700), (225, 0, 325, 600)]),
    ("o", 0x6F, 500, [(50, 0, 450, 480)]),
    ("alef", 0x627, 220, [(80, 0, 140, 700)]),
    ("alef.fina", None, 240, [(80, 0, 140, 700), (0, 0, 80, 40)]),
    ("beh", 0x628, 600, [(50, 0, 550, 120), (275, -200, 325, -150)]),
    ("beh.init", None, 400, [(100, 0, 400, 120), (225, -200, 275, -150)]),
    ("beh.init.alt", None, 450, [(100, 0, 450, 160), (225, -200, 275, -150)]),
    ("beh.medi", None, 350, [(0, 0, 350, 120), (150, -200, 200, -150)]),
    ("beh.fina", None, 620, [(0, 0, 570, 120), (275, -200, 325, -150)]),
    ("lam", 0x644, 450, [(50, 0, 400, 120), (330, 120, 390, 750)]),
    ("lam.init", None, 300, [(100, 0, 300, 120), (230, 120, 290, 750)]),
    ("lam.medi", None, 280, [(0, 0, 280, 120), (200, 120, 260, 750)]),
    ("lam.fina", None, 470, [(0, 0, 420, 120), (330, 120, 390, 750)]),
    ("meem", 0x645, 480, [(50, -300, 120, 100), (100, 0, 430, 200)]),
    ("meem.init", None, 380, [(100, 0, 380, 200)]),
    ("meem.medi", None, 330, [(0, 0, 330, 200)]),
    ("meem.fina", None, 500, [(0, 0, 450, 200), (60, -300, 120, 0)]),
    ("lam_alef", None, 520, [(50, 0, 470, 120), (100, 120, 160, 700), (360, 120, 420, 700)]),
    ("lam_alef.fina", None, 540, [(0, 0, 490, 120), (120, 120, 180, 700), (380, 120, 440, 700)]),
    ("fatha", 0x64E, 0, [(-150, 650, -50, 690)]),
    ("kasra", 0x650, 0, [(-150, -280, -50, -240)]),
    ("shadda", 0x651, 0, [(-160, 620, -40, 700)]),
    ("zwnj", 0x200C, 0, []),
    ("zwj", 0x200D, 0, []),
]

ARABIC_FEA = """
languagesystem DFLT dflt;
languagesystem arab dflt;
languagesystem latn dflt;
@BASES = [beh beh.init beh.init.alt beh.medi beh.fina lam lam.init lam.medi lam.fina meem meem.init meem.medi meem.fina alef alef.fina];
@MARKS = [fatha kasra shadda];
table GDEF {
    GlyphClassDef @BASES, [lam_alef lam_alef.fina f_i], @MARKS, ;
} GDEF;
feature init { sub [beh lam meem] by [beh.init lam.init meem.init]; } init;
feature medi { sub [beh lam meem] by [beh.medi lam.medi meem.medi]; } medi;
feature fina { sub [beh lam meem alef] by [beh.fina lam.fina meem.fina alef.fina]; } fina;
feature rlig {
    lookupflag IgnoreMarks;
    sub lam.init alef.fina by lam_alef;
    sub lam.medi alef.fina by lam_alef.fina;
} rlig;
feature calt {
    lookupflag IgnoreMarks;
    sub beh.init' meem.medi by beh.init.alt;
} calt;
feature liga { sub f i by f_i; } liga;
markClass [fatha shadda] <anchor 0 600> @TOP;
markClass [kasra] <anchor 0 -100> @BOTTOM;
feature mark {
    pos base @BASES <anchor 200 700> mark @TOP <anchor 200 -250> mark @BOTTOM;
    pos ligature [lam_alef lam_alef.fina] <anchor 130 800> mark @TOP <anchor 130 -200> mark @BOTTOM
        ligComponent <anchor 390 800> mark @TOP <anchor 390 -200> mark @BOTTOM;
} mark;
feature mkmk {
    pos mark shadda <anchor 0 850> mark @TOP;
} mkmk;
feature kern {
    pos T o -80;
    pos beh.fina beh.init -30;
} kern;
"""

ARABIC_SAMPLES = [
    ("rtl", "arab", "\u0628\u0628\u0628 \u0628"),  # beh: init medi fina, isol
    ("rtl", "arab", "\u0644\u0627 \u0628\u0644\u0627"),  # lam-alef, alone and final
    ("rtl", "arab", "\u0628\u0645\u0645 \u0645\u0628\u0628"),  # calt: beh.init before meem.medi
    ("rtl", "arab", "\u0628\u064e\u0628\u0651\u064e\u0628\u0650"),  # marks on bases, shadda + fatha
    ("rtl", "arab", "\u0644\u064e\u0627\u064e \u0628\u0644\u0651\u0627\u0650"),  # marks on ligature components
    ("rtl", "arab", "\u0628\u200c\u0628\u200d \u0628."),  # ZWNJ breaks joining, ZWJ keeps it
    ("ltr", "latn", "fi To fifo"),
]


def build_arabic():
    names = [g[0] for g in ARABIC_GLYPHS]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: name for name, cp, _, _ in ARABIC_GLYPHS if cp is not None})
    fb.setupGlyf({name: boxes_ttf(boxes, 1) for name, _, _, boxes in ARABIC_GLYPHS})
    fb.setupHorizontalMetrics({name: (adv, 0) for name, _, adv, _ in ARABIC_GLYPHS})
    fb.setupHorizontalHeader(ascent=900, descent=-400, lineGap=0)
    fb.setupNameTable({"familyName": "PdfNetStack Test Arabic", "styleName": "Regular", "psName": "PdfNetStackTestArabic-Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-400, usWinAscent=950, usWinDescent=450, sxHeight=500, sCapHeight=700, fsType=0)
    fb.setupPost()
    font = fb.font
    addOpenTypeFeaturesFromString(font, ARABIC_FEA)
    return font


def shaping_reference(font_bytes, name):
    import uharfbuzz as hb

    blob = hb.Blob(font_bytes)
    lines = []
    for direction, script, text in ARABIC_SAMPLES:
        font = hb.Font(hb.Face(blob))
        buf = hb.Buffer()
        buf.add_codepoints([ord(c) for c in text])
        buf.direction = direction
        buf.script = script
        hb.shape(font, buf, {})
        glyphs = [[i.codepoint, i.cluster, p.x_advance, p.x_offset, p.y_offset] for i, p in zip(buf.glyph_infos, buf.glyph_positions)]
        if direction == "rtl":
            glyphs.reverse()
        lines.append(json.dumps({"font": name, "text": text, "direction": direction, "script": script, "glyphs": glyphs}, ensure_ascii=False))
    (OUT / "Test-Arabic.shaping.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


# A fixed creation and modification time, so regenerating gives byte identical fixtures.
TIMESTAMP = 0xE4000000


def pin(font):
    font.recalcTimestamp = False
    font["head"].created = TIMESTAMP
    font["head"].modified = TIMESTAMP
    return font


def save(font, name, flavor=None):
    pin(font).flavor = flavor
    buffer = BytesIO()
    font.save(buffer)
    (OUT / name).write_bytes(buffer.getvalue())
    print(f"{name}: {len(buffer.getvalue())} bytes")


def reload(font):
    buffer = BytesIO()
    pin(font).save(buffer)
    buffer.seek(0)
    return pin(TTFont(buffer))


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

    arabic = build_arabic()
    save(arabic, "Test-Arabic.ttf")
    shaping_reference((OUT / "Test-Arabic.ttf").read_bytes(), "Test-Arabic.ttf")


if __name__ == "__main__":
    main()
