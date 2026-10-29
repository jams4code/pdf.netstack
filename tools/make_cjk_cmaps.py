"""Generates the predefined CJK CMaps and CID to Unicode tables that text extraction reads.

    pip install brotli
    python tools/make_cjk_cmaps.py <cmap-resources> <mapping-resources-pdf>

The arguments are checkouts of https://github.com/adobe-type-tools/cmap-resources and
https://github.com/adobe-type-tools/mapping-resources-pdf (both BSD-3-Clause; the shipped data
comes from commits f5cf3bc and 2dd5e53). The script reads
every CMap of the Adobe-CNS1, Adobe-GB1, Adobe-Japan1, Adobe-Korea1 and Adobe-KR character
collections (cmap-resources/Adobe-*/CMap) and the CID to Unicode CMaps of those collections
(mapping-resources-pdf/pdf2unicode/Adobe-*-UCS2), and writes:

    src/PdfNetStack/Text/Extraction/CMaps/<collection>.br
        One Brotli stream per collection: the binary records of its CMaps, then its CID to
        Unicode table. Brotli's large window shares the many near copies (the UCS2, UTF16, UTF8
        and UTF32 forms of one Unicode CMap, the H and V forms) between records.
    src/PdfNetStack/Text/Extraction/PredefinedCMaps.g.cs
        The CMap names, the collection and offset of each record, and the stream lengths.

Each CMap keeps its own mappings and names its usecmap parent, as in the source files. The codes
of a Unicode CMap (Uni...-UCS2, -UTF16, -UTF8, -UTF32) are stored as Unicode scalar values, so
the four forms hold the same numbers; the library turns a code into its scalar before a lookup.

Record (all integers are unsigned LEB128 varints unless marked byte; "zigzag" is a signed varint):
    byte    flags: bit 0 the writing mode, bits 1 to 3 the code form (0 bytes, 1 UCS2,
            2 UTF16, 3 UTF8, 4 UTF32)
    varint  1 + the index of the usecmap parent in the name table, 0 for none
    byte    code space ranges, then each: byte length, the low bytes, the high bytes
    varint  CID ranges, then each: the low code less the previous high code plus one, the high
            code less the low code, and zigzag the CID less the CID that would follow the
            previous range
    varint  notdef ranges, then each: low and high as above, and the CID

CID to Unicode table: the collection's UTF-32 CMap inverted (each CID to the lowest scalar that
maps to it) gives most of Adobe-<collection>-UCS2; the table holds only the CIDs where the two
differ, scalar 0 removing a CID the inverse maps but the UCS2 CMap does not.
    varint  runs, then each: the first CID less the CID after the previous run, then a header.
            An even header is twice the CIDs less one, each CID followed by zigzag its scalar
            less the scalar after the previous one. An odd header is one CID mapped to several
            scalars: twice their count less one, plus one, then the scalars.

The generator checks every record and the table by decoding them back.
"""

import re
import sys
from pathlib import Path

import brotli

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "PdfNetStack" / "Text" / "Extraction"
# Each collection, its folder in cmap-resources, and the UTF-32 CMap whose inverse the CID to
# Unicode table starts from.
COLLECTIONS = [("CNS1", "Adobe-CNS1-7", "UniCNS-UTF32-H"), ("GB1", "Adobe-GB1-6", "UniGB-UTF32-H"),
               ("Japan1", "Adobe-Japan1-7", "UniJIS-UTF32-H"), ("KR", "Adobe-KR-9", "UniAKR-UTF32-H"),
               ("Korea1", "Adobe-Korea1-2", "UniKS-UTF32-H")]
FORMS = {None: 0, "UCS2": 1, "UTF16": 2, "UTF8": 3, "UTF32": 4}

TOKEN = re.compile(rb"%[^\r\n]*|<[0-9A-Fa-f\s]*>|/[^\s/<>\[\]\(\)\{\}%]*|\([^)]*\)|[^\s/<>\[\]\(\)\{\}%]+|[\[\]\{\}]")
SECTIONS = {"begincodespacerange": 2, "begincidchar": 2, "begincidrange": 3, "beginnotdefrange": 3,
            "beginbfchar": 2, "beginbfrange": 3}


def tokens(path):
    """PostScript tokens as Python values: bytes for hex strings, str for names and keywords."""
    for t in TOKEN.findall(path.read_bytes()):
        if t.startswith(b"%"):
            continue
        if t.startswith(b"<"):
            yield ("hex", bytes.fromhex(t[1:-1].decode()))
        elif t.startswith(b"/"):
            yield ("name", t[1:].decode())
        elif t.startswith(b"("):
            yield ("string", t[1:-1].decode())
        elif re.fullmatch(rb"-?\d+", t):
            yield ("int", int(t))
        elif t in (b"[", b"]"):
            yield (t.decode(), None)
        else:
            yield ("keyword", t.decode())


def parse(path):
    """The sections, usecmap, WMode and Ordering of a CMap file."""
    cmap = {"codespace": [], "cidchar": [], "cidrange": [], "notdefrange": [], "bfchar": [], "bfrange": [],
            "use": None, "wmode": 0, "ordering": None}
    mode, operands, array = None, [], None
    for kind, value in tokens(path):
        if kind == "[":
            array = []
            continue
        if kind == "]":
            operands.append(array)
            array = None
            continue
        if array is not None:
            array.append(value)
            continue
        if kind != "keyword":
            operands.append(value)
            continue
        if value in SECTIONS:
            mode, operands = value, []
            continue
        if mode is not None:
            if value != "end" + mode[5:]:
                raise ValueError(f"{path}: {value} in {mode}")
            n = SECTIONS[mode]
            if len(operands) % n:
                raise ValueError(f"{path}: odd {mode}")
            key = mode[5:].replace("codespacerange", "codespace")
            cmap[key] += [tuple(operands[i:i + n]) for i in range(0, len(operands), n)]
            mode, operands = None, []
            continue
        if value == "usecmap":
            cmap["use"] = operands[-1]
        elif value == "def" and len(operands) >= 2 and operands[-2] == "WMode":
            cmap["wmode"] = operands[-1]
        elif value == "def" and len(operands) >= 2 and operands[-2] == "Ordering":
            cmap["ordering"] = operands[-1]
        operands = []
    return cmap


def number(b):
    return int.from_bytes(b, "big")


def form(name):
    if not name.startswith("Uni"):
        return None
    found = [f for f in ("UCS2", "UTF16", "UTF8", "UTF32") if f"-{f}-" in name]
    if len(found) != 1:
        raise ValueError(name)
    return found[0]


def key(code, f):
    """The number a code is stored under: its bytes as a big-endian number, or its scalar value."""
    if f is None or f in ("UCS2", "UTF32"):
        return number(code)
    text = code.decode("utf-16-be" if f == "UTF16" else "utf-8")
    if len(text) != 1:
        raise ValueError(code)
    return ord(text)


def codes(low, high):
    """Each code from low to high, every byte within its own bounds (the CMap range rule)."""
    if len(low) != len(high):
        raise ValueError((low, high))
    a, b = number(low), number(high)
    for v in range(a, b + 1):
        code = v.to_bytes(len(low), "big")
        if all(low[i] <= code[i] <= high[i] for i in range(len(low))):
            yield code


def mappings(entries, f, single):
    """{key: CID} for cidchar or cidrange entries; a range counts CIDs over its codes in order."""
    m = {}
    for e in entries:
        low, high, cid = (e[0], e[0], e[1]) if single else e
        for i, code in enumerate(codes(low, high)):
            k = key(code, f)
            if k in m and m[k] != cid + i:
                raise ValueError(f"key {k:X} mapped twice")
            m[k] = cid + i
    return m


def runs(m):
    """[(low, high, first CID)] covering a {key: CID} map, consecutive keys and CIDs merged."""
    result = []
    for k in sorted(m):
        if result and result[-1][1] + 1 == k and result[-1][2] + (k - result[-1][0]) == m[k]:
            result[-1] = (result[-1][0], k, result[-1][2])
        else:
            result.append((k, k, m[k]))
    return result


def varint(v, out):
    if v < 0:
        raise ValueError(v)
    while v >= 0x80:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    out.append(v)


def zigzag(v, out):
    varint(v << 1 if v >= 0 else ((-v) << 1) - 1, out)


def record(cmap, f, parent):
    out = bytearray([cmap["wmode"] | (FORMS[f] << 1)])
    varint(parent, out)
    if len(cmap["codespace"]) > 255:
        raise ValueError("code space")
    out.append(len(cmap["codespace"]))
    for low, high in cmap["codespace"]:
        out.append(len(low))
        out += low + high
    cids = mappings(cmap["cidchar"], f, True)
    for k, v in mappings(cmap["cidrange"], f, False).items():
        if k in cids and cids[k] != v:
            raise ValueError("cidchar and cidrange disagree")
        cids[k] = v
    ranges = runs(cids)
    varint(len(ranges), out)
    previous, cid = 0, 0
    for low, high, first in ranges:
        varint(low - previous, out)
        varint(high - low, out)
        zigzag(first - cid, out)
        previous, cid = high + 1, first + high - low + 1
    notdef = []
    for low, high, n in cmap["notdefrange"]:
        keys = sorted(key(c, f) for c in codes(low, high))
        for k in keys:
            if notdef and notdef[-1][1] + 1 == k and notdef[-1][2] == n:
                notdef[-1] = (notdef[-1][0], k, n)
            else:
                notdef.append((k, k, n))
    notdef.sort()
    varint(len(notdef), out)
    previous = 0
    for low, high, n in notdef:
        if low < previous:
            raise ValueError("overlapping notdef ranges")
        varint(low - previous, out)
        varint(high - low, out)
        varint(n, out)
        previous = high + 1
    return bytes(out), cids, notdef


def unicode_table(path):
    """{CID: [scalars]} from a pdf2unicode CMap."""
    cmap = parse(path)
    table = {}

    def scalars(b):
        text = b.decode("utf-16-be")
        return [ord(c) for c in text]

    for cid, value in cmap["bfchar"]:
        table[number(cid)] = scalars(value)
    for low, high, value in cmap["bfrange"]:
        for i, cid in enumerate(range(number(low), number(high) + 1)):
            if isinstance(value, list):
                table[cid] = scalars(value[i])
            else:
                s = scalars(value)
                table[cid] = s[:-1] + [s[-1] + i]
    table.pop(0, None)
    return table


def invert(cids):
    """{CID: scalar} from a Unicode CMap's {scalar: CID}: the lowest scalar of each CID, as the library inverts it."""
    inverse = {}
    for k in sorted(cids):
        if k > 0:
            inverse.setdefault(cids[k], k)
    return inverse


def encode_unicode(table, inverse):
    """The entries of the table that the inverse does not already give; 0 removes an inverse entry."""
    diff = {cid: s for cid, s in table.items() if not (len(s) == 1 and inverse.get(cid) == s[0])}
    for cid in inverse:
        if cid not in table:
            diff[cid] = [0]
    runs, cids = [], sorted(diff)
    for cid in cids:
        if runs and runs[-1][-1] + 1 == cid and len(diff[cid]) == 1 and len(diff[runs[-1][0]]) == 1:
            runs[-1].append(cid)
        else:
            runs.append([cid])
    out = bytearray()
    varint(len(runs), out)
    previous, scalar = 0, 0
    for run in runs:
        varint(run[0] - previous, out)
        values = diff[run[0]]
        if len(values) > 1:
            varint(((len(values) - 1) << 1) | 1, out)
            for v in values:
                varint(v, out)
        else:
            varint((len(run) - 1) << 1, out)
            for cid in run:
                zigzag(diff[cid][0] - scalar, out)
                scalar = diff[cid][0] + 1
        previous = run[-1] + 1
    return bytes(out), diff


class Reader:
    """Reads a record back, as the library does, to check the encoding."""

    def __init__(self, data, at):
        self.data, self.at = data, at

    def byte(self):
        self.at += 1
        return self.data[self.at - 1]

    def varint(self):
        v, shift = 0, 0
        while True:
            b = self.byte()
            v |= (b & 0x7F) << shift
            shift += 7
            if b < 0x80:
                return v

    def zigzag(self):
        v = self.varint()
        return (v >> 1) ^ -(v & 1)


def check(data, at, cids, notdef):
    r = Reader(data, at)
    r.byte()
    r.varint()
    for _ in range(r.byte()):
        n = r.byte()
        r.at += 2 * n
    previous, cid, back = 0, 0, {}
    for _ in range(r.varint()):
        low = previous + r.varint()
        high = low + r.varint()
        first = cid + r.zigzag()
        for k in range(low, high + 1):
            back[k] = first + k - low
        previous, cid = high + 1, first + high - low + 1
    if back != cids:
        raise ValueError("record does not read back")
    previous, nd = 0, []
    for _ in range(r.varint()):
        low = previous + r.varint()
        high = low + r.varint()
        nd.append((low, high, r.varint()))
        previous = high + 1
    if nd != notdef:
        raise ValueError("notdef ranges do not read back")


def check_unicode(data, at, diff):
    r = Reader(data, at)
    back, previous, scalar = {}, 0, 0
    for _ in range(r.varint()):
        cid = previous + r.varint()
        header = r.varint()
        if header & 1:
            back[cid] = [r.varint() for _ in range((header >> 1) + 1)]
            previous = cid + 1
            continue
        for i in range((header >> 1) + 1):
            value = scalar + r.zigzag()
            back[cid + i] = [value]
            scalar = value + 1
        previous = cid + (header >> 1) + 1
    if back != diff or r.at != len(data):
        raise ValueError("CID to Unicode table does not read back")


def main(cmap_resources, mapping_resources):
    blobs, sources = [], {}
    for collection, folder, _ in COLLECTIONS:
        for path in sorted((Path(cmap_resources) / folder / "CMap").iterdir()):
            if path.name in sources:
                raise ValueError(f"{path.name} is in two collections")
            sources[path.name] = (collection, path)
    names = sorted(sources, key=lambda n: n.encode())
    index = {n: i for i, n in enumerate(names)}
    entries = {}
    for collection, folder, inverted in COLLECTIONS:
        blob = bytearray()
        inverse = None
        for name in names:
            c, path = sources[name]
            if c != collection:
                continue
            cmap = parse(path)
            if cmap["ordering"] != collection:
                raise ValueError(f"{name} has ordering {cmap['ordering']}")
            f = form(name)
            parent = 0
            if cmap["use"] is not None:
                if sources[cmap["use"]][0] != collection or form(cmap["use"]) != f:
                    raise ValueError(f"{name} uses {cmap['use']}")
                parent = index[cmap["use"]] + 1
            data, cids, notdef = record(cmap, f, parent)
            entries[name] = (collection, len(blob))
            blob += data
            check(blob, entries[name][1], cids, notdef)
            if name == inverted:
                if parent:
                    raise ValueError(f"{name} has a parent")
                inverse = invert(cids)
        table = unicode_table(Path(mapping_resources) / "pdf2unicode" / f"Adobe-{collection}-UCS2")
        unicode_at = len(blob)
        data, diff = encode_unicode(table, inverse)
        blob += data
        check_unicode(bytes(blob), unicode_at, diff)
        largest = max(max(table), max(inverse))
        packed = brotli.compress(bytes(blob), quality=11, lgwin=24)
        if brotli.decompress(packed) != bytes(blob):
            raise ValueError("Brotli round trip")
        (OUT / "CMaps").mkdir(exist_ok=True)
        (OUT / "CMaps" / f"{collection}.br").write_bytes(packed)
        blobs.append((collection, len(blob), unicode_at, index[inverted], largest, len(packed)))
        print(f"{collection}: {sum(1 for n in names if sources[n][0] == collection)} CMaps, "
              f"{len(table)} CIDs to Unicode ({len(diff)} beside the inverse), {len(blob)} bytes, {len(packed)} compressed")

    collections = [c for c, _, _ in COLLECTIONS]
    o = [
        "// <auto-generated>",
        "// Generated by tools/make_cjk_cmaps.py from Adobe's cmap-resources and mapping-resources-pdf",
        "// (https://github.com/adobe-type-tools). Do not edit.",
        "// Copyright 1990-2023 Adobe. Distributed under the BSD 3-Clause license; see THIRD-PARTY-NOTICES.md.",
        "// </auto-generated>",
        "",
        "namespace PdfNetStack.Text.Extraction;",
        "",
        "internal static partial class PredefinedCMaps",
        "{",
        f"    /// <summary>The {len(collections)} character collections, by their /Ordering; each has a resource CMaps.&lt;ordering&gt;.br.</summary>",
        "    private static readonly string[] Collections = [" + ", ".join(f'"{c}"' for c in collections) + "];",
        "",
        f"    private const int CollectionCount = {len(collections)};",
        "",
        "    /// <summary>",
        "    /// Per collection: the decompressed length of its stream, where its CID to Unicode table starts,",
        "    /// the name index of the UTF-32 CMap that table starts from, and the largest CID it maps.",
        "    /// </summary>",
        "    private static readonly (int Length, int Unicode, int Inverse, int MaxCid)[] Layout =",
        "    [",
    ]
    for c, length, unicode_at, inv, largest, _ in blobs:
        o.append(f"        ({length}, {unicode_at}, {inv}, {largest}),")
    o += [
        "    ];",
        "",
        f"    /// <summary>The {len(names)} CMap names, in ordinal order.</summary>",
        "    private static readonly string[] Names =",
        "    [",
    ]
    line = "       "
    for n in names:
        item = f' "{n}",'
        if len(line) + len(item) > 110:
            o.append(line)
            line = "       "
        line += item
    o.append(line)
    o += [
        "    ];",
        "",
        "    /// <summary>Per name: the index of its collection, and where its record starts in that stream.</summary>",
        "    private static readonly (byte Collection, int Offset)[] Records =",
        "    [",
    ]
    line = "       "
    for n in names:
        c, at = entries[n]
        item = f" ({collections.index(c)}, {at}),"
        if len(line) + len(item) > 110:
            o.append(line)
            line = "       "
        line += item
    o.append(line)
    o += ["    ];", "}"]
    with open(OUT / "PredefinedCMaps.g.cs", "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(o) + "\n")
    print(f"{len(names)} CMaps, {sum(b[-1] for b in blobs)} bytes compressed in all")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
