using System.Runtime.InteropServices;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Opening documents: one read of the input, files and streams on demand, spooling, async reads, disposal and limits.</summary>
[TestClass]
public sealed class LoadOptionsTests
{
    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    /// <summary>A stream that reads like a network response: forward only, no length.</summary>
    private sealed class ForwardOnlyStream(byte[] data, int chunk = 7_000) : Stream
    {
        private int _position;

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            int n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that forbids synchronous reads, as ASP.NET Core request bodies do by default.</summary>
    private sealed class AsyncOnlyStream(byte[] data, bool canSeek, Action<int>? onRead = null) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => canSeek;

        public override bool CanWrite => false;

        public override long Length => canSeek ? _inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => canSeek ? _inner.Position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous reads are not allowed.");

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            onRead?.Invoke(++_reads);
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.Read(buffer, offset, Math.Min(count, 5_000));
        }

#if NET8_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            onRead?.Invoke(++_reads);
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.Read(buffer.Span[..Math.Min(buffer.Length, 5_000)]);
        }
#endif

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A folder of its own for temporary copies, so a test sees exactly the files it made.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfnetstack-test-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public string[] Files => Directory.GetFiles(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>A few pages of text, each with an incompressible image, about <paramref name="imageBytes"/> each.</summary>
    private static byte[] Document(int pages, int imageBytes)
    {
        var b = new PdfBuilder();
        var kids = new StringBuilder();
        var random = new Random(3);
        int next = 3;
        for (int i = 0; i < pages; i++)
        {
            int page = next++, content = next++, font = next++, image = next++;
            kids.Append(page).Append(" 0 R ");
            int side = (int)Math.Sqrt(imageBytes);
            var pixels = new byte[side * side];
            random.NextBytes(pixels);
            b.Set(page, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents {content} 0 R/Resources<</Font<</F1 {font} 0 R>>/XObject<</Im {image} 0 R>>>>>>");
            b.SetStream(content, "", Encoding.ASCII.GetBytes($"q 100 0 0 100 0 0 cm /Im Do Q BT /F1 12 Tf 20 200 Td (Page {i + 1}) Tj ET"));
            b.Set(font, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
            b.SetStream(image, $"/Type/XObject/Subtype/Image/Width {side}/Height {side}/ColorSpace/DeviceGray/BitsPerComponent 8", pixels);
        }

        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count {pages}>>");
        return b.Build();
    }

    private static string[] Texts(PdfDocument doc) => [.. doc.Pages.Select(p => p.ExtractText().Trim())];

    private static string TempPdf(byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, data);
        return path;
    }

    [TestMethod]
    public void NullPasswordStillCompilesAsInVersionOne()
    {
        // 1.0 callers wrote Open(x, null); the PdfLoadOptions overloads must not make it ambiguous.
        byte[] pdf = Document(1, 1_000);
        string path = TempPdf(pdf);
        try
        {
            Assert.AreEqual(1, PdfDocument.Open(path, null).Pages.Count);
            Assert.AreEqual(1, PdfDocument.Open(new MemoryStream(pdf), null).Pages.Count);
            Assert.AreEqual(1, PdfDocument.Open(pdf.AsMemory(), null).Pages.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EveryWayOfOpeningReadsTheSameDocument()
    {
        byte[] pdf = Document(3, 20_000);
        string[] expected = ["Page 1", "Page 2", "Page 3"];
        var exposed = new MemoryStream();
        exposed.Write(pdf, 0, pdf.Length);
        exposed.Position = 0;

        PdfDocument[] docs =
        [
            PdfDocument.Open(pdf),
            PdfDocument.Open(new MemoryStream(pdf, writable: false)),
            PdfDocument.Open(exposed),
            PdfDocument.Open(new ForwardOnlyStream(pdf)),
            PdfDocument.Open(new ForwardOnlyStream(pdf), new PdfLoadOptions { Mode = PdfLoadMode.OnDemand }),
            PdfDocument.Open(new MemoryStream(pdf, writable: false), new PdfLoadOptions { Mode = PdfLoadMode.OnDemand }),
            PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { Mode = PdfLoadMode.OnDemand }),
            PdfDocument.OpenOnDemand(new ForwardOnlyStream(pdf)),
        ];
        foreach (PdfDocument doc in docs)
        {
            using (doc)
            {
                CollectionAssert.AreEqual(expected, Texts(doc));
            }
        }

        Assert.AreEqual(pdf.Length, exposed.Position, "A memory stream reads as read to its end.");
    }

    [TestMethod]
    public void ReadsStartFromTheStreamPosition()
    {
        byte[] pdf = Document(1, 1_000);
        byte[] prefixed = [.. "junk before"u8.ToArray(), .. pdf];
        foreach (PdfLoadMode mode in new[] { PdfLoadMode.InMemory, PdfLoadMode.OnDemand })
        {
            var seekable = new MemoryStream(prefixed, writable: false) { Position = 11 };
            using PdfDocument fromSeekable = PdfDocument.Open(seekable, new PdfLoadOptions { Mode = mode });
            Assert.AreEqual("Page 1", Texts(fromSeekable)[0]);

            var exposed = new MemoryStream();
            exposed.Write(prefixed, 0, prefixed.Length);
            exposed.Position = 11;
            using PdfDocument fromExposed = PdfDocument.Open(exposed, new PdfLoadOptions { Mode = mode });
            Assert.AreEqual("Page 1", Texts(fromExposed)[0]);
        }
    }

#if NET8_0_OR_GREATER
    [TestMethod]
    public void AStreamIsReadIntoMemoryWithoutExtraCopies()
    {
        byte[] pdf = Document(1, 4_000_000);
        long Allocated(Func<PdfDocument> open)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using PdfDocument doc = open();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm up the parser so the measurements hold only the reading.
        _ = Allocated(() => PdfDocument.Open(pdf));
        long parse = Allocated(() => PdfDocument.Open(pdf));
        var exposed = new MemoryStream();
        exposed.Write(pdf, 0, pdf.Length);
        long fromExposed = Allocated(() => { exposed.Position = 0; return PdfDocument.Open(exposed); });
        long fromSeekable = Allocated(() => PdfDocument.Open(new MemoryStream(pdf, writable: false)));
        long fromForwardOnly = Allocated(() => PdfDocument.Open(new ForwardOnlyStream(pdf)));

        Assert.IsLessThan(parse + (pdf.Length / 4), fromExposed, $"An exposed buffer is used as is: parse {parse:N0}, from it {fromExposed:N0}");
        Assert.IsLessThan(parse + (pdf.Length * 5 / 4), fromSeekable, $"A seekable stream is copied once: parse {parse:N0}, from it {fromSeekable:N0}");
        Assert.IsLessThan(parse + (pdf.Length * 5 / 2), fromForwardOnly, $"A forward only stream is read in pieces, then joined once: parse {parse:N0}, from it {fromForwardOnly:N0}");
    }
#endif

    [TestMethod]
    public void AFileOpenedOnDemandStaysOpenUntilDisposed()
    {
        byte[] pdf = Document(4, 100_000);
        string path = TempPdf(pdf);
        try
        {
            PdfDocument doc = PdfDocument.Open(path, new PdfLoadOptions { Mode = PdfLoadMode.OnDemand });
            Assert.AreEqual("Page 3", doc.Pages[2].ExtractText().Trim());

            // Shared for reading while open, and released on Dispose.
            using (File.OpenRead(path))
            {
            }

            if (Windows) Assert.ThrowsExactly<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
            doc.Dispose();
            doc.Dispose();
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => doc.Pages[1].ExtractText());
            Assert.ThrowsExactly<ObjectDisposedException>(() => doc.ToArray());
            Assert.ThrowsExactly<ObjectDisposedException>(() => doc.Resolve(doc.Catalog["Pages"]));
            Assert.ThrowsExactly<ObjectDisposedException>(() => doc.SaveIncremental(new MemoryStream()));
            Assert.ThrowsExactly<ObjectDisposedException>(() => doc.ReleaseUnusedObjects());

            // In memory the file is read once and closed at once.
            using PdfDocument inMemory = PdfDocument.Open(path, new PdfLoadOptions());
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
            Assert.AreEqual("Page 4", inMemory.Pages[3].ExtractText().Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void DisposingADocumentInMemoryChangesNothing()
    {
        byte[] pdf = Document(2, 1_000);
        PdfDocument doc = PdfDocument.Open(pdf);
        doc.Dispose();
        Assert.AreEqual("Page 2", doc.Pages[1].ExtractText().Trim());
        Assert.AreEqual(0, doc.ReleaseUnusedObjects(), "A document in memory holds its objects by design.");
        doc.Info.Title = "Still usable";
        Assert.AreEqual("Still usable", PdfDocument.Open(doc.ToArray()).Info.Title);

        PdfDocument created = PdfDocument.Create();
        created.Pages.Add(PdfRectangle.A4);
        created.Dispose();
        Assert.AreEqual(1, PdfDocument.Open(created.ToArray()).Pages.Count);

        PdfDocument fromMemory = PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { Mode = PdfLoadMode.OnDemand });
        fromMemory.Dispose();
        Assert.AreEqual("Page 1", fromMemory.Pages[0].ExtractText().Trim(), "Memory the caller owns needs no release.");
    }

    [TestMethod]
    public void DisposingNeverClosesTheCallersStream()
    {
        byte[] pdf = Document(2, 10_000);
        var stream = new MemoryStream(pdf, writable: false);
        PdfDocument doc = PdfDocument.OpenOnDemand(stream);
        Assert.AreEqual("Page 1", doc.Pages[0].ExtractText().Trim());
        doc.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => doc.Pages[1].ExtractText());
        Assert.IsTrue(stream.CanRead, "The caller's stream stays open.");
        stream.Position = 0;
        using PdfDocument again = PdfDocument.OpenOnDemand(stream);
        Assert.AreEqual("Page 2", again.Pages[1].ExtractText().Trim());
    }

    [TestMethod]
    public void AStreamThatCannotSeekIsSpooledToMemoryUpToTheThreshold()
    {
        byte[] pdf = Document(3, 30_000);
        using var temp = new TempFolder();
        var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = temp.Path, SpoolThreshold = pdf.Length };
        var stream = new ForwardOnlyStream(pdf);
        PdfDocument doc = PdfDocument.Open(stream, options);
        int reads = stream.Reads;
        Assert.IsEmpty(temp.Files, "The copy fits the threshold.");
        CollectionAssert.AreEqual(new[] { "Page 1", "Page 2", "Page 3" }, Texts(doc));
        Assert.AreEqual(reads, stream.Reads, "The stream is read once, when opening.");
        doc.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => doc.Pages[0].ExtractText());
    }

    [TestMethod]
    public void AStreamThatCannotSeekIsSpooledToATemporaryFilePastTheThreshold()
    {
        byte[] pdf = Document(3, 30_000);
        using var temp = new TempFolder();
        var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = temp.Path, SpoolThreshold = pdf.Length - 1 };
        PdfDocument doc = PdfDocument.Open(new ForwardOnlyStream(pdf), options);
        if (Windows)
        {
            // Windows deletes it when its last handle closes; elsewhere it has no name from the start.
            Assert.HasCount(1, temp.Files);
            StringAssert.StartsWith(Path.GetFileName(temp.Files[0]), "pdfnetstack-");
        }

        CollectionAssert.AreEqual(new[] { "Page 1", "Page 2", "Page 3" }, Texts(doc));

        // An update to a private copy goes to the output as a whole file: the copy is never written.
        doc.Info.Title = "Spooled";
        using var output = new MemoryStream();
        doc.SaveIncremental(output);
        Assert.AreEqual("Spooled", PdfDocument.Open(output.ToArray()).Info.Title);

        doc.Dispose();
        Assert.IsEmpty(temp.Files, "Disposing deletes the copy.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => doc.Pages[0].ExtractText());

        // Threshold 0 always uses a file.
        using (PdfDocument tiny = PdfDocument.Open(new ForwardOnlyStream(Document(1, 100)), options with { SpoolThreshold = 0 }))
        {
            Assert.AreEqual("Page 1", tiny.Pages[0].ExtractText().Trim());
        }

        Assert.IsEmpty(temp.Files);
    }

    [TestMethod]
    public void HostileInputThroughTheSpoolFailsOnlyWithFormatErrorsAndLeavesNoFile()
    {
        byte[] seed = Document(2, 2_000);
        using var temp = new TempFolder();
        var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = temp.Path, SpoolThreshold = 0 };
        var random = new Random(23);
        for (int round = 0; round < 120; round++)
        {
            byte[] mutated = (byte[])seed.Clone();
            switch (round % 4)
            {
                case 0:
                    for (int k = 0; k < 6; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    mutated = [.. mutated.Take(random.Next(0, mutated.Length))];
                    break;
                case 2:
                    int at = random.Next(mutated.Length);
                    mutated = [.. mutated.Take(at), .. "endstream endobj 9 0 obj <<"u8.ToArray(), .. mutated.Skip(at)];
                    break;
                default:
                    mutated = [.. mutated.Take(random.Next(0, 40))];
                    break;
            }

            try
            {
                using PdfDocument doc = PdfDocument.Open(new ForwardOnlyStream(mutated), options);
                foreach (PdfPage page in doc.Pages) _ = page.ExtractText();
            }
            catch (PdfFormatException)
            {
            }

            Assert.IsEmpty(temp.Files, $"round {round}: the copy is deleted whether opening worked or not");
        }
    }

    [TestMethod]
    public async Task OpenAsyncNeverReadsSynchronously()
    {
        byte[] pdf = Document(3, 30_000);
        using var temp = new TempFolder();
        foreach (bool canSeek in new[] { true, false })
        {
            using (PdfDocument doc = await PdfDocument.OpenAsync(new AsyncOnlyStream(pdf, canSeek)))
            {
                CollectionAssert.AreEqual(new[] { "Page 1", "Page 2", "Page 3" }, Texts(doc));
            }

            foreach (long threshold in new long[] { 0, 1 << 20 })
            {
                var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = temp.Path, SpoolThreshold = threshold };
                using (PdfDocument doc = await PdfDocument.OpenAsync(new AsyncOnlyStream(pdf, canSeek), options))
                {
                    CollectionAssert.AreEqual(new[] { "Page 1", "Page 2", "Page 3" }, Texts(doc));
                }

                Assert.IsEmpty(temp.Files);
            }
        }

        // A password and limits come through the options.
        PdfDocument plain = PdfDocument.Open(pdf);
        plain.Security.Encrypt("user", "owner");
        byte[] encrypted = plain.ToArray();
        using PdfDocument unlocked = await PdfDocument.OpenAsync(new AsyncOnlyStream(encrypted, canSeek: false), new PdfLoadOptions { Password = "user" });
        Assert.AreEqual("Page 2", unlocked.Pages[1].ExtractText().Trim());
        await Assert.ThrowsExactlyAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(new AsyncOnlyStream(encrypted, canSeek: false), new PdfLoadOptions { Password = "wrong" }));
    }

    [TestMethod]
    public async Task CancellingOpenAsyncStopsReadingAndLeavesNoFile()
    {
        byte[] pdf = Document(4, 60_000);
        using var temp = new TempFolder();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfDocument.OpenAsync(new AsyncOnlyStream(pdf, canSeek: false), null, cancelled.Token));
        }

        foreach (PdfLoadMode mode in new[] { PdfLoadMode.InMemory, PdfLoadMode.OnDemand })
        {
            using var source = new CancellationTokenSource();
            int reads = 0;
            var stream = new AsyncOnlyStream(pdf, canSeek: false, onRead: n =>
            {
                reads = n;
                if (n == 5) source.Cancel();
            });
            var options = new PdfLoadOptions { Mode = mode, TempDirectory = temp.Path, SpoolThreshold = 0 };
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfDocument.OpenAsync(stream, options, source.Token));
            Assert.AreEqual(5, reads, "Reading stops at the cancellation.");
            Assert.IsEmpty(temp.Files, "The partial copy is deleted.");
        }
    }

    [TestMethod]
    public void OptionsAreCheckedAndThePasswordStaysOutOfLogs()
    {
        byte[] pdf = Document(1, 100);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { MaxImagePixels = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { MaxImagePixels = PdfLoadOptions.DefaultMaxImagePixels + 1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { MaxDecodedStreamBytes = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { SpoolThreshold = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(pdf.AsMemory(), new PdfLoadOptions { Mode = (PdfLoadMode)7 }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfDocument.Open(new MemoryStream(pdf), new PdfLoadOptions { TempDirectory = "" }));
        Assert.ThrowsExactly<ArgumentNullException>(() => PdfDocument.Open(new MemoryStream(pdf), (PdfLoadOptions)null!));

        string text = new PdfLoadOptions { Password = "secret" }.ToString();
        Assert.DoesNotContain("secret", text);
        StringAssert.Contains(text, "Password = (set)");
        Assert.AreEqual(PdfLoadMode.InMemory, PdfLoadOptions.Default.Mode);
        Assert.AreEqual(1L << 28, PdfLoadOptions.Default.MaxImagePixels);
    }

    /// <summary>A one page document drawing the image, read back with the given pixel limit.</summary>
    private static PdfPageImage Placed(PdfDictionary dict, byte[] data, long maxPixels)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = doc.AddObject(new PdfStream(dict, data)) } });
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("q 100 0 0 100 0 0 cm /Im Do Q"u8)));
        return PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Optimize = false }).AsMemory(), new PdfLoadOptions { MaxImagePixels = maxPixels }).Pages[0].GetImages().Single();
    }

    private static PdfDictionary ImageDictionary(int width, int height, string filter, params (string Key, PdfObject Value)[] extra)
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(width),
            ["Height"] = PdfNumber.FromInteger(height),
            ["ColorSpace"] = new PdfName("DeviceGray"),
            ["BitsPerComponent"] = PdfNumber.FromInteger(filter is "CCITTFaxDecode" or "JBIG2Decode" ? 1 : 8),
        };
        if (filter.Length > 0) dict.Set("Filter", new PdfName(filter));
        foreach ((string key, PdfObject value) in extra) dict.Set(key, value);
        return dict;
    }

    /// <summary>
    /// Each encoding decodes with a limit of exactly its pixels and is refused with one fewer, by
    /// the dictionary's size and, where the data says otherwise, by the data's own size.
    /// </summary>
    [TestMethod]
    public void TheImagePixelLimitHoldsForEveryEncodingInADocument()
    {
        var cases = new List<(string Name, int Width, int Height, Func<int, int, PdfDictionary> Dict, byte[] Data)>();

        // Raw samples under Flate with a PNG predictor.
        const int side = 120;
        var samples = new byte[side * (side + 1)];
        for (int y = 0; y < side; y++) samples[y * (side + 1)] = 1;
        var predictor = new PdfDictionary { ["Predictor"] = PdfNumber.FromInteger(15), ["Columns"] = PdfNumber.FromInteger(side) };
        cases.Add(("raw", side, side, (w, h) => ImageDictionary(w, h, "FlateDecode", ("DecodeParms", predictor)), PdfBuilder.Deflate(samples)));

        byte[] jpeg = File.ReadAllBytes(Fixture("Jpeg", "gray-baseline.jpg"));
        JpegHeader jpegHeader = JpegHeader.Read(jpeg);
        cases.Add(("JPEG", jpegHeader.Width, jpegHeader.Height, (w, h) => ImageDictionary(w, h, "DCTDecode"), jpeg));

        byte[] jpx = File.ReadAllBytes(Fixture("Jpx", "gray-53.jp2"));
        Jpeg2000Image jpxImage = Jpeg2000Decoder.Decode(jpx, 64 << 20);
        cases.Add(("JPEG 2000", jpxImage.Width, jpxImage.Height, (w, h) => ImageDictionary(w, h, "JPXDecode"), jpx));

        byte[] jbig2 = File.ReadAllBytes(Fixture("Jbig2", "generic-t0.jb2"));
        PngImage jbig2Page = PngImage.Read(File.ReadAllBytes(Fixture("Jbig2", "generic-t0.png")));
        cases.Add(("JBIG2", jbig2Page.Width, jbig2Page.Height, (w, h) => ImageDictionary(w, h, "JBIG2Decode"), jbig2));

        byte[] fax = File.ReadAllBytes(Fixture("Ccitt", "g4.bin"));
        cases.Add(("CCITT", 301, 97, (w, h) => ImageDictionary(w, h, "CCITTFaxDecode",
            ("DecodeParms", new PdfDictionary { ["K"] = PdfNumber.FromInteger(-1), ["Columns"] = PdfNumber.FromInteger(301), ["Rows"] = PdfNumber.FromInteger(97) })), fax));

        foreach ((string name, int width, int height, Func<int, int, PdfDictionary> dict, byte[] data) in cases)
        {
            long pixels = (long)width * height;
            PdfBitmap? bitmap = Placed(dict(width, height), data, pixels).Decode();
            Assert.IsNotNull(bitmap, name);
            Assert.AreEqual(width, bitmap.Width, name);

            Assert.ThrowsExactly<PdfFormatException>(() => Placed(dict(width, height), data, pixels - 1).Decode(), name + ", by the dictionary");
            if (name != "raw" && name != "JBIG2")
            {
                // The dictionary claims a tiny image; the data is what is decoded, and is refused.
                Assert.ThrowsExactly<PdfFormatException>(() => Placed(dict(2, 2), data, pixels - 1).Decode(), name + ", by the data");
            }
        }
    }

    [TestMethod]
    public void TheImagePixelLimitHoldsForEveryImageFileFormat()
    {
        string[] files =
        [
            Fixture("Jpeg", "ycc420-baseline.jpg"),
            Fixture("Images", "rgba8.png"),
            Fixture("Gif", "photo.gif"),
            Fixture("Bmp", "rgb24.bmp"),
            Fixture("Bmp", "core24.bmp"),
            Fixture("Tiff", "lzw-gray.tif"),
            Fixture("Tiff", "jpeg-strips.tif"),
            Fixture("Tiff", "g4.tif"),
            Fixture("WebP", "lossy-simple.webp"),
            Fixture("WebP", "lossless.webp"),
            Fixture("WebP", "animated.webp"),
        ];
        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(file);
            PdfImage image = PdfImage.Load(data);
            long pixels = (long)image.PixelWidth * image.PixelHeight;
            Assert.AreEqual(image.PixelWidth, PdfImage.Load(data, 0, pixels).PixelWidth, file);
            Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(data, 0, pixels - 1), file);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfImage.Load(File.ReadAllBytes(files[0]), 0, 0));
    }
}
