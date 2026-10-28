# Fidelity and verification

"Lossless" here means a conforming PDF reader cannot tell the output from the input: same pages,
same content, same fonts and images, same annotations, forms, bookmarks, structure, metadata and
attachments. Three layers of evidence back that claim.

## 1. Built-in equivalence proof

Runs on every optimization unless `Verify` is turned off.

The source bytes and the candidate bytes are parsed independently. Starting at the trailer's
`/Root`, `/Info` and `/ID`, the verifier walks both graphs together:

- names, strings, booleans and numbers must be identical (numbers compared by canonical value),
- arrays must have the same length and matching items,
- dictionaries must have the same keys with matching values; an entry whose value is null counts
  as absent, as the specification defines,
- references are followed in pairs, so cycles terminate; a source object must always map to the
  same output object, and two source objects may share one output object only when neither has
  identity of its own (annotations, form fields, pages, structure elements and tree nodes never do),
- every stream is decoded through all decodable filters and predictors on both sides and the
  bytes must match exactly; filters that stay encoded (DCT, JPX, JBIG2, CCITT) must be identical,
  including their parameters and any objects they reference such as `/JBIG2Globals`,
- JPEG data whose bytes changed is decoded to DCT coefficients on both sides (see below),
- streams that cannot be decoded must be byte identical with an identical filter chain.

The only accepted differences are the encoding entries of streams: `/Length`, `/Filter`,
`/DecodeParms` and `/DL`. With opt-in stripping enabled, `/Thumb` and `/PieceInfo` may be missing
from the output.

### JPEG images

The JPEG pass re-encodes DCTDecode data without ever producing pixels, so the proof works at the
same level. Both streams are read to their quantized coefficients, and must agree on width,
height, component ids and order, chroma sampling, and the JFIF and Adobe color transform
signalling. Every coefficient of every visible block must then be identical. Entropy coding,
progressive or baseline layout, restart markers and the segments readers ignore (Exif, XMP,
embedded ICC profiles, comments) may differ. Equal coefficients decode to equal pixels in any
conforming decoder, which is why this counts as lossless.

With `JpegQuality` set, the proof accepts one more change and checks it exactly: each output
quantization step must be at least the source step, and each output coefficient must equal the
source coefficient requantized with the integer rounding the optimizer uses. Any other change,
including one coefficient off by one, fails verification. `pdfnetstack verify --jpeg-quality`
and `PdfEquivalence.FindDifference(..., allowJpegRequantization: true)` apply the same rule.

The coefficient reader is itself checked against libjpeg: test fixtures written by libjpeg-turbo
in baseline, optimized, progressive and restart marker layouts of the same image must decode to
identical coefficients, and scaled tables must match libjpeg's own for the same quality.

What it does not check: the trailer keys that describe file layout (`/Size`, `/Prev`,
`/XRefStm`), object numbers and generations, and byte offsets. None of these affect rendering.

The checker is public as `PdfEquivalence.FindDifference` and on the command line as
`pdfnetstack verify`, so you can use it to validate any other tool's output.

## 2. Independent render comparison

`tools/render_compare.py` renders both files with PDFium (through `pypdfium2`, a development
tool only, not a dependency of any package) and compares page count, page geometry, extracted
text per page and every RGB pixel of the first pages and the last page.

```bash
pip install pypdfium2 numpy
pdfnetstack batch ./corpus ./out
python tools/render_compare.py ./corpus ./out --dpi 150 --pages 6
```

Latest run: 1,068 optimized documents, 2,085 pages at 150 DPI, zero differing pixels.

## 3. Tests

- Syntax: literal and hex string escapes, names with `#xx`, number canonicalization, references,
  nesting limits, and a randomized round trip of strings and names through the serializer.
- Filters: known vectors for ASCII85, ASCIIHex and RunLength, a reference LZW encoder for both
  EarlyChange modes, Flate with raw deflate fallback, corrupt data, a decompression bomb, PNG
  predictor round trips across bit depths, TIFF predictor.
- Reader: classic and stream xrefs, object streams, incremental updates, `/Prev` loops, shifted
  offsets, missing `startxref`, wrong stream lengths, missing objects, garbage input.
- Optimizer: every pass, every refusal rule, dedup identity rules, PDF/A-1 layout, stripping,
  cancellation, atomic file output.
- JPEG: libjpeg-turbo fixtures (4:2:0, 4:2:2, 4:4:4, gray, Adobe CMYK, untransformed RGB, odd
  sizes) in baseline, optimized, progressive and restart marker layouts; lossless and requantized
  round trips; soft masks, Flate wrapped JPEG, color space mismatches, palettes, damaged and
  truncated data, unsupported coding processes, and mutated inputs that must never throw.
- Fuzzing: mutated inputs (byte flips, deletions, truncation, injected tokens, duplicated
  regions) must never throw and must produce either the exact input or a verified rewrite.
  CI runs 600 cases; set `PDFNETSTACK_FUZZ_ITERATIONS` for a long soak. 20,000 cases pass locally.

## Reporting a fidelity bug

If an optimized file looks different from its source in any viewer, that is the most serious
class of bug in this project. Please open an issue with the viewer and version, and if you can
share it, the source file. `pdfnetstack verify` output for the pair helps triage.
