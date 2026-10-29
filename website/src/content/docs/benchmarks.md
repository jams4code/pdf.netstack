---
title: PDF compression benchmarks
description: How PdfNetStack compression was measured on 1,296 real world PDFs, the results, and how to reproduce the run and the render check on your own files.
sidebar:
  label: Benchmarks
---

What the optimizer saved on a real world corpus, how that was measured, and how to run the same
measurement on your own files.

## Short answer

On a mixed corpus of 1,296 files (569 MiB) with default options, PdfNetStack optimized 1,003 files
from 568.9 MiB to 469.0 MiB, 17.6% saved, with zero verification failures. Every optimized file
rendered identically to its original with PDFium at 150 DPI. The run took about 65 seconds on an
8 core machine.

## Method

- **Corpus.** 1,296 files, 569 MiB: invoices, reports, manuals, scanned documents, presentations
  and a 631 page spare parts catalogue. The corpus is not public.
- **Machine.** An 8 core Windows machine.
- **Options.** Defaults: lossless, `CompressionEffort.Maximum`, verification on, 1% minimum saving.
  A second run added `AllowIncrementalUpdates` and `AllowRepairedFiles`.
- **Lossless** means a conforming reader cannot tell the output from the input: same pages,
  content, fonts, images, annotations, forms, bookmarks, structure, metadata and attachments.
- **Verified** means the optimizer parsed its own output and proved it equivalent to the source,
  object by object, with every stream compared after decoding and JPEG images compared by DCT
  coefficient. A file that fails is returned unchanged and counted as a verification failure. See
  [Fidelity and verification](/concepts/fidelity/).
- **Render check.** Independent of the verifier: both files rendered with PDFium at 150 DPI,
  compared pixel for pixel, with page count, geometry and extracted text per page.

The figures are those published with version 1.0.0, in October 2026.

## Results

| | |
|---|---|
| Optimized | 1,003 files, total size 568.9 MiB to 469.0 MiB (17.6% saved) |
| Returned unchanged | 38 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs (empty or resource fork files) |
| Verification failures | 0 |
| Throughput | about 65 s for the whole corpus, 8 to 9 MB/s, lossless JPEG re-encoding included |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,118 files optimized, 20.1% saved |
| Render check | 1,118 documents, 2,161 pages, zero differing pixels, identical extracted text |

The 631 page catalogue, 72.6 MiB of CAD drawings already Flate compressed: 6,075 duplicate objects
merged, 3.1% saved, about 5 seconds with all cores, peak working set about 690 MB. Image heavy
files gain the most, often 20 to 40%, mostly from PNG prediction on raw images. Files from
producers that already use object streams have little left to gain losslessly.

Editing was checked the same way: every document in the corpus was rebuilt by importing all of its
pages into a new document and saving it. 1,152 documents, 2,233 pages, zero differing pixels
against the originals at 150 DPI.

## Reproduce it on your files

Measure without writing anything, then write the optimized files and a CSV report:

```bash
dotnet tool install -g PdfNetStack.Tool
pdfnetstack batch ./corpus                                   # measure only
pdfnetstack batch ./corpus ./out --report report.csv         # write and report
```

The summary prints documents optimized, retained and unsupported, the sizes, the time, the
throughput and the peak memory. The CSV has one row per document. See [Command line](/cli/#batch).

Then render both sets with PDFium and compare them. The script is
[tools/render_compare.py](https://github.com/jams4code/pdf.netstack/blob/main/tools/render_compare.py)
in the repository; PDFium is a development tool here, not a dependency of any package.

```bash
pip install pypdfium2 numpy
python tools/render_compare.py ./corpus ./out --dpi 150 --pages 6
```

## What comes next

A public corpus of redistributable PDFs, with size, time, memory and render checks in CI on every
release, is on the [roadmap](/roadmap/). This page shows no numbers for other tools.

## Related

- [Compress PDF](/guides/compress-pdf/)
- [Fidelity and verification](/concepts/fidelity/)
- [Memory and performance](/concepts/memory-and-performance/)
