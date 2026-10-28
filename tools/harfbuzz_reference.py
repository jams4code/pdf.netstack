"""Records HarfBuzz shaping of sample texts in local fonts, for comparison with PdfNetStack's shaper.

    pip install uharfbuzz
    python tools/harfbuzz_reference.py out.jsonl font1.ttf [font2.ttf ...]
    set PDFNETSTACK_HB_REFERENCE=out.jsonl and run the tests (HarfBuzzComparisonTests)

Dev only: fonts and HarfBuzz output stay on the machine. Each line holds the font path, the text,
direction, script and HarfBuzz's glyphs in logical order: glyph id, cluster (code point index),
x advance, x offset, y offset, in font units.
"""

import json
import sys

import uharfbuzz as hb

SAMPLES = [
    ("ltr", "latn", "office efficient fjord flight"),
    ("ltr", "latn", "AVAWAY To Ta Te Vo Yo LT. P, F."),
    ("ltr", "latn", "Zürich naïve café — “quoted” résumé"),
    ("ltr", "latn", "Ελληνικά Кириллица ŁÓDŹ Ǆ"),
    ("rtl", "arab", "مرحبا بالعالم"),
    ("rtl", "arab", "السلام عليكم"),
    ("rtl", "arab", "لا إله إلا الله"),
    ("rtl", "arab", "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ"),
    ("rtl", "arab", "پنجره گچ ژاله"),
    ("rtl", "arab", "ك‌ل‍م"),
    ("rtl", "arab", "١٢٣ (نص) [قوس]"),
]

# Not compared yet: pointed Hebrew, where HarfBuzz recomposes a letter and its points into a
# presentation form (U+FB1D to U+FB4F) for fonts without Hebrew mark positioning. That needs
# Unicode normalization in the shaper, which is on the roadmap.


def shape(blob, text, direction, script):
    face = hb.Face(blob)
    font = hb.Font(face)
    buf = hb.Buffer()
    buf.add_codepoints([ord(c) for c in text])
    buf.direction = direction
    buf.script = script
    hb.shape(font, buf, {})
    infos, positions = buf.glyph_infos, buf.glyph_positions
    glyphs = [[i.codepoint, i.cluster, p.x_advance, p.x_offset, p.y_offset] for i, p in zip(infos, positions)]
    if direction == "rtl":
        glyphs.reverse()
    return glyphs


def main(out, fonts):
    with open(out, "w", encoding="utf-8") as f:
        for path in fonts:
            blob = hb.Blob.from_file_path(path)
            cmap = hb.Font(hb.Face(blob)).face.unicodes
            for direction, script, text in SAMPLES:
                if any(ord(c) not in cmap and ord(c) not in (0x200C, 0x200D) for c in text):
                    continue
                record = {"font": path, "text": text, "direction": direction, "script": script,
                          "glyphs": shape(blob, text, direction, script)}
                f.write(json.dumps(record, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
