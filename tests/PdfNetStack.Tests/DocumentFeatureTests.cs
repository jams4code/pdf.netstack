using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class DocumentFeatureTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static PdfDocument WithPages(int count)
    {
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < count; i++) doc.Pages.Add(PdfRectangle.A4);
        return doc;
    }

    [TestMethod]
    public void ViewerSettingsRoundTrip()
    {
        PdfDocument doc = WithPages(3);
        PdfViewerSettings v = doc.Viewer;
        v.PageLayout = PdfPageLayout.TwoPageRight;
        v.PageMode = PdfPageMode.UseOutlines;
        v.DisplayDocumentTitle = true;
        v.HideToolbar = true;
        v.RightToLeft = true;
        v.OpenAt(doc.Pages[1], top: 100, zoom: 1.5);

        PdfDocument back = Reopen(doc);
        Assert.AreEqual(PdfPageLayout.TwoPageRight, back.Viewer.PageLayout);
        Assert.AreEqual(PdfPageMode.UseOutlines, back.Viewer.PageMode);
        Assert.IsTrue(back.Viewer.DisplayDocumentTitle);
        Assert.IsTrue(back.Viewer.HideToolbar);
        Assert.IsFalse(back.Viewer.HideMenubar);
        Assert.IsTrue(back.Viewer.RightToLeft);
        Assert.AreSame(back.Pages[1], back.Viewer.OpenPage);

        back.Viewer.PageLayout = null;
        back.Viewer.RightToLeft = false;
        back.Viewer.OpenAtFit(back.Pages[2]);
        Assert.IsNull(back.Viewer.PageLayout);
        Assert.IsFalse(back.Viewer.RightToLeft);
        Assert.AreSame(back.Pages[2], back.Viewer.OpenPage);
    }

    [TestMethod]
    public void PageLabelsNumberLikeViewers()
    {
        PdfDocument doc = WithPages(30);
        doc.PageLabels.Add(0, PdfPageLabelStyle.LowerRoman);
        doc.PageLabels.Add(4, PdfPageLabelStyle.Decimal);
        doc.PageLabels.Add(20, PdfPageLabelStyle.UpperLetters, "Annex ");
        doc.PageLabels.Add(28, PdfPageLabelStyle.Decimal, "B-", 7);

        PdfDocument back = Reopen(doc);
        PdfPageLabels labels = back.PageLabels;
        Assert.HasCount(4, labels.Ranges);
        Assert.AreEqual("i", labels.GetLabel(0));
        Assert.AreEqual("iv", labels.GetLabel(3));
        Assert.AreEqual("1", labels.GetLabel(4));
        Assert.AreEqual("16", labels.GetLabel(19));
        Assert.AreEqual("Annex A", labels.GetLabel(20));
        Assert.AreEqual("Annex H", labels.GetLabel(27));
        Assert.AreEqual("B-8", labels.GetLabel(29));

        labels.Clear();
        Assert.AreEqual("5", labels.GetLabel(4));
    }

    [TestMethod]
    public void LettersRepeatPastZ()
    {
        PdfDocument doc = WithPages(1);
        doc.PageLabels.Add(0, PdfPageLabelStyle.LowerLetters);
        Assert.AreEqual("z", doc.PageLabels.GetLabel(25));
        Assert.AreEqual("aa", doc.PageLabels.GetLabel(26));
        Assert.AreEqual("bb", doc.PageLabels.GetLabel(27));
    }

    [TestMethod]
    public void AttachmentsRoundTrip()
    {
        PdfDocument doc = WithPages(1);
        byte[] xml = "<invoice><total>1250.00</total></invoice>"u8.ToArray();
        doc.Attachments.Add("invoice.xml", xml, "text/xml", "Structured invoice data");
        doc.Attachments.Add("notes.txt", "hello"u8.ToArray());

        PdfDocument back = Reopen(doc);
        Assert.HasCount(2, back.Attachments);
        PdfAttachment invoice = back.Attachments["invoice.xml"]!;
        CollectionAssert.AreEqual(xml, invoice.GetData());
        Assert.AreEqual("text/xml", invoice.MimeType);
        Assert.AreEqual("Structured invoice data", invoice.Description);
        Assert.AreEqual("invoice.xml", invoice.FileName);

        back.Attachments.Add("notes.txt", "replaced"u8.ToArray());
        Assert.HasCount(2, back.Attachments);
        Assert.IsTrue(back.Attachments.Remove("invoice.xml"));
        Assert.IsFalse(back.Attachments.Remove("invoice.xml"));
        PdfDocument again = Reopen(back);
        Assert.AreEqual("replaced", System.Text.Encoding.ASCII.GetString(again.Attachments.Single().GetData()));
    }

    [TestMethod]
    public void LanguageAndCustomProperties()
    {
        PdfDocument doc = WithPages(1);
        doc.Language = "ar-MA";
        doc.Info["Department"] = "Finance";
        PdfDocument back = Reopen(doc);
        Assert.AreEqual("ar-MA", back.Language);
        Assert.AreEqual("Finance", back.Info["Department"]);
        back.Language = null;
        Assert.IsNull(back.Language);
    }

    [TestMethod]
    public void NestedAndHostileTreesAreRead()
    {
        PdfDocument doc = WithPages(1);
        PdfReference spec = doc.Attachments.Add("a.txt", "a"u8.ToArray()).Dictionary is { } d ? doc.AddObject(d) : null!;

        // A name tree whose kid lists itself: read once, without looping.
        var kid = new PdfDictionary { ["Names"] = PdfArray.Of(PdfString.FromText("b.txt"), spec) };
        PdfReference kidRef = doc.AddObject(kid);
        kid.Set("Kids", PdfArray.Of(kidRef, kidRef));
        doc.Catalog.Set("Names", new PdfDictionary { ["EmbeddedFiles"] = new PdfDictionary { ["Kids"] = PdfArray.Of(kidRef) } });
        Assert.AreEqual("b.txt", doc.Attachments.Single().Name);
    }
}
