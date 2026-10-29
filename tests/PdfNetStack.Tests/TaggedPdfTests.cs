using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Html;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class TaggedPdfTests
{
    private static readonly string Svg = Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><rect width='20' height='20' fill='navy'/></svg>"));

    private static PdfDocument Render(string html, bool tagged = true) =>
        PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false, Tagged = tagged }));

    private static string Content(PdfDocument doc, int page)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    /// <summary>The structure tree as types and children, e.g. Document(H1,P(Link)).</summary>
    private static string Tree(PdfDocument doc, PdfObject? node)
    {
        if (doc.Resolve(node) is not PdfDictionary element || element.GetName("S") is not string type) return "";
        IEnumerable<PdfObject> kids = doc.Resolve(element["K"]) switch
        {
            PdfArray a => a,
            PdfObject single => [single],
            _ => [],
        };
        string[] children = [.. kids.Select(k => Tree(doc, k)).Where(t => t.Length > 0)];
        return children.Length == 0 ? type : $"{type}({string.Join(",", children)})";
    }

    private static PdfDictionary Root(PdfDocument doc) => (PdfDictionary)doc.Resolve(doc.Catalog["StructTreeRoot"])!;

    [TestMethod]
    public void BuildsTheStructureTreeInReadingOrder()
    {
        PdfDocument doc = Render($"""
            <html lang=en><head><title>Tagged</title></head><body>
            <h1>Title</h1>
            <p>Read <a href="https://example.com/">the site</a> now.</p>
            <ul><li>one</li><li>two</li></ul>
            <table><thead><tr><th>Name</th></tr></thead><tbody><tr><td>Ada</td></tr></tbody></table>
            <img src="data:image/svg+xml;base64,{Svg}" alt="Logo">
            </body></html>
            """);

        Assert.IsTrue(((PdfDictionary)doc.Resolve(doc.Catalog["MarkInfo"])!)["Marked"] is PdfBoolean { Value: true });
        Assert.IsTrue(((PdfDictionary)doc.Resolve(doc.Catalog["ViewerPreferences"])!)["DisplayDocTitle"] is PdfBoolean { Value: true });
        PdfDictionary root = Root(doc);
        string tree = Tree(doc, ((PdfArray)doc.Resolve(root["K"])!)[0]);
        Assert.AreEqual("Document(H1,P(Link),L(LI,LI),Table(THead(TR(TH)),TBody(TR(TD))),Figure)", tree, tree);
    }

    [TestMethod]
    public void FiguresCarryAltTextAndLinksTheirAnnotations()
    {
        PdfDocument doc = Render($"""<p><a href="https://example.com/">Example site</a> <img src="data:image/svg+xml;base64,{Svg}" alt="Company logo"></p>""");
        var elements = new List<PdfDictionary>();
        void Collect(PdfObject? node)
        {
            if (doc.Resolve(node) is not PdfDictionary e) return;
            if (e.GetName("Type") is "StructElem") elements.Add(e);
            if (doc.Resolve(e["K"]) is PdfArray kids) foreach (PdfObject k in kids) Collect(k);
        }

        Collect(Root(doc)["K"] is PdfArray top ? top[0] : null);
        PdfDictionary figure = elements.Single(e => e.GetName("S") == "Figure");
        Assert.AreEqual("Company logo", ((PdfString)doc.Resolve(figure["Alt"])!).ToText());

        // The Link element holds the annotation by an object reference, and the annotation points back.
        PdfDictionary link = elements.Single(e => e.GetName("S") == "Link");
        PdfDictionary objr = ((PdfArray)doc.Resolve(link["K"])!).Select(doc.Resolve).OfType<PdfDictionary>().Single(d => d.GetName("Type") == "OBJR");
        var annotation = (PdfDictionary)doc.Resolve(objr["Obj"])!;
        Assert.AreEqual("Link", annotation.GetName("Subtype"));
        Assert.IsInstanceOfType<PdfNumber>(annotation["StructParent"]);
        Assert.AreEqual("Example site", ((PdfString)annotation["Contents"]!).ToText());
    }

    [TestMethod]
    public void EveryMarkedSequenceHasItsParent()
    {
        string rows = string.Concat(Enumerable.Range(1, 80).Select(i => $"<tr><td>Row {i}</td></tr>"));
        PdfDocument doc = Render($$"""
            <style>@page { @bottom-center { content: "Page " counter(page) } } body { background: #eee } td { border: 1px solid }</style>
            <div style="position: fixed; top: 0; right: 0">Draft</div>
            <table><thead><tr><th>Heading</th></tr></thead><tbody>{{rows}}</tbody></table>
            """);
        Assert.IsGreaterThan(1, doc.Pages.Count);
        var nums = (PdfArray)doc.Resolve(((PdfDictionary)doc.Resolve(Root(doc)["ParentTree"])!)["Nums"])!;
        var parents = new Dictionary<long, PdfObject>();
        for (int i = 0; i + 1 < nums.Count; i += 2) parents[((PdfNumber)nums[i]).IntegerValue] = nums[i + 1];

        for (int page = 0; page < doc.Pages.Count; page++)
        {
            string content = Content(doc, page);
            int[] mcids = [.. Regex.Matches(content, @"/MCID\s+(\d+)").Cast<Match>().Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
            CollectionAssert.AreEqual(Enumerable.Range(0, mcids.Length).ToArray(), mcids, $"page {page + 1}: MCIDs run 0, 1, 2 and so on");

            // The page's entry in the parent tree has one structure element per MCID.
            long key = ((PdfNumber)doc.Pages[page].Dictionary["StructParents"]!).IntegerValue;
            Assert.HasCount(mcids.Length, (PdfArray)doc.Resolve(parents[key])!, $"page {page + 1}");

            // Marked sequences are balanced, and the page number, the fixed box and backgrounds are artifacts.
            int opened = Regex.Matches(content, @"\b(BDC|BMC)\b").Cast<Match>().Count(), closed = Regex.Matches(content, @"\bEMC\b").Cast<Match>().Count();
            Assert.AreEqual(opened, closed);
            StringAssert.Contains(content, "/Artifact BMC");
        }

        // The header shows on every page, but only its first showing is content: later ones are artifacts.
        string tableText = string.Join("\n", Enumerable.Range(0, doc.Pages.Count).Select(p => doc.Pages[p].ExtractText()));
        Assert.HasCount(doc.Pages.Count, Regex.Matches(tableText, "Heading"));
        int tagged = Enumerable.Range(0, doc.Pages.Count).Sum(p => Regex.Matches(Content(doc, p), @"/TH <</MCID\s").Cast<Match>().Count());
        Assert.AreEqual(1, tagged);
    }

    [TestMethod]
    public void WritesNoTagsWhenAskedNot()
    {
        PdfDocument doc = Render("<h1>Plain</h1><p>text</p>", tagged: false);
        Assert.IsNull(doc.Catalog["StructTreeRoot"]);
        Assert.IsNull(doc.Catalog["MarkInfo"]);
        Assert.DoesNotContain("BDC", Content(doc, 0));
        Assert.DoesNotContain("BMC", Content(doc, 0));
    }
}
