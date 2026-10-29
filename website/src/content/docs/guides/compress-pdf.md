---
title: Compress PDF files in C# and .NET
description: Shrink PDFs losslessly in .NET with object streams, deduplication, resource pruning and JPEG re-encoding, every output verified. MIT, no Ghostscript.
since: '1.0'
sidebar:
  label: Compress PDF
faq:
  - q: Is PdfNetStack compression lossless?
    a: Yes, by default. Text, vectors, fonts, images, forms, tags and metadata are carried over, and every output is compared to its source before it is returned. The lossy image options (JPEG requantization, downsampling, JPEG conversion) are opt-in.
  - q: How much smaller do PDFs get?
    a: On a mixed corpus of 1,296 real world files the optimized ones went from 568.9 MiB to 469.0 MiB, 17.6% saved, losslessly. Image heavy files often gain 20 to 40%. Files from producers that already use object streams gain little.
  - q: Can it downsample images?
    a: Yes, from version 1.1, with MaxImageDpi. Images drawn above 1.5 times the target resolution are resampled with a Lanczos filter, and MinImageSsim keeps any image the change would visibly hurt.
  - q: Can PdfNetStack write linearized PDFs for fast web view?
    a: Yes, from version 1.1, with Linearize = true or pdfnetstack optimize --linearize. The file is laid out first page first with hint tables, as ISO 32000-1 Annex F describes, and verified like any other rewrite.
  - q: Does the optimizer need Ghostscript or another native tool?
    a: No. It is one managed assembly with no native code and no external process, on Windows, Linux and macOS.
---

PdfNetStack makes PDF files smaller without changing a rendered pixel, then proves it before it
hands you the result.

## Short answer

Call `PdfOptimizer.OptimizeFileAsync` with an input and an output path. The optimizer rewrites the
file with compressed object streams and a compressed cross-reference stream, drops unreachable
objects and unused resources, rewrites content streams compactly, recompresses streams,
re-encodes JPEGs losslessly and merges duplicates. It then reads its
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
| `Reason` | A sentence that explains the disposition and names every lossy change |
| `Statistics` | Per pass counters and timings, such as objects merged, JPEG images re-encoded and images downsampled |

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
| `Compression` | `Maximum` | `Fast` and `Balanced` trade size for speed: `Balanced` cut corpus time by about a third for about 2% larger output. `Exhaustive` (1.1) searches harder, see below |
| `UseObjectStreams` | true | Off writes a classic xref table |
| `ObjectsPerStream` | 200 | Larger compresses better, smaller loads single pages faster |
| `DeduplicateObjects` | true | Merges identical fonts, images, ICC profiles, resources and arrays; from 1.1 streams compare by decoded content |
| `PruneUnusedResources` | true | 1.1. Removes resource entries no content names |
| `CompactContentStreams` | true | 1.1. Rewrites content streams with minimal white space, same operators and operands |
| `MergeFontSubsets` | true | 1.1. Merges subsets of one TrueType font that keep the original glyph ids. Needs `DeduplicateObjects` |
| `Linearize` | false | 1.1. Fast web view. No object streams, so often larger; kept anyway for input not yet linearized |
| `RecompressStreams` | true | Recompresses Flate at zlib level 9, turns LZW and RunLength into Flate |
| `UseImagePredictors` | true | Tries PNG predictors on raw images |
| `OptimizeJpegImages` | true | Lossless JPEG re-encoding |
| `ProgressiveJpeg` | false | 1.1. Also tries progressive coding for every JPEG written, kept when smaller. Lossless |
| `JpegQuality` | null | 1 to 100, libjpeg scale. Lossy: requantizes JPEG images, and sets the quality of new JPEGs |
| `MaxImageDpi` | null | 1.1. Downsamples images drawn above 1.5 times this resolution. Lossy |
| `ConvertImagesToJpeg` | false | 1.1. Photographic raw images to JPEG at `JpegQuality`. Lossy |
| `MinImageSsim` | null | 1.1. Quality gate, 0 to 1: any lossy image change below this SSIM is dropped |
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
| Deduplication | Merges identical objects, iterating until merges stop. From 1.1, streams compare by what they decode to, so differently encoded copies merge | Yes |
| Unused resources | `PruneUnusedResources` (1.1): fonts, XObjects, graphics states, patterns, shadings, color spaces and property lists that no content names are removed. Anything that cannot be read for certain is kept | Yes |
| Content streams | `CompactContentStreams` (1.1): page, form, pattern and appearance content rewritten with minimal white space, canonical numbers, shortest strings and no comments; inline image data copied exactly | Yes |
| Font subsets | `MergeFontSubsets` (1.1): separate subsets of one TrueType font, as merged documents carry them, become one program when no glyph any page shows changes | Yes |
| Linearization | `Linearize` (1.1): first page first, two cross-reference sections and hint tables (Annex F), for fast web view | Yes, opt-in |
| Compact syntax | Minimal separators, canonical numbers, null entries dropped | Yes |
| Progressive JPEG | `ProgressiveJpeg`: every JPEG written is also tried progressive, kept when smaller | Yes, opt-in |
| Exhaustive Flate | `Compression = Exhaustive`: optimal parsing and block splitting in the manner of Zopfli, zlib kept when smaller | Yes, opt-in |
| Thumbnails, PieceInfo | Optional removal of page thumbnails and application private data | No, opt-in |
| JPEG requantization | `JpegQuality`: coarser quantization, never finer than the image's own. Soft masks stay lossless | No, opt-in |
| Downsampling | `MaxImageDpi`: images drawn above 1.5 times the target resolution are resampled with a Lanczos filter | No, opt-in |
| Photographs to JPEG | `ConvertImagesToJpeg`: raw gray and RGB images that look photographic become baseline JPEG | No, opt-in |
| Quality gate | `MinImageSsim`: any lossy image change below this luminance SSIM is dropped | Opt-in |

Without the lossy image options, JPEG data is decoded to DCT coefficients and never to pixels, so
components, chroma sampling and the color model pass through untouched: a CMYK image stays CMYK.
JPEG 2000, JBIG2 and CCITT data is never decoded or touched by the optimizer.

## Lossy image options

:::note[Available since 1.1]
`MaxImageDpi`, `ConvertImagesToJpeg`, `MinImageSsim`, `ProgressiveJpeg` and
`CompressionEffort.Exhaustive` are in the main branch and ship with 1.1. `JpegQuality` is in 1.0.
:::

All of them are off by default. Turn them on together for the biggest saving on image heavy
files:

```csharp sample="compress-lossy.cs" region="lossy"
```

- `MaxImageDpi` measures each image at the largest size it is drawn anywhere: on pages, in forms,
  tiling patterns, soft mask groups and annotation appearances. An image drawn above 1.5 times the
  target is resampled to the target with a three lobe Lanczos filter. A downsampled JPEG stays a
  JPEG, encoded at `JpegQuality` but never finer than it was; raw samples stay lossless after
  resampling unless `ConvertImagesToJpeg` is set.
- `ConvertImagesToJpeg` re-encodes raw 8 bit gray and RGB images as baseline JPEG at `JpegQuality`
  (4:2:0 below quality 90, Huffman tables optimized per image). A classifier leaves line art,
  screenshots, charts, palette and bilevel images lossless, and a JPEG is kept only when it is at
  least 10% smaller than the best lossless encoding. It requires `JpegQuality`.
- `MinImageSsim` is a perceptual gate on luminance SSIM. Any image whose lossy change,
  requantization included, falls below it stays as it was, and `ImagesKeptByQualityGate` counts
  those images. Around 0.95 keeps changes hard to see; 0.90 allows visible softening of fine
  texture.
- `ProgressiveJpeg` changes the coding, not the coefficients, so it is lossless.

The result says what changed:

```csharp sample="compress-lossy.cs" region="report"
```

`OptimizationStatistics` reports `ImagesDownsampled`, `ImagesConvertedToJpeg`,
`JpegImagesRequantized` and `ImagesKeptByQualityGate`, and `Reason` names every lossy change, for
example "1 image(s) downsampled to 150 dpi, 1 image(s) converted to JPEG at quality 90".

### What the proof checks for changed images

Verification still runs on every image the optimizer changed, but for pixels that changed it is a
bound, not an identity. A downsampled or converted image must keep its components and aspect
ratio, must not grow, and is decoded and compared with the source resampled to its new size: each
channel's mean, a coarse thumbnail that catches mirrored, shifted or swapped pictures, and
luminance SSIM of at least `MinImageSsim` (0.5 when no gate is set). Soft masks, masks, stencil
masks and color keyed images must match exactly, and every other part of the document must be
identical as before. The method is on the [fidelity](/concepts/fidelity/) page.

### What is left alone

Masks, images with a color key mask or a /Matte soft mask, bilevel, indexed, 16 bit, Lab,
Separation and DeviceN images keep their size, and so do images reachable from anything the
measurement does not walk, such as Type 3 glyphs and thumbnails. JPEG 2000, JBIG2 and CCITT images
are never changed. A JPEG with a color key mask is never requantized, since the key matches exact
sample values.

These options were measured on synthetic files made by `tools/make_lossy_corpus.py`, not on a real
corpus. The corpus results below are for the lossless passes only.

## Structural passes

Three lossless passes added in 1.1 work on the document's structure rather than on its streams'
compression, and all three are on by default. Unused resources go: merged and edited files often
list fonts and images in a page's resources that no content on it uses. Content streams are
written compactly. Subsets of one TrueType font that merging tools embedded once per document are
merged into one program when every glyph stays the same. The statistics count each of them.

```csharp sample="compress-structure.cs" region="structure"
```

None of them is taken on trust. The verifier runs its own analysis of the source and accepts a
missing resource only where no content can name it, a rewritten content stream only when it reads
as the same tokens, and a merged font only when every glyph shown keeps its outline and metrics.
The rules are on the [fidelity](/concepts/fidelity/) page. Turn a pass off with its option, or
`--no-prune`, `--no-compact` and `--no-font-merge` on the command line.

## Fast web view

`Linearize = true` writes a linearized file (ISO 32000-1, Annex F): the first page and what it
needs come first, with hint tables, so a viewer can show it before the whole file has arrived.
Inherited page attributes move onto the pages, and every object is written on its own, without
object streams, so the output can be larger than the input. A linearized rewrite of a file that
is not linearized yet is kept even then, provided it verifies. `Statistics.Linearized` says
whether the output is linearized; a page tree that cannot be laid out page by page, such as one
that lists a page twice, is written as usual and the reason says why.

```csharp sample="compress-structure.cs" region="linearize"
```

The tests read every linearized file back and check the linearization dictionary, both
cross-reference sections and every hint table entry against the real offsets.
`tools/measure_optimizer.py --linearize` also runs qpdf's linearization check when qpdf is
installed.

## Exhaustive Flate

`CompressionEffort.Exhaustive` is an original Deflate encoder in the manner of Zopfli: optimal
parsing over every match length with costs refined over several passes, block splitting, and code
lengths limited by package merge. Output stays standard Flate, and zlib's result is kept whenever
it is smaller. On the synthetic files of `tools/make_lossy_corpus.py` it saved 3 to 5% more than
level 9 on text, scans and photographs, and nothing on line art and screenshots, for 2 to 150
times the CPU time. It suits archives written once and read often.

```csharp sample="compress-lossy.cs" region="exhaustive"
```

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
first difference. An overload takes `allowJpegRequantization` and `minImageSsim`, and then also
accepts requantized, downsampled and converted images within the bounds above:

```csharp sample="compress-lossy.cs" region="check"
```

The command line equivalent is `pdfnetstack verify original.pdf other.pdf --min-ssim 0.95`. The
method and its limits are on the [fidelity](/concepts/fidelity/) page.

## Results

Measured on a mixed real world corpus of 1,296 files (569 MiB) with default options, lossless, on
an 8 core Windows machine:

| | |
|---|---|
| Optimized | 1,003 files, 568.9 MiB to 469.0 MiB (17.6% saved) |
| Returned unchanged | 38 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs |
| Verification failures | 0 |
| Throughput | about 65 s for the whole corpus, 8 to 9 MB/s |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,118 files optimized, 20.1% saved |

Every optimized file was rendered with PDFium at 150 DPI and compared with its original: 2,161
pages, zero differing pixels. See [Benchmarks](/benchmarks/) for the method. These figures were
measured with version 1.0, before the structural passes of 1.1, which have so far been measured
on synthetic files (`tools/make_optimizer_samples.py` and `tools/measure_optimizer.py`).

## Limits

- Lossy image options handle 8 bit gray, RGB and CMYK images, raw or JPEG. JPEG 2000, JBIG2,
  CCITT, 16 bit, indexed, Lab, Separation and DeviceN images keep their resolution, and images
  inside Type 3 glyphs are never downsampled. The quality gate measures luminance only.
- Arithmetic coded, 12 bit, lossless and hierarchical JPEGs, JPEGs with more than 64 scans, and
  damaged JPEG data are carried over untouched.
- The optimizer does not subset fonts. It merges subsets of one font only when they keep the
  original glyph ids.
- Linearization is opt-in. Without `Linearize`, the linearization of an input is dropped.
- The optimizer holds the whole document in memory: plan for about 10 times the file size, see
  [Memory and performance](/concepts/memory-and-performance/). To read or edit a large file
  without loading it, see [Large files](/guides/large-files/).

## Related

- [Command line](/cli/): `optimize`, `batch`, `inspect` and `verify`
- [Large files](/guides/large-files/): open on demand and save incremental updates
- [Linearize a PDF for fast web view](/recipes/linearize-pdf/)
- [Reduce PDF size for email](/recipes/reduce-pdf-size/) and
  [Downsample images in a PDF](/recipes/downsample-pdf-images/)
- [Edit pages](/guides/edit-pages/): saving an edited document runs the optimizer too
- [Fidelity and verification](/concepts/fidelity/)
- API: [PdfOptimizer](/api/pdfnetstack/pdfoptimizer/), [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [PdfOptimizationResult](/api/pdfnetstack/pdfoptimizationresult/), [OptimizationStatistics](/api/pdfnetstack/optimizationstatistics/), [PdfEquivalence](/api/pdfnetstack/pdfequivalence/)
