"""Regenerates the CFF encoding test fixtures.

The fonts are synthetic: every glyph is an empty charstring, since the tests read only the
encodings and charsets (Adobe Technical Note 5176, sections 12 and 13). The bare CFF programs are
assembled byte by byte, so each table takes exactly the format a test names; fontTools reads every
one back as a check. The OpenType one is built with fontTools.

    pip install fonttools
    python tools/make_cff_fixtures.py

Fixtures (tests/PdfNetStack.Tests/Fixtures/Fonts), each a FontFile3 program for a simple font:
    Cff-Ranges.cff      charset format 2 with standard and custom strings, encoding format 1 (code
                        ranges) with supplements: A B C at 41 to 43, uni4E2D and Euro at 80 and 81,
                        fi at 30; supplements give A to 61 and Euro to 90
    Cff-Codes.cff       charset format 0, encoding format 0: H at 01, e at 02, uni03B3 at F0
    Cff-Expert.cff      the predefined Expert charset (its first 60 glyphs) and Expert encoding
    Cff-Subset.cff      the predefined ExpertSubset charset (its first 12 glyphs) and encoding
                        format 0 giving its glyphs 1 to 12 the codes 20 to 2B
    Cff-Standard.cff    the predefined ISOAdobe charset (its first 100 glyphs), no Encoding
                        operator: the Standard encoding
    Cff-Encoded.otf     OpenType with a CFF table whose custom encoding puts A, V and uni4E2D at
                        codes 01 to 03
"""

import struct
from io import BytesIO
from pathlib import Path

from fontTools.cffLib import CFFFontSet, cffExpertSubsetStrings, cffIExpertStrings, cffStandardStrings
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Fonts"
OP_CHARSET, OP_ENCODING, OP_CHARSTRINGS, OP_PRIVATE = 15, 16, 17, 18


def index(items):
    """A CFF INDEX of byte strings."""
    if not items:
        return b"\x00\x00"
    offsets, at = [1], 1
    for item in items:
        at += len(item)
        offsets.append(at)
    size = 1 if at < 0x100 else 2 if at < 0x10000 else 3
    out = struct.pack(">HB", len(items), size)
    for o in offsets:
        out += o.to_bytes(size, "big")
    return out + b"".join(items)


def integer(v):
    """A DICT integer, always 5 bytes, so offsets do not change the size of the Top DICT."""
    return b"\x1d" + struct.pack(">i", v)


def sid(name, strings):
    """The string id of a name: a standard string, or one appended to the font's own strings."""
    if name in cffStandardStrings:
        return cffStandardStrings.index(name)
    if name not in strings:
        strings.append(name)
    return len(cffStandardStrings) + strings.index(name)


def assemble(glyphs, charset, encoding, strings):
    """
    A one-font CFF. charset is 0, 1 or 2 (predefined) or the bytes of a custom one; encoding is
    None (no operator), 0 or 1 (predefined) or the bytes of a custom one.
    """
    names = index([b"Test"])
    string_index = index([s.encode("ascii") for s in strings])
    charstrings = index([b"\x0e"] * len(glyphs))
    private = bytes([139, 20])  # defaultWidthX 0

    def top(charset_at, encoding_at, charstrings_at, private_at):
        d = integer(charset_at) + bytes([OP_CHARSET])
        if encoding_at is not None:
            d += integer(encoding_at) + bytes([OP_ENCODING])
        d += integer(charstrings_at) + bytes([OP_CHARSTRINGS])
        d += integer(len(private)) + integer(private_at) + bytes([OP_PRIVATE])
        return index([d])

    start = 4 + len(names) + len(top(0, 0 if encoding is not None else None, 0, 0)) + len(string_index) + 2
    at = start
    charset_at = charset if isinstance(charset, int) else at
    at += 0 if isinstance(charset, int) else len(charset)
    encoding_at = encoding if encoding is None or isinstance(encoding, int) else at
    at += 0 if encoding is None or isinstance(encoding, int) else len(encoding)
    charstrings_at = at
    at += len(charstrings)
    private_at = at
    out = bytes([1, 0, 4, 4]) + names + top(charset_at, encoding_at, charstrings_at, private_at) + string_index + b"\x00\x00"
    if not isinstance(charset, int):
        out += charset
    if encoding is not None and not isinstance(encoding, int):
        out += encoding
    out += charstrings + private
    if len(out) != at + len(private):
        raise ValueError("layout")
    return out


def check(data, expected_glyphs, expected_encoding):
    """Reads the program back with fontTools: glyph order and code to name."""
    cff = CFFFontSet()
    cff.decompile(BytesIO(data), None)
    top = cff[cff.fontNames[0]]
    if top.charset != expected_glyphs:
        raise ValueError(f"charset {top.charset[:20]}")
    encoding = top.Encoding
    if isinstance(encoding, str):
        if encoding != expected_encoding:
            raise ValueError(encoding)
        return
    got = {code: name for code, name in enumerate(encoding) if name != ".notdef"}
    if got != expected_encoding:
        raise ValueError(f"encoding {got}")


def ranges_font():
    glyphs = [".notdef", "A", "B", "C", "uni4E2D", "Euro", "fi"]
    strings = []
    sids = [sid(g, strings) for g in glyphs[1:]]
    # Format 2: A B C in one range, then the two custom strings, then fi.
    charset = bytes([2]) + struct.pack(">HH", sids[0], 2) + struct.pack(">HH", sids[3], 1) + struct.pack(">HH", sids[5], 0)
    if sids[3] + 1 != sids[4]:
        raise ValueError("custom strings are not consecutive")
    # Format 1 with supplements: ranges 41-43, 80-81, 30; supplements 61 to A, 90 to Euro.
    encoding = bytes([0x81, 3, 0x41, 2, 0x80, 1, 0x30, 0, 2, 0x61]) + struct.pack(">H", sids[0]) + bytes([0x90]) + struct.pack(">H", sids[4])
    data = assemble(glyphs, charset, encoding, strings)
    check(data, glyphs, {0x41: "A", 0x42: "B", 0x43: "C", 0x80: "uni4E2D", 0x81: "Euro", 0x30: "fi", 0x61: "A", 0x90: "Euro"})
    return data


def codes_font():
    glyphs = [".notdef", "H", "e", "uni03B3"]
    strings = []
    charset = bytes([0]) + b"".join(struct.pack(">H", sid(g, strings)) for g in glyphs[1:])
    encoding = bytes([0, 3, 0x01, 0x02, 0xF0])
    data = assemble(glyphs, charset, encoding, strings)
    check(data, glyphs, {0x01: "H", 0x02: "e", 0xF0: "uni03B3"})
    return data


def expert_font():
    glyphs = cffIExpertStrings[:60]
    data = assemble(glyphs, 1, 1, [])
    check(data, glyphs, "ExpertEncoding")
    return data


def subset_font():
    glyphs = cffExpertSubsetStrings[:13]
    encoding = bytes([0, 12]) + bytes(range(0x20, 0x2C))
    data = assemble(glyphs, 2, encoding, [])
    check(data, glyphs, {0x20 + i: glyphs[1 + i] for i in range(12)})
    return data


def standard_font():
    glyphs = cffStandardStrings[:100]
    data = assemble(glyphs, 0, None, [])
    check(data, glyphs, "StandardEncoding")
    return data


def encoded_otf():
    names = [".notdef", "A", "V", "uni4E2D"]
    fb = FontBuilder(1000, isTTF=False)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({0x41: "A", 0x56: "V", 0x4E2D: "uni4E2D"})
    charstrings = {}
    for name in names:
        pen = T2CharStringPen(600, None)
        pen.moveTo((50, 0))
        pen.lineTo((50, 700))
        pen.lineTo((550, 700))
        pen.closePath()
        charstrings[name] = pen.getCharString()
    fb.setupCFF("PdfNetStackTest-Encoded", {"FullName": "PdfNetStack Test Encoded"}, charstrings, {})
    fb.setupHorizontalMetrics({name: (600, 50) for name in names})
    fb.setupHorizontalHeader(ascent=800, descent=-200)
    fb.setupNameTable({"familyName": "PdfNetStack Test", "styleName": "Encoded"})
    fb.setupOS2()
    fb.setupPost()
    encoding = [".notdef"] * 256
    encoding[1], encoding[2], encoding[3] = "A", "V", "uni4E2D"
    fb.font["CFF "].cff.topDictIndex[0].Encoding = encoding
    out = BytesIO()
    fb.font.save(out)
    return out.getvalue()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, data in [("Cff-Ranges.cff", ranges_font()), ("Cff-Codes.cff", codes_font()), ("Cff-Expert.cff", expert_font()),
                       ("Cff-Subset.cff", subset_font()), ("Cff-Standard.cff", standard_font()), ("Cff-Encoded.otf", encoded_otf())]:
        (OUT / name).write_bytes(data)
        print(name, len(data))


if __name__ == "__main__":
    main()
