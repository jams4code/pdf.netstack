#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;

PdfDocument doc = PdfDocument.Open("contract.pdf");
PdfPage last = doc.Pages[^1];
doc.Form.AddSignatureField(last, "signature", 72, 730, 220, 60, new PdfFieldOptions
{
    BorderColor = PdfColor.Gray(0.5),
    ToolTip = "Sign here",
});
doc.Save("contract-to-sign.pdf");
Console.WriteLine(string.Join(", ", PdfDocument.Open("contract-to-sign.pdf").Form.Fields.Select(f => $"{f.Name} ({f.Kind})")));
