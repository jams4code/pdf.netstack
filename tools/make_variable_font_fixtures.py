"""Regenerates the variable font test fixtures with fontTools, and their references.

The fonts are synthetic: a few glyphs drawn as simple shapes in three masters (weights 100, 400
and 900), merged by fontTools.varLib into one variable font with TrueType outlines and one with
CFF2 outlines. No third party font is involved.

    pip install fonttools uharfbuzz
    python tools/make_variable_font_fixtures.py

Fixtures (tests/PdfNetStack.Tests/Fixtures/Fonts):
    Test-Variable.ttf    gvar with interpolated (IUP) points and a composite glyph whose component
                         moves, HVAR, MVAR (x height, cap height, typographic ascender, strikeout,
                         underline), cvar, five named instances, GPOS kerning and mark anchors with
                         variation deltas, and a GSUB feature variation (a heavy dollar from
                         weight 600)
    Test-Variable.otf    the same font with CFF2 outlines: blends in charstrings and in the Private
                         DICT, a local and a global subroutine that blend, and a glyph whose line
                         run is longer than CFF's 48 operands
    Test-Variable.instances.json  fontTools' instances (varLib.instancer) of both fonts at the
                         named instances and at weight 550: glyph outlines, advances and metrics
    Test-Variable.shaping.jsonl   HarfBuzz's shaping of sample texts at the same locations
"""

import json
from array import array
from io import BytesIO
from pathlib import Path

from fontTools.designspaceLib import AxisDescriptor, DesignSpaceDocument, InstanceDescriptor, RuleDescriptor, SourceDescriptor
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.misc.psCharStrings import T2CharString
from fontTools.pens.recordingPen import RecordingPen
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables import ttProgram
from fontTools.varLib import build as build_variable
from fontTools.varLib import instancer
from fontTools.varLib.instancer import instantiateVariableFont

# Blends stay exact, as HarfBuzz and PdfNetStack keep them; fontTools rounds CFF2 operands by default.
_instantiate_cff2 = instancer.instantiateCFF2
instancer.instantiateCFF2 = lambda *args, **kwargs: _instantiate_cff2(*args, round=lambda v: v, **kwargs)

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Fonts"

TIMESTAMP = 3786825600  # 2020-01-01, in seconds since 1904
WEIGHTS = [100, 400, 900]
INSTANCES = [("Thin", 100), ("Regular", 400), ("SemiBold", 600), ("Bold", 700), ("Black", 900)]
LOCATIONS = [{"wght": w} for _, w in INSTANCES] + [{"wght": 550}]

GLYPHS = [".notdef", "space", "A", "H", "O", "T", "o", "Z", "dollar", "dollar.heavy", "acute", "Aacute", "gravecomb"]
CMAP = {0x20: "space", 0x41: "A", 0x48: "H", 0x4F: "O", 0x54: "T", 0x6F: "o", 0x5A: "Z", 0x24: "dollar",
        0xB4: "acute", 0xC1: "Aacute", 0x300: "gravecomb"}

SAMPLES = [
    ("ltr", "latn", "To HOT A$Z"),
    ("ltr", "latn", "\u00c1 A\u0300 o\u0300 To"),
    ("ltr", "latn", "$$ T$o"),
]


def stem(w):
    """Stem thickness at weight w."""
    return 20 + w // 6


def draw(pen, name, w, quadratic):
    s = stem(w)

    def box(x0, y0, x1, y1):
        pen.moveTo((x0, y0))
        pen.lineTo((x0, y1))
        pen.lineTo((x1, y1))
        pen.lineTo((x1, y0))
        pen.closePath()

    if name in (".notdef", "H"):
        box(50, 0, 50 + s, 700)
        box(500 - s, 0, 500, 700)
        box(50 + s, 330, 500 - s, 330 + s // 2)
    elif name == "A":
        pen.moveTo((20, 0))
        pen.lineTo((280, 700))
        pen.lineTo((320 + s // 4, 700))
        pen.lineTo((580 + s // 3, 0))
        pen.lineTo((580 + s // 3 - s, 0))
        pen.lineTo((300, 560 - s))
        pen.lineTo((20 + s, 0))
        pen.closePath()
    elif name in ("O", "o"):
        top = 700 if name == "O" else 480
        right = 600 + s // 2 if name == "O" else 440 + s // 2
        mid = right // 2
        half = top // 2
        if quadratic:
            pen.moveTo((mid, 0))
            pen.qCurveTo((right, 0), (right, half))
            pen.qCurveTo((right, top), (mid, top))
            pen.qCurveTo((40, top), (40, half))
            pen.qCurveTo((40, 0), (mid, 0))
            pen.closePath()
            pen.moveTo((mid, s))
            pen.qCurveTo((40 + s, s), (40 + s, half), (mid, top - s))
            pen.qCurveTo((right - s, top - s), (right - s, half), (mid, s))
            pen.closePath()
        else:
            pen.moveTo((mid, 0))
            pen.curveTo((mid + 150, 0), (right, half - 150), (right, half))
            pen.curveTo((right, half + 150), (mid + 150, top), (mid, top))
            pen.curveTo((mid - 150, top), (40, half + 150), (40, half))
            pen.curveTo((40, half - 150), (mid - 150, 0), (mid, 0))
            pen.closePath()
            pen.moveTo((mid, s))
            pen.curveTo((mid - 100, s), (40 + s, half - 100), (40 + s, half))
            pen.curveTo((40 + s, half + 100), (mid - 100, top - s), (mid, top - s))
            pen.curveTo((mid + 100, top - s), (right - s, half + 100), (right - s, half))
            pen.curveTo((right - s, half - 100), (mid + 100, s), (mid, s))
            pen.closePath()
    elif name == "T":
        box(40, 700 - s, 560, 700)
        box(300 - s // 2, 0, 300 + s // 2, 700 - s)
    elif name == "Z":
        # Thirty teeth: one long line run, which CFF2 writes as a single operator.
        pen.moveTo((30, 0))
        x = 30
        for k in range(30):
            pen.lineTo((x + 10, 40 + s // 4 if k % 2 == 0 else 0))
            x += 10 + k % 3
        pen.lineTo((x, 700))
        pen.lineTo((30, 700))
        pen.closePath()
    elif name in ("dollar", "dollar.heavy"):
        box(100, 50, 400, 650)
        bar = 30 if name == "dollar" else 30 + s // 2
        box(250 - bar // 2, -60, 250 + bar // 2, 760)
    elif name == "acute":
        pen.moveTo((100, 750))
        pen.lineTo((200 + s // 4, 900))
        pen.lineTo((260 + s // 4, 900))
        pen.lineTo((160, 750))
        pen.closePath()
    elif name == "gravecomb":
        pen.moveTo((-200, 750))
        pen.lineTo((-260 - s // 4, 900))
        pen.lineTo((-200 - s // 4, 900))
        pen.lineTo((-140, 750))
        pen.closePath()


def advance(name, w):
    s = stem(w)
    return {".notdef": 550, "space": 250 + s // 4, "A": 600 + s // 3, "H": 550, "O": 640 + s // 2, "T": 600,
            "o": 480 + s // 2, "Z": 400, "dollar": 500, "dollar.heavy": 500 + s // 8, "acute": 360,
            "Aacute": 600 + s // 3, "gravecomb": 0}[name]


def features(w):
    s = stem(w)
    return f"""
languagesystem DFLT dflt;
languagesystem latn dflt;
@MARKS = [gravecomb];
table GDEF {{ GlyphClassDef [A H O T o Z dollar dollar.heavy Aacute], , @MARKS, ; }} GDEF;
feature kern {{
    pos T o {-40 - s // 2};
    pos A T {-20 - s // 4};
}} kern;
markClass gravecomb <anchor {-200} {700}> @TOP;
feature mark {{
    pos base [A O o] <anchor {300 + s // 3} {720 + s // 5}> mark @TOP;
}} mark;
"""


def master(w, cff):
    fb = FontBuilder(1000, isTTF=not cff)
    fb.setupGlyphOrder(GLYPHS)
    fb.setupCharacterMap(CMAP)
    if cff:
        charstrings = {}
        for name in GLYPHS:
            pen = T2CharStringPen(advance(name, w), None)
            if name == "Aacute":
                draw(pen, "A", w, False)
                shifted = RecordingPen()
                draw(shifted, "acute", w, False)
                offset = 180 + stem(w) // 3
                for op, args in shifted.value:
                    getattr(pen, op)(*[(x + offset, y) for x, y in args])
            else:
                draw(pen, name, w, False)
            charstrings[name] = pen.getCharString()
        fb.setupCFF("PdfNetStackTestVariable", {"FullName": "PdfNetStack Test Variable"}, charstrings,
                    {"BlueValues": [-10, 0, 700, 710 + stem(w) // 10], "StdVW": stem(w)})
    else:
        glyphs = {}
        for name in GLYPHS:
            pen = TTGlyphPen(glyphs)
            if name == "Aacute":
                pen.addComponent("A", (1, 0, 0, 1, 0, 0))
                pen.addComponent("acute", (1, 0, 0, 1, 180 + stem(w) // 3, 0))
            else:
                draw(pen, name, w, True)
            glyphs[name] = pen.glyph()
        fb.setupGlyf(glyphs)
    metrics = {}
    for name in GLYPHS:
        metrics[name] = (advance(name, w), 0)
    fb.setupHorizontalMetrics(metrics)
    ascender = 880 + w // 20
    fb.setupHorizontalHeader(ascent=ascender, descent=-250, lineGap=0)
    fb.setupNameTable({"familyName": "PdfNetStack Test Variable", "styleName": "Regular",
                       "psName": "PdfNetStackTestVariable-Regular"})
    fb.setupOS2(sTypoAscender=ascender, sTypoDescender=-250, sTypoLineGap=0, usWinAscent=950, usWinDescent=300,
                sxHeight=480 + w // 25, sCapHeight=700 + w // 50, yStrikeoutSize=40 + w // 20,
                yStrikeoutPosition=260 + w // 25, usWeightClass=w, fsType=0)
    fb.setupPost(underlinePosition=-100 - w // 30, underlineThickness=40 + w // 20)
    font = fb.font
    if not cff:
        # Control values that vary, for cvar; the same (empty) programs in every master.
        cvt = newTable("cvt ")
        cvt.values = array("h", [0, 700, stem(w), 480 + w // 25])
        font["cvt "] = cvt
        for tag in ("fpgm", "prep"):
            table = newTable(tag)
            table.program = ttProgram.Program()
            table.program.fromBytecode(b"")
            font[tag] = table
    addOpenTypeFeaturesFromString(font, features(w))
    font = reload(font)
    if cff:
        # Read the Private DICT now, while the table is still CFF: varLib converts it in place.
        font["CFF "].cff.topDictIndex[0].Private.BlueValues
    return font


def build(cff):
    doc = DesignSpaceDocument()
    axis = AxisDescriptor()
    axis.tag, axis.name, axis.minimum, axis.default, axis.maximum = "wght", "Weight", 100, 400, 900
    doc.addAxis(axis)
    for w in WEIGHTS:
        source = SourceDescriptor()
        source.font = master(w, cff)
        source.location = {"Weight": w}
        source.styleName = str(w)
        doc.addSource(source)
    for style, w in INSTANCES:
        instance = InstanceDescriptor()
        instance.styleName = style
        instance.location = {"Weight": w}
        doc.addInstance(instance)
    rule = RuleDescriptor()
    rule.name = "heavy dollar"
    rule.conditionSets.append([{"name": "Weight", "minimum": 600, "maximum": 900}])
    rule.subs.append(("dollar", "dollar.heavy"))
    doc.addRule(rule)
    font, _, _ = build_variable(doc)
    if cff:
        font = reload(font)
        add_subroutines(font)
    return font


def add_subroutines(font):
    """Moves the start of two glyphs into a local and a global subroutine, blends included."""
    cff = font["CFF2"].cff
    top = cff.topDictIndex[0]
    top.CharStrings["O"].decompile()
    top.CharStrings["T"].decompile()
    private = top.FDArray[0].Private
    from fontTools.cffLib import GlobalSubrsIndex, SubrsIndex
    if not hasattr(private, "Subrs") or private.Subrs is None:
        private.Subrs = SubrsIndex()
    for name, subrs, op in (("O", private.Subrs, "callsubr"), ("T", cff.GlobalSubrs, "callgsubr")):
        program = top.CharStrings[name].program
        # The first operator with its operands (a moveto, blended) goes into the subroutine.
        cut = next(i for i, v in enumerate(program) if v in ("rmoveto", "hmoveto", "vmoveto")) + 1
        subrs.append(T2CharString(program=program[:cut]))
        bias = 107
        top.CharStrings[name].program = [len(subrs) - 1 - bias, op] + program[cut:]
    assert any(isinstance(v, str) and v == "blend" for v in private.Subrs[0].program), "the local subroutine must blend"
    # The operands Z's longest operator has once blends are resolved.
    regions = top.VarStore.otVarStore.VarData[0].VarRegionCount
    z = top.CharStrings["Z"]
    z.decompile()
    stack, longest = [], 0
    for v in z.program:
        if not isinstance(v, str):
            stack.append(v)
        elif v == "blend":
            n = stack.pop()
            del stack[len(stack) - n * regions:]
        else:
            longest = max(longest, len(stack))
            stack = []
    assert longest > 48, "Z must have an operator with more than 48 operands"


def outlines(font):
    """Each glyph's outline as fontTools draws it, and its advance."""
    glyphset = font.getGlyphSet()
    result = {}
    for gid, name in enumerate(font.getGlyphOrder()):
        if "glyf" in font:
            coords, ends, flags = font["glyf"][name].getCoordinates(font["glyf"])
            shape = {"points": [[int(x), int(y), int(f & 1)] for (x, y), f in zip(coords, flags)], "ends": list(ends)}
        else:
            pen = RecordingPen()
            glyphset[name].draw(pen)
            letters = {"moveTo": "M", "lineTo": "L", "curveTo": "C", "closePath": "Z"}
            shape = {"path": " ".join(letters[op] + "".join(f" {number(x)} {number(y)}" for x, y in args) for op, args in pen.value)}
        result[gid] = {"advance": font["hmtx"][name][0], **shape}
    return result


def number(v):
    return f"{round(v, 2):g}"


def metrics(font):
    os2, hhea, post = font["OS/2"], font["hhea"], font["post"]
    return {"typoAscender": os2.sTypoAscender, "hheaAscender": hhea.ascent, "xHeight": os2.sxHeight,
            "capHeight": os2.sCapHeight, "strikeoutSize": os2.yStrikeoutSize, "strikeoutPosition": os2.yStrikeoutPosition,
            "underlinePosition": post.underlinePosition, "underlineThickness": post.underlineThickness,
            "weightClass": os2.usWeightClass}


def pin(font):
    """Fixed timestamps, so the fixtures come out the same on every run."""
    font.recalcTimestamp = False
    font["head"].created = TIMESTAMP
    font["head"].modified = TIMESTAMP
    return font


def reload(font):
    buffer = BytesIO()
    pin(font).save(buffer)
    buffer.seek(0)
    return TTFont(buffer)


def shaping(data, name):
    import uharfbuzz as hb

    lines = []
    for location in LOCATIONS:
        for direction, script, text in SAMPLES:
            face = hb.Face(hb.Blob(data))
            font = hb.Font(face)
            font.set_variations(location)
            buf = hb.Buffer()
            buf.add_codepoints([ord(c) for c in text])
            buf.direction = direction
            buf.script = script
            hb.shape(font, buf, {})
            glyphs = [[i.codepoint, i.cluster, p.x_advance, p.x_offset, p.y_offset] for i, p in zip(buf.glyph_infos, buf.glyph_positions)]
            label = name + "@" + ",".join(f"{k}={v}" for k, v in location.items())
            lines.append(json.dumps({"font": label, "text": text, "direction": direction, "script": script, "glyphs": glyphs}, ensure_ascii=False))
    return lines


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    references = []
    shaped = []
    for cff, name in ((False, "Test-Variable.ttf"), (True, "Test-Variable.otf")):
        font = build(cff)
        buffer = BytesIO()
        pin(font).save(buffer)
        data = buffer.getvalue()
        (OUT / name).write_bytes(data)
        print(f"{name}: {len(data)} bytes")
        for location in LOCATIONS:
            instance = instantiateVariableFont(TTFont(BytesIO(data)), location)
            instance = reload(instance)
            references.append({"font": name, "location": location, "metrics": metrics(instance), "glyphs": outlines(instance)})
        shaped += shaping(data, name)
    (OUT / "Test-Variable.instances.json").write_text(json.dumps(references, indent=None) + "\n", encoding="utf-8", newline="\n")
    (OUT / "Test-Variable.shaping.jsonl").write_text("\n".join(shaped) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
