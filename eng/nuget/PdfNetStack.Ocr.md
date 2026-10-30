# PdfNetStack.Ocr

Make scanned PDFs searchable in .NET. PdfNetStack.Ocr finds the scan on each page, reads it with
the PP-OCRv5 models on ONNX Runtime, and lays the words over it as invisible text: the page looks
the same, and now selects, copies and searches. Latin and Arabic script. Built on
[PdfNetStack](https://www.nuget.org/packages/PdfNetStack).

```bash
dotnet add package PdfNetStack.Ocr
dotnet add package PdfNetStack.Ocr.Models    # the models, about 21 MB
```

```csharp
using PdfNetStack;
using PdfNetStack.Ocr;

using var engine = OnnxOcrEngine.Load();     // the models from PdfNetStack.Ocr.Models
PdfDocument doc = PdfDocument.Open("scan.pdf");
PdfOcrReport report = new PdfOcr(engine).MakeSearchable(doc);
doc.Save("scan-searchable.pdf");
Console.WriteLine(report.Text);
```

Runs on .NET 8 and later, and on .NET Framework 4.6.2+ through .NET Standard 2.0 (64 bit only;
see Limits).

## Usage

```csharp
// Latin and Arabic: each line keeps the reading its recognizer is most confident in.
var languages = new[] { "latin", "arabic" };
using var engine = OnnxOcrEngine.Load(new OnnxOcrEngineOptions { Languages = languages });
var ocr = new PdfOcr(engine, new PdfOcrOptions
{
    Languages = languages,
    MinimumConfidence = 0.5,      // lines read with less confidence are left out
    MaxDegreeOfParallelism = 4,   // pages recognized at once
    MaxOcrDpi = 400,              // finer scans are averaged down first
});

// Recognize reads without changing the document: lines and words with their boxes in page space.
PdfOcrReport report = ocr.Recognize(doc);
foreach (PdfOcrPage page in report.Pages)
{
    Console.WriteLine($"page {page.Index + 1}: {page.Status} {page.Reason}");
    foreach (OcrLine line in page.Lines) Console.WriteLine($"  {line.Text} ({line.Confidence:P0})");
}

// Models kept in a folder of your own, without PdfNetStack.Ocr.Models.
using var custom = OnnxOcrEngine.Load("path/to/models");
```

## What it does

- **Finds the scan.** The image that covers the page, or several images that cover it together:
  strips or tiles side by side, or a text mask over a background, as some scanners write them.
  Pieces are put back together in page space, one at a time, so memory stays at one page plus one
  piece.
- **Turns it upright** from its placement and the page's `/Rotate`; a line orientation model turns
  upside down lines.
- **Reads Latin and Arabic script** (Arabic also covers Persian and Urdu), right to left text in
  logical order, with word boxes mapped back to the page.
- **Writes invisible text** (render mode 3) over each word, stretched to its box, with a glyphless
  font that covers every script. Pages that already show text are left alone.
- **Plugs in other engines** through `IOcrEngine`.

## Safety

Detection, decoding and right to left ordering are managed code: the native runtime only ever
sees float tensors, never PDF bytes. Pixel counts are checked before anything is decoded. Model
files are checked against pinned SHA-256 hashes before they load, and nothing is downloaded at
run time. ONNX Runtime's telemetry is turned off before the first model loads.

## Limits

- Pages need a decodable scan covering at least half the page (`MinimumCoverage`); vector art is
  not rasterized. Scans in several pieces must be upright or turned by quarter turns.
- ONNX Runtime ships native builds for Windows, Linux and macOS on x64 and arm64, except macOS
  x64. On Linux and macOS set `ORT_DISABLE_TELEMETRY=1` before the process starts for the runtime
  to send nothing at all.
- On .NET Framework the app must run 64 bit and reference `Microsoft.ML.OnnxRuntime` itself, so
  its native runtime is copied next to the app.
- Recognition uses floating point on the CPU, so results can differ slightly between machines.

## Links

- Project and documentation: https://github.com/jams4code/pdf.netstack
- OCR design notes: https://github.com/jams4code/pdf.netstack/blob/main/docs/OCR.md
- Changelog: https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md

By Jamal Abdelkhalek, [JADEV](https://jadev-corp.com). MIT license. Depends on ONNX Runtime (MIT).
