#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("report.pdf");

#region info
Console.WriteLine($"Title: {doc.Info.Title}, PDF {doc.Version}, {doc.Pages.Count} pages");
doc.Info.Title = "Quarterly report, final";
doc.Info.Author = "Finance";
doc.Info.Keywords = "report, quarterly";
doc.Info.ModificationDate = DateTimeOffset.Now;
doc.Info["Department"] = "Controlling";        // custom entries too
foreach (string key in doc.Info.Keys)
{
    Console.WriteLine($"  {key}: {doc.Info[key]}");
}
#endregion

#region outlines
PdfOutline summary = doc.Outlines.Add("Summary", doc.Pages[0]);
summary.Add("Revenue", doc.Pages[1]);          // nested under Summary
summary.Add("Costs", doc.Pages[2]);
summary.IsOpen = true;                         // expanded when the file opens

foreach (PdfOutline bookmark in doc.Outlines)
{
    Console.WriteLine($"{bookmark.Title}: page {bookmark.Destination?.Index + 1}, {bookmark.Children.Count} children");
}
#endregion

doc.Save("report-final.pdf");
