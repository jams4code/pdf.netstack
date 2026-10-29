---
title: HTML to PDF in C# without a browser
description: Convert HTML and CSS to PDF in .NET with a C# layout engine. Flexbox, grid, tables, paged media and web fonts, no Chromium. MIT licensed.
since: '1.1'
sidebar:
  label: Overview
faq:
  - q: Does PdfNetStack.Html run a browser or JavaScript?
    a: No. It is a layout engine written in C# that parses HTML, applies CSS and lays out pages itself. Scripts are not run, so the HTML must already contain the content to print.
  - q: Does HTML to PDF work on Linux and in containers?
    a: Yes. The package is managed code with no native binaries, so it runs wherever .NET runs. Set UseSystemFonts to false and supply fonts for output that is the same on every machine.
  - q: Is the text in the PDF searchable?
    a: Yes. Text is written as real text with the fonts embedded or referenced, so it selects, copies and searches, and the output is a tagged PDF by default.
---

`PdfNetStack.Html` turns HTML and CSS into PDF with a layout engine written in C#: no browser, no
native binaries, no JavaScript.

## Short answer

Call `HtmlConverter.ConvertToPdf` with an HTML string to get the PDF bytes. The markup is parsed
the way browsers repair it, styled with the CSS cascade, laid out in boxes and split into pages
by the paged media rules.

```csharp sample="html-first.cs" region="short"
```

The package is `PdfNetStack.Html`. It depends on the `PdfNetStack` version it ships with, and runs
on .NET 8 and later and on .NET Framework 4.6.2 and later through .NET Standard 2.0.

## Convert a file with its assets

`ConvertFileToDocument` reads an HTML file and resolves relative URLs against its folder, so style
sheets, images and fonts next to the template load. Files outside that folder do not.

```csharp sample="html-invoice.cs" region="file"
```

![An invoice converted from HTML: logo, addresses in a two column grid, an item table and totals](/previews/html-invoice.webp)

The template behind this invoice uses `@font-face` with WOFF2 files, custom properties, flexbox,
grid, a table with striped rows and a gradient. Its `<title>`, `<meta name="author">` and
`lang` attribute become the PDF title, author and language.

## Three entry points

| Method | Input | Output |
|---|---|---|
| `ConvertToPdf(html, options)` | An HTML string | The PDF as a byte array |
| `ConvertToPdf(html, stream, options)` | An HTML string | The PDF written to a stream |
| `ConvertToDocument(html, options)` | An HTML string | A `PdfDocument` to change before saving |
| `ConvertFileToDocument(path, options)` | An HTML file, read as UTF-8 unless it starts with a byte order mark | A `PdfDocument`, with the file's folder as the base |

All of them take an optional `HtmlConverterOptions`.

## Options

```csharp sample="html-options.cs" region="options"
```

| Option | Default | Notes |
|---|---|---|
| `BaseUri` | null | Where relative URLs point: a folder as a file URI, or a web address when `ResourceLoader` fetches from the network. `ConvertFileToDocument` sets it to the file's location when you leave it null |
| `ResourceLoader` | null | Fetches style sheets, images and fonts by absolute URL; returns null for anything that must not load. Without it only `data:` URIs and files under `BaseUri`'s folder load |
| `PageStyle` | `size: A4; margin: 2cm` | Page size and margins as CSS, applied before the document's own `@page` rules |
| `StyleSheet` | null | A style sheet applied before the document's own, for house styles |
| `Fonts` | null | A `PdfFontCollection` searched before installed fonts |
| `UseSystemFonts` | true | With false, output depends only on the HTML, the fonts you pass and the standard 14 fonts, and is the same on every machine |
| `Bookmarks` | true | Headings become bookmarks, following the `bookmark-level` property |
| `Tagged` | true | Writes a tagged PDF with a structure tree in reading order |

Resource loading and fonts are covered in [Fonts and assets](/guides/html-to-pdf/fonts-and-assets/).

## Work with the result

`ConvertToDocument` returns a `PdfDocument`, so the rest of PdfNetStack applies before the file
is written: attach files, save as PDF/A, sign, encrypt, merge or stamp pages.

```csharp sample="html-options.cs" region="document"
```

This sample writes a PDF/A-3u file with an XML attachment. PDF/A needs every font embedded, which
is why the options above pass Inter in `Fonts` and turn installed fonts off. See
[PDF/A](/guides/pdf-a/), [Signatures](/guides/signatures/) and [Edit pages](/guides/edit-pages/).

To write straight to a stream, as an HTTP response or a blob upload would:

```csharp sample="html-options.cs" region="stream"
```

## Accessible output

By default the output is a tagged PDF (ISO 32000-1, 14.8). Headings, paragraphs, lists, tables
with header and data cells, figures with their `alt` text, links tied to their annotations,
sections and quotes form a structure tree in reading order. Backgrounds, borders, text shadows,
repeated table headers, fixed boxes and page margins are marked as artifacts, so screen readers
skip them. The catalog is marked and viewers show the document title. Set `Tagged = false` to
leave the structure out.

## Links and bookmarks

Links to web addresses and to anchors in the same document become PDF links. Headings `h1` to
`h6` become nested bookmarks, from the `bookmark-level` property the default style sheet sets on
them; set `Bookmarks = false` to write none.

## What it supports

- **HTML.** A tolerant parser that repairs markup as browsers do, and the legacy presentational
  attributes old templates rely on (`align`, `bgcolor`, `width`, `cellpadding`, `cellspacing`,
  `border`, `font`).
- **CSS.** The cascade with `!important`, inheritance, `var()`, Selectors Level 4, `@media print`,
  `@import`, `@font-face`, `@page`, `calc()`, `min()`, `max()`, `clamp()` and CSS Color 4.
- **Layout.** Block, inline, table, flex and grid layout (with named lines, dense packing,
  `subgrid`, `auto-fill` and `auto-fit`), floats, relative, absolute and fixed positioning in
  `z-index` stacking order, inline blocks, lists, counters and generated content.
- **Paged media.** Page sizes and margins, margin boxes, page counters, named pages, running
  headers and footers, forced and avoided breaks, repeated table headers. See
  [Paged media](/guides/html-to-pdf/paged-media/).
- **Text.** Unicode line breaking, right to left and Arabic text with contextual shaping, Indic
  scripts, kerning, ligatures and fallback fonts per character.

The full list, module by module, is on [CSS support](/guides/html-to-pdf/css-support/).

## Safe for untrusted HTML

Without a `ResourceLoader`, nothing is fetched from the network and only files under the base
folder are read, so converting HTML from users cannot reach internal services or other files.
Parsing, styling and layout are bounded: nesting depth, selector complexity, `var()` expansion,
`@import` chains, table spans, grid tracks and background tiling all have limits, so hostile
markup ends in a finite document rather than a hang or a crash.

## Limits

- No JavaScript: pages must be rendered HTML, not built in the browser.
- No multi-column layout or vertical writing modes yet.
- Images: JPEG, PNG, GIF, BMP, TIFF and WebP (the first frame of animations), and SVG drawn as
  vectors without filters, masks or patterns.
- The `page` property takes no effect inside flex, grid and table containers. An inline running
  element shows on every page rather than from where it stands.
- Variable fonts draw their default instance: `font-weight` does not choose an instance yet.
- Box shadow blur is approximated, `text-shadow` blur is not drawn, and group opacity is applied
  per element.

## Related

- [Paged media](/guides/html-to-pdf/paged-media/): headers, footers, page numbers and breaks
- [Fonts and assets](/guides/html-to-pdf/fonts-and-assets/): `@font-face`, images and resource loading
- [CSS support](/guides/html-to-pdf/css-support/): what is supported, module by module
- [Fonts and text](/guides/fonts-and-text/): the text engine underneath
- API: [HtmlConverter](/api/pdfnetstack-html/htmlconverter/), [HtmlConverterOptions](/api/pdfnetstack-html/htmlconverteroptions/)
