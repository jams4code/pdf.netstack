---
title: Flatten annotations in a PDF in C#
description: Turn comments, highlights, stamps and other PDF annotations into page content in .NET, so they print and show the same everywhere. MIT, pure C#.
since: '1.1'
sidebar:
  label: Flatten annotations
---

Make review marks part of the page, so nobody can move, edit or hide them.

## Short answer

`PdfDocument.FlattenAnnotations` draws the appearance of every markup annotation (notes, text
markup, free text, stamps, ink, shapes and file attachments) into its page's content and removes
the annotation with its pop up. Links and form fields stay unless you ask for them.

```csharp sample="recipe-flatten-pdf-annotations.cs" region="all"
```

To flatten a single annotation, pass it to its page's collection:

```csharp sample="recipe-flatten-pdf-annotations.cs" region="one"
```

## Notes

- `new PdfFlattenOptions { IncludeLinks = true }` also flattens links: they keep their look and
  stop working. `IncludeWidgets = true` also flattens form fields, which show their current value
  and leave the form. `doc.Form.Flatten()` flattens a whole form.
- Hidden annotations, and annotations without an appearance, stay as they are.

## Related

- [Annotations](/guides/annotations/): adding, reading and flattening annotations
- [Fill and flatten a PDF form](/recipes/fill-and-flatten-pdf-form/)
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/), [PdfAnnotationCollection](/api/pdfnetstack/pdfannotationcollection/), [PdfFlattenOptions](/api/pdfnetstack/pdfflattenoptions/)
