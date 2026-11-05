# PdfNetStack.Ocr.Tesseract

Make scanned PDFs searchable with a Tesseract you have installed. An `IOcrEngine` for
[PdfNetStack.Ocr](https://www.nuget.org/packages/PdfNetStack.Ocr) that runs the `tesseract`
executable, one process per image. Any language Tesseract has data for.

```bash
dotnet add package PdfNetStack.Ocr.Tesseract
```

Tesseract 4 or later and its language data are installed separately; neither ships in this package:

```bash
apt install tesseract-ocr tesseract-ocr-ara    # Debian, Ubuntu
brew install tesseract tesseract-lang          # macOS
```

On Windows use one of the installers listed at
https://tesseract-ocr.github.io/tessdoc/Installation.html. Language data files are also at
https://github.com/tesseract-ocr/tessdata_fast.

```csharp
using PdfNetStack;
using PdfNetStack.Ocr;
using PdfNetStack.Ocr.Tesseract;

string[] languages = ["fra+ara"];    // one request for a page in two scripts
using var engine = TesseractOcrEngine.Load(new TesseractOcrEngineOptions { Languages = languages });
PdfDocument doc = PdfDocument.Open("scan.pdf");
PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = languages }).MakeSearchable(doc);
doc.Save("scan-searchable.pdf");
```

## Options

| `TesseractOcrEngineOptions` | Default | Notes |
|---|---|---|
| `ExecutablePath` | null | Null looks in the absolute folders of the `PATH`, never the current folder |
| `TessdataPath` | null | The folder of `.traineddata` files; null leaves it to Tesseract |
| `Languages` | latin | Checked when loading: a missing one fails there, naming where to get it |
| `LanguageMap` | latin: eng, arabic: ara | Other names go to Tesseract as they are |
| `PageSegmentationMode` | 3 | Tesseract's `--psm` |
| `Dpi` | null | Null lets Tesseract estimate it |
| `ThreadsPerProcess` | 1 | Through `OMP_THREAD_LIMIT`; 0 leaves it to Tesseract |
| `MaxConcurrentProcesses` | CPU count | Further calls wait their turn |
| `Timeout` | 5 minutes | Per image; the process is killed |
| `MaxOutputBytes` | 64 MiB | Per image; the process is killed |

## Safety

Each image is its own process, started without a shell, with no temporary files: the pixels go in
on standard input as a bitmap built by managed code and the results come back on standard output.
A crash, a hang or a flood of output in Tesseract costs that image, not your application.
Cancelling kills the process. Language names are checked before they reach a command line, the
output is parsed as untrusted text, and boxes are clamped to the image.

## Limits

- A page scanned sideways or upside down is not detected. TSV does not say which way text runs, so
  `PdfOcrOptions.DetectOrientation` has nothing to go on.
- Each image starts a process and loads the language data again.
- Read a page in two scripts with one request (`eng+ara`). `PdfOcrOptions.Languages` with two entries
  reads the page twice and merges lines only when their boxes match to the pixel, which two
  Tesseract layouts need not do.

## Links

- Project and documentation: https://github.com/jams4code/pdf.netstack
- Choosing an engine: https://github.com/jams4code/pdf.netstack/blob/main/docs/OCR.md
- Changelog: https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md

By Jamal Abdelkhalek, [JADEV](https://jadev-corp.com). MIT license. Tesseract is Apache-2.0 and is
not distributed with this package.
