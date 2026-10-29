using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class FormCreationTests
{
    private static PdfDocument Build()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage one = doc.Pages.Add(PdfRectangle.A4), two = doc.Pages.Add(PdfRectangle.A4);
        PdfAcroForm form = doc.Form;
        form.AddTextField(one, "name", 72, 100, 200, 20, "Ada Lovelace", new PdfFieldOptions { ToolTip = "Full name", Required = true });
        form.AddTextField(one, "notes", 72, 130, 200, 60, null, new PdfFieldOptions { Multiline = true, Font = PdfStandardFont.TimesRoman, FontSize = 10 });
        form.AddTextField(one, "code", 72, 200, 100, 20, "ABC", new PdfFieldOptions { MaxLength = 5, Comb = true, Alignment = PdfTextAlignment.Center });
        form.AddTextField(one, "id", 72, 230, 100, 20, "42", new PdfFieldOptions { ReadOnly = true });
        form.AddCheckBox(one, "subscribe", 72, 260, 14, 14, isChecked: true);
        form.AddRadioGroup("size", [(one, 72, 290, 14, 14, "S"), (one, 100, 290, 14, 14, "M"), (two, 72, 72, 14, 14, "L")], "M");
        form.AddComboBox(one, "country", 72, 320, 150, 20, ["Belgium", "France", "Morocco"], "France");
        form.AddListBox(one, "tags", 72, 350, 150, 60, ["red", "green", "blue"], ["red", "blue"], new PdfFieldOptions { MultiSelect = true });
        form.AddSignatureField(two, "signature", 72, 400, 200, 50);
        form.AddPushButton(two, "site", 72, 470, 100, 24, "Visit", new Uri("https://example.com/"));
        return PdfDocument.Open(doc.ToArray());
    }

    [TestMethod]
    public void CreatesEveryKindOfField()
    {
        PdfAcroForm form = Build().Form;
        Assert.IsTrue(form.Exists);
        CollectionAssert.AreEqual(
            new[] { "name", "notes", "code", "id", "subscribe", "size", "country", "tags", "signature", "site" },
            form.Fields.Select(f => f.Name).ToArray());

        var name = (PdfTextField)form["name"]!;
        Assert.AreEqual("Ada Lovelace", name.Text);
        Assert.AreEqual("Full name", name.ToolTip);
        Assert.IsTrue(name.Required);
        Assert.IsFalse(name.ReadOnly);

        // Placed from the page's top left, as drawing is: 100 points down, 20 high.
        PdfRectangle rect = name.Widgets[0].Rectangle;
        Assert.AreEqual(72, rect.Left, 1e-9);
        Assert.AreEqual(842 - 120, rect.Bottom, 1e-9);
        Assert.AreEqual(272, rect.Right, 1e-9);
        Assert.AreEqual(842 - 100, rect.Top, 1e-9);

        Assert.IsNull(((PdfTextField)form["notes"]!).Text);
        Assert.IsTrue(((PdfTextField)form["notes"]!).Multiline);
        Assert.IsTrue(((PdfTextField)form["code"]!).Comb);
        Assert.AreEqual(5, ((PdfTextField)form["code"]!).MaxLength);
        Assert.IsTrue(form["id"]!.ReadOnly);
        Assert.IsTrue(((PdfCheckBoxField)form["subscribe"]!).Checked);

        var size = (PdfRadioButtonField)form["size"]!;
        CollectionAssert.AreEqual(new[] { "S", "M", "L" }, size.Options.ToArray());
        Assert.AreEqual("M", size.Selected);
        Assert.HasCount(3, size.Widgets);
        Assert.AreEqual(1, size.Widgets[2].Page!.Index, "a group can span pages");

        var country = (PdfChoiceField)form["country"]!;
        Assert.AreEqual(PdfFieldKind.ComboBox, country.Kind);
        Assert.AreEqual("France", country.Value);
        var tags = (PdfChoiceField)form["tags"]!;
        Assert.AreEqual(PdfFieldKind.ListBox, tags.Kind);
        CollectionAssert.AreEqual(new[] { "red", "blue" }, tags.SelectedValues.ToArray());

        Assert.AreEqual(PdfFieldKind.Signature, form["signature"]!.Kind);
        Assert.IsFalse(((PdfSignatureField)form["signature"]!).IsSigned);
        Assert.AreEqual(PdfFieldKind.PushButton, form["site"]!.Kind);

        // Every widget carries its appearance and the print flag, so viewers and printers show it.
        foreach (PdfWidget widget in form.Fields.SelectMany(f => f.Widgets))
        {
            Assert.IsNotNull(widget.Dictionary["AP"], widget.Dictionary.ToString());
            Assert.AreEqual(4, ((PdfNumber)widget.Dictionary["F"]!).IntegerValue);
        }
    }

    [TestMethod]
    public void CreatedFieldsFillAndFlatten()
    {
        PdfDocument doc = Build();
        PdfAcroForm form = doc.Form;
        ((PdfTextField)form["name"]!).Text = "Grace Hopper";
        ((PdfRadioButtonField)form["size"]!).Selected = "L";
        ((PdfChoiceField)form["country"]!).Select("Morocco");
        Assert.ThrowsExactly<InvalidOperationException>(() => ((PdfTextField)form["id"]!).Text = "43");

        PdfDocument filled = PdfDocument.Open(doc.ToArray());
        Assert.AreEqual("Grace Hopper", filled.Form["name"]!.Value);
        Assert.AreEqual("L", filled.Form["size"]!.Value);

        // Flattened, the values are page content: they show and extract as text.
        filled.Form.Flatten();
        PdfDocument flat = PdfDocument.Open(filled.ToArray());
        Assert.IsFalse(flat.Form.Exists);
        string text = flat.Pages[0].ExtractText();
        StringAssert.Contains(text, "Grace Hopper");
        StringAssert.Contains(text, "Morocco");
        StringAssert.Contains(flat.Pages[1].ExtractText(), "Visit");
    }

    [TestMethod]
    public void RejectsBadNamesAndValues()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        doc.Form.AddTextField(page, "a", 0, 0, 10, 10);
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddTextField(page, "a", 0, 20, 10, 10));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddTextField(page, "a.b", 0, 20, 10, 10));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddTextField(page, "", 0, 20, 10, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.Form.AddCheckBox(page, "c", 0, 20, 0, 10));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddRadioGroup("r", [(page, 0, 0, 10, 10, "x"), (page, 20, 0, 10, 10, "x")]));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddRadioGroup("r", []));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddComboBox(page, "c", 0, 20, 50, 10, ["one"], "two"));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddPushButton(page, "p", 0, 20, 50, 10, "Go", new Uri("relative", UriKind.Relative)));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Form.AddTextField(PdfDocument.Create().Pages.Add(PdfRectangle.A4), "z", 0, 0, 10, 10));
    }
}
