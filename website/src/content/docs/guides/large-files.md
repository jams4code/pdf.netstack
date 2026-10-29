---
title: Work with large PDF files in C#
description: Open large PDFs in .NET without loading them into memory, read pages on demand, and append incremental updates that keep signatures valid. MIT licensed.
since: '1.1'
sidebar:
  label: Large files
faq:
  - q: Can PdfNetStack open a PDF without reading the whole file into memory?
    a: Yes, from version 1.1. PdfDocument.OpenOnDemand reads the cross-reference data and the page tree, and parses every other object when it is first used. Stream data such as page content, images and fonts is read from the stream each time it is needed and not kept.
  - q: Does saving an edited signed PDF break its signatures?
    a: Not with SaveIncremental. It writes the original bytes unchanged followed by the changed objects, so every signature over the original stays intact. A full save rewrites the file and invalidates them.
---

Read a page of a large file without loading the rest, and save a change by appending it.

## Short answer

Open the file with `PdfDocument.OpenOnDemand` on a seekable stream. Only the cross-reference data
and the page tree are read; a page's content, fonts and images are read when you use them. Save
with `SaveIncremental`, which appends the changed objects as an incremental update. Given the
stream the document was opened on, it writes only the update, at the end of the file.

```csharp sample="large-files.cs" region="on-demand"
```

On the sample manual, changing the title appended about 200 bytes to the file.

## Opening on demand

`OpenOnDemand(stream, password, maxDecodedStreamBytes)` takes any readable, seekable stream. Memory
use follows what your code touches, not the size of the file: objects are parsed when first used,
and stream data is read from the stream each time it is needed and not kept. Offsets are read as
64 bit values.

The stream belongs to the document while you use it:

- It must stay open and unchanged, and nothing else may read from it meanwhile.
- The document never disposes it: dispose it yourself when you are done.
- Open it with `FileAccess.ReadWrite` when you want `SaveIncremental` to append in place.

Everything else works as on a document opened with `Open`: pages, text and image extraction,
annotations, forms, metadata and the object model. A full save with `Save` or `ToArray` still
builds the whole output in memory.

## Incremental updates

`SaveIncremental(output)` writes an incremental update (ISO 32000-1, 7.5.6): the original file
byte for byte, then the objects that changed or were added, and a cross-reference section that
points back to the original's.

| Output | What is written |
|---|---|
| The stream given to `OpenOnDemand` | Only the update, appended at the end. Later saves build on the updated file |
| Any other stream | The original bytes, then the update |

Because the original bytes stay as they were, signatures over them stay valid. Here a note is added
to a signed contract:

```csharp sample="large-files.cs" region="signed"
```

The signature is still intact. It no longer covers the whole file, because the note came after
it.

`SaveIncremental` refuses, with `NotSupportedException`, a document that is encrypted or set to be
encrypted, and one that was damaged and had to be rebuilt when opened: save those in full with
`Save`. A document created with `PdfDocument.Create` has no original to append to, and raises
`InvalidOperationException`.

## Compressing large files

The optimizer reads the whole document into memory. Plan for about 10 times the file size, more
for image heavy files, and lower `MaxDegreeOfParallelism` to cap memory. Inputs larger than
`MaxInputBytes` (1 GiB by default) are returned unchanged. See
[Memory and performance](/concepts/memory-and-performance/).

## Limits

- `ToArray` and a full `Save` build the whole output in memory, also for a document opened on
  demand.
- The optimizer holds the whole document in memory, whichever way the file was opened.
- An incremental update keeps everything the original holds: it never makes a file smaller. Run
  the optimizer for that, on a document that is not signed.

## Related

- [Edit pages](/guides/edit-pages/): opening, editing and saving in full
- [Signatures](/guides/signatures/): signing appends an incremental update too
- [Compress PDF](/guides/compress-pdf/)
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/)
