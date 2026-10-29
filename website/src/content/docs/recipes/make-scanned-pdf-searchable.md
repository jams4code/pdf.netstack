---
title: Make a scanned PDF searchable in C#
description: Add an invisible OCR text layer to scanned PDFs in .NET with the PdfNetStack.Ocr add-on, Latin and Arabic, models checked by hash. MIT.
since: 'unreleased'
sidebar:
  label: Make a scan searchable
---

Read the text of scanned pages and lay it over the scans, so the PDF selects, copies and searches.

## Short answer

Load the OCR models with `OnnxOcrEngine.Load`, then call `PdfOcr.MakeSearchable` on the document and
save it. Each page's scan is found, turned upright, read, and every word is laid over it as
invisible text. The pages look the same as before.

```csharp sample="recipe-make-scanned-pdf-searchable.cs"
```

## Notes

- `PdfNetStack.Ocr` is an add-on that is not on NuGet yet: reference `src/PdfNetStack.Ocr` from a
  clone of the repository. It runs the PP-OCRv5 mobile models on ONNX Runtime.
- Get the models with `python tools/fetch_ocr_models.py models`. They are checked against pinned
  SHA-256 hashes when loaded.
- A page qualifies when its scan is one image covering at least half the page. Pages that already
  have text are left alone. `Recognize` reports the text without changing the document.
- ONNX Runtime's official builds send telemetry by default. The engine turns it off before loading
  a model; on Linux and macOS also set `ORT_DISABLE_TELEMETRY=1` before the process starts.

## Related

- [OCR](/guides/ocr/)
- [Extract text](/guides/extract-text/)
- API: [PdfOcr](/api/pdfnetstack-ocr/pdfocr/), [OnnxOcrEngine](/api/pdfnetstack-ocr/onnxocrengine/)
