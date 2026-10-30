# Roadmap

PdfNetStack is a PDF library and nothing else. Work is ordered by value per unit of risk. Every
item ships with tests, and anything that is not lossless sits behind an option that is off by
default.

## Released

**1.0** (on NuGet): the lossless optimizer with equivalence verification, document editing
(open, create, merge, split, import, reorder, rotate and remove pages, document info, bookmarks),
encryption (reads RC4, AES-128 and AES-256; writes AES-128 and AES-256 with permissions), and the
`pdfnetstack` command line tool.

## Next release: 1.1

Done and in the main branch:

| Area | Scope |
|---|---|
| Object model | Public `PdfObject` types, `Catalog`, `Resolve`, `AddObject`, `GetStreamData`, page dictionaries |
| Drawing | Paths, pens, gray, RGB and CMYK colors, opacity, blend modes, gradients, transforms, clipping, on new and existing pages |
| Fonts | Standard 14 fonts; TrueType, OpenType CFF, collections, WOFF and WOFF2; subsetting, Type 0 embedding, ToUnicode, kerning; font collections with CSS font matching (width, style and weight ranges); variable fonts at named instances or any axis values, TrueType (gvar, HVAR, MVAR, cvar) and CFF2, embedded as static fonts once per location, and matched as ranges in collections and HTML |
| Text layout | Unicode line breaking (UAX #14) and bidi (UAX #9) passing the Unicode 16 suites; an OpenType shaper matching HarfBuzz on the fonts we compare, with Unicode normalization, Arabic, the nine Indic scripts, the Universal Shaping Engine (Southeast Asian, Tibetan, Mongolian and the other complex scripts), Thai and Lao, Khmer, Myanmar and Hangul; paragraphs with wrapping, alignment, justification (kashida for Arabic, opt in), rich text and fallback |
| Images | JPEG, PNG, GIF, BMP, TIFF (every page), WebP and SVG placement with fit, alignment and opacity; Form XObjects; pages of other PDFs as overlays, letterheads and n-up |
| Annotations | Links, named destinations (read and rename), notes, text markup, free text, stamps, ink, squares, circles, lines with endings, polygons, polylines and file attachments, with appearance streams; flattening of one annotation, a page or a document |
| Forms | Creating text, check box, radio, choice, signature and button fields; reading, filling and flattening; fields travel with imported and merged pages; the Acrobat number, percent, date, time and special format actions applied in C# |
| Document features | Viewer preferences, page labels, language, custom properties, file attachments |
| Signatures and PDF/A | PAdES B-B, B-T, B-LT and B-LTA (RSA and ECDSA): RFC 3161 signature and document timestamps, the document security store with OCSP and CRLs; verification with chain building to trust anchors, validity, key usage and revocation; the X.509 checks of ETSI EN 319 102-1: certificate policies with mappings and constraints (RFC 5280 on the RFC 9618 policy graph), name constraints, and the qualified status QCStatements claim; PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e and 4f output validated by veraPDF in CI; Factur-X and ZUGFeRD invoices |
| Content extraction | Text in reading order with column detection and vertical writing; Adobe's CJK CMaps and CID to Unicode tables; built-in encodings of embedded Type 1 and CFF fonts; letters and words with geometry; images with placement, decoded from JPEG, JPEG 2000, CCITT fax and JBIG2 to PNG |
| Optimizer | Opt-in lossy image profile: downsampling by placed resolution, photographs to JPEG, a perceptual SSIM gate, progressive JPEG; an exhaustive Zopfli style Flate encoder. Lossless structural passes: unused resource pruning, content stream compaction, merging of font subsets, deduplication by decoded content, and opt-in linearized output (fast web view), each proven by the equivalence check |
| Scale and performance | Documents read on demand from a seekable stream, with incremental saving; text extraction and HTML conversion profiled and made faster with unchanged output, measured by a benchmark project under `tools/` |
| Command line | Native AOT executables for linux-x64, linux-arm64, win-x64, osx-x64 and osx-arm64 on every GitHub release, with SHA-256 checksums |
| Searchable scans | `DrawInvisibleText` in the core; the `PdfNetStack.Ocr` add-on (PP-OCRv5 on ONNX Runtime, Latin and Arabic) with its models in `PdfNetStack.Ocr.Models`, scans stored in one image or in several (strips, tiles, a text mask over a background) |
| HTML and CSS | `PdfNetStack.Html`: cascade, block, inline, table, flex and grid layout (named lines, dense packing, column flow, subgrid, auto-fit), floats, positioning, paged media with margin boxes, named pages at any depth, running elements, web fonts, variable fonts (`font-weight`, `font-stretch`, `font-style`, `font-optical-sizing` and `font-variation-settings` choosing the instance), tagged PDF |
| Evidence | A public benchmark corpus of 77 openly licensed PDFs, listed by URL and SHA-256 in `eng/corpus/manifest.json` and never committed; `tools/corpus_bench.py` measures lossless and lossy optimization (size, time, peak memory), proves every rewrite again, compares extracted text and images and PDFium renders, weekly and on demand in CI |

## Next

**Text**
- Dictionary hyphenation, once permissively licensed patterns cover the main languages.

**Signatures and PDF/A**
- Qualified status confirmed against EU trusted lists supplied by the application.

**OCR**
- 90 degree page orientation (try both, keep the more confident reading).
- A rasterizer hook for pages that mix vector art and scans, and a command line `ocr` command.

**Optimizer**
- Lossy image options for JPEG 2000, JBIG2 and CCITT images, which the corpus shows dominate
  scanned documents (under 2% saved on the public domain book scans).

**Scale and performance**
- JPEG 2000 decoding into bytes tile by tile instead of 32 bit planes, so 600 dpi color scans fit
  the default decode limit, and faster JBIG2 region composition.
- An optimizer that streams its output, for files larger than memory (reading and incremental
  saving already work on demand).

**Evidence**
- Run the corpus benchmark on every release tag and keep its reports, so a regression in size,
  time or memory shows against the previous release.

The first corpus run (77 documents, 4,045 pages): the lossless profile rewrote 65 and saved
12.1% (196.6 MiB to 172.9 MiB), the lossy profile 27.2%; the other 12 are signed or encrypted
and were left alone. 217 pages rendered by PDFium were identical after the lossless rewrites,
text and decoded images read the same before and after, and no rewrite failed its proof. The
run found three bugs, fixed with synthetic regression tests: a looping `/Prev` chain that hid
the page tree, compaction refusing streams with `%` inside strings, and a JBIG2 work budget too
small for page sized symbols. JPEG 2000 images of 600 dpi book scans exceed the default decode
limit when extracted, and JBIG2 and JPEG 2000 extraction is slow on such pages (about a second
per page); both are listed under scale and performance.

## Out of scope

- Rendering pages to images.
- Running JavaScript or embedding a browser engine.
- Wrappers around external tools. The core packages stay pure managed code; native code is
  allowed only in optional add-ons, such as the ONNX Runtime behind OCR.
- Cloud or hosting integrations. PdfNetStack works on bytes and streams; where they come from is
  up to the application.
