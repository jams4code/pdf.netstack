---
title: Frequently asked questions
description: Answers about PdfNetStack licensing, platforms, native dependencies, lossless compression, HTML to PDF, Arabic text, PDF/A, OCR and memory use.
sidebar:
  label: FAQ
faq:
  - q: Is PdfNetStack free for commercial use?
    a: Yes. PdfNetStack is MIT licensed. On .NET 8 and later the core package has no dependencies; on .NET Standard 2.0 it uses Microsoft's System.Memory, which is also MIT. CI blocks any runtime package that is not on a reviewed license allowlist.
  - q: Does it need Ghostscript, a browser or other native binaries?
    a: No. The library, the HTML to PDF package and the command line tool are managed code and start no external process. The one exception is the optional OCR add-on, which runs its models on ONNX Runtime.
  - q: Which platforms and .NET versions does it run on?
    a: .NET 8, 9, 10 and later through the net8.0 and net10.0 targets, and .NET Framework 4.6.2 to 4.8.1, Mono, Unity and .NET Core 2.0+ through netstandard2.0. CI runs the tests on Windows, Linux and macOS, on .NET Framework 4.8, .NET 8 and .NET 10.
  - q: Is it compatible with trimming and Native AOT?
    a: Yes. The net8.0 and net10.0 builds of the library are marked trim and AOT compatible, and CI publishes the command line tool with Native AOT, failing on any trim or AOT warning.
  - q: Is the compression lossless?
    a: Yes, by default. Text, vectors, fonts, images, annotations, forms, bookmarks, structure tags, metadata and attachments are carried over, and every output is compared to its source before it is returned. Lossy JPEG requantization exists, but only when you set JpegQuality.
  - q: How much smaller do files get?
    a: On a mixed corpus of 1,296 real world files, the 1,003 files that were optimized went from 568.9 MiB to 469.0 MiB, 17.6% saved, with zero differing pixels at 150 DPI. Image heavy files often gain 20 to 40%. Files from producers that already use object streams gain little.
  - q: What happens with encrypted or signed PDFs?
    a: The optimizer returns them byte for byte, because rewriting would break the encryption or the signatures. The editing API opens encrypted files with a password and keeps their protection on save. Editing a signed document invalidates its signatures.
  - q: Can PdfNetStack render PDF pages to images?
    a: No. Rendering pages to images is out of scope for the project. It extracts the images a page contains, and the text, but it does not rasterize pages.
  - q: Is the HTML to PDF converter a headless browser?
    a: No. PdfNetStack.Html is a layout engine written in C# with the CSS cascade, block, inline, table, flex and grid layout and paged media. It does not run JavaScript, so pages must be rendered HTML. It ships with version 1.1 and is not on NuGet yet.
  - q: Does it handle Arabic and right to left text?
    a: Yes, from version 1.1. Text goes through the Unicode bidirectional algorithm and an OpenType shaper, so Arabic letters join and take their contextual forms, and right to left text copies and searches in logical order.
  - q: Can it create PDF/A files?
    a: Yes, from version 1.1. Saving with PdfSaveOptions.Conformance writes PDF/A-2b, 2u, 3b or 3u, after checking what cannot be repaired and listing every problem at once.
  - q: Can it make scanned PDFs searchable?
    a: Yes, with the PdfNetStack.Ocr add-on, for Latin and Arabic script. It is in the repository and is not on NuGet yet. The core library also has DrawInvisibleText to lay the words of any OCR engine over a scan.
  - q: How much memory does it use?
    a: The whole document is held in memory. Plan for about 10 times the file size, more for image heavy files, or lower MaxDegreeOfParallelism to cap memory.
  - q: Is it safe to run on files from untrusted sources?
    a: That is a design goal. Nesting depth, decoded stream sizes, image sizes and loops in the file structure are bounded, and damaged input raises PdfFormatException instead of crashing or hanging. The HTML converter fetches nothing from the network unless you give it a resource loader.
  - q: Which version should I use today?
    a: Version 1.0.0 on NuGet has the optimizer, page editing, encryption and the command line tool. The other features on this site are in the main branch and ship with 1.1; until then, build against the source.
  - q: Is PdfNetStack related to the PDFNet SDK?
    a: No. PDFNet is a commercial SDK from Apryse. PdfNetStack is an independent open source project by JADEV with no connection to it.
  - q: Who maintains PdfNetStack?
    a: Jamal Abdelkhalek at JADEV created it and maintains it. Contributions are welcome under the contribution guide in the repository.
---

Short answers to the questions people ask before they adopt PdfNetStack. Each one links to the
page with the details below.

## Where to read more

- Licensing and dependencies: [About](/about/)
- Platforms and installation: [Getting started](/getting-started/)
- Compression results and method: [Compress PDF](/guides/compress-pdf/), [Benchmarks](/benchmarks/),
  [Fidelity and verification](/concepts/fidelity/)
- Encrypted, signed and damaged files: [Safety model](/concepts/safety-model/)
- HTML to PDF: [HTML to PDF](/guides/html-to-pdf/) and [CSS support](/guides/html-to-pdf/css-support/)
- Arabic and right to left text: [Fonts and text](/guides/fonts-and-text/)
- PDF/A: [PDF/A](/guides/pdf-a/)
- OCR: [OCR](/guides/ocr/)
- Memory: [Memory and performance](/concepts/memory-and-performance/)
- What comes next: [Roadmap](/roadmap/)

A question that is not answered here? Ask it in the
[issues](https://github.com/jams4code/pdf.netstack/issues) of the repository.
