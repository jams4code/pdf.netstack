namespace PdfNetStack.Signing;

/// <summary>DER encoding (ITU-T X.690) of the few ASN.1 types CMS signatures need.</summary>
internal static class Der
{
    public const byte Integer = 0x02, OctetString = 0x04, Null = 0x05, ObjectIdentifier = 0x06, Sequence = 0x30, Set = 0x31;

    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        int n = content.Length;
        int lengthBytes = n < 0x80 ? 0 : n <= 0xFF ? 1 : n <= 0xFFFF ? 2 : n <= 0xFFFFFF ? 3 : 4;
        var b = new byte[2 + lengthBytes + n];
        b[0] = tag;
        if (lengthBytes == 0)
        {
            b[1] = (byte)n;
        }
        else
        {
            b[1] = (byte)(0x80 | lengthBytes);
            for (int i = 0; i < lengthBytes; i++) b[2 + i] = (byte)(n >> (8 * (lengthBytes - 1 - i)));
        }

        content.CopyTo(b.AsSpan(2 + lengthBytes));
        return b;
    }

    public static byte[] Seq(params byte[][] items) => Tlv(Sequence, Concat(items));

    /// <summary>A SET OF: DER sorts the encoded elements.</summary>
    public static byte[] SetOf(params byte[][] items)
    {
        byte[][] sorted = [.. items];
        Array.Sort(sorted, Compare);
        return Tlv(Set, Concat(sorted));
    }

    public static byte[] Oid(string dotted)
    {
        long[] parts = [.. dotted.Split('.').Select(long.Parse)];
        var content = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        for (int i = 2; i < parts.Length; i++)
        {
            long v = parts[i];
            var chunk = new Stack<byte>();
            chunk.Push((byte)(v & 0x7F));
            for (v >>= 7; v > 0; v >>= 7) chunk.Push((byte)(0x80 | (v & 0x7F)));
            content.AddRange(chunk);
        }

        return Tlv(ObjectIdentifier, [.. content]);
    }

    public static byte[] Int(long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)value);
            value >>= 8;
        }
        while (value != 0 && value != -1);
        if (value == 0 && (bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return Tlv(Integer, [.. bytes]);
    }

    /// <summary>An INTEGER from its big endian two's complement content, as a certificate serial number is stored.</summary>
    public static byte[] IntRaw(ReadOnlySpan<byte> content) => Tlv(Integer, content);

    public static byte[] Octets(ReadOnlySpan<byte> content) => Tlv(OctetString, content);

    public static byte[] NullValue() => [Null, 0];

    /// <summary>A context specific tag around content: constructed for EXPLICIT tagging and for IMPLICIT constructed types.</summary>
    public static byte[] Context(int number, bool constructed, ReadOnlySpan<byte> content) =>
        Tlv((byte)(0x80 | (constructed ? 0x20 : 0) | number), content);

    public static byte[] Concat(params byte[][] items)
    {
        var result = new byte[items.Sum(i => i.Length)];
        int at = 0;
        foreach (byte[] item in items)
        {
            item.CopyTo(result, at);
            at += item.Length;
        }

        return result;
    }

    private static int Compare(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }

        return a.Length.CompareTo(b.Length);
    }
}

/// <summary>A parsed DER element: tag, the whole encoding, and the content.</summary>
internal sealed class DerNode
{
    private const int MaxDepth = 64;

    private DerNode(byte tag, byte[] source, int start, int contentStart, int end)
    {
        Tag = tag;
        Source = source;
        Start = start;
        ContentStart = contentStart;
        End = end;
    }

    public byte Tag { get; }

    public byte[] Source { get; }

    public int Start { get; }

    public int ContentStart { get; }

    public int End { get; }

    public ReadOnlySpan<byte> Encoded => Source.AsSpan(Start, End - Start);

    public ReadOnlySpan<byte> Content => Source.AsSpan(ContentStart, End - ContentStart);

    public bool Constructed => (Tag & 0x20) != 0;

    /// <exception cref="FormatException">The bytes are not valid DER.</exception>
    public static DerNode Parse(byte[] data, int start = 0, int? limit = null)
    {
        int end = limit ?? data.Length;
        if (start + 2 > end) throw new FormatException("ASN.1 element is truncated.");
        byte tag = data[start];
        if ((tag & 0x1F) == 0x1F) throw new FormatException("Multi-byte ASN.1 tags are not supported.");
        int lengthByte = data[start + 1];
        int at = start + 2;
        long length;
        if (lengthByte < 0x80)
        {
            length = lengthByte;
        }
        else
        {
            int count = lengthByte & 0x7F;
            if (count == 0 || count > 4 || at + count > end) throw new FormatException("ASN.1 length is invalid.");
            length = 0;
            for (int i = 0; i < count; i++) length = length << 8 | data[at++];
        }

        if (length > end - at) throw new FormatException("ASN.1 element is truncated.");
        return new DerNode(tag, data, start, at, at + (int)length);
    }

    /// <summary>The elements inside a constructed element.</summary>
    public List<DerNode> Children(int depth = 0)
    {
        if (depth > MaxDepth) throw new FormatException("ASN.1 nesting is too deep.");
        var children = new List<DerNode>();
        for (int at = ContentStart; at < End;)
        {
            DerNode child = Parse(Source, at, End);
            children.Add(child);
            at = child.End;
        }

        return children;
    }

    public string OidValue()
    {
        if (Tag != Der.ObjectIdentifier || Content.Length == 0) throw new FormatException("Expected an object identifier.");
        ReadOnlySpan<byte> c = Content;
        var parts = new List<long> { c[0] / 40, c[0] % 40 };
        long v = 0;
        for (int i = 1; i < c.Length; i++)
        {
            v = v << 7 | (uint)(c[i] & 0x7F);
            if ((c[i] & 0x80) == 0)
            {
                parts.Add(v);
                v = 0;
            }
        }

        return string.Join(".", parts);
    }
}
