using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Tests;

/// <summary>Progressive and asynchronous saving: same bytes as ToArray, bounded writes, no synchronous IO.</summary>
[TestClass]
public sealed class SaveStreamingTests
{
    /// <summary>A stream that records every write and can refuse synchronous IO, fail after a number of bytes, or cancel.</summary>
    internal sealed class RecordingStream : MemoryStream
    {
        public bool ForbidSynchronousIo { get; init; }

        /// <summary>Throw an IOException once this many bytes were written.</summary>
        public long FailAfter { get; init; } = long.MaxValue;

        /// <summary>Cancelled once this many bytes were written.</summary>
        public (CancellationTokenSource Source, long After)? CancelAfter { get; init; }

        public List<int> Writes { get; } = [];

        public int LargestWrite => Writes.Count == 0 ? 0 : Writes.Max();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (ForbidSynchronousIo) throw new InvalidOperationException("Synchronous operations are disallowed.");
            Record(buffer, offset, count);
        }

        public override void WriteByte(byte value)
        {
            if (ForbidSynchronousIo) throw new InvalidOperationException("Synchronous operations are disallowed.");
            base.WriteByte(value);
        }

        public override void Flush()
        {
            if (ForbidSynchronousIo) throw new InvalidOperationException("Synchronous operations are disallowed.");
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(buffer, offset, count);
            return Task.CompletedTask;
        }

#if NET
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (ForbidSynchronousIo) throw new InvalidOperationException("Synchronous operations are disallowed.");
            Record(buffer.ToArray(), 0, buffer.Length);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
#endif

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void Record(byte[] buffer, int offset, int count)
        {
            if (Length + count > FailAfter) throw new IOException("The disk is full.");
            base.Write(buffer, offset, count);
            Writes.Add(count);
            if (CancelAfter is { } cancel && Length >= cancel.After) cancel.Source.Cancel();
        }
    }

    private static readonly PdfOpenTypeFont Embedded = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));

    /// <summary>
    /// A document drawn with the library: text in an embedded font and, unless it is to be
    /// archived, in standard fonts, shapes, JPEG images (one shared), bookmarks, and unless
    /// archived, notes and an attachment.
    /// </summary>
    internal static PdfDocument Created(int seed, int imageSide = 96, bool archival = false)
    {
        var random = new Random(seed);
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = "Generated " + seed;
        doc.Info.Author = "Synthetic";
        PdfImage shared = PdfImage.Load(SyntheticImages.Jpeg(SyntheticImages.Photo(imageSide, imageSide, 3, seed), 85));
        int pages = 1 + random.Next(5);
        for (int p = 0; p < pages; p++)
        {
            PdfPage page = doc.Pages.Add(p % 2 == 0 ? PdfRectangle.A4 : new PdfRectangle(0, 0, 400 + random.Next(200), 500));
            using (PdfGraphics g = page.CreateGraphics())
            {
                // The test font draws only a few letters.
                g.DrawString("AV To " + new string('A', 1 + random.Next(9)), Embedded, 18, PdfColor.Black, 40, 40);
                if (!archival)
                {
                    g.DrawString($"Page {p + 1} of document {seed}", p % 2 == 0 ? PdfStandardFont.Helvetica : PdfStandardFont.TimesRoman, 14, PdfColor.Rgb(0, 0, 0), 40, 60);
                    for (int line = 0; line < 3 + random.Next(10); line++)
                    {
                        g.DrawString("Line " + line + " " + new string('x', random.Next(40)), PdfStandardFont.Courier, 9, PdfColor.Rgb(0.2, 0.2, 0.2), 40, 90 + (line * 12));
                    }
                }

                g.DrawRectangle(new PdfPen(PdfColor.Rgb(random.NextDouble(), 0, 1), 1.5), PdfColor.Rgb(1, random.NextDouble(), 0), 40, 300, 100 + random.Next(100), 60);
                g.DrawImage(shared, 200, 300, 120, 120);
                if (random.Next(2) == 0)
                {
                    g.DrawImage(PdfImage.Load(SyntheticImages.Jpeg(SyntheticImages.Photo(48 + random.Next(64), 48, 3, seed + p), 70)), 40, 420, 80, 60);
                }
            }

            if (!archival && random.Next(3) == 0) page.Annotations.AddNote(20, 20, "Note " + p, author: "Tester");
            doc.Outlines.Add("Chapter " + (p + 1), page);
        }

        if (!archival && seed % 3 == 0) doc.Attachments.Add("data.txt", Encoding.ASCII.GetBytes(new string('d', 500 + seed)), "text/plain", "Attached data");
        return doc;
    }

    /// <summary>Pages of text, each also painting a large image that does not compress.</summary>
    internal static byte[] HeavyFile(int pages, int imageBytes, bool objectStreams = true, int seed = 7)
    {
        var b = new PdfBuilder();
        var kids = new StringBuilder();
        var packed = new HashSet<int> { 1, 2 };
        int next = 3;
        var random = new Random(seed);
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

    private static readonly PdfSaveOptions[] OptionSets =
    [
        new() { Optimize = false },
        new() { Optimize = false, UseObjectStreams = false },
        new() { Optimize = false, Compression = CompressionEffort.Fast },
        new() { Optimize = false, Compression = CompressionEffort.Exhaustive },
        new() { Optimize = false, Compression = CompressionEffort.Balanced, UseObjectStreams = false },
        new() { Optimize = false, Conformance = PdfConformance.PdfA2B },
        new() { Optimize = false, Conformance = PdfConformance.PdfA1B },
        new() { Optimize = true },
        new() { Optimize = true, VerifyOptimization = false, UseObjectStreams = false },
    ];

    private static byte[] SaveToStream(PdfDocument doc, PdfSaveOptions options)
    {
        using var stream = new MemoryStream();
        doc.Save(stream, options);
        return stream.ToArray();
    }

    private static byte[] SaveAsyncToStream(PdfDocument doc, PdfSaveOptions options)
    {
        using var stream = new RecordingStream { ForbidSynchronousIo = true };
        doc.SaveAsync(stream, options).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    [TestMethod]
    public void SaveWritesTheBytesToArrayReturnsForManyDocumentsAndOptions()
    {
        int compared = 0;
        for (int seed = 1; seed <= 12; seed++)
        {
            foreach (PdfSaveOptions options in OptionSets.Where(o => o.Conformance == PdfConformance.None))
            {
                PdfDocument doc = Created(seed);
                byte[] array = doc.ToArray(options);
                byte[] saved = SaveToStream(doc, options);
                byte[] savedAsync = SaveAsyncToStream(doc, options);
                CollectionAssert.AreEqual(array, saved, $"Document {seed}, {options}");
                CollectionAssert.AreEqual(array, savedAsync, $"Document {seed}, {options}, async");
                Assert.AreEqual(doc.Pages.Count, PdfDocument.Open(saved).Pages.Count);
                compared++;
            }
        }

        Assert.AreEqual(12 * 7, compared);
    }

    /// <summary>
    /// Every PDF/A save is a new instance of the document: its XMP packet gets a fresh instance
    /// id and the time of the save. Saved within the same second, two saves differ only there.
    /// </summary>
    [TestMethod]
    [DataRow(PdfConformance.PdfA1B)]
    [DataRow(PdfConformance.PdfA2B)]
    [DataRow(PdfConformance.PdfA3B)]
    public void ArchivalSavesDifferFromToArrayOnlyInTheInstanceId(PdfConformance conformance)
    {
        for (int seed = 1; seed <= 4; seed++)
        {
            var options = new PdfSaveOptions { Optimize = false, Conformance = conformance, UseObjectStreams = seed % 2 == 0 };
            for (int attempt = 0; ; attempt++)
            {
                PdfDocument doc = Created(seed, archival: true);
                long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                byte[] array = doc.ToArray(options);
                byte[] saved = SaveToStream(doc, options);
                byte[] savedAsync = SaveAsyncToStream(doc, options);
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() != second && attempt < 5) continue;

                foreach (byte[] other in new[] { saved, savedAsync })
                {
                    Assert.HasCount(array.Length, other);
                    string text = Compat.Latin1.GetString(array);
                    int start = text.IndexOf("<xmpMM:InstanceID>", StringComparison.Ordinal);
                    int end = text.IndexOf("</xmpMM:InstanceID>", StringComparison.Ordinal);
                    Assert.IsGreaterThan(0, start);
                    for (int i = 0; i < array.Length; i++)
                    {
                        if (array[i] != other[i]) Assert.IsTrue(i > start && i < end, $"Byte {i} differs outside the instance id, {options}");
                    }
                }

                break;
            }
        }
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void OpenedDocumentsSaveTheSameBytesEitherWay(bool objectStreams, bool onDemand)
    {
        byte[] source = HeavyFile(pages: 5, imageBytes: 90_000, objectStreams);
        foreach (PdfSaveOptions options in OptionSets.Where(o => o.Conformance == PdfConformance.None))
        {
            using var input = new MemoryStream(source);
            PdfDocument doc = onDemand ? PdfDocument.OpenOnDemand(input) : PdfDocument.Open(source);
            doc.Pages[1].Rotate(90);
            byte[] array = doc.ToArray(options);
            CollectionAssert.AreEqual(array, SaveToStream(doc, options), options.ToString());
            CollectionAssert.AreEqual(array, SaveAsyncToStream(doc, options), options + ", async");
        }
    }

    [TestMethod]
    [DataRow(PdfEncryption.Aes256, true)]
    [DataRow(PdfEncryption.Aes128, true)]
    [DataRow(PdfEncryption.Aes256, false)]
    [DataRow(PdfEncryption.Aes128, false)]
    public void EncryptedSavesMatchToArrayApartFromTheirRandomVectors(PdfEncryption algorithm, bool optimize)
    {
        for (int seed = 1; seed <= 4; seed++)
        {
            PdfDocument doc = Created(seed);
            doc.Security.Encrypt("user", "owner", PdfPermissions.Print, algorithm);
            var options = new PdfSaveOptions { Optimize = optimize, UseObjectStreams = seed % 2 == 0 };
            byte[] array = doc.ToArray(options);
            byte[] saved = SaveToStream(doc, options);
            byte[] savedAsync = SaveAsyncToStream(doc, options);
            foreach (byte[] other in new[] { saved, savedAsync })
            {
                // AES draws a fresh initialization vector for every string and stream, and a string
                // is written in whichever notation is shorter, so lengths may differ by a few
                // bytes. The objects, their numbers and what they decrypt to are the same.
                Assert.IsLessThanOrEqualTo(64, Math.Abs(array.Length - other.Length));
                PdfFile a = PdfFile.Open(array, 64 << 20);
                PdfFile b = PdfFile.Open(other, 64 << 20);
                CollectionAssert.AreEqual(Layout(a), Layout(b));
                a.Unlock("user");
                b.Unlock("user");
                Assert.IsNull(EquivalenceVerifier.Verify(a, b, VerificationRules.Lossless, 64 << 20, 1, CancellationToken.None, out _));
            }

            Assert.AreEqual(doc.Pages.Count, PdfDocument.Open(saved, "user").Pages.Count);
        }
    }

    /// <summary>Each object number with where it lives: in the file, or in which object stream at which index.</summary>
    private static List<string> Layout(PdfFile file) =>
        file.Xref.OrderBy(e => e.Key).Select(e => e.Value.Type == XrefEntryType.InObjectStream ? $"{e.Key}:{e.Value.Field1}/{e.Value.Field2}" : $"{e.Key}:{e.Value.Type}").ToList();

    [TestMethod]
    public void UnoptimizedEncryptedSaveIsEncryptedInOnePass()
    {
        PdfDocument doc = Created(5);
        doc.Security.Encrypt("user", "owner", PdfPermissions.Print, PdfEncryption.Aes256);
        byte[] saved = SaveToStream(doc, new PdfSaveOptions { Optimize = false, UseObjectStreams = false });

        // Names are not encrypted, so with every object on its own the catalog shows its extension.
        string raw = Compat.Latin1.GetString(saved);
        Assert.AreEqual(-1, raw.IndexOf("Page 1 of document", StringComparison.Ordinal));
        StringAssert.Contains(raw, "/Encrypt");
        StringAssert.Contains(raw, "/ADBE");
        PdfDocument back = PdfDocument.Open(saved, "user");
        StringAssert.Contains(back.Pages[0].ExtractText(), "Page 1 of document 5");
        Assert.AreEqual("Generated 5", back.Info.Title);
        Assert.IsNull(doc.Catalog["Extensions"], "The extension goes into the file, not into the document.");
    }

    [TestMethod]
    public void UnoptimizedSaveWritesObjectByObject()
    {
        const int imageBytes = 250_000;
        byte[] source = HeavyFile(pages: 8, imageBytes);
        using var input = new MemoryStream(source);
        PdfDocument doc = PdfDocument.OpenOnDemand(input);
        using var output = new RecordingStream();
        doc.Save(output, new PdfSaveOptions { Optimize = false });

        // Every image goes out on its own; nothing close to the whole file is ever one write.
        Assert.IsGreaterThan(8, output.Writes.Count);
        Assert.IsLessThanOrEqualTo(imageBytes + 64, output.LargestWrite);
        Assert.IsLessThan(source.Length / 4, output.LargestWrite);
        CollectionAssert.AreEqual(doc.ToArray(new PdfSaveOptions { Optimize = false }), output.ToArray());
    }

    [TestMethod]
    public async Task SaveAsyncNeverWritesSynchronously()
    {
        foreach (PdfSaveOptions options in OptionSets)
        {
            PdfDocument doc = Created(4, archival: options.Conformance != PdfConformance.None);
            using var output = new RecordingStream { ForbidSynchronousIo = true };
            await doc.SaveAsync(output, options);
            Assert.IsGreaterThan(0, output.Writes.Count);
            Assert.AreEqual(doc.Pages.Count, PdfDocument.Open(output.ToArray()).Pages.Count);
        }
    }

    [TestMethod]
    public void SynchronousSaveFailsOnAStreamThatForbidsIt()
    {
        PdfDocument doc = Created(2);
        using var output = new RecordingStream { ForbidSynchronousIo = true };
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Save(output, new PdfSaveOptions { Optimize = false }));
    }

    [TestMethod]
    public async Task SaveAsyncStopsWhenCancelledBeforeStarting()
    {
        PdfDocument doc = Created(1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var output = new RecordingStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => doc.SaveAsync(output, new PdfSaveOptions { Optimize = false }, cancel.Token));
        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public async Task SaveAsyncStopsBetweenObjectsWhenCancelled()
    {
        byte[] source = HeavyFile(pages: 10, imageBytes: 120_000);
        PdfDocument doc = PdfDocument.Open(source);
        int full = doc.ToArray(new PdfSaveOptions { Optimize = false }).Length;

        using var cancel = new CancellationTokenSource();
        using var output = new RecordingStream { CancelAfter = (cancel, 200_000) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => doc.SaveAsync(output, new PdfSaveOptions { Optimize = false }, cancel.Token));
        Assert.IsLessThan(full / 2, (int)output.Length, "The save stopped soon after the token fired.");
    }

    [TestMethod]
    public async Task AFailingOutputStreamSurfacesItsError()
    {
        byte[] source = HeavyFile(pages: 6, imageBytes: 100_000);
        foreach (bool optimize in new[] { false, true })
        {
            var options = new PdfSaveOptions { Optimize = optimize };
            PdfDocument doc = PdfDocument.Open(source);
            using var failing = new RecordingStream { FailAfter = 150_000 };
            Assert.ThrowsExactly<IOException>(() => doc.Save(failing, options));

            using var failingAsync = new RecordingStream { FailAfter = 150_000 };
            await Assert.ThrowsExactlyAsync<IOException>(() => doc.SaveAsync(failingAsync, options));
        }
    }

    [TestMethod]
    public async Task SaveAsyncToAPathReplacesTheFileOnlyWhenComplete()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "out.pdf");
            File.WriteAllText(path, "previous");
            PdfDocument doc = PdfDocument.Open(HeavyFile(pages: 10, imageBytes: 120_000));

            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => doc.SaveAsync(path, new PdfSaveOptions { Optimize = false }, cancel.Token));
            }

            Assert.AreEqual("previous", File.ReadAllText(path));
            Assert.HasCount(1, Directory.GetFiles(directory));

            await doc.SaveAsync(path, new PdfSaveOptions { Optimize = false });
            CollectionAssert.AreEqual(doc.ToArray(new PdfSaveOptions { Optimize = false }), File.ReadAllBytes(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Saving a document opened on demand without the optimizer keeps memory near the largest
    /// object. Measured as live managed bytes after forced collections, sampled during the save.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void UnoptimizedSaveOfALargeFileKeepsMemoryBounded()
    {
        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-heavy-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, HeavyFile(pages: 120, imageBytes: 400_000));
            long size = new FileInfo(path).Length;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read);
            PdfDocument doc = PdfDocument.OpenOnDemand(input);

            long peak = MemorySampler.PeakAbove(() =>
            {
                using var sink = new CountingSink();
                doc.Save(sink, new PdfSaveOptions { Optimize = false });
                Assert.IsGreaterThan(size / 2, sink.Written);
            });

            Assert.IsLessThan(size / 4, peak, $"Peak {peak:N0} bytes above the baseline for a {size:N0} byte file.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Discards what is written, counting it.</summary>
    internal sealed class CountingSink : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position { get => Written; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written += count;

#if NET
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
#endif
    }
}

/// <summary>Live managed memory during an action, sampled after forced collections.</summary>
internal static class MemorySampler
{
    /// <summary>The highest live managed memory seen while <paramref name="action"/> ran, less what was live before it.</summary>
    public static long PeakAbove(Action action)
    {
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = baseline;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!done.Wait(15))
            {
                long now = GC.GetTotalMemory(forceFullCollection: true);
                if (now > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, now);
            }
        })
        { IsBackground = true };
        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            done.Set();
            sampler.Join();
        }

        return Math.Max(0, peak - baseline);
    }
}
