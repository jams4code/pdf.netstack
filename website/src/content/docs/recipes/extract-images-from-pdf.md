---
title: Extract all images from a PDF in C#
description: Save every image of a PDF in .NET, JPEGs as stored and other images as PNG, with size and resolution. MIT licensed, pure C#, no native code.
since: '1.1'
sidebar:
  label: Extract all images
---

Save the images every page draws, as JPEG or PNG files.

## Short answer

`GetImages` lists the images a page draws, with their placement and resolution. A JPEG
(`DCTDecode`) is written as stored with `GetEncodedData`, so nothing is recompressed. Other images
are decoded to 8 bit gray or RGB pixels with `Decode` and saved as PNG.

```csharp sample="recipe-extract-images-from-pdf.cs"
```

## Notes

- An image drawn on several pages is listed on each of them. `HorizontalResolution` and
  `VerticalResolution` give the dots per inch at which it is drawn.
- `Decode` handles raw samples, JPEG, JPEG 2000, CCITT fax and JBIG2. Soft masks are not applied,
  and `Decode` returns null for an unsupported color space.

## Related

- [Extract images](/guides/extract-images/)
- [Extract text](/guides/extract-text/)
- API: [PdfPageImage](/api/pdfnetstack/pdfpageimage/), [PdfBitmap](/api/pdfnetstack/pdfbitmap/)
