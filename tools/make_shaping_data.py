"""Generates src/PdfNetStack/Text/ShapingData.g.cs from the Unicode Character Database.

Usage: python tools/make_shaping_data.py <ucd directory> <output .cs file>

The directory holds these files of one Unicode version (https://www.unicode.org/Public/<v>/ucd/):
UnicodeData.txt, DerivedNormalizationProps.txt, IndicSyllabicCategory.txt,
IndicPositionalCategory.txt, Blocks.txt, Scripts.txt, ArabicShaping.txt and
DerivedCoreProperties.txt; and Microsoft's data for the Universal Shaping Engine,
IndicSyllabicCategory-Additional.txt, IndicPositionalCategory-Additional.txt and
IndicShapingInvalidCluster.txt (MIT license, published with HarfBuzz at
https://github.com/harfbuzz/harfbuzz/tree/main/src/ms-use). The output
has what the OpenType shaper needs beyond UnicodeData.g.cs:

- general categories and canonical combining classes, as range tables;
- canonical decompositions, one step each (a character to one or two characters), and the
  canonical compositions that normalization form C allows (no Full_Composition_Exclusion);
- the Hebrew presentation forms that are excluded from composition, which the shaper composes for
  fonts without mark positioning;
- for the Indic, Khmer and Myanmar shapers, each character's shaping category and position, from
  its Indic syllabic and positional categories as the Microsoft script development specifications
  use them;
- for the Universal Shaping Engine, each character's USE category, derived from the same
  properties, the joining type and default ignorability with the rules of Microsoft's "Creating
  and supporting OpenType fonts for the Universal Shaping Engine";
- the vowel sequences that look like another vowel and are not valid (a letter a with the sign
  aa looks like the letter aa), before whose last character the Indic and USE shapers put a
  dotted circle.
"""

import re
import sys
from pathlib import Path

MAX = 0x110000

# System.Globalization.UnicodeCategory order.
CATEGORIES = ["Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc", "Cf",
              "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn"]

# Indic shaping categories (the order matches IndicCategory in IndicShaper.cs). After CS come the
# Khmer and Myanmar ones: vowel signs by side, Khmer robat, sign groups X and Y, Myanmar asat,
# medials ha, ra, wa, ya and la, pwo tones and variation selectors.
INDIC_CATEGORIES = ["X", "C", "V", "N", "H", "ZWNJ", "ZWJ", "M", "SM", "A", "Placeholder", "DottedCircle", "RS",
                    "Repha", "Ra", "CM", "Symbol", "CS",
                    "VAbv", "VBlw", "VPre", "VPst", "Robatic", "Xgroup", "Ygroup",
                    "As", "MH", "MR", "MW", "MY", "ML", "PT", "VS"]

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

# Blocks of the Khmer and Myanmar shapers, whose vowel signs are categorized by side.
SOUTHEAST_ASIAN_BLOCKS = ["Khmer", "Khmer Symbols", "Myanmar", "Myanmar Extended-A", "Myanmar Extended-B",
                          "Myanmar Extended-C"]

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
    # Generic bases of the Myanmar specification, and variation selectors, which only its grammar uses.
    0x2015: "Placeholder", 0x2022: "Placeholder", **{cp: "Placeholder" for cp in range(0x25FB, 0x25FF)},
    **{cp: "VS" for cp in range(0xFE00, 0xFE10)},
    # Khmer: ro, the robat and register shifters, and the two groups of signs after the vowels.
    0x179A: "Ra", 0x17C9: "Robatic", 0x17CA: "Robatic", 0x17CC: "Robatic",
    **{cp: "Xgroup" for cp in [0x17C6, 0x17CB, 0x17CD, 0x17CE, 0x17CF, 0x17D0, 0x17D1]},
    **{cp: "Ygroup" for cp in [0x17C7, 0x17C8, 0x17D3, 0x17DD]},
    0x17D9: "Placeholder",
    # Myanmar: the specification's categories where they differ from the syllabic ones. Nga, ra and
    # Mon nga form kinzi.
    0x104E: "C", 0x1004: "Ra", 0x101B: "Ra", 0x105A: "Ra", 0x1032: "A", 0x1036: "A", 0x103A: "As",
    0x103E: "MH", 0x1060: "ML", 0x103C: "MR", 0x103D: "MW", 0x1082: "MW", 0x103B: "MY", 0x105E: "MY", 0x105F: "MY",
    **{cp: "PT" for cp in [0x1063, 0x1064, 0x1069, 0x106A, 0x106B, 0x106C, 0x106D, 0xAA7B]},
    **{cp: "SM" for cp in [0x1038, 0x1087, 0x1088, 0x1089, 0x108A, 0x108B, 0x108C, 0x108D, 0x108F, 0x109A, 0x109B, 0x109C]},
    0x104A: "Placeholder",
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
        southeast = block.get(cp) in SOUTHEAST_ASIAN_BLOCKS
        if block.get(cp) not in INDIC_BLOCKS and not southeast and cp not in CATEGORY_OVERRIDES:
            table[cp] = end << 8
            continue
        category = CATEGORY_OVERRIDES.get(cp, SYLLABIC[syllabic.get(cp, "Other")])
        side = POSITION_OVERRIDES.get(cp, POSITIONAL[positional.get(cp, "Not_Applicable")])
        if category == "M" and southeast:
            # Khmer and Myanmar vowel signs are categorized by the side of the consonant they go on.
            category = {"PreC": "VPre", "AboveC": "VAbv", "BelowC": "VBlw", "PostC": "VPst"}[side]
            position = "End"
        elif category in CONSONANTS:
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


# USE categories (the order matches UseCategory in UseShaper.cs). Positional variants carry their
# side: Abv above, Blw below, Pst after, Pre before the base.
USE_CATEGORIES = ["O", "B", "N", "GB", "CGJ", "SUB", "H", "HN", "ZWNJ", "WJ", "R", "CS", "IS", "Sk", "G", "J", "SB", "SE",
                  "HVM", "HM", "HR", "RK", "FAbv", "FBlw", "FPst", "MAbv", "MBlw", "MPst", "MPre", "CMAbv", "CMBlw",
                  "VAbv", "VBlw", "VPst", "VPre", "VMAbv", "VMBlw", "VMPst", "VMPre", "SMAbv", "SMBlw",
                  "FMAbv", "FMBlw", "FMPst"]

# Which Indic positional categories give each side, for the USE categories that have sides.
USE_SIDES = {
    "F": {"Abv": ["Top"], "Blw": ["Bottom"], "Pst": ["Right"]},
    "M": {"Abv": ["Top"], "Blw": ["Bottom", "Bottom_And_Left", "Bottom_And_Right"], "Pst": ["Right"],
          "Pre": ["Left", "Top_And_Bottom_And_Left"]},
    "CM": {"Abv": ["Top"], "Blw": ["Bottom", "Overstruck"]},
    "V": {"Abv": ["Top", "Top_And_Bottom", "Top_And_Bottom_And_Right", "Top_And_Right"],
          "Blw": ["Bottom", "Overstruck", "Bottom_And_Right"], "Pst": ["Right"],
          "Pre": ["Left", "Top_And_Left", "Top_And_Left_And_Right", "Left_And_Right"]},
    "VM": {"Abv": ["Top"], "Blw": ["Bottom", "Overstruck"], "Pst": ["Right"], "Pre": ["Left"]},
    "SM": {"Abv": ["Top"], "Blw": ["Bottom"]},
    "FM": {"Abv": ["Top"], "Blw": ["Bottom"], "Pst": ["Not_Applicable"]},
}

# Scripts with shapers of their own; the USE table leaves their characters as other.
USE_EXCLUDED_SCRIPTS = {"Arabic", "Lao", "Samaritan", "Syriac", "Thai"}

# Hangul fillers are default ignorable but drawn.
HANGUL_FILLERS = {0x115F, 0x1160, 0x3164, 0xFFA0, 0x1BCA0, 0x1BCA1, 0x1BCA2, 0x1BCA3}


def use_category(cp, isc, joining, ignorable, gc):
    """The USE category of a character, from its Indic syllabic category, joining type, default
    ignorability and general category, as the specification's category table derives it."""
    lo = gc == "Lo"
    if isc in ("Number", "Consonant", "Consonant_Head_Letter", "Tone_Letter", "Vowel_Independent") \
            or (joining in "CDLR" and isc != "Joiner") \
            or (lo and isc in ("Avagraha", "Bindu", "Consonant_Final", "Consonant_Medial", "Consonant_Subjoined",
                               "Vowel", "Vowel_Dependent")):
        return "B"
    if isc == "Brahmi_Joining_Number":
        return "N"
    if isc == "Consonant_Placeholder" or cp in (0x2015, 0x2022, 0x25FB, 0x25FC, 0x25FD, 0x25FE):
        return "GB"
    if isc == "Joiner" or (ignorable and gc in ("Mc", "Me", "Mn")):
        return "CGJ"
    if (isc == "Consonant_Final" and not lo) or isc == "Consonant_Succeeding_Repha":
        return "F"
    if isc == "Syllable_Modifier":
        return "FM"
    if (isc == "Consonant_Medial" and not lo) or isc == "Consonant_Initial_Postfixed":
        return "M"
    if isc in ("Nukta", "Gemination_Mark", "Consonant_Killer"):
        return "CM"
    if isc == "Consonant_Subjoined" and not lo:
        return "SUB"
    if isc == "Consonant_With_Stacker":
        return "CS"
    if isc == "Virama":
        return "HVM" if cp == 0x0DCA else "H"
    if isc == "Number_Joiner":
        return "HN"
    if isc == "Invisible_Stacker":
        return "Sk" if cp == 0x1A60 else "IS"
    if isc == "Reordering_Killer":
        return "RK"
    hieroglyph = {"Hieroglyph": "G", "Hieroglyph_Joiner": "J", "Hieroglyph_Mirror": "HR", "Hieroglyph_Modifier": "HM",
                  "Hieroglyph_Mark_Begin": "SB", "Hieroglyph_Segment_Begin": "SB",
                  "Hieroglyph_Mark_End": "SE", "Hieroglyph_Segment_End": "SE"}
    if isc in hieroglyph:
        return hieroglyph[isc]
    if isc == "Non_Joiner":
        return "ZWNJ"
    if isc in ("Consonant_Preceding_Repha", "Consonant_Prefixed"):
        return "R"
    if isc == "Symbol_Modifier":
        return "SM"
    if isc == "Pure_Killer" or (not lo and isc in ("Vowel", "Vowel_Dependent")):
        return "V"
    if isc in ("Tone_Mark", "Cantillation_Mark", "Register_Shifter", "Visarga") or (not lo and isc == "Bindu"):
        return "VM"
    if (ignorable and cp not in HANGUL_FILLERS and isc == "Other") or gc == "Cn":
        return "WJ"
    if gc == "Po" or isc in ("Consonant_Dead", "Joiner", "Modifying_Letter", "Other"):
        return "O"
    raise ValueError(f"no USE category for U+{cp:04X} ({isc}, {gc})")


def use(ucd, gc):
    """USE category of every code point. Characters with no Indic property, joining type or
    default ignorability are other, as are those of scripts with shapers of their own."""
    script = {}
    for first, last, fields in entries(ucd / "Scripts.txt"):
        for cp in range(first, last + 1):
            script[cp] = fields[0]
    isc, ipc, joining, ignorable = {}, {}, {}, set()
    for name, values in (("IndicSyllabicCategory.txt", isc), ("IndicPositionalCategory.txt", ipc),
                         ("IndicSyllabicCategory-Additional.txt", isc), ("IndicPositionalCategory-Additional.txt", ipc)):
        for first, last, fields in entries(ucd / name):
            for cp in range(first, last + 1):
                if gc[cp] == "Cn":
                    continue  # the overrides may already cover a later Unicode version
                value = {"Consonant_Final_Modifier": "Syllable_Modifier", "NA": "Not_Applicable"}.get(fields[0], fields[0])
                values[cp] = value
    for first, last, fields in entries(ucd / "ArabicShaping.txt"):
        for cp in range(first, last + 1):
            joining[cp] = fields[1]
    for first, last, fields in entries(ucd / "DerivedCoreProperties.txt"):
        if fields[0] == "Default_Ignorable_Code_Point":
            ignorable.update(range(first, last + 1))

    table = [0] * MAX
    for cp in set(isc) | set(ipc) | set(joining) | ignorable:
        if script.get(cp, "Unknown") in USE_EXCLUDED_SCRIPTS:
            continue
        side = ipc.get(cp, "Not_Applicable")
        syllabic = isc.get(cp, "Other")
        # Vedic tone marks and Tibetan signs the Unicode data leaves without a syllabic category.
        if 0x1CE2 <= cp <= 0x1CE8:
            syllabic = "Cantillation_Mark"
        if 0x0F18 <= cp <= 0x0F19 or 0x0F3E <= cp <= 0x0F3F:
            syllabic = "Vowel_Dependent"
        if cp == 0x1CED:
            syllabic = "Tone_Mark"
        # Zanabazar Square's cluster-initial ra stacks on the consonant after it, in front of it: a
        # consonant with stacker that starts a cluster, not a repha that moves (as HarfBuzz has it).
        if cp == 0x11A3A:
            syllabic = "Consonant_With_Stacker"
        category = use_category(cp, syllabic, joining.get(cp, "X"), cp in ignorable, gc[cp])
        if cp in (0x0953, 0x0954):
            side = "Not_Applicable"
        if 0xA926 <= cp <= 0xA92A or cp in (0x11302, 0x11303, 0x114C1):
            side = "Top"
        if category in USE_SIDES:
            sides = [k for k, v in USE_SIDES[category].items() if side in v]
            if len(sides) != 1:
                raise ValueError(f"no side for U+{cp:04X} ({category}, {side})")
            category += sides[0]
        table[cp] = USE_CATEGORIES.index(category)
    return table


def vowel_constraints(ucd):
    """Invalid sequences, each as its length then its code points. A sequence that starts with a
    shorter one is left out: the shorter one already breaks it."""
    sequences = []
    for line in (ucd / "IndicShapingInvalidCluster.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#")[0].split(";")[0].strip()
        if line:
            sequences.append(tuple(int(cp, 16) for cp in line.split()))
    kept = [q for q in sorted(set(sequences)) if not any(len(p) < len(q) and q[:len(p)] == p for p in sequences)]
    return [[len(q), *q] for q in kept]


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
    updates = re.findall(r"^# (Updated for Unicode [\d.]+)", (ucd / "IndicSyllabicCategory-Additional.txt").read_text(encoding="utf-8"), re.M)
    microsoft = updates[-1] if updates else "initial version"

    decompositions = sorted((cp, d[0], d[1] if len(d) > 1 else 0) for cp, d in decomposition.items())
    compositions = sorted((d[0], d[1], cp) for cp, d in decomposition.items() if len(d) == 2 and cp not in excluded)
    hebrew = sorted((d[0], d[1], cp) for cp, d in decomposition.items()
                    if 0xFB1D <= cp <= 0xFB4F and len(d) == 2 and cp in excluded)

    o = ["// <auto-generated>",
         f"// Generated by tools/make_shaping_data.py from the Unicode Character Database {version}. Do not edit.",
         "// Copyright (c) 1991-2024 Unicode, Inc. Distributed under the Unicode License v3; see THIRD-PARTY-NOTICES.md.",
         f"// USE overrides: Copyright (c) Microsoft Corporation, MIT license ({microsoft}); see THIRD-PARTY-NOTICES.md.",
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
    table(o, "Use", use(ucd, gc), "byte", "Universal Shaping Engine category")
    flat(o, "VowelConstraints", vowel_constraints(ucd), 15,
         "Invalid vowel sequences: the length, then the code points. Sorted; none starts with another.")
    o.append("}")
    Path(out).write_text("\n".join(o) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
