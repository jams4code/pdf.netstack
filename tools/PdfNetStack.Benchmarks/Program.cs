using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PdfNetStack;
using PdfNetStack.Benchmarks;
using PdfNetStack.Html;

// dotnet run -c Release -- quick [extract|letters|html|<sample>] [seconds]   a stopwatch loop, for profiling
// dotnet run -c Release -- cold [<sample>]                 first HTML conversions in a fresh process
// dotnet run -c Release -- samples <dir>                   writes the sample documents and their PDFs
// dotnet run -c Release -- [BenchmarkDotNet arguments]     e.g. --filter *
// Samples: invoice, report, long-invoice, rich-report, long-text.
if (args.Length > 0 && args[0] == "quick")
{
    string what = args.Length > 1 ? args[1] : "all";
    double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5;
    var bench = new ExtractionBenchmarks();
    bench.Setup();
    if (what is "all" or "extract") Quick("ExtractText, 60 pages", () => bench.ExtractText(), seconds);
    if (what is "all" or "letters") Quick("GetLetters, 60 pages", () => bench.GetLetters(), seconds);
    foreach ((string name, string html) in HtmlBenchmarks.Documents)
    {
        if (what is "all" or "html" || what == name) Quick("HtmlConverter " + name, () => HtmlConverter.ConvertToPdf(html), seconds);
    }

    return;
}

if (args.Length > 0 && args[0] == "cold")
{
    // The first conversion in a process, then the next ones: what a command line run pays.
    string? only = args.Length > 1 ? args[1] : null;
    for (int i = 0; i < 3; i++)
    {
        foreach ((string name, string html) in HtmlBenchmarks.Documents)
        {
            if (only is not null && only != name) continue;
            var sw = Stopwatch.StartNew();
            HtmlConverter.ConvertToPdf(html);
            Console.Write($"{name} {sw.Elapsed.TotalMilliseconds:F0} ms; ");
        }

        Console.WriteLine();
    }

    return;
}

if (args.Length > 1 && args[0] == "samples")
{
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "report-60-pages.pdf"), Samples.TextReport());
    foreach ((string name, string html) in HtmlBenchmarks.Documents)
    {
        File.WriteAllText(Path.Combine(args[1], name + ".html"), html);
        byte[] pdf = HtmlConverter.ConvertToPdf(html);
        File.WriteAllBytes(Path.Combine(args[1], name + ".pdf"), pdf);
        Console.WriteLine($"{name}: {PdfDocument.Open(pdf).Pages.Count} pages, {pdf.Length / 1024} KB");
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Samples).Assembly).Run(args);

static void Quick(string name, Func<object> run, double seconds)
{
    run();
    var times = new List<double>();
    var cpu = new List<double>();
    long allocated = 0;
    Process process = Process.GetCurrentProcess();
    var total = Stopwatch.StartNew();
    while (total.Elapsed.TotalSeconds < seconds || times.Count < 3)
    {
        process.Refresh();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long bytes = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        run();
        times.Add(sw.Elapsed.TotalMilliseconds);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - bytes;
        process.Refresh();
        cpu.Add((process.TotalProcessorTime - cpuStart).TotalMilliseconds);
    }

    times.Sort();
    cpu.Sort();
    Console.WriteLine($"{name}: median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms, CPU median {cpu[cpu.Count / 2]:F0} ms, {allocated / 1048576.0:F1} MB allocated, {times.Count} runs");
}

namespace PdfNetStack.Benchmarks
{
    /// <summary>Text extraction from a 60 page, 27,000 word report in an embedded TrueType font.</summary>
    [MemoryDiagnoser]
    public class ExtractionBenchmarks
    {
        private byte[] _pdf = [];

        [GlobalSetup]
        public void Setup() => _pdf = Samples.TextReport();

        /// <summary>Opens the file and reads every page's text, as a caller would.</summary>
        [Benchmark]
        public int ExtractText()
        {
            PdfDocument doc = PdfDocument.Open(_pdf);
            int length = 0;
            foreach (PdfPage page in doc.Pages) length += page.ExtractText().Length;
            return length;
        }

        /// <summary>Letters only, without layout.</summary>
        [Benchmark]
        public int GetLetters()
        {
            PdfDocument doc = PdfDocument.Open(_pdf);
            int count = 0;
            foreach (PdfPage page in doc.Pages) count += page.GetLetters().Count;
            return count;
        }
    }

    /// <summary>
    /// HTML to PDF: a one page invoice and report, a 20 page invoice with a long table, a report
    /// with lists, images, SVG and a grid, and a 50 page text.
    /// </summary>
    [MemoryDiagnoser]
    public class HtmlBenchmarks
    {
        /// <summary>The sample documents by name, generated once.</summary>
        public static readonly IReadOnlyList<(string Name, string Html)> Documents =
        [
            ("invoice", Samples.Invoice()),
            ("report", Samples.Report()),
            ("long-invoice", Samples.LongInvoice()),
            ("rich-report", Samples.RichReport()),
            ("long-text", Samples.LongText()),
        ];

        [Benchmark]
        public byte[] Invoice() => HtmlConverter.ConvertToPdf(Documents[0].Html);

        [Benchmark]
        public byte[] Report() => HtmlConverter.ConvertToPdf(Documents[1].Html);

        [Benchmark]
        public byte[] LongInvoice() => HtmlConverter.ConvertToPdf(Documents[2].Html);

        [Benchmark]
        public byte[] RichReport() => HtmlConverter.ConvertToPdf(Documents[3].Html);

        [Benchmark]
        public byte[] LongText() => HtmlConverter.ConvertToPdf(Documents[4].Html);
    }
}
