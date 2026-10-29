---
title: PDF links and annotations in C# and .NET
description: Add web and page links, sticky notes, highlights and free text to PDF pages in C#, each with its own appearance, and read existing annotations. MIT.
since: '1.1'
sidebar:
  label: Annotations
faq:
  - q: Do annotations added by PdfNetStack look the same in every viewer?
    a: Yes. Each new annotation carries its own appearance stream, so viewers show it as drawn instead of rebuilding it.
  - q: Which coordinates do annotation methods use?
    a: The same as drawing, points from the top left corner of the page with y down. Reading an annotation's Rectangle returns PDF user space, origin bottom left.
---

Links, notes, text markup and free text on PDF pages, and the annotations a file already has.

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

Every method returns the new `PdfAnnotation`. `Remove(annotation)` takes one off the page.

Text markup does not find the text for you: pass the rectangle of the words. Text extraction
reports word bounds, see [Extract text](/guides/extract-text/) and
[Coordinates and units](/concepts/coordinates-and-units/) to convert them.

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

- Stamps, ink, shapes and file attachment annotations cannot be added yet, and annotations cannot
  be flattened into page content yet. Both are on the [roadmap](/roadmap/).

## Related

- [Forms](/guides/forms/)
- [Drawing](/guides/drawing/)
- [Edit pages](/guides/edit-pages/)
- API: [PdfAnnotationCollection](/api/pdfnetstack/pdfannotationcollection/), [PdfAnnotation](/api/pdfnetstack/pdfannotation/), [PdfTextMarkup](/api/pdfnetstack/pdftextmarkup/)
