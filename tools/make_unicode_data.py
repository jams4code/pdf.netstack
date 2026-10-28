"""Generates src/PdfNetStack/Text/UnicodeData.g.cs from the Unicode Character Database.

Usage: python tools/make_unicode_data.py <ucd directory> <output .cs file>

The directory holds these files of one Unicode version (https://www.unicode.org/Public/<v>/ucd/):
LineBreak.txt, EastAsianWidth.txt, DerivedGeneralCategory.txt (extracted/), emoji-data.txt
(emoji/), DerivedBidiClass.txt (extracted/), BidiBrackets.txt, BidiMirroring.txt,
ArabicShaping.txt, Scripts.txt and PropertyValueAliases.txt. Every property becomes a sorted
table of range starts and values.
"""

import re
import sys
from pathlib import Path

LINE_BREAK = [
    "BK", "CR", "LF", "CM", "NL", "WJ", "ZW", "GL", "SP", "ZWJ", "B2", "BA", "BB", "HY", "CB", "CL",
    "CP", "EX", "IN", "NS", "OP", "QU", "IS", "NU", "PO", "PR", "SY", "AK", "AL", "AP", "AS", "EB",
    "EM", "H2", "H3", "HL", "ID", "JL", "JV", "JT", "RI", "VF", "VI",
]
BIDI = [
    "L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
    "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI",
]
JOINING = ["U", "R", "L", "D", "C", "T"]

# Flags used by line breaking next to quotation marks, opening punctuation and emoji.
EAST_ASIAN, PI, PF, PICTOGRAPHIC_UNASSIGNED = 1, 2, 4, 8

MAX = 0x110000


# Long value names some @missing lines use.
LONG_NAMES = {"Left_To_Right": "L", "Right_To_Left": "R", "Arabic_Letter": "AL", "European_Terminator": "ET"}


def entries(path):
    """(first, last, value) from a UCD file, @missing defaults first, in file order."""
    missing, explicit = [], []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        m = re.match(r"#\s*@missing:\s*([0-9A-F]+)\.\.([0-9A-F]+)\s*;\s*([^;#\s]+)", line)
        if m:
            missing.append((int(m.group(1), 16), int(m.group(2), 16), LONG_NAMES.get(m.group(3), m.group(3))))
            continue
        line = line.split("#")[0].strip()
        if not line:
            continue
        fields = [f.strip() for f in line.split(";")]
        cps = fields[0].split("..")
        first = int(cps[0], 16)
        last = int(cps[-1], 16)
        explicit.append((first, last, fields[1:]))
    return missing, explicit


def fill(table, first, last, value):
    for cp in range(first, last + 1):
        table[cp] = value


def ranges(table):
    starts, values = [], []
    for cp, v in enumerate(table):
        if not values or v != values[-1]:
            starts.append(cp)
            values.append(v)
    return starts, values


def general_categories(ucd):
    gc = ["Cn"] * MAX
    _, explicit = entries(ucd / "DerivedGeneralCategory.txt")
    for first, last, fields in explicit:
        fill(gc, first, last, fields[0])
    return gc


def line_break(ucd, gc):
    lb = ["XX"] * MAX
    missing, explicit = entries(ucd / "LineBreak.txt")
    for first, last, value in missing:
        fill(lb, first, last, value)
    for first, last, fields in explicit:
        fill(lb, first, last, fields[0])

    # UAX #14, LB1: resolve the classes the default algorithm does not use.
    for cp in range(MAX):
        c = lb[cp]
        if c in ("AI", "SG", "XX"):
            lb[cp] = "AL"
        elif c == "SA":
            lb[cp] = "CM" if gc[cp] in ("Mn", "Mc") else "AL"
        elif c == "CJ":
            lb[cp] = "NS"
    return [LINE_BREAK.index(c) for c in lb]


def flags(ucd, gc):
    f = [0] * MAX
    ea = ["N"] * MAX
    missing, explicit = entries(ucd / "EastAsianWidth.txt")
    for first, last, value in missing:
        fill(ea, first, last, value)
    for first, last, fields in explicit:
        fill(ea, first, last, fields[0])
    pictographic = [False] * MAX
    _, explicit = entries(ucd / "emoji-data.txt")
    for first, last, fields in explicit:
        if fields[0] == "Extended_Pictographic":
            fill(pictographic, first, last, True)
    for cp in range(MAX):
        v = 0
        if ea[cp] in ("F", "W", "H"):
            v |= EAST_ASIAN
        if gc[cp] == "Pi":
            v |= PI
        if gc[cp] == "Pf":
            v |= PF
        if pictographic[cp] and gc[cp] == "Cn":
            v |= PICTOGRAPHIC_UNASSIGNED
        f[cp] = v
    return f


def bidi(ucd):
    table = ["L"] * MAX
    missing, explicit = entries(ucd / "DerivedBidiClass.txt")
    for first, last, value in missing:
        fill(table, first, last, value)
    for first, last, fields in explicit:
        fill(table, first, last, fields[0])
    return [BIDI.index(c) for c in table]


def joining(ucd, gc):
    table = ["T" if gc[cp] in ("Mn", "Me", "Cf") else "U" for cp in range(MAX)]
    _, explicit = entries(ucd / "ArabicShaping.txt")
    for first, last, fields in explicit:
        fill(table, first, last, fields[1])
    return [JOINING.index(c) for c in table]


# OpenType script tags that are not the lowercase ISO 15924 code.
OPENTYPE_TAGS = {"Zyyy": "DFLT", "Zinh": "DFLT", "Zzzz": "DFLT", "Hira": "kana", "Kana": "kana",
                 "Laoo": "lao ", "Yiii": "yi  ", "Nkoo": "nko ", "Vaii": "vai "}


def scripts(ucd):
    """Script index of each code point, and the OpenType tag of each index. Index 0 is Common,
    1 Inherited, 2 Unknown."""
    codes = {}
    for line in (ucd / "PropertyValueAliases.txt").read_text(encoding="utf-8").splitlines():
        f = [x.strip() for x in line.split("#")[0].split(";")]
        if len(f) >= 3 and f[0] == "sc":
            codes[f[2]] = f[1]
    names = ["Common", "Inherited", "Unknown"]
    table = [2] * MAX
    _, explicit = entries(ucd / "Scripts.txt")
    for first, last, fields in explicit:
        if fields[0] not in names:
            names.append(fields[0])
        fill(table, first, last, names.index(fields[0]))
    tags = [OPENTYPE_TAGS.get(codes[n], codes[n].lower()) for n in names]
    return table, tags


def pairs(path, with_type):
    out = []
    _, explicit = entries(path)
    for first, _, fields in explicit:
        other = int(fields[0], 16)
        out.append((first, other, fields[1] if with_type else None))
    return sorted(out)


def numbers(values, per_line=20, fmt="{}"):
    return "\n".join(
        "        " + ", ".join(fmt.format(v) for v in values[i:i + per_line]) + ","
        for i in range(0, len(values), per_line)
    )


def table(o, name, data, kind):
    starts, values = ranges(data)
    o.append(f"    internal static readonly int[] {name}Starts =")
    o.append("    [")
    o.append(numbers(starts, 12, "0x{:X}"))
    o.append("    ];")
    o.append("")
    o.append(f"    internal static readonly {kind}[] {name}Values =")
    o.append("    [")
    o.append(numbers(values, 32))
    o.append("    ];")
    o.append("")


def main(ucd_dir, out):
    ucd = Path(ucd_dir)
    version = re.search(r"-(\d+\.\d+\.\d+)\.txt", (ucd / "LineBreak.txt").read_text(encoding="utf-8").splitlines()[0]).group(1)
    gc = general_categories(ucd)

    o = ["// <auto-generated>",
         f"// Generated by tools/make_unicode_data.py from the Unicode Character Database {version}. Do not edit.",
         "// Copyright (c) 1991-2024 Unicode, Inc. Distributed under the Unicode License v3; see THIRD-PARTY-NOTICES.md.",
         "// </auto-generated>",
         "",
         "namespace PdfNetStack.Text;",
         "",
         "internal static partial class UnicodeData",
         "{",
         f'    internal const string Version = "{version}";',
         ""]
    table(o, "LineBreak", line_break(ucd, gc), "byte")
    table(o, "Flags", flags(ucd, gc), "byte")
    table(o, "Bidi", bidi(ucd), "byte")
    table(o, "Joining", joining(ucd, gc), "byte")
    script_table, tags = scripts(ucd)
    table(o, "Script", script_table, "byte")
    o.append("    /// <summary>OpenType script tag of each script index; 0 Common, 1 Inherited, 2 Unknown.</summary>")
    o.append("    internal static readonly string[] ScriptTags =")
    o.append("    [")
    o.append(numbers(tags, 16, '"{}"'))
    o.append("    ];")
    o.append("")

    brackets = pairs(ucd / "BidiBrackets.txt", True)
    o.append("    /// <summary>Paired brackets: code point, its pair, 1 for opening or 2 for closing.</summary>")
    o.append("    internal static readonly int[] Brackets =")
    o.append("    [")
    o.append(numbers([v for cp, other, t in brackets for v in (cp, other, 1 if t == "o" else 2)], 15, "0x{:X}"))
    o.append("    ];")
    o.append("")
    mirrors = pairs(ucd / "BidiMirroring.txt", False)
    o.append("    /// <summary>Bidi mirrored glyphs: code point and its mirror.</summary>")
    o.append("    internal static readonly int[] Mirrors =")
    o.append("    [")
    o.append(numbers([v for cp, other, _ in mirrors for v in (cp, other)], 16, "0x{:X}"))
    o.append("    ];")
    o.append("}")

    Path(out).write_text("\n".join(o) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
