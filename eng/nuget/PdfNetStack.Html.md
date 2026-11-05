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
    SaveOptions = new PdfSaveOptions { Compression = CompressionEffort.Balanced, VerifyOptimization = false },
};
HtmlConverter.ConvertToPdf(html, outputStream, options);
```

`ConvertToDocument` returns a `PdfDocument`, so the result can be signed, encrypted, saved as
PDF/A, merged or stamped with the rest of PdfNetStack before it is written.

`ConvertToPdf` saves with `SaveOptions`. Without them it optimizes the file as
`PdfDocument.Save` does but skips the optimizer's equivalence proof
(`PdfSaveOptions.VerifyOptimization`), which reads both files back: the converter draws every
page itself, and the bytes are the same whenever the proof would pass.
`SaveOptions = PdfSaveOptions.Default` keeps the proof. `CompressionEffort.Balanced`, as above,
saves faster for slightly larger files.

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

Named pages give a section its own page type, laid out at that page's width:

```css
@page wide { size: A4 landscape }
.annex { page: wide }
```

Named strings carry text from the document into the margins, for running headers:

```css
h1 { string-set: chapter content(text) }
@page { @top-left { content: string(chapter) } }
```

`string()` takes `first` (the default), `start`, `last` or `first-except`.

Running elements put any content in the margins, a logo and a table as much as text:

```css
header { position: running(header) }
@page { @top-center { content: element(header) } }
```

Each page shows the element that stands on it, else the last one before, so chapter headers follow
the chapters; `element()` takes `first`, `start`, `last` or `first-except` as `string()` does, and
`counter(page)` and `counter(pages)` in a running element's generated content show the page's
numbers. A
margin box alone on its side takes the whole side, beside others its share, so a running header
spans the page.

Table header rows repeat at the top of every page a table continues on. `position: fixed`
boxes repeat on every page, for letterheads and watermarks.

## What it supports

- **Accessibility.** Output is a tagged PDF: headings, paragraphs, lists, tables, figures with
  their `alt` text and links form a structure tree in reading order, and decoration, repeated
  table headers and page margins are marked as artifacts, so screen readers read the document and
  skip the furniture. `Tagged = false` turns it off.

- **HTML.** A tolerant parser that repairs markup as browsers do: implied elements, misnested
  tags, tables, character references. The legacy attributes old templates rely on (`align`,
  `bgcolor`, `width`, `cellpadding`, `cellspacing`, `border`, `font`) are honoured.
- **CSS.** The cascade with `!important`, inheritance and `var()`; Selectors Level 4 (`:is`,
  `:not`, `:where`, `:nth-child(… of S)`, attribute operators); `@media print`, `@import`,
  `@font-face`, `@page`; `calc()`, `min()`, `max()`, `clamp()`; colors up to CSS Color 4
  (`oklch()`, `lab()`, `hwb()`).
- **Layout.** Block flow with margin collapsing, inline layout with Unicode line breaking,
  justification, soft hyphens and `hyphens: auto` (with `HtmlConverterOptions.Hyphenators` and the
  `PdfNetStack.Hyphenation` package), floats and `clear`, relative, absolute and fixed
  positioning, tables (spans, both border models, fixed and automatic widths), flexbox (wrap,
  gap, grow and shrink, alignment, order), grid (fr tracks, `minmax()`, `repeat()` with
  `auto-fill` and `auto-fit`, named areas and lines, auto-placement by rows or columns, dense
  packing, `subgrid`, the `grid` and `grid-template` shorthands), inline blocks, list markers
  and counters, `::before` and `::after`.
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

Sizes are bounded too. Computed lengths (margins, padding, borders, widths, line heights, letter
and word spacing) are clamped to a million px either way, percentages to a million percent,
font sizes to 10000px as in browsers, unitless line heights to 1000, and page sizes to PDF's
largest page (14400 points a side). A conversion produces at most `MaxPages` pages (10000 by
default); a document that needs more ends with `HtmlConversionException` rather than using
memory without bound. Raise `MaxPages` for documents that are genuinely that long.

## Fonts

No fonts ship in the package. The engine uses `@font-face` (TrueType, OpenType, WOFF and
WOFF2), fonts you pass in `Fonts`, and installed fonts. The generic families map to the standard PDF fonts,
so a document that asks only for `serif`, `sans-serif` or `monospace` needs no embedded font.
Installed fonts with wide coverage stand in for characters the chosen fonts lack; they are
looked up only when a character needs them, so a document the chosen fonts cover never scans
the installed fonts for fallbacks.

Variable fonts draw at the instance the style asks for, as CSS Fonts 4 sets out: `font-weight`,
`font-stretch` (or `font-width`) and `font-style` pick the weight, width, italic and slant,
within the ranges of the `@font-face` descriptors (`font-weight: 100 900`,
`font-stretch: 75% 125%`, `font-style: oblique 0deg 15deg`); `font-optical-sizing: auto` (the
default) sets the opsz axis to the font size in CSS px; `font-variation-settings` sets any axis
last. The `@font-face` descriptors `font-variation-settings` and `font-named-instance` apply too.
Each location is embedded once however many elements use it.

Since sizes and axis values come from the document, two limits keep a page from asking for an
embedded font per value. Axis values snap to a grid before an instance is made: whole units for
wght, wdth and opsz, half degrees for slnt, tenths for ital, and a hundredth of the axis's range
for other axes. And one conversion makes at most 32 instances of each font; past that, text uses
the nearest instance already made. Values that are not finite numbers are ignored, and weights,
widths (up to 1000%), oblique angles and optical sizes (up to 1000) are clamped to their ranges,
so no style value stops a conversion.

## Limits

- No JavaScript: pages must be rendered HTML, not built in the browser.
- No multi-column layout or vertical writing modes yet.
- Images: JPEG, PNG, GIF (the first frame), BMP, TIFF (the first page), WebP (the first frame)
  and SVG (drawn as vectors, without filters, masks or patterns).
- The `page` property breaks between blocks of the block flow. Inside a flex, grid or table
  container it takes no effect, and the container stays on one page type. An inline running
  element shows on every page rather than from where it stands.
- Box shadow blur is approximated, `text-shadow` blur is not drawn, and group opacity is applied
  per element.

## Links

- Project and documentation: https://github.com/jams4code/pdf.netstack
- Roadmap: https://github.com/jams4code/pdf.netstack/blob/main/docs/ROADMAP.md
- Changelog: https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md

By Jamal Abdelkhalek, [JADEV](https://jadev-corp.com). MIT license.
