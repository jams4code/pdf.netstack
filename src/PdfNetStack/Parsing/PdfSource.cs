namespace PdfNetStack.Parsing;

/// <summary>
/// Random access to the bytes of a PDF: a buffer in memory, or a seekable stream read on
/// demand. Offsets are 64 bit, so a file on a stream may be larger than any one array.
/// </summary>
internal abstract class PdfSource
{
    /// <summary>Length of the file in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>
    /// The bytes from <paramref name="start"/> up to <paramref name="end"/>, clamped to the file.
    /// In memory this is a slice of the buffer; from a stream it is a fresh copy.
    /// </summary>
    public abstract ReadOnlyMemory<byte> Read(long start, long end);

    /// <summary>Offset of the first <paramref name="pattern"/> starting in [<paramref name="start"/>, <paramref name="end"/>), or -1.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long start, long end)
    {
        end = Math.Min(end, Length);
        int overlap = pattern.Length - 1;
        for (long at = Math.Max(0, start); at < end; at += ChunkSize)
        {
            long chunkEnd = Math.Min(end, at + ChunkSize);
            ReadOnlySpan<byte> window = Read(at, Math.Min(Length, chunkEnd + overlap)).Span;
            int found = window.IndexOf(pattern);
            if (found >= 0 && at + found < end) return at + found;
        }

        return -1;
    }

    /// <summary>Offset of the last <paramref name="pattern"/> that ends at or before <paramref name="end"/>, or -1.</summary>
    public long LastIndexOf(ReadOnlySpan<byte> pattern, long end)
    {
        end = Math.Min(end, Length);
        int overlap = pattern.Length - 1;
        for (long stop = end; stop > 0; stop -= ChunkSize)
        {
            long start = Math.Max(0, stop - ChunkSize - overlap);
            int found = Read(start, stop).Span.LastIndexOf(pattern);
            if (found >= 0) return start + found;
        }

        return -1;
    }

    /// <summary>How much a search reads at a time. In memory the whole file is one chunk.</summary>
    protected abstract long ChunkSize { get; }
}

/// <summary>A PDF held in memory. Reads are slices, never copies.</summary>
internal sealed class MemorySource(ReadOnlyMemory<byte> data) : PdfSource
{
    public ReadOnlyMemory<byte> Data { get; } = data;

    public override long Length => Data.Length;

    protected override long ChunkSize => Math.Max(1, Data.Length);

    public override ReadOnlyMemory<byte> Read(long start, long end)
    {
        start = Math.Max(0, Math.Min(start, Data.Length));
        end = Math.Max(start, Math.Min(end, Data.Length));
        return Data[(int)start..(int)end];
    }
}

/// <summary>
/// A PDF on a seekable stream. Nothing is read until asked for, and only the bytes asked for
/// are kept, apart from one 4 KB block cached for the many short reads parsing makes. Reads
/// are serialized, so the stream must not be used by anyone else while the file is open.
/// </summary>
internal sealed class StreamSource : PdfSource
{
    // Small enough that reading one object header costs little, large enough to serve the
    // windows a parse reads one after another.
    private const int BlockSize = 4096;

    private readonly Stream _stream;
    private readonly long _length;
    private readonly object _gate = new();
    private byte[] _block = [];
    private long _blockStart = -1;

    public StreamSource(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        _stream = stream;
        _length = stream.Length;
    }

    public override long Length => _length;

    protected override long ChunkSize => 1 << 20;

    /// <summary>Total bytes read from the stream so far, cached reads excluded.</summary>
    public long BytesRead { get; private set; }

    public override ReadOnlyMemory<byte> Read(long start, long end)
    {
        start = Math.Max(0, Math.Min(start, _length));
        end = Math.Max(start, Math.Min(end, _length));
        long count = end - start;
        if (count > Compat.MaxArrayLength) throw new PdfFormatException("A single read exceeds the largest supported buffer.");
        if (count == 0) return ReadOnlyMemory<byte>.Empty;

        lock (_gate)
        {
            if (count <= BlockSize)
            {
                if (_blockStart < 0 || start < _blockStart || end > _blockStart + _block.Length)
                {
                    _blockStart = start;
                    _block = ReadExactly(start, (int)Math.Min(BlockSize, _length - start));
                }

                int offset = (int)(start - _blockStart);
                int available = (int)Math.Min(count, Math.Max(0, _block.Length - offset));
                return _block.AsMemory(Math.Min(offset, _block.Length), available).ToArray();
            }

            return ReadExactly(start, (int)count);
        }
    }

    private byte[] ReadExactly(long start, int count)
    {
        var buffer = new byte[count];
        _stream.Position = start;
        int total = 0;
        while (total < count)
        {
            int read = _stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }

        BytesRead += total;

        // The stream shrank after it was opened; what is left is all there is.
        return total == count ? buffer : buffer.AsSpan(0, total).ToArray();
    }
}
