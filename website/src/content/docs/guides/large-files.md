---
title: Work with large PDF files in C#
description: Open large PDFs in .NET without loading them into memory, read pages on demand, and append incremental updates that keep signatures valid. MIT licensed.
since: '1.1'
sidebar:
  label: Large files
faq:
  - q: Can PdfNetStack open a PDF without reading the whole file into memory?
    a: Yes, from version 1.1. PdfDocument.OpenOnDemand reads the cross-reference data and the page tree, and parses every other object when it is first used. Stream data such as page content, images and fonts is read from the stream each time it is needed and not kept.
  - q: Can PdfNetStack read a PDF from an HTTP request or a storage download in AWS Lambda or Azure Functions?
    a: Yes, from version 1.1. PdfDocument.OpenAsync reads the stream with asynchronous reads only. Opened on demand, a stream that cannot seek is copied to memory up to PdfLoadOptions.SpoolThreshold and past it to a temporary file in TempDirectory, such as /tmp on Lambda, deleted when the document is disposed.
  - q: How do I limit the memory one PDF can take?
    a: PdfLoadOptions caps any decoded stream (MaxDecodedStreamBytes, 256 MiB by default) and the pixels of any decoded image (MaxImagePixels, 2^28 by default). Open large files on demand, and call ReleaseUnusedObjects while reading many pages.
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

`OpenOnDemand(stream, password, maxDecodedStreamBytes)` takes any readable stream. Memory use
follows what your code touches, not the size of the file: objects are parsed when first used,
and stream data is read from the stream each time it is needed and not kept. Offsets are read as
64 bit values.

A seekable stream belongs to the document while you use it:

- It must stay open and unchanged, and nothing else may read from it meanwhile.
- The document never disposes it: dispose it yourself when you are done.
- Open it with `FileAccess.ReadWrite` when you want `SaveIncremental` to append in place.

`PdfDocument` is disposable. Disposing a document read on demand releases what it reads from and
ends its use: anything that needs its objects afterwards throws `ObjectDisposedException`.
Disposing a document read into memory does nothing.

## Load options

`PdfLoadOptions` gathers how a document is opened, for `Open(path, options)`,
`Open(stream, options)`, `Open(memory, options)` and `OpenAsync`:

| Option | Default | What it does |
|---|---|---|
| `Password` | null | User or owner password; null tries the empty user password |
| `Mode` | `InMemory` | `OnDemand` parses objects when used and reads stream data when needed |
| `MaxDecodedStreamBytes` | 256 MiB | Ceiling for any single decoded stream, and for the bytes of one decoded image |
| `MaxImagePixels` | 2^28 | Most pixels one image may decode to; a larger one raises `PdfFormatException` before its pixels are allocated |
| `SpoolThreshold` | 16 MiB | How much of a stream that cannot seek is copied to memory before it goes to a file |
| `TempDirectory` | system temp folder | Where that file goes |

```csharp sample="large-files.cs" region="options"
```

Opened on demand by path, the document opens the file itself, shared for reading, and keeps it
open until it is disposed. In memory, the file or stream is read once: a `MemoryStream` whose
buffer is exposed is used without copying, a seekable stream is read into an array of its length,
and any other stream in pieces joined once.

`MaxImagePixels` holds for every encoding a document's images use: raw samples (with PNG or TIFF
predictors), JPEG, JPEG 2000, JBIG2 and CCITT fax, by the size the dictionary gives and by the size
the data declares. For image files you place on pages, `PdfImage.Load(data, page, maxPixels)`
does the same for JPEG, PNG, GIF, BMP, TIFF and WebP and raises `FormatException`.

## Streams that cannot seek, and async reads

An HTTP request body or a storage download can be read once, front to back. Opened on demand,
such a stream is copied first: to memory up to `SpoolThreshold`, and past it to a temporary file
in `TempDirectory`. The copy belongs to the document and is deleted when the document is disposed,
or at once if opening fails. On Windows the file is opened with `DeleteOnClose`; elsewhere its
name is removed as soon as it is open, so nothing stays on disk if the process ends first. On AWS
Lambda, the one writable folder is `/tmp`.

`OpenAsync(stream, options, cancellationToken)` reads the stream with asynchronous reads only,
for hosts that forbid synchronous I/O, such as ASP.NET Core request bodies. On demand it always
reads from its own copy, so the stream is not used after the call. Cancelling stops the reads and
deletes any partial copy.

```csharp sample="large-files.cs" region="async"
```

## Memory while reading page by page

A document read on demand keeps every object it parses: on pages that each have their own font,
about 25 KB a page. `ReleaseUnusedObjects` frees the parsed objects nothing uses any more, and
they are parsed again if you need them later. It cannot lose an edit: an object that differs from
the file is kept, and so is every object still referenced from anywhere, which stays the instance
the document returns. To find those, it runs a full garbage collection, so call it every few dozen
pages rather than after each one.

On a synthetic document of 300 pages, each with its own font, descriptor, resources and link,
reading the text of every page kept 1,503 parsed objects and 7.8 MB. Releasing every 50 pages
kept 301 objects (the pages themselves) and 1.0 MB.

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

## Saving large files

A full save without the optimizer is written progressively: each object, or each batch of
objects packed into an object stream, goes to the output as soon as it is serialized, offsets
are counted as the bytes go out, and the cross-reference data comes last. Memory beyond the
document follows the largest object, not the file, and for a document opened on demand the
stream data is copied from the source as it is written. The bytes are the ones `ToArray`
returns for the same options.

`SaveAsync` writes the same file with asynchronous calls only and flushes the stream at the end,
so it can write straight into an ASP.NET Core or Azure Functions response body, which refuse
synchronous writes. `HtmlConverter.ConvertToPdfAsync` does the same for HTML.

```csharp sample="large-files-save.cs" region="progressive"
```

| Save | Memory beyond the document |
|---|---|
| `Optimize = false` | The largest object or object stream batch, and a few bytes of cross-reference data per object |
| `Optimize = true`, the default | The whole file, twice: the optimizer reads all of it and proves its rewrite before any of it is used |
| Encrypted, with the optimizer | As above; encryption is a last pass over the proven file |

Cancelling `SaveAsync` stops it before the next object; what reached the stream is then not a
complete file, and `SaveAsync(path)` leaves the file as it was. A failed write surfaces as the
stream's own exception.

To shrink a file too large for memory, save it without the optimizer, then run the optimizer in
streaming mode. `PdfOptimizerOptions.Streaming` reads the source on demand, recompresses streams a
batch of at most 32 MiB of source data at a time, and writes the rewrite to a temporary file: the
destination's own for `OptimizeFileAsync`, otherwise one in the system temporary folder, which
needs room for the output. The proof then reads that file back on demand and compares it with
the source exactly as in memory, every reachable object and every stream by its decoded content.
Only a proven rewrite, or else the input, reaches the destination.

```csharp sample="large-files-save.cs" region="streaming"
```

On a generated 278 MB file of 400 image pages (`tools/PdfNetStack.MemoryBench`), the streaming
optimizer peaked at about 50 MB of live managed memory against 810 MB in memory, took no longer,
and wrote the same bytes. The same file saved progressively from a document opened on demand
peaked at 3 MB instead of 835 MB.

## Compressing large files

By default the optimizer reads the whole document into memory. Plan for about 10 times the file
size, more for image heavy files, and lower `MaxDegreeOfParallelism` to cap memory, or use
`Streaming` as above. Inputs larger than `MaxInputBytes` (1 GiB by default) are returned
unchanged; raise it for streaming mode. See
[Memory and performance](/concepts/memory-and-performance/).

## Limits

- `ToArray` builds the whole output in memory, and so does a full `Save` with the optimizer on.
- In memory, the optimizer holds the whole document, whichever way the file was opened.
  `Streaming` cannot be combined with `Linearize`, and its output can differ slightly from the
  in-memory rewrite: duplicates are found before streams are recompressed, so two JPEG files
  that differ only in metadata stay two.
- Read into memory, a file is limited to 2 GiB, and a stream that cannot seek takes up to about
  twice its size while it is read. Open larger files on demand.
- Decoded object streams are kept apart from parsed objects, up to `MaxDecodedStreamBytes` in
  total, and `ReleaseUnusedObjects` does not free them.
- A temporary copy of a stream is never written to: `SaveIncremental` given another stream writes
  the original bytes and the update there.
- Two saves of an encrypted document never match byte for byte (each string and stream gets a
  random initialization vector), nor do two PDF/A saves (each records a new instance id and the
  time of the save).
- An incremental update keeps everything the original holds: it never makes a file smaller. Run
  the optimizer for that, on a document that is not signed.

## Related

- [Edit pages](/guides/edit-pages/): opening, editing and saving in full
- [Signatures](/guides/signatures/): signing appends an incremental update too
- [Compress PDF](/guides/compress-pdf/)
- [Memory and performance](/concepts/memory-and-performance/)
- [Safety model](/concepts/safety-model/): the limits that bound hostile input
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/), [PdfLoadOptions](/api/pdfnetstack/pdfloadoptions/)
