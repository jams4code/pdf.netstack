using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfNetStack;
using PdfNetStack.Cli;

return await Cli.RunAsync(args);

namespace PdfNetStack.Cli
{
    internal static class Cli
    {
        private const string Usage = """
            pdfnetstack: lossless PDF optimizer

            Usage:
              pdfnetstack optimize <input.pdf> <output.pdf> [options]
              pdfnetstack batch <input-dir> [output-dir] [options]   (no output-dir: measure only)
              pdfnetstack inspect <input.pdf> [--json]
              pdfnetstack verify <original.pdf> <candidate.pdf>

            Options:
              --fast | --balanced       Faster Flate levels (default: maximum)
              --no-object-streams       Write a classic xref table, no object streams
              --no-dedup                Do not merge identical objects
              --no-recompress           Do not re-encode streams
              --no-image-predictors     Do not try PNG prediction on raw images
              --no-verify               Skip the equivalence proof (not recommended)
              --allow-incremental       Flatten files with incremental updates
              --allow-repaired          Rewrite damaged files that needed repair
              --strip-thumbnails        Remove page thumbnails (not lossless)
              --strip-pieceinfo         Remove application private data (not lossless)
              --min-saving <percent>    Minimum saving to keep a rewrite (default 1)
              --threads <n>             Threads per document (default: all cores)
              --jobs <n>                Documents processed in parallel by batch (default: all cores)
              --report <file.csv>       Batch: write one row per document
              --json                    Machine readable output
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            try
            {
                var parsed = Arguments.Parse(args.Skip(1).ToArray());
                return args[0] switch
                {
                    "optimize" when parsed.Positional.Count == 2 => await OptimizeAsync(parsed),
                    "batch" when parsed.Positional.Count is 1 or 2 => await BatchAsync(parsed),
                    "inspect" when parsed.Positional.Count == 1 => Inspect(parsed),
                    "verify" when parsed.Positional.Count == 2 => Verify(parsed),
                    _ => Fail("Unknown command or wrong number of arguments.\n\n" + Usage),
                };
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
            catch (IOException ex)
            {
                return Fail(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(ex.Message);
            }
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        private static async Task<int> OptimizeAsync(Arguments a)
        {
            PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync(a.Positional[0], a.Positional[1], a.Options);
            if (a.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CliJson.Default.PdfOptimizationResult));
            }
            else
            {
                Console.WriteLine($"{result.Disposition}: {Size(result.OriginalBytes)} -> {Size(result.OutputBytes)} " +
                                  $"({result.SavingPercent.ToString("F2", CultureInfo.InvariantCulture)}% saved) in {result.Statistics.Elapsed.TotalMilliseconds:F0} ms");
                Console.WriteLine(result.Reason);
                OptimizationStatistics s = result.Statistics;
                if (result.Disposition == OptimizationDisposition.Optimized)
                {
                    Console.WriteLine($"pages {result.PageCount}, objects {s.ObjectsInFile} -> {s.ObjectsWritten} " +
                                      $"(unreachable {s.UnreachableRemoved}, merged {s.ObjectsDeduplicated}), " +
                                      $"streams re-encoded {s.StreamsRecompressed}, objects verified {s.ObjectsVerified}");
                    PhaseTimings p = s.Phases;
                    Console.WriteLine($"analyze {p.Analyze.TotalMilliseconds:F0} ms, recompress {p.Recompress.TotalMilliseconds:F0} ms, " +
                                      $"dedup {p.Deduplicate.TotalMilliseconds:F0} ms, write {p.Write.TotalMilliseconds:F0} ms, verify {p.Verify.TotalMilliseconds:F0} ms");
                }
            }

            return 0;
        }

        private static int Inspect(Arguments a)
        {
            byte[] data = File.ReadAllBytes(a.Positional[0]);
            PdfInspection i = PdfInspector.Inspect(data);
            if (a.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(i, CliJson.Default.PdfInspection));
                return 0;
            }

            Console.WriteLine($"Size          {Size(i.FileBytes)}");
            Console.WriteLine($"Version       {i.Version}");
            Console.WriteLine($"Pages         {i.PageCount}");
            Console.WriteLine($"Objects       {i.ObjectCount} ({i.ReachableObjects} reachable)");
            Console.WriteLine($"Revisions     {i.Revisions}{(i.IsLinearized ? ", linearized" : "")}");
            Console.WriteLine($"Xref          {(i.UsesXrefStreams ? "stream" : "table")}{(i.UsesObjectStreams ? ", object streams" : "")}{(i.NeededRepair ? ", REPAIRED" : "")}");
            Console.WriteLine($"Encrypted     {i.IsEncrypted}");
            Console.WriteLine($"Signed        {i.IsSigned}");
            Console.WriteLine($"PDF/A-1       {i.IsPdfA1}");
            Console.WriteLine();
            Console.WriteLine("Streams by kind:");
            foreach ((string kind, StreamBucket b) in i.StreamsByKind.OrderByDescending(p => p.Value.Bytes))
            {
                Console.WriteLine($"  {kind,-16} {b.Count,7}  {Size(b.Bytes),10}  {100.0 * b.Bytes / i.FileBytes,5:F1}%");
            }

            Console.WriteLine("Filters:");
            foreach ((string f, int count) in i.FilterUsage.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"  {f,-32} {count,7}");
            }

            return 0;
        }

        private static int Verify(Arguments a)
        {
            string? difference = PdfEquivalence.FindDifference(File.ReadAllBytes(a.Positional[0]), File.ReadAllBytes(a.Positional[1]));
            Console.WriteLine(difference is null ? "Equivalent." : "Different: " + difference);
            return difference is null ? 0 : 1;
        }

        private static async Task<int> BatchAsync(Arguments a)
        {
            string inputDir = Path.GetFullPath(a.Positional[0]);
            string? outputDir = a.Positional.Count > 1 ? Path.GetFullPath(a.Positional[1]) : null;
            string[] files = Directory.EnumerateFiles(inputDir, "*.pdf", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => outputDir is null || !Path.GetFullPath(f).StartsWith(outputDir, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            PdfOptimizerOptions options = a.Options with { MaxDegreeOfParallelism = a.ThreadsExplicit ? a.Options.MaxDegreeOfParallelism : 1 };
            var rows = new BatchRow[files.Length];
            var clock = Stopwatch.StartNew();
            int done = 0;

            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions { MaxDegreeOfParallelism = a.Jobs }, async (i, ct) =>
            {
                string file = files[i];
                string relative = Path.GetRelativePath(inputDir, file);
                var started = Stopwatch.StartNew();
                try
                {
                    PdfOptimizationResult r;
                    if (outputDir is null)
                    {
                        byte[] data = await File.ReadAllBytesAsync(file, ct);
                        r = PdfOptimizer.Optimize(data, Stream.Null, options, ct);
                    }
                    else
                    {
                        r = await PdfOptimizer.OptimizeFileAsync(file, Path.Combine(outputDir, relative), options, ct);
                    }

                    rows[i] = new BatchRow(relative, r.Disposition.ToString(), r.OriginalBytes, r.OutputBytes, r.PageCount, started.Elapsed.TotalMilliseconds, r.Reason);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rows[i] = new BatchRow(relative, "Error", 0, 0, 0, started.Elapsed.TotalMilliseconds, ex.Message);
                }

                int n = Interlocked.Increment(ref done);
                if (!a.Json && (n % 25 == 0 || n == files.Length)) Console.Error.Write($"\r{n}/{files.Length}");
            });

            if (!a.Json) Console.Error.WriteLine();
            TimeSpan elapsed = clock.Elapsed;
            long before = rows.Sum(r => r.OriginalBytes);
            long after = rows.Sum(r => r.OutputBytes);
            var summary = new BatchSummary(
                files.Length,
                rows.Count(r => r.Disposition == nameof(OptimizationDisposition.Optimized)),
                rows.Count(r => r.Disposition == nameof(OptimizationDisposition.OriginalRetained)),
                rows.Count(r => r.Disposition == nameof(OptimizationDisposition.UnsupportedRetained)),
                rows.Count(r => r.Disposition == "Error"),
                before,
                after,
                before == 0 ? 0 : 100.0 * (before - after) / before,
                elapsed.TotalSeconds,
                elapsed.TotalSeconds == 0 ? 0 : before / 1048576.0 / elapsed.TotalSeconds,
                Process.GetCurrentProcess().PeakWorkingSet64,
                rows.Where(r => r.Reason.StartsWith("Verification failed", StringComparison.Ordinal) || r.Reason.StartsWith("Rewrite failed", StringComparison.Ordinal))
                    .Select(r => r.File + ": " + r.Reason).ToArray(),
                rows.Where(r => r.Disposition == nameof(OptimizationDisposition.UnsupportedRetained))
                    .GroupBy(r => ReasonCategory(r.Reason)).ToDictionary(g => g.Key, g => g.Count()));

            if (a.ReportPath is not null) WriteCsv(a.ReportPath, rows);

            if (a.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(summary, CliJson.Default.BatchSummary));
            }
            else
            {
                Console.WriteLine($"Documents     {summary.Documents}");
                Console.WriteLine($"Optimized     {summary.Optimized}");
                Console.WriteLine($"Retained      {summary.Retained} (saving below threshold or failed checks)");
                Console.WriteLine($"Unsupported   {summary.Unsupported}");
                foreach ((string reason, int count) in summary.UnsupportedReasons.OrderByDescending(p => p.Value))
                {
                    Console.WriteLine($"  {count,5}  {reason}");
                }

                Console.WriteLine($"Errors        {summary.Errors}");
                Console.WriteLine($"Size          {Size(before)} -> {Size(after)} ({summary.SavingPercent:F2}% saved)");
                Console.WriteLine($"Time          {summary.Seconds:F1} s ({summary.MegabytesPerSecond:F1} MB/s)");
                Console.WriteLine($"Peak memory   {Size(summary.PeakWorkingSetBytes)}");
                if (summary.Failures.Length > 0)
                {
                    Console.WriteLine($"Failures      {summary.Failures.Length} (original kept)");
                    foreach (string f in summary.Failures.Take(50)) Console.WriteLine("  " + f);
                }
            }

            return 0;
        }

        private static string ReasonCategory(string reason)
        {
            int cut = reason.IndexOfAny(['.', ':', '(']);
            return cut > 0 ? reason[..cut] : reason;
        }

        private static void WriteCsv(string path, BatchRow[] rows)
        {
            var sb = new StringBuilder("file,disposition,original_bytes,output_bytes,pages,milliseconds,reason\n");
            foreach (BatchRow r in rows)
            {
                sb.Append(Csv(r.File)).Append(',').Append(r.Disposition).Append(',')
                  .Append(r.OriginalBytes).Append(',').Append(r.OutputBytes).Append(',').Append(r.PageCount).Append(',')
                  .Append(r.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(',').Append(Csv(r.Reason)).Append('\n');
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        internal static string Size(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MiB",
            >= 1L << 10 => $"{bytes / 1024.0:F1} KiB",
            _ => $"{bytes} B",
        };
    }

    internal sealed record BatchRow(string File, string Disposition, long OriginalBytes, long OutputBytes, int PageCount, double Milliseconds, string Reason);

    internal sealed record BatchSummary(
        int Documents,
        int Optimized,
        int Retained,
        int Unsupported,
        int Errors,
        long OriginalBytes,
        long OutputBytes,
        double SavingPercent,
        double Seconds,
        double MegabytesPerSecond,
        long PeakWorkingSetBytes,
        string[] Failures,
        Dictionary<string, int> UnsupportedReasons);

    internal sealed class Arguments
    {
        public List<string> Positional { get; } = [];

        public PdfOptimizerOptions Options { get; private set; } = PdfOptimizerOptions.Default;

        public bool Json { get; private set; }

        public bool ThreadsExplicit { get; private set; }

        public int Jobs { get; private set; } = Environment.ProcessorCount;

        public string? ReportPath { get; private set; }

        public static Arguments Parse(string[] args)
        {
            var a = new Arguments();
            PdfOptimizerOptions o = PdfOptimizerOptions.Default;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--json": a.Json = true; break;
                    case "--fast": o = o with { Compression = CompressionEffort.Fast }; break;
                    case "--balanced": o = o with { Compression = CompressionEffort.Balanced }; break;
                    case "--no-object-streams": o = o with { UseObjectStreams = false }; break;
                    case "--no-dedup": o = o with { DeduplicateObjects = false }; break;
                    case "--no-recompress": o = o with { RecompressStreams = false }; break;
                    case "--no-image-predictors": o = o with { UseImagePredictors = false }; break;
                    case "--no-verify": o = o with { Verify = false }; break;
                    case "--allow-incremental": o = o with { AllowIncrementalUpdates = true }; break;
                    case "--allow-repaired": o = o with { AllowRepairedFiles = true }; break;
                    case "--strip-thumbnails": o = o with { RemovePageThumbnails = true }; break;
                    case "--strip-pieceinfo": o = o with { RemovePieceInfo = true }; break;
                    case "--min-saving": o = o with { MinSavingPercent = double.Parse(Value(args, ref i), CultureInfo.InvariantCulture) }; break;
                    case "--threads":
                        o = o with { MaxDegreeOfParallelism = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture) };
                        a.ThreadsExplicit = true;
                        break;
                    case "--jobs": a.Jobs = Math.Max(1, int.Parse(Value(args, ref i), CultureInfo.InvariantCulture)); break;
                    case "--report": a.ReportPath = Value(args, ref i); break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown option " + arg);
                        a.Positional.Add(arg);
                        break;
                }
            }

            a.Options = o;
            return a;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value.");
            return args[++i];
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(JsonStringEnumConverter<OptimizationDisposition>)])]
    [JsonSerializable(typeof(PdfOptimizationResult))]
    [JsonSerializable(typeof(PdfInspection))]
    [JsonSerializable(typeof(BatchSummary))]
    internal sealed partial class CliJson : JsonSerializerContext;
}
