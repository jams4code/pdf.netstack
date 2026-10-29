---
title: PDF coordinates and units in PdfNetStack
description: How PdfNetStack places things in points from the top left when you draw, and reports bottom left PDF user space when you extract text and images.
since: '1.1'
sidebar:
  label: Coordinates and units
---

Two coordinate systems, one unit: what each API uses, and how to convert between them.

## Short answer

Everything is in points, 1/72 inch. When you create content (drawing, annotations, form fields),
the origin is the top left corner of the page and y points down. When you read content back (text,
images, annotation and widget rectangles), positions are in PDF default user space: origin bottom
left, y up, before the page's `/Rotate`.

```csharp sample="coordinates-units.cs" region="units"
```

## Units

| Unit | Points |
|---|---|
| 1 inch | 72 |
| 1 cm | 28.35 (72 / 2.54) |
| 1 mm | 2.835 (72 / 25.4) |
| CSS pixel | 0.75 (96 per inch) |

`PdfRectangle.A4` is 595 by 842 points (210 by 297 mm, rounded), `PdfRectangle.Letter` 612 by 792.
`PdfRectangle.FromSize(width, height)` makes any other size, such as `FromSize(842, 595)` for A4
landscape. Images drawn at their natural size use the resolution the file records, else 96 pixels
per inch.

## Creating content: top left, y down

| API | Origin |
|---|---|
| `PdfPage.CreateGraphics()` and every `PdfGraphics` method | Top left corner of the media box, y down |
| Every `page.Annotations.Add...` method: links, notes, markup, free text, stamps, shapes, lines, ink, attachments | Same as the canvas |
| `doc.Form.AddTextField` and the other field methods | Same as the canvas |
| `PdfForm.CreateGraphics()` | Top left corner of the form |
| `PdfTextLayout` lines (`Top`, `Baseline`, `Left`) | Top left corner of the laid out paragraph |

Two details matter when placing text:

- `DrawString(text, font, size, brush, x, y)` puts the start of the baseline at (x, y). Add
  `font.Ascent * size` to y to place the top of the line instead.
- `DrawText(paragraph, x, y, width)` puts the top left corner of the paragraph at (x, y).

Canvas transforms (`TranslateTransform`, `RotateTransform` and the others) change where later
drawing lands. Positive rotation angles turn clockwise on the canvas. `PdfGraphics.AddLink` follows
the current transform; the annotation methods on `page.Annotations` do not.

## Reading content: bottom left, y up

| API | Coordinates |
|---|---|
| `PdfPage.GetWords()`, `GetLetters()`: `Bounds`, `BaselineStart`, `BaselineEnd` | Default user space, before `/Rotate` |
| `PdfPage.GetImages()`: `Bounds`, `Transform` | Default user space, before `/Rotate` |
| `PdfAnnotation.Rectangle`, `PdfWidget.Rectangle` | PDF user space |
| `PdfPage.MediaBox`, `CropBox` | PDF user space, as `PdfRectangle(Left, Bottom, Right, Top)` |

```csharp sample="coordinates-units.cs" region="readback"
```

To convert a point from user space to the canvas: `x = pdfX - page.MediaBox.Left` and
`y = page.MediaBox.Top - pdfY`. For a rectangle, its canvas top is `MediaBox.Top - rect.Top` and its
height stays `rect.Height`. That is how you place a highlight over a word that text extraction
found.

## Rotated pages

`PdfPage.Rotation` is the clockwise rotation a viewer applies when it displays the page: 0, 90, 180
or 270. `Rotate(degrees)` adds to it.

- The canvas does not apply `/Rotate`: coordinates are those of the unrotated page. On a page
  rotated by 90 degrees, what you draw at the canvas top shows on the right side in a viewer.
- Extraction positions are also before `/Rotate`.
- `PdfForm.FromPage` shows a page upright, as its rotation shows it, so placing a rotated page on
  another page needs no extra turn.

## Media box and crop box

The media box is the page boundary; it defaults to US Letter when a file does not define one. The
crop box is the visible region and defaults to the media box. The canvas origin is the top left of
the media box, and `PdfForm.FromPage` shows the crop box.

## Related

- [Drawing](/guides/drawing/)
- [Extract text](/guides/extract-text/)
- [Annotations](/guides/annotations/)
- API: [PdfRectangle](/api/pdfnetstack/pdfrectangle/), [PdfPage](/api/pdfnetstack/pdfpage/), [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/), [PdfWord](/api/pdfnetstack/pdfword/)
