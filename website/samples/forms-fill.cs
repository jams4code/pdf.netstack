#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

#region read
PdfDocument doc = PdfDocument.Open("application.pdf");
foreach (PdfField field in doc.Form.Fields)
{
    Console.WriteLine($"{field.Name} ({field.Kind}) = {field.Value ?? "empty"}");
}
#endregion

#region fill
((PdfTextField)doc.Form["name"]!).Text = "Jamal Abdelkhalek";
((PdfCheckBoxField)doc.Form["subscribe"]!).Checked = true;
((PdfRadioButtonField)doc.Form["plan"]!).Selected = "pro";
((PdfChoiceField)doc.Form["country"]!).Select("Morocco");
doc.Save("application-filled.pdf");          // still a form: values can be changed in a viewer
#endregion

#region flatten
doc.Form.Flatten();                            // values become page content, the form is removed
doc.Save("application-flat.pdf");
#endregion

Console.WriteLine($"After flattening: {doc.Form.Fields.Count} fields");
