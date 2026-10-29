#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Objects;

#region catalog
PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Catalog.Set("PageMode", new PdfName("UseOutlines"));   // open with the bookmarks panel shown

PdfPage page = doc.Pages[0];
if (doc.Resolve(page.Dictionary["Resources"]) is PdfDictionary resources
    && doc.Resolve(resources["Font"]) is PdfDictionary fonts)
{
    Console.WriteLine($"Fonts on page 1: {string.Join(", ", fonts.Keys)}");
}
#endregion

#region read
// /Contents is one stream or an array of streams.
PdfObject? contents = doc.Resolve(page.Dictionary["Contents"]);
IEnumerable<PdfObject> parts = contents is PdfArray array ? array : [contents!];
foreach (PdfObject part in parts)
{
    if (doc.Resolve(part) is PdfStream stream)
    {
        byte[] content = doc.GetStreamData(stream);   // filters undone, image codecs kept
        Console.WriteLine($"Content stream: {content.Length:N0} bytes decoded, {stream.Data.Length:N0} stored");
    }
}
#endregion

#region write
// A second content stream that draws a blue square over the page.
PdfStream square = PdfStream.FromData("q 0 0 1 rg 72 72 144 144 re f Q"u8);
PdfObject existing = page.Dictionary["Contents"]!;
page.Dictionary.Set("Contents", PdfArray.Of(existing, doc.AddObject(square)));
doc.Info["Reviewed"] = "yes";
doc.Save("report-square.pdf");
#endregion
