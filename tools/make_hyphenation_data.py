"""Generates the hyphenation pattern sets of PdfNetStack.Hyphenation and their test fixtures.

    python tools/make_hyphenation_data.py <tex-hyphen checkout> <wortliste checkout> [--check]

The arguments are checkouts of https://github.com/hyphenation/tex-hyphen (the shipped data comes
from commit 5684c0f) and of https://repo.or.cz/wortliste.git (MIT; the word list the German
patterns are generated from, commit 09e5221, used only for test data). For each language in
LANGUAGES the script reads hyph-<source>.tex for its licence and minimums, and
hyph-<source>.pat.txt and .hyp.txt for its patterns and exceptions, and writes:

    src/PdfNetStack.Hyphenation/Patterns/<tag>.deflate
        Raw deflate of UTF-8 lines: the set's provenance, copyright and licence, each line starting
        with #; then "left right", the minimums the source gives for typesetting; then the patterns,
        one per line; an empty line; then the exceptions, one per line. The patterns and exceptions
        are unchanged. A line "@tag" in place of the patterns means the patterns of that set.
    src/PdfNetStack.Hyphenation/LICENSE.txt
        The licence of the package: its code, then every set's copyright and licence.
    tests/PdfNetStack.Tests/Fixtures/Hyphenation/reference.tsv
        Tag, word and expected hyphenation for each exception, each word in SAMPLES and pseudo words
        built from the set's own patterns. Expected results come from hyphenate() below, a direct
        reading of Liang's definition that scans every substring against a dictionary and shares
        nothing with the library's trie, so the tests compare two implementations.
    tests/PdfNetStack.Tests/Fixtures/Hyphenation/de-wortliste.tsv
        A sample of the German word list: word and the hyphenation the list itself gives, in
        reformed spelling. The expected results come from the list; only the selection is made
        here: a word the patterns disagree with is dropped, and the agreement over the sample is
        printed and written in the file's first line.

A set ships only if its .tex file carries the grant named in LANGUAGES: the script stops when a
licence changes upstream. --check writes nothing and fails when a file differs.

Licences were read from the .tex files, language by language. Where a file offers a choice (it,
da, pl, cy, et, ga and others), the permissive one is taken. Not shipped, for their licences:
ca, eo, hsb, ia, is, kmr, ru, sh, sv, th, tr (LPPL only), cs, id, mk, sr (GPL only), hu (MPL, GPL,
LGPL), hy, lv (LGPL, GPL), fi ("may be freely distributed", no right to modify), ro and
mn-cyrl-x-lmc (no licence stated). Permissive but not shipped, to keep the package to living
languages in Latin, Greek and Cyrillic scripts that hyphenate in practice: de-1901, de-ch-1901,
fi-x-school, the Indic sets, cop, cu, fur, grc, ka, kk, la-x-classic, la-x-liturgic, mn-cyrl,
mul-ethi, oc, pi, pms, rm, sa, tk and zh-latn-pinyin. ar, fa, he and vi hold almost no patterns.
"""

import random
import re
import sys
import zlib
from pathlib import Path

COMMIT = "5684c0f51c0b81133db2efbe60a408b4155a3ff5"

MIT = "MIT"
BSD3 = "BSD-3-Clause"
DATA_FILES = "Data files"  # The notice of the Unicode data files, with a duty to mark changes.
COPYING = "Copying"  # "Copying and distribution of this file ... permitted", notice preserved.
COPYING_NO = "Copying, Norwegian"  # The same grant, in the wording of hyph-nb, hyph-nn and hyph-no.
BG = "Bulgarian"  # The licence of hyph-bg: a short notice much like BSD.
UNLICENSE = "Unlicense"

# tag, source file stem, chosen licence, what the licence block of the source must say.
LANGUAGES = [
    ("en-us", "en-us", COPYING, "Copying and distribution of this file, with or without modification"),
    ("en-gb", "en-gb", MIT, "name: MIT"),
    ("de", "de-1996", MIT, "name: MIT"),
    ("fr", "fr", MIT, "name: MIT"),
    ("nl", "nl", MIT, "name: MIT"),
    ("es", "es", MIT, "name: MIT/X11"),
    ("it", "it", MIT, "name: MIT"),
    ("pt", "pt", BSD3, "name: BSD 3-clause licence"),
    ("af", "af", MIT, "name: MIT"),
    ("be", "be", MIT, "name: MIT"),
    ("bg", "bg", BG, "This software may be used, modified, copied, distributed, and sold"),
    ("cy", "cy", MIT, "name: MIT"),
    ("da", "da", MIT, "name: MIT"),
    ("el-monoton", "el-monoton", MIT, "name: MIT"),
    ("el-polyton", "el-polyton", MIT, "name: MIT"),
    ("et", "et", MIT, "name: MIT"),
    ("eu", "eu", DATA_FILES, "a copy of this file and any associated documentation"),
    ("ga", "ga", MIT, "name: MIT"),
    ("gl", "gl", UNLICENSE, "name: Unlicence"),
    ("hr", "hr", DATA_FILES, "a copy of this file and any associated documentation"),
    ("la", "la", MIT, "name: MIT"),
    ("lt", "lt", MIT, "name: MIT"),
    ("nb", "nb", COPYING_NO, "Copying and distribution of this file, with or without modification"),
    ("nn", "nn", COPYING_NO, "Copying and distribution of this file, with or without modification"),
    ("pl", "pl", MIT, "name: MIT"),
    ("sk", "sk", MIT, "name: MIT"),
    ("sl", "sl", MIT, "name: MIT"),
    ("sq", "sq", MIT, "name: MIT"),
    ("uk", "uk", MIT, "name: MIT"),
]

# Norwegian Nynorsk has the patterns of Bokmal, checked here, and its own exceptions: its file says
# "@nb" in place of the patterns. Both come from hyph-no.tex, whose copyright both files name.
PATTERNS_OF = {"nn": "nb"}

TEXTS = {
    MIT: """\
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
""",
    BSD3: """\
Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:
* Redistributions of source code must retain the above copyright notice, this list of conditions
  and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions
  and the following disclaimer in the documentation and/or other materials provided with the
  distribution.
* Neither the name of the University of Campinas, of the University of Minho nor the names of its
  contributors may be used to endorse or promote products derived from this software without
  specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL PEDRO J. DE REZENDE OR J.JOAO
DIAS ALMEIDA BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
""",
    DATA_FILES: """\
Permission is hereby granted, free of charge, to any person obtaining a copy of this file and any
associated documentation (the "Data Files") to deal in the Data Files without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, and/or sell copies
of the Data Files, and to permit persons to whom the Data Files are furnished to do so, provided
that (a) this copyright and permission notice appear with all copies of the Data Files, (b) this
copyright and permission notice appear in associated documentation, and (c) there is clear notice
in each modified Data File as well as in the documentation associated with the Data File(s) that the
data has been modified.

THE DATA FILES ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT OF THIRD PARTY RIGHTS. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN
THIS NOTICE BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES.

Except as contained in this notice, the name of a copyright holder shall not be used in advertising
or otherwise to promote the sale, use or other dealings in these Data Files without prior written
authorization of the copyright holder.
""",
    COPYING: """\
Copying and distribution of this file, with or without modification, are permitted in any medium
without royalty provided the copyright notice and this notice are preserved.
""",
    COPYING_NO: """\
hyph-nb and hyph-nn: Copying and distribution of this file, with or without modification, are
permitted in any medium without royalty, provided the copyright notice and this notice are preserved.
hyph-no: Copying and distribution of this file, with or without modification, is permitted in any
medium without royalty provided the copyright notice and this notice are preserved.
""",
    BG: """\
This software may be used, modified, copied, distributed, and sold, both in source and binary form
provided that the above copyright notice and these terms are retained. The name of the author may
not be used to endorse or promote products derived from this software without prior permission. THIS
SOFTWARE IS PROVIDES "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL
THE AUTHOR BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE.
""",
    UNLICENSE: "Released under the Unlicense, https://unlicense.org/.\n",
}

# hyph-en-us keeps Knuth's patterns from Plain TeX's hyphen.tex, whose own notice is this.
KNUTH = """\
The patterns from Plain TeX's hyphen.tex by Donald E. Knuth carry this notice: "Unlimited copying
and redistribution of this file are permitted as long as this file is not modified. Modifications
are permitted, but only if the resulting file is not named hyphen.tex."
"""

CHANGED = (
    "Modified from the file in hyph-utf8, commit {commit}: the patterns and exceptions are copied "
    "unchanged into one file with the rest of the file left out; the data format is PdfNetStack's."
)

# Real words, so a run of the fixture reads like the language. The expected breaks are computed.
SAMPLES = {
    "en-us": "hyphenation algorithm typography concatenation international representation",
    "en-gb": "hyphenation colour programme organisation characterisation typography",
    "de": "silbentrennung rechtschreibung donaudampfschifffahrt bundesverfassungsgericht schifffahrt straße zucker eisenbahn",
    "fr": "anticonstitutionnellement ordinateur développement bibliothèque information cœur",
    "nl": "verantwoordelijkheid ontwikkeling woordafbreking arbeidsongeschiktheid",
    "es": "electrodoméstico desarrollo paralelepípedo otorrinolaringólogo información",
    "it": "precipitevolissimevolmente sviluppo informazione biblioteca",
    "pt": "desenvolvimento anticonstitucionalidade informação biblioteca",
    "af": "ontwikkeling verantwoordelikheid inligting",
    "be": "беларуская развіццё інфармацыя",
    "bg": "българска информация развитие",
    "cy": "datblygiad gwybodaeth llyfrgell",
    "da": "udvikling information bibliotek",
    "el-monoton": "ανάπτυξη πληροφορία βιβλιοθήκη",
    "el-polyton": "ἀνάπτυξη πληροφορία",
    "et": "arendamine informatsioon raamatukogu",
    "eu": "garapena informazioa liburutegia",
    "ga": "forbairt eolas leabharlann",
    "gl": "desenvolvemento información biblioteca",
    "hr": "razvoj informacija knjižnica",
    "la": "bibliotheca informatio progressio",
    "lt": "biblioteka informacija plėtra",
    "nb": "utvikling informasjon bibliotek",
    "nn": "utvikling informasjon bibliotek",
    "pl": "rozwój informacja biblioteka",
    "sk": "knižnica informácia vývoj",
    "sl": "knjižnica informacije razvoj",
    "sq": "biblioteka informacion zhvillim",
    "uk": "бібліотека інформація розвиток",
}


def header(tex):
    """The YAML style header of a .tex file: its leading comment lines, up to the ==== rule."""
    lines = []
    for line in tex.split("\n"):
        if not line.startswith("%") or line.startswith("% ==="):
            break
        lines.append(line[1:])
    return lines


def block(lines, key):
    """The lines of a top level header key, the first one included."""
    out, inside = [], False
    for line in lines:
        if re.match(r"^ [a-z_]+:", line):
            if inside:
                break
            inside = line.startswith(f" {key}:")
        if inside:
            out.append(line)
    return out


def copyright_of(lines, tag):
    text = " ".join(l.strip() for l in block(lines, "copyright") if l.strip())
    text = re.sub(r"^copyright:\s*>?\s*", "", text)
    if "Copyright" not in text:
        raise SystemExit(f"{tag}: no copyright line")
    # The year ranges use a plain hyphen here, whatever dash the source has.
    return re.sub(r"\s+", " ", text).replace("\u2013", "-").replace("\u2014", "-")


def minimums(lines, tag):
    text = "\n".join(block(lines, "hyphenmins"))
    match = re.search(r"typesetting:\s*\n\s*left:\s*(\d+)\s*\n\s*right:\s*(\d+)", text) or re.search(r"left:\s*(\d+)\s*\n\s*right:\s*(\d+)", text)
    if not match:
        raise SystemExit(f"{tag}: no hyphenmins")
    return int(match[1]), int(match[2])


def read_lines(path):
    if not path.exists():
        return []
    return [l.strip() for l in path.read_text(encoding="utf-8").split("\n") if l.strip()]


def parse(pattern):
    """Letters, and the digit before each letter and after the last, as in Liang's patterns."""
    letters, digits = [], [0]
    for c in pattern:
        if c.isascii() and c.isdigit():
            digits[-1] = int(c)
        else:
            letters.append(c)
            digits.append(0)
    return "".join(letters), digits


def join_points(word, points):
    out, last = [], 0
    for k in points:
        out.append(word[last:k])
        last = k
    out.append(word[last:])
    return "-".join(out)


def hyphenate(word, patterns, exceptions, left, right):
    """Liang's definition, directly: every substring of .word. is looked up in a dictionary."""
    n = len(word)
    if word in exceptions:
        points = exceptions[word]
    else:
        dotted = "." + word + "."
        values = [0] * (len(dotted) + 1)
        for i in range(len(dotted)):
            for j in range(i + 1, len(dotted) + 1):
                digits = patterns.get(dotted[i:j])
                if digits:
                    for k, d in enumerate(digits):
                        values[i + k] = max(values[i + k], d)
        # values[i] is the gap before dotted[i], so a break after letter k is values[k + 1].
        points = [k for k in range(1, n) if values[k + 1] % 2 == 1]
    return join_points(word, [k for k in points if left <= k <= n - right])


def pseudo_words(patterns, seed):
    """Words joined from the patterns' own letters, so many patterns fire and collide."""
    rng = random.Random(seed)
    keys = sorted(k.strip(".") for k in patterns if len(k.strip(".")) >= 2 and "'" not in k and "’" not in k)
    words = []
    for _ in range(24):
        word = ""
        while len(word) < 12:
            word += rng.choice(keys)
        words.append(word[:rng.randint(8, 40)])
    return words


def wortliste_reformed(path):
    """Word and hyphenation in reformed spelling, for plain entries of the German word list."""
    for raw in path.read_text(encoding="utf-8").split("\n"):
        fields = raw.split("#", 1)[0].strip().split(";")
        if len(fields) < 2 or not fields[0]:
            continue
        # Field 2 serves every spelling; otherwise field 4 is the reformed one.
        form = fields[1] if not re.fullmatch(r"-\d-", fields[1]) else (fields[3] if len(fields) > 3 else "")
        if not form or re.fullmatch(r"-\d-", form):
            continue
        # Alternatives, discouraged points and other special markers: left out.
        if re.search(r"[{}\[\]\\/.·_]", form):
            continue
        yield fields[0], re.sub(r"[-=<>]+", "-", form).lower()


def sample_wortliste(path, table, exceptions, left, right):
    """Every 2000th plain entry. Returns the lines the patterns agree with, and the sample's size."""
    letters = {c for pattern in table for c in pattern}
    kept, sampled = [], 0
    for index, (word, expected) in enumerate(wortliste_reformed(path)):
        lower = word.lower()
        if index % 2000 != 0 or len(lower) < 6 or not all(c in letters for c in lower):
            continue
        # The list marks every break; those inside the minimums do not apply.
        points, count = [], 0
        for part in expected.split("-")[:-1]:
            count += len(part)
            if left <= count <= len(lower) - right:
                points.append(count)
        want = join_points(lower, points)
        sampled += 1
        if hyphenate(lower, table, exceptions, left, right) == want:
            kept.append(f"{lower}\t{want}")
    return kept, sampled


def licence_file(notices):
    out = [
        "PdfNetStack.Hyphenation",
        "",
        "The code of this package is under the MIT License, Copyright (c) 2026 Jamal Abdelkhalek and",
        "PdfNetStack contributors. The hyphenation patterns it embeds come from hyph-utf8",
        f"(https://github.com/hyphenation/tex-hyphen, commit {COMMIT}) and stay under their own terms,",
        "each listed here with its holders and its text. Where the source offers a choice of licences,",
        "the one listed is the one taken. Each pattern file also carries its own notice inside it.",
        "",
        CHANGED.format(commit=COMMIT),
        "",
        "--- MIT License, for the code of this package and for the pattern sets listed ---",
        "",
    ]
    for licence in [MIT, BSD3, COPYING, COPYING_NO, BG, DATA_FILES, UNLICENSE]:
        if licence != MIT:
            out.extend([f"--- {licence} ---", ""])
        for tag, holder in notices[licence]:
            out.append(f"{tag}: {holder}")
        out.append("")
        if licence == COPYING:
            out.extend(KNUTH.rstrip("\n").split("\n"))
            out.append("")
        out.extend(TEXTS[licence].rstrip("\n").split("\n"))
        out.append("")
    return "\n".join(out)


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    checkout = Path(sys.argv[1])
    wortliste = Path(sys.argv[2]) / "wortliste"
    check = "--check" in sys.argv
    repo = Path(__file__).resolve().parent.parent
    source = checkout / "hyph-utf8" / "tex" / "generic" / "hyph-utf8" / "patterns"
    out_dir = repo / "src" / "PdfNetStack.Hyphenation" / "Patterns"
    fixtures = repo / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Hyphenation"
    outputs = {}
    reference = ["# Generated by tools/make_hyphenation_data.py: tag, word, expected."]
    notices = {}
    sizes = []

    for tag, stem, licence, grant in LANGUAGES:
        lines = header((source / "tex" / f"hyph-{stem}.tex").read_text(encoding="utf-8"))
        if grant not in "\n".join(block(lines, "licence")):
            raise SystemExit(f"{tag}: the licence of hyph-{stem}.tex no longer says {grant!r}")
        holder = copyright_of(lines, tag)
        left, right = minimums(lines, tag)
        pats = read_lines(source / "txt" / f"hyph-{stem}.pat.txt")
        hyps = read_lines(source / "txt" / f"hyph-{stem}.hyp.txt")
        if not pats:
            raise SystemExit(f"{tag}: no patterns")
        for line in pats + hyps:
            if "#" in line or " " in line or "\t" in line:
                raise SystemExit(f"{tag}: unexpected line {line!r}")

        stored = pats
        if tag in PATTERNS_OF:
            other = PATTERNS_OF[tag]
            if read_lines(source / "txt" / f"hyph-{other}.pat.txt") != pats:
                raise SystemExit(f"{tag}: no longer has the patterns of {other}")
            no_lines = header((source / "tex" / "hyph-no.tex").read_text(encoding="utf-8"))
            if grant not in "\n".join(block(no_lines, "licence")):
                raise SystemExit("hyph-no: the licence changed")
            holder = f"{holder}; {copyright_of(no_lines, 'no')}"
            stored = [f"@{other}"]

        notice = [
            f"PdfNetStack.Hyphenation, set {tag}, from hyph-{stem} of hyph-utf8 (https://github.com/hyphenation/tex-hyphen).",
            CHANGED.format(commit=COMMIT),
            holder,
            *TEXTS[licence].rstrip("\n").split("\n"),
        ]
        if tag == "en-us":
            notice.extend(KNUTH.rstrip("\n").split("\n"))
        body = [f"# {n}".rstrip() for n in notice] + [f"{left} {right}"] + stored + [""] + hyps + [""]
        data = "\n".join(body).encode("utf-8")
        compressor = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
        packed = compressor.compress(data) + compressor.flush()
        outputs[out_dir / f"{tag}.deflate"] = packed
        sizes.append((tag, len(data), len(packed), len(pats), len(hyps), left, right))
        notices.setdefault(licence, []).append((tag, holder))

        table = {}
        for pattern in pats:
            letters, digits = parse(pattern)
            table[letters] = digits
        exceptions = {}
        for line in hyps:
            points, count = [], 0
            for c in line:
                if c == "-":
                    points.append(count)
                else:
                    count += 1
            exceptions[line.replace("-", "")] = points

        if tag == "de":
            kept, sampled = sample_wortliste(wortliste, table, exceptions, left, right)
            print(f"word list: {len(kept)} of {sampled} sampled words hyphenate as the list says")
            first = f"# Sample of wortliste (MIT), commit 09e5221: {len(kept)} of {sampled} sampled words, those the patterns agree with."
            outputs[fixtures / "de-wortliste.tsv"] = ("\n".join([first] + kept) + "\n").encode("utf-8")

        for word in list(exceptions) + SAMPLES[tag].split() + pseudo_words(table, tag):
            if len(word) >= left + right:
                reference.append(f"{tag}\t{word}\t{hyphenate(word, table, exceptions, left, right)}")

    outputs[repo / "src" / "PdfNetStack.Hyphenation" / "LICENSE.txt"] = licence_file(notices).encode("utf-8")
    outputs[fixtures / "reference.tsv"] = ("\n".join(reference) + "\n").encode("utf-8")

    failed = False
    for path, data in outputs.items():
        if check:
            if not path.exists() or path.read_bytes() != data:
                print(f"DIFFERS {path}")
                failed = True
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    for s in sizes:
        print(f"{s[0]:12} raw {s[1]:>7}  deflate {s[2]:>7}  patterns {s[3]:>6}  exceptions {s[4]:>4}  min {s[5]} {s[6]}")
    print(f"{'total':12} raw {sum(s[1] for s in sizes):>7}  deflate {sum(s[2] for s in sizes):>7}")
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
