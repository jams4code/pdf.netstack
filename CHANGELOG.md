# Changelog

## Unreleased

- New package `PdfNetStack.Html`: HTML and CSS to PDF in pure C#, without a browser.
  `HtmlConverter.ConvertToPdf`, `ConvertToDocument` and `ConvertFileToDocument`, configured with
  `HtmlConverterOptions` (base address, resource loader, fonts, page style, extra style sheet,
  bookmarks).
  - HTML parsed the way browsers repair it, with the legacy presentational attributes.
  - CSS: cascade, inheritance, `var()`, Selectors Level 4, media queries for print, `@import`,
    `@font-face`, `@page` with margin boxes, `calc()` and friends, CSS Color 4.
  - Layout: blocks with margin collapsing, inline text with Unicode line breaking, bidi and
    shaping, floats, relative, absolute and fixed positioning, tables in both border models,
    flexbox, grid with fr tracks, `minmax()`, `repeat()` and named areas, inline blocks,
    lists, counters and generated content.
  - Paged media: page sizes and margins, `:first`, `:left` and `:right`, running headers and
    footers with page counters, forced and avoided breaks, orphans and widows, table headers
    repeated on every page.
  - Painting: backgrounds with images and gradients, rounded borders, shadows, outlines,
    transforms, `object-fit`, decorations; links and bookmarks; document metadata from the head.
  - Safe by default for untrusted HTML: no network access without a loader, files only from
    the base folder, and limits on nesting, selectors, `var()` expansion, spans and tiling.
  - It depends on the exact `PdfNetStack` version it ships with.
- The `PdfNetStack` and `PdfNetStack.Azure` packages are checked against 1.0.0 on every pack, so
  a change that would break code built against the published version fails the build.
- Public object model: `PdfObject`, `PdfNull`, `PdfBoolean`, `PdfNumber`, `PdfName`,
  `PdfString`, `PdfArray`, `PdfDictionary`, `PdfStream` and `PdfReference` in
  `PdfNetStack.Objects`, with `PdfDocument.Catalog`, `Resolve`, `AddObject` and
  `GetStreamData`, and `PdfPage.Dictionary`, for features the high level API does not cover.
- Drawing: `PdfPage.CreateGraphics()` returns a `PdfGraphics` canvas (top left origin, points)
  for new and existing pages, in front of or behind the existing content. `PdfPath` with lines,
  Bezier curves, arcs, ellipses, polygons and rounded rectangles; `PdfPen`, `PdfColor` (gray, RGB,
  CMYK, opacity), solid, linear and radial gradient brushes; transforms, saved states, clipping.
  Only changed graphics state is written, and shared page resources are never modified.
- Text with the 14 standard fonts: `PdfStandardFont`, `PdfGraphics.DrawString` (fill with any
  brush including gradients, stroke with a pen, or both) and `PdfFont.MeasureString`, with
  ascent, descent, line height, cap height, x-height and underline metrics. Latin text uses
  WinAnsiEncoding; up to 37 further characters per font and document (Central European letters,
  the minus sign) get codes through /Differences. Symbol and ZapfDingbats take their Unicode
  characters. Control and format characters are skipped; a character the font lacks throws.
- Embedded fonts: `PdfOpenTypeFont.Load` reads TrueType and OpenType (CFF) fonts from .ttf,
  .otf, .ttc/.otc collections and WOFF files. Drawn text embeds a subset of the glyphs used as a
  Type 0 font (CIDFontType2 or CIDFontType0C), with a W array and a ToUnicode map, so text is
  searchable and copyable in any script. Pair kerning from GPOS (formats 1 and 2, extension
  lookups) or the kern table. Line metrics follow browsers (typo metrics when the font sets
  USE_TYPO_METRICS, else hhea). Fonts whose fsType forbids embedding are refused; damaged fonts
  raise `FontFormatException`.
- `PdfFontCollection`: add fonts, files and directories, or use `PdfFontCollection.System` for
  installed fonts; `Find(family, weight, italic)` matches as CSS does. Scanning reads only the
  name, OS/2 and head tables; fonts load on first use.
- `PdfFont` gains `StrikeoutPosition` and `StrikeoutThickness`.
- Complex text: `DrawString` and `MeasureString` run the Unicode bidirectional algorithm and an
  OpenType shaper. Arabic and Persian join and take their contextual forms, ligatures form
  (lam-alef, fi), vowel marks sit on their letters, mixed right to left and left to right text is
  ordered correctly with mirrored brackets, and kerning follows GPOS or the kern table as
  HarfBuzz applies them. Shaped and right to left text carries ActualText, so it copies and
  searches in logical order.
- Paragraph layout: `PdfParagraph` (rich text spans of `PdfTextStyle`), `PdfTextLayout` and
  `PdfGraphics.DrawText`. Wrapping at Unicode line break opportunities, start, end, left, right,
  center and justified alignment, right to left paragraphs, fallback fonts per style, letter and
  word spacing, underline and strikethrough, first line indent, CSS line height, soft hyphens,
  tabs, and drawing a range of lines for text split across pages.
- Images: `PdfImage.Load` for JPEG (embedded without recompression, EXIF orientation, Adobe
  CMYK, ICC profiles, JFIF density) and PNG (all color types and bit depths, Adam7 interlacing,
  tRNS as color key or soft mask, alpha channels as soft masks, iCCP, pHYs; simple PNGs keep
  their compressed data with PNG predictors). `PdfGraphics.DrawImage` at natural or given size.
  Each image is written once per document.
- Form XObjects: `PdfForm.Create` for reusable drawings, `PdfForm.FromPage` to place any page of
  any document (rotation honored, resources imported once), `PdfGraphics.DrawForm`, and
  `PdfGraphics.Form` for drawing into a form.
- Annotations: `PdfPage.Annotations` reads every annotation (kind, rectangle, contents, link
  address, destination page including named destinations) and adds web and page links, notes,
  text markup (highlight, underline, strikeout, squiggly) and free text, each with an appearance
  stream. `PdfGraphics.AddLink` maps a canvas rectangle through the current transform.
- `PdfGraphics.BlendMode`: the 16 PDF blend modes, saved and restored with the graphics state.
- Interactive forms: `PdfDocument.Form` lists every field (`PdfTextField`, `PdfCheckBoxField`,
  `PdfRadioButtonField`, `PdfChoiceField`, `PdfPushButtonField`, `PdfSignatureField`) with fully
  qualified names, flags, options and widgets. Setting values regenerates appearances (borders,
  backgrounds, DA font and color, auto size, multiline, comb, password) and drops XFA.
  `PdfAcroForm.Flatten` stamps the appearances into the pages and removes the form.
- Document features: `PdfDocument.Viewer` (page layout, page mode, window options, right to left
  reading, opening page and zoom), `PdfDocument.PageLabels` (roman, decimal and letter ranges
  with prefixes, label lookup), `PdfDocument.Attachments` (embed, read, replace, remove files)
  and `PdfDocument.Language`.
- PDF/A: `PdfSaveOptions.Conformance` writes PDF/A-2b, 2u, 3b or 3u. Problems that cannot be
  repaired are listed together in one exception; the output gets an sRGB output intent with a
  profile the library generates, XMP metadata synchronized with the document information,
  printable annotations, and PDF/A-3 associated file entries for attachments.
- Digital signatures: `PdfSigner.Sign` adds a PAdES B-B signature (ETSI.CAdES.detached, SHA-256,
  384 or 512, RSA or ECDSA, ESS signing certificate v2) in an incremental update, matching the
  file's cross-reference style, optionally with a visible appearance. `PdfSigner.Verify` checks
  each signature's byte range, digest and signature and reports the signer certificate. The CMS
  structure is encoded by the library; no new dependency.
- Packages now include `THIRD-PARTY-NOTICES.md`, which carries Adobe's notice for the standard
  font metrics.

## 1.0.0 (2026-10-28)

First release on NuGet: `PdfNetStack`, `PdfNetStack.Tool` and `PdfNetStack.Azure`.

- JPEG images: lossless re-encoding on by default (`OptimizeJpegImages`). Huffman tables are
  rebuilt for each image, progressive and restart marked JPEGs become baseline, and segments PDF
  readers ignore are dropped. Coefficients are untouched and verification compares them one by one.
- Opt-in JPEG requantization (`JpegQuality`, CLI `--jpeg-quality`): coarser tables at a libjpeg
  quality, never finer than the image's own, applied in the coefficient domain so the color model
  cannot change. Soft masks stay lossless. Verification checks each coefficient is exactly the
  requantized source value. `OptimizationStatistics.JpegImagesRecompressed` and
  `JpegImagesRequantized`; the Azure sidecar records the quality, and lossy blobs carry
  `jpegquality` metadata.
- Document editing API: `PdfDocument.Open`, `Create`, `Save`, `Merge`, `Split`; pages can be
  added, inserted, removed, moved, rotated and imported from other documents with shared
  resources copied once; `Info` for metadata with Unicode and date support; `Outlines` for
  reading, editing and writing bookmarks while keeping their original styling and actions.
- CLI: `merge`, `split`, `extract`, `rotate`, `meta`, `encrypt`, `decrypt`.
- Encryption: open documents protected with any standard security handler revision (RC4, AES-128,
  AES-256), with user or owner password; write AES-256 or AES-128 with permissions; documents
  opened with the owner password keep their protection on save. `PdfDocument.Security`.
- .NET Standard 2.0 target: .NET Framework 4.6.2+, Mono and Unity. Tests run on .NET Framework 4.8.
- Contribution guide with an AI-assisted contribution policy, and a code of conduct.
- Hardening from review: bounded resolution depth, bounded object regions, predictor row
  clamping, identity checks in deduplication and verification, per document decode budget.
- JPEG reader hardening: oversubscribed Huffman tables are refused before use, at most 64 scans
  per image, frames that claim more blocks than their data can code are refused as decompression
  bombs, JFIF and Adobe segments are kept once, and an image with a component no scan covers is
  left untouched.
- Verification accepts requantized coefficients only on the images the optimizer requantized;
  every other JPEG must stay coefficient identical.
- Requantization follows the Adobe marker over `/ColorTransform` when choosing chroma tables, as
  ISO 32000-1 specifies.
- NuGet packages: icon, a readme per package, package validation.

## 0.1.0

First public version.

- Lossless optimizer: unreachable object removal, object streams, compressed xref streams,
  stream recompression, PNG prediction for raw images, iterative deduplication, compact syntax.
- Equivalence verifier on every rewrite, public as `PdfEquivalence.FindDifference`.
- Refuses encrypted, signed, incrementally updated and damaged files unless allowed.
- PDF/A-1 detection with classic xref output.
- `pdfnetstack` command line tool: optimize, batch, inspect, verify.
- `PdfNetStack.Azure`: Blob Storage pipeline with content-addressed output and provenance sidecar.
