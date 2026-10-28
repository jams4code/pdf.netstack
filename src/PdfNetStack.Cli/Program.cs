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

            Editing:
              pdfnetstack merge <output.pdf> <input.pdf>...
              pdfnetstack split <input.pdf> <output-dir>
              pdfnetstack extract <input.pdf> <output.pdf> <pages>      e.g. 1-3,7,9-  (order kept, repeats allowed)
              pdfnetstack rotate <input.pdf> <output.pdf> <degrees> [pages]
              pdfnetstack meta <input.pdf> [output.pdf] [--title t] [--author a] [--subject s] [--keywords k]

            Security:
              pdfnetstack encrypt <input.pdf> <output.pdf> --user-password u --owner-password o
                                  [--allow print,copy,modify,annotate,forms,accessibility,assemble,print-hq] [--aes128]
              pdfnetstack decrypt <input.pdf> <output.pdf> --password <owner password>
              Every editing command accepts --password to open an encrypted input.

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
              --no-optimize             Editing: save without the optimizer pass
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
                    "merge" when parsed.Positional.Count >= 2 => Merge(parsed),
                    "split" when parsed.Positional.Count == 2 => Split(parsed),
                    "extract" when parsed.Positional.Count == 3 => Extract(parsed),
                    "rotate" when parsed.Positional.Count is 3 or 4 => Rotate(parsed),
                    "meta" when parsed.Positional.Count is 1 or 2 => Meta(parsed),
                    "encrypt" when parsed.Positional.Count == 2 => Encrypt(parsed),
                    "decrypt" when parsed.Positional.Count == 2 => Decrypt(parsed),
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
            catch (PdfFormatException ex)
            {
                return Fail("Not a readable PDF: " + ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return Fail(ex.Message);
            }
            catch (PdfPasswordException ex)
            {
                return Fail(ex.Message + " Pass it with --password.");
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message);
            }
            catch (FormatException ex)
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

        private static PdfSaveOptions SaveOptions(Arguments a) => new() { Optimize = !a.NoOptimize, Compression = a.Options.Compression };

        private static int Saved(PdfDocument doc, string path, Arguments a)
        {
            doc.Save(path, SaveOptions(a));
            Console.WriteLine($"{path}: {doc.Pages.Count} page(s), {Size(new FileInfo(path).Length)}");
            return 0;
        }

        private static int Merge(Arguments a)
        {
            var inputs = a.Positional.Skip(1).Select(path => PdfDocument.Open(path, a.Password)).ToList();
            return Saved(PdfDocument.Merge(inputs), a.Positional[0], a);
        }

        private static int Split(Arguments a)
        {
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            string name = Path.GetFileNameWithoutExtension(a.Positional[0]);
            int width = doc.Pages.Count.ToString(CultureInfo.InvariantCulture).Length;
            int i = 0;
            foreach (PdfDocument page in doc.Split())
            {
                i++;
                page.Save(Path.Combine(a.Positional[1], $"{name}-{i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}.pdf"), SaveOptions(a));
            }

            Console.WriteLine($"{i} file(s) written to {a.Positional[1]}");
            return 0;
        }

        private static int Extract(Arguments a)
        {
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            List<int> pages = Editing.ParseRanges(a.Positional[2], doc.Pages.Count);
            PdfDocument result = PdfDocument.Create();
            result.Pages.Import(pages.Select(i => doc.Pages[i]));
            return Saved(result, a.Positional[1], a);
        }

        private static int Rotate(Arguments a)
        {
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            int degrees = int.Parse(a.Positional[2], CultureInfo.InvariantCulture);
            IEnumerable<int> pages = a.Positional.Count == 4
                ? Editing.ParseRanges(a.Positional[3], doc.Pages.Count).Distinct()
                : Enumerable.Range(0, doc.Pages.Count);
            foreach (int i in pages) doc.Pages[i].Rotate(degrees);
            return Saved(doc, a.Positional[1], a);
        }

        private static int Encrypt(Arguments a)
        {
            if (a.UserPassword is null && a.OwnerPassword is null) return Fail("Give --user-password and/or --owner-password.");
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            doc.Security.Encrypt(a.UserPassword ?? "", a.OwnerPassword ?? "", a.Permissions, a.Aes128 ? PdfEncryption.Aes128 : PdfEncryption.Aes256);
            return Saved(doc, a.Positional[1], a);
        }

        private static int Decrypt(Arguments a)
        {
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            doc.Security.RemoveEncryption();
            return Saved(doc, a.Positional[1], a);
        }

        private static int Meta(Arguments a)
        {
            PdfDocument doc = PdfDocument.Open(a.Positional[0], a.Password);
            foreach ((string key, string value) in a.Metadata) doc.Info[key] = value;
            if (a.Positional.Count == 2) return Saved(doc, a.Positional[1], a);
            if (a.Metadata.Count > 0) return Fail("Give an output file to write metadata changes.");

            Console.WriteLine($"Pages         {doc.Pages.Count}");
            foreach (string key in doc.Info.Keys) Console.WriteLine($"{Printable(key),-13} {Printable(doc.Info[key])}");
            if (doc.Outlines.Count > 0) Console.WriteLine($"Bookmarks     {doc.Outlines.Count} top level");
            return 0;
        }

        /// <summary>
        /// Text from a document is untrusted. Control, C1 and bidirectional override characters are
        /// shown as escapes so a crafted title cannot drive the terminal.
        /// </summary>
        internal static string Printable(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                bool unsafeChar = char.IsControl(c) || c is >= '\u0080' and <= '\u009F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C';
                if (unsafeChar) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }

            return sb.ToString();
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

        // Reasons and file names come from untrusted documents. A leading formula character is
        // neutralized so spreadsheet apps never evaluate a cell.
        private static string Csv(string s)
        {
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        internal static string Size(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MiB",
            >= 1L << 10 => $"{bytes / 1024.0:F1} KiB",
            _ => $"{bytes} B",
        };
    }

    internal static class Editing
    {
        /// <summary>Parses 1-based page ranges such as "1-3,7,9-" against a page count.</summary>
        public static List<int> ParseRanges(string spec, int pageCount)
        {
            var result = new List<int>();
            foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] bounds = raw.Split('-');
                if (bounds.Length > 2) throw new FormatException($"Invalid page range '{raw}'.");
                int from = bounds[0].Length == 0 ? 1 : Page(bounds[0], pageCount);
                int to = bounds.Length == 1 ? from : bounds[1].Length == 0 ? pageCount : Page(bounds[1], pageCount);
                int step = from <= to ? 1 : -1;
                for (int p = from; p != to + step; p += step) result.Add(p - 1);
            }

            if (result.Count == 0) throw new FormatException("No pages selected.");
            return result;
        }

        private static int Page(string text, int pageCount)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 1 || page > pageCount)
            {
                throw new FormatException($"Page '{text}' is outside 1-{pageCount}.");
            }

            return page;
        }
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

        public bool NoOptimize { get; private set; }

        public List<(string Key, string Value)> Metadata { get; } = [];

        public string? Password { get; private set; }

        public string? UserPassword { get; private set; }

        public string? OwnerPassword { get; private set; }

        public PdfPermissions Permissions { get; private set; } = PdfPermissions.All;

        public bool Aes128 { get; private set; }

        private static PdfPermissions ParsePermissions(string list)
        {
            PdfPermissions result = PdfPermissions.None;
            foreach (string item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result |= item switch
                {
                    "print" => PdfPermissions.Print,
                    "print-hq" => PdfPermissions.PrintHighQuality | PdfPermissions.Print,
                    "copy" => PdfPermissions.CopyContent,
                    "modify" => PdfPermissions.Modify,
                    "annotate" => PdfPermissions.Annotate,
                    "forms" => PdfPermissions.FillForms,
                    "accessibility" => PdfPermissions.ExtractForAccessibility,
                    "assemble" => PdfPermissions.Assemble,
                    "none" => PdfPermissions.None,
                    _ => throw new ArgumentException($"Unknown permission '{item}'."),
                };
            }

            return result;
        }

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
                    case "--no-optimize": a.NoOptimize = true; break;
                    case "--password": a.Password = Value(args, ref i); break;
                    case "--user-password": a.UserPassword = Value(args, ref i); break;
                    case "--owner-password": a.OwnerPassword = Value(args, ref i); break;
                    case "--allow": a.Permissions = ParsePermissions(Value(args, ref i)); break;
                    case "--aes128": a.Aes128 = true; break;
                    case "--title": a.Metadata.Add(("Title", Value(args, ref i))); break;
                    case "--author": a.Metadata.Add(("Author", Value(args, ref i))); break;
                    case "--subject": a.Metadata.Add(("Subject", Value(args, ref i))); break;
                    case "--keywords": a.Metadata.Add(("Keywords", Value(args, ref i))); break;
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
