---
title: Fonts, images and resources in HTML to PDF
description: Load web fonts, images and style sheets when converting HTML to PDF in .NET, with no network access unless you allow it. WOFF2 and SVG, MIT licensed.
since: '1.1'
sidebar:
  label: Fonts and assets
faq:
  - q: Does HTML to PDF download resources from the internet?
    a: Not by default. Without a ResourceLoader only data URIs and files under the base folder load. A loader you supply decides, address by address, what is fetched.
  - q: Which font formats work with @font-face?
    a: TrueType, OpenType, WOFF and WOFF2. Only the glyphs a document uses are embedded.
---

Style sheets, images and fonts load through one rule: `data:` URIs and files under the base
folder, and nothing else unless your code says so.

## Short answer

Convert a file with `ConvertFileToDocument`, or set `BaseUri` to the folder that holds the assets,
and relative URLs in `<link>`, `<img>`, `url()` and `@font-face` resolve there. Fonts come from
`@font-face`, then the `Fonts` collection you pass, then installed fonts; the generic families map
to the standard PDF fonts. To load from the web, pass a `ResourceLoader` that decides per address.

```csharp sample="html-assets.cs" region="loader"
```

![A page converted from HTML: a photograph with rounded corners, a PNG logo, two SVG shapes, a label in Inter and a line of Arabic text](/previews/html-assets.webp)

## Where resources come from

| Setup | What loads |
|---|---|
| No `BaseUri`, no loader (`ConvertToPdf` and `ConvertToDocument` defaults) | `data:` URIs only |
| `ConvertFileToDocument(path)` | `data:` URIs, and files in the HTML file's folder and below |
| `BaseUri` set to a folder | `data:` URIs, and files in that folder and below, after resolving any `..` |
| `ResourceLoader` set | `data:` URIs, and whatever the loader returns for each absolute URL |

With a loader, every URL that is not a `data:` URI goes to it, files included, so the loader
decides for local files too. Return `null` for anything that must not load; the document is laid
out without it. Keep the default for untrusted HTML: converting it then cannot reach internal
services or read files elsewhere on the machine. A single resource larger than 64 MiB is not
loaded.

`@import` and `<link rel="stylesheet">` load style sheets the same way, as UTF-8 unless a byte
order mark says otherwise.

## Web fonts

```html
<style>
  @font-face { font-family: Inter; src: url(fonts/Inter-Regular.woff2) format("woff2") }
  @font-face { font-family: Inter; font-weight: 700; src: url(fonts/Inter-Bold.woff2) format("woff2") }
  @font-face { font-family: "Noto Sans Arabic"; src: url(fonts/NotoSansArabic-Regular.woff2) }
  body { font-family: Inter, "Noto Sans Arabic", sans-serif; font-size: 10pt }
</style>
```

- `src` takes TrueType, OpenType, WOFF and WOFF2 files, and `local()` names an installed font or
  one from the `Fonts` collection. Sources are tried in order; a damaged file is skipped for the
  next one.
- `font-weight` takes one weight or a range, and `font-style` italic or oblique. The face whose
  style and weight match best is used, as CSS matches them.
- Only the glyphs the document draws are embedded, as a subset.

## How a font is chosen

For each family in `font-family`, in order:

1. `@font-face` rules of the document.
2. The `Fonts` option, a `PdfFontCollection` you fill from files or folders.
3. Installed fonts, unless `UseSystemFonts` is false.

The generic families map to the 14 standard PDF fonts, which need no embedding: `serif` (also
`cursive`, `fantasy` and `math`) to Times, `sans-serif` and `system-ui` to Helvetica, and
`monospace` to Courier. Common families that are not found, such as Arial, Roboto, Times New
Roman or Consolas, fall back to the standard font with the same role. A document with no font
named at all uses Times, as browsers default to serif.

Characters the chosen font lacks come from the next family in the list that has them, so
`Inter, "Noto Sans Arabic"` sets Latin text in Inter and Arabic in Noto Sans Arabic. With
`UseSystemFonts` on, installed fonts with wide coverage, such as Segoe UI, Noto Sans and DejaVu
Sans, back up any character still missing.

For output that is identical on every machine, such as on a build server or in a container, set
`UseSystemFonts = false` and ship the fonts with the application, in `@font-face` or in `Fonts`.
See [Fonts and text](/guides/fonts-and-text/) for the font engine itself.

## Images

```html
<img class="photo" src="photo.jpg" alt="Landscape photograph">
<img src="logo.png" alt="Logo" width="90">
<svg width="40" height="40" viewBox="0 0 40 40"><circle cx="20" cy="20" r="18" fill="#2563eb"/></svg>
```

| Format | Notes |
|---|---|
| JPEG | Embedded as is, without recompression |
| PNG | Every color type and bit depth, interlaced or not, transparency kept |
| GIF | The first frame, with its transparent color |
| SVG | Drawn as PDF vectors, sharp at any zoom, without filters, masks or patterns. Works in `<img>`, in CSS backgrounds and inline as `<svg>` |
| WebP | Not supported yet |

Images work in `<img>`, `background-image`, `list-style-image` and `content: url()`. Each image is
written once per document however often it appears. Intrinsic sizes follow the pixel density a
raster image records, else 96 pixels per inch. `object-fit` and `object-position` place an image
in its box.

## Data URIs

`data:` URIs work everywhere a URL does, base64 or percent encoded, so a template can carry its
images and fonts inside the HTML and need no files at all:

```html
<img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='40' height='40'%3E%3Crect width='40' height='40' rx='8' fill='%2314b8a6'/%3E%3C/svg%3E" alt="">
```

## Limits

- WebP images do not load yet.
- SVG filters, masks and patterns are not drawn.
- A resource larger than 64 MiB is not loaded.
- With `UseSystemFonts` off, text needs a supplied font that covers its characters: the standard
  14 fonts cover Western and Central European languages only.

## Related

- [HTML to PDF overview](/guides/html-to-pdf/)
- [Fonts and text](/guides/fonts-and-text/)
- [Images](/guides/images/)
- API: [HtmlConverterOptions](/api/pdfnetstack-html/htmlconverteroptions/), [PdfFontCollection](/api/pdfnetstack-fonts/pdffontcollection/)
