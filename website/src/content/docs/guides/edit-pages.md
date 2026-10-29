---
title: Edit PDF pages in C# and .NET
description: Merge, split, reorder, rotate, insert and remove PDF pages in C#, edit metadata and bookmarks, and save an optimized, verified file. MIT licensed.
since: '1.0'
sidebar:
  label: Edit pages
faq:
  - q: Does merging PDFs duplicate shared fonts and images?
    a: No. Resources shared by several imported pages are copied once, and saving runs the optimizer, which merges byte identical fonts and images across the merged documents.
  - q: What happens to links and bookmarks when pages are removed?
    a: Links and bookmarks that point to a removed page become empty. Links between pages imported in the same call keep working.
  - q: Can PdfNetStack edit a signed PDF?
    a: Yes. Saving in full writes a new single revision file and invalidates the signatures. From version 1.1, SaveIncremental appends the changes after the signed bytes instead, so every signature stays intact.
---

Open a PDF, change its pages, metadata and bookmarks, and save a new file that is optimized and
verified on the way out.

## Short answer

`PdfDocument.Open` reads a file, `doc.Pages` is an ordered list you can change, and `doc.Save`
writes a new single revision PDF. `Pages.Import` copies pages from any other document, with shared
fonts and images copied once.

```csharp sample="edit-pages.cs" region="edit"
```

```bash
dotnet add package PdfNetStack
```

The same edits on the command line are `pdfnetstack merge`, `split`, `extract`, `rotate` and
`meta`, see [Command line](/cli/).

## Open and create

| Call | What it does |
|---|---|
| `PdfDocument.Open(path, password)` | Reads a file. The password is only needed for encrypted files |
| `PdfDocument.Open(stream, password)` | Reads a stream to its end |
| `PdfDocument.Open(bytes, password, maxDecodedStreamBytes)` | Opens from memory. The buffer is referenced, not copied, so it must not change while the document is in use |
| `PdfDocument.Create()` | An empty document. Add at least one page before saving |

A file that is not a readable PDF raises `PdfFormatException`. A wrong password raises
`PdfPasswordException`, see [Encryption](/guides/encryption/).

## Pages

`doc.Pages` is a `PdfPageCollection`, zero based.

| Member | Effect |
|---|---|
| `Add(PdfRectangle.A4)`, `Insert(index, size)` | Blank page at the end or at a position |
| `RemoveAt(index)`, `Remove(page)`, `Clear()` | Removes pages. Links and bookmarks pointing to them become empty |
| `Move(from, to)` | Reorders |
| `Import(pages, index)` | Copies pages from this or another document to the end, or to `index`, and returns the new pages |
| `page.Rotate(90)` | Rotates clockwise by a multiple of 90. `page.Rotation` reads the result: 0, 90, 180 or 270 |
| `page.Width`, `page.Height`, `page.MediaBox`, `page.CropBox` | Page geometry, in points |

Inherited page attributes (resources, media box, rotation) are copied onto each page when the
document opens, so a page stays correct when it moves within the document or to another one.

## Merge, split and extract

```csharp sample="edit-merge.cs" region="merge"
```

`Merge` imports every page of every document, in order. `Split` returns one new document per page:

```csharp sample="edit-merge.cs" region="split"
```

To extract a page range, import the pages you want into a new document. Any order works, and the
same page may appear twice:

```csharp sample="edit-merge.cs" region="range"
```

Annotations travel with their page and are copied fresh for every import. Links to pages imported
in the same call are remapped to the copies; links to pages left behind become empty. Since 1.1,
form fields travel with their pages too, renamed with a `_2` suffix when the target form already
has the name, see [Forms](/guides/forms/#fields-in-imported-pages). A document
opened with only its user password gives up its pages only when its permissions allow copying.

## Metadata

`doc.Info` holds the document information dictionary: title, author, subject, keywords, creator,
producer and the two dates, plus any custom entry by name.

```csharp sample="edit-info.cs" region="info"
```

Values are decoded from PDFDocEncoding or UTF-16 and written back in whichever of the two fits.
Changing them does not update XMP metadata; [PDF/A](/guides/pdf-a/) output keeps both in sync.

## Bookmarks

`doc.Outlines` reads and writes the bookmark tree. A bookmark has a title, the page it opens, and
child bookmarks. Existing bookmarks keep their original styling and actions when you edit others.

```csharp sample="edit-info.cs" region="outlines"
```

## Named destinations

Available since 1.1. Bookmarks and links often point at a name instead of a page, and the
document maps each name to a page. `doc.NamedDestinations` lists the names of the name tree and of
the older catalog dictionary, resolves them to pages, and renames them. A rename changes every
bookmark, link and GoTo action that refers to the name, in its own string or name form, and
rewrites the name tree as one sorted leaf. A rename that would collide with an existing name is
refused. `PdfOutline.DestinationName` gives the name a bookmark points at.

```csharp sample="edit-names.cs" region="names"
```

## Save options

`Save` writes a new single revision file. By default it runs the lossless
[optimizer](/guides/compress-pdf/) on the result, with verification, because merged documents
often share fonts and images. `Save(path)` writes through a temporary file and replaces the
target atomically.

```csharp sample="edit-save.cs" region="save"
```

| `PdfSaveOptions` | Default | Notes |
|---|---|---|
| `Optimize` | true | Runs the lossless optimizer, with verification, on the saved file |
| `Compression` | `Maximum` | Flate effort: `Fast` (zlib level 1), `Balanced` (level 6) or `Maximum` (level 9) |
| `UseObjectStreams` | true | Packs objects into compressed object streams (PDF 1.5). Off writes a classic xref table |
| `Conformance` | `None` | Since 1.1: PDF/A-2b, 2u, 3b or 3u, see [PDF/A](/guides/pdf-a/) |

## Limits

- Saving in full with `Save` writes a single revision: incremental updates of the source are not
  kept, and the signatures of a signed document become invalid. `SaveIncremental` (1.1) appends
  the changes instead and keeps them valid, see [Large files](/guides/large-files/). To add a
  signature without breaking earlier ones, see [Signatures](/guides/signatures/).
- A document opened with only the user password of an encrypted file can be read but not saved.
- `Open` reads the whole file into memory. `OpenOnDemand` (1.1) reads objects as they are used,
  see [Large files](/guides/large-files/) and
  [Memory and performance](/concepts/memory-and-performance/).

## Related

- [Compress PDF](/guides/compress-pdf/): what the save time optimizer does
- [Encryption](/guides/encryption/): open protected files and protect your output
- [Command line](/cli/): `merge`, `split`, `extract`, `rotate` and `meta`
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/), [PdfPageCollection](/api/pdfnetstack/pdfpagecollection/), [PdfPage](/api/pdfnetstack/pdfpage/), [PdfDocumentInfo](/api/pdfnetstack/pdfdocumentinfo/), [PdfOutline](/api/pdfnetstack/pdfoutline/), [PdfNamedDestinations](/api/pdfnetstack/pdfnameddestinations/), [PdfSaveOptions](/api/pdfnetstack/pdfsaveoptions/)
