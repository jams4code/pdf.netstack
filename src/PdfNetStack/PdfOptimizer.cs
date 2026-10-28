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
/// Lossless PDF optimizer. Every entry point writes a valid PDF to the output: either a smaller
/// rewrite that has been proven equivalent to the input, or the input bytes unchanged.
/// </summary>
public static partial class PdfOptimizer
{
    /// <summary>Optimizes a PDF held in memory and writes the result to <paramref name="output"/>.</summary>
    /// <param name="input">The complete source PDF. It is never modified.</param>
    /// <param name="output">Receives the optimized PDF, or the original bytes when no safe saving exists.</param>
    /// <param name="options">Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    /// <returns>What was written and why.</returns>
    public static PdfOptimizationResult Optimize(
        ReadOnlyMemory<byte> input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= PdfOptimizerOptions.Default;
        options.Validate();

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
        ArgumentNullException.ThrowIfNull(input);
        using var output = new MemoryStream();
        PdfOptimizationResult result = Optimize(input, output, options, cancellationToken);
        return (output.ToArray(), result);
    }

    /// <summary>Reads <paramref name="input"/> to the end, optimizes it and writes to <paramref name="output"/>.</summary>
    public static async Task<PdfOptimizationResult> OptimizeAsync(
        Stream input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= PdfOptimizerOptions.Default;
        // Buffer at most MaxInputBytes. Anything larger is streamed through untouched without
        // ever being held in memory, as the in-memory path would return it unchanged anyway.
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > options.MaxInputBytes)
            {
                long total = buffer.Length;
                buffer.Position = 0;
                await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                return new PdfOptimizationResult(OptimizationDisposition.UnsupportedRetained, total, total, "", "", 0,
                    "Input exceeds the configured size limit.", OptimizationStatistics.Empty(TimeSpan.Zero));
            }
        }

        using var result = new MemoryStream();
        PdfOptimizationResult r = Optimize(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), result, options, cancellationToken);
        result.Position = 0;
        await result.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return r;
    }

    /// <summary>
    /// Optimizes a file. The destination is written through a temporary file and moved into place,
    /// so readers never observe a partial file. Source and destination must differ.
    /// </summary>
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

        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PdfOptimizationResult result;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                result = Optimize(data, stream, options, cancellationToken);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, destination, overwrite: true);
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

            if (file.Trailer["Encrypt"] is not null and not PdfNull)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, "Encrypted PDF.");
            }

            if (input.Span.IndexOf("/ByteRange"u8) >= 0)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, "Digitally signed PDF.");
            }

            if (file.RevisionCount > 1 && !options.AllowIncrementalUpdates)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained,
                    $"PDF has {file.RevisionCount} revisions (incremental updates). Set AllowIncrementalUpdates to flatten.");
            }

            if ((file.WasReconstructed || file.LengthsRepaired > 0) && !options.AllowRepairedFiles)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained,
                    "PDF is damaged and needed repair. Set AllowRepairedFiles to rewrite it.");
            }

            ObjectGraph graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
            if (graph.HasSignatureMarkers)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, "Digitally signed or usage-rights enabled PDF.");
            }

            if (graph.HasGenerationMismatch && !options.AllowRepairedFiles)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained,
                    "References name generations the cross-reference data does not record. Set AllowRepairedFiles to rewrite it.");
            }

            if (file.UnreadableObjects > 0 && !options.AllowRepairedFiles)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained,
                    $"{file.UnreadableObjects} object(s) could not be parsed. Set AllowRepairedFiles to rewrite it.");
            }

            bool pdfA1 = IsPdfA1(file, options.MaxDecodedStreamBytes);
            timings = timings with { Analyze = phase.Elapsed };
            phase.Restart();

            int recompressed = 0;
            if (options.RecompressStreams)
            {
                recompressed = StreamRecompressor.Run(file, graph, options.ZlibLevel, options.UseImagePredictors,
                    options.MaxDecodedStreamBytes, options.MaxDegreeOfParallelism, ct);
            }

            timings = timings with { Recompress = phase.Elapsed };
            phase.Restart();

            Dictionary<int, int> representative;
            int merged = 0;
            if (options.DeduplicateObjects)
            {
                (representative, merged) = Deduplicator.Run(file, graph, ct);
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
                OutputVersion(file.HeaderVersion, objectStreams), options.ZlibLevel);
            ReadOnlyMemory<byte> candidate = PdfDocumentWriter.Write(objects, trailer, Map, settings);
            timings = timings with { Write = phase.Elapsed };
            phase.Restart();

            var stats = new OptimizationStatistics(
                file.Xref.Count(e => e.Value.Type != XrefEntryType.Free),
                graph.Order.Count,
                objects.Count,
                recompressed,
                merged,
                graph.StrippedEntries,
                0,
                TimeSpan.Zero,
                timings);

            double saving = 100.0 * (input.Length - candidate.Length) / input.Length;
            if (saving < options.MinSavingPercent)
            {
                return new Outcome(OptimizationDisposition.OriginalRetained, null,
                    string.Create(CultureInfo.InvariantCulture, $"Rewrite saved {saving:F2}%, below the {options.MinSavingPercent}% threshold."),
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
                string? difference = EquivalenceVerifier.Verify(reference, written, ignored, options.MaxDecodedStreamBytes,
                    options.MaxDegreeOfParallelism, ct, out int compared);
                if (difference is not null)
                {
                    return new Outcome(OptimizationDisposition.OriginalRetained, null, "Verification failed: " + difference, pageCount, stats);
                }

                stats = stats with { ObjectsVerified = compared, Phases = timings with { Verify = phase.Elapsed } };
            }

            string reason = options.Verify
                ? "Rewritten and verified equivalent to the source."
                : "Rewritten. Verification was disabled.";
            return new Outcome(OptimizationDisposition.Optimized, candidate, reason, pageCount, stats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Retained(OptimizationDisposition.OriginalRetained, $"Rewrite failed ({ex.GetType().Name}): {ex.Message}");
        }

        Outcome Retained(OptimizationDisposition disposition, string reason) => new(disposition, null, reason, pageCount, null);
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

    /// <summary>PDF/A-1 is based on PDF 1.4 and forbids object and xref streams.</summary>
    internal static bool IsPdfA1(PdfFile file, long decodeLimit)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (file.Resolve(catalog?["Metadata"]) is not PdfStream metadata) return false;
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(metadata.Data, StreamCodec.GetFilters(metadata.Dictionary, file.Resolve),
                DecodeDepth.Full, decodeLimit, file.Resolve);
            return PdfAPartOne().IsMatch(Encoding.Latin1.GetString(decoded.Data.Span));
        }
        catch (StreamDecodeException)
        {
            // Unknown conformance: stay on the safe side.
            return true;
        }
    }

    [GeneratedRegex("""pdfaid:part\s*(=\s*["']\s*1\s*["']|>\s*1\s*<)""")]
    private static partial Regex PdfAPartOne();

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
