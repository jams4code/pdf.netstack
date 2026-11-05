using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Hyphenation;
using PdfNetStack.Objects;
using PdfNetStack.Ocr;

// Without arguments: every check, in one process, exit code 1 on the first failure.
// --inputs <dir>: writes the synthetic inputs the cold start steps read.
// --cold <step> <dir>: runs one step twice and prints "step,first ms,first allocated bytes,second
// ms", for a fresh process per measurement (tools/measure_cold_start.py). font-text reads
// <dir>/font.ttf, which the script copies from the test fixtures.
try
{
    if (args.Length == 2 && args[0] == "--inputs") return Smoke.WriteInputs(args[1]);
    if (args.Length == 3 && args[0] == "--cold") return Smoke.Cold(args[1], args[2]);
    if (args.Length == 0) return Smoke.All();
    Console.Error.WriteLine("Usage: PdfNetStack.AotSmoke [--inputs <dir> | --cold <open-save|optimize|text|html|html-system-fonts|font-text> <dir>]");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL " + e);
    return 1;
}

internal static class Smoke
{
    private const string Heading = "Serverless smoke test";

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head><title>Invoice 42</title>
        <style>
          body { font-family: Inter, sans-serif; font-size: 11pt }
          h1 { color: #1e3a8a; border-bottom: 2px solid #1e3a8a }
          table { border-collapse: collapse; width: 100% }
          td, th { border: 1px solid #999; padding: 4pt 6pt; text-align: left }
          .total { display: flex; justify-content: flex-end; font-weight: bold; margin-top: 12pt }
        </style></head>
        <body>
          <h1>Serverless smoke test</h1>
          <p>Converted without a browser, in a single process.</p>
          <table>
            <tr><th>Item</th><th>Quantity</th><th>Price</th></tr>
            <tr><td>Optimization</td><td>3</td><td>12.50</td></tr>
            <tr><td>Conversion</td><td>1</td><td>7.00</td></tr>
          </table>
          <div class="total">Total 44.50</div>
        </body>
        </html>
        """;

    public static int All()
    {
        Run("create", () =>
        {
            byte[] pdf = Create(pages: 2);
            Check(PdfDocument.Open(pdf).Pages.Count == 2, "the created file has two pages");
        });

        byte[] source = Create(pages: 2);
        Run("open and save", () =>
        {
            byte[] saved = OpenAndSave(source);
            PdfDocument reopened = PdfDocument.Open(saved);
            Check(reopened.Pages.Count == 3, "a page was added");
            Check(reopened.Info.Title == "Edited", "the title was set");
        });

        Run("open on demand", () =>
        {
            using var stream = new MemoryStream(source, writable: false);
            PdfDocument doc = PdfDocument.OpenOnDemand(stream);
            Check(doc.Pages[1].ExtractText().Contains("Page 2", StringComparison.Ordinal), "page 2 reads on demand");
        });

        Run("optimize lossless", () =>
        {
            (byte[] output, PdfOptimizationResult result) = Optimize(source, PdfOptimizerOptions.Default);
            Check(result.Disposition is OptimizationDisposition.Optimized or OptimizationDisposition.OriginalRetained, result.Reason);
            Check(PdfDocument.Open(output).Pages.Count == 2, "the output opens");
        });

        Run("optimize lossy", () =>
        {
            var options = new PdfOptimizerOptions { ConvertImagesToJpeg = true, JpegQuality = 75, MaxImageDpi = 72 };
            (byte[] output, PdfOptimizationResult result) = Optimize(source, options);
            Check(result.Disposition == OptimizationDisposition.Optimized, result.Reason);
            Check(output.Length < source.Length, "the lossy output is smaller");
        });

        Run("extract text", () =>
        {
            string text = ExtractText(source);
            Check(text.Contains(Heading, StringComparison.Ordinal) && text.Contains("Page 2", StringComparison.Ordinal), "the text reads back");
        });

        Run("extract images", () =>
        {
            PdfPageImage image = PdfDocument.Open(source).Pages[0].GetImages().Single();
            byte[] png = image.Decode()?.ToPng() ?? [];
            Check(png.Length > 8 && png[1] == (byte)'P', "the image decodes to PNG");
        });

        Run("predefined CJK CMaps", () =>
        {
            // Embedded Brotli resources: Shift-JIS 82A0 is CID 843 in Adobe-Japan1, which is あ.
            Check(PdfDocument.Open(CjkPdf()).Pages[0].ExtractText() == "あ", "the CMap resource reads");
        });

        Run("html to pdf", () =>
        {
            byte[] pdf = ConvertHtml(Html, useSystemFonts: false);
            Check(ExtractText(pdf).Contains(Heading, StringComparison.Ordinal), "the converted text reads back");
        });

        Run("hyphenation", () =>
        {
            // Embedded deflate resources, read on first use, and the layouts that take them.
            Check(string.Join("-", PdfHyphenationPatterns.ForLanguage("de-AT")!.InsertSoftHyphens("Silbentrennung").Split((char)0xAD)) == "Sil-ben-tren-nung", "the German patterns load");
            Check(PdfHyphenationPatterns.ForLanguage("EN-gb") is not null && PdfHyphenationPatterns.ForLanguage("ru") is null, "tags resolve under invariant globalization");
            var layout = PdfTextLayout.Create(new PdfParagraph { Hyphenator = PdfHyphenationPatterns.ForLanguage("en") }.Add("hyphenation", new PdfTextStyle(PdfStandardFont.Courier, 10)), 50);
            Check(layout.Lines.Count == 2 && layout.Lines[0].End == 6, "a paragraph breaks at a hyphenation point");
            byte[] pdf = HtmlConverter.ConvertToPdf(
                "<html lang=\"fr\"><body style=\"hyphens: auto; width: 60px\">développement</body></html>",
                new HtmlConverterOptions { UseSystemFonts = false, Hyphenators = PdfHyphenationPatterns.ForLanguage });
            Check(PdfDocument.Open(pdf).Pages.Count == 1, "hyphens: auto converts");
        });

        Run("ocr types", () =>
        {
            // The OCR engine needs ONNX Runtime's native library and the models, which this app
            // does not carry; the managed side must still load and be analyzed.
            Check(OnnxOcrEngine.ModelHashes.Count == 4, "the model pins load");
            Check(new PdfOcrOptions().Languages.Count > 0, "the options load");
        });

        Console.WriteLine("All checks passed.");
        return 0;
    }

    public static int WriteInputs(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "input.pdf"), Create(pages: 3));
        File.WriteAllText(Path.Combine(directory, "input.html"), Html, Encoding.UTF8);
        return 0;
    }

    public static int Cold(string step, string directory)
    {
        // The inputs are read with the base library only, so nothing of PdfNetStack runs before the step.
        byte[] pdf = File.ReadAllBytes(Path.Combine(directory, "input.pdf"));
        string html = File.ReadAllText(Path.Combine(directory, "input.html"), Encoding.UTF8);
        Func<int> run = step switch
        {
            "open-save" => () => OpenAndSave(pdf).Length,
            "optimize" => () => Optimize(pdf, PdfOptimizerOptions.Default).Output.Length,
            "text" => () => ExtractText(pdf).Length,
            "html" => () => ConvertHtml(html, useSystemFonts: false).Length,
            "html-system-fonts" => () => ConvertHtml(html, useSystemFonts: true).Length,
            "font-text" => () => FontText(File.ReadAllBytes(Path.Combine(directory, "font.ttf"))).Length,
            _ => throw new ArgumentException("Unknown step " + step),
        };

        // The first call pays for loading, static initialization and compilation; the second is the work alone.
        (double first, long allocated) = Measure(run);
        (double second, _) = Measure(run);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{step},{first:0.0},{allocated},{second:0.0}"));
        return 0;
    }

    private static (double Milliseconds, long Allocated) Measure(Func<int> run)
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        int size = run();
        clock.Stop();
        Check(size > 0, "the step produced output");
        return (clock.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - allocated);
    }

    private static void Run(string name, Action check)
    {
        var clock = Stopwatch.StartNew();
        check();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ok   {name} ({clock.Elapsed.TotalMilliseconds:0} ms)"));
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + what);
    }

    /// <summary>A file with text in two standard fonts, vector art and a photograph-like image on each page.</summary>
    private static byte[] Create(int pages)
    {
        PdfImage image = PdfImage.Load(Gradient(480, 320));
        PdfDocument doc = PdfDocument.Create();
        for (int n = 1; n <= pages; n++)
        {
            PdfPage page = doc.Pages.Add(PdfRectangle.A4);
            using PdfGraphics g = page.CreateGraphics();
            g.DrawRectangle(null, PdfColor.FromHex("#1E3A8A"), 0, 0, page.Width, 90);
            g.DrawString(Heading, PdfStandardFont.HelveticaBold, 24, PdfColor.White, 40, 55);
            g.DrawString("Page " + n.ToString(CultureInfo.InvariantCulture), PdfStandardFont.TimesRoman, 12, PdfColor.Black, 40, 130);
            g.DrawImage(image, 40, 160, 480, 320);
        }

        doc.Info.Title = "Smoke test";
        return doc.ToArray();
    }

    private static byte[] OpenAndSave(byte[] pdf)
    {
        PdfDocument doc = PdfDocument.Open(pdf);
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("Added page", PdfStandardFont.Helvetica, 12, PdfColor.Black, 40, 60);
        }

        doc.Info.Title = "Edited";
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    private static (byte[] Output, PdfOptimizationResult Result) Optimize(byte[] pdf, PdfOptimizerOptions options)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfOptimizationResult result = PdfOptimizer.OptimizeAsync(input, output, options).GetAwaiter().GetResult();
        return (output.ToArray(), result);
    }

    private static string ExtractText(byte[] pdf)
    {
        var text = new StringBuilder();
        foreach (PdfPage page in PdfDocument.Open(pdf).Pages) text.AppendLine(page.ExtractText());
        return text.ToString();
    }

    /// <summary>A line in an embedded TrueType font: loading, shaping, subsetting. The test font has A, H, T, V, o and x.</summary>
    private static byte[] FontText(byte[] font)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("AVA To HoT TAxo", PdfOpenTypeFont.Load(font), 14, PdfColor.Black, 40, 60);
        }

        return doc.ToArray();
    }

    private static byte[] ConvertHtml(string html, bool useSystemFonts) =>
        HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = useSystemFonts });

    /// <summary>A one page file drawing あ through the predefined 90ms-RKSJ-H CMap, with no ToUnicode map.</summary>
    private static byte[] CjkPdf()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        var cidFont = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("CIDFontType0"),
            ["BaseFont"] = new PdfName("Test"),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = PdfString.FromText("Adobe"),
                ["Ordering"] = PdfString.FromText("Japan1"),
                ["Supplement"] = PdfNumber.FromInteger(0),
            },
        };
        var font = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
            ["BaseFont"] = new PdfName("Test"),
            ["Encoding"] = new PdfName("90ms-RKSJ-H"),
            ["DescendantFonts"] = PdfArray.Of(cidFont),
        };
        page.Dictionary.Set("Resources", new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = doc.AddObject(font) } });
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td <82A0> Tj ET"))));
        return doc.ToArray(new PdfSaveOptions { Optimize = false });
    }

    /// <summary>A 24 bit BMP of a smooth color gradient: photograph-like content for the lossy optimizer.</summary>
    private static byte[] Gradient(int width, int height)
    {
        int stride = (width * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * height];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), 54);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), width);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), height);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (short)24);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = 54 + y * stride + x * 3;
                bmp[at] = (byte)(255 * x / width);
                bmp[at + 1] = (byte)(128 + 127 * Math.Sin(x / 23.0) * Math.Cos(y / 17.0));
                bmp[at + 2] = (byte)(255 * y / height);
            }
        }

        return bmp;
    }
}
