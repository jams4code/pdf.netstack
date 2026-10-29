---
title: Write Arabic text in a PDF in C#
description: Draw Arabic and other right to left text in a PDF in .NET with joined letters, bidi ordering and searchable text. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Arabic text
---

Write a right to left paragraph with an embedded Arabic font, numbers and currency included.

## Short answer

Load a font that covers Arabic with `PdfOpenTypeFont.Load`, put the text in a `PdfParagraph` with
`Direction = PdfTextDirection.RightToLeft`, and draw it with `DrawText`. Letters join and take their
contextual forms, and the text carries ActualText, so it copies and searches in logical order.

```csharp sample="recipe-arabic-text-pdf.cs"
```

![An Arabic sentence drawn right to left at the top of a page](/previews/recipe-arabic.webp)

## Notes

- The font is embedded as a subset of the glyphs drawn. TrueType, OpenType, WOFF and WOFF2 files
  load the same way.
- A font that lacks some characters, such as Latin letters, digits or the euro sign, borrows them
  from the fonts in `Fallbacks` on the `PdfTextStyle`, as the sample does with Inter.
- Numbers keep their left to right order inside the right to left line, as the Unicode
  bidirectional algorithm places them.
- `doc.Viewer.RightToLeft = true` tells viewers the document reads right to left, which sets the
  order of pages shown side by side.

## Related

- [Fonts and text](/guides/fonts-and-text/)
- [Extract text](/guides/extract-text/)
- API: [PdfParagraph](/api/pdfnetstack-drawing/pdfparagraph/), [PdfTextStyle](/api/pdfnetstack-drawing/pdftextstyle/), [PdfOpenTypeFont](/api/pdfnetstack-fonts/pdfopentypefont/)
