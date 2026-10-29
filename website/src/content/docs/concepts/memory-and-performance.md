---
title: Memory and performance
description: Plan memory and CPU for PdfNetStack in .NET services and batch jobs, cap parallelism, choose compression effort, and publish with Native AOT.
sidebar:
  label: Memory and performance
faq:
  - q: How much memory does PdfNetStack need to optimize a PDF?
    a: The whole document is held in memory. Plan for about 10 times the file size, more for image heavy files, or lower MaxDegreeOfParallelism.
  - q: Is PdfNetStack compatible with Native AOT and trimming?
    a: Yes, on .NET 8 and later. The core and HTML libraries are marked AOT compatible, and CI publishes the command line tool with Native AOT and fails on any trim or AOT warning.
---

What PdfNetStack costs in memory and time, and the settings that change it.

## Short answer

A document is read whole into memory and processed there. Plan for about 10 times the file size,
more for image heavy files. `MaxDegreeOfParallelism` is the main lever on peak memory, and
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

`PdfDocument.Open(ReadOnlyMemory<byte>)` references the buffer you pass instead of copying it, so
the buffer must not change while the document is in use. The stream and file overloads read the
input into memory first.

A measured example from the README: a 631 page catalogue of 72.6 MiB, already Flate compressed,
took about 5 seconds with all cores and peaked at a working set of about 690 MB.

## Time

| Setting | Effect |
|---|---|
| `Compression = CompressionEffort.Maximum` (default) | zlib level 9 |
| `Compression = CompressionEffort.Balanced` | zlib level 6. On the reference corpus it cut total time by about a third for about 2% larger output |
| `Compression = CompressionEffort.Fast` | zlib level 1, for throughput bound batch jobs |
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

On .NET 8 and .NET 10 the `PdfNetStack` and `PdfNetStack.Html` libraries are marked AOT
compatible, which also enables the trim analyzers. CI publishes the command line tool with Native
AOT for Linux and fails the build on any trim or AOT warning. The library has no native code to
ship per platform.

## .NET Framework and .NET Standard 2.0

The `netstandard2.0` build runs on .NET Framework 4.6.2 to 4.8.1, Mono, Unity and .NET Core 2.0 and
later, with Microsoft's `System.Memory` as its only dependency. .NET Standard's zlib has no level
9, so `CompressionEffort.Maximum` compresses a little less there. The full test suite and the real
world corpus pass on .NET Framework 4.8. On .NET 9 and later, `System.IO.Compression` uses zlib-ng.

## Limits

- Files larger than memory are not supported. A streaming parse is on the
  [roadmap](/roadmap/).
- Linearized (fast web view) output is not produced; linearization is dropped.
- Faster text extraction and HTML layout are roadmap items.

## Related

- [Compress PDF](/guides/compress-pdf/): every optimizer option
- [Safety model](/concepts/safety-model/): the limits that bound hostile input
- [Architecture](/concepts/architecture/): the pipeline and its threading
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [CompressionEffort](/api/pdfnetstack/compressioneffort/)
