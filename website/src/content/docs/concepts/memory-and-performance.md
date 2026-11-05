---
title: Memory and performance
description: Plan memory and CPU for PdfNetStack in .NET services and batch jobs, cap parallelism, choose compression effort, and publish with Native AOT.
sidebar:
  label: Memory and performance
faq:
  - q: How much memory does PdfNetStack need to optimize a PDF?
    a: The optimizer holds the whole document in memory. Plan for about 10 times the file size, more for image heavy files, or lower MaxDegreeOfParallelism. To read or edit a large file without loading it, open it with PdfDocument.OpenOnDemand (1.1).
  - q: Is PdfNetStack compatible with Native AOT and trimming?
    a: Yes, on .NET 8 and later. The core, HTML and OCR libraries are marked AOT compatible, and CI publishes them with Native AOT on Linux x64 and arm64 and fails on any trim or AOT warning.
---

What PdfNetStack costs in memory and time, and the settings that change it.

## Short answer

The optimizer reads a document whole into memory and processes it there. Plan for about 10 times
the file size, more for image heavy files. From 1.1, `PdfDocument.OpenOnDemand` reads and edits a
file of any size page by page, without loading it. `MaxDegreeOfParallelism` is the main lever on peak memory, and
`Compression` is the main lever on time.

## Memory

PDFs are random access by design and every optimizer pass touches most of the file, so the input
is read into one buffer and stream data is sliced from it, never copied until it is re-encoded.
Peak memory then depends on the decoded size of the streams being worked on and on how many are
worked on at once.

| Setting | Effect on memory |
|---|---|
| `MaxDegreeOfParallelism` (default: processor count) | Caps the worker threads of stream recompression and verification, so fewer streams are decoded at once. Lower it for image heavy files |
| `MaxDecodedStreamBytes` (default 256 MiB) | Ceiling for any single decoded stream; larger streams are left untouched |
| `MaxInputBytes` (default 1 GiB) | Larger inputs are returned unchanged. `OptimizeAsync` buffers at most this much and streams anything larger through to the output untouched |
| `Streaming` (1.1, default false) | The source is read on demand and the rewrite goes through a temporary file, proven there: memory follows the number of objects and a batch of at most 32 MiB of source data, not the file |

`PdfDocument.Open(ReadOnlyMemory<byte>)` references the buffer you pass instead of copying it, so
the buffer must not change while the document is in use. The stream and file overloads read the
input into memory first, once (1.1). Opened on demand (`PdfLoadMode.OnDemand`, 1.1), a document
reads only the cross-reference data and the page tree, parses other objects when first used and
reads stream data each time it is needed, so memory follows what your code touches;
`SaveIncremental` then appends only the changes. `PdfLoadOptions` also caps decoded streams and
image pixels per document. See [Large files](/guides/large-files/).

A measured example from the README: a 631 page catalogue of 72.6 MiB, already Flate compressed,
took about 5 seconds with all cores and peaked at a working set of about 690 MB.

## Time

| Setting | Effect |
|---|---|
| `Compression = CompressionEffort.Maximum` (default) | zlib level 9 |
| `Compression = CompressionEffort.Balanced` | zlib level 6. On the reference corpus it cut total time by about a third for about 2% larger output |
| `Compression = CompressionEffort.Fast` | zlib level 1, for throughput bound batch jobs |
| `Compression = CompressionEffort.Exhaustive` (1.1) | An optimal parsing Deflate search, kept only where it beats level 9. On synthetic files it saved 3 to 5% more on text, scans and photographs, for 2 to 150 times the CPU time |
| `MaxImageDpi`, `ConvertImagesToJpeg`, `MinImageSsim` (1.1) | The lossy image options decode the images they consider to pixels, resample and encode them, and the proof decodes them again, so they cost time and memory the lossless passes do not. Without them the optimizer never decodes pixels |
| `Verify = false` | Skips the equivalence proof. Only for inputs a validated pipeline already checked |
| `ObjectsPerStream` (default 200) | Larger compresses better, smaller lets viewers load single pages faster |

Parsing and graph work run on one thread. Stream recompression and stream comparison run in
parallel, after every object they need has been resolved.

On the reference corpus of 1,296 files (569 MiB), the whole run took about 65 seconds on an 8 core
machine, 8 to 9 MB/s, lossless JPEG re-encoding included. See [Benchmarks](/benchmarks/).

## Many documents at once

For a batch, run documents in parallel and give each one thread rather than giving one document
every core. `pdfnetstack batch` does this by default: `--jobs` documents at once (default: all
cores), each with one thread unless `--threads` is given. In a service, set
`MaxDegreeOfParallelism` to bound the CPU and memory one request can take.

## Native AOT and trimming

On .NET 8 and .NET 10 the `PdfNetStack`, `PdfNetStack.Html` and `PdfNetStack.Ocr` libraries are
marked AOT compatible, which also enables the trim analyzers. CI publishes the command line tool
and a smoke test app that roots all three libraries with Native AOT for linux-x64 and linux-arm64,
fails on any trim or AOT warning, and runs the result. The core and HTML libraries have no native
code to ship per platform. For serverless hosts, see [AWS Lambda](/guides/aws-lambda/) and
[Azure Functions](/guides/azure-functions/).

## .NET Framework and .NET Standard 2.0

The `netstandard2.0` build runs on .NET Framework 4.6.2 to 4.8.1, Mono, Unity and .NET Core 2.0 and
later, with Microsoft's `System.Memory` as its only dependency. .NET Standard's zlib has no level
9, so `CompressionEffort.Maximum` compresses a little less there. The full test suite and the real
world corpus pass on .NET Framework 4.8. On .NET 9 and later, `System.IO.Compression` uses zlib-ng.

## Limits

- In memory, the default, the optimizer needs the whole document, and so do `ToArray` and a full
  `Save` with the optimizer on. A save with `Optimize = false` is written progressively, and the
  optimizer's `Streaming` mode (1.1) keeps memory bounded for files larger than memory; see
  [Large files](/guides/large-files/).
- Linearized output (`Linearize`, 1.1) writes every object on its own, without object streams,
  so it can be larger than the default output.
- Faster text extraction and HTML layout are roadmap items.

## Related

- [Compress PDF](/guides/compress-pdf/): every optimizer option
- [Safety model](/concepts/safety-model/): the limits that bound hostile input
- [Architecture](/concepts/architecture/): the pipeline and its threading
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [CompressionEffort](/api/pdfnetstack/compressioneffort/)
