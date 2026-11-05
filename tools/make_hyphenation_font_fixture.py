"""Writes the font the hyphenation layout tests use: synthetic, with an fi ligature and a hyphen.

    pip install fonttools
    python tools/make_hyphenation_font_fixture.py

tests/PdfNetStack.Tests/Fixtures/Hyphenation/Test-Ligature.ttf has the glyphs below and nothing
from any existing font. Its boxes only give the glyphs some ink. The other test fonts have the
ligature or the hyphen, not both, and their glyph ids are fixed by the tests that use them.
"""

from pathlib import Path

from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Hyphenation" / "Test-Ligature.ttf"

# name, code point, advance
GLYPHS = [
    (".notdef", None, 500),
    ("space", 0x20, 250),
    ("hyphen", 0x2D, 300),
    ("f", 0x66, 300),
    ("i", 0x69, 250),
    ("f_i", None, 520),
    ("o", 0x6F, 500),
    ("x", 0x78, 480),
]


def box(width):
    pen = TTGlyphPen(None)
    pen.moveTo((20, 0))
    pen.lineTo((20, 500))
    pen.lineTo((max(40, width - 20), 500))
    pen.lineTo((max(40, width - 20), 0))
    pen.closePath()
    return pen.glyph()


def main():
    builder = FontBuilder(1000, isTTF=True)
    builder.setupGlyphOrder([g[0] for g in GLYPHS])
    builder.setupCharacterMap({g[1]: g[0] for g in GLYPHS if g[1] is not None})
    builder.setupGlyf({g[0]: box(g[2]) for g in GLYPHS})
    builder.setupHorizontalMetrics({g[0]: (g[2], 20) for g in GLYPHS})
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({"familyName": "PdfNetStack Test Ligature", "styleName": "Regular"})
    builder.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    builder.setupPost()
    builder.setupHead(unitsPerEm=1000, created=0, modified=0)
    addOpenTypeFeaturesFromString(builder.font, "feature liga { sub f i by f_i; } liga;")
    OUT.parent.mkdir(parents=True, exist_ok=True)
    builder.save(OUT)


if __name__ == "__main__":
    main()
