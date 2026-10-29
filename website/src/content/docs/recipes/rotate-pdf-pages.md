---
title: Rotate PDF pages in C#
description: Rotate one page or every page of a PDF in .NET by a multiple of 90 degrees, without rewriting the page content. MIT licensed, pure C#.
since: '1.0'
sidebar:
  label: Rotate pages
---

Turn pages clockwise by 90, 180 or 270 degrees.

## Short answer

Call `Rotate` on a page with a multiple of 90. The rotation adds to the page's current one and
viewers apply it when they display the page; the content itself is not rewritten. `Rotation` reads
the result: 0, 90, 180 or 270.

```csharp sample="recipe-rotate-pdf-pages.cs"
```

```bash
dotnet add package PdfNetStack
```

## Related

- [Edit pages](/guides/edit-pages/)
- [Command line](/cli/): `pdfnetstack rotate in.pdf out.pdf 90 2-4`
- API: [PdfPage](/api/pdfnetstack/pdfpage/)
