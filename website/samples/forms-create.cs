#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: forms form.pdf 0.5
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    PdfFont label = PdfStandardFont.Helvetica;
    g.DrawString("Registration", PdfStandardFont.HelveticaBold, 22, PdfColor.Black, 72, 70);
    g.DrawString("Name", label, 10, PdfColor.Black, 72, 115);
    g.DrawString("Subscribe to updates", label, 10, PdfColor.Black, 92, 146);
    g.DrawString("Plan", label, 10, PdfColor.Black, 72, 176);
    g.DrawString("Free", label, 10, PdfColor.Black, 182, 176);
    g.DrawString("Pro", label, 10, PdfColor.Black, 242, 176);
    g.DrawString("Country", label, 10, PdfColor.Black, 72, 206);
    g.DrawString("Interests", label, 10, PdfColor.Black, 72, 236);
    g.DrawString("Postal code", label, 10, PdfColor.Black, 72, 316);
    g.DrawString("Signature", label, 10, PdfColor.Black, 72, 356);
}

#region create
PdfAcroForm form = doc.Form;
// Positions are points from the page's top left, as for drawing: x, y, width, height.
form.AddTextField(page, "name", 160, 100, 220, 20, options: new PdfFieldOptions { Required = true, ToolTip = "Full name" });
form.AddCheckBox(page, "subscribe", 72, 134, 14, 14, isChecked: true);
form.AddRadioGroup("plan", [(page, 160, 164, 14, 14, "free"), (page, 220, 164, 14, 14, "pro")], selected: "free");
form.AddComboBox(page, "country", 160, 190, 150, 20, ["Belgium", "France", "Morocco"], selected: "France");
form.AddListBox(page, "interests", 160, 222, 150, 60, ["Compression", "Forms", "Signatures"],
    options: new PdfFieldOptions { MultiSelect = true });
form.AddTextField(page, "postcode", 160, 300, 100, 22, options: new PdfFieldOptions { MaxLength = 5, Comb = true });
form.AddSignatureField(page, "signature", 160, 340, 220, 50);
form.AddPushButton(page, "help", 400, 100, 80, 22, "Help", new Uri("https://example.com/help"));
doc.Save("form.pdf");
#endregion
