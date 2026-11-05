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
- a bilevel image whose output is CCITT data is decoded to pixels on both sides (see below),
- streams that cannot be decoded must be byte identical with an identical filter chain.

Apart from the structural changes below, bilevel images coded again, and the opt-in image
changes described under JPEG images, the only accepted differences are the encoding entries of streams: `/Length`, `/Filter`, `/DecodeParms` and `/DL`. With opt-in stripping
enabled, `/Thumb` and `/PieceInfo` may be missing from the output.

### Structural changes

The lossless structural passes change more than encoding, so each has a rule of its own. None
is taken on trust: the verifier runs its own content analysis on its own parse of the source,
and accepts a difference only where that analysis proves it invisible. `PdfEquivalence` and
`pdfnetstack verify` apply the same rules.

**Unused resources.** A resource entry (font, XObject, graphics state, pattern, shading, color
space, property list) may be missing from the output only when no content that can use it names
it. The analysis reads every content stream from the page tree and from annotation appearances
(every state of /N, /R and /D), and follows form XObjects, tiling patterns, Type 3 glyph
procedures, soft mask groups and fonts selected through graphics states. A name counts as used
when it appears anywhere in the decoded content as a name token, whatever operator it belongs
to, and even inside strings, comments or inline image data. A page's content parts are scanned
joined with and without a separator between them, as readers differ. Content with resources of
its own that names something those resources lack also keeps that name in the resources of the
content that invoked it, since some readers look there. `DefaultGray`, `DefaultRGB` and
`DefaultCMYK` are used implicitly and always kept.

A category dictionary is pruned only when every resource dictionary holding it belongs to
content the analysis read in full: a dictionary also reached as form default resources (`/DR`),
from content that does not decode, from a holder that was never executed, or through any other
key keeps every entry. When a work budget runs out (2 GB of decoded content or 50 million
operators) nothing is pruned.

**Content streams.** A stream that is only ever used as content (page contents, forms, tiling
patterns, appearances, glyph procedures, soft mask groups, widget icons) may be rewritten when
it reads as the same tokens. The comparison tokenizes both versions: operators must be byte
identical, numbers equal by canonical value, names equal after `#xx` decoding, strings equal by
their bytes whether written literal or hex, and inline image dictionaries equal by value with
their data byte identical. Inline image data whose end is ambiguous (a filtered image without
/L whose data contains `EI`, or rules that disagree) stops the rewrite: everything from that
image on must be byte identical. Because readers join a page's parts in different ways, a
leading or trailing white space byte must be kept, and a first or last token that touches the
edge must keep its exact bytes. A stream that ends inside a comment is not changed.

**Merged font programs.** The TrueType program behind a composite font may be replaced by a
program shared with other subsets of the same font when: the font is Type0 with Identity-H or
Identity-V, its descendant is a CIDFontType2 with an identity /CIDToGIDMap and no /CIDSet, the
program is only reached through such fonts, and the analysis decoded every string shown with
them. The glyphs a program must keep are the codes those fonts show, glyph 0 and every component
of a composite glyph among them. For a merged program the verifier requires: every table other
than glyf, loca, hmtx, hhea, maxp, head, post, name and cmap byte identical; the same version,
revision, flags, units per em, style, recommended size, direction hint and glyph data format in
head; the same ascender, descender, line gap, caret slope and metric format in hhea; the same maxp
version and hinting limits at least as wide; and for each of those glyphs the same outline bytes
(up to trailing zero padding), the same advance width and the same left side bearing. Glyph names
(post) are dropped and the name and cmap tables come from one of the merged programs; a
CIDFontType2 with an identity map selects glyphs by id and reads none of them.

**Streams equal after decoding.** Deduplication compares streams by what they decode to, so two
copies of a font program or image merge even when they were encoded differently. The comparison
covers the decoded bytes, any predictor and filter still applied, and the stream dictionary
without its encoding entries.

**Inherited page attributes.** With `Linearize`, `/Resources`, `/MediaBox`, `/CropBox` and
`/Rotate` move from intermediate page tree nodes onto the pages that inherit them. The verifier
then compares each page by the value it ends up with, its own or its nearest ancestor's, and does
not compare those keys on intermediate nodes, whose values matter only through the pages.

### Streaming mode

With `PdfOptimizerOptions.Streaming` the proof is the same one, not a sampled or per page check.
The rewrite is written to a temporary file, and the verifier runs on two readers that load
objects on demand: a fresh reader over the source, which has parsed nothing the passes touched,
and a reader over that file. It walks both graphs from the trailer and compares every reachable
object, and every stream by its decoded content, under the same rules as above. Each stream is
decoded only while it is compared. The output must also parse with no repaired stream length,
checked again after the walk has read every stream. A merged object is allowed exactly the
image change its representative got, which is what the output holds for it. Only a proven
rewrite reaches the destination; otherwise the input is copied there unchanged.

### Linearized output

A linearized file holds the same objects as any other rewrite, so the proof above covers its
content. Its layout is checked separately. The tests read every linearized file back and check
the linearization dictionary against the file (`/L`, `/O`, `/E`, `/N`, `/T`, `/H`), both
cross-reference sections, and every entry of the page offset, shared object and outline hint
tables against the real offsets and lengths of the objects. `tools/measure_optimizer.py
--linearize` also runs qpdf's `--check-linearization` on each output when qpdf, or pikepdf with
its bundled libqpdf, is installed. The objects are assigned to the parts of Annex F the way qpdf
assigns them, so its check computes the same layout.

### Bilevel images

The Group 4 pass (`OptimizeBilevelImages`) codes 1 bit images again as CCITT Group 4: stencil
masks and 1 bit gray, palette and single colorant images, stored as samples under any general
filters (predictors included) or as CCITT Group 3. The encoder follows ITU-T T.6 and chooses its
modes by the coding procedure of T.4, so it writes the same bytes as libtiff (a test checks this
on libtiff's own output). /BlackIs1 is set when the 0 bits are the more common value, which keeps
the samples as they were while long runs use the short white codes.

The proof accepts an output coded as CCITT data in place of samples or CCITT data only by its
pixels. Both sides are decoded, every pixel of every row must match, and the padding bits at
the end of a row, which are not pixels, are ignored. Readers recover from flawed fax data in
different ways, so only data every reader takes alike counts, on both sides:

- parameters of their own type (integers, booleans), /Columns equal to the width (left out it
  means 1728 to some readers and the width to others), /Rows unset or equal to the height,
- data that decodes without a flaw: every code exists, no run overshoots its row, the changes
  on a row are strictly ordered (an empty run only where a row starts black or ends), no pass
  reaches past the reference row, and every row is there before the data ends or stops,
- samples at least as long as the image.

Anything else must match as it was, encoded bytes and parameters included. Group 3 data with a
flaw, or with parameters a reader could take differently, is never coded again.

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

The lossy image options (`MaxImageDpi`, `ConvertImagesToJpeg`) change pixels, so their proof
works on pixels and is a bound, not an identity. Only images the optimizer changed get it, and
`PdfEquivalence` grants it only to images the optimizer could have changed: soft masks, masks,
stencil and color keyed images must match exactly, and so must the size of images with a /Mask
or a /Matte soft mask. An image whose data is unchanged must keep its size, and so must a
bilevel image, a JPEG 2000 image that stays JPEG 2000 and a JBIG2 image that stays JBIG2: the
optimizer changes those only by reducing them. Every other dictionary entry must still match,
except that a JPEG 2000 image without /BitsPerComponent gains /BitsPerComponent 8 as samples or
a JPEG; the output must have the same components and bit depth, no larger a size, the same
aspect within a pixel, and decode as its dictionary says (raw samples, or one JPEG, JPEG 2000,
JBIG2 or CCITT image). Both images are decoded and the source is resampled to the output's size
with the same filter: Lanczos, or for bilevel images the reduction described below. Then each
channel's mean may move by at most 4 steps, 32 pixel thumbnails may differ by at most 12 steps
on average, which catches mirrored, shifted or swapped pictures, and luminance SSIM must reach
`MinImageSsim`, or 0.5 when no gate is set. With a gate, requantized JPEGs must also reach it.
The optimizer checks the same bounds on every JPEG it makes before choosing it, so it never
writes an image the proof would reject. `pdfnetstack verify --min-ssim` and
`PdfEquivalence.FindDifference(..., minImageSsim)` apply the same checks to any two files.

A JPEG 2000 image counts only when its dictionary describes it fully: a gray or RGB /ColorSpace,
no /Decode, no /SMaskInData, /BitsPerComponent 8 or none, and data with exactly that many color
channels, no opacity channel and colors that are not sYCC. A bilevel image (raw, CCITT data
every reader takes alike, or JBIG2) stays bilevel when reduced. Each new pixel takes the share
of its area that the 1 bits cover in the source, an exact box filter. The less common value
wins a pixel it covers more than half of, and also a pixel it covers at least a quarter of that
is a ridge across or along the image (covered more than the neighbour before it and at least as
much as the one after it): the middle of a stroke thinner than the new pixels, which plain
thresholding would erase. The arithmetic is exact and in a fixed order, so the proof reproduces
the reduction bit for bit and the optimizer's own output reaches SSIM 1 against it.

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

Structural passes, on synthetic samples from `tools/make_optimizer_samples.py` (unused
resources, verbose content, separately embedded subsets of one font, inherited attributes with
bookmarks, and all of them in one file): 88 pages rendered at 100 DPI from the default output
and from the linearized output, zero differing pixels, identical text. Every linearized output
passed qpdf 12.4's `--check-linearization`.

### Public benchmark corpus

The documents above are private. `eng/corpus/manifest.json` lists a public corpus anyone can
download and check: 77 public domain and openly licensed PDFs, each with its URL, SHA-256, size,
license and what it exercises (scans with JBIG2, JPEG 2000 and CCITT images, Chinese, Japanese
and Korean text, forms, XFA, tagged, signed, encrypted, linearized, damaged and PDF 2.0 files).
`tools/corpus_bench.py` optimizes each one with a lossless and a lossy profile, proves every
rewrite again with `PdfEquivalence`, compares the text and decoded images the library reads
before and after, and renders the first pages and the last page of both with PDFium:

```bash
python tools/fetch_corpus.py
dotnet build tools/PdfNetStack.CorpusRunner -c Release
python tools/corpus_bench.py --dpi 100 --pages 6
```

First run: 65 of 77 documents rewritten (the other 12 are signed or encrypted), 217 pages at
100 DPI identical after the lossless rewrites, the same text and decoded images before and
after, no rewrite failing its proof. Lossy rewrites lost at least 37.6 dB PSNR on any page but
one: a 1.8 KiB ImageMagick test file of tiny images, at 30.4 dB.

Run with bilevel, JBIG2 and JPEG 2000 images in the lossy profile: again no failure, lossless
rewrites identical, lossy saving 29.5% (27.2% before). Pages of CCITT scans reduced to 150 dpi
bilevel measure 22.8 to 22.9 dB PSNR against the source at 100 DPI, and a 1.8 KiB ImageMagick
fax test at 17.2 dB: a bilevel page differs from its source along every stroke edge, so its
PSNR says little. Read side by side, the reduced pages keep every stroke, rule and serif.

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
- Structural passes: pruning through forms, patterns, Type 3 fonts, appearances, inherited and
  shared resources, `/DR`, unreadable content and names split across parts; compaction of
  numbers, names, strings, comments and inline images, a randomized round trip, and every kind
  of token change caught; font merging with glyph conflicts, glyphs past the end of a subset and
  changed outlines; mutated inputs that must never throw.
- Linearization: the layout checks above on documents with one and many pages, shared fonts,
  bookmarks in and out of the first page, form fields, an open action and inherited attributes;
  page trees it cannot lay out; mutated inputs.
- JPEG: libjpeg-turbo fixtures (4:2:0, 4:2:2, 4:4:4, gray, Adobe CMYK, untransformed RGB, odd
  sizes) in baseline, optimized, progressive and restart marker layouts; lossless and requantized
  round trips; soft masks, Flate wrapped JPEG, color space mismatches, palettes, damaged and
  truncated data, unsupported coding processes, and mutated inputs that must never throw.
- Bilevel images: the Group 4 encoder byte for byte against libtiff, round trips of random rows
  at widths from 1 to 9000 pixels with runs past 2560 in both polarities, the libtiff Group 3
  fixtures coded again as Group 4, stencil masks, palettes, /Decode and predictors; Group 3 with
  flaws or parameters readers take differently left alone; a changed pixel, an inverted
  reading, short data and ambiguous parameters each caught by the proof; reductions of CCITT,
  JBIG2 and raw scans with thin strokes kept, and bad reductions (another page, too dark, plain
  subsampling, gray output) rejected; JPEG 2000 converted and reduced, and the JPEG 2000 images
  the dictionary cannot describe left alone; mutated fax and JBIG2 data that must never throw.
- Fuzzing: mutated inputs (byte flips, deletions, truncation, injected tokens, duplicated
  regions) must never throw and must produce either the exact input or a verified rewrite.
  CI runs 600 cases; set `PDFNETSTACK_FUZZ_ITERATIONS` for a long soak. 20,000 cases pass locally.

## Reporting a fidelity bug

If an optimized file looks different from its source in any viewer, that is the most serious
class of bug in this project. Please open an issue with the viewer and version, and if you can
share it, the source file. `pdfnetstack verify` output for the pair helps triage.
