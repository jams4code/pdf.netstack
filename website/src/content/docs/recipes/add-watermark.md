---
title: Add a watermark to a PDF in C#
description: Stamp a diagonal, semi transparent DRAFT watermark on every page of a PDF in .NET with the PdfNetStack canvas. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Add a watermark
---

Write a large, rotated, semi transparent word across every page.

## Short answer

Draw on each page with `CreateGraphics`, rotate the canvas about the page center with
`RotateAtTransform`, and draw the text with a color that has an alpha below 1. Positive angles turn
clockwise, so -45 runs the text up from bottom left to top right.

```csharp sample="recipe-add-watermark.cs"
```

![The first page of the contract with a red DRAFT watermark across it](/previews/recipe-watermark.webp)

## Variations

- `page.CreateGraphics(PdfGraphicsLayer.Background)` draws underneath the existing content, so the
  text of the page stays on top of the watermark.
- For an image or a logo, use `DrawImage` or `DrawSvg` the same way. To stamp another PDF page,
  such as a letterhead, use `PdfForm.FromPage` and `DrawForm`, see [Images](/guides/images/).

## Related

- [Drawing](/guides/drawing/)
- [Images](/guides/images/)
- API: [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/), [PdfColor](/api/pdfnetstack-drawing/pdfcolor/)
