---
title: PDF command line tool for .NET
description: Compress, merge, split, rotate, encrypt and inspect PDF files from the command line on Windows, Linux and macOS with pdfnetstack. MIT, no Ghostscript.
since: '1.0'
sidebar:
  label: Command line
faq:
  - q: How do I install the pdfnetstack command?
    a: Run dotnet tool install -g PdfNetStack.Tool. It needs the .NET 8 or later runtime and works on Windows, Linux and macOS.
  - q: Can pdfnetstack compress a whole folder of PDFs?
    a: Yes. pdfnetstack batch <input-dir> <output-dir> optimizes every PDF under the folder, subfolders included, and --report writes one CSV row per document. Without an output folder it only measures.
---

`pdfnetstack` compresses, edits and protects PDF files from a terminal or a script, with the same
engine and the same equivalence proof as the library.

## Short answer

Install the tool once, then run a command per job. `optimize` is lossless by default and returns
the original bytes when a rewrite would not be verified equivalent or would save less than 1%.

```bash
dotnet tool install -g PdfNetStack.Tool
pdfnetstack optimize in.pdf out.pdf
pdfnetstack batch ./incoming ./optimized --report report.csv
```

It needs the .NET 8 or later runtime. `pdfnetstack --help` prints every command and option.

## Commands at a glance

| Command | Does |
|---|---|
| [`optimize`](#optimize) | Compresses one file |
| [`batch`](#batch) | Compresses or measures every PDF in a folder tree |
| [`inspect`](#inspect) | Shows where the bytes of a file go |
| [`verify`](#verify) | Checks that two files are equivalent |
| [`merge`](#merge) | Joins files |
| [`split`](#split) | Writes one file per page |
| [`extract`](#extract) | Copies a page range into a new file |
| [`rotate`](#rotate) | Rotates pages |
| [`meta`](#meta) | Reads or writes the title, author, subject and keywords |
| [`encrypt`](#encrypt) | Adds password protection |
| [`decrypt`](#decrypt) | Removes password protection |
| [`ocr`](#ocr) | Makes scanned pages searchable (separate package, 1.1) |

## optimize

```bash
pdfnetstack optimize <input.pdf> <output.pdf> [options]
pdfnetstack optimize in.pdf out.pdf --jpeg-quality 60   # lossy, JPEG images only
pdfnetstack optimize in.pdf out.pdf --max-dpi 150 --to-jpeg --jpeg-quality 75 --min-ssim 0.95
pdfnetstack optimize in.pdf out.pdf --exhaustive        # archival: smaller Flate, much slower
pdfnetstack optimize in.pdf out.pdf --linearize         # fast web view
pdfnetstack optimize in.pdf out.pdf --json              # the full result as JSON
```

The lossy image options (`--max-dpi`, `--to-jpeg`, `--min-ssim`), `--progressive`,
`--exhaustive`, `--linearize` and the structural passes with their `--no-prune`, `--no-compact`
and `--no-font-merge` switches are in the main branch and ship with 1.1; the 1.0.0 tool on NuGet
does not have them. The reason line names every lossy change.

Prints the disposition, the sizes, the saving and the time, the reason, and for an optimized file
the object counts and the time per phase. The output file is always written: the verified
rewrite, or the input unchanged. Input and output must be different files.
The library equivalent is [`PdfOptimizer.OptimizeFileAsync`](/guides/compress-pdf/).

## batch

```bash
pdfnetstack batch <input-dir> [output-dir] [options]
pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./corpus                  # measure only, writes nothing
```

Finds every `.pdf` under the input folder, subfolders included, and writes each result to the same
relative path under the output folder. Without an output folder nothing is written: the run
measures what the optimizer would save. Documents run in parallel (`--jobs`, default all cores),
each on one thread unless `--threads` is given.

The summary lists documents optimized, retained and unsupported (grouped by reason), errors, the
total size before and after, throughput and peak memory, and any verification failures, for which
the original was kept. `--report <file.csv>` writes one row per document with the columns `file`,
`disposition`, `original_bytes`, `output_bytes`, `pages`, `milliseconds` and `reason`. A value that
starts with a formula character is prefixed with a quote, so spreadsheet applications never
evaluate it. `--json` prints the summary as JSON.

## inspect

```bash
pdfnetstack inspect <input.pdf> [--json]
```

Shows the size, PDF version, page count, objects (and how many are reachable), revisions and
linearization, the cross-reference format, whether the file needed repair, is encrypted, signed or
PDF/A-1, then the streams grouped by kind with their share of the file, and the filters used.
The library equivalent is `PdfInspector.Inspect`.

## verify

```bash
pdfnetstack verify <original.pdf> <candidate.pdf>
pdfnetstack verify original.pdf other.pdf --jpeg-quality 60   # accept JPEG requantization
pdfnetstack verify original.pdf other.pdf --min-ssim 0.95     # accept downsampled and converted images (1.1)
```

Runs the equivalence check the optimizer runs on itself, on the output of any tool. Prints
`Equivalent.` and exits 0, or `Different:` with the first difference and exits 1. With
`--jpeg-quality`, JPEG images may also differ by exactly the requantization the optimizer applies.
With `--min-ssim`, downsampled and converted images are accepted when they keep their components
and aspect, are no larger, keep their mean colors and reach that SSIM against the original
resampled to their size; requantized images must then reach it too.
See [Fidelity and verification](/concepts/fidelity/).

## merge

```bash
pdfnetstack merge <output.pdf> <input.pdf>...
pdfnetstack merge out.pdf a.pdf b.pdf c.pdf
```

The output comes first, then the inputs in order.

## split

```bash
pdfnetstack split <input.pdf> <output-dir>
```

Writes one file per page, named after the input with a zero padded page number, such as
`report-01.pdf` to `report-12.pdf`.

## extract

```bash
pdfnetstack extract <input.pdf> <output.pdf> <pages>
pdfnetstack extract in.pdf out.pdf 1-3,7,9-
```

Pages are numbered from 1. Ranges are separated by commas: `9-` runs to the last page, `-3` starts
at the first, and `5-2` runs backwards. The order given is kept and pages may repeat.

## rotate

```bash
pdfnetstack rotate <input.pdf> <output.pdf> <degrees> [pages]
pdfnetstack rotate in.pdf out.pdf 90 2-4
```

Rotates clockwise by a multiple of 90 degrees, every page or the given ranges.

## meta

```bash
pdfnetstack meta <input.pdf>                                 # print
pdfnetstack meta in.pdf out.pdf --title "Annual report" --author "Finance"
```

Without an output file, prints the page count, every document information entry and the number of
top level bookmarks. Text from the document is printed with control and bidirectional override
characters escaped, so a crafted title cannot drive the terminal. With an output file, sets any
of `--title`, `--author`, `--subject` and `--keywords` and saves.

## encrypt

```bash
pdfnetstack encrypt <input.pdf> <output.pdf> --user-password u --owner-password o
                    [--allow print,copy,modify,annotate,forms,accessibility,assemble,print-hq] [--aes128]
```

Writes AES-256 by default, AES-128 with `--aes128`. At least one password is required; an empty
owner password is replaced by a random one. Without `--allow` every permission is granted;
`--allow none` grants none, and `print-hq` includes `print`. See [Encryption](/guides/encryption/).

## decrypt

```bash
pdfnetstack decrypt <input.pdf> <output.pdf> --password <owner password>
```

Saves the document without protection. It needs the owner password.

## ocr

```bash
pdfnetstack ocr <input.pdf> <output.pdf> [--languages latin,arabic] [--models <folder>] [options]
pdfnetstack-ocr <input.pdf> <output.pdf> [options]    # the same tool, run directly
```

In the main branch, ships with 1.1. Makes scanned pages searchable: the scan on each page is read
and its words are laid over it as invisible text, as the [OCR guide](/guides/ocr/) describes.
OCR needs ONNX Runtime's native libraries and about 21 MB of models, so it is a tool of its own,
`pdfnetstack-ocr`, in the `PdfNetStack.Ocr.Tool` package (about 70 MB, models included).
`pdfnetstack ocr` runs it with the same arguments, found next to `pdfnetstack` or on the `PATH`,
and says how to install it when it is missing:

```bash
dotnet tool install -g PdfNetStack.Ocr.Tool
```

| Option | Effect | Library option |
|---|---|---|
| `--languages <list>` | Scripts to read, comma separated: `latin`, `arabic` (default `latin`) | `Languages` |
| `--models <folder>` | Load the models from this folder instead of the ones installed with the tool | `OnnxOcrEngine.Load(folder)` |
| `--min-confidence <0-1>` | Leave out lines read with less confidence (default 0.5) | `MinimumConfidence` |
| `--no-orientation` | Do not look for pages scanned sideways or upside down | `DetectOrientation = false` |
| `--all-pages` | Also read pages that already show text | `SkipPagesWithText = false` |
| `--threads <n>` | Pages recognized at once (default: all cores) | `MaxDegreeOfParallelism` |
| `--text <file.txt>` | Also write the text found, pages apart by a blank line | `PdfOcrReport.Text` |
| `--password <password>` | Open an encrypted input | |
| `--no-optimize` | Save without the optimizer pass | |
| `--json` | Per page report: status, reason, orientation, lines and text | |

Prints one line per page (lines and words read, how far a sideways page was turned, or why it was
skipped) and the output size. The output is written when the run completes, even if some pages
failed; it exits 1 in that case. Pages that already show text are left as they are, so a second
run changes nothing.

## Options

Optimizer options apply to `optimize`, `batch` and, where they make sense, to `verify`.

| Option | Effect | Library option |
|---|---|---|
| `--fast`, `--balanced` | Faster Flate levels (default: maximum) | `Compression` |
| `--exhaustive` | 1.1. Exhaustive Flate search for archival jobs: a few percent smaller, many times slower | `Compression = Exhaustive` |
| `--no-object-streams` | Classic xref table, no object streams | `UseObjectStreams = false` |
| `--no-dedup` | Do not merge identical objects | `DeduplicateObjects = false` |
| `--no-prune` | 1.1. Keep resources no content uses | `PruneUnusedResources = false` |
| `--no-compact` | 1.1. Do not rewrite content streams compactly | `CompactContentStreams = false` |
| `--no-font-merge` | 1.1. Do not merge subsets of one TrueType font | `MergeFontSubsets = false` |
| `--linearize` | 1.1. Write a linearized file (fast web view) | `Linearize` |
| `--no-recompress` | Do not re-encode streams | `RecompressStreams = false` |
| `--no-image-predictors` | Do not try PNG prediction on raw images | `UseImagePredictors = false` |
| `--no-jpeg` | Do not re-encode JPEG images | `OptimizeJpegImages = false` |
| `--jpeg-quality <1-100>` | Requantize JPEG images, and the quality of new JPEGs (lossy) | `JpegQuality` |
| `--max-dpi <n>` | 1.1. Downsample images drawn above 1.5 times n dpi to n (lossy) | `MaxImageDpi` |
| `--to-jpeg` | 1.1. Re-encode photographic raw images as JPEG at `--jpeg-quality` (lossy) | `ConvertImagesToJpeg` |
| `--min-ssim <0-1>` | 1.1. Keep any image whose lossy change falls below this SSIM | `MinImageSsim` |
| `--progressive` | 1.1. Write JPEG progressively when that is smaller (lossless) | `ProgressiveJpeg` |
| `--no-verify` | Skip the equivalence proof (not recommended) | `Verify = false` |
| `--allow-incremental` | Flatten files with incremental updates | `AllowIncrementalUpdates` |
| `--allow-repaired` | Rewrite damaged files that needed repair | `AllowRepairedFiles` |
| `--strip-thumbnails` | Remove page thumbnails (not lossless) | `RemovePageThumbnails` |
| `--strip-pieceinfo` | Remove application private data (not lossless) | `RemovePieceInfo` |
| `--min-saving <percent>` | Minimum saving to keep a rewrite (default 1) | `MinSavingPercent` |
| `--threads <n>` | Threads per document (default: all cores) | `MaxDegreeOfParallelism` |
| `--jobs <n>` | Documents in parallel for `batch` (default: all cores) | |
| `--report <file.csv>` | `batch`: one row per document | |
| `--json` | Machine readable output | |

The editing commands (`merge`, `split`, `extract`, `rotate`, `meta`, `encrypt`, `decrypt`) save
through the lossless optimizer; `--no-optimize` skips that pass, and `--fast` or `--balanced` set
the Flate level. Every editing command accepts `--password` to open an encrypted input.

## Full help text

The output of `pdfnetstack --help`, captured from the tool built from the same source as this
page:

```text generated="cli-help.txt"
```

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. For `verify`: the files are equivalent |
| 1 | `verify`: the files differ. `ocr`: the output was written, but some pages failed |
| 2 | Wrong arguments, an unreadable PDF, a wrong password, or a file error. The message goes to standard error |

An `optimize` or `batch` run that keeps the original, because the saving is too small or the file
is refused by the [safety model](/concepts/safety-model/), is a success: the output is a valid PDF.

## Limits

- The tool works on files only; it has no stdin or stdout mode.
- Lossy options handle 8 bit gray, RGB and CMYK images, raw or JPEG; `--to-jpeg` needs
  `--jpeg-quality`. JPEG 2000, JBIG2, CCITT, 16 bit, indexed, Lab, Separation and DeviceN images
  keep their resolution.
- The optimizer holds each document in memory while it processes it. For large image heavy files, lower
  `--threads` or `--jobs`, see [Memory and performance](/concepts/memory-and-performance/).

## Related

- [Compress PDF](/guides/compress-pdf/): the optimizer from C#
- [Linearize a PDF for fast web view](/recipes/linearize-pdf/)
- [Edit pages](/guides/edit-pages/): merge, split and extract from C#
- [Benchmarks](/benchmarks/): how `batch` reports were used to measure a corpus
- [OCR](/guides/ocr/): the same from C#, and what happens to each page
- API: [PdfOptimizer](/api/pdfnetstack/pdfoptimizer/), [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [PdfInspector](/api/pdfnetstack/pdfinspector/), [PdfEquivalence](/api/pdfnetstack/pdfequivalence/)
