using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PdfNetStack;
using PdfNetStack.Benchmarks;
using PdfNetStack.Html;

// dotnet run -c Release -- quick [extract|letters|html] [seconds]   a stopwatch loop, for profiling
// dotnet run -c Release -- cold                             first HTML conversions in a fresh process
// dotnet run -c Release -- samples <dir>                   writes the sample documents
// dotnet run -c Release -- [BenchmarkDotNet arguments]     e.g. --filter *
if (args.Length > 0 && args[0] == "quick")
{
    string what = args.Length > 1 ? args[1] : "all";
    double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5;
    var bench = new ExtractionBenchmarks();
    bench.Setup();
    var html = new HtmlBenchmarks();
    if (what is "all" or "extract") Quick("ExtractText, 60 pages", () => bench.ExtractText(), seconds);
    if (what is "all" or "letters") Quick("GetLetters, 60 pages", () => bench.GetLetters(), seconds);
    if (what is "all" or "html") Quick("HtmlConverter invoice", () => html.Invoice(), seconds);
    if (what is "all" or "html") Quick("HtmlConverter report", () => html.Report(), seconds);
    return;
}

if (args.Length > 0 && args[0] == "cold")
{
    // The first conversion in a process, then the next ones: what a command line run pays.
    var html = new HtmlBenchmarks();
    for (int i = 0; i < 3; i++)
    {
        var sw = Stopwatch.StartNew();
        html.Invoice();
        Console.Write($"invoice {sw.Elapsed.TotalMilliseconds:F0} ms; ");
        sw.Restart();
        html.Report();
        Console.WriteLine($"report {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    return;
}

if (args.Length > 1 && args[0] == "samples")
{
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "report-60-pages.pdf"), Samples.TextReport());
    File.WriteAllText(Path.Combine(args[1], "invoice.html"), Samples.Invoice());
    File.WriteAllText(Path.Combine(args[1], "report.html"), Samples.Report());
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
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        run();
        times.Add(sw.Elapsed.TotalMilliseconds);
        allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
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

    /// <summary>HTML to PDF for a one page invoice and a one page report.</summary>
    [MemoryDiagnoser]
    public class HtmlBenchmarks
    {
        private static readonly string InvoiceHtml = Samples.Invoice();
        private static readonly string ReportHtml = Samples.Report();

        [Benchmark]
        public byte[] Invoice() => HtmlConverter.ConvertToPdf(InvoiceHtml);

        [Benchmark]
        public byte[] Report() => HtmlConverter.ConvertToPdf(ReportHtml);
    }
}
