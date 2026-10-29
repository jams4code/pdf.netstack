---
title: Add images to PDF in C# and .NET
description: Place JPEG, PNG, GIF and SVG images on PDF pages in C#, and reuse pages of other PDFs as watermarks, letterheads and n-up sheets. MIT licensed.
since: '1.1'
sidebar:
  label: Images
faq:
  - q: Does PdfNetStack recompress JPEG images when it places them?
    a: No. A JPEG is copied into the PDF as is, and its EXIF orientation is honored so photos stand upright.
  - q: Does SVG stay vector?
    a: Yes. PdfSvg draws shapes, paths, gradients, clipping and text as PDF vectors, so the drawing stays sharp at any zoom. Filters, masks and patterns are not drawn.
---

Images, vector drawings and pages of other PDFs, placed on a page wherever you want them.

## Short answer

Load an image with `PdfImage.Load` or an SVG drawing with `PdfSvg.Load`, then place it with
`DrawImage` or `DrawSvg` on a page's canvas. Positions and sizes are points from the top left of
the page. Each image is written once per document, however often it is drawn.

```csharp sample="images-place.cs" region="place"
```

![A photo, a transparent PNG logo and an SVG badge placed on a page](/previews/images.webp)

## Image formats

| Format | How it is embedded |
|---|---|
| JPEG | Copied without recompression. EXIF orientation, Adobe CMYK, ICC profiles and JFIF density are honored |
| PNG | All color types and bit depths, interlaced or not. Simple PNGs keep their compressed data; an alpha channel or tRNS transparency becomes a soft mask. ICC profiles and pixel density are kept |
| GIF | GIF 87a and 89a, the first frame, interlaced or not, with its transparent color as a soft mask |
| SVG | `PdfSvg`: drawn as PDF vectors, see below |

`PdfImage.Load` takes a path, a stream or a byte array, and reports `PixelWidth`, `PixelHeight`,
`HorizontalResolution`, `VerticalResolution` and `HasTransparency`. An unsupported file raises a
`FormatException`.

## Size and placement

`DrawImage(image, x, y, width, height)` stretches the image to the rectangle. `DrawImage(image, x,
y)` draws it at its natural size: its pixels at the resolution the file records, else 96 per inch.
`Width` and `Height` give that natural size in points.

```csharp sample="images-place.cs" region="size"
```

Images follow the current transform and clip of the canvas, so `RotateTransform`, `ScaleTransform`
and `IntersectClip` work on them as on shapes. See [Drawing](/guides/drawing/).

## SVG

`PdfSvg.Load` reads a file, a stream or bytes, and `PdfSvg.Parse` reads a string. `DrawSvg(svg, x,
y, width, height)` scales the drawing into the rectangle; its `viewBox` and
`preserveAspectRatio` decide how it fits.

Supported: shapes, paths with arcs, groups, `use` and `symbol`, nested viewports, transforms, solid
colors, linear and radial gradients, clip paths, opacity, dashes, text in the standard fonts,
embedded JPEG and PNG images, and `<style>` rules with type, class and id selectors. Doctypes are
skipped, so entity expansion cannot be abused, and size, depth and `use` nesting are capped.

## Watermarks with a reusable drawing

`PdfForm.Create` makes a reusable drawing (a form XObject). Draw it once, then place it on every
page: each page refers to the same object.

```csharp sample="images-overlay.cs" region="watermark"
```

![A report page with a translucent diagonal CONFIDENTIAL stamp](/previews/images-watermark.webp)

## Pages of other PDFs: n-up and handouts

`PdfForm.FromPage` turns any page of any document into a drawing you can scale and place. It shows
the page's visible area (its crop box), upright as the page's rotation shows it, and copies its
fonts and images into the target document once.

```csharp sample="images-overlay.cs" region="nup"
```

![Four pages of a manual laid out on one landscape sheet](/previews/images-nup.webp)

## Letterheads and stationery

Draw a page of a letterhead PDF underneath the content of every page with the background layer:

```csharp sample="images-overlay.cs" region="letterhead"
```

## Limits

- Image input is JPEG, PNG, GIF and SVG. BMP, TIFF and WebP are on the [roadmap](/roadmap/).
- GIF animations contribute their first frame only.
- SVG filters, masks, patterns, external references and animation are not drawn.
- `PdfForm.FromPage` does not carry annotations, which are not part of a page's content. It throws
  when the source document does not permit copying its content.

## Related

- [Drawing](/guides/drawing/)
- [Edit pages](/guides/edit-pages/): import whole pages instead of placing them as drawings
- [Extract images](/guides/extract-images/): read images back out of a PDF
- API: [PdfImage](/api/pdfnetstack-drawing/pdfimage/), [PdfSvg](/api/pdfnetstack-drawing/pdfsvg/), [PdfForm](/api/pdfnetstack-drawing/pdfform/), [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/)
