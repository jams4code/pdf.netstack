using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Content;

/// <summary>An operator with its operands; for BI ... EI, the inline image's dictionary and data.</summary>
internal sealed class ContentOperation
{
    public string Operator { get; set; } = "";

    public List<PdfObject> Operands { get; } = [];

    public PdfDictionary? InlineImage { get; set; }

    public ReadOnlyMemory<byte> InlineData { get; set; }
}

/// <summary>
/// Reads a content stream as operators and operands (ISO 32000-1, 7.8.2), tolerantly: a damaged
/// operand or a stray delimiter is skipped, as viewers skip them, and the rest still reads.
/// </summary>
internal sealed class ContentReader
{
    /// <summary>Operands one operator may take. TJ arrays are one operand however long.</summary>
    private const int MaxOperands = 4096;

    // Inline image abbreviations (ISO 32000-1, Table 93 and 94).
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace", ["D"] = "Decode", ["DP"] = "DecodeParms", ["F"] = "Filter",
        ["H"] = "Height", ["IM"] = "ImageMask", ["I"] = "Interpolate", ["W"] = "Width", ["L"] = "Length",
    };

    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        ["G"] = "DeviceGray", ["RGB"] = "DeviceRGB", ["CMYK"] = "DeviceCMYK", ["I"] = "Indexed",
        ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
    };

    private readonly PdfParser _parser;
    private readonly ReadOnlyMemory<byte> _data;

    public ContentReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _parser = new PdfParser(data);
    }

    /// <summary>Reads the next operation into <paramref name="op"/>; false at the end of the data.</summary>
    public bool Next(ContentOperation op)
    {
        op.Operands.Clear();
        op.InlineImage = null;
        op.InlineData = default;
        while (true)
        {
            int start;
            Token t;
            try
            {
                start = _parser.Position;
                t = _parser.NextToken();
            }
            catch (PdfFormatException)
            {
                // An unterminated string or hex string: nothing after it can be read.
                return false;
            }

            if (t.Kind == TokenKind.EndOfInput) return false;
            if (t.Kind == TokenKind.Keyword)
            {
                ReadOnlySpan<byte> k = _parser.Span[t.Start..t.End];
                if (k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8) || k.SequenceEqual("null"u8))
                {
                    _parser.Position = start;
                    AddOperand(op, _parser.ParseObject());
                    continue;
                }

                if (k.Length == 1 && k[0] is (byte)')' or (byte)'{' or (byte)'}') continue;
                op.Operator = Compat.Latin1.GetString(k);
                if (op.Operator == "BI") ReadInlineImage(op);
                return true;
            }

            _parser.Position = start;
            try
            {
                AddOperand(op, _parser.ParseObject());
            }
            catch (PdfFormatException)
            {
                // A damaged operand: skip past the token that started it.
                _parser.Position = Math.Max(t.End, start + 1);
            }
        }
    }

    private static void AddOperand(ContentOperation op, PdfObject value)
    {
        // Operands beyond any operator's need are dropped; the newest matter, as the operator follows them.
        if (op.Operands.Count >= MaxOperands) op.Operands.RemoveAt(0);
        op.Operands.Add(value);
    }

    /// <summary>BI key value pairs ID data EI (ISO 32000-1, 8.9.7).</summary>
    private void ReadInlineImage(ContentOperation op)
    {
        var dict = new PdfDictionary();
        op.Operator = "BI";
        while (true)
        {
            int start = _parser.Position;
            Token t;
            try
            {
                t = _parser.NextToken();
            }
            catch (PdfFormatException)
            {
                _parser.Position = _data.Length;
                return;
            }

            if (t.Kind == TokenKind.EndOfInput) return;
            if (t.Kind == TokenKind.Keyword && _parser.Span[t.Start..t.End].SequenceEqual("ID"u8)) break;
            if (t.Kind != TokenKind.Name) continue;
            string key = PdfParser.DecodeName(_parser.Span[(t.Start + 1)..t.End]);
            try
            {
                PdfObject value = Expand(_parser.ParseObject());
                dict.Set(Keys.TryGetValue(key, out string? full) ? full : key, value);
            }
            catch (PdfFormatException)
            {
                _parser.Position = Math.Max(_parser.Position, start + 1);
            }
        }

        // One white-space byte separates ID from the data.
        int dataStart = _parser.Position + 1;
        if (dataStart > _data.Length) return;
        int end = DataEnd(dict, dataStart);
        op.InlineImage = dict;
        op.InlineData = _data.Slice(dataStart, end - dataStart);
        _parser.Position = Math.Min(_data.Length, end);

        // Step over EI.
        int saved = _parser.Position;
        try
        {
            Token ei = _parser.NextToken();
            if (!(ei.Kind == TokenKind.Keyword && _parser.Span[ei.Start..ei.End].SequenceEqual("EI"u8))) _parser.Position = saved;
        }
        catch (PdfFormatException)
        {
            _parser.Position = saved;
        }
    }

    private static PdfObject Expand(PdfObject value) => value switch
    {
        PdfName n when Values.TryGetValue(n.Value, out string? full) => new PdfName(full),
        PdfArray a => new PdfArray(a.Select(Expand).ToList()),
        _ => value,
    };

    /// <summary>
    /// Where inline image data ends. Unfiltered data has a known length; otherwise the end is the
    /// first EI between white space, the rule viewers use, since filtered data has no length.
    /// </summary>
    private int DataEnd(PdfDictionary dict, int start)
    {
        ReadOnlySpan<byte> s = _data.Span;
        if (dict["Length"] is PdfNumber { IsInteger: true } length && length.IntegerValue >= 0 && start + length.IntegerValue <= s.Length)
        {
            return start + (int)length.IntegerValue;
        }

        if (dict["Filter"] is null && Size(dict) is long size && start + size <= s.Length) return start + (int)size;

        for (int i = start; i + 1 < s.Length; i++)
        {
            if (s[i] == (byte)'E' && s[i + 1] == (byte)'I'
                && (i == start || PdfSyntax.IsWhitespace(s[i - 1]))
                && (i + 2 == s.Length || PdfSyntax.IsWhitespace(s[i + 2]) || PdfSyntax.IsDelimiter(s[i + 2])))
            {
                return i > start ? i - 1 : i;
            }
        }

        return s.Length;
    }

    /// <summary>Bytes of unfiltered samples: rows padded to whole bytes.</summary>
    private static long? Size(PdfDictionary dict)
    {
        if (dict["Width"] is not PdfNumber { IsInteger: true } w || dict["Height"] is not PdfNumber { IsInteger: true } h) return null;
        if (w.IntegerValue is <= 0 or > 1 << 20 || h.IntegerValue is <= 0 or > 1 << 20) return null;
        bool mask = dict["ImageMask"] is PdfBoolean { Value: true };
        long bpc = mask ? 1 : dict["BitsPerComponent"] is PdfNumber { IsInteger: true } b ? b.IntegerValue : 8;
        int colors = mask ? 1 : dict["ColorSpace"] switch
        {
            PdfName { Value: "DeviceRGB" or "CalRGB" or "Lab" } => 3,
            PdfName { Value: "DeviceCMYK" } => 4,
            PdfName { Value: "DeviceGray" or "CalGray" } => 1,
            PdfArray a when a.Count > 0 && a[0] is PdfName { Value: "Indexed" } => 1,
            _ => -1,
        };
        if (colors < 0 || bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        return ((w.IntegerValue * colors * bpc) + 7) / 8 * h.IntegerValue;
    }
}
