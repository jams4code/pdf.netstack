using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class FormTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static string Content(PdfDocument doc, int page = 0)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfStream XObject(PdfDocument doc, string name, int page = 0)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        return (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(resources["XObject"])!)[name])!;
    }

    private static double[] Numbers(PdfObject? o) => [.. ((PdfArray)o!).Select(v => ((PdfNumber)v).ToDouble())];

    [TestMethod]
    public void AFormIsDrawnOnceAndPlacedMany()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfForm stamp = PdfForm.Create(doc, 120, 40);
        using (PdfGraphics f = stamp.CreateGraphics())
        {
            Assert.AreSame(stamp, f.Form);
            Assert.IsNull(f.Page);
            f.DrawRoundedRectangle(new PdfPen(PdfColor.Rgb(0.8, 0, 0), 2), null, 2, 2, 116, 36, 6);
            f.DrawString("APPROVED", PdfStandardFont.HelveticaBold, 18, PdfColor.Rgb(0.8, 0, 0), 14, 27);
        }

        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawForm(stamp, 50, 50);
            g.DrawForm(stamp, 300, 50, 240, 80);
        }

        PdfDocument back = Reopen(doc);
        string content = Content(back);
        StringAssert.Contains(content, "1 0 0 -1 50 90 cm\n/Fm1 Do");
        StringAssert.Contains(content, "2 0 0 -2 300 130 cm\n/Fm1 Do");

        PdfStream form = XObject(back, "Fm1");
        Assert.AreEqual("Form", ((PdfName)form.Dictionary["Subtype"]!).Value);
        CollectionAssert.AreEqual(new double[] { 0, 0, 120, 40 }, Numbers(form.Dictionary["BBox"]));
        string formContent = Encoding.ASCII.GetString(back.GetStreamData(form));
        StringAssert.StartsWith(formContent, "q\n1 0 0 -1 0 40 cm\n");
        StringAssert.Contains(formContent, "(APPROVED)Tj");
        var resources = (PdfDictionary)back.Resolve(form.Dictionary["Resources"])!;
        Assert.IsNotNull(resources["Font"]);
    }

    [TestMethod]
    public void FormsStayInTheirDocument()
    {
        PdfForm foreign = PdfForm.Create(PdfDocument.Create(), 10, 10);
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        Assert.ThrowsExactly<ArgumentException>(() => g.DrawForm(foreign, 0, 0));
        PdfForm own = PdfForm.Create(doc, 10, 10);
        using PdfGraphics f = own.CreateGraphics();
        Assert.ThrowsExactly<ArgumentException>(() => f.DrawForm(own, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfForm.Create(doc, 0, 10));
    }

    [TestMethod]
    public void PagesFromAnotherDocumentBecomeForms()
    {
        PdfDocument source = PdfDocument.Create();
        PdfPage page = source.Pages.Add(new PdfRectangle(0, 0, 300, 200));
        using (PdfGraphics g = page.CreateGraphics()) g.DrawString("Original", PdfStandardFont.TimesBold, 24, PdfColor.Black, 20, 50);
        page.Rotate(90);
        source = Reopen(source);

        PdfDocument target = PdfDocument.Create();
        PdfForm form = PdfForm.FromPage(target, source.Pages[0]);
        Assert.AreEqual(200, form.Width);
        Assert.AreEqual(300, form.Height);
        using (PdfGraphics g = target.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawForm(form, 50, 50, 100, 150);

        PdfDocument back = Reopen(target);
        PdfStream x = XObject(back, "Fm1");
        CollectionAssert.AreEqual(new double[] { 0, -1, 1, 0, 0, 300 }, Numbers(x.Dictionary["Matrix"]));
        StringAssert.Contains(Encoding.ASCII.GetString(back.GetStreamData(x)), "(Original)Tj");
        var fonts = (PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(x.Dictionary["Resources"])!)["Font"])!;
        Assert.AreEqual("Times-Bold", ((PdfName)((PdfDictionary)back.Resolve(fonts.First().Value)!)["BaseFont"]!).Value);
    }

    [TestMethod]
    public void FourPagesPerSheet()
    {
        PdfDocument source = PdfDocument.Create();
        for (int i = 1; i <= 4; i++)
        {
            using PdfGraphics g = source.Pages.Add(PdfRectangle.A4).CreateGraphics();
            g.DrawString("Page " + i, PdfStandardFont.Helvetica, 48, PdfColor.Black, 100, 400);
        }

        PdfDocument sheet = PdfDocument.Create();
        using (PdfGraphics g = sheet.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            for (int i = 0; i < 4; i++)
            {
                PdfForm form = PdfForm.FromPage(sheet, source.Pages[i]);
                g.DrawForm(form, i % 2 * g.Width / 2, i / 2 * g.Height / 2, g.Width / 2, g.Height / 2);
            }
        }

        PdfDocument back = Reopen(sheet);
        string content = Content(back);
        for (int i = 1; i <= 4; i++) StringAssert.Contains(content, $"/Fm{i} Do");

        // The four pages share one Helvetica, imported once from the source document.
        var fonts = new HashSet<int>();
        for (int i = 1; i <= 4; i++)
        {
            PdfStream x = XObject(back, "Fm" + i);
            var f = (PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(x.Dictionary["Resources"])!)["Font"])!;
            fonts.Add(((PdfReference)f.First().Value).Number);
        }

        Assert.HasCount(1, fonts);
    }
}
