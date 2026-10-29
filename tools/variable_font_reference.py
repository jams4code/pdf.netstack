"""Records fontTools' instances of local variable fonts, for comparison with PdfNetStack's instancer.

    pip install fonttools
    python tools/variable_font_reference.py out.json font1.ttf [font2.otf font3.ttc#1 ...]
    set PDFNETSTACK_VF_REFERENCE=out.json and run the tests (VariableFontTests)

Dev only: fonts and the output stay on the machine. A font in a collection is named with its
index after '#'. For each font the default location and every named instance are recorded: the
outlines of the first 400 glyphs, their advances and the font metrics, in the format of
tests/PdfNetStack.Tests/Fixtures/Fonts/Test-Variable.instances.json.
"""

import json
import sys
from io import BytesIO

from fontTools.ttLib import TTFont

from make_variable_font_fixtures import instantiateVariableFont, metrics, outlines

GLYPHS = 400


def main(out, names):
    references = []
    for name in names:
        path, _, index = name.partition("#")
        font = TTFont(path, fontNumber=int(index or 0))
        if "fvar" not in font:
            print(f"{name}: not a variable font")
            continue
        fvar = font["fvar"]
        locations = [{a.axisTag: a.defaultValue for a in fvar.axes}]
        locations += [dict(i.coordinates) for i in fvar.instances]
        for location in locations:
            instance = instantiateVariableFont(TTFont(path, fontNumber=int(index or 0)), location)
            buffer = BytesIO()
            instance.save(buffer)
            buffer.seek(0)
            instance = TTFont(buffer)
            glyphs = {gid: g for gid, g in outlines(instance).items() if gid < GLYPHS}
            references.append({"font": name, "location": location, "metrics": metrics(instance), "glyphs": glyphs})
        print(f"{name}: {len(locations)} locations")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(references, f)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
