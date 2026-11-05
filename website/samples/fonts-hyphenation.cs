#:project ../../src/PdfNetStack.Hyphenation/PdfNetStack.Hyphenation.csproj
#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.2
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Hyphenation;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    #region paragraph
    // Opt in per paragraph. A language loads on first use; the hyphenator is shared and thread safe.
    var german = new PdfParagraph { Alignment = PdfTextAlignment.Justify, Hyphenator = PdfHyphenationPatterns.ForLanguage("de") }
        .Add("Die Rechtschreibung regelt die Silbentrennung, auch im Bundesverfassungsgericht.", new PdfTextStyle(PdfStandardFont.Helvetica, 11));
    g.DrawText(german, 50, 60, 160);
    #endregion

    #region tuning
    // Tune the limits, and add your own words: a word without a hyphen never breaks.
    PdfHyphenator english = PdfHyphenationPatterns.ForLanguage("en-GB")!.WithExceptions(["PdfNetStack", "ta-ble"]) with { MinWordLength = 6, MinLeft = 3 };
    string marked = english.InsertSoftHyphens("Characterisation of programmes");   // soft hyphens at each point
    Console.WriteLine(string.Join("|", english.FindBreaks("Characterisation")));
    #endregion

    g.DrawText(new PdfParagraph().Add(marked, new PdfTextStyle(PdfStandardFont.Helvetica, 11)), 50, 160, 90);
}

doc.Save("hyphenation.pdf");

#region html
// In HTML: hyphens: auto, the language from the nearest lang attribute, and a way to find its patterns.
byte[] pdf = HtmlConverter.ConvertToPdf(
    """<html lang="fr"><body style="hyphens: auto; text-align: justify; width: 200px"><p>Le développement de la bibliothèque.</p></body></html>""",
    new HtmlConverterOptions { Hyphenators = PdfHyphenationPatterns.ForLanguage });
File.WriteAllBytes("hyphenation-html.pdf", pdf);
#endregion
