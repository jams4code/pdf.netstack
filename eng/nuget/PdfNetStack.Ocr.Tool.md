# PdfNetStack.Ocr.Tool

`pdfnetstack-ocr`: make scanned PDF files searchable from the command line. It finds the scan on
each page, reads it with the PP-OCRv5 models on ONNX Runtime, and lays the words over it as
invisible text: the page looks the same, and now selects, copies and searches. Latin and Arabic
script. Built on [PdfNetStack.Ocr](https://www.nuget.org/packages/PdfNetStack.Ocr) by Jamal
Abdelkhalek at [JADEV](https://github.com/jams4code). MIT licensed.

```bash
dotnet tool install -g PdfNetStack.Ocr.Tool
```

The models and ONNX Runtime's native libraries come with the tool. It is a package of its own so
that [PdfNetStack.Tool](https://www.nuget.org/packages/PdfNetStack.Tool) stays small for
everyone who never runs OCR; with both installed, `pdfnetstack ocr` runs this tool.

## Usage

```bash
pdfnetstack-ocr scan.pdf searchable.pdf                              # Latin script
pdfnetstack-ocr scan.pdf searchable.pdf --languages latin,arabic     # each line in its best reading
pdfnetstack-ocr scan.pdf searchable.pdf --text scan.txt              # also write the text found
pdfnetstack-ocr scan.pdf searchable.pdf --json                       # per page report
pdfnetstack-ocr scan.pdf searchable.pdf --models ./my-models         # models from a folder of your own
pdfnetstack ocr scan.pdf searchable.pdf                              # the same, through pdfnetstack
```

Pages that already show text are left as they are. Pages scanned sideways or upside down are
found and read turned back (`--no-orientation` turns that off). Run `pdfnetstack-ocr --help` for
every option. Exit codes: 0 done, 1 written but some pages failed, 2 nothing written.

Requires the .NET 8 or later runtime, on Windows, Linux or macOS, x64 or arm64 (macOS x64 is not
built by ONNX Runtime).

## Licenses

The tool is MIT. The PP-OCRv5 models are PaddleOCR's, converted to ONNX by RapidOCR, both
Apache-2.0 (`models/LICENSE.txt` and `models/NOTICE.txt` in the package). ONNX Runtime is MIT
(`onnxruntime/LICENSE` and `onnxruntime/ThirdPartyNotices.txt`). ONNX Runtime's telemetry is
turned off before the first model loads; on Linux and macOS also set `ORT_DISABLE_TELEMETRY=1`
in the environment for the runtime to send nothing at all.

## Links

- [Documentation and source](https://github.com/jams4code/pdf.netstack)
- [OCR guide](https://github.com/jams4code/pdf.netstack/blob/main/docs/OCR.md)
- [Changelog](https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md)
- [Report an issue](https://github.com/jams4code/pdf.netstack/issues)
