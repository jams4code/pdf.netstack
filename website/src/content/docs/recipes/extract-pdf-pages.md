---
title: Extract pages from a PDF in C#
description: Copy a page range or a list of pages from a PDF into a new file in .NET, in any order. Shared resources are copied once. MIT licensed.
since: '1.0'
sidebar:
  label: Extract pages
---

Copy some pages of a PDF into a new document, in the order you choose.

## Short answer

Create an empty document with `PdfDocument.Create`, then import the pages you want with
`Pages.Import`. Page numbers in the sample start at 1. Fonts and images shared between the pages
are copied once.

```csharp sample="recipe-extract-pdf-pages.cs"
```

```bash
dotnet add package PdfNetStack
```

## Notes

Annotations are copied fresh for every import. Links to pages imported in the same call are
remapped, and links to pages left behind become empty.

## Related

- [Edit pages](/guides/edit-pages/)
- [Command line](/cli/): `pdfnetstack extract in.pdf out.pdf 1-3,7,9-` takes ranges in any order, repeats allowed
- API: [PdfPageCollection](/api/pdfnetstack/pdfpagecollection/)
