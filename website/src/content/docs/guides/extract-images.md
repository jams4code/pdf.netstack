---
title: Extract images from PDF files in C#
description: List every image a PDF page draws in .NET, with placement and resolution, save JPEGs as stored and decode the rest to PNG. Pure C#, MIT licensed.
since: '1.1'
sidebar:
  label: Extract images
faq:
  - q: Are JPEG images re-encoded when extracted?
    a: No. GetEncodedData returns a DCTDecode image as the JPEG file stored in the PDF, byte for byte once general compression is undone.
  - q: Which image encodings can be decoded to pixels?
    a: Raw samples of 1 to 16 bits, JPEG, CCITT fax (Group 3 and 4) and JBIG2. JPEG 2000 images come back encoded.
---

Find the images on a page, where they land and at what resolution, and save them as files.

## Short answer

`page.GetImages()` lists the images a page draws, in drawing order, with their placement.
Save a JPEG as stored with `GetEncodedData()`, or call `Decode()` to get 8 bit gray or RGB pixels
and save them with `SavePng`.

```csharp sample="extract-images-png.cs" region="save"
```

## What a page draws

`GetImages()` walks the page content: image XObjects, inside form XObjects too, and inline
images. The same XObject drawn twice appears twice, once per placement.

```csharp sample="extract-images-png.cs" region="list"
```

```text
300x100, raw samples, 225 dpi, at 72,770
640x400, DCTDecode, 288 dpi, at 72,22
```

| `PdfPageImage` | Meaning |
|---|---|
| `Width`, `Height` | Size in samples |
| `BitsPerComponent` | 1, 2, 4, 8 or 16 (1 for a mask, 8 for a JPEG) |
| `Encoding` | `DCTDecode`, `JPXDecode`, `JBIG2Decode` or `CCITTFaxDecode`, or null for plain samples |
| `Bounds` | The smallest rectangle around the image on the page, in points from the bottom left, before `/Rotate` |
| `Transform` | The matrix that maps the image's unit square to the page |
| `HorizontalResolution`, `VerticalResolution` | Dots per inch, as drawn |
| `IsInline`, `IsMask` | An inline image (BI ... EI), a stencil mask |
| `Dictionary`, `Stream` | The image dictionary and XObject, for anything else |

## Encoded data

`GetEncodedData()` returns the data with general compression such as Flate undone:

- a JPEG file for `DCTDecode`, as stored, with no re-encoding,
- a JPEG 2000 codestream for `JPXDecode`,
- the raw samples when `Encoding` is null.

## Decoded pixels

`Decode()` returns a `PdfBitmap`: rows top to bottom, `Format` `Gray8` or `Rgb24`, with `Width`,
`Height`, `Stride` and `Pixels`. `SavePng(path)`, `SavePng(stream)` and `ToPng()` write it as PNG.
It returns null for an encoding not decoded yet (JPEG 2000) or a color space it does not support.

| Input | Decoded |
|---|---|
| Raw samples | 1 to 16 bits per component |
| JPEG | Inverse DCT, upsampling and color conversion as libjpeg does them, within two steps per sample |
| CCITT fax | Group 3 one and two dimensional, Group 4, bit exact with libtiff |
| JBIG2 | Generic, refinement, text and halftone regions, with JBIG2Globals |
| Color spaces | DeviceGray, DeviceRGB, DeviceCMYK, CalGray, CalRGB, ICCBased, Indexed, Lab, Separation and DeviceN |

The Decode array is applied, and stencil masks come back black where they paint.

## Damaged and hostile files

Decoding is bounded by the image's size and the document's decode limit. Damaged or oversized
data raises `PdfFormatException`; damaged page content is read as far as it goes.

## Limits

- JPEG 2000 images come back encoded (`GetEncodedData`). Decoding them is on the
  [roadmap](/roadmap/).
- Soft masks and transparency are not applied: an image with an alpha channel decodes without
  it.
- Separation and DeviceN colors other than a Type 2 tint transform are approximated as gray.
- Rendering whole pages to images is out of scope.

## Related

- [Extract text](/guides/extract-text/)
- [OCR](/guides/ocr/): decoded scans feed the recognizer
- [Images](/guides/images/): place images on pages
- API: [PdfPageImage](/api/pdfnetstack/pdfpageimage/), [PdfBitmap](/api/pdfnetstack/pdfbitmap/), [PdfPage](/api/pdfnetstack/pdfpage/)
