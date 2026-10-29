#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("report.pdf");

#region text
foreach (PdfPage page in doc.Pages)
{
    Console.WriteLine(page.ExtractText());   // lines top to bottom, words spaced
}
#endregion

#region words
foreach (PdfWord word in doc.Pages[0].GetWords().Take(5))
{
    PdfRectangle b = word.Bounds;             // points, from the bottom left of the page
    Console.WriteLine($"{word.Text,-10} left {b.Left:0.#} bottom {b.Bottom:0.#} width {b.Width:0.#}");
}
#endregion

#region letters
foreach (PdfLetter letter in doc.Pages[0].GetLetters().Take(3))
{
    Console.WriteLine($"{letter.Text} {letter.FontName} {letter.FontSize:0.#} pt, baseline at y {letter.BaselineStart.Y:0.#}");
}
#endregion
