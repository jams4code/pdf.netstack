"""Regenerates the Brotli test vectors with Google's brotli encoder.

The .NET Framework tests have no Brotli encoder, so the managed decoder is checked against these
stored streams. On .NET 8 and later the tests also encode fresh streams with
System.IO.Compression.BrotliEncoder.

    pip install brotli
    python tools/make_brotli_fixtures.py

Fixtures (tests/PdfNetStack.Tests/Fixtures/Brotli), each <input>.<settings>.br:
    sample.html          English text and HTML markup written for these tests, which the encoder
                         matches against the static dictionary and its transforms
    sample.html.*.br     at qualities 0, 1, 5 and 11, windows from 10 to 24 bits, text mode
    Test-Arabic.ttf.q11w24.br     a fixture font, font mode
    random.q9w18.br      20000 bytes of noise (see noise below), which do not compress: the
                         encoder stores them in uncompressed meta-blocks
"""

from pathlib import Path

import brotli

ROOT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures"
OUT = ROOT / "Brotli"

SAMPLE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Notes on web fonts and print</title>
<link rel="stylesheet" href="style.css">
</head>
<body>
<h1>Notes on web fonts and print</h1>
<p>The first thing to know about a web font is that the browser never sees the file you designed.
It sees a compressed copy, and the compression is the part of the format that changed the most
between the first and the second version of the standard. The older format wrapped each table on
its own. The newer one puts every table into a single stream, which lets the compressor find the
same patterns across the whole font.</p>
<p>For a document converter this matters in a simple way: if the style sheet of a page says that
the heading uses a particular family, and that family is only available as a compressed web font,
then the converter must be able to read it, or the heading will be drawn in a fallback face that
the author did not choose.</p>
<h2>What the converter does</h2>
<ul>
<li>It reads the style sheet and finds every rule that declares a font face.</li>
<li>It loads the first source that it can read, and skips the ones it cannot.</li>
<li>It rebuilds the font tables, checks them, and embeds only the glyphs that the page uses.</li>
</ul>
<p>Because fonts come from pages that anyone can write, the reader treats every number in the file
as a claim to be checked. A length that runs past the end of the data is an error. A count that
would need more bytes than the stream holds is an error. A table that decompresses to more than
the limit is an error. None of these errors should take down the process that is reading the page.</p>
<h2>Why the details matter</h2>
<p>The International Organization for Standardization publishes the Open Font Format, and the
World Wide Web Consortium publishes the web font formats. They describe the same tables in slightly
different words. When the two descriptions disagree, the safest choice is the one that keeps the
output valid for every reader, including the ones that were written a long time ago.</p>
<p>THE END. This document is for testing; it is not a reference. Thank you for reading it, and
remember that the information here is only an example of English text with some markup.</p>
<footer><a href="https://example.com/index.html">Home</a> | <a href="https://example.com/about.html">About</a></footer>
</body>
</html>
"""


def write(name, data, quality, window, mode):
    encoded = brotli.compress(data, mode=mode, quality=quality, lgwin=window)
    assert brotli.decompress(encoded) == data
    path = OUT / f"{name}.q{quality}w{window}.br"
    path.write_bytes(encoded)
    print(f"{path.name}: {len(data)} -> {len(encoded)} bytes")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    sample = SAMPLE.encode("utf-8")
    (OUT / "sample.html").write_bytes(sample)
    for quality, window in ((0, 16), (1, 10), (5, 18), (11, 22), (11, 24)):
        write("sample.html", sample, quality, window, brotli.MODE_TEXT)
    write("Test-Arabic.ttf", (ROOT / "Fonts" / "Test-Arabic.ttf").read_bytes(), 11, 24, brotli.MODE_FONT)
    write("random", noise(20000), 9, 18, brotli.MODE_GENERIC)


def noise(length):
    """Bytes that do not compress, from a linear congruential generator the tests repeat."""
    state, out = 20261029, bytearray()
    for _ in range(length):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(state >> 16 & 0xFF)
    return bytes(out)


if __name__ == "__main__":
    main()
