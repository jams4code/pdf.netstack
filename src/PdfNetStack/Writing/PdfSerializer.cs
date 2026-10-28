using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Writing;

/// <summary>Growable byte buffer tuned for many small writes.</summary>
internal sealed class ByteSink
{
    private byte[] _buffer;

    public ByteSink(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Length);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, Length);

    public void Clear() => Length = 0;

    public void Write(byte b)
    {
        if (Length == _buffer.Length) Grow(1);
        _buffer[Length++] = b;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (Length + data.Length > _buffer.Length) Grow(data.Length);
        data.CopyTo(_buffer.AsSpan(Length));
        Length += data.Length;
    }

    public void WriteAscii(string text)
    {
        int max = text.Length;
        if (Length + max > _buffer.Length) Grow(max);
        Length += Encoding.ASCII.GetBytes(text, _buffer.AsSpan(Length));
    }

    public void WriteInteger(long value)
    {
        if (Length + 20 > _buffer.Length) Grow(20);
        if (value < 0)
        {
            _buffer[Length++] = (byte)'-';
            if (value == long.MinValue)
            {
                WriteAscii("9223372036854775808");
                return;
            }

            value = -value;
        }

        // Digits are produced right to left into a small scratch area, then copied.
        Span<byte> digits = stackalloc byte[20];
        int n = 0;
        do
        {
            digits[n++] = (byte)('0' + (int)(value % 10));
            value /= 10;
        }
        while (value != 0);
        while (n > 0) _buffer[Length++] = digits[--n];
    }

    public byte LastByte => Length == 0 ? (byte)' ' : _buffer[Length - 1];

    public byte[] ToArray() => WrittenSpan.ToArray();

    private void Grow(int needed)
    {
        long next = Math.Max((long)Length + needed, (long)_buffer.Length * 2);
        if (next > Compat.MaxArrayLength) next = Math.Max((long)Length + needed, Compat.MaxArrayLength);
        Array.Resize(ref _buffer, (int)next);
    }
}

/// <summary>
/// Writes PDF objects with the minimum syntax needed: a separator is emitted only where two
/// regular characters would otherwise merge into one token.
/// </summary>
internal sealed class PdfSerializer
{
    private readonly Func<PdfReference, int> _mapReference;

    /// <param name="mapReference">
    /// Maps a source reference to its output object number. A negative result means the target
    /// does not exist, and the reference is written as null, which is what it meant in the source.
    /// </param>
    public PdfSerializer(Func<PdfReference, int> mapReference) => _mapReference = mapReference;

    /// <summary>Applied to every string before it is written; used for encryption of in-file objects.</summary>
    public Func<byte[], byte[]>? StringTransform { get; set; }

    public void Write(ByteSink sink, PdfObject obj)
    {
        switch (obj)
        {
            case PdfNull:
                WriteRegular(sink, "null"u8);
                break;
            case PdfBoolean b:
                WriteRegular(sink, b.Value ? "true"u8 : "false"u8);
                break;
            case PdfNumber n:
                WriteRegular(sink, n.Text);
                break;
            case PdfName name:
                WriteName(sink, name.Value);
                break;
            case PdfString str:
                WriteString(sink, StringTransform is null ? str.Value : StringTransform(str.Value));
                break;
            case PdfArray array:
                sink.Write((byte)'[');
                foreach (PdfObject item in array.Items) Write(sink, item);
                sink.Write((byte)']');
                break;
            case PdfDictionary dict:
                WriteDictionary(sink, dict, skipKey: null);
                break;
            case PdfReference r:
            {
                int target = _mapReference(r);
                if (target < 0)
                {
                    WriteRegular(sink, "null"u8);
                }
                else
                {
                    Separate(sink);
                    sink.WriteInteger(target);
                    sink.Write(" 0 R"u8);
                }

                break;
            }

            case PdfStream:
                throw new InvalidOperationException("Streams are written by the document writer.");
            default:
                throw new InvalidOperationException("Unknown object type.");
        }
    }

    /// <summary>Writes a dictionary, dropping null valued entries, which the specification treats as absent.</summary>
    public void WriteDictionary(ByteSink sink, PdfDictionary dict, string? skipKey)
    {
        sink.Write("<<"u8);
        for (int i = 0; i < dict.Count; i++)
        {
            (string key, PdfObject value) = dict.EntryAt(i);
            if (skipKey is not null && key == skipKey) continue;
            if (IsNullValue(value)) continue;
            WriteName(sink, key);
            Write(sink, value);
        }

        sink.Write(">>"u8);
    }

    private bool IsNullValue(PdfObject value) => value is PdfNull || (value is PdfReference r && _mapReference(r) < 0);

    private static void Separate(ByteSink sink)
    {
        // A trailing '/' can only be an empty name, which would absorb the next regular token.
        byte last = sink.LastByte;
        if (PdfSyntax.IsRegular(last) || last == (byte)'/') sink.Write((byte)' ');
    }

    private static void WriteRegular(ByteSink sink, ReadOnlySpan<byte> token)
    {
        Separate(sink);
        sink.Write(token);
    }

    public static void WriteName(ByteSink sink, string name)
    {
        sink.Write((byte)'/');
        foreach (char c in name)
        {
            byte b = (byte)c;
            if (b is > 0x20 and < 0x7F && b != (byte)'#' && PdfSyntax.IsRegular(b))
            {
                sink.Write(b);
            }
            else
            {
                sink.Write((byte)'#');
                sink.Write(HexDigit(b >> 4));
                sink.Write(HexDigit(b & 0xF));
            }
        }
    }

    private static byte HexDigit(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    /// <summary>Writes whichever of the literal or hex form is shorter.</summary>
    public static void WriteString(ByteSink sink, ReadOnlySpan<byte> value)
    {
        bool balanced = ParenthesesBalanced(value);
        int literal = 2;
        foreach (byte b in value)
        {
            literal += b switch
            {
                (byte)'\\' or (byte)'\r' => 2,
                (byte)'(' or (byte)')' => balanced ? 1 : 2,
                _ => 1,
            };
        }

        if (literal <= value.Length * 2 + 2)
        {
            sink.Write((byte)'(');
            foreach (byte b in value)
            {
                switch (b)
                {
                    case (byte)'\\':
                        sink.Write("\\\\"u8);
                        break;
                    case (byte)'\r':
                        sink.Write("\\r"u8);
                        break;
                    case (byte)'(' or (byte)')' when !balanced:
                        sink.Write((byte)'\\');
                        sink.Write(b);
                        break;
                    default:
                        sink.Write(b);
                        break;
                }
            }

            sink.Write((byte)')');
        }
        else
        {
            sink.Write((byte)'<');
            foreach (byte b in value)
            {
                sink.Write(HexDigit(b >> 4));
                sink.Write(HexDigit(b & 0xF));
            }

            sink.Write((byte)'>');
        }
    }

    private static bool ParenthesesBalanced(ReadOnlySpan<byte> value)
    {
        int depth = 0;
        foreach (byte b in value)
        {
            if (b == (byte)'(') depth++;
            else if (b == (byte)')' && --depth < 0) return false;
        }

        return depth == 0;
    }
}
