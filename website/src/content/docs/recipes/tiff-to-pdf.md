---
title: Convert a TIFF to PDF in C#
description: Turn a single or multi-page TIFF into a PDF in .NET, one page per image at its recorded resolution, fax pages kept compact. MIT, pure C#.
since: '1.1'
sidebar:
  label: TIFF to PDF
---

Put every page of a TIFF file, such as a scan or a fax, on its own PDF page.

## Short answer

`PdfImage.GetPageCount` counts the pages of the TIFF and `PdfImage.Load(path, page)` loads one,
from 0. Add a PDF page the size of the image, which is its pixel size at the resolution the file
records, and draw the image at its natural size.

```csharp sample="recipe-tiff-to-pdf.cs"
```

## Notes

- A TIFF page stored as one strip of CCITT fax or JPEG data goes into the PDF as is, without
  decoding (CCITTFaxDecode, DCTDecode). Other pages are decoded and Flate compressed.
- TIFF strips and tiles, chunky and planar data, PackBits, LZW, Deflate, CCITT Group 3 and 4 and
  JPEG compression, bilevel to 16 bit samples, alpha and BigTIFF are read. A damaged file raises a
  `FormatException`.
- A file without a resolution is taken at 96 pixels per inch. To fit pages to A4 instead, add
  `PdfRectangle.A4` pages and draw with `PdfImageFit.Contain`.

## Related

- [Images](/guides/images/): every supported format, fit and alignment
- [Make a scanned PDF searchable](/recipes/make-scanned-pdf-searchable/)
- API: [PdfImage](/api/pdfnetstack-drawing/pdfimage/), [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/)
