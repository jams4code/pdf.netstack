using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>
/// PDF optimizer, lossless by default. Every entry point writes a valid PDF to the output: either
/// a smaller rewrite that has been proven equivalent to the input, or the input bytes unchanged.
/// With <see cref="PdfOptimizerOptions.Linearize"/> set, a proven linearized rewrite of a file
/// that is not linearized yet is written even when it is larger.
/// With <see cref="PdfOptimizerOptions.JpegQuality"/> set, JPEG images may be requantized; the
/// proof then checks that each such image changed by exactly that requantization and nothing else.
/// By default the input and the rewrite are held in memory; with
/// <see cref="PdfOptimizerOptions.Streaming"/> neither is, for files larger than memory.
/// </summary>
public static partial class PdfOptimizer
{
    /// <summary>Optimizes a PDF held in memory and writes the result to <paramref name="output"/>.</summary>
    /// <param name="input">The complete source PDF. It is never modified.</param>
    /// <param name="output">Receives the optimized PDF, or the original bytes when no safe saving exists.</param>
    /// <param name="options">
    /// Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null. With
    /// <see cref="PdfOptimizerOptions.Streaming"/>, the rewrite goes through a temporary file
    /// instead of memory.
    /// </param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    /// <returns>What was written and why.</returns>
    public static PdfOptimizationResult Optimize(
        ReadOnlyMemory<byte> input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(output, nameof(output));
        options ??= PdfOptimizerOptions.Default;
        options.Validate();
        if (options.Streaming) return OptimizeStreaming(new Source(input), output, options, cancellationToken);

        var clock = Stopwatch.StartNew();
        string sourceHash = Sha256Hex(input.Span);
        Outcome outcome = Run(input, options, cancellationToken);

        ReadOnlySpan<byte> written = outcome.Candidate is { } candidate ? candidate.Span : input.Span;
        output.Write(written);
        string outputHash = outcome.Candidate is null ? sourceHash : Sha256Hex(written);

        OptimizationStatistics stats = (outcome.Statistics ?? OptimizationStatistics.Empty(TimeSpan.Zero)) with { Elapsed = clock.Elapsed };
        return new PdfOptimizationResult(outcome.Disposition, input.Length, written.Length, sourceHash, outputHash,
            outcome.PageCount, outcome.Reason, stats);
    }

    /// <summary>Optimizes a PDF and returns the output bytes together with the result.</summary>
    public static (byte[] Output, PdfOptimizationResult Result) Optimize(
        byte[] input,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(input, nameof(input));
        using var output = new MemoryStream();
        PdfOptimizationResult result = Optimize(input, output, options, cancellationToken);
        return (output.ToArray(), result);
    }

    /// <summary>Reads <paramref name="input"/> to the end, optimizes it and writes to <paramref name="output"/>.</summary>
    /// <remarks>
    /// <paramref name="output"/> is written only asynchronously. By default the input is read
    /// asynchronously into memory. With <see cref="PdfOptimizerOptions.Streaming"/>, a
    /// <see cref="FileStream"/> or <see cref="MemoryStream"/> positioned at its start is read on
    /// demand where it is, and any other input is first copied, asynchronously, to a temporary file.
    /// </remarks>
    /// <param name="input">The source PDF, read from its current position to its end.</param>
    /// <param name="output">Receives the optimized PDF, or the input unchanged.</param>
    /// <param name="options">Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    public static async Task<PdfOptimizationResult> OptimizeAsync(
        Stream input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(input, nameof(input));
        Compat.ThrowIfNull(output, nameof(output));
        options ??= PdfOptimizerOptions.Default;
        if (options.Streaming)
        {
            options.Validate();
            return await OptimizeStreamingAsync(input, output, options, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
        // Buffer at most MaxInputBytes. Anything larger is streamed through untouched without
        // ever being held in memory, as the in-memory path would return it unchanged anyway.
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > options.MaxInputBytes)
            {
                long total = buffer.Length;
                buffer.Position = 0;
                await buffer.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                while ((read = await input.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(chunk, 0, read, cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                return new PdfOptimizationResult(OptimizationDisposition.UnsupportedRetained, total, total, "", "", 0,
                    "Input exceeds the configured size limit.", OptimizationStatistics.Empty(TimeSpan.Zero));
            }
        }

#pragma warning restore CA1835
        using var result = new MemoryStream();
        PdfOptimizationResult r = Optimize(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), result, options, cancellationToken);
        result.Position = 0;
        await result.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        return r;
    }

    /// <summary>
    /// Optimizes a file. The destination is written through a temporary file and moved into place,
    /// so readers never observe a partial file. Source and destination must differ.
    /// </summary>
    /// <remarks>
    /// By default the source is read into memory. With <see cref="PdfOptimizerOptions.Streaming"/>
    /// it is read on demand, and the rewrite is written to the destination's temporary file and
    /// proven there before it is moved into place; when it is not used, the source is copied there
    /// instead.
    /// </remarks>
    /// <param name="sourcePath">The PDF to optimize. It is never modified.</param>
    /// <param name="destinationPath">Receives the optimized PDF, or a copy of the source.</param>
    /// <param name="options">Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes; the destination is then left as it was.</param>
    public static async Task<PdfOptimizationResult> OptimizeFileAsync(
        string sourcePath,
        string destinationPath,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination must be different files.", nameof(destinationPath));
        }

        if (options is { Streaming: true })
        {
            options.Validate();
            return await OptimizeFileStreamingAsync(source, destination, options, cancellationToken).ConfigureAwait(false);
        }

        byte[] data = await Compat.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PdfOptimizationResult result;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                result = Optimize(data, stream, options, cancellationToken);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Compat.MoveOverwrite(temp, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record Outcome(
        OptimizationDisposition Disposition,
        ReadOnlyMemory<byte>? Candidate,
        string Reason,
        int PageCount,
        OptimizationStatistics? Statistics);

    private static Outcome Run(ReadOnlyMemory<byte> input, PdfOptimizerOptions options, CancellationToken ct)
    {
        if (input.Length > options.MaxInputBytes)
        {
            return new Outcome(OptimizationDisposition.UnsupportedRetained, null, "Input exceeds the configured size limit.", 0, null);
        }

        PdfFile file;
        try
        {
            file = PdfFile.Open(input, options.MaxDecodedStreamBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Outcome(OptimizationDisposition.UnsupportedRetained, null, "Not a readable PDF: " + ex.Message, 0, null);
        }

        int pageCount = 0;
        var phase = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        try
        {
            pageCount = CountPages(file);
            if (Unsupported(file, input.Span.IndexOf("/ByteRange"u8) >= 0, options) is { } unsupported)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, unsupported);
            }

            ObjectGraph graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
            if (Unsupported(graph, file, options) is { } unsupportedGraph)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, unsupportedGraph);
            }

            bool pdfA1 = IsPdfA1(file, options.MaxDecodedStreamBytes);
            int stripped = graph.StrippedEntries;
            timings = timings with { Analyze = phase.Elapsed };
            phase.Restart();

            // Structural passes, all decided by one analysis of the content before anything changes.
            int pruned = 0, fontsMerged = 0;
            HashSet<int> compact = [], encoded = [];
            bool mergeFonts = options.MergeFontSubsets && options.DeduplicateObjects;
            if (options.PruneUnusedResources || options.CompactContentStreams || mergeFonts)
            {
                ContentAnalysis analysis = ContentAnalysis.Run(file, graph.Order, [], options.MaxDecodedStreamBytes, ct);
                if (options.PruneUnusedResources && (pruned = ResourcePruner.Run(analysis)) > 0)
                {
                    graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
                }

                if (mergeFonts) (fontsMerged, encoded) = FontProgramMerger.Run(file, analysis, options.ZlibLevel, options.MaxDecodedStreamBytes, ct);
                if (options.CompactContentStreams) compact = analysis.ContentStreams;
            }

            // A linearized file describes each page by what the page itself refers to.
            string? notLinearized = options.Linearize ? InheritedAttributes.PushToPages(file) : null;
            bool pushedDown = options.Linearize && notLinearized is null;

            timings = timings with { Content = phase.Elapsed };
            phase.Restart();

            RecompressionCounts recompressed = new(0, 0, []);
            if (options.RecompressStreams || compact.Count > 0)
            {
                JpegSettings? jpeg = options.OptimizeJpegImages ? new JpegSettings(options.JpegQuality, options.ProgressiveJpeg, options.MinImageSsim) : null;
                ImageSettings? images = options.MaxImageDpi is not null || options.ConvertImagesToJpeg || options.QuantizeImages
                    ? new ImageSettings(options.MaxImageDpi, options.ConvertImagesToJpeg, options.JpegQuality, options.MinImageSsim, options.ProgressiveJpeg) { Quantize = options.QuantizeImages }
                    : null;
                var scope = new RecompressionScope(options.RecompressStreams, compact, encoded, options.DeduplicateObjects);
                recompressed = StreamRecompressor.Run(file, graph, scope, options.ZlibLevel, options.Compression == CompressionEffort.Exhaustive, options.UseImagePredictors,
                    options.OptimizeBilevelImages, jpeg, images, options.MaxDecodedStreamBytes, options.MaxDegreeOfParallelism, ct);

                // A JBIG2 image coded anew no longer needs its JBIG2Globals, which may now be unused.
                if (recompressed.ResampledObjects.Count > 0) graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
            }

            timings = timings with { Recompress = phase.Elapsed };
            phase.Restart();

            Dictionary<int, int> representative;
            int merged = 0;
            if (options.DeduplicateObjects)
            {
                (representative, merged) = Deduplicator.Run(file, graph, recompressed.ContentHashes, ct);
            }
            else
            {
                representative = graph.Order.ToDictionary(n => n, n => n);
            }

            timings = timings with { Deduplicate = phase.Elapsed };
            phase.Restart();

            ct.ThrowIfCancellationRequested();
            var outputNumber = new Dictionary<int, int>(graph.Order.Count);
            var objects = new List<PdfObject>(graph.Order.Count);
            foreach (int number in graph.Order)
            {
                if (representative[number] != number) continue;
                objects.Add(file.GetObject(number));
                outputNumber[number] = objects.Count;
            }

            int Map(PdfReference r) =>
                representative.TryGetValue(r.Number, out int rep) && outputNumber.TryGetValue(rep, out int n) ? n : -1;

            var trailer = new PdfDictionary();
            foreach (string key in new[] { "Root", "Info" })
            {
                if (file.Trailer[key] is { } v) trailer.Set(key, v);
            }

            if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);

            bool objectStreams = options.UseObjectStreams && !pdfA1;
            var settings = new WriterSettings(objectStreams, options.ObjectsPerStream,
                OutputVersion(file.HeaderVersion, objectStreams), options.ZlibLevel)
            {
                Exhaustive = options.Compression == CompressionEffort.Exhaustive,
            };
            ReadOnlyMemory<byte> candidate;
            bool linearized = false;
            if (pushedDown && LinearizedWriter.Write(objects, trailer, Map, settings with { UseObjectStreams = false, Version = LinearizedVersion(file.HeaderVersion) }) is { } lin)
            {
                candidate = lin;
                linearized = true;
            }
            else
            {
                if (pushedDown) notLinearized = "the page tree cannot be laid out page by page";
                candidate = PdfDocumentWriter.Write(objects, trailer, Map, settings);
            }

            timings = timings with { Write = phase.Elapsed };
            phase.Restart();

            var stats = new OptimizationStatistics(
                file.Xref.Count(e => e.Value.Type != XrefEntryType.Free),
                graph.Order.Count,
                objects.Count,
                recompressed.Streams,
                merged,
                stripped,
                0,
                TimeSpan.Zero,
                timings)
            {
                JpegImagesRecompressed = recompressed.JpegImages,
                JpegImagesRequantized = recompressed.JpegRequantized,
                ImagesDownsampled = recompressed.Downsampled,
                ImagesConvertedToJpeg = recompressed.ConvertedToJpeg,
                ImagesQuantized = recompressed.Quantized,
                ImagesKeptByQualityGate = recompressed.KeptByGate,
                ImagesRecodedAsGroup4 = recompressed.Group4,
                ResourcesPruned = pruned,
                ContentStreamsCompacted = recompressed.Compacted,
                FontProgramsMerged = fontsMerged,
                Linearized = linearized,
            };

            // Linearizing a file that is not linearized yet is worth keeping even when it costs bytes.
            double saving = 100.0 * (input.Length - candidate.Length) / input.Length;
            if (saving < options.MinSavingPercent && !(linearized && !file.IsLinearized))
            {
                return new Outcome(OptimizationDisposition.OriginalRetained, null,
                    Compat.Format("Rewrite saved {0:F2}%, below the {1}% threshold.", saving, options.MinSavingPercent),
                    pageCount, stats);
            }

            if (options.Verify)
            {
                ct.ThrowIfCancellationRequested();
                PdfFile reference = PdfFile.Open(input, options.MaxDecodedStreamBytes);
                PdfFile written = PdfFile.Open(candidate, options.MaxDecodedStreamBytes);
                if (written.WasReconstructed || written.LengthsRepaired > 0)
                {
                    return new Outcome(OptimizationDisposition.OriginalRetained, null, "Verification failed: output is not well formed.", pageCount, stats);
                }

                var ignored = new List<string>();
                if (options.RemovePageThumbnails) ignored.Add("Thumb");
                if (options.RemovePieceInfo) ignored.Add("PieceInfo");
                var rules = new VerificationRules
                {
                    IgnoredSourceKeys = ignored,
                    Images = new ImageAllowance(recompressed.RequantizedObjects.Contains, recompressed.ResampledObjects.Contains, options.MinImageSsim),
                    AllowResourcePruning = pruned > 0,
                    AllowContentCompaction = recompressed.Compacted > 0,
                    AllowFontMerging = fontsMerged > 0,
                    AllowInheritedAttributes = pushedDown,
                    AllowBilevelRecoding = recompressed.Group4 > 0,
                };
                string? difference = EquivalenceVerifier.Verify(reference, written, rules, options.MaxDecodedStreamBytes,
                    options.MaxDegreeOfParallelism, ct, out int compared);
                if (difference is not null)
                {
                    return new Outcome(OptimizationDisposition.OriginalRetained, null, "Verification failed: " + difference, pageCount, stats);
                }

                stats = stats with { ObjectsVerified = compared, Phases = timings with { Verify = phase.Elapsed } };
            }

            string reason = Reason(options, recompressed);
            if (linearized) reason += " The output is linearized.";
            else if (notLinearized is not null) reason += " Not linearized: " + notLinearized + ".";
            return new Outcome(OptimizationDisposition.Optimized, candidate, reason, pageCount, stats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Retained(OptimizationDisposition.OriginalRetained, $"Rewrite failed ({ex.GetType().Name}): {ex.Message}");
        }

        Outcome Retained(OptimizationDisposition disposition, string reason) => new(disposition, null, reason, pageCount, null);
    }

    /// <summary>Why a file is left as it is, from what opening it showed; null when it may be rewritten.</summary>
    /// <param name="signed">The file's bytes hold a signature's /ByteRange.</param>
    private static string? Unsupported(PdfFile file, bool signed, PdfOptimizerOptions options)
    {
        if (file.Trailer["Encrypt"] is not null and not PdfNull) return "Encrypted PDF.";
        if (signed) return "Digitally signed PDF.";
        if (file.RevisionCount > 1 && !options.AllowIncrementalUpdates)
        {
            return $"PDF has {file.RevisionCount} revisions (incremental updates). Set AllowIncrementalUpdates to flatten.";
        }

        if ((file.WasReconstructed || file.LengthsRepaired > 0) && !options.AllowRepairedFiles)
        {
            return "PDF is damaged and needed repair. Set AllowRepairedFiles to rewrite it.";
        }

        return null;
    }

    /// <summary>Why a file is left as it is, from what walking its object graph showed; null when it may be rewritten.</summary>
    private static string? Unsupported(ObjectGraph graph, PdfFile file, PdfOptimizerOptions options)
    {
        if (graph.HasSignatureMarkers) return "Digitally signed or usage-rights enabled PDF.";
        if (graph.HasGenerationMismatch && !options.AllowRepairedFiles)
        {
            return "References name generations the cross-reference data does not record. Set AllowRepairedFiles to rewrite it.";
        }

        if (file.UnreadableObjects > 0 && !options.AllowRepairedFiles)
        {
            return $"{file.UnreadableObjects} object(s) could not be parsed. Set AllowRepairedFiles to rewrite it.";
        }

        return null;
    }

    /// <summary>What was written, naming every lossy image change.</summary>
    private static string Reason(PdfOptimizerOptions options, RecompressionCounts recompressed)
    {
        var lossy = new List<string>();
        if (recompressed.JpegRequantized > 0) lossy.Add(Compat.Format("{0} JPEG image(s) requantized to quality {1}", recompressed.JpegRequantized, options.JpegQuality!.Value));
        if (recompressed.Downsampled > 0) lossy.Add(Compat.Format("{0} image(s) downsampled to {1} dpi", recompressed.Downsampled, options.MaxImageDpi!.Value));
        if (recompressed.ConvertedToJpeg > 0) lossy.Add(Compat.Format("{0} image(s) converted to JPEG at quality {1}", recompressed.ConvertedToJpeg, options.JpegQuality!.Value));
        if (recompressed.Quantized > 0) lossy.Add(Compat.Format("{0} image(s) reduced to a palette", recompressed.Quantized));
        if (lossy.Count == 0)
        {
            return options.Verify
                ? "Rewritten and verified equivalent to the source."
                : "Rewritten. Verification was disabled.";
        }

        string changes = string.Join(", ", lossy);
        return options.Verify
            ? "Rewritten and verified. " + changes + "; everything else is equivalent to the source."
            : "Rewritten. " + changes + ". Verification was disabled.";
    }

    internal static int CountPages(PdfFile file)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        PdfDictionary? pages = file.ResolveDictionary(catalog?["Pages"]);
        return file.Resolve(pages?["Count"]) is PdfNumber { IsInteger: true } n && n.IntegerValue is >= 0 and <= int.MaxValue
            ? (int)n.IntegerValue
            : 0;
    }

    private static string OutputVersion(string header, bool objectStreams)
    {
        if (!objectStreams) return header;
        return decimal.TryParse(header, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.5m
            ? header
            : "1.5";
    }

    /// <summary>Linearization arrived with PDF 1.2.</summary>
    private static string LinearizedVersion(string header) =>
        decimal.TryParse(header, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.2m ? header : "1.2";

    /// <summary>PDF/A-1 is based on PDF 1.4 and forbids object and xref streams.</summary>
    internal static bool IsPdfA1(PdfFile file, long decodeLimit)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (file.Resolve(catalog?["Metadata"]) is not PdfStream metadata) return false;
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(metadata.Data, StreamCodec.GetFilters(metadata.Dictionary, file.Resolve),
                DecodeDepth.Full, decodeLimit, file.Resolve);
            return PdfAPartOne().IsMatch(Compat.Latin1.GetString(decoded.Data.Span));
        }
        catch (StreamDecodeException)
        {
            // Unknown conformance: stay on the safe side.
            return true;
        }
    }

#if NET8_0_OR_GREATER
    [GeneratedRegex("""pdfaid:part\s*(=\s*["']\s*1\s*["']|>\s*1\s*<)""")]
    private static partial Regex PdfAPartOne();
#else
    private static readonly Regex PdfAPartOneRegex = new("""pdfaid:part\s*(=\s*["']\s*1\s*["']|>\s*1\s*<)""", RegexOptions.CultureInvariant);

    private static Regex PdfAPartOne() => PdfAPartOneRegex;
#endif

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Compat.ToHex(Compat.Sha256(data)).ToLowerInvariant();
}
