namespace PdfNetStack.Fonts.OpenType;

/// <summary>Big endian reads with bounds checks. Font files come from anywhere, so every read is checked.</summary>
internal static class FontData
{
    public static byte U8(ReadOnlySpan<byte> data, int offset)
    {
        Check(data, offset, 1);
        return data[offset];
    }

    public static ushort U16(ReadOnlySpan<byte> data, int offset)
    {
        Check(data, offset, 2);
        return (ushort)(data[offset] << 8 | data[offset + 1]);
    }

    public static short I16(ReadOnlySpan<byte> data, int offset) => (short)U16(data, offset);

    public static int U24(ReadOnlySpan<byte> data, int offset)
    {
        Check(data, offset, 3);
        return data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];
    }

    public static uint U32(ReadOnlySpan<byte> data, int offset)
    {
        Check(data, offset, 4);
        return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }

    /// <summary>A 32 bit offset or length that must fit an int.</summary>
    public static int Offset32(ReadOnlySpan<byte> data, int offset)
    {
        uint v = U32(data, offset);
        if (v > int.MaxValue) throw new FontFormatException("Font offset is out of range.");
        return (int)v;
    }

    public static string Tag(ReadOnlySpan<byte> data, int offset)
    {
        Check(data, offset, 4);
        return new string([(char)data[offset], (char)data[offset + 1], (char)data[offset + 2], (char)data[offset + 3]]);
    }

    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length)
    {
        Check(data, offset, length);
        return data.Slice(offset, length);
    }

    public static void Check(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length) throw new FontFormatException("Font data is truncated.");
    }

    /// <summary>OpenType table checksum: the sum of big endian 32 bit words, the last one zero padded.</summary>
    public static uint Checksum(ReadOnlySpan<byte> table)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= table.Length; i += 4) sum += (uint)(table[i] << 24 | table[i + 1] << 16 | table[i + 2] << 8 | table[i + 3]);
        if (i < table.Length)
        {
            uint last = 0;
            for (int k = 0; k < 4; k++) last = last << 8 | (i + k < table.Length ? table[i + k] : 0u);
            sum += last;
        }

        return sum;
    }
}

/// <summary>A growable big endian buffer for building font tables.</summary>
internal sealed class FontWriter
{
    private byte[] _buffer;

    public FontWriter(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    public void U8(int value)
    {
        Ensure(1);
        _buffer[Length++] = (byte)value;
    }

    public void U16(int value)
    {
        Ensure(2);
        _buffer[Length++] = (byte)(value >> 8);
        _buffer[Length++] = (byte)value;
    }

    public void U32(uint value)
    {
        Ensure(4);
        _buffer[Length++] = (byte)(value >> 24);
        _buffer[Length++] = (byte)(value >> 16);
        _buffer[Length++] = (byte)(value >> 8);
        _buffer[Length++] = (byte)value;
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(Length));
        Length += bytes.Length;
    }

    /// <summary>Zero bytes up to a multiple of <paramref name="alignment"/>.</summary>
    public void Pad(int alignment)
    {
        while (Length % alignment != 0) U8(0);
    }

    public void SetU32(int offset, uint value)
    {
        _buffer[offset] = (byte)(value >> 24);
        _buffer[offset + 1] = (byte)(value >> 16);
        _buffer[offset + 2] = (byte)(value >> 8);
        _buffer[offset + 3] = (byte)value;
    }

    private void Ensure(int extra)
    {
        if (Length + extra <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + extra));
    }
}
