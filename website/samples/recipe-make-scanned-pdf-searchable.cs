#:project ../../src/PdfNetStack.Ocr/PdfNetStack.Ocr.csproj
// since: 1.1
// run: build-only needs the OCR model files from tools/fetch_ocr_models.py
using PdfNetStack;
using PdfNetStack.Ocr;

// Models: python tools/fetch_ocr_models.py models (checked against pinned SHA-256 hashes).
using var engine = OnnxOcrEngine.Load("models", new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });

PdfDocument doc = PdfDocument.Open("scan.pdf");
var ocr = new PdfOcr(engine, new PdfOcrOptions { Languages = ["latin", "arabic"] });
PdfOcrReport report = ocr.MakeSearchable(doc);   // invisible text laid over each scan
doc.Save("scan-searchable.pdf");
Console.WriteLine(report.Text);
