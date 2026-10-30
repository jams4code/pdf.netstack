using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using PdfNetStack;
using PdfNetStack.Cli;
using PdfNetStack.Ocr;
using static PdfNetStack.Cli.ConsoleText;

return OcrCli.Run(args);

namespace PdfNetStack.Cli
{
    internal static class OcrCli
    {
        private const string Usage = """
            pdfnetstack-ocr: make scanned PDF files searchable

            Usage:
              pdfnetstack-ocr <input.pdf> <output.pdf> [options]
              pdfnetstack ocr <input.pdf> <output.pdf> [options]   (the same, through PdfNetStack.Tool)

            The scan on each page is read and its words are laid over it as invisible text: the page
            looks the same, and now selects, copies and searches. Pages that already show text are
            left as they are.

            Options:
              --languages <list>        Scripts to read, comma separated: latin, arabic (Arabic also
                                        reads Persian and Urdu). Default: latin
              --models <folder>         Load the models from this folder instead of the ones
                                        installed with the tool
              --min-confidence <0-1>    Leave out lines read with less confidence (default 0.5)
              --no-orientation          Do not look for pages scanned sideways or upside down
              --all-pages               Also read pages that already show text
              --threads <n>             Pages recognized at once (default: all cores)
              --text <file.txt>         Also write the text found, pages apart by a blank line
              --password <password>     Open an encrypted input
              --no-optimize             Save without the optimizer pass
              --json                    Machine readable report

            Exit codes: 0 done, 1 written but some pages failed, 2 nothing written.
            """;

        public static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler stop = (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
            Console.CancelKeyPress += stop;
            try
            {
                OcrArguments a = OcrArguments.Parse(args);
                if (a.Positional.Count != 2) return Fail("Give an input and an output file.\n\n" + Usage);
                return Ocr(a, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return Fail("Cancelled; nothing was written.");
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
            catch (PdfPasswordException ex)
            {
                return Fail(ex.Message + " Pass it with --password.");
            }
            catch (InvalidDataException ex)
            {
                return Fail(ex.Message);
            }
            catch (FormatException ex)
            {
                return Fail(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return Fail(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message);
            }
            catch (OnnxRuntimeException ex)
            {
                return Fail("ONNX Runtime failed: " + ex.Message);
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
                return Fail("ONNX Runtime has no native build for this platform (Windows, Linux and macOS on x64 and arm64, except macOS x64): " + ex.Message);
            }
            finally
            {
                Console.CancelKeyPress -= stop;
            }
        }

        private static int Fail(string message)
        {
            // Reasons can quote names taken from the document; never let them drive the terminal.
            Console.Error.WriteLine(Printable(message).Replace(@"\u000A", "\n", StringComparison.Ordinal));
            return 2;
        }

        private static int Ocr(OcrArguments a, CancellationToken cancellationToken)
        {
            string input = a.Positional[0], output = a.Positional[1];

            // The document first: a file that does not open fails before the models load.
            PdfDocument doc = PdfDocument.Open(input, a.Password);
            var engineOptions = new OnnxOcrEngineOptions { Languages = a.Languages };
            using OnnxOcrEngine engine = a.Models is null ? OnnxOcrEngine.Load(engineOptions) : OnnxOcrEngine.Load(a.Models, engineOptions);
            var options = new PdfOcrOptions
            {
                Languages = a.Languages,
                MinimumConfidence = a.MinimumConfidence,
                DetectOrientation = a.DetectOrientation,
                SkipPagesWithText = !a.AllPages,
                MaxDegreeOfParallelism = a.Threads,
            };
            PdfOcrReport report = new PdfOcr(engine, options).MakeSearchable(doc, cancellationToken);

            doc.Save(output, new PdfSaveOptions { Optimize = !a.NoOptimize });
            if (a.TextPath is not null) File.WriteAllText(a.TextPath, report.Text);

            int read = report.Pages.Count(p => p.Status == PdfOcrPageStatus.Done);
            int failed = report.Pages.Count(p => p.Status == PdfOcrPageStatus.Failed);
            if (a.Json)
            {
                var summary = new OcrSummary(
                    output,
                    report.Pages.Count,
                    read,
                    failed,
                    [.. report.Pages.Select(p => new OcrPageSummary(p.Index + 1, p.Status.ToString(), p.Reason, p.Orientation, p.Lines.Count, p.Text))]);
                Console.WriteLine(JsonSerializer.Serialize(summary, OcrJson.Default.OcrSummary));
            }
            else
            {
                foreach (PdfOcrPage page in report.Pages) Console.WriteLine($"page {page.Index + 1}: {Describe(page)}");
                Console.WriteLine($"{output}: {report.Pages.Count} page(s), {read} read, {Size(new FileInfo(output).Length)}");
            }

            return failed > 0 ? 1 : 0;
        }

        private static string Describe(PdfOcrPage page) => page.Status switch
        {
            PdfOcrPageStatus.Done => $"{page.Lines.Count} line(s), {page.Lines.Sum(l => l.Words.Count)} word(s)" + (page.Orientation != 0 ? $", scanned turned {page.Orientation} degrees" : ""),
            PdfOcrPageStatus.SkippedHasText => "already has text",
            PdfOcrPageStatus.SkippedNoScan => "no scan found",
            PdfOcrPageStatus.SkippedUnsupported => "not read: " + Printable(page.Reason),
            _ => "failed: " + Printable(page.Reason),
        };
    }

    internal sealed class OcrArguments
    {
        private static readonly string[] KnownLanguages = ["latin", "arabic"];

        public List<string> Positional { get; } = [];

        public string[] Languages { get; private set; } = ["latin"];

        public string? Models { get; private set; }

        public double MinimumConfidence { get; private set; } = 0.5;

        public bool DetectOrientation { get; private set; } = true;

        public bool AllPages { get; private set; }

        public int Threads { get; private set; } = Environment.ProcessorCount;

        public string? TextPath { get; private set; }

        public string? Password { get; private set; }

        public bool NoOptimize { get; private set; }

        public bool Json { get; private set; }

        public static OcrArguments Parse(string[] args)
        {
            var a = new OcrArguments();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--languages": a.Languages = ParseLanguages(Value(args, ref i)); break;
                    case "--models": a.Models = Value(args, ref i); break;
                    case "--min-confidence":
                        a.MinimumConfidence = double.Parse(Value(args, ref i), NumberStyles.Float, CultureInfo.InvariantCulture);
                        if (!(a.MinimumConfidence is >= 0 and <= 1)) throw new ArgumentException("--min-confidence takes a number from 0 to 1.");
                        break;
                    case "--no-orientation": a.DetectOrientation = false; break;
                    case "--all-pages": a.AllPages = true; break;
                    case "--threads": a.Threads = Math.Max(1, int.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture)); break;
                    case "--text": a.TextPath = Value(args, ref i); break;
                    case "--password": a.Password = Value(args, ref i); break;
                    case "--no-optimize": a.NoOptimize = true; break;
                    case "--json": a.Json = true; break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown option " + arg);
                        a.Positional.Add(arg);
                        break;
                }
            }

            return a;
        }

        private static string[] ParseLanguages(string list)
        {
            string[] languages = [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.ToLowerInvariant()).Distinct()];
            if (languages.Length == 0) throw new ArgumentException("--languages needs at least one of: latin, arabic.");
            foreach (string language in languages)
            {
                if (!KnownLanguages.Contains(language)) throw new ArgumentException($"Unknown language '{language}'. Known: latin, arabic.");
            }

            return languages;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value.");
            return args[++i];
        }
    }

    internal sealed record OcrPageSummary(int Page, string Status, string? Reason, int Orientation, int Lines, string Text);

    internal sealed record OcrSummary(string Output, int Pages, int Read, int Failed, OcrPageSummary[] PageResults);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(OcrSummary))]
    internal sealed partial class OcrJson : JsonSerializerContext;
}
