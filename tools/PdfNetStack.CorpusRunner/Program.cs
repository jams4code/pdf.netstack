using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfNetStack;
using PdfNetStack.Objects;
using PdfNetStack.CorpusRunner;

return Runner.Run(args);

namespace PdfNetStack.CorpusRunner
{
    /// <summary>
    /// Measures one document for tools/corpus_bench.py and prints one JSON object. Each call runs
    /// in its own process, so peak memory belongs to that one operation and a crash or a hang
    /// costs one document, not the whole run.
    /// </summary>
    internal static class Runner
    {
        private const string Usage = """
            Usage:
              PdfNetStack.CorpusRunner optimize <input.pdf> <output.pdf> --profile lossless|lossy|balanced|lossy-palette
              PdfNetStack.CorpusRunner extract <input.pdf>

            optimize writes <output.pdf> only when the optimizer kept a rewrite, then checks the
            rewrite again with PdfEquivalence. extract reads the text of every page and decodes
            every image the pages draw. Both print one JSON object to standard output.
            """;

        private const long MaxDecodedStreamBytes = 256L << 20;

        public static int Run(string[] args)
        {
            try
            {
                return args switch
                {
                    ["optimize", string input, string output, "--profile", string profile] => Optimize(input, output, profile),
                    ["extract", string input] => Extract(input),
                    _ => Fail(Usage),
                };
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

        /// <summary>
        /// The option sets the benchmark measures. All accept files with incremental updates and
        /// files that needed repair, and keep any rewrite that is not larger, so the engine and its
        /// proof run on as many documents as possible. The lossy profile turns on the image options
        /// that change resolution and JPEG behind the perceptual gate; balanced is the preset of
        /// that name; lossy-palette is lossy with palettes, to measure what they add.
        /// </summary>
        internal static PdfOptimizerOptions? Profile(string name) => name switch
        {
            "lossless" => new PdfOptimizerOptions { AllowIncrementalUpdates = true, AllowRepairedFiles = true, MinSavingPercent = 0 },
            "lossy" => new PdfOptimizerOptions
            {
                AllowIncrementalUpdates = true,
                AllowRepairedFiles = true,
                MinSavingPercent = 0,
                JpegQuality = 75,
                MaxImageDpi = 150,
                ConvertImagesToJpeg = true,
                MinImageSsim = 0.95,
                ProgressiveJpeg = true,
            },
            "balanced" => PdfOptimizerOptions.Balanced with { AllowIncrementalUpdates = true, AllowRepairedFiles = true, MinSavingPercent = 0 },
            "lossy-palette" => Profile("lossy")! with { QuantizeImages = true },
            _ => null,
        };

        private static int Optimize(string inputPath, string outputPath, string profile)
        {
            PdfOptimizerOptions? options = Profile(profile);
            if (options is null) return Fail("Unknown profile: " + profile);

            byte[] input = File.ReadAllBytes(inputPath);
            using var json = new JsonOutput();
            Utf8JsonWriter w = json.Writer;
            w.WriteString("profile", profile);
            w.WriteNumber("input_bytes", input.Length);

            byte[] output;
            PdfOptimizationResult result;
            var clock = Stopwatch.StartNew();
            try
            {
                (output, result) = PdfOptimizer.Optimize(input, options);
            }
#pragma warning disable CA1031 // Any exception here is a library bug the report must show.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                w.WriteNumber("milliseconds", Ms(clock.Elapsed.TotalMilliseconds));
                w.WriteString("error", Describe(ex));
                return 1;
            }

            double elapsed = clock.Elapsed.TotalMilliseconds;
            long peak = PeakWorkingSet();
            OptimizationStatistics s = result.Statistics;
            w.WriteString("disposition", result.Disposition.ToString());
            w.WriteString("reason", result.Reason);
            w.WriteNumber("output_bytes", result.OutputBytes);
            w.WriteNumber("pages", result.PageCount);
            w.WriteNumber("milliseconds", Ms(elapsed));
            w.WriteNumber("peak_working_set_bytes", peak);
            w.WriteStartObject("phases_ms");
            w.WriteNumber("analyze", Ms(s.Phases.Analyze));
            w.WriteNumber("content", Ms(s.Phases.Content));
            w.WriteNumber("recompress", Ms(s.Phases.Recompress));
            w.WriteNumber("deduplicate", Ms(s.Phases.Deduplicate));
            w.WriteNumber("write", Ms(s.Phases.Write));
            w.WriteNumber("verify", Ms(s.Phases.Verify));
            w.WriteEndObject();
            w.WriteStartObject("counters");
            w.WriteNumber("streams_recompressed", s.StreamsRecompressed);
            w.WriteNumber("objects_deduplicated", s.ObjectsDeduplicated);
            w.WriteNumber("unreachable_removed", s.UnreachableRemoved);
            w.WriteNumber("jpeg_recompressed", s.JpegImagesRecompressed);
            w.WriteNumber("jpeg_requantized", s.JpegImagesRequantized);
            w.WriteNumber("images_downsampled", s.ImagesDownsampled);
            w.WriteNumber("images_converted_to_jpeg", s.ImagesConvertedToJpeg);
            w.WriteNumber("images_kept_by_quality_gate", s.ImagesKeptByQualityGate);
            w.WriteNumber("images_recoded_as_group4", s.ImagesRecodedAsGroup4);
            w.WriteNumber("resources_pruned", s.ResourcesPruned);
            w.WriteNumber("content_streams_compacted", s.ContentStreamsCompacted);
            w.WriteNumber("font_programs_merged", s.FontProgramsMerged);
            w.WriteEndObject();

            if (result.Disposition != OptimizationDisposition.Optimized) return 0;

            string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllBytes(outputPath, output);

            // The optimizer proved the rewrite before returning it; this proves it again from the
            // two files alone, as any other tool's output would be checked.
            clock.Restart();
            string? difference;
            try
            {
                difference = PdfEquivalence.FindDifference(input, output, MaxDecodedStreamBytes, options.JpegQuality is not null, options.MinImageSsim);
            }
#pragma warning disable CA1031 // A throwing check is a failed check, reported as such.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                difference = "check threw " + Describe(ex);
            }

            w.WriteNumber("equivalence_ms", Ms(clock.Elapsed.TotalMilliseconds));
            if (difference is null) w.WriteNull("equivalence_difference");
            else w.WriteString("equivalence_difference", difference);
            return 0;
        }

        private static int Extract(string inputPath)
        {
            byte[] input = File.ReadAllBytes(inputPath);
            using var json = new JsonOutput();
            Utf8JsonWriter w = json.Writer;

            PdfDocument doc;
            var clock = Stopwatch.StartNew();
            try
            {
                doc = PdfDocument.Open(input);
            }
            catch (PdfPasswordException)
            {
                w.WriteString("error", "password required");
                return 0;
            }
            catch (PdfFormatException ex)
            {
                w.WriteString("error", "not readable: " + ex.Message);
                return 0;
            }
#pragma warning disable CA1031 // Anything else is a library bug the report must show.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                w.WriteString("error", Describe(ex));
                return 1;
            }

            double openMs = clock.Elapsed.TotalMilliseconds;
            var errors = new List<string>();
            var pageHashes = new List<string>();
            long characters = 0;
            clock.Restart();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                try
                {
                    string text = doc.Pages[i].ExtractText();
                    characters += text.Length;
                    pageHashes.Add(Hash(Encoding.UTF8.GetBytes(text)));
                }
#pragma warning disable CA1031 // Extraction reads damaged content as far as it goes; a throw is a bug.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    pageHashes.Add("error");
                    errors.Add($"text, page {i + 1}: {Describe(ex)}");
                }
            }

            double textMs = clock.Elapsed.TotalMilliseconds;
            int images = 0, decoded = 0, undecodable = 0, annotations = 0;
            var encodings = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var undecodableEncodings = new SortedDictionary<string, int>(StringComparer.Ordinal);
            using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            clock.Restart();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                IReadOnlyList<PdfPageImage> found;
                try
                {
                    annotations += doc.Pages[i].AnnotationCount;
                    found = doc.Pages[i].GetImages();
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    errors.Add($"images, page {i + 1}: {Describe(ex)}");
                    continue;
                }

                foreach (PdfPageImage image in found)
                {
                    images++;
                    string encoding = image.Encoding ?? "raw";
                    encodings[encoding] = encodings.GetValueOrDefault(encoding) + 1;
                    try
                    {
                        PdfBitmap? bitmap = image.Decode();
                        if (bitmap is null)
                        {
                            undecodable++;
                            undecodableEncodings[encoding] = undecodableEncodings.GetValueOrDefault(encoding) + 1;
                            continue;
                        }

                        decoded++;
                        imageHash.AppendData(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{bitmap.Width}x{bitmap.Height}:{bitmap.Format};")));
                        imageHash.AppendData(bitmap.Pixels.Span);
                    }
#pragma warning disable CA1031
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        undecodable++;
                        undecodableEncodings[encoding] = undecodableEncodings.GetValueOrDefault(encoding) + 1;
                        errors.Add($"image on page {i + 1}: {Describe(ex)}");
                    }
                }
            }

            w.WriteNumber("pages", doc.Pages.Count);
            w.WriteNumber("open_ms", Ms(openMs));
            w.WriteNumber("text_ms", Ms(textMs));
            w.WriteNumber("text_characters", characters);
            w.WriteStartArray("page_text_sha256");
            foreach (string h in pageHashes) w.WriteStringValue(h);
            w.WriteEndArray();
            w.WriteNumber("images_ms", Ms(clock.Elapsed.TotalMilliseconds));
            w.WriteNumber("images", images);
            w.WriteNumber("images_decoded", decoded);
            w.WriteNumber("images_undecodable", undecodable);
            WriteCounts(w, "image_encodings", encodings);
            WriteCounts(w, "undecodable_encodings", undecodableEncodings);
            w.WriteNumber("annotations", annotations);
            w.WriteStartArray("features");
            foreach (string feature in Features(doc)) w.WriteStringValue(feature);
            w.WriteEndArray();
            w.WriteString("decoded_images_sha256", Convert.ToHexString(imageHash.GetHashAndReset()).ToLowerInvariant());
            w.WriteNumber("peak_working_set_bytes", PeakWorkingSet());
            w.WriteStartArray("errors");
            foreach (string e in errors.Take(20)) w.WriteStringValue(e);
            w.WriteEndArray();
            w.WriteNumber("error_count", errors.Count);
            return errors.Count == 0 ? 0 : 1;
        }

        /// <summary>Document level features read from the catalog, for the report's coverage table.</summary>
        private static IEnumerable<string> Features(PdfDocument doc)
        {
            PdfDictionary catalog = doc.Catalog;
            bool marked = doc.Resolve(catalog["MarkInfo"]) is PdfDictionary markInfo && doc.Resolve(markInfo["Marked"]) is PdfBoolean { Value: true };
            if (marked || doc.Resolve(catalog["StructTreeRoot"]) is PdfDictionary) yield return "tagged";
            if (doc.Resolve(catalog["AcroForm"]) is PdfDictionary form)
            {
                if (doc.Resolve(form["Fields"]) is PdfArray { Count: > 0 }) yield return "acroform";
                if (doc.Resolve(form["XFA"]) is not null) yield return "xfa";
            }

            if (doc.Resolve(catalog["OCProperties"]) is PdfDictionary) yield return "optional-content";
            if (doc.Resolve(catalog["Names"]) is PdfDictionary names && doc.Resolve(names["EmbeddedFiles"]) is PdfDictionary) yield return "attachments";
            if (doc.Resolve(catalog["Outlines"]) is PdfDictionary) yield return "outlines";
        }

        private static void WriteCounts(Utf8JsonWriter w, string name, SortedDictionary<string, int> counts)
        {
            w.WriteStartObject(name);
            foreach (KeyValuePair<string, int> pair in counts) w.WriteNumber(pair.Key, pair.Value);
            w.WriteEndObject();
        }

        private static long PeakWorkingSet()
        {
            using Process self = Process.GetCurrentProcess();
            return self.PeakWorkingSet64;
        }

        private static double Ms(double milliseconds) => Math.Round(milliseconds, 1);

        private static double Ms(TimeSpan time) => Ms(time.TotalMilliseconds);

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
    }

    /// <summary>One JSON object written to standard output when disposed, whatever path returned.</summary>
    internal sealed class JsonOutput : IDisposable
    {
        private readonly MemoryStream _buffer = new();

        public JsonOutput()
        {
            Writer = new Utf8JsonWriter(_buffer);
            Writer.WriteStartObject();
        }

        public Utf8JsonWriter Writer { get; }

        public void Dispose()
        {
            Writer.WriteEndObject();
            Writer.Flush();
            Writer.Dispose();
            using Stream stdout = Console.OpenStandardOutput();
            stdout.Write(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            stdout.WriteByte((byte)'\n');
            _buffer.Dispose();
        }
    }
}
