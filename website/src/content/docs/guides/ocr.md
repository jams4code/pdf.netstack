---
title: Make scanned PDFs searchable with OCR in C#
description: Add an invisible text layer to scanned PDFs in .NET with the PdfNetStack.Ocr add-on, Latin and Arabic, PDF bytes never reach native code. MIT.
since: '1.1'
sidebar:
  label: OCR
faq:
  - q: Is PdfNetStack.Ocr on NuGet?
    a: Not yet. It is built from source with a project reference until the OCR models have a package of their own.
  - q: Which languages does the OCR add-on read?
    a: Latin script and Arabic script. The Arabic model also reads Persian and Urdu.
  - q: Can I run OCR from the command line?
    a: Yes, with pdfnetstack-ocr from the PdfNetStack.Ocr.Tool package, which carries the models and ONNX Runtime. With PdfNetStack.Tool also installed, pdfnetstack ocr runs it.
  - q: What about pages scanned sideways or upside down?
    a: They are found from the way their lines run and read turned back, with nothing needed in the PDF. PdfOcrPage.Orientation says how far a page stood turned; DetectOrientation turns this off.
  - q: Does the OCR add-on send data anywhere?
    a: The engine turns ONNX Runtime's telemetry off before the first model loads. On Linux and macOS, ORT_DISABLE_TELEMETRY=1 must also be set before the process starts for the runtime to send nothing at all.
  - q: Can I use Tesseract for OCR with PdfNetStack?
    a: Yes, with TesseractOcrEngine from the PdfNetStack.Ocr.Tesseract package, which runs a tesseract executable you installed, for any language it has data for. Tesseract and its language data are not in the package.
---

Turn scanned pages into pages that select, copy and search, while they look exactly the same.

## Short answer

Load the recognizer with `OnnxOcrEngine.Load`, then call `MakeSearchable` on a document. For each
page that is a scan, PdfNetStack finds the image, reads it, and lays every word over it as
invisible text. Save the document as usual.

```csharp sample="ocr-searchable.cs" region="searchable"
```

## Install from source

The add-on needs the PdfNetStack source and four model files.

1. Clone the repository: `git clone https://github.com/jams4code/pdf.netstack`.
2. Reference the project from your application:

   ```xml
   <ItemGroup>
     <ProjectReference Include="..\pdf.netstack\src\PdfNetStack.Ocr\PdfNetStack.Ocr.csproj" />
   </ItemGroup>
   ```

   In a .NET 10 file-based app beside the clone, the line is
   `#:project pdf.netstack/src/PdfNetStack.Ocr/PdfNetStack.Ocr.csproj`.
3. Fetch the models from the clone: `python tools/fetch_ocr_models.py <folder>`. The script
   downloads them, checks each against a pinned SHA-256, and puts them in the folder you then pass
   to `OnnxOcrEngine.Load`.

The project references `Microsoft.ML.OnnxRuntime`, which brings the native runtime. Everything
else is managed code. See [Getting started](/getting-started/#build-from-source) for building
against the main branch.

## The models

| File | Role |
|---|---|
| `ch_PP-OCRv5_det_mobile.onnx` | Finds the text lines |
| `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | Turns upside down lines the right way up |
| `latin_PP-OCRv5_rec_mobile.onnx` | Reads Latin script |
| `arabic_PP-OCRv5_rec_mobile.onnx` | Reads Arabic script, Persian and Urdu included |

These are the PP-OCRv5 mobile models of PaddleOCR (Apache-2.0), in the ONNX form published by the
RapidOCR project (Apache-2.0). `OnnxOcrEngine` checks every file against its pinned SHA-256 before
loading it (`VerifyModels`, on by default), and `OnnxOcrEngine.ModelHashes` lists the pins for
your own audits.

## What happens to each page

1. The page is skipped when it already shows text, so OCR never runs twice over a page.
2. The scan is the image that covers at least half of the page. It is decoded, scaled down to
   at most 400 DPI by default, and turned upright from its placement and the page's `/Rotate`.
   A scan stored as several images, such as strips or tiles side by side or a text mask over a
   background, is put back together first: the pieces are painted one at a time into one gray
   image of the page, and the words found map back through it. A page with no scan to read can
   be rendered by your application instead (see [the rasterizer hook](#pages-without-a-scan)).
3. The detector finds the lines, the orientation model turns upside down lines, and the
   recognizer reads each line. Box detection, text decoding and right to left ordering are
   managed code: the native runtime only ever sees float tensors, never PDF bytes.
4. When most of the lines do not run left to right, the page was scanned sideways or upside
   down. It is read again turned the right way (see [pages scanned sideways](#pages-scanned-sideways)).
5. Every word is drawn over the scan as invisible text, stretched to its box, with
   `DrawInvisibleText`. The page looks the same and now selects, copies and searches. The text
   layer takes any script with a tiny built in font, and it passes PDF/A-2u.

With several languages, each line keeps the reading its recognizer is most confident in. Lines
under `MinimumConfidence` are left out.

## The report

`MakeSearchable` and `Recognize` return a `PdfOcrReport` with one `PdfOcrPage` per page: its
status, the reason a page was skipped, how far the page stood turned (`Orientation`), and the
lines found, each with its words, a confidence and a box.

```csharp sample="ocr-searchable.cs" region="report"
```

| `PdfOcrPageStatus` | Meaning |
|---|---|
| `Done` | The scan was read; the text is in `Lines` |
| `SkippedHasText` | The page already shows text |
| `SkippedNoScan` | No image covers enough of the page to be its scan |
| `SkippedUnsupported` | The scan's encoding, size or placement is not supported; see `Reason` |
| `Failed` | Reading failed; see `Reason` |

Boxes are in PDF user space: points, y up, before `/Rotate`, as for
[text extraction](/guides/extract-text/).

## Pages scanned sideways

A page fed into a scanner sideways or upside down gives a scan whose text is turned, and nothing
in the PDF says so. The detector still finds its lines, running down, up or backwards, so the
add-on notices when most of the text on a page does not run left to right. It then reads a copy
of the page reduced to 1600 pixels on its long side in each of the four orientations, keeps the
one with the most text read left to right with confidence, and reads the page once more turned
that way. The words map back to where they are on the page, running the way the text runs, and
the lines come out in reading order. Upright pages cost nothing more.

`PdfOcrPage.Orientation` is how far the text stood turned clockwise: 0, 90, 180 or 270.
`DetectOrientation = false` reads every page as it is.

## Pages without a scan

Some pages have no image to read: text converted to vector outlines, vector art with images too
small to be the page's scan, or a scan in an encoding PdfNetStack does not decode. PdfNetStack does not render pages, but
your application can: give `PdfOcrOptions.Rasterizer` an
[`IPdfPageRasterizer`](/api/pdfnetstack-ocr/ipdfpagerasterizer/), for example one built on a
PDFium binding.

- It is asked only for pages that would otherwise be skipped as `SkippedNoScan` or
  `SkippedUnsupported`, one page at a time, before the page gets its text layer. It receives the
  page and its index, so it can render from the original file.
- It renders the page as it is shown, the crop box turned by `/Rotate`, on white, in 8 bit gray,
  at the resolution it is given: `RasterizerDpi` (300), at most `MaxOcrDpi`, lowered beforehand
  so the page fits `MaxImagePixels`.
- Returning null leaves the page skipped. A bitmap that is too large or not the page's shape is
  reported as `SkippedUnsupported`, and an exception from the renderer as `Failed`.

The rendered page then goes through the same steps as a scan, orientation included.

## Command line

`pdfnetstack-ocr` does the same from a terminal or a script. It comes in its own package,
`PdfNetStack.Ocr.Tool`, with the models and ONNX Runtime included, so the main
[`pdfnetstack`](/cli/) tool stays small for everyone who never runs OCR.

```bash
dotnet tool install -g PdfNetStack.Ocr.Tool
pdfnetstack-ocr scan.pdf searchable.pdf --languages latin,arabic --text scan.txt
pdfnetstack ocr scan.pdf searchable.pdf      # the same, through pdfnetstack
```

See [the ocr command](/cli/#ocr) for every option.

## Read without changing the document

`Recognize` reads every scanned page and leaves the document as it is, for indexing or
classification.

```csharp sample="ocr-searchable.cs" region="recognize"
```

## Options

| `PdfOcrOptions` | Default | Notes |
|---|---|---|
| `Languages` | latin | `latin`, `arabic`, or both; the engine must have each loaded |
| `SkipPagesWithText` | true | Leave pages that already show text alone |
| `MinimumConfidence` | 0.5 | Lines read with less confidence are left out |
| `MaxDegreeOfParallelism` | CPU count | Pages recognized at once; each holds its decoded scan in memory |
| `MaxImagePixels` | 2^27 | Larger scans (about 11,500 by 11,500) are skipped rather than decoded |
| `MaxOcrDpi` | 400 | Finer scans are averaged down to this resolution |
| `MinimumCoverage` | 0.5 | Share of the page an image must cover to be its scan |
| `DetectOrientation` | true | Find pages scanned sideways or upside down and read them turned back |
| `Rasterizer` | null | Renders pages with no scan to read; supplied by your application |
| `RasterizerDpi` | 300 | Resolution pages are rendered at, at most `MaxOcrDpi` |

| `OnnxOcrEngineOptions` | Default | Notes |
|---|---|---|
| `Languages` | latin | Recognizers to load; each needs its model file |
| `MaxDetectionSide` | 1920 | Longest side the detector sees |
| `BoxThreshold` | 0.6 | Mean text probability a detected box needs |
| `CorrectUpsideDownLines` | true | Needs the orientation model |
| `ThreadsPerModel` | 0 | 0 lets ONNX Runtime decide |
| `VerifyModels` | true | Check each model file against its pinned SHA-256 |

## Choosing an engine

`OnnxOcrEngine` is the default. `TesseractOcrEngine`, in the `PdfNetStack.Ocr.Tesseract` package, runs
a `tesseract` executable you have installed.

| | `OnnxOcrEngine` | `TesseractOcrEngine` |
|---|---|---|
| Languages | Latin and Arabic script | Any Tesseract has data for |
| What you install | Nothing beyond packages | Tesseract 4 or later and its language data |
| Platforms | win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64 | Wherever you can install Tesseract, Alpine included. Homebrew has no prebuilt bottle for Intel Macs |
| Size of a published app | 241 MB without a runtime identifier, with every platform's native libraries (17 to 45 MB for one), plus 21 MB of models | 3 MB, plus your Tesseract install |
| Where recognition runs | In your process, on float tensors built by managed code | In a separate process for each image; a crash or hang costs that image |
| Each image costs | Inference; models stay loaded | A process start and loading the language data, then inference |
| Pages scanned sideways or upside down | Found and read turned back | Not detected |
| Several languages | Each line keeps its most confident reading | One request, such as `eng+ara` |
| Native AOT | Yes, on Linux, checked in CI | Yes, on Linux, checked in CI; Tesseract itself is a separate program |

Sizes are `dotnet publish` of a net10.0 console app. Speed and accuracy depend on your scans and
we publish no comparison: try both on a sample of your own.

## Tesseract

```csharp sample="ocr-tesseract.cs" region="tesseract"
```

Install Tesseract and the languages you need. `apt install tesseract-ocr tesseract-ocr-ara` on Debian
and Ubuntu, `brew install tesseract tesseract-lang` on macOS, and on Windows one of the
[installers Tesseract lists](https://tesseract-ocr.github.io/tessdoc/Installation.html). Language
data is also at [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (smaller and
faster) and [tessdata_best](https://github.com/tesseract-ocr/tessdata_best); put the
`.traineddata` files in a folder and name it with `TessdataPath`. PdfNetStack ships neither.

`Load` checks all of it up front. A missing executable or missing language data throws
`FileNotFoundException`, saying where to get it, and a Tesseract older than version 4 throws
`TesseractOcrException`.

Each image is its own process. The pixels go in on standard input and the results come back on
standard output, with no shell and no temporary files, so calls from several threads share
nothing. `TesseractOcrEngine` looks for the executable in the absolute folders of the `PATH`, never
in the current folder. Language names are checked before they reach a command line, so one taken
from a request cannot become an option or a path. Cancelling kills the process, as do `Timeout`
(`TimeoutException`) and `MaxOutputBytes` (`TesseractOcrException`); with `PdfOcr`, a failed image is
a page with status `Failed`.

Names in `LanguageMap` let `latin` and `arabic` keep working, and any other name goes to Tesseract
as it is: `fra`, `chi_sim`, `script/Latin`. Read a page in two scripts with one request, such as
`eng+ara`: `PdfOcr` merges lines from separate requests only when their boxes match to the pixel,
which two Tesseract layouts need not do.

| `TesseractOcrEngineOptions` | Default | Notes |
|---|---|---|
| `ExecutablePath` | null | Null looks on the `PATH` |
| `TessdataPath` | null | Null leaves it to Tesseract: `TESSDATA_PREFIX`, else where it was installed |
| `Languages` | latin | Checked when loading |
| `LanguageMap` | latin: eng, arabic: ara | Other names go to Tesseract as they are |
| `PageSegmentationMode` | 3 | Tesseract's `--psm`; 0 and 2 give no text and are refused, and 1 and 12 need `osd.traineddata` |
| `Dpi` | null | Null lets Tesseract estimate it, as scans come at any resolution |
| `ThreadsPerProcess` | 1 | Through `OMP_THREAD_LIMIT`, as `PdfOcr` already reads pages in parallel; 0 leaves it to Tesseract |
| `MaxConcurrentProcesses` | CPU count | Further calls wait their turn |
| `Timeout` | 5 minutes | Per image |
| `MaxOutputBytes` | 64 MiB | Per image |

An application that uses only this engine leaves ONNX Runtime's native libraries out, without
doing anything. Referencing `PdfNetStack.Ocr` directly as well brings them back.

## Another OCR engine

`PdfOcr` takes any `IOcrEngine`. An engine receives an upright 8 bit gray `OcrBitmap` and an
`OcrRequest` with the script to read, and returns an `OcrImageResult` with lines and words boxed
in the image's pixels. PdfNetStack does the rest: finding and turning the scan, mapping boxes to
the page and writing the text layer. Implementations must be safe to call from several threads.
`PdfOcr` does not dispose the engine; the caller owns it.

## Platforms

ONNX Runtime's official builds send usage telemetry to Microsoft by default. `OnnxOcrEngine`
turns it off before the first model loads. On Linux and macOS, also set
`ORT_DISABLE_TELEMETRY=1` in the environment before the process starts, so the runtime sends
nothing at all.

On .NET Framework, the application must run as 64 bit and reference `Microsoft.ML.OnnxRuntime`
itself, so its native runtime is copied next to the application.

## Limits

- A page is read when its scan covers at least half of the page: one image, upright or turned by
  quarter turns, or several images that together cover it, each upright or turned by quarter
  turns.
- The scan must decode to pixels: raw samples, JPEG, JPEG 2000, CCITT fax or JBIG2, in a color
  space [image extraction](/guides/extract-images/) supports.
- Vector art and text drawn as outlines are read only through a rasterizer your application
  supplies; PdfNetStack does not render.
- Orientation is decided by the recognizer's own confidence: a page with very little text, or
  text in a script that is not loaded, may be read as it is.
- `OnnxOcrEngine` reads Latin and Arabic script only; `TesseractOcrEngine` does not detect turned
  pages.
- Not on NuGet yet. `PdfNetStack.Ocr`, its models package `PdfNetStack.Ocr.Models`, the
  `PdfNetStack.Ocr.Tool` command line tool and `PdfNetStack.Ocr.Tesseract` go up with a coming release.

## Related

- [Extract text](/guides/extract-text/): read the text back
- [Extract images](/guides/extract-images/): the decoders OCR relies on
- [PDF/A](/guides/pdf-a/): archive the searchable scan
- [Command line](/cli/#ocr): `pdfnetstack ocr`
- API: [PdfOcr](/api/pdfnetstack-ocr/pdfocr/), [OnnxOcrEngine](/api/pdfnetstack-ocr/onnxocrengine/), [TesseractOcrEngine](/api/pdfnetstack-ocr-tesseract/tesseractocrengine/), [PdfOcrOptions](/api/pdfnetstack-ocr/pdfocroptions/), [IOcrEngine](/api/pdfnetstack-ocr/iocrengine/), [IPdfPageRasterizer](/api/pdfnetstack-ocr/ipdfpagerasterizer/)
