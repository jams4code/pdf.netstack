using PdfNetStack.Html;

namespace PdfNetStack.Tests;

/// <summary>The HTML converter and the command line on top of progressive, asynchronous output.</summary>
[TestClass]
public sealed class AsyncOutputTests
{
    private static readonly HtmlConverterOptions Standard = new() { UseSystemFonts = false };

    private const string Html = "<h1>Invoice 42</h1><p>Total: 42 EUR</p><table><tr><td>One</td><td>Two</td></tr></table>";

    [TestMethod]
    public async Task HtmlConvertsToAStreamThatForbidsSynchronousWrites()
    {
        using var output = new SaveStreamingTests.RecordingStream { ForbidSynchronousIo = true };
        await HtmlConverter.ConvertToPdfAsync(Html, output, Standard);

        PdfDocument doc = PdfDocument.Open(output.ToArray());
        StringAssert.Contains(doc.Pages[0].ExtractText(), "Invoice 42");

        // The document the synchronous call writes; only the file identifier, drawn anew for each conversion, differs.
        byte[] reference = HtmlConverter.ConvertToPdf(Html, Standard);
        Assert.AreEqual(PdfDocument.Open(reference).Pages[0].ExtractText(), doc.Pages[0].ExtractText());
    }

    [TestMethod]
    public async Task HtmlConversionHonoursCancellationAndFailures()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => HtmlConverter.ConvertToPdfAsync(Html, output, Standard, cancel.Token));
        Assert.AreEqual(0, output.Length);

        using var failing = new SaveStreamingTests.RecordingStream { FailAfter = 100 };
        await Assert.ThrowsExactlyAsync<IOException>(() => HtmlConverter.ConvertToPdfAsync(Html, failing, Standard));

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => HtmlConverter.ConvertToPdfAsync(null!, output));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => HtmlConverter.ConvertToPdfAsync(Html, null!));
    }

    [TestMethod]
    public async Task HtmlUnoptimizedOutputIsWrittenProgressively()
    {
        // Text that compresses poorly, so the file outgrows one chunk of output.
        var random = new Random(1);
        string Words() => string.Join(" ", Enumerable.Range(0, 150).Select(_ => new string(Enumerable.Range(0, 3 + random.Next(6)).Select(_ => (char)('a' + random.Next(26))).ToArray())));
        string html = string.Concat(Enumerable.Range(0, 200).Select(i => $"<h2>Section {i}</h2><p>{Words()}</p>"));
        using var output = new SaveStreamingTests.RecordingStream { ForbidSynchronousIo = true };
        await HtmlConverter.ConvertToPdfAsync(html, output, new HtmlConverterOptions { UseSystemFonts = false, SaveOptions = new PdfSaveOptions { Optimize = false } });
        Assert.IsGreaterThan(1, output.Writes.Count, $"{output.Length} bytes");
        Assert.IsGreaterThan(1, PdfDocument.Open(output.ToArray()).Pages.Count);
    }

#if NET
    [TestMethod]
    public async Task TheCommandLineOptimizesInStreamingMode()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "in.pdf"), output = Path.Combine(directory, "out.pdf");
            byte[] source = SaveStreamingTests.Created(4).ToArray(new PdfSaveOptions { Optimize = false });
            File.WriteAllBytes(input, source);

            Assert.AreEqual(0, await PdfNetStack.Cli.Cli.RunAsync(["optimize", input, output, "--streaming", "--min-saving", "0", "--json"]));

            byte[] written = File.ReadAllBytes(output);
            Assert.IsNull(PdfEquivalence.FindDifference(source, written));
            (byte[] inMemory, _) = PdfOptimizer.Optimize(source, new PdfOptimizerOptions { MinSavingPercent = 0 });
            CollectionAssert.AreEqual(inMemory, written);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
#endif
}
