# Roadmap

PdfNetStack is a PDF library and nothing else. Work is ordered by value per unit of risk. Every
item ships with tests, and anything that is not lossless sits behind an option that is off by
default.

## Released

**1.0** (on NuGet): the lossless optimizer with equivalence verification, document editing
(open, create, merge, split, import, reorder, rotate and remove pages, document info, bookmarks),
encryption (reads RC4, AES-128 and AES-256; writes AES-128 and AES-256 with permissions), and the
`pdfnetstack` command line tool.

**1.1** (on NuGet):

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
| Signatures and PDF/A | PAdES B-B, B-T, B-LT and B-LTA (RSA and ECDSA): RFC 3161 signature and document timestamps, the document security store with OCSP and CRLs; verification with chain building to trust anchors, validity, key usage and revocation; the X.509 checks of ETSI EN 319 102-1: certificate policies with mappings and constraints (RFC 5280 on the RFC 9618 policy graph), name constraints, and the qualified status QCStatements claim, confirmed against EU trusted lists the application supplies (ETSI TS 119 612 lists with their XML signatures verified, qualification as ETSI TS 119 615 decides it, qualified time stamp services); PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e and 4f output validated by veraPDF in CI; Factur-X and ZUGFeRD invoices |
| Content extraction | Text in reading order with column detection and vertical writing; Adobe's CJK CMaps and CID to Unicode tables; built-in encodings of embedded Type 1 and CFF fonts; letters and words with geometry; images with placement, decoded from JPEG, JPEG 2000, CCITT fax and JBIG2 to PNG |
| Optimizer | Opt-in lossy image profile: downsampling by placed resolution, scans included (JPEG 2000 to JPEG, CCITT and JBIG2 kept bilevel with strokes preserved), photographs (raw or JPEG 2000) to JPEG, a perceptual SSIM gate, progressive JPEG; an exhaustive Zopfli style Flate encoder. Bilevel images coded as CCITT Group 4 (ITU-T T.6) with every pixel proven. Lossless structural passes: unused resource pruning, content stream compaction, merging of font subsets, deduplication by decoded content, and opt-in linearized output (fast web view), each proven by the equivalence check |
| Scale and performance | Documents read on demand from a file, a seekable stream or a stream that cannot seek (copied to memory or a temporary file), with incremental saving; `PdfLoadOptions` (password, decode and image pixel limits, mode, spool threshold and folder), `OpenAsync` without synchronous reads, `PdfDocument` disposable, the input read once, and `ReleaseUnusedObjects` to keep memory flat while reading page by page; progressive saving (each object written as it is serialized, the cross-reference data last) and `SaveAsync` with asynchronous writes only; a streaming optimizer for files larger than memory, which reads the source on demand, writes through a temporary file and runs the full proof on it (a generated 278 MB file: about 50 MB of live memory against 810 MB in memory, the same bytes out); text extraction (3.8 times) and HTML conversion (1.5 to 1.9 times) profiled and made faster with unchanged output, measured by a benchmark project under `tools/`; JPEG 2000 decoded tile by tile and a band of rows at a time straight into 8 bit pixels, so 600 dpi color scans fit the default decode limit; JBIG2 regions combined a byte at a time, page sized text regions drawn straight onto the page, and bilevel images expanded to pixels by lookup |
| Serverless and containers | Native AOT and trimming without warnings for the core, HTML and OCR libraries on linux-x64 and linux-arm64, checked in CI by a smoke app; tests under invariant globalization and tr-TR; robust font directory scanning; bounded, thread safe caches in long lived instances; cold start measured and lazier; AWS Lambda and Azure Functions samples |
| Command line | Native AOT executables for linux-x64, linux-arm64, win-x64, osx-x64 and osx-arm64 on every GitHub release, with SHA-256 checksums |
| Searchable scans | `DrawInvisibleText` in the core; the `PdfNetStack.Ocr` add-on (PP-OCRv5 on ONNX Runtime, Latin and Arabic) with its models in `PdfNetStack.Ocr.Models`, scans stored in one image or in several (strips, tiles, a text mask over a background), pages scanned sideways or upside down found and read turned back, an `IPdfPageRasterizer` hook for pages an application renders; the `pdfnetstack-ocr` command line tool (`PdfNetStack.Ocr.Tool`, also run as `pdfnetstack ocr`) |
| HTML and CSS | `PdfNetStack.Html`: cascade, block, inline, table, flex and grid layout (named lines, dense packing, column flow, subgrid, auto-fit), floats, positioning, paged media with margin boxes, named pages at any depth, running elements, web fonts, variable fonts (`font-weight`, `font-stretch`, `font-style`, `font-optical-sizing` and `font-variation-settings` choosing the instance), tagged PDF |
| Evidence | A public benchmark corpus of 77 openly licensed PDFs, listed by URL and SHA-256 in `eng/corpus/manifest.json` and never committed; `tools/corpus_bench.py` measures lossless and lossy optimization (size, time, peak memory), proves every rewrite again, compares extracted text and images and PDFium renders, weekly, on demand and for every version tag in CI, where the report and a comparison with the previous release's report (`tools/corpus_compare.py`: size, time, memory, new and fixed failures) are attached to the GitHub release |

### Corpus results

The first corpus run (77 documents, 4,045 pages): the lossless profile rewrote 65 and saved
12.1% (196.6 MiB to 172.9 MiB), the lossy profile 27.2%; the other 12 are signed or encrypted
and were left alone. 217 pages rendered by PDFium were identical after the lossless rewrites,
text and decoded images read the same before and after, and no rewrite failed its proof. The
run found three bugs, fixed with synthetic regression tests: a looping `/Prev` chain that hid
the page tree, compaction refusing streams with `%` inside strings, and a JBIG2 work budget too
small for page sized symbols. JPEG 2000 images of 600 dpi book scans exceeded the default decode
limit when extracted, and JBIG2 and JPEG 2000 extraction was slow on such pages (about a second
per page). Both are fixed: a 4812 by 7541 color page now decodes within 256 MiB (about 190 MB at
peak, the 109 MB of pixels included, against about 1 GB before), and JBIG2 text pages come out
of `PdfPageImage.Decode` four to five times faster.

With JPEG 2000, JBIG2 and CCITT images in the lossy profile, a later run saved 29.5% lossy, and
24.0% on the nine scanned documents (4.1% lossless): 37% on a 309 page JBIG2 report, 42% to 88%
on CCITT pages. With palettes (`QuantizeImages`) the lossy profile saves 32.1%, and the `Balanced`
preset, which keeps every image's resolution, 23.4%. The two Internet Archive books, a JPEG 2000 picture over a JBIG2 soft mask on
every page, still gain about 2%, and their JPEG 2000 decoding takes 2 to 4 minutes per book.

## Unreleased

| Area | Scope |
|---|---|
| Hyphenation | Liang's algorithm in the core, opt in through `PdfParagraph.Hyphenator`, with exceptions and limits per language; `PdfNetStack.Hyphenation` with patterns from TeX's hyph-utf8 for English (US and UK), German (reformed spelling), French, Dutch, Spanish, Italian and Portuguese and 21 more sets, all under permissive licenses and read per language on first use; `hyphens: auto` with `lang` in `PdfNetStack.Html` |
| Searchable scans | `PdfNetStack.Ocr.Tesseract`: a second `IOcrEngine` that runs an installed `tesseract` executable, one process per image, with any language Tesseract has data for. It drives the executable rather than a .NET wrapper: `docs/OCR.md` has the evidence and the reasons |

Languages whose only patterns are under LPPL, GPL, LGPL or MPL (among them Russian, Swedish, Czech,
Hungarian, Turkish, Catalan) or give no right to modify (Finnish) stay out, as does German in the
1901 spelling, whose patterns are permissive but not included.

## Next

## Out of scope

- Rendering pages to images.
- Running JavaScript or embedding a browser engine.
- Wrappers around external tools in the core packages, which stay pure managed code. Native code
  and external programs are allowed only in optional add-ons, such as ONNX Runtime and the
  Tesseract engine behind OCR.
- Cloud or hosting integrations. PdfNetStack works on bytes and streams; where they come from is
  up to the application. The samples in `samples/serverless` show the wiring for two hosts.
