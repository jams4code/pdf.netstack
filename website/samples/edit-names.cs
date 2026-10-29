#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Objects;

WriteGuideWithNamedDestinations("guide.pdf");

#region names
PdfDocument doc = PdfDocument.Open("guide.pdf");
foreach (string name in doc.NamedDestinations.Names)
{
    Console.WriteLine($"{name}: page {doc.NamedDestinations.GetPage(name)?.Index + 1}");
}

foreach (PdfOutline bookmark in doc.Outlines)
{
    Console.WriteLine($"Bookmark {bookmark.Title} points at {bookmark.DestinationName}");
}

// Every bookmark, link and GoTo action that uses the old names follows the rename.
int changed = doc.NamedDestinations.Rename(new Dictionary<string, string>
{
    ["_TOC_d1e10"] = "introduction",
    ["_TOC_d1e20"] = "installation",
});
Console.WriteLine($"{changed} references renamed");
doc.Save("guide-renamed.pdf");
#endregion

// A three page document whose bookmarks point at named destinations, as publishing tools write it.
static void WriteGuideWithNamedDestinations(string path)
{
    PdfDocument doc = PdfDocument.Create();
    for (int i = 0; i < 3; i++) doc.Pages.Add(PdfRectangle.A4);
    doc = PdfDocument.Open(doc.ToArray());
    var kids = (PdfArray)doc.Resolve(((PdfDictionary)doc.Resolve(doc.Catalog["Pages"])!)["Kids"])!;

    var dests = new PdfDictionary
    {
        ["_TOC_d1e10"] = PdfArray.Of(kids[0], new PdfName("Fit")),
        ["_TOC_d1e20"] = PdfArray.Of(kids[2], new PdfName("Fit")),
    };
    doc.Catalog.Set("Dests", doc.AddObject(dests));
    doc.Outlines.Add("Introduction");
    doc.Outlines.Add("Installation");
    doc.Save(path);

    // Bookmarks added through the API point at pages; point these two at the names instead.
    doc = PdfDocument.Open(path);
    var outlines = (PdfDictionary)doc.Resolve(doc.Catalog["Outlines"])!;
    var first = (PdfDictionary)doc.Resolve(outlines["First"])!;
    var last = (PdfDictionary)doc.Resolve(outlines["Last"])!;
    first.Set("Dest", PdfString.FromText("_TOC_d1e10"));
    last.Set("Dest", PdfString.FromText("_TOC_d1e20"));
    doc.Save(path);
}
