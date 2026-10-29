using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Forms against the ReportLab fixture of tools/make_form_fixtures.py.</summary>
[TestClass]
public sealed class AcroFormTests
{
    private static PdfDocument Fixture() => PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forms", "form.pdf"));

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static string AppearanceText(PdfDocument doc, PdfWidget widget)
    {
        var ap = (PdfDictionary)doc.Resolve(widget.Dictionary["AP"])!;
        return Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(ap["N"])!));
    }

    [TestMethod]
    public void ReadsFieldsAndValues()
    {
        PdfAcroForm form = Fixture().Form;
        Assert.IsTrue(form.Exists);
        CollectionAssert.AreEqual(
            new[] { "name", "email", "code", "notes", "subscribe", "plan", "country", "languages" },
            form.Fields.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PdfFieldKind.Text, PdfFieldKind.Text, PdfFieldKind.Text, PdfFieldKind.Text, PdfFieldKind.CheckBox, PdfFieldKind.RadioButton, PdfFieldKind.ComboBox, PdfFieldKind.ListBox },
            form.Fields.Select(f => f.Kind).ToArray());

        Assert.AreEqual("someone@example.com", form["email"]!.Value);
        Assert.AreEqual("Full name", form["name"]!.ToolTip);
        Assert.AreEqual(6, ((PdfTextField)form["code"]!).MaxLength);
        Assert.IsTrue(((PdfTextField)form["notes"]!).Multiline);
        var subscribe = (PdfCheckBoxField)form["subscribe"]!;
        Assert.IsFalse(subscribe.Checked);
        Assert.AreEqual("Yes", subscribe.OnValue);
        var plan = (PdfRadioButtonField)form["plan"]!;
        CollectionAssert.AreEqual(new[] { "basic", "pro", "team" }, plan.Options.ToArray());
        Assert.AreEqual("basic", plan.Selected);
        Assert.HasCount(3, plan.Widgets);
        var country = (PdfChoiceField)form["country"]!;
        Assert.AreEqual("Switzerland", country.Value);
        CollectionAssert.AreEqual(new[] { "France", "Switzerland", "Morocco" }, country.Options.Select(o => o.Text).ToArray());
        Assert.IsTrue(form.Fields.All(f => f.Widgets.All(w => w.Page is not null)));
    }

    [TestMethod]
    public void FillsEveryKind()
    {
        PdfDocument doc = Fixture();
        PdfAcroForm form = doc.Form;
        ((PdfTextField)form["name"]!).Text = "Jamal Abdelkhalek";
        ((PdfTextField)form["code"]!).Text = "ABC1234567";
        ((PdfTextField)form["notes"]!).Text = "First line of a longer note that has to wrap inside the box.\nSecond paragraph.";
        ((PdfCheckBoxField)form["subscribe"]!).Checked = true;
        ((PdfRadioButtonField)form["plan"]!).Selected = "pro";
        ((PdfChoiceField)form["country"]!).Select("Morocco");
        ((PdfChoiceField)form["languages"]!).Select("French");

        PdfDocument back = Reopen(doc);
        PdfAcroForm filled = back.Form;
        Assert.AreEqual("Jamal Abdelkhalek", filled["name"]!.Value);
        Assert.AreEqual("ABC123", filled["code"]!.Value);
        Assert.IsTrue(((PdfCheckBoxField)filled["subscribe"]!).Checked);
        Assert.AreEqual("pro", filled["plan"]!.Value);
        Assert.AreEqual("Morocco", filled["country"]!.Value);
        Assert.AreEqual("French", filled["languages"]!.Value);

        // Appearances: the text in a /Tx marked content, the radio buttons switched.
        string name = AppearanceText(back, filled["name"]!.Widgets[0]);
        StringAssert.StartsWith(name, "/Tx BMC\n");
        StringAssert.Contains(name, "(Jamal Abdelkhalek)Tj");
        CollectionAssert.AreEqual(new[] { "Off", "pro", "Off" }, filled["plan"]!.Widgets.Select(w => w.Dictionary.GetName("AS")).ToArray());
        Assert.AreEqual("Yes", filled["subscribe"]!.Widgets[0].Dictionary.GetName("AS"));
        StringAssert.Contains(AppearanceText(back, filled["country"]!.Widgets[0]), "(Morocco)Tj");
    }

    [TestMethod]
    public void RejectsInvalidValues()
    {
        PdfAcroForm form = Fixture().Form;
        Assert.ThrowsExactly<ArgumentException>(() => ((PdfRadioButtonField)form["plan"]!).Selected = "gold");
        Assert.ThrowsExactly<ArgumentException>(() => ((PdfChoiceField)form["country"]!).Select("Atlantis"));
        Assert.ThrowsExactly<ArgumentException>(() => ((PdfChoiceField)form["languages"]!).Select(["Arabic", "French"]));

        var name = (PdfTextField)form["name"]!;
        name.Dictionary.Set("Ff", PdfNumber.FromInteger(1));
        Assert.IsTrue(name.ReadOnly);
        Assert.ThrowsExactly<InvalidOperationException>(() => name.Text = "x");
    }

    [TestMethod]
    public void FlatteningKeepsTheValuesAsPageContent()
    {
        PdfDocument doc = Fixture();
        ((PdfTextField)doc.Form["name"]!).Text = "Flattened Value";
        doc.Form.Flatten();
        Assert.IsFalse(doc.Form.Exists);

        PdfDocument back = Reopen(doc);
        Assert.IsFalse(back.Form.Exists);
        Assert.AreEqual(0, back.Pages[0].Annotations.Count(a => a.Subtype == "Widget"));
        PdfObject? contents = back.Resolve(back.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        string content = string.Concat(parts.Select(p => Encoding.ASCII.GetString(back.GetStreamData((PdfStream)back.Resolve(p)!))));
        Assert.IsGreaterThanOrEqualTo(8, content.Split(new[] { " Do\n" }, StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void CyclicFieldTreesTerminate()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Pages.Add(PdfRectangle.A4);
        var node = new PdfDictionary { ["T"] = PdfString.FromText("loop"), ["FT"] = new PdfName("Tx") };
        PdfReference self = doc.AddObject(node);
        node.Set("Kids", PdfArray.Of(self, self));
        doc.Catalog.Set("AcroForm", new PdfDictionary { ["Fields"] = PdfArray.Of(self, self) });
        Assert.IsEmpty(doc.Form.Fields);
    }

    [TestMethod]
    public void DocumentsWithoutAFormHaveNoFields()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Pages.Add(PdfRectangle.A4);
        Assert.IsFalse(doc.Form.Exists);
        Assert.IsEmpty(doc.Form.Fields);
        doc.Form.Flatten();
    }
}
