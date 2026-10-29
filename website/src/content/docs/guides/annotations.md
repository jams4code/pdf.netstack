---
title: PDF links and annotations in C# and .NET
description: Add links, notes, highlights, stamps, ink, shapes and file attachments to PDF pages in C#, read them back, and flatten them into page content. MIT.
since: '1.1'
sidebar:
  label: Annotations
faq:
  - q: Do annotations added by PdfNetStack look the same in every viewer?
    a: Yes. Each new annotation carries its own appearance stream, so viewers show it as drawn instead of rebuilding it.
  - q: Can annotations be flattened into the page?
    a: Yes. PdfDocument.FlattenAnnotations draws every markup annotation's appearance into the page content and removes the annotation. Links and form fields stay unless PdfFlattenOptions asks for them.
  - q: Which coordinates do annotation methods use?
    a: The same as drawing, points from the top left corner of the page with y down. Reading an annotation's Rectangle returns PDF user space, origin bottom left.
---

Links, notes, text markup, free text, stamps, ink, shapes and file attachments on PDF pages, the
annotations a file already has, and flattening them into page content.

## Short answer

`page.Annotations` lists a page's annotations and adds new ones. Positions use the same
coordinates as the drawing canvas: points from the top left corner of the page, y down. Each new
annotation comes with an appearance stream, so every viewer shows it the same way.

```csharp sample="annotations-add.cs" region="add"
```

![A manual page with a highlighted line, a struck out line, a note icon and a free text box](/previews/annotations.webp)

## What you can add

| Method | Adds |
|---|---|
| `AddLink(x, y, width, height, uri)` | A link that opens a web address |
| `AddLink(x, y, width, height, page, top)` | A link to a page of the same document, shown from `top` points below its top edge, or whole when `top` is null |
| `AddNote(x, y, text, author, color)` | A sticky note icon that opens the text |
| `AddTextMarkup(kind, x, y, width, height, color, comment)` | `Highlight` (yellow by default), `Underline`, `StrikeOut` or `Squiggly` over a text area |
| `AddFreeText(x, y, width, height, text, style, border, fill)` | Text written in a box, with an optional border and fill |
| `AddStamp(stamp, x, y, width, height, options)` | One of the fourteen standard rubber stamps (`PdfStampName`), framed, in the stamp's color |
| `AddStamp(x, y, width, height, appearance, name, options)` | A custom stamp showing a `PdfForm` of the document, such as a logo or a signature image |
| `AddStamp(x, y, width, height, draw, name, options)` | A custom stamp drawn by a callback on a canvas of the stamp's size |
| `AddSquare`, `AddCircle(x, y, width, height, options)` | A rectangle or an ellipse filling the box, the border drawn inside it |
| `AddLine(start, end, options)` | A straight line, with optional endings at either end (`PdfLineEnding`) |
| `AddPolygon(vertices, options)`, `AddPolyline(vertices, options)` | A closed polygon, or connected lines with optional endings |
| `AddInk(strokes, options)` | Freehand ink: one or more strokes, each a list of points |
| `AddFileAttachment(x, y, fileName, data, mimeType, icon, options)` | A file embedded behind an icon at one spot on the page (`PdfAttachmentIcon`) |

Every method returns the new `PdfAnnotation`. `Remove(annotation)` takes one off the page.

Text markup does not find the text for you: pass the rectangle of the words. Text extraction
reports word bounds, see [Extract text](/guides/extract-text/) and
[Coordinates and units](/concepts/coordinates-and-units/) to convert them.

## Stamps, shapes, ink and attachments

The newer annotation kinds take a `PdfAnnotationOptions` for their look. Each comes with a
generated appearance, so every viewer shows it as drawn.

```csharp sample="annotations-review.cs" region="stamps"
```

```csharp sample="annotations-review.cs" region="shapes"
```

```csharp sample="annotations-review.cs" region="ink"
```

```csharp sample="annotations-review.cs" region="attachment"
```

![A report page with an Approved stamp, a custom Received stamp, a red box, a blue ellipse, an arrow, a polygon, a polyline, an ink stroke and a paperclip icon](/previews/annotations-review.webp)

| Option | Applies to | Meaning |
|---|---|---|
| `Color` | All | The line, border, stamp or icon color; null for the kind's default (red for shapes and ink) |
| `InteriorColor` | Squares, circles, polygons, stamps, closed line endings | The fill; null for none |
| `BorderWidth` | Lines, ink, shapes | Stroke width in points, 0 for no stroke. Default 1 |
| `DashPattern` | Lines, ink, shapes | Dash and gap lengths in points; empty for a solid stroke |
| `StartEnding`, `EndEnding` | Lines, polylines | The shape at the first and last point |
| `Opacity` | All | From 0 to 1. Below 1 the drawing is a transparency group faded as a whole |
| `Comment`, `Author`, `Subject` | All | What a viewer shows in the annotation's pop up |
| `Font` | Standard stamps | Helvetica Bold when null. PDF/A output needs an embedded font |

The file behind a file attachment annotation reads back through `PdfAnnotation.Attachment`, with
its name, type and data. Unlike `PdfDocument.Attachments`, the file belongs to that spot on the
page. Saved as PDF/A-3, such files are declared as associated files; PDF/A-2 refuses them.

## Flatten annotations

Flattening draws an annotation's appearance into the page content and removes the annotation,
with its pop up. The page looks the same, prints the same, and nothing is left to edit or move.

```csharp sample="annotations-review.cs" region="flatten"
```

| Method | Flattens |
|---|---|
| `PdfDocument.FlattenAnnotations(options)` | The annotations of every page. Returns how many |
| `PdfAnnotationCollection.Flatten(options)` | The annotations of one page. Returns how many |
| `PdfAnnotationCollection.Flatten(annotation)` | One annotation. Returns false, changing nothing, when it is hidden, not shown on screen, or has no appearance |

By default notes, text markup, free text, stamps, ink, shapes and file attachments are
flattened. Hidden and NoView annotations, pop ups and annotations without an appearance stay.
`PdfFlattenOptions.IncludeLinks` also flattens links: their look stays and they stop working.
`IncludeWidgets` also flattens form fields, which show their current value and leave the form.
Each appearance is fitted to its rectangle as ISO 32000-1, 12.5.5 describes, kept upright on
rotated pages for annotations marked NoRotate, and kept inside its optional content group.

## Links that follow the canvas

`PdfGraphics.AddLink` adds a link over a rectangle in current canvas coordinates. Under a
transform, the link covers the transformed rectangle's bounds, so a link drawn with its label
stays on the label.

```csharp sample="annotations-add.cs" region="canvas"
```

## Reading annotations

`page.Annotations` lists every annotation on the page, of any kind a file contains.

```csharp sample="annotations-add.cs" region="read"
```

| Property | Meaning |
|---|---|
| `Subtype` | The kind, such as `Link`, `Text`, `Highlight`, `FreeText` or `Widget` |
| `Rectangle` | Where it sits, in PDF user space (origin bottom left, y up) |
| `Contents` | The text of a note or comment, or the description of other annotations |
| `Uri` | The web address a link opens, or null |
| `Destination` | The page a link goes to in the same document, named destinations included, or null |
| `Dictionary` | The annotation dictionary, for everything else |

Form fields show on pages as `Widget` annotations. Fill them through the form API instead: see
[Forms](/guides/forms/).

## Annotations and page edits

Annotations travel with their page when pages are moved or imported, and are copied fresh for
every import. Links to pages imported in the same call are remapped; links to pages left behind
become empty. Named destinations, which bookmarks and links point at by name, can be listed and
renamed: see [Edit pages](/guides/edit-pages/).

## Limits

- Text markup takes the rectangle you give it; it does not search the page for the words.
- Flattening draws the normal appearance (or the one the annotation's state selects). An
  annotation without an appearance stream is left in place, except a link, which is removed.
- Sound, movie, 3D and redaction annotations cannot be added. Existing ones are read like any
  other annotation.

## Related

- [Forms](/guides/forms/)
- [Drawing](/guides/drawing/)
- [Edit pages](/guides/edit-pages/)
- API: [PdfAnnotationCollection](/api/pdfnetstack/pdfannotationcollection/), [PdfAnnotation](/api/pdfnetstack/pdfannotation/), [PdfAnnotationOptions](/api/pdfnetstack/pdfannotationoptions/), [PdfStampName](/api/pdfnetstack/pdfstampname/), [PdfFlattenOptions](/api/pdfnetstack/pdfflattenoptions/), [PdfTextMarkup](/api/pdfnetstack/pdftextmarkup/)
