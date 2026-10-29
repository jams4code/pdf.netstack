using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>Opening from a stream without loading the file, and incremental saving.</summary>
[TestClass]
public sealed class StreamingTests
{
    /// <summary>A stream that counts the bytes read through it.</summary>
    private sealed class CountingStream(byte[] data) : MemoryStream(data.Length)
    {
        private readonly byte[] _seed = data;

        public long BytesRead { get; private set; }

        public CountingStream Prepared()
        {
            Write(_seed, 0, _seed.Length);
            Position = 0;
            BytesRead = 0;
            return this;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
    }

    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    /// <summary>Pages of text, each also painting a large image that does not compress.</summary>
    private static byte[] HeavyDocument(int pages, int imageBytes, bool objectStreams = true)
    {
        var b = new PdfBuilder();
        var kids = new StringBuilder();
        var packed = new HashSet<int> { 1, 2 };
        int next = 3;
        var random = new Random(7);
        for (int i = 0; i < pages; i++)
        {
            int page = next++, content = next++, font = next++, image = next++;
            kids.Append(page).Append(" 0 R ");
            packed.Add(page);
            packed.Add(font);
            int side = (int)Math.Sqrt(imageBytes);
            var pixels = new byte[side * side];
            random.NextBytes(pixels);
            b.Set(page, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents {content} 0 R/Resources<</Font<</F1 {font} 0 R>>/XObject<</Im{i} {image} 0 R>>>>>>");
            b.SetStream(content, "", Encoding.ASCII.GetBytes($"q 100 0 0 100 0 0 cm /Im{i} Do Q BT /F1 12 Tf 20 200 Td (Page {i + 1}) Tj ET"));
            b.Set(font, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
            b.SetStream(image, $"/Type/XObject/Subtype/Image/Width {side}/Height {side}/ColorSpace/DeviceGray/BitsPerComponent 8", pixels);
        }

        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count {pages}>>");
        return objectStreams ? b.BuildWithXrefStream(packed) : b.Build();
    }

    [TestMethod]
    public void OpeningOnDemandReadsOnlyWhatIsUsed()
    {
        byte[] pdf = HeavyDocument(pages: 6, imageBytes: 400_000);
        using CountingStream stream = new CountingStream(pdf).Prepared();

        PdfDocument doc = PdfDocument.OpenOnDemand(stream);
        long afterOpen = stream.BytesRead;
        Assert.AreEqual(6, doc.Pages.Count);
        Assert.IsLessThan(pdf.Length / 20, afterOpen, "Opening reads the cross-reference data and the page tree only.");

        StringAssert.Contains(doc.Pages[3].ExtractText(), "Page 4");
        long afterText = stream.BytesRead;
        Assert.IsLessThan(pdf.Length / 10, afterText, "Text needs the content stream and the font, not the images.");

        var resources = (PdfDictionary)doc.Resolve(doc.Pages[3].Dictionary["Resources"])!;
        var image = (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(resources["XObject"])!)["Im3"])!;
        Assert.HasCount(632 * 632, doc.GetStreamData(image));
        Assert.IsGreaterThanOrEqualTo(afterText + (632 * 632), stream.BytesRead, "Image data is read when asked for.");
    }

    [TestMethod]
    public void OnDemandDocumentsReadLikeDocumentsInMemory()
    {
        PdfDocument created = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
        {
            PdfPage page = created.Pages.Add(PdfRectangle.A4);
            using Drawing.PdfGraphics g = page.CreateGraphics();
            g.DrawString("Streaming page " + (i + 1), Fonts.PdfStandardFont.Helvetica, 14, Drawing.PdfColor.Rgb(0, 0, 0), 40, 60);
        }

        created.Info.Title = "Streamed";
        foreach (byte[] pdf in new[] { created.ToArray(), created.ToArray(new PdfSaveOptions { UseObjectStreams = false }) })
        {
            PdfDocument inMemory = PdfDocument.Open(pdf);
            using var stream = new MemoryStream(pdf, writable: false);
            PdfDocument onDemand = PdfDocument.OpenOnDemand(stream);
            Assert.AreEqual(inMemory.Pages.Count, onDemand.Pages.Count);
            Assert.AreEqual(inMemory.Info.Title, onDemand.Info.Title);
            for (int i = 0; i < inMemory.Pages.Count; i++) Assert.AreEqual(inMemory.Pages[i].ExtractText(), onDemand.Pages[i].ExtractText());

            // A full save of a document read on demand is the same document.
            string? difference = PdfEquivalence.FindDifference(inMemory.ToArray(Plain), onDemand.ToArray(Plain));
            Assert.IsNull(difference, difference);
        }
    }

    [TestMethod]
    public void IncrementalSaveAppendsOnlyWhatChanged()
    {
        byte[] original = HeavyDocument(pages: 3, imageBytes: 50_000);
        PdfDocument doc = PdfDocument.Open(original);
        doc.Info.Title = "Updated";
        using var output = new MemoryStream();
        doc.SaveIncremental(output);
        byte[] updated = output.ToArray();

        CollectionAssert.AreEqual(original, updated.Take(original.Length).ToArray(), "The original bytes are kept as they were.");
        Assert.IsLessThan(2048, updated.Length - original.Length, "Only the information dictionary, the catalog's trailer and a cross-reference section are new.");

        PdfFile file = PdfFile.Open(updated, 64 << 20);
        Assert.AreEqual(2, file.RevisionCount);
        Assert.IsTrue(file.UsesXrefStreams, "An update to a file with cross-reference streams uses one too.");
        Assert.IsFalse(file.WasReconstructed);
        PdfDocument reopened = PdfDocument.Open(updated);
        Assert.AreEqual("Updated", reopened.Info.Title);
        Assert.AreEqual(3, reopened.Pages.Count);
        StringAssert.Contains(reopened.Pages[2].ExtractText(), "Page 3");
    }

    [TestMethod]
    public void IncrementalSaveRewritesThePageTreeWhenPagesChange()
    {
        byte[] original = HeavyDocument(pages: 3, imageBytes: 10_000, objectStreams: false);
        PdfDocument doc = PdfDocument.Open(original);
        doc.Pages.Remove(doc.Pages[0]);
        PdfPage added = doc.Pages.Add(PdfRectangle.A4);
        using (Drawing.PdfGraphics g = added.CreateGraphics())
        {
            g.DrawString("Added later", Fonts.PdfStandardFont.Helvetica, 12, Drawing.PdfColor.Rgb(0, 0, 0), 40, 60);
        }

        using var output = new MemoryStream();
        doc.SaveIncremental(output);
        byte[] updated = output.ToArray();

        PdfFile file = PdfFile.Open(updated, 64 << 20);
        Assert.IsFalse(file.UsesXrefStreams, "An update to a file with a cross-reference table uses a table.");
        PdfDocument reopened = PdfDocument.Open(updated);
        CollectionAssert.AreEqual(new[] { "Page 2", "Page 3", "Added later" }, reopened.Pages.Select(p => p.ExtractText().Trim()).ToArray());
    }

    [TestMethod]
    public void UnchangedDocumentSavesAsTheOriginal()
    {
        byte[] original = HeavyDocument(pages: 2, imageBytes: 10_000);
        PdfDocument doc = PdfDocument.Open(original);
        _ = doc.Pages[1].ExtractText();
        using var output = new MemoryStream();
        doc.SaveIncremental(output);
        CollectionAssert.AreEqual(original, output.ToArray());
    }

    [TestMethod]
    public void UpdatesAppendInPlaceToTheStreamTheDocumentCameFrom()
    {
        byte[] original = HeavyDocument(pages: 4, imageBytes: 100_000);
        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, original);
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                PdfDocument doc = PdfDocument.OpenOnDemand(stream);
                doc.Info.Title = "First";
                doc.SaveIncremental(stream);
                Assert.IsLessThan(original.Length + 2048, stream.Length);

                // A second update builds on the first.
                doc.Info.Author = "Second";
                doc.Pages[1].Dictionary.Set("Rotate", PdfNumber.FromInteger(90));
                doc.SaveIncremental(stream);
            }

            byte[] updated = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(original, updated.Take(original.Length).ToArray());
            Assert.AreEqual(3, PdfFile.Open(updated, 64 << 20).RevisionCount);
            PdfDocument reopened = PdfDocument.Open(updated);
            Assert.AreEqual("First", reopened.Info.Title);
            Assert.AreEqual("Second", reopened.Info.Author);
            Assert.AreEqual(90, reopened.Pages[1].Rotation);
            Assert.AreEqual(0, reopened.Pages[0].Rotation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void IncrementalSaveKeepsExistingSignaturesValid()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Incremental Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] signed = PdfSigner.Sign(PdfBuilder.Simple("signed").Build(), certificate);

        PdfDocument doc = PdfDocument.Open(signed);
        doc.Info.Subject = "Annotated after signing";
        using var output = new MemoryStream();
        doc.SaveIncremental(output);

        PdfSignatureInfo signature = PdfSigner.Verify(output.ToArray()).Single();
        Assert.IsTrue(signature.DigestMatches);
        Assert.IsTrue(signature.SignatureValid);
        Assert.IsFalse(signature.CoversWholeDocument, "The update comes after the signed bytes.");
        Assert.AreEqual("Annotated after signing", PdfDocument.Open(output.ToArray()).Info.Subject);
    }

    [TestMethod]
    public void IncrementalSaveRefusesWhatItCannotUpdate()
    {
        PdfDocument created = PdfDocument.Create();
        created.Pages.Add(PdfRectangle.A4);
        Assert.ThrowsExactly<InvalidOperationException>(() => created.SaveIncremental(new MemoryStream()));

        PdfDocument encrypted = PdfDocument.Open(PdfBuilder.Simple("x").Build());
        encrypted.Security.Encrypt("user", "owner");
        Assert.ThrowsExactly<NotSupportedException>(() => encrypted.SaveIncremental(new MemoryStream()));

        byte[] damaged = PdfBuilder.Simple("x").Build();
        int startxref = Compat.Latin1.GetString(damaged).LastIndexOf("startxref", StringComparison.Ordinal);
        PdfDocument repaired = PdfDocument.Open(damaged.Take(startxref).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => repaired.SaveIncremental(new MemoryStream()));
    }

    [TestMethod]
    public void HostileInputOnAStreamFailsOnlyWithFormatErrors()
    {
        byte[] seed = HeavyDocument(pages: 2, imageBytes: 2_000);
        var random = new Random(11);
        for (int round = 0; round < 150; round++)
        {
            byte[] mutated = (byte[])seed.Clone();
            switch (round % 3)
            {
                case 0:
                    for (int k = 0; k < 6; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    mutated = mutated.Take(random.Next(1, mutated.Length)).ToArray();
                    break;
                default:
                    int at = random.Next(mutated.Length);
                    mutated = [.. mutated.Take(at), .. "endstream endobj 9 0 obj <<"u8.ToArray(), .. mutated.Skip(at)];
                    break;
            }

            try
            {
                using var stream = new MemoryStream(mutated, writable: false);
                PdfDocument doc = PdfDocument.OpenOnDemand(stream);
                foreach (PdfPage page in doc.Pages) _ = page.ExtractText();
                doc.SaveIncremental(new MemoryStream());
            }
            catch (PdfFormatException)
            {
            }
            catch (NotSupportedException)
            {
                // A damaged file that had to be rebuilt is not updated incrementally.
            }
        }
    }

    [TestMethod]
    public void StreamSourceSearchesAcrossChunks()
    {
        byte[] data = new byte[(3 << 20) + 17];
        "needle"u8.CopyTo(data.AsSpan((1 << 20) - 3));
        "needle"u8.CopyTo(data.AsSpan((2 << 20) + 5));
        using var stream = new MemoryStream(data, writable: false);
        var source = new StreamSource(stream);
        var memory = new MemorySource(data);
        foreach (PdfSource s in new PdfSource[] { source, memory })
        {
            Assert.AreEqual((1L << 20) - 3, s.IndexOf("needle"u8, 0, s.Length));
            Assert.AreEqual((2L << 20) + 5, s.IndexOf("needle"u8, 1L << 20, s.Length));
            Assert.AreEqual((2L << 20) + 5, s.LastIndexOf("needle"u8, s.Length));
            Assert.AreEqual((1L << 20) - 3, s.LastIndexOf("needle"u8, (2L << 20) + 10));
            Assert.AreEqual(-1, s.IndexOf("needle"u8, (2L << 20) + 6, s.Length));
        }

    }
}
