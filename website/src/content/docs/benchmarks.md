---
title: PDF compression benchmarks
description: PdfNetStack compression measured on a public corpus at every release and on 1,296 real world PDFs, with the method and how to reproduce it.
sidebar:
  label: Benchmarks
---

What the optimizer saves on a public corpus that CI measures at every release, what it saved on a
larger private corpus, and how to run the same measurement yourself.

## Short answer

On the public corpus of 77 openly licensed PDFs, release 1.1.0 saved 12.1% losslessly, 23.4%
with the balanced preset and 29.5% with the lossy profile, with zero failures. On a mixed private
corpus of 1,296 files (569 MiB) with default options, PdfNetStack optimized 1,003 files
from 568.9 MiB to 469.0 MiB, 17.6% saved, with zero verification failures. Every optimized file
rendered identically to its original with PDFium at 150 DPI. The run took about 65 seconds on an
8 core machine.

## Public corpus

CI downloads the documents listed in
[eng/corpus/manifest.json](https://github.com/jams4code/pdf.netstack/blob/main/eng/corpus/manifest.json):
public domain and openly licensed PDFs such as NIST and NASA publications, arXiv papers,
government gazettes and test files from other PDF projects. They are never committed. The run
happens every week, on demand, and for every version tag, when the report is attached to the
GitHub release.

Each file runs in its own process under three profiles:

- **Lossless.** The default optimizer.
- **Balanced.** The preset of that name: palettes for drawings and screenshots, JPEG quality 75 at
  full resolution, behind an SSIM gate of 0.95.
- **Lossy.** JPEG quality 75, downsampling to 150 dpi, photographs to JPEG and progressive JPEG,
  behind the same gate.

Every kept rewrite is checked again with `PdfEquivalence`, the text of every page and every image
are compared with the original, and PDFium renders the first pages and the last page of both. A
lossless rewrite must render identically.

Release 1.1.0, 5 November 2026, on a 4 core Linux runner:

| Profile | Documents optimized | Input | Output | Saved | Optimizer time |
|---|---:|---:|---:|---:|---:|
| Lossless | 65 of 77 | 196.6 MiB | 172.9 MiB | 12.1% | 44.4 s |
| Balanced | 65 of 77 | 196.6 MiB | 150.6 MiB | 23.4% | 154.5 s |
| Lossy | 65 of 77 | 196.6 MiB | 138.6 MiB | 29.5% | 497.4 s |

Zero failures across the 77 documents. The 12 documents kept as they are were signed or encrypted
files, which the optimizer never rewrites. The report with every document, its time, memory and
render result is attached to the
[1.1.0 release](https://github.com/jams4code/pdf.netstack/releases/tag/v1.1.0) as
`corpus-report.md` and `corpus-report.json`.

To run it yourself:

```bash
pip install pypdfium2 numpy
python tools/fetch_corpus.py --cache artifacts/corpus/files
dotnet build tools/PdfNetStack.CorpusRunner -c Release
python tools/corpus_bench.py --cache artifacts/corpus/files --out artifacts/corpus/report
```

## Private corpus

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

The figures are those published with version 1.0.0, in October 2026, and cover the lossless
passes only. The lossy profiles are measured on the public corpus above.

### Results

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

## Measure your own files

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

Each release report is compared with the previous one, so a change in size, time or memory shows
next to the release. This page shows no numbers for other tools.

## Related

- [Compress PDF](/guides/compress-pdf/)
- [Fidelity and verification](/concepts/fidelity/)
- [Memory and performance](/concepts/memory-and-performance/)
