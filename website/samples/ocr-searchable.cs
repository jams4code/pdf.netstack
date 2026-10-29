#:project ../../src/PdfNetStack.Ocr/PdfNetStack.Ocr.csproj
// since: unreleased
// run: build-only (needs the OCR model files from tools/fetch_ocr_models.py)
using PdfNetStack;
using PdfNetStack.Ocr;

#region searchable
// Models: python tools/fetch_ocr_models.py models
using var engine = OnnxOcrEngine.Load("models", new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
var ocr = new PdfOcr(engine, new PdfOcrOptions { Languages = ["latin", "arabic"] });

PdfDocument doc = PdfDocument.Open("scan.pdf");
PdfOcrReport report = ocr.MakeSearchable(doc);
doc.Save("scan-searchable.pdf");   // looks the same, now selects, copies and searches
#endregion

#region report
foreach (PdfOcrPage page in report.Pages)
{
    Console.WriteLine($"Page {page.Index + 1}: {page.Status} {page.Reason}");
    foreach (OcrLine line in page.Lines)
    {
        string direction = line.RightToLeft ? ", right to left" : "";
        Console.WriteLine($"  {line.Text} ({line.Confidence:P0}{direction})");
    }
}
#endregion

#region recognize
// Read without changing the document, for indexing or classification.
PdfOcrReport found = ocr.Recognize(PdfDocument.Open("scan.pdf"));
Console.WriteLine(found.Text);
#endregion
