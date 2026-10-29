# Roadmap

Ordered by value per unit of risk. Each item ships behind tests and, where it is not lossless,
behind an explicit option that is off by default.

## PdfSharp parity

Released in 1.0: document editing (pages, import, merge, split, info, outlines) and encryption
(revisions 2 to 6 for reading, AES-256 and AES-128 for writing).

1. **Public object model.** Done for 1.1: `PdfObject` and its subtypes, `PdfDocument.Catalog`,
   `Resolve`, `AddObject`, `GetStreamData`, `PdfPage.Dictionary`. Anything the high level API
   lacks can be done by hand.
2. **Drawing canvas.** Done for 1.1. `PdfGraphics` on any page, new or existing: paths (lines, Bezier curves,
   rectangles, rounded rectangles, ellipses, arcs, polygons), fill and stroke with even-odd or
   nonzero rules, pens (width, caps, joins, dashes), DeviceRGB, DeviceGray and DeviceCMYK colors,
   constant alpha, transforms and a state stack, clipping, linear and radial gradients. Drawing on
   an existing page wraps its content so the page's own graphics state cannot leak into ours.
3. **Fonts and text.** Done for 1.1. The 14 standard fonts with their metrics. TrueType and
   OpenType (CFF, name-keyed and CID-keyed) from .ttf, .otf, collections and WOFF; subsetting
   that keeps glyph ids; Type 0 embedding over CIDFontType2 or CIDFontType0 with Identity-H;
   ToUnicode maps; measurement; pair kerning from GPOS or kern. `PdfFontCollection` for files,
   directories and system fonts with CSS weight matching. Still to do: WOFF2 (needs Brotli on
   .NET Standard), CFF2 variable fonts, named instances of variable fonts, optional hinting
   removal for smaller subsets.
4. **Text layout and complex scripts.** Done: the Unicode line breaking algorithm (UAX #14) and
   bidirectional algorithm (UAX #9), both passing the full Unicode 16 conformance suites; an
   OpenType shaper (GSUB and GPOS, all lookup types, contextual and chained lookups, Arabic
   joining, marks, cursive attachment) that matches HarfBuzz glyph for glyph on the fonts we
   compare; ActualText so shaped and right to left text copies back in logical order; paragraph
   layout (`PdfParagraph`, `PdfTextLayout`) with wrapping, alignment, justification, rich text
   runs, font fallback, CSS line height, soft hyphens and tabs. Later: Unicode normalization in
   the shaper (pointed Hebrew in older fonts), Indic and other complex shapers, kashida
   justification, dictionary hyphenation.
5. **Images and Form XObjects.** Done for 1.1: JPEG passthrough with EXIF orientation, PNG
   decoding (all color types and depths, interlacing, alpha into a soft mask), Form XObjects,
   and pages of any PDF drawn as forms for overlays, watermarks, letterheads and n-up. Still to
   do: GIF, BMP, TIFF and WebP input, aspect fitting helpers, image opacity.
6. **Annotations.** Done for 1.1: links to URLs and pages, named destinations when reading,
   notes, highlight, underline, strikeout, squiggly and free text, all with appearance streams;
   blend modes on the canvas. Still to do: stamps, ink, shapes, file attachments, flattening.
7. **AcroForm.** Done for 1.1: field tree, reading and setting values, appearance generation
   for text, check box, radio and choice fields, flattening. Still to do: creating new fields,
   importing fields together with their pages, JavaScript formatting actions.
8. **Remaining document features.** Done for 1.1: viewer preferences, page layout and mode,
   open action, right to left reading, page labels, language, custom properties, file
   attachments.
9. **Signatures and PDF/A.** PDF/A-2b, 2u, 3b and 3u output: done for 1.1 (checks, sRGB output
   intent, XMP sync, associated files). PAdES B-B signing in an incremental update, RSA and
   ECDSA, visible appearances, verification of every signature: done for 1.1. Next: veraPDF
   validation in CI, Factur-X and ZUGFeRD XMP extension schemas, RFC 3161 timestamps (B-T),
   long term validation data (B-LT, DSS), certificate chain and revocation checks.

## HTML and CSS to PDF

A layout engine in pure C#, in the spirit of WeasyPrint: no browser, no JavaScript, print
quality output, built on the canvas, fonts and text layout above.

1. **Parsing.** An HTML5 tokenizer and tree builder, a CSS Syntax Level 3 parser, selectors
   Level 4 (minus dynamic pseudo classes), cascade, inheritance and computed values, `var()`.
   Done: the HTML parser, the CSS tokenizer and rule parser (`@media`, `@page` with margin boxes,
   `@font-face`, `@import`), selectors with all combinators, attribute operators, the `:nth-*`
   family with `of S`, `:not`, `:is`, `:where`, `:lang`, `:dir`, pseudo-elements and
   specificity. The cascade: origins and `!important`, style attributes, presentational
   hints (`align`, `bgcolor`, `cellpadding` and the rest of the legacy table markup), media
   queries for print with range syntax, shorthand expansion, `var()` with cycle detection and
   an output budget, `calc()`, `min()`, `max()`, `clamp()`, inheritance and computed values in
   CSS px, colors through CSS Color 4 (`oklch()`, `lab()`, `hwb()`). The box tree: display
   types, `display: contents`, `::before`, `::after` and `::marker` with counters, `counters()`,
   `attr()` and quotes, HTML list numbering (`start`, `reversed`, `value`), anonymous blocks,
   inlines split around blocks, table and flex fix-ups, white space collapsing across inline
   boundaries, images with alt text fallback.
2. **Block and inline layout.** Done: block flow with margin collapsing (siblings, parent and
   child, empty blocks), widths and auto margins with min and max, box-sizing, percentage
   heights against definite containers, relative positioning; inline layout over the whole
   paragraph with UAX #14 breaks, UAX #9 reordering, shaping, font fallback per character,
   inline box edges, justification, soft hyphens, `overflow-wrap`, `word-break: break-all`,
   line heights from the strut and `line-height`, every `vertical-align` value; inline
   blocks shrink to fit, replaced elements keep their ratio under `max-width`, outside list
   markers sit on the first baseline. Fonts: `@font-face` (with weight ranges and `local()`),
   caller fonts, installed fonts, generic families as the standard 14 fonts, and installed
   fonts with wide coverage as fallbacks. Floats: shrink-to-fit sizing, placement beside and
   below earlier floats, line boxes shortened or moved down around them, `clear`, formatting
   contexts that contain their floats and sit beside others. Positioning: `relative` offsets,
   `absolute` against the nearest positioned ancestor's padding box (insets, auto sizes,
   static position, centring, `z-index` below or above), `fixed` repeated on every page.
3. **Paged media.** Done: `@page` with `:first`, `:left`, `:right`, named and custom sizes,
   orientation and margins; margin boxes with text and `counter(page)` / `counter(pages)`;
   breaks between lines and blocks, forced with `break-before` / `break-after` (and the
   `page-break-*` aliases), avoided with `break-inside`, orphans and widows; margins truncated
   at breaks. Next: named pages, `string-set`, running elements, table headers repeated per page.
4. **Visual CSS.** Done: background colors (with `background-clip` and the root and body
   background covering the page), solid, dashed, dotted and double borders, text decorations
   in every style, opacity, `overflow` clipping, `visibility`; border radius with overlap
   scaling (elliptical corners, mixed side colors, rounded clipping), background layers with
   images (`background-size`, `-position`, `-repeat`, `-origin`, `-clip`) and linear and
   radial gradients, box shadows with blur and spread, 2D transforms around
   `transform-origin`, `object-fit` and `object-position`, outlines, text shadows.
5. **Flexbox, tables, then grid.** Tables done: the auto width algorithm browsers share (min
   and max per column, spanning cells, percentages, constrained columns, shrink to fit),
   `table-layout: fixed`, row and column spans, header groups first and footer groups last,
   `border-spacing` and `border-collapse` with conflict resolution between cells, rows, groups
   and the table, vertical alignment in cells, captions, `empty-cells`, inline tables, the
   legacy `cellpadding`, `cellspacing` and `border` attributes; rows never split, and header
   rows repeat at the top of every page a table continues on. Flexbox done: rows and
   columns, the reverse directions and right to left rows, wrapping with `gap`, flex base
   sizes and the automatic minimum, flexible lengths with min and max freezing, auto margins,
   `justify-content`, `align-items`, `align-self` (with baselines), `align-content`, `order`,
   inline flex containers. Grid done: track lists with lengths, percentages, `fr`, `auto`,
   `min-content`, `max-content`, `minmax()`, `fit-content()` and `repeat()` (counts and
   `auto-fill`), implicit tracks, placement by lines (negative too), spans and named areas,
   row-major auto-placement, gaps, `justify-items`, `align-items` and the `-self` forms,
   inline grids; pages break between rows no item spans. Next: dense packing, column flow,
   subgrid and named lines.
6. **Resources.** Done: a resource loader callback; without one only data: URIs and files under
   the base folder load, so untrusted HTML cannot reach the network or other files. Style
   sheets from `<link>` and `@import`, images in JPEG and PNG. Next: SVG images.
7. **Links and structure.** Done: links to URLs and internal anchors, bookmarks from
   `bookmark-level` (headings by default), title, author, subject, keywords and language from
   the head. Tagged PDF later.

`PdfNetStack.Chromium` is a separate, optional package for pages that need JavaScript or exact
browser rendering: it drives headless Chrome and passes the result through the optimizer. The core
packages never depend on it.

Fonts: no fonts ship in the MIT packages. The engine uses `@font-face`, fonts passed in by the
caller, and installed system fonts, and falls back to the standard 14 fonts for Latin text.

## OCR, queued

`PdfNetStack.Ocr`, an optional package that makes scanned PDFs searchable: page images are
recognized and an invisible text layer is written over them, so text can be selected, copied
and searched. The core stays pure managed and MIT; the OCR engine and its models live only in
the add-on. Scoping (engine choice, the image decoders scanned PDFs need, the text layer) is in
progress.

## Optimizer, next

**Lossy image profile, continued.** JPEG requantization shipped in 0.2 (`JpegQuality`), with
lossless Huffman optimization on by default. Next: a perceptual gate (SSIM on luminance, which
needs an inverse DCT on top of the existing coefficient decoder) that keeps the original image
when it fails; effective resolution per placement, then downsampling of oversampled photographs;
progressive output where it is smaller than optimized baseline; and conversion of raw continuous
tone images to JPEG, which needs a forward DCT and a classifier that leaves bilevel, indexed,
line art and screenshot-like images alone.

**Unused resource pruning.** Drop fonts, XObjects and graphics states listed in a resource
dictionary but never named by the content that uses it. Needs a content stream tokenizer and
care with inherited page resources and Form XObjects without their own resources.

**Better Flate.** A Zopfli style encoder typically saves another 3 to 8% on Flate data at a
large CPU cost. Opt-in, for archival jobs.

## Later

- Linearized output for fast web view.
- Content stream compaction: whitespace and number normalization inside content streams.
- Font program deduplication across subsets with identical glyph data.
- Streaming parse for files larger than memory.
- Native AOT single file CLI builds for Linux, Windows and macOS on each release.

## Azure operations

- Event Grid to Service Bus to a bounded Container Apps worker, with a dead letter queue.
- Lifecycle tiering and a cost report from the sidecar files.
- Load test and runbook.

## Evidence

- Publish a public benchmark corpus with licenses that allow redistribution, and run size,
  time, memory and render checks against it in CI on every release.
