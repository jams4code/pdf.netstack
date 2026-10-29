#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Objects;

#region import
// Fields come along with their pages. A top level name the target form already uses gets the
// first free suffix: the second copy's fields become name_2, plan_2 and so on.
PdfDocument twice = PdfDocument.Merge([PdfDocument.Open("application.pdf"), PdfDocument.Open("application.pdf")]);
Console.WriteLine(string.Join(", ", twice.Form.Fields.Select(f => f.Name)));
twice.Save("applications.pdf");
#endregion

// A field with a format action, as form designers write them: two decimals, comma separators,
// negative numbers in red, and "EUR " in front.
PdfDocument doc = PdfDocument.Open("application.pdf");
PdfTextField field = doc.Form.AddTextField(doc.Pages[0], "amount", 160, 240, 150, 20);
var format = new PdfDictionary();
format.Set("S", new PdfName("JavaScript"));
format.Set("JS", PdfString.FromText("AFNumber_Format(2, 0, 1, 0, \"EUR \", true);"));
var actions = new PdfDictionary();
actions.Set("F", format);
field.Dictionary.Set("AA", actions);

#region format
// The value is stored as typed; the appearance and FormattedText show it formatted.
field.Text = "-1234.5";
Console.WriteLine($"{field.Text} shows as {field.FormattedText}");
doc.Save("application-amount.pdf");
#endregion
