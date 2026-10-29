---
title: Draw on PDF pages in C# and .NET
description: Draw shapes, paths, text, gradients and transparency on new or existing PDF pages in C# with a top left canvas in points. Pure .NET, MIT licensed.
since: '1.1'
sidebar:
  label: Drawing
faq:
  - q: Can I draw on a page of an existing PDF?
    a: Yes. PdfPage.CreateGraphics works on new and opened pages. It draws over the existing content by default, or underneath it with PdfGraphicsLayer.Background.
  - q: Which color models are supported?
    a: Gray, RGB and CMYK device colors, each with an opacity, plus linear and radial gradients and the 16 PDF blend modes.
---

A canvas for PDF pages: shapes, paths, colors, gradients, transforms and clipping, on new pages and
on pages you opened.

## Short answer

Call `CreateGraphics()` on a page to get a `PdfGraphics` canvas. Coordinates are points (1/72
inch) from the top left corner of the page, with y pointing down. Dispose the canvas, or save the
document, and the drawing is written into the page.

```csharp sample="create-first.cs"
```

![A page with a blue header band and the words Hello, PDF](/previews/hello.webp)

## The canvas

```csharp sample="drawing-shapes.cs" region="canvas"
```

`g.Width` and `g.Height` give the page size in points. A4 is 595 by 842 points, US Letter 612 by
792. The page's `/Rotate` is not applied: coordinates are those of the unrotated page. See
[Coordinates and units](/concepts/coordinates-and-units/) for how this canvas relates to the
coordinates text and image extraction report.

A `PdfGraphics` is not thread safe. Use one canvas per page and per thread.

## Shapes and paths

Every shape method takes a pen for the outline and a brush for the fill, in that order. Pass
`null` for either one to leave it out. A `PdfColor` converts to a solid brush.

```csharp sample="drawing-shapes.cs" region="shapes"
```

| Method | Draws |
|---|---|
| `DrawLine`, `DrawLines` | A segment, or connected segments through a list of points |
| `DrawRectangle` | A rectangle from its top left corner |
| `DrawRoundedRectangle` | A rectangle with one radius, or with `PdfCornerRadii` for elliptical corners sized like CSS `border-radius` |
| `DrawEllipse` | An ellipse inscribed in a rectangle |
| `DrawArc`, `DrawPie` | Part of an ellipse; angles in degrees from the x axis, clockwise on the canvas |
| `DrawBezier` | A cubic Bezier curve |
| `DrawPolygon` | A closed polygon, with the nonzero or even-odd fill rule |
| `DrawPath` | Any `PdfPath` |

`PdfPath` builds outlines step by step: `MoveTo`, `LineTo`, `CurveTo`, `QuadraticCurveTo`,
`AddArc`, `AddRectangle`, `AddRoundedRectangle`, `AddEllipse`, `AddCircle`, `AddPolygon` and
`Close`. Its `FillRule` decides how overlapping parts fill.

### Pens

`PdfPen` is an immutable record: create one with a color and a width, and derive variants with
`with`.

| Property | Default | Notes |
|---|---|---|
| `Color` | | Stroke color, with its opacity |
| `Width` | 1 | In points. 0 draws the thinnest line the device can show |
| `LineCap` | `Butt` | `Butt`, `Round` or `Square` |
| `LineJoin` | `Miter` | `Miter`, `Round` or `Bevel` |
| `MiterLimit` | 10 | At least 1 |
| `DashPattern` | solid | Alternating dash and gap lengths in points |
| `DashOffset` | 0 | Where in the pattern the line starts |

## Colors, gradients and transparency

```csharp sample="drawing-shapes.cs" region="colors"
```

- `PdfColor.Gray`, `Rgb` (components 0 to 1), `FromRgb` (0 to 255), `Cmyk` and `FromHex` (`#RGB`,
  `#RGBA`, `#RRGGBB` or `#RRGGBBAA`) create device colors. CMYK is written as DeviceCMYK, for
  print workflows.
- Every color has an opacity: pass `alpha`, or call `WithAlpha`. Opacity below 1 is written as a
  constant alpha graphics state.
- `PdfLinearGradientBrush` and `PdfRadialGradientBrush` take two colors, or a list of
  `PdfGradientStop` values for more. Gradient points are canvas coordinates.
- `BlendMode` sets one of the 16 PDF blend modes, such as `Multiply` or `Screen`, for what is
  drawn next. It is saved and restored with the graphics state.

![Shapes, gradients, a rotated pattern and a clipped circle drawn with PdfNetStack](/previews/drawing.webp)

## Transforms, saved states and clipping

```csharp sample="drawing-shapes.cs" region="transforms"
```

`TranslateTransform`, `ScaleTransform`, `RotateTransform` (clockwise for positive angles),
`RotateAtTransform` and `MultiplyTransform` with a `PdfMatrix` change where later drawing lands.
`Save` records the transform, clip, colors and pen settings; dispose its result, or call
`Restore`, to return to them. `IntersectClip` narrows the area that shows to a rectangle or a path.

## Drawing behind existing content

`CreateGraphics(PdfGraphicsLayer.Background)` draws underneath what a page already shows. Use it
for letterheads, stationery and colored backgrounds that must not cover the text.

```csharp sample="drawing-background.cs" region="background"
```

![A report page with a light blue letterhead band drawn behind its text](/previews/drawing-background.webp)

On an existing page, the page's own content is wrapped in a save and restore pair, so graphics
state it leaves behind cannot change your drawing. Resources shared with other pages are never
modified.

## Text, images and links on the canvas

The canvas also draws text and images. Each has its own guide:

- `DrawString` and `DrawText` for single lines and wrapped paragraphs: [Fonts and text](/guides/fonts-and-text/).
- `DrawImage`, `DrawSvg` and `DrawForm` for images, vector drawings and pages of other PDFs:
  [Images](/guides/images/).
- `AddLink` for a clickable area that follows the current transform: [Annotations](/guides/annotations/).
- `DrawInvisibleText` writes text that is not drawn but selects, copies and searches, stretched to
  fill a box. Laid over the words an OCR engine found, it makes a scanned page searchable. See
  [OCR](/guides/ocr/).

## Limits

- Colors are device colors. There is no ICC based color management for drawn colors, and
  `ToRgb` converts CMYK by the naive formula.
- Patterns and shadings beyond linear and radial gradients are not available on the canvas.

## Related

- [Fonts and text](/guides/fonts-and-text/)
- [Images](/guides/images/)
- [Coordinates and units](/concepts/coordinates-and-units/)
- API: [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/), [PdfPath](/api/pdfnetstack-drawing/pdfpath/), [PdfPen](/api/pdfnetstack-drawing/pdfpen/), [PdfColor](/api/pdfnetstack-drawing/pdfcolor/), [PdfLinearGradientBrush](/api/pdfnetstack-drawing/pdflineargradientbrush/), [PdfMatrix](/api/pdfnetstack-drawing/pdfmatrix/)
