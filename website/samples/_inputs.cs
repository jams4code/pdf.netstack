#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// Builds the input files the other samples read: synthetic documents, nothing third party.
// scripts/run-samples.mjs runs it first, in a folder that already holds photo.jpg and logo.png.
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfImage photo = PdfImage.Load("photo.jpg");
PdfImage logo = PdfImage.Load("logo.png");

// A plain multi page document, saved without the optimizer so there is something to gain.
var unoptimized = new PdfSaveOptions { Optimize = false, UseObjectStreams = false };
Write("manual.pdf", "Operating manual", 6, unoptimized);
Write("report.pdf", "Quarterly report", 3, PdfSaveOptions.Default);
Write("contract.pdf", "Service contract", 4, PdfSaveOptions.Default);
Write("annex.pdf", "Annex", 2, PdfSaveOptions.Default);
Write("a.pdf", "Part A", 2, PdfSaveOptions.Default);
Write("b.pdf", "Part B", 2, PdfSaveOptions.Default);
Write("c.pdf", "Part C", 1, PdfSaveOptions.Default);

// A scan: one page that is a single full page image, as scanners write them.
PdfDocument scan = PdfDocument.Create();
PdfPage scanPage = scan.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = scanPage.CreateGraphics())
{
    g.DrawImage(photo, 0, 0, scanPage.Width, scanPage.Height);
}
scan.Save("scan.pdf");

// A form to fill.
PdfDocument application = PdfDocument.Create();
PdfPage formPage = application.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = formPage.CreateGraphics())
{
    PdfFont font = PdfStandardFont.Helvetica;
    g.DrawString("Application", PdfStandardFont.HelveticaBold, 20, PdfColor.Black, 72, 80);
    g.DrawString("Name", font, 10, PdfColor.Black, 72, 115);
    g.DrawString("Subscribe to updates", font, 10, PdfColor.Black, 92, 145);
    g.DrawString("Plan: free / pro", font, 10, PdfColor.Black, 72, 175);
    g.DrawString("Country", font, 10, PdfColor.Black, 72, 205);
}
PdfAcroForm form = application.Form;
form.AddTextField(formPage, "name", 160, 100, 200, 20);
form.AddCheckBox(formPage, "subscribe", 72, 133, 14, 14);
form.AddRadioGroup("plan", [(formPage, 160, 163, 14, 14, "free"), (formPage, 200, 163, 14, 14, "pro")], "free");
form.AddComboBox(formPage, "country", 160, 190, 150, 20, ["Belgium", "France", "Morocco"]);
application.Save("application.pdf");

void Write(string path, string title, int pages, PdfSaveOptions options)
{
    PdfDocument doc = PdfDocument.Create();
    doc.Info.Title = title;
    for (int i = 1; i <= pages; i++)
    {
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using PdfGraphics g = page.CreateGraphics();
        g.DrawImage(logo, 72, 40, 96, 32);
        g.DrawString(title, PdfStandardFont.HelveticaBold, 22, PdfColor.Black, 72, 110);
        g.DrawString($"Page {i} of {pages}", PdfStandardFont.Helvetica, 11, PdfColor.Gray(0.4), 72, 135);
        for (int line = 0; line < 30; line++)
        {
            g.DrawString(
                $"Section {i}.{line + 1}: the quick brown fox jumps over the lazy dog, line {line + 1}.",
                PdfStandardFont.TimesRoman, 11, PdfColor.Black, 72, 170 + line * 18);
        }
        if (i == 1)
        {
            g.DrawImage(photo, 72, 720, 160, 100);
        }
    }
    doc.Save(path, options);
}
