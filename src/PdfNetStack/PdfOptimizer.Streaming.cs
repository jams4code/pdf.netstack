using System.Diagnostics;
using System.Security.Cryptography;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>
/// Streaming mode (<see cref="PdfOptimizerOptions.Streaming"/>): the same passes and the same
/// proof as in memory, with the source read on demand and the rewrite written progressively to
/// a temporary file, which the proof reads back on demand.
/// </summary>
public static partial class PdfOptimizer
{
    // Streams are recompressed in batches of at most this much source data; a larger stream goes alone.
    private const long StreamingBatchBytes = 32L << 20;

    /// <summary>The source of a streaming run: a buffer, or a seekable stream whose content starts at offset 0.</summary>
    private sealed class Source
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private readonly Stream? _stream;

        public Source(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
            Length = memory.Length;
        }

        public Source(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        public PdfFile Open(long decodeLimit) => _stream is null ? PdfFile.Open(_memory, decodeLimit) : PdfFile.Open(_stream, decodeLimit);

        /// <summary>The file's SHA-256, and whether it holds a signature's /ByteRange, from one read of the file.</summary>
        public (string Hash, bool Signed) Scan(CancellationToken ct)
        {
            if (_stream is null) return (Sha256Hex(_memory.Span), _memory.Span.IndexOf("/ByteRange"u8) >= 0);

            ReadOnlySpan<byte> marker = "/ByteRange"u8;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 20];
            bool signed = false;
            int carry = 0;
            _stream.Position = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = _stream.Read(buffer, carry, buffer.Length - carry);
                if (read == 0) break;
                hash.AppendData(buffer, carry, read);
                int filled = carry + read;
                signed = signed || buffer.AsSpan(0, filled).IndexOf(marker) >= 0;

                // The end of this read may hold the start of the marker.
                carry = Math.Min(marker.Length - 1, filled);
                Buffer.BlockCopy(buffer, filled - carry, buffer, 0, carry);
            }

            return (Compat.ToHex(hash.GetHashAndReset()).ToLowerInvariant(), signed);
        }

        public void CopyTo(Stream output, CancellationToken ct)
        {
            if (_stream is null)
            {
                output.Write(_memory.Span);
                return;
            }

            _stream.Position = 0;
            OutputStreams.Copy(_stream, output, ct);
        }

        public async Task CopyToAsync(Stream output, CancellationToken ct)
        {
            if (_stream is null)
            {
                await OutputStreams.WriteAsync(output, _memory, ct).ConfigureAwait(false);
                return;
            }

            _stream.Position = 0;
            await OutputStreams.CopyAsync(_stream, output, ct).ConfigureAwait(false);
        }
    }

    /// <param name="Rewritten">The candidate file holds a proven rewrite to deliver; otherwise the input is delivered.</param>
    /// <param name="CandidateSha256">Lowercase hex SHA-256 of the rewrite, when there is one.</param>
    private sealed record StreamedOutcome(
        OptimizationDisposition Disposition,
        bool Rewritten,
        string Reason,
        int PageCount,
        OptimizationStatistics? Statistics,
        string CandidateSha256 = "",
        long CandidateLength = 0);

    /// <summary>Streaming mode for a source in memory or on a seekable stream, to any output.</summary>
    private static PdfOptimizationResult OptimizeStreaming(Source source, Stream output, PdfOptimizerOptions options, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        (string sourceHash, bool signed) = source.Scan(ct);
        StreamedOutcome outcome;
        using (FileStream candidate = TemporaryFile())
        {
            outcome = RunStreaming(source, signed, candidate, options, ct);
            if (outcome.Rewritten)
            {
                candidate.Position = 0;
                OutputStreams.Copy(candidate, output, ct);
            }
        }

        if (!outcome.Rewritten) source.CopyTo(output, ct);
        return StreamedResult(outcome, source.Length, sourceHash, clock.Elapsed);
    }

    /// <summary>Streaming mode for <see cref="OptimizeAsync"/>: the caller's streams are only read and written asynchronously.</summary>
    private static async Task<PdfOptimizationResult> OptimizeStreamingAsync(Stream input, Stream output, PdfOptimizerOptions options, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // A local file or a buffer can be read on demand; anything else, a network or request
        // stream among them, is copied to a temporary file first.
        bool inPlace = input is FileStream or MemoryStream && input.CanSeek && input.CanRead && input.Position == 0;
        FileStream? spool = null;
        try
        {
            if (!inPlace)
            {
                spool = TemporaryFile();
                await OutputStreams.CopyAsync(input, spool, ct).ConfigureAwait(false);
                await spool.FlushAsync(ct).ConfigureAwait(false);
            }

            var source = new Source(spool ?? input);
            (string sourceHash, bool signed) = source.Scan(ct);
            StreamedOutcome outcome;
            using (FileStream candidate = TemporaryFile())
            {
                outcome = RunStreaming(source, signed, candidate, options, ct);
                if (outcome.Rewritten)
                {
                    candidate.Position = 0;
                    await OutputStreams.CopyAsync(candidate, output, ct).ConfigureAwait(false);
                }
            }

            if (!outcome.Rewritten) await source.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return StreamedResult(outcome, source.Length, sourceHash, clock.Elapsed);
        }
        finally
        {
            spool?.Dispose();
        }
    }

    /// <summary>
    /// Streaming mode for <see cref="OptimizeFileAsync"/>: the rewrite is written to the
    /// destination's temporary file, proven there, and moved into place.
    /// </summary>
    private static async Task<PdfOptimizationResult> OptimizeFileStreamingAsync(string source, string destination, PdfOptimizerOptions options, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PdfOptimizationResult result;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var candidate = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                var from = new Source(input);
                (string sourceHash, bool signed) = from.Scan(ct);
                StreamedOutcome outcome = RunStreaming(from, signed, candidate, options, ct);
                if (!outcome.Rewritten)
                {
                    candidate.SetLength(0);
                    await from.CopyToAsync(candidate, ct).ConfigureAwait(false);
                }

                await candidate.FlushAsync(ct).ConfigureAwait(false);
                result = StreamedResult(outcome, from.Length, sourceHash, clock.Elapsed);
            }

            Compat.MoveOverwrite(temp, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static PdfOptimizationResult StreamedResult(StreamedOutcome outcome, long inputLength, string sourceHash, TimeSpan elapsed)
    {
        OptimizationStatistics stats = (outcome.Statistics ?? OptimizationStatistics.Empty(TimeSpan.Zero)) with { Elapsed = elapsed, Streamed = true };
        return outcome.Rewritten
            ? new PdfOptimizationResult(outcome.Disposition, inputLength, outcome.CandidateLength, sourceHash, outcome.CandidateSha256, outcome.PageCount, outcome.Reason, stats)
            : new PdfOptimizationResult(outcome.Disposition, inputLength, inputLength, sourceHash, sourceHash, outcome.PageCount, outcome.Reason, stats);
    }

    /// <summary>A temporary file for a spooled input or a rewrite, deleted when closed.</summary>
    private static FileStream TemporaryFile() =>
        new(Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);

    /// <summary>
    /// The passes of <see cref="Run"/> on a file read on demand. The rewrite is written to
    /// <paramref name="candidate"/>, an empty, readable and seekable stream, and proven by reading
    /// it back; the outcome says whether it is to be delivered.
    /// </summary>
    private static StreamedOutcome RunStreaming(Source source, bool signed, Stream candidate, PdfOptimizerOptions options, CancellationToken ct)
    {
        if (source.Length > options.MaxInputBytes)
        {
            return new StreamedOutcome(OptimizationDisposition.UnsupportedRetained, false, "Input exceeds the configured size limit.", 0, null);
        }

        PdfFile file;
        try
        {
            file = source.Open(options.MaxDecodedStreamBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StreamedOutcome(OptimizationDisposition.UnsupportedRetained, false, "Not a readable PDF: " + ex.Message, 0, null);
        }

        int pageCount = 0;
        var phase = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        try
        {
            pageCount = CountPages(file);
            if (Unsupported(file, signed, options) is { } unsupported)
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

            // The structural passes change object dictionaries and, for merged fonts, a few
            // programs, all of which stay in memory as in the in-memory mode.
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

            timings = timings with { Content = phase.Elapsed };
            phase.Restart();

            // Recompression is planned now and done while writing. Duplicates must be known
            // before anything is written, so they are found from what each stream decodes to.
            StreamRecompressor.Work? work = null;
            var contentKeys = new Dictionary<int, byte[]>();
            if (options.RecompressStreams || compact.Count > 0)
            {
                JpegSettings? jpeg = options.OptimizeJpegImages ? new JpegSettings(options.JpegQuality, options.ProgressiveJpeg, options.MinImageSsim) : null;
                ImageSettings? images = options.MaxImageDpi is not null || options.ConvertImagesToJpeg || options.QuantizeImages
                    ? new ImageSettings(options.MaxImageDpi, options.ConvertImagesToJpeg, options.JpegQuality, options.MinImageSsim, options.ProgressiveJpeg) { Quantize = options.QuantizeImages }
                    : null;
                var scope = new RecompressionScope(options.RecompressStreams, compact, encoded, options.DeduplicateObjects);
                work = StreamRecompressor.Prepare(file, graph, scope, options.ZlibLevel, options.Compression == CompressionEffort.Exhaustive, options.UseImagePredictors,
                    options.OptimizeBilevelImages, jpeg, images, options.MaxDecodedStreamBytes, ct);
                if (options.DeduplicateObjects) contentKeys = ContentKeys(work, options.MaxDegreeOfParallelism, ct);
            }

            timings = timings with { Recompress = phase.Elapsed };
            phase.Restart();

            Dictionary<int, int> representative;
            int merged = 0;
            if (options.DeduplicateObjects)
            {
                (representative, merged) = Deduplicator.Run(file, graph, contentKeys, ct);
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
            var sourceNumbers = new List<int>(graph.Order.Count);
            foreach (int number in graph.Order)
            {
                if (representative[number] != number) continue;
                objects.Add(file.GetObject(number));
                sourceNumbers.Add(number);
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

            Func<int, PdfStream, PdfStream>? rewrite = work is null
                ? null
                : new BatchedRewrite(work, objects, sourceNumbers, options.MaxDegreeOfParallelism, ct).Rewrite;
            string candidateHash;
            long candidateLength;
            using (var hashing = new HashingStream(candidate))
            {
                foreach (ReadOnlyMemory<byte> chunk in PdfDocumentWriter.WriteChunks(objects, trailer, Map, settings, rewrite, ct))
                {
                    hashing.Write(chunk.Span);
                }

                candidate.Flush();
                candidateHash = hashing.Hash();
                candidateLength = hashing.Length;
            }

            timings = timings with { Write = phase.Elapsed };
            phase.Restart();

            RecompressionCounts recompressed = work?.Counts(representative) ?? new RecompressionCounts(0, 0, []);
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
                Streamed = true,
            };

            double saving = 100.0 * (source.Length - candidateLength) / Math.Max(1, source.Length);
            if (saving < options.MinSavingPercent)
            {
                return new StreamedOutcome(OptimizationDisposition.OriginalRetained, false,
                    Compat.Format("Rewrite saved {0:F2}%, below the {1}% threshold.", saving, options.MinSavingPercent),
                    pageCount, stats);
            }

            if (options.Verify)
            {
                ct.ThrowIfCancellationRequested();

                // A second reader over the source that has parsed nothing yet sees the file as it
                // is, whatever the passes did to this reader's objects.
                PdfFile reference = file.Fork();
                PdfFile written = PdfFile.Open(candidate, options.MaxDecodedStreamBytes);
                if (written.WasReconstructed || written.LengthsRepaired > 0)
                {
                    return new StreamedOutcome(OptimizationDisposition.OriginalRetained, false, "Verification failed: output is not well formed.", pageCount, stats);
                }

                var ignored = new List<string>();
                if (options.RemovePageThumbnails) ignored.Add("Thumb");
                if (options.RemovePieceInfo) ignored.Add("PieceInfo");
                var rules = new VerificationRules
                {
                    IgnoredSourceKeys = ignored,
                    Images = new ImageAllowance(recompressed.RequantizedObjects.Contains, recompressed.ResampledObjects.Contains, options.MinImageSsim),
                    AllowResourcePruning = pruned > 0,

                    // A content stream merged with one that was compacted is written compacted too.
                    AllowContentCompaction = compact.Count > 0,
                    AllowFontMerging = fontsMerged > 0,
                    AllowBilevelRecoding = recompressed.Group4 > 0,
                };
                string? difference = EquivalenceVerifier.Verify(reference, written, rules, options.MaxDecodedStreamBytes,
                    options.MaxDegreeOfParallelism, ct, out int compared);

                // Stream lengths are checked as the proof parses each stream of the output.
                if (difference is null && written.LengthsRepaired > 0) difference = "output is not well formed";
                if (difference is not null)
                {
                    return new StreamedOutcome(OptimizationDisposition.OriginalRetained, false, "Verification failed: " + difference, pageCount, stats);
                }

                stats = stats with { ObjectsVerified = compared, Phases = timings with { Verify = phase.Elapsed } };
            }

            return new StreamedOutcome(OptimizationDisposition.Optimized, true, Reason(options, recompressed), pageCount, stats, candidateHash, candidateLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Retained(OptimizationDisposition.OriginalRetained, $"Rewrite failed ({ex.GetType().Name}): {ex.Message}");
        }

        StreamedOutcome Retained(OptimizationDisposition disposition, string reason) => new(disposition, false, reason, pageCount, null);
    }

    /// <summary>
    /// <see cref="StreamRecompressor.Work.ContentKey"/> for every planned stream, side by side.
    /// Each worker holds one decoded stream at a time.
    /// </summary>
    private static Dictionary<int, byte[]> ContentKeys(StreamRecompressor.Work work, int parallelism, CancellationToken ct)
    {
        var keys = new byte[]?[work.Count];
        var partitioner = System.Collections.Concurrent.Partitioner.Create(Enumerable.Range(0, work.Count), System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);
        Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i => keys[i] = work.ContentKey(i));
        var result = new Dictionary<int, byte[]>();
        for (int i = 0; i < work.Count; i++)
        {
            if (keys[i] is { } key) result[work.Numbers[i]] = key;
        }

        return result;
    }

    /// <summary>
    /// Recompresses streams for the writer a batch at a time. When the writer asks for a planned
    /// stream that is not ready, the streams from it on, in the order the writer asks for them, are
    /// recompressed side by side up to <see cref="StreamingBatchBytes"/> of source data; each is
    /// handed over once and then forgotten, so at most one batch is held.
    /// </summary>
    private sealed class BatchedRewrite
    {
        private readonly StreamRecompressor.Work _work;
        private readonly int _parallelism;
        private readonly CancellationToken _ct;

        // For each output number less one, the stream's index in the work, or -1 when it has none.
        private readonly int[] _workIndex;

        // Output numbers of the streams with work planned, ascending: the order the writer asks.
        private readonly List<int> _queue = [];
        private readonly Dictionary<int, PdfStream?> _ready = new();

        public BatchedRewrite(StreamRecompressor.Work work, List<PdfObject> objects, List<int> sourceNumbers, int parallelism, CancellationToken ct)
        {
            _work = work;
            _parallelism = parallelism;
            _ct = ct;
            _workIndex = new int[objects.Count];
            for (int n = 0; n < objects.Count; n++)
            {
                int i = objects[n] is PdfStream ? work.IndexOf(sourceNumbers[n]) : -1;
                _workIndex[n] = i >= 0 && work.IsPlanned(i) ? i : -1;
                if (_workIndex[n] >= 0) _queue.Add(n + 1);
            }
        }

        public PdfStream Rewrite(int outputNumber, PdfStream stream)
        {
            if (_workIndex[outputNumber - 1] < 0) return stream;
            if (!_ready.ContainsKey(outputNumber)) RunBatch(outputNumber);
            _ready.TryGetValue(outputNumber, out PdfStream? rewritten);
            _ready.Remove(outputNumber);
            return rewritten ?? stream;
        }

        private void RunBatch(int outputNumber)
        {
            _ready.Clear();
            int start = _queue.BinarySearch(outputNumber);
            if (start < 0) start = ~start;
            var batch = new List<int>();
            long bytes = 0;
            for (int k = start; k < _queue.Count; k++)
            {
                long size = _work.Streams[_workIndex[_queue[k] - 1]].EncodedLength;
                if (batch.Count > 0 && (bytes + size > StreamingBatchBytes || batch.Count >= 4 * _parallelism)) break;
                batch.Add(_queue[k]);
                bytes += size;
            }

            // Largest first, so one big image never runs alone at the end of the batch.
            var results = new PdfStream?[batch.Count];
            int[] schedule = Enumerable.Range(0, batch.Count).OrderByDescending(k => _work.Streams[_workIndex[batch[k] - 1]].EncodedLength).ToArray();
            var partitioner = System.Collections.Concurrent.Partitioner.Create(schedule, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = _ct },
                k => results[k] = _work.Rewritten(_workIndex[batch[k] - 1], _ct));
            for (int k = 0; k < batch.Count; k++) _ready[batch[k]] = results[k];
        }
    }

    /// <summary>Passes writes through to another stream, hashing and counting them. Disposing it leaves the other stream open.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        /// <summary>Lowercase hex SHA-256 of everything written so far.</summary>
        public string Hash() => Compat.ToHex(_hash.GetHashAndReset()).ToLowerInvariant();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
            _length += count;
        }

#if NET8_0_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
            _length += buffer.Length;
        }
#endif

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
