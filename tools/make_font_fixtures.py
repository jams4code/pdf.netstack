"""Regenerates the font test fixtures with fontTools.

The fonts are synthetic: a dozen glyphs drawn as simple shapes, with known advances, metrics and
kerning, so tests can check exact numbers without shipping any third party font.

    pip install fonttools brotli
    python tools/make_font_fixtures.py

Fixtures (tests/PdfNetStack.Tests/Fixtures/Fonts):
    Test-Regular.ttf     TrueType, 2048 units per em, cmap formats 4 and 12, a composite glyph,
                         GPOS kerning (a glyph pair and a class pair, in separate subtables)
    Test.ttc             a collection of Test-Regular and Test-Bold (USE_TYPO_METRICS, weight 700)
    Test-Regular.woff    WOFF 1.0 of Test-Regular.ttf
    Test-Regular.woff2   WOFF 2.0 of Test-Regular.ttf, glyf and loca transformed
    Test.woff2           WOFF 2.0 of Test.ttc: a collection with shared tables, glyf and loca
                         transformed (fontTools has no collection encoder, so woff2_collection does it)
    Test-Outlines.ttf    TrueType outlines for the WOFF2 glyf transform: quadratic curves, deltas
                         of every triplet size, contours of 300, 520 and 800 points (the three
                         long forms of 255UInt16), glyph and composite instructions, an overlap flag, a stored bounding box that
                         differs from the points, scaled and 2x2 composites, a monospaced hmtx tail
    Test-Outlines.woff2  WOFF 2.0 with glyf, loca and hmtx transformed
    Test-Outlines-Untransformed.woff2  WOFF 2.0 with no table transformed
    Test-Restricted.ttf  Test-Regular with fsType 2, which forbids embedding
    Test-Regular.otf     name-keyed CFF, 1000 units per em, legacy 'kern' table
    Test-CID.otf         CID-keyed CFF whose CIDs differ from glyph ids (CID = 100 + 3 * glyph id)
    Test-Arabic.ttf      Arabic joining forms, a lam-alef ligature, a contextual substitution,
                         marks on bases, ligature components and other marks, kerning, a
                         tatweel for kashida justification, and a Latin fi ligature
    Test-Arabic.shaping.jsonl  HarfBuzz's shaping of sample texts in Test-Arabic.ttf, which the
                         tests compare against (needs uharfbuzz to regenerate)
    Test-Indic.ttf       Devanagari and Bengali (dev2 and bng2): nukta, akhand, reph, below base
                         ra, half forms, a post base ya, presentation ligatures, marks above and
                         below, a dotted circle; and Latin letters with combining marks and one
                         precomposed letter, for Unicode normalization
    Test-Indic.shaping.jsonl   HarfBuzz's shaping of sample texts in Test-Indic.ttf
"""

import json
import math
import struct

from io import BytesIO
from pathlib import Path

import brotli
from fontTools.cffLib import FDArrayIndex, FDSelect, FontDict
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTCollection, TTFont, getTableClass, newTable, woff2
from fontTools.ttLib.tables import ttProgram
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
    ("x", 0x78, 480, [(40, 0, 440, 500)]),
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
    ("tatweel", 0x640, 200, [(0, 0, 200, 40)]),
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
feature curs {
    lookupflag RightToLeft IgnoreMarks;
    pos cursive beh.init <anchor NULL> <anchor 0 40>;
    pos cursive beh.medi <anchor 350 0> <anchor 0 60>;
    pos cursive beh.fina <anchor 570 0> <anchor NULL>;
} curs;
""" + "".join(
    # Twenty lookups that each double x: a million glyphs from one, unless the shaper caps growth.
    f"lookup grow{n} {{ sub x by x x; }} grow{n};\n" for n in range(20)
) + "feature ccmp {\n" + "".join(f"    lookup grow{n};\n" for n in range(20)) + "} ccmp;\n"

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


# name, code point, advance, drawing as boxes. Consonants are 500 wide with a head bar; halves,
# reph and below forms are partial shapes; marks have no advance.
INDIC_GLYPHS = [
    (".notdef", None, 500, [(50, 0, 450, 700)]),
    ("space", 0x20, 250, []),
    ("dottedcircle", 0x25CC, 500, [(100, 200, 400, 500)]),
    ("zwnj", 0x200C, 0, []),
    ("zwj", 0x200D, 0, []),
    ("ka", 0x915, 500, [(0, 600, 500, 650), (200, 0, 260, 600), (80, 250, 420, 300)]),
    ("kha", 0x916, 500, [(0, 600, 500, 650), (100, 0, 160, 600), (340, 0, 400, 600)]),
    ("ga", 0x917, 450, [(0, 600, 450, 650), (350, 0, 410, 600)]),
    ("ta", 0x924, 450, [(0, 600, 450, 650), (330, 0, 390, 600), (60, 300, 330, 350)]),
    ("na", 0x928, 450, [(0, 600, 450, 650), (330, 0, 390, 600), (60, 200, 200, 250)]),
    ("ya", 0x92F, 500, [(0, 600, 500, 650), (380, 0, 440, 600), (60, 300, 380, 350)]),
    ("ra", 0x930, 400, [(0, 600, 400, 650), (100, 200, 300, 600)]),
    ("ssa", 0x937, 500, [(0, 600, 500, 650), (380, 0, 440, 600), (60, 100, 380, 150)]),
    ("sa", 0x938, 500, [(0, 600, 500, 650), (380, 0, 440, 600), (60, 400, 380, 450)]),
    ("candrabindu", 0x901, 0, [(-300, 700, -200, 800)]),
    ("anusvara", 0x902, 0, [(-270, 720, -230, 760)]),
    ("nukta", 0x93C, 0, [(-260, -100, -220, -60)]),
    ("aa", 0x93E, 150, [(40, 0, 100, 650)]),
    ("i", 0x93F, 150, [(40, 0, 100, 650), (40, 650, 400, 800)]),
    ("ii", 0x940, 150, [(40, 0, 100, 650), (-200, 650, 100, 800)]),
    ("u", 0x941, 0, [(-300, -200, -150, -60)]),
    ("e", 0x947, 0, [(-300, 680, -100, 820)]),
    ("virama", 0x94D, 0, [(-200, -150, -100, -50)]),
    ("ka.nukta", None, 500, [(0, 600, 500, 650), (200, 0, 260, 600), (80, 250, 420, 300), (220, -100, 260, -60)]),
    ("ka.half", None, 300, [(0, 600, 300, 650), (200, 0, 260, 600)]),
    ("ta.half", None, 300, [(0, 600, 300, 650), (60, 300, 300, 350)]),
    ("na.half", None, 300, [(0, 600, 300, 650), (60, 200, 200, 250)]),
    ("sa.half", None, 300, [(0, 600, 300, 650), (60, 400, 300, 450)]),
    ("ra.reph", None, 0, [(-150, 700, -50, 850)]),
    ("rakar", None, 0, [(-300, -150, -150, 0)]),
    ("ka_ssa", None, 600, [(0, 600, 600, 650), (200, 0, 260, 600), (480, 0, 540, 600)]),
    ("ta_ra", None, 450, [(0, 600, 450, 650), (330, 0, 390, 600), (60, 0, 200, 300)]),
    ("i.alt", None, 150, [(40, 0, 100, 650), (40, 650, 500, 800)]),
    ("ka.beng", 0x995, 500, [(0, 600, 500, 650), (200, 0, 260, 600), (100, 200, 400, 260)]),
    ("ya.beng", 0x9AF, 500, [(0, 600, 500, 650), (380, 0, 440, 600), (60, 200, 380, 260)]),
    ("ra.beng", 0x9B0, 450, [(0, 600, 450, 650), (330, 0, 390, 600), (100, -100, 140, -60)]),
    ("aa.beng", 0x9BE, 150, [(40, 0, 100, 650)]),
    ("e.beng", 0x9C7, 200, [(40, 0, 100, 500), (40, 500, 160, 560)]),
    ("virama.beng", 0x9CD, 0, [(-200, -150, -100, -50)]),
    ("ya.post", None, 200, [(0, 0, 60, 500), (60, 0, 180, 60)]),
    ("ra.beng.reph", None, 0, [(-150, 700, -50, 850)]),
    ("a", 0x61, 500, [(50, 0, 450, 500)]),
    ("gravecomb", 0x300, 0, [(-350, 600, -250, 750)]),
    ("acutecomb", 0x301, 0, [(-250, 600, -150, 750)]),
    ("dotabovecomb", 0x307, 0, [(-280, 600, -220, 660)]),
    ("dotbelowcomb", 0x323, 0, [(-280, -150, -220, -90)]),
    ("aacute", 0xE1, 500, [(50, 0, 450, 500), (250, 600, 350, 750)]),
    ("adotbelow", 0x1EA1, 500, [(50, 0, 450, 500), (220, -150, 280, -90)]),
]

INDIC_FEA = """
languagesystem DFLT dflt;
languagesystem latn dflt;
languagesystem dev2 dflt;
languagesystem bng2 dflt;
@CONSONANTS = [dottedcircle ka kha ga ta na ya ra ssa sa ka.nukta ka_ssa ta_ra ka.beng ya.beng ra.beng];
@MARKS = [candrabindu anusvara nukta u e virama rakar ra.reph virama.beng ra.beng.reph gravecomb acutecomb dotabovecomb dotbelowcomb];
table GDEF {
    GlyphClassDef [@CONSONANTS aa i ii i.alt ka.half ta.half na.half sa.half aa.beng e.beng ya.post a aacute adotbelow], , @MARKS, ;
} GDEF;
feature nukt { script dev2; sub ka nukta by ka.nukta; } nukt;
feature akhn { script dev2; sub ka virama ssa by ka_ssa; } akhn;
feature rphf {
    script dev2; sub ra virama by ra.reph;
    script bng2; sub ra.beng virama.beng by ra.beng.reph;
} rphf;
feature blwf { script dev2; sub virama ra by rakar; } blwf;
feature half { script dev2; sub ka virama by ka.half; sub ta virama by ta.half; sub na virama by na.half; sub sa virama by sa.half; } half;
feature pstf { script bng2; sub virama.beng ya.beng by ya.post; } pstf;
feature pres {
    script dev2;
    sub ta rakar by ta_ra;
    sub ka.half ssa by ka_ssa;
} pres;
feature psts { script dev2; sub i' [ka kha] by i.alt; } psts;
markClass [candrabindu anusvara e ra.reph ra.beng.reph] <anchor 0 650> @TOP;
markClass [u nukta rakar virama virama.beng] <anchor 0 0> @BOTTOM;
markClass [gravecomb acutecomb dotabovecomb] <anchor -250 550> @LATIN_TOP;
markClass [dotbelowcomb] <anchor -250 0> @LATIN_BOTTOM;
feature abvm { pos base @CONSONANTS <anchor 300 650> mark @TOP; } abvm;
feature blwm { pos base @CONSONANTS <anchor 250 0> mark @BOTTOM; } blwm;
feature mark {
    pos base [a adotbelow] <anchor 250 550> mark @LATIN_TOP <anchor 250 0> mark @LATIN_BOTTOM;
} mark;
feature mkmk { pos mark [dotabovecomb gravecomb acutecomb] <anchor -250 700> mark @LATIN_TOP; } mkmk;
feature kern { script dev2; pos ka aa -20; } kern;
"""

INDIC_SAMPLES = [
    ("ltr", "deva", "\u0915\u093f \u0915\u094d\u0937 \u0930\u094d\u0915 \u0924\u094d\u0930 \u0938\u094d\u0924 \u0915\u093c \u0915\u0902 \u0915\u0941\u0902 \u0915\u093e"),
    ("ltr", "deva", "\u0915\u094d\u200d\u0937 \u0915\u094d\u200c\u0937 \u0930\u094d\u0915\u093f \u0928\u094d\u0924\u094d\u0930\u0947 \u0915\u0947\u0902"),
    ("ltr", "deva", "\u093f \u094d\u0915 \u0915\u093f\u093f \u0930\u094d \u0958 \u0930\u094d\u200d\u092f \u0915\u0901"),
    ("ltr", "deva", "\u0938\u094d\u0924\u094d\u0930\u0940 \u0916\u093f \u0917\u093f \u0930\u0941 \u0915\u094d\u0937\u093f"),
    ("ltr", "beng", "\u0995\u09c7 \u09b0\u09cd\u0995 \u0995\u09cd\u09af \u0995\u09cb \u09b0\u09cd\u0995\u09c7 \u0995\u09be"),
    ("ltr", "latn", "a\u0301 \u00e1 a\u0323\u0307 a\u0307\u0323 \u1ea1\u0307 \u1ea1 a\u0300 a\u2003a\u2009a"),
]


def build_indic():
    names = [g[0] for g in INDIC_GLYPHS]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: name for name, cp, _, _ in INDIC_GLYPHS if cp is not None})
    fb.setupGlyf({name: boxes_ttf(boxes, 1) for name, _, _, boxes in INDIC_GLYPHS})
    fb.setupHorizontalMetrics({name: (adv, 0) for name, _, adv, _ in INDIC_GLYPHS})
    fb.setupHorizontalHeader(ascent=900, descent=-300, lineGap=0)
    fb.setupNameTable({"familyName": "PdfNetStack Test Indic", "styleName": "Regular", "psName": "PdfNetStackTestIndic-Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-300, usWinAscent=950, usWinDescent=350, sxHeight=500, sCapHeight=650, fsType=0)
    fb.setupPost()
    font = fb.font
    addOpenTypeFeaturesFromString(font, INDIC_FEA)
    return font


def shaping_reference(font_bytes, name, samples):
    import uharfbuzz as hb

    blob = hb.Blob(font_bytes)
    lines = []
    for direction, script, text in samples:
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
    (OUT / name.replace(".ttf", ".shaping.jsonl")).write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


# name, code point, advance; drawn by draw_outline. The last three share an advance, so hmtx ends
# with a monospaced run of left side bearings only.
OUTLINE_GLYPHS = [
    (".notdef", None, 500), ("space", 0x20, 250), ("O", 0x4F, 1000), ("Z", 0x5A, 700), ("I", 0x49, 350),
    ("V", 0x56, 600), ("B", 0x42, 500), ("acute", 0xB4, 300), ("Oacute", 0xD3, 1000), ("OO", 0x152, 1000),
    ("zero", 0x30, 1000), ("one", 0x31, 520), ("two", 0x32, 520), ("three", 0x33, 520),
]

# Point deltas covering every WOFF2 triplet size: x or y only, 4 bit, 8 bit, 12 bit and 16 bit.
ZIGZAG = [(0, 7), (0, -300), (0, 1000), (9, 0), (-700, 0), (900, 0), (3, 4), (-20, 35), (60, -10), (-1, -64),
          (200, 300), (-600, 500), (700, -700), (2000, -1500), (-3000, 4000), (12000, -9000), (-12000, 9000), (-500, -200)]


def program(code):
    p = ttProgram.Program()
    p.fromBytecode(code)
    return p


def draw_outline(name, glyphs):
    pen = TTGlyphPen(glyphs)
    if name in (".notdef", "B"):
        pen.moveTo((100, 0))
        pen.lineTo((100, 700))
        pen.lineTo((400, 700))
        pen.lineTo((400, 0))
        pen.closePath()
    elif name == "O":
        # An outer contour of on and off curve points, and a counter whose off curve points
        # follow each other, with the on curve point between them implied.
        pen.moveTo((500, 0))
        pen.qCurveTo((800, 0), (800, 350))
        pen.qCurveTo((800, 700), (500, 700))
        pen.qCurveTo((200, 700), (200, 350))
        pen.qCurveTo((200, 0), (500, 0))
        pen.closePath()
        pen.moveTo((500, 120))
        pen.qCurveTo((320, 120), (320, 580), (500, 580))
        pen.qCurveTo((680, 580), (680, 120), (500, 120))
        pen.closePath()
    elif name == "Z":
        x, y = -50, -60
        pen.moveTo((x, y))
        for dx, dy in ZIGZAG:
            x, y = x + dx, y + dy
            pen.lineTo((x, y))
        pen.closePath()
    elif name == "I":
        pen.moveTo((100, 0))
        pen.lineTo((100, 700))
        pen.lineTo((250, 700))
        pen.lineTo((250, 0))
        pen.closePath()
    elif name == "V":
        for inset in (0, 60):
            pen.moveTo((50 + inset, 700))
            pen.lineTo((550 - inset, 700))
            pen.lineTo((300, inset))
            pen.closePath()
    elif name == "acute":
        pen.moveTo((100, 750))
        pen.lineTo((200, 900))
        pen.lineTo((250, 750))
        pen.closePath()
    elif name == "Oacute":
        pen.addComponent("O", (0.9, 0, 0, 0.9, 50, 0))
        pen.addComponent("acute", (1, 0, 0.25, 1, 250, 30))
    elif name == "OO":
        pen.addComponent("O", (0.5, 0, 0, 0.8, 0, 0))
        pen.addComponent("O", (0.5, 0, 0, 0.8, 500, 0))
    elif name == "zero":
        # Contours of 800, 520 and 300 points: three 255UInt16 encodings of the point count.
        for radius, count in ((450, 800), (300, 520), (150, 300)):
            for k in range(count):
                angle = 2 * math.pi * k / count
                point = (round(500 + radius * math.cos(angle)), round(350 + radius * math.sin(angle)))
                if k == 0:
                    pen.moveTo(point)
                else:
                    pen.lineTo(point)
            pen.closePath()
    elif name in ("one", "two", "three"):
        left = {"one": 60, "two": 90, "three": 120}[name]
        pen.moveTo((left, 0))
        pen.lineTo((left, 700))
        pen.lineTo((left + 300, 700))
        pen.lineTo((left + 300, 0))
        pen.closePath()
    glyph = pen.glyph()
    if name == "I":
        glyph.program = program(b"\xb1\x02\x03\x21\x21")  # PUSHB[1] 2 3, POP, POP
    elif name == "Oacute":
        glyph.program = program(b"\xb0\x01\x21")  # PUSHB[0] 1, POP
    elif name == "V":
        glyph.flags[0] |= 0x40  # OVERLAP_SIMPLE
    return glyph


def build_outlines():
    names = [g[0] for g in OUTLINE_GLYPHS]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: name for name, cp, _ in OUTLINE_GLYPHS if cp is not None})
    glyphs = {}
    for name in names:
        glyphs[name] = draw_outline(name, glyphs)
    fb.setupGlyf(glyphs)
    glyf = fb.font["glyf"]
    bearings = {}
    for name in names:
        glyph = glyf[name]
        glyph.recalcBounds(glyf)
        bearings[name] = getattr(glyph, "xMin", 0) if glyph.numberOfContours else 0
    fb.setupHorizontalMetrics({name: (adv, bearings[name]) for name, _, adv in OUTLINE_GLYPHS})
    fb.setupHorizontalHeader(ascent=900, descent=-300, lineGap=0)
    fb.setupNameTable({"familyName": "PdfNetStack Test Outlines", "styleName": "Regular", "psName": "PdfNetStackTestOutlines-Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-300, usWinAscent=950, usWinDescent=350, sxHeight=500, sCapHeight=700, fsType=0)
    fb.setupPost()

    # A stored bounding box wider than the points, which the WOFF2 bbox stream must carry. hmtx
    # keeps lsb = xMin, so the hmtx transform still applies to the proportional glyphs; one
    # bearing of the monospaced run differs from xMin, so that run stays stored.
    font = reload(fb.font)
    font.recalcBBoxes = False
    b = font["glyf"]["B"]
    b.xMin, b.yMin, b.xMax, b.yMax = 50, -50, 450, 750
    font["hmtx"]["B"] = (font["hmtx"]["B"][0], 50)
    font["hmtx"]["three"] = (520, 125)
    return font


def woff2_collection(ttc):
    """A WOFF 2.0 collection (section 4.2) of a TTC: shared tables stored once, glyf and loca transformed."""
    count = struct.unpack(">I", ttc[8:12])[0]
    fonts = []
    for offset in struct.unpack(f">{count}I", ttc[12:12 + 4 * count]):
        flavor, n = struct.unpack(">IH", ttc[offset:offset + 6])
        records = {}
        for i in range(n):
            tag, _, start, length = struct.unpack(">4sIII", ttc[offset + 12 + 16 * i:offset + 28 + 16 * i])
            records[tag.decode("latin-1")] = (start, length)
        fonts.append((flavor, records))

    # Unique tables in font order, each loca right after its glyf (section 5.5).
    order, index = [], {}
    for font_number, (_, records) in enumerate(fonts):
        for tag in sorted(records):
            if tag == "loca":
                continue
            for t in ([tag, "loca"] if tag == "glyf" else [tag]):
                if records[t] not in index:
                    index[records[t]] = len(order)
                    order.append((t, records[t], font_number))

    directory, data = b"", b""
    for tag, (start, length), font_number in order:
        raw = ttc[start:start + length]
        known = woff2.woff2KnownTags.index(tag) if tag in woff2.woff2KnownTags else 63
        directory += bytes([known]) + (tag.encode("latin-1") if known == 63 else b"") + woff2.packBase128(length)
        if tag == "glyf":
            source = TTFont(BytesIO(ttc), fontNumber=font_number, recalcBBoxes=False, recalcTimestamp=False)
            work = TTFont(recalcBBoxes=False, recalcTimestamp=False)
            for t in ("maxp", "head", "loca", "glyf"):
                table = {"loca": woff2.WOFF2LocaTable, "glyf": woff2.WOFF2GlyfTable}.get(t, getTableClass(t))(t)
                work.tables[t] = table
                table.decompile(source.reader[t], work)
            raw = work["glyf"].transform(work)
            directory += woff2.packBase128(len(raw))
        elif tag == "loca":
            raw = b""
            directory += woff2.packBase128(0)
        data += raw

    collection = struct.pack(">I", struct.unpack(">I", ttc[4:8])[0]) + woff2.pack255UShort(len(fonts))
    for flavor, records in fonts:
        collection += woff2.pack255UShort(len(records)) + struct.pack(">I", flavor)
        collection += b"".join(woff2.pack255UShort(index[records[tag]]) for tag in sorted(records))

    compressed = brotli.compress(data, mode=brotli.MODE_FONT)
    body = directory + collection + compressed
    length = 48 + len(body)
    padded = length + (-length % 4)
    header = struct.pack(">4sIIHHIIHHIIIII", b"wOF2", 0x74746366, padded, len(order), 0, len(ttc), len(compressed),
                         1, 0, 0, 0, 0, 0, 0)
    return header + body + b"\0" * (padded - length)


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


def save_woff2(source, name, transform):
    """WOFF 2.0 of a saved fixture, transforming the given tables, as fontTools.ttLib.woff2.compress does."""
    font = TTFont(OUT / source, recalcBBoxes=False, recalcTimestamp=False)
    font.flavor = "woff2"
    font.flavorData = woff2.WOFF2FlavorData(transformedTables=transform)
    buffer = BytesIO()
    font.save(buffer, reorderTables=False)
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
    (OUT / "Test.woff2").write_bytes(woff2_collection(buffer.getvalue()))
    print(f"Test.woff2: {len((OUT / 'Test.woff2').read_bytes())} bytes")

    save_woff2("Test-Regular.ttf", "Test-Regular.woff2", {"glyf", "loca"})
    save(build_outlines(), "Test-Outlines.ttf")
    save_woff2("Test-Outlines.ttf", "Test-Outlines.woff2", {"glyf", "loca", "hmtx"})
    save_woff2("Test-Outlines.ttf", "Test-Outlines-Untransformed.woff2", set())

    save(build_cff(cid_keyed=False), "Test-Regular.otf")
    save(build_cff(cid_keyed=True), "Test-CID.otf")

    arabic = build_arabic()
    save(arabic, "Test-Arabic.ttf")
    shaping_reference((OUT / "Test-Arabic.ttf").read_bytes(), "Test-Arabic.ttf", ARABIC_SAMPLES)

    save(build_indic(), "Test-Indic.ttf")
    shaping_reference((OUT / "Test-Indic.ttf").read_bytes(), "Test-Indic.ttf", INDIC_SAMPLES)


if __name__ == "__main__":
    main()
