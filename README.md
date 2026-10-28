# PdfNetStack

Lossless PDF optimizer for .NET. Pure C#, zero runtime dependencies, MIT licensed.

[![CI](https://github.com/jams4code/pdf.netstack/actions/workflows/ci.yml/badge.svg)](https://github.com/jams4code/pdf.netstack/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PdfNetStack.svg)](https://www.nuget.org/packages/PdfNetStack)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

PdfNetStack makes PDFs smaller without changing a single rendered pixel. It rewrites the file
with compressed object streams and a compressed cross-reference stream, drops objects nothing
points to, recompresses streams, adds PNG prediction to raw images, and merges duplicate fonts,
images and resources. Then it reads its own output back and proves, object by object, that the
document is the same as the one you gave it. If anything is off, you get your original bytes back.

```csharp
using PdfNetStack;

var result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");
```

## Why another PDF tool

Most PDF compression in the .NET world shells out to Ghostscript, depends on a copyleft or
commercial library, or re-distills the document and quietly loses forms, tags or metadata.
PdfNetStack takes a narrower job and does it with proof:

- **Lossless by construction.** Text, vectors, fonts, images, annotations, forms, bookmarks,
  structure tags, metadata and attachments are carried over. Nothing is rasterized.
- **Verified, not assumed.** Every rewrite is re-parsed and compared to the source, with stream
  content compared after decoding. A mismatch means the original is returned.
- **Fail safe.** Encrypted, signed and damaged files are returned byte for byte. The output is
  always a valid PDF: the verified rewrite or your input.
- **No native code, no processes.** One managed assembly for .NET 8 and .NET 10, trim and AOT
  compatible, same behaviour on Windows, Linux and macOS.
- **MIT all the way down.** The core package has no dependencies at all. CI blocks any runtime
  package that is not on a reviewed license allowlist.

### Why C# and not a native library

Optimization is mostly parsing, hashing and zlib. Modern .NET does all three at close to native
speed (`Span<T>`, SIMD-backed hashing, zlib-ng under `System.IO.Compression` on .NET 9+), and the
work parallelizes across streams. A Rust or C++ core would mean shipping native binaries per
platform and a marshalling layer, for little measured gain. If profiling ever shows a hot spot
worth it, the pipeline stages are isolated enough to swap one out.

## Install

```bash
dotnet add package PdfNetStack            # library
dotnet tool install -g PdfNetStack.Tool   # command line: pdfnetstack
dotnet add package PdfNetStack.Azure      # optional Blob Storage pipeline
```

## Usage

### Library

```csharp
// In memory
(byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(File.ReadAllBytes("in.pdf"));

// Streams
await using var input = File.OpenRead("in.pdf");
await using var output = File.Create("out.pdf");
PdfOptimizationResult r = await PdfOptimizer.OptimizeAsync(input, output);

// Options
var options = new PdfOptimizerOptions
{
    Compression = CompressionEffort.Balanced,   // faster, slightly larger
    AllowIncrementalUpdates = true,             // flatten edit history into one revision
    MaxDegreeOfParallelism = 2,                 // cap CPU and memory per document
};
```

`PdfOptimizationResult` tells you what was written and why: `Disposition`, sizes, SHA-256 of
input and output, page count, a human readable `Reason`, and per pass statistics and timings.

### Command line

```bash
pdfnetstack optimize in.pdf out.pdf
pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./corpus                  # measure only, writes nothing
pdfnetstack inspect in.pdf                  # where the bytes go
pdfnetstack verify original.pdf other.pdf   # check any tool's output for equivalence
```

### Azure Blob Storage

```csharp
var pipeline = new BlobPdfOptimizer(new Uri("https://account.blob.core.windows.net"));
BlobOptimizationOutcome outcome = await pipeline.OptimizeAsync("incoming", "docs/manual.pdf", "optimized");
```

Uses managed identity through `DefaultAzureCredential`, never account keys. The source blob is
read at a pinned ETag and never modified. Output goes to a different container under
`pdf/<sha256 prefix>/<sha256>.pdf` with a conditional write, so retries and concurrent workers
are idempotent. A JSON sidecar records source URI, ETag, both hashes, sizes, disposition, reason,
engine version and timings.

## What it does

| Pass | Effect | Lossless |
|---|---|---|
| Garbage collection | Drops objects unreachable from the catalog, including old revisions | Yes |
| Object streams | Packs non-stream objects into Flate compressed object streams (PDF 1.5) | Yes |
| Xref stream | Replaces the text xref table with a predictor encoded, compressed stream | Yes |
| Stream recompression | Removes ASCIIHex/ASCII85 layers, turns LZW and RunLength into Flate, recompresses Flate at zlib level 9, keeps a result only when smaller | Yes |
| Image prediction | Tries PNG predictors on raw images of 8 bits or less per component | Yes |
| Deduplication | Merges byte identical fonts, images, ICC profiles, resource dictionaries and arrays, iterating until merges stop | Yes |
| Compact syntax | Minimal separators, canonical numbers, null entries dropped | Yes |
| Thumbnails, PieceInfo | Optional removal of page thumbnails and application private data | No, opt-in |

JPEG, JPEG 2000, JBIG2 and CCITT data is never decoded or touched.

## Safety model

The optimizer refuses to rewrite, and returns the input unchanged, when the file is:

- encrypted,
- digitally signed or has usage rights (any `/ByteRange`, signature dictionary, `/Perms` or `/DSS`),
- incrementally updated, unless `AllowIncrementalUpdates` is set,
- damaged: xref reconstruction, stream length repair, unreadable objects, or references whose
  generation numbers disagree with the xref, unless `AllowRepairedFiles` is set,
- larger than `MaxInputBytes`.

PDF/A-1 files are detected from their XMP metadata and written with a classic xref table and
no object streams, because PDF/A-1 predates both.

Hostile input is bounded: nesting depth is capped, decoded stream size is capped
(`MaxDecodedStreamBytes`, default 256 MiB) as a zip bomb defence, `/Prev` loops terminate,
wrong stream lengths are recovered from `endstream`, and unreadable objects never crash the run.

The verifier walks the source and output graphs in lockstep from the trailer. Dictionaries
must have the same keys and values, arrays the same items, references must line up, and every
stream must decode to identical bytes with identical remaining filters. Encoding keys
(`/Length`, `/Filter`, `/DecodeParms`) are the only differences it accepts.
See [docs/FIDELITY.md](docs/FIDELITY.md) for the full method and its limits.

## Results

Measured on a mixed real world corpus of 1,296 files (569 MiB): invoices, reports, manuals,
scanned documents, presentations and a 631 page spare parts catalogue, on an 8 core Windows
machine with default options.

| | |
|---|---|
| Optimized | 954 files, total size 568.9 MiB to 474.2 MiB (16.7% saved) |
| Returned unchanged | 87 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs (empty or resource fork files) |
| Verification failures | 0 |
| Throughput | 34 to 41 s for the whole corpus, 14 to 17 MB/s depending on machine load |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,068 files optimized, 19.2% saved |

Independent render check: every optimized file was rendered with PDFium at 150 DPI and
compared with the original. 1,068 documents, 2,085 pages, zero differing pixels, identical
extracted text. The script is in [tools/render_compare.py](tools/render_compare.py).

The 631 page catalogue, 72.6 MiB of CAD drawings already Flate compressed: 6,075 duplicate
objects merged, 3.1% saved, about 5 seconds with all cores, peak working set about 690 MB.
Image heavy files gain the most, often 20 to 40%, mostly from PNG prediction on raw images.

Your mileage depends on what produced the PDF. Files from well tuned producers that already use
object streams have little left to gain losslessly.

## Options

| Option | Default | Notes |
|---|---|---|
| `MinSavingPercent` | 1.0 | Keep the original unless the rewrite saves at least this much |
| `Compression` | `Maximum` | `Balanced` cut corpus time by about a third for about 2% larger output |
| `UseObjectStreams` | true | Off writes a classic xref table |
| `ObjectsPerStream` | 200 | Larger compresses better, smaller loads single pages faster |
| `DeduplicateObjects` | true | |
| `RecompressStreams` | true | |
| `UseImagePredictors` | true | |
| `Verify` | true | Disable only for inputs you already trust |
| `AllowIncrementalUpdates` | false | Flattens edit history into one revision |
| `AllowRepairedFiles` | false | Rewrites damaged files into clean ones |
| `RemovePageThumbnails` | false | Not lossless |
| `RemovePieceInfo` | false | Not lossless for the authoring app, invisible to viewers |
| `MaxInputBytes` | 1 GiB | |
| `MaxDecodedStreamBytes` | 256 MiB | Per stream decode ceiling |
| `MaxDegreeOfParallelism` | CPU count | Lower it to cap memory on image heavy files |

## Limits

- Lossy image recompression is not implemented. It needs an MIT licensed JPEG encoder and a
  visual quality gate, and it is the next major item on the [roadmap](docs/ROADMAP.md).
- Font subsetting, unused resource pruning and content stream rewriting are not done.
- Linearized (fast web view) output is not produced; linearization is dropped.
- The whole document is held in memory. Peak memory depends on decoded image sizes and on
  parallelism: plan for about 10 times the file size, more for image heavy files, or lower
  `MaxDegreeOfParallelism`.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Fidelity and verification](docs/FIDELITY.md)
- [Roadmap](docs/ROADMAP.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## License

MIT. See [LICENSE](LICENSE). Runtime dependency licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
