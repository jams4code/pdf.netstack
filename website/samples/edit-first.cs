#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region short
PdfDocument doc = PdfDocument.Open("contract.pdf");
doc.Pages.RemoveAt(0);                                       // drop the cover
doc.Pages.Import(PdfDocument.Open("annex.pdf").Pages);      // append another document
doc.Info.Title = "Contract with annex";
doc.Save("contract-final.pdf");                              // optimized and verified by default
#endregion
