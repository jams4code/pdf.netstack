#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// preview: invoice-html invoice.pdf 0.3
using PdfNetStack.Html;

#region short
byte[] pdf = HtmlConverter.ConvertToPdf("<h1>Invoice 42</h1><p>Total: <b>1,210.00 EUR</b></p>");
File.WriteAllBytes("invoice.pdf", pdf);
#endregion
