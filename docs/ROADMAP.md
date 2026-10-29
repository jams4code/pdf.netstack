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
| Fonts | Standard 14 fonts; TrueType, OpenType CFF, collections, WOFF and WOFF2; subsetting, Type 0 embedding, ToUnicode, kerning; font collections with CSS weight matching; variable fonts at named instances or any axis values, TrueType (gvar, HVAR, MVAR, cvar) and CFF2, embedded as static fonts |
| Text layout | Unicode line breaking (UAX #14) and bidi (UAX #9) passing the Unicode 16 suites; an OpenType shaper matching HarfBuzz on the fonts we compare, with Unicode normalization, Arabic and the nine Indic scripts; paragraphs with wrapping, alignment, justification (kashida for Arabic, opt in), rich text and fallback |
| Images | JPEG, PNG, GIF, BMP, TIFF (every page), WebP and SVG placement with fit, alignment and opacity; Form XObjects; pages of other PDFs as overlays, letterheads and n-up |
| Annotations | Links, named destinations (read and rename), notes, text markup, free text, stamps, ink, squares, circles, lines with endings, polygons, polylines and file attachments, with appearance streams; flattening of one annotation, a page or a document |
| Forms | Creating text, check box, radio, choice, signature and button fields; reading, filling and flattening; fields travel with imported and merged pages; the Acrobat number, percent, date, time and special format actions applied in C# |
| Document features | Viewer preferences, page labels, language, custom properties, file attachments |
| Signatures and PDF/A | PAdES B-B, B-T, B-LT and B-LTA (RSA and ECDSA): RFC 3161 signature and document timestamps, the document security store with OCSP and CRLs; verification with chain building to trust anchors, validity, key usage and revocation; PDF/A-2b, 2u, 3b and 3u output validated by veraPDF in CI; Factur-X and ZUGFeRD invoices |
| Content extraction | Text in reading order with column detection and vertical writing; Adobe's CJK CMaps and CID to Unicode tables; built-in encodings of embedded Type 1 and CFF fonts; letters and words with geometry; images with placement, decoded from JPEG, JPEG 2000, CCITT fax and JBIG2 to PNG |
| Optimizer | Opt-in lossy image profile: downsampling by placed resolution, photographs to JPEG, a perceptual SSIM gate, progressive JPEG; an exhaustive Zopfli style Flate encoder. Lossless structural passes: unused resource pruning, content stream compaction, merging of font subsets, deduplication by decoded content, and opt-in linearized output (fast web view), each proven by the equivalence check |
| Scale | Documents read on demand from a seekable stream, with incremental saving |
| Searchable scans | `DrawInvisibleText` in the core; the `PdfNetStack.Ocr` add-on (PP-OCRv5 on ONNX Runtime, Latin and Arabic) |
| HTML and CSS | `PdfNetStack.Html`: cascade, block, inline, table, flex and grid layout (named lines, dense packing, column flow, subgrid, auto-fit), floats, positioning, paged media with margin boxes, named pages at any depth, running elements, web fonts, tagged PDF |

## Next

**Core**
- Variable fonts in `PdfFontCollection` and HTML (`font-weight` and `font-variation-settings` choosing an instance).

**Text**
- The Universal Shaping Engine (Southeast Asian and other complex scripts), Khmer, Myanmar, Thai
  and Hangul shapers.
- Dictionary hyphenation, once permissively licensed patterns cover the main languages.

**Signatures and PDF/A**
- Full ETSI EN 319 102-1 validation: certificate policies, name constraints, qualified status.
- PDF/A-1b and PDF/A-4 output.

**OCR**
- A models package, then `PdfNetStack.Ocr` on NuGet.
- Pages whose scan is made of several images.

**Optimizer**
- Lossy image options measured on the public benchmark corpus, once it exists.

**Scale and performance**
- Faster text extraction and HTML layout.
- An optimizer that streams its output, for files larger than memory (reading and incremental
  saving already work on demand).
- Native AOT builds of the command line tool for Linux, Windows and macOS.

**Evidence**
- A public benchmark corpus with redistributable licenses, with size, time, memory and render
  checks in CI on every release.

## Out of scope

- Rendering pages to images.
- Running JavaScript or embedding a browser engine.
- Wrappers around external tools. The core packages stay pure managed code; native code is
  allowed only in optional add-ons, such as the ONNX Runtime behind OCR.
- Cloud or hosting integrations. PdfNetStack works on bytes and streams; where they come from is
  up to the application.
