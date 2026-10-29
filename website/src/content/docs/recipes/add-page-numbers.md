---
title: Add page numbers to a PDF in C#
description: Stamp "Page 1 of 3" at the foot of every page of an existing PDF in .NET with the PdfNetStack drawing canvas. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Add page numbers
---

Number the pages of an existing PDF, centered at the bottom of each page.

## Short answer

Open the document, call `CreateGraphics` on each page, and draw the label with `DrawString`.
`MeasureString` gives the label's width so it can be centered. The canvas origin is the top left
corner of the page, y grows downward, and units are points.

```csharp sample="recipe-add-page-numbers.cs"
```

## Variations

- Use an embedded font for the label: `PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2")`.
- To start the visible numbering elsewhere or use roman numerals in viewers, set
  `doc.PageLabels` as well. Page labels change what viewers show in their page box, not the page
  content.
- New documents produced from HTML can number pages with `counter(page)` and `counter(pages)` in
  `@page` margin boxes, see [HTML to PDF](/guides/html-to-pdf/).

## Related

- [Drawing](/guides/drawing/)
- [Fonts and text](/guides/fonts-and-text/)
- API: [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/), [PdfFont](/api/pdfnetstack-fonts/pdffont/)
