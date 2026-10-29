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
| Fonts | Standard 14 fonts; TrueType, OpenType CFF, collections, WOFF and WOFF2; subsetting, Type 0 embedding, ToUnicode, kerning; font collections with CSS weight matching |
| Text layout | Unicode line breaking (UAX #14) and bidi (UAX #9) passing the Unicode 16 suites; an OpenType shaper matching HarfBuzz on the fonts we compare; paragraphs with wrapping, alignment, justification, rich text and fallback |
| Images | JPEG, PNG, GIF and SVG placement; Form XObjects; pages of other PDFs as overlays, letterheads and n-up |
| Annotations | Links, named destinations (read and rename), notes, text markup, free text, stamps, ink, squares, circles, lines with endings, polygons, polylines and file attachments, with appearance streams; flattening of one annotation, a page or a document |
| Forms | Creating text, check box, radio, choice, signature and button fields; reading, filling and flattening; fields travel with imported and merged pages; the Acrobat number, percent, date, time and special format actions applied in C# |
| Document features | Viewer preferences, page labels, language, custom properties, file attachments |
| Signatures and PDF/A | PAdES B-B signing and verification (RSA and ECDSA); PDF/A-2b, 2u, 3b and 3u output |
| Content extraction | Text in reading order with column detection and vertical writing; Adobe's CJK CMaps and CID to Unicode tables; built-in encodings of embedded Type 1 and CFF fonts; letters and words with geometry; images with placement, decoded from JPEG, JPEG 2000, CCITT fax and JBIG2 to PNG |
| Searchable scans | `DrawInvisibleText` in the core; the `PdfNetStack.Ocr` add-on (PP-OCRv5 on ONNX Runtime, Latin and Arabic) |
| HTML and CSS | `PdfNetStack.Html`: cascade, block, inline, table, flex and grid layout, floats, positioning, paged media with margin boxes, named pages, running elements, web fonts, tagged PDF |

## Next

**Core**
- Image input in BMP, TIFF and WebP.
- CFF2 and named instances of variable fonts.

**Text**
- Indic and other complex shapers, Unicode normalization in the shaper, kashida justification,
  dictionary hyphenation.

**Signatures and PDF/A**
- veraPDF validation in CI.
- RFC 3161 timestamps (PAdES B-T), long term validation data (B-LT), certificate chain and
  revocation checks.
- Factur-X and ZUGFeRD metadata.

**HTML and CSS**
- The `page` property on nested elements.
- Grid: dense packing, column flow, subgrid, named lines.

**OCR**
- A models package, then `PdfNetStack.Ocr` on NuGet.
- Pages whose scan is made of several images.

**Optimizer**
- A perceptual quality gate for lossy JPEG, downsampling of oversampled photographs, and raw
  continuous tone images to JPEG.
- Pruning of unused fonts, XObjects and graphics states.
- Content stream compaction and font program deduplication.
- An opt-in Zopfli style Flate encoder for archival jobs.

**Scale and performance**
- Faster text extraction and HTML layout.
- Streaming parse for files larger than memory.
- Linearized output for fast web view.
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
