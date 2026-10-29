using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class NamedDestinationTests
{
    private const string Intro = "_OPENTOPIC_TOC_PROCESSING_d1e10";
    private const string Scope = "_OPENTOPIC_TOC_PROCESSING_d1e20";

    /// <summary>
    /// Three pages with named destinations as publishing tools write them: a name tree split into
    /// kids with limits, the older catalog /Dests, bookmarks by /Dest string, /A GoTo and /Dest name,
    /// a link, an open action, and an unrelated string that happens to equal a name.
    /// </summary>
    private static PdfDocument Build()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage[] pages = [doc.Pages.Add(PdfRectangle.A4), doc.Pages.Add(PdfRectangle.A4), doc.Pages.Add(PdfRectangle.A4)];
        PdfArray At(int page) => PdfArray.Of(new PdfReference(pages[page].Number, 0), new PdfName("XYZ"), PdfNull.Instance, PdfNumber.FromInteger(800), PdfNull.Instance);

        PdfReference leaf1 = doc.AddObject(new PdfDictionary
        {
            ["Limits"] = PdfArray.Of(PdfString.FromText(Intro), PdfString.FromText(Scope)),
            ["Names"] = PdfArray.Of(PdfString.FromText(Intro), At(0), PdfString.FromText(Scope), At(1)),
        });
        PdfReference leaf2 = doc.AddObject(new PdfDictionary
        {
            ["Limits"] = PdfArray.Of(PdfString.FromText("z-last"), PdfString.FromText("z-last")),
            ["Names"] = PdfArray.Of(PdfString.FromText("z-last"), doc.AddObject(new PdfDictionary { ["D"] = At(2) })),
        });
        doc.Catalog.Set("Names", new PdfDictionary { ["Dests"] = doc.AddObject(new PdfDictionary { ["Kids"] = PdfArray.Of(leaf1, leaf2) }) });
        doc.Catalog.Set("Dests", doc.AddObject(new PdfDictionary { ["legacy"] = PdfArray.Of(new PdfReference(pages[2].Number, 0), new PdfName("Fit")) }));

        var outlines = new PdfDictionary { ["Type"] = new PdfName("Outlines") };
        PdfReference outlinesRef = doc.AddObject(outlines);
        var intro = new PdfDictionary { ["Title"] = PdfString.FromText("1 Introduction"), ["Parent"] = outlinesRef, ["Dest"] = PdfString.FromText(Intro) };
        var scope = new PdfDictionary
        {
            ["Title"] = PdfString.FromText("1.1 Scope"),
            ["A"] = doc.AddObject(new PdfDictionary { ["S"] = new PdfName("GoTo"), ["D"] = PdfString.FromText(Scope) }),
        };
        var appendix = new PdfDictionary { ["Title"] = PdfString.FromText("2 Appendix"), ["Parent"] = outlinesRef, ["Dest"] = new PdfName("legacy") };
        PdfReference introRef = doc.AddObject(intro), scopeRef = doc.AddObject(scope), appendixRef = doc.AddObject(appendix);
        scope.Set("Parent", introRef);
        intro.Set("First", scopeRef);
        intro.Set("Last", scopeRef);
        intro.Set("Count", PdfNumber.FromInteger(1));
        intro.Set("Next", appendixRef);
        appendix.Set("Prev", introRef);
        outlines.Set("First", introRef);
        outlines.Set("Last", appendixRef);
        outlines.Set("Count", PdfNumber.FromInteger(3));
        doc.Catalog.Set("Outlines", outlinesRef);
        doc.Catalog.Set("OpenAction", new PdfDictionary { ["S"] = new PdfName("GoTo"), ["D"] = PdfString.FromText(Intro) });

        pages[0].Dictionary.Set("Annots", PdfArray.Of(doc.AddObject(new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Link"),
            ["Rect"] = PdfArray.Of(72, 700, 200, 720),
            ["A"] = new PdfDictionary { ["S"] = new PdfName("GoTo"), ["D"] = PdfString.FromText(Scope) },
        })));
        pages[0].Dictionary.Set("Note", PdfString.FromText(Intro));
        return PdfDocument.Open(doc.ToArray());
    }

    [TestMethod]
    public void ListsAndResolvesNames()
    {
        PdfDocument doc = Build();
        CollectionAssert.AreEqual(new[] { Intro, Scope, "z-last", "legacy" }, doc.NamedDestinations.Names.ToArray());
        Assert.AreEqual(0, doc.NamedDestinations.GetPage(Intro)!.Index);
        Assert.AreEqual(1, doc.NamedDestinations.GetPage(Scope)!.Index);
        Assert.AreEqual(2, doc.NamedDestinations.GetPage("z-last")!.Index, "a destination dictionary with /D");
        Assert.AreEqual(2, doc.NamedDestinations.GetPage("legacy")!.Index);
        Assert.IsNull(doc.NamedDestinations.GetPage("missing"));

        Assert.AreEqual(Intro, doc.Outlines[0].DestinationName);
        Assert.AreEqual(Scope, doc.Outlines[0].Children[0].DestinationName, "through a GoTo action");
        Assert.AreEqual("legacy", doc.Outlines[1].DestinationName);
        Assert.AreEqual(1, doc.Outlines[0].Children[0].Destination!.Index);
    }

    [TestMethod]
    public void RenamesEveryReference()
    {
        PdfDocument doc = Build();
        int changed = doc.NamedDestinations.Rename(new Dictionary<string, string>
        {
            [Intro] = "GUID-B",
            [Scope] = "GUID-A",
            ["legacy"] = "GUID-C",
        });

        // Three definitions; two bookmarks by /Dest, one by action, a link and the open action.
        Assert.AreEqual(8, changed);

        PdfDocument saved = PdfDocument.Open(saveAndReopen(doc));
        PdfNamedDestinations names = saved.NamedDestinations;
        CollectionAssert.AreEqual(new[] { "GUID-A", "GUID-B", "z-last", "GUID-C" }, names.Names.ToArray(), "the tree sorted, the catalog's after");
        Assert.AreEqual(0, names.GetPage("GUID-B")!.Index);
        Assert.AreEqual(1, names.GetPage("GUID-A")!.Index);
        Assert.AreEqual(2, names.GetPage("GUID-C")!.Index);

        // The tree is one leaf now: nothing can sit outside its kids' limits.
        var tree = (PdfDictionary)saved.Resolve(((PdfDictionary)saved.Resolve(saved.Catalog["Names"])!)["Dests"])!;
        Assert.IsNull(tree["Kids"]);
        Assert.IsNull(tree["Limits"]);

        // Every reference follows, in its own form.
        Assert.AreEqual("GUID-B", saved.Outlines[0].DestinationName);
        Assert.AreEqual("GUID-A", saved.Outlines[0].Children[0].DestinationName);
        Assert.AreEqual("GUID-C", saved.Outlines[1].DestinationName);
        Assert.IsInstanceOfType<PdfName>(saved.Resolve(((PdfDictionary)saved.Resolve(OutlineItem(saved, 1))!)["Dest"]));
        Assert.AreEqual(0, saved.Outlines[0].Destination!.Index);
        Assert.AreEqual(1, saved.Outlines[0].Children[0].Destination!.Index);
        Assert.AreEqual(1, saved.Pages[0].Annotations[0].Destination!.Index);
        Assert.AreEqual("GUID-B", ((PdfString)((PdfDictionary)saved.Resolve(saved.Catalog["OpenAction"])!)["D"]!).ToText());

        // A string that is not a destination reference keeps its text.
        Assert.AreEqual(Intro, ((PdfString)saved.Pages[0].Dictionary["Note"]!).ToText());

        static byte[] saveAndReopen(PdfDocument d)
        {
            using var output = new MemoryStream();
            d.Save(output);
            return output.ToArray();
        }
    }

    private static PdfObject OutlineItem(PdfDocument doc, int index)
    {
        var outlines = (PdfDictionary)doc.Resolve(doc.Catalog["Outlines"])!;
        PdfObject item = outlines["First"]!;
        for (int i = 0; i < index; i++) item = ((PdfDictionary)doc.Resolve(item)!)["Next"]!;
        return item;
    }

    [TestMethod]
    public void RefusesRenamesThatCollide()
    {
        PdfDocument doc = Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.NamedDestinations.Rename(Intro, "z-last"));
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.NamedDestinations.Rename(new Dictionary<string, string> { [Intro] = "same", [Scope] = "same" }));
        Assert.ThrowsExactly<ArgumentException>(() => doc.NamedDestinations.Rename(Intro, ""));
        Assert.AreEqual(0, doc.NamedDestinations.Rename(Intro, Intro));
        Assert.AreEqual(0, doc.NamedDestinations.Rename("not-there", "anything"));
        CollectionAssert.Contains(doc.NamedDestinations.Names.ToArray(), Intro, "nothing changed");
    }
}
