#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: recipe-filled-form application-filled.pdf 0.3
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("application.pdf");
((PdfTextField)doc.Form["name"]!).Text = "Jamal Abdelkhalek";
((PdfCheckBoxField)doc.Form["subscribe"]!).Checked = true;
((PdfRadioButtonField)doc.Form["plan"]!).Selected = "pro";
((PdfChoiceField)doc.Form["country"]!).Select("Morocco");
doc.Form.Flatten();   // values become page content, the form is removed
doc.Save("application-filled.pdf");
