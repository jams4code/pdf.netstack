---
title: Merge PDF files in C#
description: Combine several PDF files into one in .NET with PdfDocument.Merge. Shared fonts and images are written once. MIT licensed, pure C#.
since: '1.0'
sidebar:
  label: Merge PDF files
---

Combine several PDFs into one document, pages in the order you give.

## Short answer

Open each file with `PdfDocument.Open`, pass them to `PdfDocument.Merge`, and save the result.
Saving runs the lossless optimizer, so fonts and images the files share are written once.

```csharp sample="recipe-merge-pdf-files.cs"
```

```bash
dotnet add package PdfNetStack
```

## Variations

- To insert pages at a given place instead, use `doc.Pages.Import(other.Pages, index)`.
- Annotations travel with their pages. Links to pages imported in the same call are remapped.
- Encrypted inputs open with `PdfDocument.Open(path, password)`. Pages can be copied out of them
  only when their permissions allow copying.

## Related

- [Edit pages](/guides/edit-pages/): import, reorder, rotate and remove pages
- [Command line](/cli/): `pdfnetstack merge out.pdf a.pdf b.pdf c.pdf`
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/), [PdfPageCollection](/api/pdfnetstack/pdfpagecollection/)
