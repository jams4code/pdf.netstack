# PdfNetStack.Ocr.Models

The PP-OCRv5 mobile models that [PdfNetStack.Ocr](https://www.nuget.org/packages/PdfNetStack.Ocr)
runs to make scanned PDFs searchable: text detection, text line orientation, and Latin and Arabic
recognition, in ONNX format. About 21 MB.

```bash
dotnet add package PdfNetStack.Ocr
dotnet add package PdfNetStack.Ocr.Models
```

```csharp
using PdfNetStack;
using PdfNetStack.Ocr;

using var engine = OnnxOcrEngine.Load(new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
PdfDocument doc = PdfDocument.Open("scan.pdf");
new PdfOcr(engine, new PdfOcrOptions { Languages = ["latin", "arabic"] }).MakeSearchable(doc);
doc.Save("scan-searchable.pdf");
```

## How it works

The package holds no code. On build and publish it copies the four model files into a
`pdfnetstack-ocr-models` folder next to the application, and `OnnxOcrEngine.Load()` reads them from
there. The files stay outside a single file bundle. Each one is checked against its pinned SHA-256
before ONNX Runtime loads it, so a damaged or swapped file fails to load instead of giving wrong
text.

To keep the models elsewhere, leave this package out and pass a folder:
`OnnxOcrEngine.Load("path/to/models")`. `OnnxOcrEngine.ModelHashes` lists the files and their
hashes.

| File | Model | Size |
|---|---|---|
| `ch_PP-OCRv5_det_mobile.onnx` | Text detection (DB) | 4.8 MB |
| `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | Text line orientation | 1.0 MB |
| `latin_PP-OCRv5_rec_mobile.onnx` | Latin script recognition | 7.9 MB |
| `arabic_PP-OCRv5_rec_mobile.onnx` | Arabic script recognition (also Persian and Urdu) | 8.0 MB |

## License

The models are PaddleOCR's (https://github.com/PaddlePaddle/PaddleOCR, Copyright (c) 2016
PaddlePaddle Authors), as converted to ONNX by RapidOCR (https://github.com/RapidAI/RapidOCR,
Copyright (c) 2021 RapidAI), both under the Apache License 2.0. `LICENSE.txt` and `NOTICE.txt` in
the package carry the license and notices; keep them with the files if you redistribute them. The
small build file that copies the models is part of PdfNetStack, MIT License.

## Links

- Project and documentation: https://github.com/jams4code/pdf.netstack
- OCR design notes: https://github.com/jams4code/pdf.netstack/blob/main/docs/OCR.md
- Changelog: https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md

By Jamal Abdelkhalek, [JADEV](https://jadev-corp.com). Models Apache-2.0.
