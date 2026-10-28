using System.IO.Compression;
using PdfNetStack.Objects;

namespace PdfNetStack.Filters;

internal readonly record struct FilterSpec(string Name, PdfObject? Parms);

/// <summary>
/// Result of peeling decodable layers off a stream. Owns a pooled buffer: dispose it once the
/// data has been consumed, and copy <see cref="Data"/> first if it must outlive the result.
/// </summary>
internal sealed class DecodedStream : IDisposable
{
    private BoundedBuffer? _lease;

    public DecodedStream(BoundedBuffer? lease, ReadOnlyMemory<byte> data, List<FilterSpec> remaining, PredictorParams? pendingPredictor, int layersDecoded)
    {
        _lease = lease;
        Data = data;
        Remaining = remaining;
        PendingPredictor = pendingPredictor;
        LayersDecoded = layersDecoded;
    }

    public ReadOnlyMemory<byte> Data { get; private set; }

    public void Dispose()
    {
        Data = default;
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>Filters that were not decoded, outermost first (for example DCTDecode).</summary>
    public List<FilterSpec> Remaining { get; }

    /// <summary>Set when decoding stopped with predictor encoded bytes still in <see cref="Data"/>.</summary>
    public PredictorParams? PendingPredictor { get; }

    public int LayersDecoded { get; }
}

internal enum DecodeDepth
{
    /// <summary>Undo compression only; predictor transforms stay in place.</summary>
    CompressionOnly,

    /// <summary>Undo compression and predictors, producing the logical stream content.</summary>
    Full,
}

internal static class StreamCodec
{
    public static string NormalizeFilterName(string name) => name switch
    {
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "Fl" => "FlateDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    public static List<FilterSpec> GetFilters(PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve)
    {
        var result = new List<FilterSpec>();
        PdfObject? filter = resolve(dict["Filter"]);
        PdfObject? parms = resolve(dict["DecodeParms"]);
        switch (filter)
        {
            case PdfName name:
                result.Add(new FilterSpec(NormalizeFilterName(name.Value), parms is PdfArray pa && pa.Count > 0 ? resolve(pa[0]) : parms));
                break;
            case PdfArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (resolve(array[i]) is not PdfName n) throw new StreamDecodeException("Filter array contains a non-name.");
                    PdfObject? p = parms switch
                    {
                        PdfArray parmArray => i < parmArray.Count ? resolve(parmArray[i]) : null,
                        PdfDictionary d when i == 0 && array.Count == 1 => d,
                        _ => null,
                    };
                    result.Add(new FilterSpec(NormalizeFilterName(n.Value), p is PdfNull ? null : p));
                }

                break;
        }

        return result;
    }

    public static bool IsDecodable(string filter) => filter is
        "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "RunLengthDecode";

    public static DecodedStream Decode(
        ReadOnlyMemory<byte> raw,
        List<FilterSpec> filters,
        DecodeDepth depth,
        long limit,
        Func<PdfObject?, PdfObject?> resolve)
    {
        ReadOnlyMemory<byte> current = raw;
        BoundedBuffer? lease = null;
        int i = 0;
        try
        {
            for (; i < filters.Count; i++)
            {
                FilterSpec f = filters[i];
                if (!IsDecodable(f.Name)) break;

                var parms = resolve(f.Parms) as PdfDictionary;
                BoundedBuffer next = f.Name switch
                {
                    "FlateDecode" => Decoders.Flate(current, limit),
                    "LZWDecode" => Decoders.Lzw(current.Span, GetInt(parms, "EarlyChange", 1, resolve), limit),
                    "ASCIIHexDecode" => Decoders.AsciiHex(current.Span, limit),
                    "ASCII85Decode" => Decoders.Ascii85(current.Span, limit),
                    _ => Decoders.RunLength(current.Span, limit),
                };
                lease?.Dispose();
                lease = next;
                current = next.Memory;

                if (f.Name is "FlateDecode" or "LZWDecode")
                {
                    PredictorParams p = ReadPredictor(parms, resolve);
                    if (p.Predictor > 1)
                    {
                        if (depth == DecodeDepth.CompressionOnly || !Predictors.TryDecode(current.Span, p, limit, out BoundedBuffer? unpredicted))
                        {
                            var pending = new DecodedStream(lease, current, filters.GetRange(i + 1, filters.Count - i - 1), p, i + 1);
                            lease = null;
                            return pending;
                        }

                        lease.Dispose();
                        lease = unpredicted!;
                        current = lease.Memory;
                    }
                }
            }

            var result = new DecodedStream(lease, current, filters.GetRange(i, filters.Count - i), null, i);
            lease = null;
            return result;
        }
        finally
        {
            // Only reached with a live lease when decoding failed part way.
            lease?.Dispose();
        }
    }

    public static PredictorParams ReadPredictor(PdfDictionary? parms, Func<PdfObject?, PdfObject?> resolve) => new(
        GetInt(parms, "Predictor", 1, resolve),
        GetInt(parms, "Colors", 1, resolve),
        GetInt(parms, "BitsPerComponent", 8, resolve),
        GetInt(parms, "Columns", 1, resolve));

    public static PdfDictionary PredictorDictionary(PredictorParams p)
    {
        var d = new PdfDictionary();
        d.Set("Predictor", PdfNumber.FromInteger(p.Predictor));
        if (p.Colors != 1) d.Set("Colors", PdfNumber.FromInteger(p.Colors));
        if (p.BitsPerComponent != 8) d.Set("BitsPerComponent", PdfNumber.FromInteger(p.BitsPerComponent));
        if (p.Columns != 1) d.Set("Columns", PdfNumber.FromInteger(p.Columns));
        return d;
    }

    private static int GetInt(PdfDictionary? d, string key, int fallback, Func<PdfObject?, PdfObject?> resolve) =>
        d is not null && resolve(d[key]) is PdfNumber { IsInteger: true } n && n.IntegerValue is >= int.MinValue and <= int.MaxValue
            ? (int)n.IntegerValue
            : fallback;

    public static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new PooledWriteStream(Math.Max(256, data.Length / 2));
        using (var z = new ZLibStream(output, level, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }
}

/// <summary>Write-only stream over pooled arrays; only the final exact-size copy is allocated.</summary>
internal sealed class PooledWriteStream : Stream
{
    private byte[] _buffer;
    private int _length;

    public PooledWriteStream(int capacity) => _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            long next = Math.Max((long)_length + data.Length, (long)_buffer.Length * 2);
            byte[] grown = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(next, Array.MaxLength));
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer.Length > 0)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        base.Dispose(disposing);
    }
}
