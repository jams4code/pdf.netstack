# PdfNetStack.Html

HTML and CSS to PDF for .NET, in pure C#: no browser, no native binaries, no JavaScript, MIT
licensed. Built on [PdfNetStack](https://www.nuget.org/packages/PdfNetStack).

```csharp
using PdfNetStack.Html;

byte[] pdf = HtmlConverter.ConvertToPdf("<h1>Invoice 42</h1><p>Total: <b>1,210.00 EUR</b></p>");
File.WriteAllBytes("invoice.pdf", pdf);
```

## Install

```bash
dotnet add package PdfNetStack.Html
```

Runs on .NET 8 and later, and on .NET Framework 4.6.2+ through .NET Standard 2.0.

## Usage

```csharp
using PdfNetStack;
using PdfNetStack.Fonts;
using PdfNetStack.Html;

// A file: relative URLs (style sheets, images, fonts) resolve against its folder.
PdfDocument doc = HtmlConverter.ConvertFileToDocument("report.html");
doc.Save("report.pdf");

// A string, with options.
var myFonts = new PdfFontCollection();
myFonts.AddDirectory("fonts");
var options = new HtmlConverterOptions
{
    BaseUri = new Uri(@"C:\templates\"),               // where relative URLs point
    PageStyle = "size: A4; margin: 2cm",               // before the document's @page rules
    StyleSheet = "body { font-family: Inter, sans-serif }",
    Fonts = myFonts,                                   // a PdfFontCollection of your own
    UseSystemFonts = false,                            // same output on every machine
};
HtmlConverter.ConvertToPdf(html, outputStream, options);
```

`ConvertToDocument` returns a `PdfDocument`, so the result can be signed, encrypted, saved as
PDF/A, merged or stamped with the rest of PdfNetStack before it is written.

## Paged media

```css
@page {
  size: A4;
  margin: 2cm 2cm 2.5cm;
  @top-right { content: "Quarterly report"; font-size: 9pt }
  @bottom-center { content: "Page " counter(page) " of " counter(pages) }
}
@page :first { @top-right { content: none } }
h2 { break-before: page }
tr, figure { break-inside: avoid }
```

Table header rows repeat at the top of every page a table continues on. `position: fixed`
boxes repeat on every page, for letterheads and watermarks.

## What it supports

- **HTML.** A tolerant parser that repairs markup as browsers do: implied elements, misnested
  tags, tables, character references. The legacy attributes old templates rely on (`align`,
  `bgcolor`, `width`, `cellpadding`, `cellspacing`, `border`, `font`) are honoured.
- **CSS.** The cascade with `!important`, inheritance and `var()`; Selectors Level 4 (`:is`,
  `:not`, `:where`, `:nth-child(… of S)`, attribute operators); `@media print`, `@import`,
  `@font-face`, `@page`; `calc()`, `min()`, `max()`, `clamp()`; colors up to CSS Color 4
  (`oklch()`, `lab()`, `hwb()`).
- **Layout.** Block flow with margin collapsing, inline layout with Unicode line breaking,
  justification and soft hyphens, floats and `clear`, relative, absolute and fixed
  positioning, tables (spans, both border models, fixed and automatic widths), flexbox (wrap,
  gap, grow and shrink, alignment, order), inline blocks, list markers and counters,
  `::before` and `::after`.
- **Text.** Right to left and Arabic with contextual shaping that matches HarfBuzz, bidi
  reordering, kerning, ligatures, fallback fonts per character, every `vertical-align`.
- **Painting.** Backgrounds with images and linear or radial gradients, rounded borders in
  every style, box shadows, outlines, 2D transforms, opacity, `object-fit`, text decorations
  and shadows.
- **Output.** Links to web pages and to anchors in the document, bookmarks from headings, title,
  author, subject, keywords and language from the head. Text is real text: searchable and
  copyable.

## Resources and safety

Without a `ResourceLoader`, only `data:` URIs and files under `BaseUri`'s folder load; nothing
is fetched from the network. That keeps converting untrusted HTML from reaching internal
services or reading other files. To allow web resources, pass a loader and decide per address:

```csharp
var http = new HttpClient();
var options = new HtmlConverterOptions
{
    BaseUri = new Uri("https://example.com/invoices/"),
    ResourceLoader = uri => uri.Host == "example.com" ? http.GetByteArrayAsync(uri).Result : null,
};
```

Parsing, styling and layout are bounded: nesting depth, selector complexity, `var()`
expansion, table spans and background tiling all have limits, so hostile markup ends in a
finite document rather than a hang or a crash.

## Fonts

No fonts ship in the package. The engine uses `@font-face` (TrueType, OpenType, WOFF), fonts
you pass in `Fonts`, and installed fonts. The generic families map to the standard PDF fonts,
so a document that asks only for `serif`, `sans-serif` or `monospace` needs no embedded font.
Installed fonts with wide coverage stand in for characters the chosen fonts lack.

## Limits

- No JavaScript: pages must be rendered HTML, not built in the browser.
- No CSS grid, multi-column layout or vertical writing modes yet.
- Images: JPEG and PNG. SVG, GIF and WebP do not load yet.
- `@page` named pages, `string-set` and running elements are not supported yet; margin boxes
  take text and page counters.
- Box shadow blur is approximated, `text-shadow` blur is not drawn, and group opacity is applied
  per element.
- WOFF2 fonts are not read.

## Links

- Project and documentation: https://github.com/jams4code/pdf.netstack
- Roadmap: https://github.com/jams4code/pdf.netstack/blob/main/docs/ROADMAP.md
- Changelog: https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md

By Jamal Abdelkhalek, [JADEV](https://jadev-corp.com). MIT license.
