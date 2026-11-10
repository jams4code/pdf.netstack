using System.IO.Compression;
using System.Text;
using PdfNetStack.Filters;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>The optimizer in streaming mode: source read on demand, output written progressively, the full proof.</summary>
[TestClass]
public sealed class StreamingOptimizerTests
{
    private static readonly PdfOptimizerOptions InMemory = new() { MinSavingPercent = 0 };
    private static readonly PdfOptimizerOptions Streaming = InMemory with { Streaming = true };

    private static string Hash(byte[] data) => Compat.ToHex(Compat.Sha256(data)).ToLowerInvariant();

    /// <summary>Optimizes <paramref name="input"/> read on demand from a stream, as <see cref="PdfOptimizer.OptimizeAsync"/> does for a file.</summary>
    private static (byte[] Output, PdfOptimizationResult Result) RunStreaming(byte[] input, PdfOptimizerOptions? options = null)
    {
        using var source = new MemoryStream(input);
        using var output = new SaveStreamingTests.RecordingStream { ForbidSynchronousIo = true };
        PdfOptimizationResult result = PdfOptimizer.OptimizeAsync(source, output, options ?? Streaming).GetAwaiter().GetResult();
        return (output.ToArray(), result);
    }

    private static void AssertProven(byte[] input, byte[] output, PdfOptimizationResult result)
    {
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Streamed);
        Assert.IsGreaterThan(0, result.Statistics.ObjectsVerified);
        Assert.AreEqual(Hash(input), result.SourceSha256);
        Assert.AreEqual(Hash(output), result.OutputSha256);
        Assert.AreEqual(output.Length, result.OutputBytes);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
        PdfFile file = PdfFile.Open(output, 64 << 20);
        Assert.IsFalse(file.WasReconstructed);
    }

    /// <summary>Documents drawn by the library and saved without the optimizer: fonts, images, content, bookmarks.</summary>
    private static IEnumerable<byte[]> Inputs()
    {
        for (int seed = 1; seed <= 8; seed++)
        {
            yield return SaveStreamingTests.Created(seed).ToArray(new PdfSaveOptions { Optimize = false, UseObjectStreams = seed % 2 == 0 });
        }

        // Merged documents repeat fonts and images.
        yield return PdfDocument.Merge([SaveStreamingTests.Created(2), SaveStreamingTests.Created(2), SaveStreamingTests.Created(5)]).ToArray(new PdfSaveOptions { Optimize = false });
        yield return SaveStreamingTests.HeavyFile(pages: 4, imageBytes: 40_000, objectStreams: false);
    }

    [TestMethod]
    public void StreamingRewritesAreProvenAndMatchTheInMemoryRewrite()
    {
        int index = 0;
        foreach (byte[] input in Inputs())
        {
            (byte[] streamed, PdfOptimizationResult result) = RunStreaming(input);
            AssertProven(input, streamed, result);

            // Without whitespace variants of the same content or JPEG files that differ only in
            // metadata, duplicates are found alike, and the output is the same.
            (byte[] inMemory, PdfOptimizationResult reference) = PdfOptimizer.Optimize(input, InMemory);
            Assert.AreEqual(OptimizationDisposition.Optimized, reference.Disposition);
            CollectionAssert.AreEqual(inMemory, streamed, $"Input {index}");
            Assert.AreEqual(reference.Statistics.ObjectsDeduplicated, result.Statistics.ObjectsDeduplicated);
            index++;
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EveryEntryPointStreams(bool objectStreams)
    {
        byte[] input = SaveStreamingTests.Created(6).ToArray(new PdfSaveOptions { Optimize = false, UseObjectStreams = objectStreams });
        var options = Streaming with { UseObjectStreams = objectStreams };

        using (var output = new MemoryStream())
        {
            PdfOptimizationResult fromMemory = PdfOptimizer.Optimize(input.AsMemory(), output, options);
            AssertProven(input, output.ToArray(), fromMemory);
        }

        (byte[] bytes, PdfOptimizationResult fromArray) = PdfOptimizer.Optimize(input, options);
        AssertProven(input, bytes, fromArray);

        string directory = NewDirectory();
        try
        {
            string source = Path.Combine(directory, "in.pdf"), destination = Path.Combine(directory, "out.pdf");
            File.WriteAllBytes(source, input);
            PdfOptimizationResult fromFile = PdfOptimizer.OptimizeFileAsync(source, destination, options).GetAwaiter().GetResult();
            AssertProven(input, File.ReadAllBytes(destination), fromFile);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(destination));
            CollectionAssert.AreEquivalent(new[] { source, destination }, Directory.GetFiles(directory));

            using var input2 = new FileStream(source, FileMode.Open, FileAccess.Read);
            using var output2 = new SaveStreamingTests.RecordingStream { ForbidSynchronousIo = true };
            PdfOptimizationResult fromFileStream = PdfOptimizer.OptimizeAsync(input2, output2, options).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(bytes, output2.ToArray());
            Assert.IsTrue(fromFileStream.Statistics.Streamed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A request body: forward only, and synchronous reads forbidden.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(_inner.Read(buffer, offset, Math.Min(count, 7000)));

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_inner.Read(buffer.Span[..Math.Min(buffer.Length, 7000)]));
#endif

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    public async Task AForwardOnlyInputIsCopiedAsynchronouslyFirst()
    {
        byte[] input = SaveStreamingTests.Created(3).ToArray(new PdfSaveOptions { Optimize = false });
        (byte[] expected, _) = RunStreaming(input);
        using var source = new ForwardOnlyStream(input);
        using var output = new SaveStreamingTests.RecordingStream { ForbidSynchronousIo = true };
        PdfOptimizationResult result = await PdfOptimizer.OptimizeAsync(source, output, Streaming);
        AssertProven(input, output.ToArray(), result);
        CollectionAssert.AreEqual(expected, output.ToArray());
    }

    [TestMethod]
    public void DuplicatesEncodedDifferentlyMerge()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        var random = new Random(3);
        var pixels = new byte[64 * 64 * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i / 3 % 64) * 4 + random.Next(2));
        const string image = "/Type/XObject/Subtype/Image/Width 64/Height 64/ColorSpace/DeviceRGB/BitsPerComponent 8";
        b.SetStream(10, image, pixels);
        b.SetStream(11, image + "/Filter/FlateDecode", StreamCodec.Deflate(pixels, CompressionLevel.Fastest));
        b.SetStream(12, image + "/Filter/ASCIIHexDecode", Encoding.ASCII.GetBytes(Compat.ToHex(pixels) + ">"));
        b.SetStream(4, "", Encoding.ASCII.GetBytes("q 64 0 0 64 0 0 cm /Im0 Do Q q 64 0 0 64 70 0 cm /Im1 Do Q q 64 0 0 64 140 0 cm /Im2 Do Q"));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Resources<</XObject<</Im0 10 0 R/Im1 11 0 R/Im2 12 0 R>>>>/Contents 4 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = RunStreaming(input);

        AssertProven(input, output, result);
        Assert.AreEqual(2, result.Statistics.ObjectsDeduplicated);
        Assert.IsLessThan(input.Length / 2, output.Length);
    }

    [TestMethod]
    public void LossyImagesStreamWithTheirAllowance()
    {
        Imaging.Raster photo = SyntheticImages.Photo(600, 400, 3, seed: 4);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        string dictionary = $"/Type/XObject/Subtype/Image/Width {photo.Width}/Height {photo.Height}/ColorSpace/DeviceRGB/BitsPerComponent 8";
        b.SetStream(10, dictionary + "/Filter/FlateDecode", StreamCodec.Deflate(photo.Samples, CompressionLevel.Optimal));

        // The same picture again, as a second object: merged, and allowed the same change.
        b.SetStream(11, dictionary + "/Filter/FlateDecode", StreamCodec.Deflate(photo.Samples, CompressionLevel.Fastest));
        b.SetStream(12, dictionary + "/Filter/DCTDecode", SyntheticImages.Jpeg(photo, 95));
        b.SetStream(4, "", Encoding.ASCII.GetBytes("q 144 0 0 96 0 0 cm /Im0 Do Q q 144 0 0 96 200 0 cm /Im1 Do Q q 144 0 0 96 0 300 cm /Im2 Do Q"));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R/Im1 11 0 R/Im2 12 0 R>>>>/Contents 4 0 R>>");
        byte[] input = b.Build();
        PdfOptimizerOptions lossy = Streaming with { MaxImageDpi = 150, JpegQuality = 60, ConvertImagesToJpeg = true };

        (byte[] output, PdfOptimizationResult result) = RunStreaming(input, lossy);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsGreaterThan(0, result.Statistics.ImagesDownsampled);
        Assert.AreEqual(1, result.Statistics.ObjectsDeduplicated);
        Assert.IsGreaterThan(0, result.Statistics.ObjectsVerified);
        Assert.IsLessThan(input.Length / 4, output.Length);
        Assert.AreEqual(Hash(output), result.OutputSha256);
    }

    [TestMethod]
    public void FilesTheOptimizerMustNotTouchComeBackUnchanged()
    {
        PdfDocument encrypted = SaveStreamingTests.Created(2);
        encrypted.Security.Encrypt("user", "owner");
        byte[] locked = encrypted.ToArray(new PdfSaveOptions { Optimize = false });
        (byte[] output, PdfOptimizationResult result) = RunStreaming(locked);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(locked, output);
        Assert.AreEqual(Hash(locked), result.OutputSha256);

        byte[] plain = SaveStreamingTests.Created(2).ToArray(new PdfSaveOptions { Optimize = false });
        byte[] signed = [.. plain, .. "\n% /ByteRange [0 0 0 0]\n"u8.ToArray()];
        (output, result) = RunStreaming(signed);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "signed");
        CollectionAssert.AreEqual(signed, output);

        (output, result) = RunStreaming(plain, Streaming with { MaxInputBytes = 1000 });
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(plain, output);

        (output, result) = RunStreaming(plain, Streaming with { MinSavingPercent = 99 });
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, result.Disposition);
        CollectionAssert.AreEqual(plain, output);

        byte[] junk = Encoding.ASCII.GetBytes("not a pdf at all");
        (output, result) = RunStreaming(junk);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(junk, output);
    }

    [TestMethod]
    public void LinearizationCannotStream()
    {
        byte[] input = SaveStreamingTests.Created(1).ToArray(new PdfSaveOptions { Optimize = false });
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, Streaming with { Linearize = true }));
    }

    [TestMethod]
    public async Task CancellationLeavesTheDestinationAsItWas()
    {
        string directory = NewDirectory();
        try
        {
            string source = Path.Combine(directory, "in.pdf"), destination = Path.Combine(directory, "out.pdf");
            File.WriteAllBytes(source, SaveStreamingTests.HeavyFile(pages: 6, imageBytes: 60_000));
            File.WriteAllText(destination, "previous");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfOptimizer.OptimizeFileAsync(source, destination, Streaming, cancel.Token));
            Assert.AreEqual("previous", File.ReadAllText(destination));
            Assert.HasCount(2, Directory.GetFiles(directory));

            using var output = new MemoryStream();
            using var input = new MemoryStream(File.ReadAllBytes(source));
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfOptimizer.OptimizeAsync(input, output, Streaming, cancel.Token));
            Assert.AreEqual(0, output.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AFailingOutputStreamSurfacesItsError()
    {
        byte[] input = SaveStreamingTests.HeavyFile(pages: 6, imageBytes: 60_000);
        using var source = new MemoryStream(input);
        using var failing = new SaveStreamingTests.RecordingStream { FailAfter = 100_000 };
        await Assert.ThrowsExactlyAsync<IOException>(() => PdfOptimizer.OptimizeAsync(source, failing, Streaming));
    }

    /// <summary>
    /// The point of streaming mode: live memory stays far below the in-memory mode's, and below
    /// the file size. Measured as live managed bytes after forced collections, sampled during
    /// the run, with generous margins.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void StreamingKeepsMemoryFarBelowTheFileSize()
    {
        string directory = NewDirectory();
        try
        {
            string source = Path.Combine(directory, "in.pdf");
            // Large enough that fixed overhead, such as buffers pooled per core, stays well inside the
            // bound on every platform: at 120 pages a 3 core macOS runner came within 2% of it.
            File.WriteAllBytes(source, SaveStreamingTests.HeavyFile(pages: 240, imageBytes: 300_000));
            long size = new FileInfo(source).Length;
            var options = new PdfOptimizerOptions { MinSavingPercent = 0, MaxDegreeOfParallelism = 2 };

            PdfOptimizationResult? inMemory = null, streamed = null;
            long inMemoryPeak = MemorySampler.PeakAbove(() => inMemory = PdfOptimizer.OptimizeFileAsync(source, Path.Combine(directory, "a.pdf"), options).GetAwaiter().GetResult());
            long streamingPeak = MemorySampler.PeakAbove(() => streamed = PdfOptimizer.OptimizeFileAsync(source, Path.Combine(directory, "b.pdf"), options with { Streaming = true }).GetAwaiter().GetResult());

            Assert.AreEqual(OptimizationDisposition.Optimized, inMemory!.Disposition, inMemory.Reason);
            Assert.AreEqual(OptimizationDisposition.Optimized, streamed!.Disposition, streamed.Reason);
            Assert.AreEqual(inMemory.OutputSha256, streamed.OutputSha256);
            Assert.IsGreaterThan(size, inMemoryPeak, $"In memory {inMemoryPeak:N0} bytes for a {size:N0} byte file.");
            Assert.IsLessThan(size / 3, streamingPeak, $"Streaming {streamingPeak:N0} bytes for a {size:N0} byte file, in memory {inMemoryPeak:N0}.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
