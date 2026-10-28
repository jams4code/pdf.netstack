using System.IO.Compression;

namespace PdfNetStack.Filters;

/// <summary>Raised when a stream cannot be decoded or exceeds the decoded size ceiling.</summary>
internal sealed class StreamDecodeException : Exception
{
    public StreamDecodeException(string message) : base(message) { }

    public StreamDecodeException(string message, Exception inner) : base(message, inner) { }

    public StreamDecodeException() { }
}

/// <summary>
/// Growable output buffer with a hard ceiling, the defence against decompression bombs.
/// Growth uses pooled arrays so large decodes do not leave a trail of discarded large-object-heap
/// buffers; only the final exact-size copy is a fresh allocation.
/// </summary>
internal sealed class BoundedBuffer : IDisposable
{
    private readonly long _limit;
    private byte[] _buffer;
    private int _length;

    public BoundedBuffer(int initialCapacity, long limit)
    {
        _limit = limit;
        _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(256, Math.Min(initialCapacity, (int)Math.Min(limit, 1 << 26))));
    }

    public int Length => _length;

    public void Write(byte b)
    {
        if (_length == _buffer.Length) Grow(1);
        _buffer[_length++] = b;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length) Grow(data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Appends <paramref name="count"/> bytes and returns them for the caller to fill.</summary>
    public Span<byte> Extend(int count)
    {
        if (_length + count > _buffer.Length) Grow(count);
        Span<byte> span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    /// <summary>Copies an earlier slice of the output to the end, as used by LZW.</summary>
    public void CopyWithin(int start, int count)
    {
        if (_length + count > _buffer.Length) Grow(count);
        Buffer.BlockCopy(_buffer, start, _buffer, _length, count);
        _length += count;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    /// <summary>Mutable view of the written bytes, for in-place transforms.</summary>
    public Span<byte> WritableSpan => _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
    }

    private void Grow(int needed)
    {
        long required = (long)_length + needed;
        if (required > _limit) throw new StreamDecodeException("Decoded stream exceeds the configured size limit.");
        long next = Math.Max(required, Math.Min(_limit, (long)_buffer.Length * 2));
        if (next > Array.MaxLength) throw new StreamDecodeException("Decoded stream is too large.");
        byte[] grown = System.Buffers.ArrayPool<byte>.Shared.Rent((int)next);
        Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
        System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}

internal static class Decoders
{
    /// <summary>
    /// Inflates zlib data. Raw deflate without the zlib header is deliberately not accepted:
    /// arbitrary bytes can inflate into garbage without error, and re-encoding that garbage as a
    /// valid stream would make other readers draw something they currently ignore.
    /// </summary>
    public static BoundedBuffer Flate(ReadOnlyMemory<byte> data, long limit) => InflateWith(data, limit, raw: false);

    private static BoundedBuffer InflateWith(ReadOnlyMemory<byte> data, long limit, bool raw)
    {
        using var input = new ReadOnlyMemoryStream(data);
        using Stream inflater = raw
            ? new DeflateStream(input, CompressionMode.Decompress)
            : new ZLibStream(input, CompressionMode.Decompress);
        var output = new BoundedBuffer(data.Length * 4, limit);
        byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk.AsSpan(0, read));
            }

            return output;
        }
        catch (InvalidDataException ex)
        {
            output.Dispose();
            throw new StreamDecodeException("Invalid Flate data.", ex);
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public static BoundedBuffer Lzw(ReadOnlySpan<byte> data, int earlyChange, long limit)
    {
        var output = new BoundedBuffer(data.Length * 3, limit);
        try
        {
            LzwCore(output, data, earlyChange, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void LzwCore(BoundedBuffer output, ReadOnlySpan<byte> data, int earlyChange, long limit)
    {
        var prefixStart = new int[4096];
        var prefixLength = new int[4096];
        int bitBuffer = 0, bitCount = 0, pos = 0;
        int codeLength = 9;
        int nextCode = 258;
        int previousStart = -1, previousLength = 0;

        while (true)
        {
            while (bitCount < codeLength)
            {
                if (pos >= data.Length) return;
                bitBuffer = (bitBuffer << 8) | data[pos++];
                bitCount += 8;
            }

            int code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
            bitCount -= codeLength;

            if (code == 256)
            {
                codeLength = 9;
                nextCode = 258;
                previousStart = -1;
                continue;
            }

            if (code == 257) break;

            int start = output.Length;
            if (code < 256)
            {
                output.Write((byte)code);
                if (previousStart >= 0) AddEntry(previousStart, previousLength + 1);
                previousStart = start;
                previousLength = 1;
            }
            else if (code < nextCode)
            {
                output.CopyWithin(prefixStart[code], prefixLength[code]);
                if (previousStart >= 0) AddEntry(previousStart, previousLength + 1);
                previousStart = start;
                previousLength = prefixLength[code];
            }
            else if (code == nextCode && previousStart >= 0)
            {
                // KwKwK case: previous string plus its own first byte.
                output.CopyWithin(previousStart, previousLength);
                output.CopyWithin(previousStart, 1);
                AddEntry(start, previousLength + 1);
                previousStart = start;
                previousLength = previousLength + 1;
            }
            else
            {
                throw new StreamDecodeException("Invalid LZW code.");
            }

            int threshold = (1 << codeLength) - earlyChange;
            if (nextCode >= threshold && codeLength < 12) codeLength++;
        }

        return;

        void AddEntry(int start, int length)
        {
            if (nextCode >= 4096) return;
            prefixStart[nextCode] = start;
            prefixLength[nextCode] = length;
            nextCode++;
        }
    }

    public static BoundedBuffer AsciiHex(ReadOnlySpan<byte> data, long limit)
    {
        var output = new BoundedBuffer(data.Length / 2 + 1, limit);
        try
        {
            AsciiHexCore(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void AsciiHexCore(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        int high = -1;
        foreach (byte b in data)
        {
            if (b == (byte)'>') break;
            int v = Parsing.PdfSyntax.HexValue(b);
            if (v < 0)
            {
                if (Parsing.PdfSyntax.IsWhitespace(b)) continue;
                throw new StreamDecodeException("Invalid character in ASCIIHex data.");
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                output.Write((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0) output.Write((byte)(high << 4));
    }

    public static BoundedBuffer Ascii85(ReadOnlySpan<byte> data, long limit)
    {
        var output = new BoundedBuffer(data.Length, limit);
        try
        {
            Ascii85Core(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Ascii85Core(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        Span<byte> group = stackalloc byte[5];
        int n = 0;
        int i = 0;
        if (data.Length >= 2 && data[0] == (byte)'<' && data[1] == (byte)'~') i = 2;
        for (; i < data.Length; i++)
        {
            byte b = data[i];
            if (Parsing.PdfSyntax.IsWhitespace(b)) continue;
            if (b == (byte)'~') break;
            if (b == (byte)'z' && n == 0)
            {
                output.Write([0, 0, 0, 0]);
                continue;
            }

            if (b < (byte)'!' || b > (byte)'u') throw new StreamDecodeException("Invalid character in ASCII85 data.");
            group[n++] = b;
            if (n == 5)
            {
                WriteGroup(output, group, 4);
                n = 0;
            }
        }

        if (n == 1) throw new StreamDecodeException("Truncated ASCII85 group.");
        if (n > 1)
        {
            for (int k = n; k < 5; k++) group[k] = (byte)'u';
            WriteGroup(output, group, n - 1);
        }
    }

    private static void WriteGroup(BoundedBuffer output, ReadOnlySpan<byte> group, int count)
    {
        ulong value = 0;
        for (int k = 0; k < 5; k++) value = value * 85 + (ulong)(group[k] - '!');
        if (value > uint.MaxValue) throw new StreamDecodeException("ASCII85 group overflow.");
        Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        output.Write(bytes[..count]);
    }

    public static BoundedBuffer RunLength(ReadOnlySpan<byte> data, long limit)
    {
        var output = new BoundedBuffer(data.Length * 2, limit);
        try
        {
            RunLengthCore(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void RunLengthCore(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128) break;
            if (length < 128)
            {
                int count = length + 1;
                if (i + count > data.Length) count = data.Length - i;
                output.Write(data.Slice(i, count));
                i += count;
            }
            else
            {
                if (i >= data.Length) break;
                byte value = data[i++];
                for (int k = 0; k < 257 - length; k++) output.Write(value);
            }
        }
    }
}

/// <summary>Read-only stream over a <see cref="ReadOnlyMemory{T}"/> without copying.</summary>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) => _data = data;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _data.Length;

    public override long Position
    {
        get => _position;
        set => _position = (int)Math.Clamp(value, 0, _data.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _data.Length - _position);
        if (n <= 0) return 0;
        _data.Span.Slice(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _data.Length + offset,
        };
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
