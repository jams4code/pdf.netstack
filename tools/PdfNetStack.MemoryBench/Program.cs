using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfNetStack;

// dotnet run -c Release --project tools/PdfNetStack.MemoryBench -- <work-dir> [pages]
//     Generates <work-dir>/input.pdf (about 0.7 MB a page, 400 pages by default) unless it is
//     there already, then runs each scenario twice in a fresh process: once timed, once with
//     live memory sampled. Delete the folder afterwards; nothing in it belongs in the repository.
// dotnet run -c Release --project tools/PdfNetStack.MemoryBench -- run <scenario> <input> <output> <sample>
//     One scenario in this process: save-buffered, save-progressive, optimize-memory, optimize-streaming.
//
// Live memory is GC.GetTotalMemory after a forced full collection, sampled every 200 ms on a
// thread of its own, less what was live before the run. The forced collections slow the run, so
// times come from the run without sampling, which reports the process's peak working set instead.
if (args.Length >= 5 && args[0] == "run") return Run(args[1], args[2], args[3], args[4] == "sample");
if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: MemoryBench <work-dir> [pages] | run <scenario> <input> <output> <sample|time>");
    return 2;
}

string work = Path.GetFullPath(args[0]);
int pages = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 400;
Directory.CreateDirectory(work);
string input = Path.Combine(work, "input.pdf");
if (!File.Exists(input)) Generate(input, pages);
Console.WriteLine($"input: {new FileInfo(input).Length / 1e6:F1} MB, {pages} pages");
Console.WriteLine($"{"scenario",-20} {"time s",8} {"peak WS MB",11} {"peak live MB",13} {"allocated MB",13} {"output MB",10}");
foreach (string scenario in new[] { "save-buffered", "save-progressive", "optimize-memory", "optimize-streaming" })
{
    string output = Path.Combine(work, scenario + ".pdf");
    string[] timed = Child(scenario, input, output, "time");
    string[] sampled = Child(scenario, input, output, "sample");
    Console.WriteLine($"{scenario,-20} {timed[0],8} {timed[1],11} {sampled[2],13} {timed[3],13} {timed[4],10}  {timed[5]}");
}

return 0;

static string[] Child(string scenario, string input, string output, string mode)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };

    // Started as "dotnet MemoryBench.dll", the host needs the assembly again.
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(Marker).Assembly.Location);
    foreach (string a in new[] { "run", scenario, input, output, mode }) start.ArgumentList.Add(a);
    using Process process = Process.Start(start)!;
    // Only the line break goes: an empty last field still counts.
    string line = process.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
    process.WaitForExit();
    string[] fields = line.Split('\t');
    return fields.Length >= 6 ? fields : throw new InvalidOperationException(scenario + ": " + line);
}

static int Run(string scenario, string input, string output, bool sample)
{
    long baseline = GC.GetTotalMemory(forceFullCollection: true);
    long peak = baseline;
    using var done = new ManualResetEventSlim();
    Thread? sampler = null;
    if (sample)
    {
        sampler = new Thread(() =>
        {
            while (!done.Wait(200))
            {
                long now = GC.GetTotalMemory(forceFullCollection: true);
                if (now > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, now);
            }
        })
        { IsBackground = true };
        sampler.Start();
    }

    var clock = Stopwatch.StartNew();
    string outcome = "";
    switch (scenario)
    {
        case "save-buffered":
        {
            // What Save(Stream) did before 1.1: the whole file built in memory, then written.
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read);
            PdfDocument doc = PdfDocument.OpenOnDemand(source);
            using var target = new FileStream(output, FileMode.Create, FileAccess.Write);
            target.Write(doc.ToArray(new PdfSaveOptions { Optimize = false }));
            break;
        }

        case "save-progressive":
        {
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read);
            PdfDocument doc = PdfDocument.OpenOnDemand(source);
            using var target = new FileStream(output, FileMode.Create, FileAccess.Write);
            doc.Save(target, new PdfSaveOptions { Optimize = false });
            break;
        }

        case "optimize-memory":
        case "optimize-streaming":
        {
            var options = new PdfOptimizerOptions { MaxInputBytes = long.MaxValue, Streaming = scenario == "optimize-streaming" };
            PdfOptimizationResult result = PdfOptimizer.OptimizeFileAsync(input, output, options).GetAwaiter().GetResult();
            outcome = $"{result.Disposition}, {result.SavingPercent:F1}% saved, {result.Statistics.ObjectsDeduplicated} merged, {result.Statistics.ObjectsVerified} verified";
            break;
        }

        default:
            throw new ArgumentException("Unknown scenario " + scenario);
    }

    TimeSpan elapsed = clock.Elapsed;
    done.Set();
    sampler?.Join();
    long allocated = GC.GetTotalAllocatedBytes(precise: true);
    long workingSet = Process.GetCurrentProcess().PeakWorkingSet64;
    Console.WriteLine(string.Join('\t',
        elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
        (workingSet / 1e6).ToString("F0", CultureInfo.InvariantCulture),
        sample ? ((peak - baseline) / 1e6).ToString("F0", CultureInfo.InvariantCulture) : "",
        (allocated / 1e6).ToString("F0", CultureInfo.InvariantCulture),
        (new FileInfo(output).Length / 1e6).ToString("F1", CultureInfo.InvariantCulture),
        outcome));
    return 0;
}

// A classic file written straight to disk: one image a page, a quarter of them noise that does
// not compress, a quarter smooth pictures under fast Flate, a quarter the same stored raw, and a
// quarter repeating the previous page's image as an object of its own.
static void Generate(string path, int pages)
{
    const int side = 480;
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
    var offsets = new List<long> { 0 };
    void Text(string s) => file.Write(Encoding.Latin1.GetBytes(s));
    void Begin(int number)
    {
        while (offsets.Count <= number) offsets.Add(0);
        offsets[number] = file.Position;
        Text($"{number} 0 obj\n");
    }

    void Stream(int number, string dictionary, byte[] data)
    {
        Begin(number);
        Text($"<<{dictionary}/Length {data.Length}>>stream\n");
        file.Write(data);
        Text("\nendstream\nendobj\n");
    }

    Text("%PDF-1.4\n%âãÏÓ\n");
    var random = new Random(11);
    var kids = new StringBuilder();
    byte[] previous = [];
    string previousFilter = "";
    const int first = 3;
    for (int i = 0; i < pages; i++)
    {
        int page = first + (4 * i), content = page + 1, font = page + 2, image = page + 3;
        kids.Append(page).Append(" 0 R ");
        byte[] data;
        string filter = "";
        switch (i % 4)
        {
            case 0:
                data = new byte[side * side * 3];
                random.NextBytes(data);
                break;
            case 1:
                data = Deflate(Picture(side, i));
                filter = "/Filter/FlateDecode";
                break;
            case 2:
                data = Picture(side, i);
                break;
            default:
                data = previous;
                filter = previousFilter;
                break;
        }

        previous = data;
        previousFilter = filter;
        Begin(page);
        Text($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents {content} 0 R/Resources<</Font<</F1 {font} 0 R>>/XObject<</Im0 {image} 0 R>>>>>>\nendobj\n");
        Stream(content, "", Encoding.ASCII.GetBytes($"q 432 0 0 432 90 300 cm /Im0 Do Q\nBT /F1 18 Tf 90 200 Td (Generated page {i + 1}) Tj ET\n"));
        Begin(font);
        Text("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>\nendobj\n");
        Stream(image, $"/Type/XObject/Subtype/Image/Width {side}/Height {side}/ColorSpace/DeviceRGB/BitsPerComponent 8{filter}", data);
    }

    Begin(1);
    Text("<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
    Begin(2);
    Text($"<</Type/Pages/Kids[{kids}]/Count {pages}>>\nendobj\n");
    long xref = file.Position;
    Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
    for (int n = 1; n < offsets.Count; n++) Text($"{offsets[n]:D10} 00000 n \n");
    Text($"trailer\n<</Size {offsets.Count}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
}

// Smooth color fields with a little noise, like a photograph, different on every page.
static byte[] Picture(int side, int seed)
{
    var random = new Random(seed);
    var samples = new byte[side * side * 3];
    for (int y = 0; y < side; y++)
    {
        for (int x = 0; x < side; x++)
        {
            double u = (double)x / side, v = (double)y / side;
            int o = ((y * side) + x) * 3;
            samples[o] = (byte)Math.Clamp(128 + (90 * Math.Sin((u * 7) + (v * 3) + seed)) + random.Next(8), 0, 255);
            samples[o + 1] = (byte)Math.Clamp(40 + (170 * u * v) + random.Next(8), 0, 255);
            samples[o + 2] = (byte)Math.Clamp(200 - (150 * v) + random.Next(8), 0, 255);
        }
    }

    return samples;
}

static byte[] Deflate(byte[] data)
{
    using var output = new MemoryStream();
    using (var z = new ZLibStream(output, CompressionLevel.Fastest)) z.Write(data);
    return output.ToArray();
}

/// <summary>Names this assembly.</summary>
internal sealed class Marker;
