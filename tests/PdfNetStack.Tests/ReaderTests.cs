using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class ReaderTests
{
    private const long Limit = 64 << 20;

    private static string? PageText(PdfFile file, int pageNumber)
    {
        var page = (PdfDictionary)file.GetObject(pageNumber);
        var content = (PdfStream)file.Resolve(page["Contents"])!;
        return Encoding.ASCII.GetString(content.Data.Span);
    }

    [TestMethod]
    public void ReadsClassicXref()
    {
        PdfFile file = PdfFile.Open(PdfBuilder.Simple("Hello").Build(), Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(1, file.RevisionCount);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReadsXrefStreamsAndObjectStreams(bool predictor)
    {
        PdfBuilder b = PdfBuilder.Simple("A", "B");
        byte[] pdf = b.BuildWithXrefStream(new HashSet<int> { 1, 2, 3, 5, 7 }, predictor);
        PdfFile file = PdfFile.Open(pdf, Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.IsTrue(file.UsesXrefStreams);
        Assert.IsTrue(file.UsesObjectStreams);
        Assert.AreEqual("Catalog", file.ResolveDictionary(file.Trailer["Root"])!.GetName("Type"));
        StringAssert.Contains(PageText(file, 7), "(B)");
    }

    [TestMethod]
    public void IncrementalUpdateNewestRevisionWins()
    {
        PdfBuilder b = PdfBuilder.Simple("Old");
        byte[] v1 = b.Build();
        string newContent = "<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream";
        byte[] v2 = PdfBuilder.AppendUpdate(v1, new Dictionary<int, string> { [4] = newContent }, size: 6);

        PdfFile file = PdfFile.Open(v2, Limit);
        Assert.AreEqual(2, file.RevisionCount);
        StringAssert.Contains(PageText(file, 5), "(New)");
    }

    [TestMethod]
    public void PrevLoopTerminates()
    {
        byte[] v1 = PdfBuilder.Simple("x").Build();
        string text = Compat.Latin1.GetString(v1);
        int startxref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        string looped = text.Replace("/Root 1 0 R>>", $"/Root 1 0 R/Prev {startxref}>>");
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(looped), Limit);
        Assert.IsNotNull(file.ResolveDictionary(file.Trailer["Root"]));
    }

    [TestMethod]
    public void BrokenOffsetsTriggerReconstruction()
    {
        byte[] pdf = PdfBuilder.Simple("Hello").Build();
        string text = Compat.Latin1.GetString(pdf);

        // Shift every object by inserting a comment, so every xref offset is now wrong.
        int firstObject = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        string broken = text[..firstObject] + "% padding that moves every object\n" + text[firstObject..];

        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(broken), Limit);
        Assert.IsTrue(file.WasReconstructed);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    public void MissingStartxrefTriggersReconstruction()
    {
        byte[] pdf = PdfBuilder.Simple("Hello").Build();
        string text = Compat.Latin1.GetString(pdf);
        string truncated = text[..text.LastIndexOf("startxref", StringComparison.Ordinal)];
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(truncated), Limit);
        Assert.IsTrue(file.WasReconstructed);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    public void WrongStreamLengthIsRecoveredFromEndstream()
    {
        var b = PdfBuilder.Simple("Hello");
        byte[] pdf = b.Build();
        string text = new System.Text.RegularExpressions.Regex(@"/Length \d+>>").Replace(Compat.Latin1.GetString(pdf), "/Length 99999999>>", 1);
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(text), Limit);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
        Assert.AreEqual(1, file.LengthsRepaired);
    }

    [TestMethod]
    public void MissingObjectResolvesToNull()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Metadata 999 0 R>>");
        PdfFile file = PdfFile.Open(b.Build(), Limit);
        Assert.IsInstanceOfType<PdfNull>(file.Resolve(file.ResolveDictionary(file.Trailer["Root"])!["Metadata"]));
    }

    [TestMethod]
    public void GarbageIsRejected()
    {
        Assert.ThrowsExactly<PdfFormatException>(() => PdfFile.Open("hello world"u8.ToArray(), Limit));
        Assert.ThrowsExactly<PdfFormatException>(() => PdfFile.Open("%PDF-1.4\nnothing here"u8.ToArray(), Limit));
    }
}
