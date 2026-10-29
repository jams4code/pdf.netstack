"""Generates src/PdfNetStack/Text/ShapingData.g.cs from the Unicode Character Database.

Usage: python tools/make_shaping_data.py <ucd directory> <output .cs file>

The directory holds these files of one Unicode version (https://www.unicode.org/Public/<v>/ucd/):
UnicodeData.txt, DerivedNormalizationProps.txt, IndicSyllabicCategory.txt,
IndicPositionalCategory.txt and Blocks.txt. The output has what the OpenType shaper needs beyond
UnicodeData.g.cs:

- general categories and canonical combining classes, as range tables;
- canonical decompositions, one step each (a character to one or two characters), and the
  canonical compositions that normalization form C allows (no Full_Composition_Exclusion);
- the Hebrew presentation forms that are excluded from composition, which the shaper composes for
  fonts without mark positioning;
- for the Indic shaper, each character's shaping category and position, from its Indic syllabic
  and positional categories as the Microsoft script development specifications use them.
"""

import re
import sys
from pathlib import Path

MAX = 0x110000

# System.Globalization.UnicodeCategory order.
CATEGORIES = ["Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc", "Cf",
              "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn"]

# Indic shaping categories (the order matches IndicCategory in IndicShaper.cs).
INDIC_CATEGORIES = ["X", "C", "V", "N", "H", "ZWNJ", "ZWJ", "M", "SM", "A", "Placeholder", "DottedCircle", "RS",
                    "Repha", "Ra", "CM", "Symbol", "CS"]

# Positions, in the order the syllable is sorted into (IndicPosition in IndicShaper.cs).
INDIC_POSITIONS = ["Start", "RaToBecomeReph", "PreM", "PreC", "BaseC", "AfterMain", "AboveC", "BeforeSub",
                   "BelowC", "AfterSub", "BeforePost", "PostC", "AfterPost", "Smvd", "End"]

SYLLABIC = {
    "Other": "X",
    "Avagraha": "Symbol",
    "Bindu": "SM",
    "Brahmi_Joining_Number": "Placeholder",
    "Cantillation_Mark": "A",
    "Consonant": "C",
    "Consonant_Dead": "C",
    "Consonant_Final": "CM",
    "Consonant_Head_Letter": "C",
    "Consonant_Initial_Postfixed": "C",
    "Consonant_Killer": "M",
    "Consonant_Medial": "CM",
    "Consonant_Placeholder": "Placeholder",
    "Consonant_Preceding_Repha": "Repha",
    "Consonant_Prefixed": "X",
    "Consonant_Subjoined": "CM",
    "Consonant_Succeeding_Repha": "CM",
    "Consonant_With_Stacker": "CS",
    "Gemination_Mark": "SM",
    "Invisible_Stacker": "H",
    "Joiner": "ZWJ",
    "Modifying_Letter": "X",
    "Non_Joiner": "ZWNJ",
    "Nukta": "N",
    "Number": "Placeholder",
    "Number_Joiner": "Placeholder",
    "Pure_Killer": "M",
    "Register_Shifter": "RS",
    "Reordering_Killer": "H",
    "Syllable_Modifier": "SM",
    "Tone_Letter": "X",
    "Tone_Mark": "N",
    "Virama": "H",
    "Visarga": "SM",
    "Vowel": "V",
    "Vowel_Dependent": "M",
    "Vowel_Independent": "V",
}

# A split vowel sign takes the position of its last part.
POSITIONAL = {
    "Not_Applicable": "End",
    "Left": "PreC",
    "Top": "AboveC",
    "Bottom": "BelowC",
    "Right": "PostC",
    "Bottom_And_Left": "BelowC",
    "Bottom_And_Right": "PostC",
    "Left_And_Right": "PostC",
    "Top_And_Bottom": "BelowC",
    "Top_And_Bottom_And_Left": "BelowC",
    "Top_And_Bottom_And_Right": "PostC",
    "Top_And_Left": "AboveC",
    "Top_And_Left_And_Right": "PostC",
    "Top_And_Right": "PostC",
    "Overstruck": "AfterMain",
    "Visual_Order_Left": "PreM",
}

# Blocks whose characters take part in Indic syllables: the nine scripts, the Vedic signs, and the
# digits, spaces, dashes and placeholders that carry marks in running text.
INDIC_BLOCKS = ["Basic Latin", "Latin-1 Supplement", "Devanagari", "Bengali", "Gurmukhi", "Gujarati", "Oriya",
                "Tamil", "Telugu", "Kannada", "Malayalam", "Vedic Extensions", "General Punctuation",
                "Superscripts and Subscripts", "Geometric Shapes", "Devanagari Extended"]

# Characters whose shaping category differs from their syllabic category.
CATEGORY_OVERRIDES = {
    # The Ra of each script, which can form a reph or a pre-base form.
    0x0930: "Ra", 0x09B0: "Ra", 0x09F0: "Ra", 0x0A30: "Ra", 0x0AB0: "Ra", 0x0B30: "Ra", 0x0BB0: "Ra",
    0x0C30: "Ra", 0x0CB0: "Ra", 0x0D30: "Ra",
    # Devanagari grave and acute accents behave like bindus.
    0x0953: "SM", 0x0954: "SM",
    # Gurmukhi iri and ura, and the Vedic jihvamuliya and upadhmaniya, are bases.
    0x0A72: "C", 0x0A73: "C", 0x1CF5: "C", 0x1CF6: "C",
    # Vedic tone marks after a visarga or nasalization sign.
    **{cp: "A" for cp in range(0x1CE2, 0x1CE9)}, 0x1CED: "A",
    # Signs that carry marks on their own, like the avagraha.
    **{cp: "Symbol" for cp in [*range(0xA8F2, 0xA8F8), *range(0x1CE9, 0x1CED), *range(0x1CEE, 0x1CF2)]},
    # Gurmukhi udaat is a vowel sign below.
    0x0A51: "M",
    # Grantha signs that Tamil text uses.
    0x11301: "SM", 0x11303: "SM", 0x1133B: "N", 0x1133C: "N",
    # Gujarati shadda and Oriya sign overline are nuktas.
    0x0AFB: "N", 0x0B55: "N",
    # Bengali anji, Bengali vedic anusvara and Kannada and Malayalam signs that start a syllable.
    0x0980: "Placeholder", 0x09FC: "Placeholder", 0x0C80: "Placeholder", 0x0D04: "Placeholder",
    0x25CC: "DottedCircle",
}

POSITION_OVERRIDES = {0x0A51: "BelowC"}

CONSONANTS = {"C", "CS", "Ra", "CM", "V", "Placeholder", "DottedCircle"}


def matra_position(cp, block, side):
    """Where a dependent vowel sign is sorted, per script and side of the consonant."""
    if side == "PreC":
        return "PreM"
    if side == "PostC":
        return {"Devanagari": "AfterSub", "Bengali": "AfterPost", "Gurmukhi": "AfterPost", "Gujarati": "AfterPost",
                "Oriya": "AfterPost", "Tamil": "AfterPost", "Malayalam": "AfterPost",
                "Telugu": "BeforeSub" if cp <= 0x0C42 else "AfterSub",
                "Kannada": "BeforeSub" if cp < 0x0CC3 or cp > 0x0CD6 else "AfterSub"}.get(block, "AfterSub")
    if side == "AboveC":
        return {"Gurmukhi": "AfterPost", "Oriya": "AfterMain", "Telugu": "BeforeSub",
                "Kannada": "BeforeSub"}.get(block, "AfterSub")
    if side == "BelowC":
        return {"Gurmukhi": "AfterPost", "Gujarati": "AfterPost", "Tamil": "AfterPost", "Malayalam": "AfterPost",
                "Telugu": "BeforeSub", "Kannada": "BeforeSub"}.get(block, "AfterSub")
    return side


def entries(path):
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.split("#")[0].strip()
        if not line:
            continue
        fields = [f.strip() for f in line.split(";")]
        cps = fields[0].split("..")
        yield int(cps[0], 16), int(cps[-1], 16), fields[1:]


def unicode_data(ucd):
    """General category, combining class and canonical decomposition of each code point."""
    gc = ["Cn"] * MAX
    ccc = [0] * MAX
    decomposition = {}
    first = None
    for line in (ucd / "UnicodeData.txt").read_text(encoding="utf-8").splitlines():
        f = line.split(";")
        cp = int(f[0], 16)
        if f[1].endswith(", First>"):
            first = cp
            continue
        start = first if f[1].endswith(", Last>") else cp
        first = None
        for c in range(start, cp + 1):
            gc[c] = f[2]
            ccc[c] = int(f[3])
        if f[5] and not f[5].startswith("<"):
            decomposition[cp] = [int(x, 16) for x in f[5].split()]
    return gc, ccc, decomposition


def exclusions(ucd):
    excluded = set()
    for first, last, fields in entries(ucd / "DerivedNormalizationProps.txt"):
        if fields[0] == "Full_Composition_Exclusion":
            excluded.update(range(first, last + 1))
    return excluded


def blocks(ucd):
    table = {}
    for first, last, fields in entries(ucd / "Blocks.txt"):
        for cp in range(first, last + 1):
            table[cp] = fields[0]
    return table


def indic(ucd):
    """Shaping category and position of the characters the Indic shaper sees, as category | position << 8."""
    block = blocks(ucd)
    syllabic = {}
    for first, last, fields in entries(ucd / "IndicSyllabicCategory.txt"):
        for cp in range(first, last + 1):
            syllabic[cp] = fields[0]
    positional = {}
    for first, last, fields in entries(ucd / "IndicPositionalCategory.txt"):
        for cp in range(first, last + 1):
            positional[cp] = fields[0]

    table = [0] * MAX
    end = INDIC_POSITIONS.index("End")
    for cp in range(MAX):
        if block.get(cp) not in INDIC_BLOCKS and cp not in CATEGORY_OVERRIDES:
            table[cp] = end << 8
            continue
        category = CATEGORY_OVERRIDES.get(cp, SYLLABIC[syllabic.get(cp, "Other")])
        side = POSITION_OVERRIDES.get(cp, POSITIONAL[positional.get(cp, "Not_Applicable")])
        if category in CONSONANTS:
            position = "BaseC"
        elif category == "M":
            position = matra_position(cp, block.get(cp), side)
        elif category in ("SM", "A", "Symbol"):
            position = "Smvd"
        else:
            position = side
        # Oriya candrabindu sorts before subjoined consonants.
        if cp == 0x0B01:
            position = "BeforeSub"
        table[cp] = INDIC_CATEGORIES.index(category) | INDIC_POSITIONS.index(position) << 8
    return table


def ranges(table):
    starts, values = [], []
    for cp, v in enumerate(table):
        if not values or v != values[-1]:
            starts.append(cp)
            values.append(v)
    return starts, values


def numbers(values, per_line=20, fmt="{}"):
    return "\n".join(
        "        " + ", ".join(fmt.format(v) for v in values[i:i + per_line]) + ","
        for i in range(0, len(values), per_line)
    )


def table(o, name, data, kind, summary):
    starts, values = ranges(data)
    o.append(f"    /// <summary>{summary}: the first code point of each range.</summary>")
    o.append(f"    internal static readonly int[] {name}Starts =")
    o.append("    [")
    o.append(numbers(starts, 12, "0x{:X}"))
    o.append("    ];")
    o.append("")
    o.append(f"    internal static readonly {kind}[] {name}Values =")
    o.append("    [")
    o.append(numbers(values, 24 if kind == "byte" else 16, "{}" if kind == "byte" else "0x{:X}"))
    o.append("    ];")
    o.append("")


def flat(o, name, rows, per_line, summary):
    o.append(f"    /// <summary>{summary}</summary>")
    o.append(f"    internal static readonly int[] {name} =")
    o.append("    [")
    o.append(numbers([v for row in rows for v in row], per_line, "0x{:X}"))
    o.append("    ];")
    o.append("")


def main(ucd_dir, out):
    ucd = Path(ucd_dir)
    version = re.search(r"-(\d+\.\d+\.\d+)\.txt", (ucd / "IndicSyllabicCategory.txt").read_text(encoding="utf-8").splitlines()[0]).group(1)
    gc, ccc, decomposition = unicode_data(ucd)
    excluded = exclusions(ucd)

    decompositions = sorted((cp, d[0], d[1] if len(d) > 1 else 0) for cp, d in decomposition.items())
    compositions = sorted((d[0], d[1], cp) for cp, d in decomposition.items() if len(d) == 2 and cp not in excluded)
    hebrew = sorted((d[0], d[1], cp) for cp, d in decomposition.items()
                    if 0xFB1D <= cp <= 0xFB4F and len(d) == 2 and cp in excluded)

    o = ["// <auto-generated>",
         f"// Generated by tools/make_shaping_data.py from the Unicode Character Database {version}. Do not edit.",
         "// Copyright (c) 1991-2024 Unicode, Inc. Distributed under the Unicode License v3; see THIRD-PARTY-NOTICES.md.",
         "// </auto-generated>",
         "",
         "namespace PdfNetStack.Text;",
         "",
         "internal static partial class ShapingData",
         "{",
         f'    internal const string Version = "{version}";',
         ""]
    table(o, "Category", [CATEGORIES.index(c) for c in gc], "byte", "General category, in System.Globalization.UnicodeCategory order")
    table(o, "CombiningClass", ccc, "byte", "Canonical combining class")
    flat(o, "Decompositions", decompositions, 15,
         "Canonical decompositions, one step: code point, first part, second part or 0. Sorted.")
    flat(o, "Compositions", compositions, 15,
         "Canonical compositions allowed in normalization form C: first, second, composite. Sorted.")
    flat(o, "HebrewCompositions", hebrew, 15,
         "Hebrew presentation forms excluded from composition: first, second, composite. Sorted.")
    table(o, "Indic", indic(ucd), "ushort", "Indic shaping category | position &lt;&lt; 8")
    o.append("}")
    Path(out).write_text("\n".join(o) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
