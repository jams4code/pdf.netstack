#:project ../../src/PdfNetStack.Ocr.Tesseract/PdfNetStack.Ocr.Tesseract.csproj
// since: 1.2
// run: build-only (needs Tesseract installed)
using PdfNetStack;
using PdfNetStack.Ocr;
using PdfNetStack.Ocr.Tesseract;

#region tesseract
string[] languages = ["fra+ara"];   // one request for a page in two scripts
using var engine = TesseractOcrEngine.Load(new TesseractOcrEngineOptions
{
    Languages = languages,
    // ExecutablePath and TessdataPath, when they are not where Tesseract finds them by itself.
});
var ocr = new PdfOcr(engine, new PdfOcrOptions { Languages = languages });

PdfDocument doc = PdfDocument.Open("scan.pdf");
PdfOcrReport report = ocr.MakeSearchable(doc);
doc.Save("scan-searchable.pdf");
#endregion

Console.WriteLine(report.Text);
