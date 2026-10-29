using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Form fields travel with their pages through import, merge and split.</summary>
[TestClass]
public sealed class FieldImportTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    /// <summary>Two pages: a text field and a check box on the first, a radio group over both, a signature on the second.</summary>
    private static PdfDocument Form(string name = "Ada")
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage one = doc.Pages.Add(PdfRectangle.A4), two = doc.Pages.Add(PdfRectangle.A4);
        doc.Form.AddTextField(one, "name", 72, 100, 200, 20, name);
        doc.Form.AddCheckBox(one, "agree", 72, 130, 14, 14, isChecked: true);
        doc.Form.AddRadioGroup("size", [(one, 72, 160, 14, 14, "S"), (two, 72, 72, 14, 14, "L")], "L");
        doc.Form.AddSignatureField(two, "signature", 72, 400, 200, 50);
        return PdfDocument.Open(doc.ToArray());
    }

    private static string[] Names(PdfDocument doc) => [.. doc.Form.Fields.Select(f => f.Name)];

    [TestMethod]
    public void ImportedPagesBringTheirFields()
    {
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(Form().Pages);

        CollectionAssert.AreEqual(new[] { "name", "agree", "size", "signature" }, Names(target));
        Assert.AreEqual("Ada", ((PdfTextField)target.Form["name"]!).Text);
        Assert.IsTrue(((PdfCheckBoxField)target.Form["agree"]!).Checked);
        var size = (PdfRadioButtonField)target.Form["size"]!;
        Assert.AreEqual("L", size.Selected);
        Assert.HasCount(2, size.Widgets);
        Assert.AreSame(target.Pages[0], size.Widgets[0].Page);
        Assert.AreSame(target.Pages[1], size.Widgets[1].Page);

        // Each widget's parent is the copied field, which lists it.
        var parent = (PdfReference)size.Widgets[0].Dictionary["Parent"]!;
        Assert.AreSame(size.Dictionary, target.Resolve(parent));

        PdfDocument back = Reopen(target);
        CollectionAssert.AreEqual(new[] { "name", "agree", "size", "signature" }, Names(back));
        Assert.HasCount(2, back.Form["size"]!.Widgets);
    }

    [TestMethod]
    public void FieldsOnPagesLeftBehindStayBehind()
    {
        PdfDocument source = Form();
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import([source.Pages[0]]);

        CollectionAssert.AreEqual(new[] { "name", "agree", "size" }, Names(target));
        var size = (PdfRadioButtonField)target.Form["size"]!;
        Assert.HasCount(1, size.Widgets);
        CollectionAssert.AreEqual(new[] { "S" }, size.Options.ToArray());
        Assert.AreEqual("L", size.Selected);

        // Split gives each part the fields of its page.
        PdfDocument[] parts = [.. source.Split()];
        CollectionAssert.AreEqual(new[] { "name", "agree", "size" }, Names(parts[0]));
        CollectionAssert.AreEqual(new[] { "size", "signature" }, Names(parts[1]));
    }

    [TestMethod]
    public void NameConflictsAreRenamed()
    {
        PdfDocument merged = PdfDocument.Merge([Form("Ada"), Form("Grace"), Form("Alan")]);
        CollectionAssert.AreEqual(
            new[] { "name", "agree", "size", "signature", "name_2", "agree_2", "size_2", "signature_2", "name_3", "agree_3", "size_3", "signature_3" },
            Names(merged));

        // Each copy keeps its own value, and changing one leaves the others.
        Assert.AreEqual("Ada", ((PdfTextField)merged.Form["name"]!).Text);
        Assert.AreEqual("Grace", ((PdfTextField)merged.Form["name_2"]!).Text);
        Assert.AreEqual("Alan", ((PdfTextField)merged.Form["name_3"]!).Text);
        ((PdfTextField)merged.Form["name_2"]!).Text = "Grace Hopper";
        PdfDocument back = Reopen(merged);
        Assert.AreEqual("Ada", ((PdfTextField)back.Form["name"]!).Text);
        Assert.AreEqual("Grace Hopper", ((PdfTextField)back.Form["name_2"]!).Text);
        Assert.HasCount(2, back.Form["size_3"]!.Widgets);
        Assert.AreSame(back.Pages[5], back.Form["size_3"]!.Widgets[1].Page);
    }

    [TestMethod]
    public void TheFirstFreeSuffixIsUsed()
    {
        PdfDocument target = PdfDocument.Create();
        PdfPage page = target.Pages.Add(PdfRectangle.A4);
        target.Form.AddTextField(page, "name", 72, 100, 100, 20);
        target.Form.AddTextField(page, "name_2", 72, 130, 100, 20);
        target.Form.AddTextField(page, "other", 72, 160, 100, 20);
        target.Pages.Import([Form().Pages[0]]);
        CollectionAssert.AreEqual(new[] { "name", "name_2", "other", "name_3", "agree", "size" }, Names(target));
    }

    [TestMethod]
    public void DuplicatedPagesGetTheirOwnFields()
    {
        PdfDocument doc = Form();
        doc.Pages.Import([doc.Pages[0]]);
        CollectionAssert.AreEqual(new[] { "name", "agree", "size", "signature", "name_2", "agree_2", "size_2" }, Names(doc));
        ((PdfTextField)doc.Form["name_2"]!).Text = "Copy";
        Assert.AreEqual("Ada", ((PdfTextField)doc.Form["name"]!).Text);
        Assert.HasCount(2, doc.Form["size"]!.Widgets);
        Assert.HasCount(1, doc.Form["size_2"]!.Widgets);
        Assert.AreSame(doc.Pages[2], doc.Form["name_2"]!.Widgets[0].Page);
        Reopen(doc);
    }

    [TestMethod]
    public void NestedFieldsKeepTheirTree()
    {
        // address.city and address.zip under one parent, the parent holding the default appearance.
        PdfDocument source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(PdfRectangle.A4);
        var parent = new PdfDictionary { ["T"] = PdfString.FromText("address"), ["FT"] = new PdfName("Tx"), ["DA"] = PdfString.FromText("/Helv 9 Tf 0 g") };
        PdfReference parentReference = source.AddObject(parent);
        var kids = new PdfArray();
        foreach ((string name, double y) in new[] { ("city", 700.0), ("zip", 670.0) })
        {
            var widget = new PdfDictionary
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Widget"),
                ["T"] = PdfString.FromText(name),
                ["V"] = PdfString.FromText(name.ToUpperInvariant()),
                ["Rect"] = new PdfRectangle(72, y, 272, y + 20).ToArray(),
                ["Parent"] = parentReference,
            };
            kids.Add(page.Annotations.AddAnnotation(widget));
        }

        parent.Set("Kids", kids);
        PdfReference helv = source.AddObject(new PdfDictionary { ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type1"), ["BaseFont"] = new PdfName("Helvetica") });
        source.Catalog.Set("AcroForm", new PdfDictionary
        {
            ["Fields"] = PdfArray.Of(parentReference),
            ["DR"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["Helv"] = helv } },
            ["NeedAppearances"] = PdfBoolean.True,
            ["CO"] = PdfArray.Of(kids[1]),
        });

        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source.Pages);
        target.Pages.Import(source.Pages);
        CollectionAssert.AreEqual(new[] { "address.city", "address.zip", "address_2.city", "address_2.zip" }, Names(target));
        Assert.AreEqual("ZIP", target.Form["address_2.zip"]!.Value);

        // The form's resources, need for appearances and calculation order come along.
        var form = (PdfDictionary)target.Resolve(target.Catalog["AcroForm"])!;
        Assert.IsNotNull(((PdfDictionary)target.Resolve(((PdfDictionary)target.Resolve(form["DR"])!)["Font"])!)["Helv"]);
        Assert.IsTrue(target.Form.NeedAppearances);
        var order = (PdfArray)target.Resolve(form["CO"])!;
        Assert.HasCount(2, order);
        Assert.AreSame(target.Form["address.zip"]!.Dictionary, target.Resolve(order[0]));
        Assert.AreSame(target.Form["address_2.zip"]!.Dictionary, target.Resolve(order[1]));

        // Setting a value draws with the inherited default appearance.
        ((PdfTextField)target.Form["address_2.city"]!).Text = "Brussels";
        Reopen(target);
    }

    [TestMethod]
    public void TheSourceDefaultAppearanceIsKept()
    {
        PdfDocument target = PdfDocument.Create();
        PdfPage page = target.Pages.Add(PdfRectangle.A4);
        target.Form.AddTextField(page, "existing", 72, 100, 100, 20);
        var targetForm = (PdfDictionary)target.Resolve(target.Catalog["AcroForm"])!;
        targetForm.Set("DA", PdfString.FromText("/Helvetica 0 Tf 1 0 0 rg"));

        // A form whose fields take their font and color from the form's default.
        PdfDocument source = PdfDocument.Create();
        PdfPage sourcePage = source.Pages.Add(PdfRectangle.A4);
        source.Form.AddTextField(sourcePage, "imported", 72, 100, 100, 20, "x");
        source.Form.Fields[0].Dictionary.Remove("DA");
        ((PdfDictionary)source.Resolve(source.Catalog["AcroForm"])!).Set("DA", PdfString.FromText("/Courier 10 Tf 0 0 1 rg"));

        target.Pages.Import(source.Pages);
        PdfField imported = target.Form["imported"]!;
        Assert.AreEqual("/Courier 10 Tf 0 0 1 rg", ((PdfString)target.Resolve(imported.Dictionary["DA"])!).ToText());
        Assert.IsNotNull(target.Form["existing"]!.Dictionary["DA"]);
        Assert.AreEqual("/Helvetica 0 Tf 1 0 0 rg", ((PdfString)target.Resolve(targetForm["DA"])!).ToText());
    }

    [TestMethod]
    public void ReportLabFormsImport()
    {
        PdfDocument source = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forms", "form.pdf"));
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source.Pages);
        target.Pages.Import(source.Pages);
        string[] names = Names(target);
        Assert.HasCount(16, names);
        CollectionAssert.AreEqual(source.Form.Fields.Select(f => f.Name).ToArray(), names.Take(8).ToArray());
        CollectionAssert.AreEqual(source.Form.Fields.Select(f => f.Name + "_2").ToArray(), names.Skip(8).ToArray());
        Assert.AreEqual("someone@example.com", target.Form["email_2"]!.Value);
        Assert.AreEqual("basic", target.Form["plan_2"]!.Value);
        Assert.HasCount(3, target.Form["plan_2"]!.Widgets);

        ((PdfRadioButtonField)target.Form["plan_2"]!).Selected = "team";
        Assert.AreEqual("basic", target.Form["plan"]!.Value);
        target.Form.Flatten();
        Assert.IsFalse(Reopen(target).Form.Exists);
    }

    [TestMethod]
    public void HostileFieldTreesImport()
    {
        // A field tree whose nodes list themselves and each other: import terminates and the
        // widget comes along as a plain annotation when no path reaches it.
        PdfDocument source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(PdfRectangle.A4);
        var loop = new PdfDictionary { ["T"] = PdfString.FromText("loop"), ["FT"] = new PdfName("Tx") };
        PdfReference loopReference = source.AddObject(loop);
        loop.Set("Kids", PdfArray.Of(loopReference, loopReference, PdfNull.Instance, new PdfReference(9999, 0)));
        var orphan = new PdfDictionary { ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Widget"), ["Rect"] = PdfRectangle.A4.ToArray(), ["Parent"] = loopReference };
        page.Annotations.AddAnnotation(orphan);
        source.Catalog.Set("AcroForm", new PdfDictionary { ["Fields"] = PdfArray.Of(loopReference, PdfNull.Instance, new PdfName("x")) });

        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source.Pages);
        Assert.HasCount(1, target.Pages[0].Annotations);
        Assert.IsNull(target.Pages[0].Annotations[0].Dictionary["Parent"]);
        Assert.IsFalse(target.Form.Exists);
        Reopen(target);

        // A source form with no field array imports its pages as before.
        source.Catalog.Set("AcroForm", new PdfDictionary());
        PdfDocument other = PdfDocument.Create();
        other.Pages.Import(source.Pages);
        Assert.IsFalse(other.Form.Exists);
    }
}
