using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Content;

/// <summary>
/// Rewrites a content stream with the fewest bytes that read back as the same tokens: one
/// separator only where two tokens would otherwise merge, numbers in canonical form (no plus
/// sign, no redundant zeros), names with minimal escaping, strings in whichever of the literal
/// or hex form is shorter, and no comments. Operators and operand values never change. Inline
/// image data is copied byte for byte, and when its end is ambiguous the rest of the stream is
/// copied unchanged.
/// </summary>
/// <remarks>
/// A page's content may be split over several streams that readers join, some with a line
/// break and some without, so the edges of each stream are kept as they were: a leading or
/// trailing separator stays, and a first or last token not separated from its neighbour keeps
/// its exact bytes. A stream ending inside a comment is left alone.
/// </remarks>
internal static class ContentCompactor
{
    /// <summary>The compacted stream, or null when it cannot be rewritten safely or would not shrink.</summary>
    public static byte[]? Compact(ReadOnlyMemory<byte> content)
    {
        ReadOnlySpan<byte> s = content.Span;
        if (s.IsEmpty || EndsInComment(s)) return null;

        bool leading = StartsWithSeparator(s);
        bool trailing = EndsWithSeparator(s);
        var parser = new PdfParser(content);
        var sink = new ByteSink(s.Length);
        if (leading) sink.Write((byte)' ');

        int lastStart = -1, lastSinkStart = -1, lastEnd = -1;
        bool first = true;
        try
        {
            while (true)
            {
                Token t = parser.NextToken();
                if (t.Kind == TokenKind.EndOfInput) break;
                lastSinkStart = sink.Length;
                lastStart = t.Start;
                lastEnd = t.End;
                ReadOnlySpan<byte> raw = s[t.Start..t.End];
                if (first && !leading)
                {
                    WriteToken(sink, raw, regular: true);
                }
                else
                {
                    Emit(sink, t, raw);
                }

                first = false;
                if (t.Kind == TokenKind.Keyword && raw.SequenceEqual("BI"u8))
                {
                    int? resume = CopyInlineImage(sink, parser, content);
                    if (resume is null)
                    {
                        // Ambiguous data: everything from BI on stays exactly as it was.
                        sink.Truncate(lastSinkStart);
                        Separate(sink, s[lastStart]);
                        sink.Write(s[lastStart..]);
                        return Smaller(sink, s);
                    }

                    parser.Position = resume.Value;
                    lastStart = -1;
                }
            }
        }
        catch (PdfFormatException)
        {
            return null;
        }

        if (!trailing && lastStart >= 0)
        {
            // The last token may run into whatever a reader appends; keep its exact bytes.
            sink.Truncate(lastSinkStart);
            WriteToken(sink, s[lastStart..lastEnd], regular: true);
        }

        if (trailing) sink.Write((byte)'\n');
        return Smaller(sink, s);
    }

    private static byte[]? Smaller(ByteSink sink, ReadOnlySpan<byte> original) => sink.Length < original.Length ? sink.ToArray() : null;

    /// <summary>
    /// Copies BI's dictionary as tokens, then ID, the data and EI exactly. Returns the position
    /// after EI, or null when the data's end is ambiguous.
    /// </summary>
    private static int? CopyInlineImage(ByteSink sink, PdfParser parser, ReadOnlyMemory<byte> content)
    {
        ReadOnlySpan<byte> s = content.Span;
        int dictStart = parser.Position;
        PdfDictionary? image = ReadImageDictionary(parser);
        if (image is null) return null;
        int idEnd = parser.Position;
        (_, int keywordEnd, bool unambiguous) = InlineImageBounds.Find(s, idEnd + 1, image);
        if (!unambiguous) return null;

        var dict = new PdfParser(content, dictStart);
        while (dict.Position < idEnd)
        {
            Token t = dict.NextToken();
            ReadOnlySpan<byte> raw = s[t.Start..t.End];
            if (t.Kind == TokenKind.Keyword && raw.SequenceEqual("ID"u8))
            {
                WriteToken(sink, raw, regular: true);
                break;
            }

            Emit(sink, t, raw);
        }

        sink.Write(s[idEnd..keywordEnd]);
        return keywordEnd;
    }

    /// <summary>Reads BI's key and value pairs up to ID. Null when the dictionary is malformed.</summary>
    internal static PdfDictionary? ReadImageDictionary(PdfParser parser)
    {
        var image = new PdfDictionary();
        while (true)
        {
            Token t = parser.NextToken();
            if (t.Kind == TokenKind.EndOfInput) return null;
            if (t.Kind == TokenKind.Keyword && parser.Span[t.Start..t.End].SequenceEqual("ID"u8)) return image;
            if (t.Kind != TokenKind.Name) return null;
            image.Set(PdfParser.DecodeName(parser.Span[(t.Start + 1)..t.End]), parser.ParseObject());
        }
    }

    private static void Emit(ByteSink sink, Token t, ReadOnlySpan<byte> raw)
    {
        switch (t.Kind)
        {
            case TokenKind.Number:
                WriteToken(sink, PdfNumber.Parse(raw).Text, regular: true);
                break;
            case TokenKind.Name:
                // A name starts with a delimiter, so it never needs a separator in front.
                PdfSerializer.WriteName(sink, PdfParser.DecodeName(raw[1..]));
                break;
            case TokenKind.LiteralString:
                PdfSerializer.WriteString(sink, PdfParser.DecodeLiteralString(raw[1..]));
                break;
            case TokenKind.HexString:
                PdfSerializer.WriteString(sink, PdfParser.DecodeHexString(raw[1..]));
                break;
            default:
                WriteToken(sink, raw, regular: t.Kind == TokenKind.Keyword);
                break;
        }
    }

    private static void WriteToken(ByteSink sink, ReadOnlySpan<byte> token, bool regular)
    {
        if (regular && !token.IsEmpty) Separate(sink, token[0]);
        sink.Write(token);
    }

    /// <summary>A space where the previous byte and <paramref name="next"/> would merge into one token.</summary>
    private static void Separate(ByteSink sink, byte next)
    {
        if (sink.Length == 0) return;
        byte last = sink.LastByte;

        // A lone '/' is an empty name, which would absorb a regular token after it.
        if ((PdfSyntax.IsRegular(last) || last == (byte)'/') && PdfSyntax.IsRegular(next)) sink.Write((byte)' ');
    }

    /// <summary>
    /// Null when two content streams read as the same tokens, under the rules
    /// <see cref="Compact"/> follows: equal operators, operands equal by value, inline image
    /// data equal byte for byte, and the same edges. Otherwise the first difference.
    /// </summary>
    public static string? FindDifference(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> candidate)
    {
        ReadOnlySpan<byte> a = original.Span, b = candidate.Span;
        if (a.SequenceEqual(b)) return null;
        if (StartsWithSeparator(a) != StartsWithSeparator(b)) return "leading separator differs";
        if (EndsWithSeparator(a) != EndsWithSeparator(b)) return "trailing separator differs";
        if (EndsInComment(a) || EndsInComment(b)) return "content ending in a comment changed";

        var pa = new PdfParser(original);
        var pb = new PdfParser(candidate);
        bool first = true;
        (int Start, int End) lastA = (-1, -1), lastB = (-1, -1);
        try
        {
            for (int index = 0; ; index++)
            {
                Token ta = pa.NextToken();
                Token tb = pb.NextToken();
                if (ta.Kind == TokenKind.EndOfInput || tb.Kind == TokenKind.EndOfInput)
                {
                    if (ta.Kind != tb.Kind) return $"token count differs after {index} tokens";
                    break;
                }

                ReadOnlySpan<byte> ra = a[ta.Start..ta.End], rb = b[tb.Start..tb.End];
                if (first && !StartsWithSeparator(a) && !ra.SequenceEqual(rb)) return "first token changed";
                first = false;
                lastA = (ta.Start, ta.End);
                lastB = (tb.Start, tb.End);
                if (!SameToken(ta.Kind, ra, tb.Kind, rb)) return $"token {index} differs";
                if (ta.Kind != TokenKind.Keyword || !ra.SequenceEqual("BI"u8)) continue;

                string? image = CompareInlineImage(pa, pb, a, b, out bool restCompared);
                if (image is not null) return image;
                if (restCompared) return null;
                lastA = lastB = (-1, -1);
            }
        }
        catch (PdfFormatException)
        {
            return "content does not tokenize";
        }

        if (!EndsWithSeparator(a) && lastA.Start >= 0 && !a[lastA.Start..lastA.End].SequenceEqual(b[lastB.Start..lastB.End]))
        {
            return "last token changed";
        }

        return null;
    }

    private static string? CompareInlineImage(PdfParser pa, PdfParser pb, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, out bool restCompared)
    {
        restCompared = false;
        PdfDictionary? ia = ReadImageDictionary(pa);
        PdfDictionary? ib = ReadImageDictionary(pb);
        if (ia is null || ib is null) return ia is null && ib is null && a[pa.Position..].SequenceEqual(b[pb.Position..]) ? Rest(out restCompared) : "inline image dictionary differs";
        if (!SameValue(ia, ib)) return "inline image dictionary differs";

        (_, int endA, bool clearA) = InlineImageBounds.Find(a, pa.Position + 1, ia);
        if (!clearA) return a[pa.Position..].SequenceEqual(b[pb.Position..]) ? Rest(out restCompared) : "content after an ambiguous inline image changed";
        (_, int endB, bool clearB) = InlineImageBounds.Find(b, pb.Position + 1, ib);
        if (!clearB || !a[pa.Position..endA].SequenceEqual(b[pb.Position..endB])) return "inline image data differs";
        pa.Position = endA;
        pb.Position = endB;
        return null;

        static string? Rest(out bool compared)
        {
            compared = true;
            return null;
        }
    }

    private static bool SameToken(TokenKind ka, ReadOnlySpan<byte> a, TokenKind kb, ReadOnlySpan<byte> b)
    {
        bool stringA = ka is TokenKind.LiteralString or TokenKind.HexString;
        bool stringB = kb is TokenKind.LiteralString or TokenKind.HexString;
        if (stringA || stringB) return stringA && stringB && Decode(ka, a).AsSpan().SequenceEqual(Decode(kb, b));
        if (ka != kb) return false;
        return ka switch
        {
            TokenKind.Number => PdfNumber.Parse(a).Text.AsSpan().SequenceEqual(PdfNumber.Parse(b).Text),
            TokenKind.Name => PdfParser.DecodeName(a[1..]) == PdfParser.DecodeName(b[1..]),
            TokenKind.Keyword => a.SequenceEqual(b),
            _ => true,
        };

        static byte[] Decode(TokenKind kind, ReadOnlySpan<byte> raw) =>
            kind == TokenKind.LiteralString ? PdfParser.DecodeLiteralString(raw[1..]) : PdfParser.DecodeHexString(raw[1..]);
    }

    private static bool SameValue(PdfObject a, PdfObject b) => (a, b) switch
    {
        (PdfNumber x, PdfNumber y) => x.Text.AsSpan().SequenceEqual(y.Text),
        (PdfName x, PdfName y) => x.Value == y.Value,
        (PdfString x, PdfString y) => x.Value.AsSpan().SequenceEqual(y.Value),
        (PdfBoolean x, PdfBoolean y) => x.Value == y.Value,
        (PdfNull, PdfNull) => true,
        (PdfArray x, PdfArray y) => x.Count == y.Count && x.Items.Zip(y.Items, SameValue).All(same => same),
        (PdfDictionary x, PdfDictionary y) => x.Count == y.Count && x.Keys.SequenceEqual(y.Keys) && x.Keys.All(k => SameValue(x[k]!, y[k]!)),
        _ => false,
    };

    internal static bool StartsWithSeparator(ReadOnlySpan<byte> s) => s.Length > 0 && (PdfSyntax.IsWhitespace(s[0]) || s[0] == (byte)'%');

    internal static bool EndsWithSeparator(ReadOnlySpan<byte> s) => s.Length > 0 && PdfSyntax.IsWhitespace(s[^1]);

    /// <summary>True when the stream's last line is a comment with no line break after it.</summary>
    internal static bool EndsInComment(ReadOnlySpan<byte> s)
    {
        int lineStart = s.LastIndexOfAny((byte)'\n', (byte)'\r') + 1;
        return s[lineStart..].IndexOf((byte)'%') >= 0;
    }
}
