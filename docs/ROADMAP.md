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
4. **Text layout and complex scripts.** Line breaking (UAX #14), paragraphs with alignment and
   justification, the Unicode bidirectional algorithm (UAX #9), Arabic shaping through the font's
   GSUB and GPOS tables (joining forms, mandatory ligatures, mark positioning).
5. **Images and Form XObjects.** JPEG passthrough, PNG decoding (all color types, alpha into a soft
   mask, interlacing), image placement with aspect fitting. Pages of another PDF drawn as Form
   XObjects: overlays, watermarks, letterheads, n-up.
6. **Annotations.** Links (to a page, a URL, a file), text notes, stamps and highlights, with
   appearance streams.
7. **AcroForm.** Field tree, reading and setting values, appearance generation for text, check
   box, radio and choice fields, flattening, import of fields together with their pages.
8. **Remaining document features.** Viewer preferences, page layout and mode, page labels,
   custom document properties, file attachments.
9. **Signatures and PDF/A.** PAdES baseline signing with incremental save, signature
   validation report, PDF/A-2b and 3b output with XMP sync and embedded output intent.

## HTML and CSS to PDF

A layout engine in pure C#, in the spirit of WeasyPrint: no browser, no JavaScript, print
quality output, built on the canvas, fonts and text layout above.

1. **Parsing.** An HTML5 tokenizer and tree builder, a CSS Syntax Level 3 parser, selectors
   Level 4 (minus dynamic pseudo classes), cascade, inheritance and computed values, `var()`.
2. **Block and inline layout.** The box model, block formatting, inline formatting with the text
   engine (fonts, bidi, Arabic), floats, positioning, margins and collapsing, overflow clipping.
3. **Paged media.** `@page` sizes and margins, margin boxes for running headers and footers,
   page counters, `break-before`, `break-after`, `break-inside`, orphans and widows.
4. **Visual CSS.** Backgrounds and background images, borders with radius, box shadows,
   linear and radial gradients, opacity, transforms, `object-fit`.
5. **Flexbox, tables, then grid.** Table layout with header rows repeated across pages.
6. **Resources.** `@font-face` from files, data URIs and URLs (Google Fonts works), images in
   JPEG, PNG and SVG, a resource loader callback so servers control what may be fetched.
7. **Links and structure.** Hyperlinks and internal anchors as PDF links, headings as bookmarks,
   tagged PDF later.

`PdfNetStack.Chromium` is a separate, optional package for pages that need JavaScript or exact
browser rendering: it drives headless Chrome and passes the result through the optimizer. The core
packages never depend on it.

Fonts: no fonts ship in the MIT packages. The engine uses `@font-face`, fonts passed in by the
caller, and installed system fonts, and falls back to the standard 14 fonts for Latin text.

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
