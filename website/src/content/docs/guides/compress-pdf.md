---
title: Compress PDF files in C# and .NET
description: Shrink PDFs losslessly in .NET with object streams, deduplication and JPEG re-encoding, every output verified. MIT licensed, no Ghostscript.
since: '1.0'
sidebar:
  label: Compress PDF
faq:
  - q: Is PdfNetStack compression lossless?
    a: Yes, by default. Text, vectors, fonts, images, forms, tags and metadata are carried over, and every output is compared to its source before it is returned. Lossy JPEG requantization is opt-in through JpegQuality.
  - q: How much smaller do PDFs get?
    a: On a mixed corpus of 1,296 real world files the optimized ones went from 568.9 MiB to 469.0 MiB, 17.6% saved. Image heavy files often gain 20 to 40%. Files from producers that already use object streams gain little.
  - q: Does the optimizer need Ghostscript or another native tool?
    a: No. It is one managed assembly with no native code and no external process, on Windows, Linux and macOS.
---

PdfNetStack makes PDF files smaller without changing a rendered pixel, then proves it before it
hands you the result.

## Short answer

Call `PdfOptimizer.OptimizeFileAsync` with an input and an output path. The optimizer rewrites the
file with compressed object streams and a compressed cross-reference stream, drops unreachable
objects, recompresses streams, re-encodes JPEGs losslessly and merges duplicates. It then reads its
own output back and compares it to the source object by object. When anything differs, or the
saving is under 1%, the output is your original bytes.

```csharp sample="compress.cs"
```

```bash
dotnet add package PdfNetStack
```

The same job on the command line is `pdfnetstack optimize in.pdf out.pdf`, see
[Command line](/cli/).

## What the result tells you

`PdfOptimizationResult` reports what was written and why.

```csharp sample="compress-options.cs" region="result"
```

| Property | Meaning |
|---|---|
| `Disposition` | `Optimized` (a smaller, verified rewrite), `OriginalRetained` (no worthwhile saving, or verification failed) or `UnsupportedRetained` (refused by the safety rules) |
| `OriginalBytes`, `OutputBytes`, `SavingPercent` | Sizes before and after |
| `SourceSha256`, `OutputSha256` | Hashes of the input and of what was written |
| `PageCount` | Pages in the document |
| `Reason` | A sentence that explains the disposition |
| `Statistics` | Per pass counters and timings, such as objects merged and JPEG images re-encoded |

The output is always a valid PDF: either the verified rewrite or the input unchanged.

## Bytes, streams and files

Three entry points cover the usual cases. All of them take the same options and return the same
result.

```csharp sample="compress-options.cs" region="memory"
```

`OptimizeFileAsync` writes the output file. `OptimizeAsync` reads a stream and writes another one,
which suits blob storage and HTTP bodies. `Optimize` works on a byte array and returns the bytes.

## Options

Pass a `PdfOptimizerOptions` to change the defaults.

```csharp sample="compress-options.cs" region="options"
```

| Option | Default | Notes |
|---|---|---|
| `MinSavingPercent` | 1.0 | Keep the original unless the rewrite saves at least this much |
| `Compression` | `Maximum` | `Balanced` cut corpus time by about a third for about 2% larger output |
| `UseObjectStreams` | true | Off writes a classic xref table |
| `ObjectsPerStream` | 200 | Larger compresses better, smaller loads single pages faster |
| `DeduplicateObjects` | true | Merges byte identical fonts, images, ICC profiles, resources and arrays |
| `RecompressStreams` | true | Recompresses Flate at zlib level 9, turns LZW and RunLength into Flate |
| `UseImagePredictors` | true | Tries PNG predictors on raw images |
| `OptimizeJpegImages` | true | Lossless JPEG re-encoding |
| `JpegQuality` | null | 1 to 100, libjpeg scale. Lossy, JPEG images only |
| `Verify` | true | Disable only for inputs you already trust |
| `AllowIncrementalUpdates` | false | Flattens edit history into one revision |
| `AllowRepairedFiles` | false | Rewrites damaged files into clean ones |
| `RemovePageThumbnails` | false | Not lossless |
| `RemovePieceInfo` | false | Not lossless for the authoring application, invisible to viewers |
| `MaxInputBytes` | 1 GiB | Larger inputs are returned unchanged |
| `MaxDecodedStreamBytes` | 256 MiB | Per stream decode ceiling, a zip bomb defence |
| `MaxDegreeOfParallelism` | CPU count | Lower it to cap memory on image heavy files |

## What the optimizer does

| Pass | Effect | Lossless |
|---|---|---|
| Garbage collection | Drops objects unreachable from the catalog, including old revisions | Yes |
| Object streams | Packs non-stream objects into Flate compressed object streams (PDF 1.5) | Yes |
| Xref stream | Replaces the text xref table with a predictor encoded, compressed stream | Yes |
| Stream recompression | Removes ASCIIHex and ASCII85 layers, turns LZW and RunLength into Flate, recompresses Flate at level 9, keeps a result only when smaller | Yes |
| Image prediction | Tries PNG predictors on raw images of 8 bits or less per component | Yes |
| JPEG re-encoding | Rebuilds Huffman tables per image, writes progressive and restart marked JPEGs as baseline, drops segments PDF readers ignore. The DCT coefficients stay identical | Yes |
| Deduplication | Merges byte identical objects, iterating until merges stop | Yes |
| Compact syntax | Minimal separators, canonical numbers, null entries dropped | Yes |
| Thumbnails, PieceInfo | Optional removal of page thumbnails and application private data | No, opt-in |
| JPEG requantization | `JpegQuality`: coarser quantization, never finer than the image's own. Soft masks stay lossless | No, opt-in |

JPEG data is decoded to DCT coefficients and never to pixels, so components, chroma sampling and
the color model pass through untouched: a CMYK image stays CMYK. JPEG 2000, JBIG2 and CCITT data
is never touched.

## Smaller files with lossy JPEG

`JpegQuality` requantizes JPEG images at a libjpeg quality between 1 and 100. It never makes an
image finer than it was, works on the coefficients so the color model cannot change, and leaves
soft masks lossless. Verification still runs: each coefficient must be exactly the requantized
source value, and everything else must be identical. `Statistics.JpegImagesRequantized` counts the
images it changed. The command line takes the same setting as `--jpeg-quality 60`.

## Files the optimizer will not rewrite

The optimizer returns the input unchanged, with `UnsupportedRetained` and a reason, when the file
is encrypted, digitally signed or carries usage rights, incrementally updated (unless
`AllowIncrementalUpdates` is set), damaged and repaired on load (unless `AllowRepairedFiles` is
set), or larger than `MaxInputBytes`. The [safety model](/concepts/safety-model/) explains each
rule.

## Check another tool's output

The equivalence check the optimizer runs on itself is public. Use it on the output of any PDF
tool:

```csharp sample="verify.cs" region="short"
```

`FindDifference` returns `null` when the two documents are equivalent, or a description of the
first difference. The method and its limits are on the [fidelity](/concepts/fidelity/) page.

## Results

Measured on a mixed real world corpus of 1,296 files (569 MiB) with default options, on an 8 core
Windows machine:

| | |
|---|---|
| Optimized | 1,003 files, 568.9 MiB to 469.0 MiB (17.6% saved) |
| Returned unchanged | 38 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs |
| Verification failures | 0 |
| Throughput | about 65 s for the whole corpus, 8 to 9 MB/s |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,118 files optimized, 20.1% saved |

Every optimized file was rendered with PDFium at 150 DPI and compared with its original: 2,161
pages, zero differing pixels. See [Benchmarks](/benchmarks/) for the method.

## Limits

- Lossy compression covers JPEG images only. It does not downsample and does not convert raw
  images to JPEG.
- Arithmetic coded, 12 bit, lossless and hierarchical JPEGs, JPEGs with more than 64 scans, and
  damaged JPEG data are carried over untouched.
- Font subsetting, unused resource pruning and content stream rewriting are not done.
- Linearized (fast web view) output is not produced; linearization is dropped.
- The whole document is held in memory: plan for about 10 times the file size, see
  [Memory and performance](/concepts/memory-and-performance/).

## Related

- [Command line](/cli/): `optimize`, `batch`, `inspect` and `verify`
- [Edit pages](/guides/edit-pages/): saving an edited document runs the optimizer too
- [Fidelity and verification](/concepts/fidelity/)
- API: [PdfOptimizer](/api/pdfnetstack/pdfoptimizer/), [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [PdfOptimizationResult](/api/pdfnetstack/pdfoptimizationresult/), [PdfEquivalence](/api/pdfnetstack/pdfequivalence/)
