---
title: Split a PDF into one file per page in C#
description: Split a PDF into single page files in .NET with PdfDocument.Split, each saved as a valid, optimized PDF. MIT licensed, pure C#.
since: '1.0'
sidebar:
  label: Split a PDF
---

Turn every page of a PDF into a file of its own.

## Short answer

`PdfDocument.Split` returns one new document per page. Save each one under its own name. Inherited
page attributes such as resources, media box and rotation are materialized on each page, so every
file stands on its own.

```csharp sample="recipe-split-pdf.cs"
```

```bash
dotnet add package PdfNetStack
```

## Variations

To keep groups of pages together rather than single pages, import the pages you want into a new
document, as in [Extract pages from a PDF](/recipes/extract-pdf-pages/).

## Related

- [Edit pages](/guides/edit-pages/)
- [Command line](/cli/): `pdfnetstack split in.pdf ./pages`
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/)
