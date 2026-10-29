---
title: Add images to PDF in C# and .NET
description: Place JPEG, PNG, GIF, BMP, TIFF, WebP and SVG images on PDF pages in C#, fit them to boxes, and reuse pages of other PDFs as watermarks. MIT licensed.
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
| BMP | 1 to 32 bits per pixel, RLE4 and RLE8, bit fields, top down rows, alpha, or an embedded JPEG or PNG. Decoded and Flate compressed; palette images keep their palette |
| TIFF | Strips and tiles, chunky and planar; uncompressed, PackBits, LZW and Deflate with the predictor, CCITT modified Huffman, Group 3 and Group 4, JPEG; bilevel, gray, palette, RGB, CMYK and YCbCr samples of 1 to 16 bits, associated and unassociated alpha, BigTIFF. A page stored as one strip of fax or JPEG data is embedded as is; other pages are decoded and Flate compressed |
| WebP | Lossy and lossless, with alpha, the first frame of an animation. Lossy WebP decodes to the same pixels as libwebp |
| SVG | `PdfSvg`: drawn as PDF vectors, see below |

`PdfImage.Load` takes a path, a stream or a byte array, and reports its `Format`, `PixelWidth`,
`PixelHeight`, `HorizontalResolution`, `VerticalResolution` and `HasTransparency`. Alpha becomes a
soft mask. Decoding is bounded, and an unsupported or damaged file raises a `FormatException`.

```csharp sample="images-fit.cs" region="formats"
```

A TIFF file can hold several pages. `PdfImage.GetPageCount` counts them and `PdfImage.Load(path,
page)` loads one, from 0; other formats have only page 0. The
[TIFF to PDF recipe](/recipes/tiff-to-pdf/) puts every page of a TIFF on its own PDF page.

## Size and placement

`DrawImage(image, x, y, width, height)` stretches the image to the rectangle. `DrawImage(image, x,
y)` draws it at its natural size: its pixels at the resolution the file records, else 96 per inch.
`Width` and `Height` give that natural size in points.

```csharp sample="images-place.cs" region="size"
```

## Fit, alignment and opacity

`DrawImage(image, x, y, width, height, fit, alignment, opacity)` fits an image into a box the way
CSS `object-fit` does, places it inside the box where it does not fill it, clips whatever spills
over, and fades it as a whole on top of its own alpha.

```csharp sample="images-fit.cs" region="fit"
```

![One photo fitted into four boxes with Fill, Contain, Cover and None, then faded to 40 percent](/previews/images-fit.webp)

| `PdfImageFit` | Effect |
|---|---|
| `Fill` | Stretched to the box, aspect ratio ignored |
| `Contain` | Scaled to fit inside the box, aspect ratio kept: the whole image shows |
| `Cover` | Scaled to cover the box, aspect ratio kept: what spills over is clipped |
| `None` | At its natural size, clipped to the box |
| `ScaleDown` | At its natural size, or as `Contain` when that is smaller |

`PdfImageAlignment` places the image: `TopLeft`, `Top`, `TopRight`, `Left`, `Center` (the default),
`Right`, `BottomLeft`, `Bottom` or `BottomRight`.

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

- GIF and WebP animations contribute their first frame only.
- SVG filters, masks, patterns, external references and animation are not drawn.
- `PdfForm.FromPage` does not carry annotations, which are not part of a page's content. It throws
  when the source document does not permit copying its content.

## Related

- [Drawing](/guides/drawing/)
- [Edit pages](/guides/edit-pages/): import whole pages instead of placing them as drawings
- [Extract images](/guides/extract-images/): read images back out of a PDF
- [Convert a TIFF to PDF](/recipes/tiff-to-pdf/)
- API: [PdfImage](/api/pdfnetstack-drawing/pdfimage/), [PdfImageFit](/api/pdfnetstack-drawing/pdfimagefit/), [PdfImageAlignment](/api/pdfnetstack-drawing/pdfimagealignment/), [PdfSvg](/api/pdfnetstack-drawing/pdfsvg/), [PdfForm](/api/pdfnetstack-drawing/pdfform/), [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/)
