using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Parsing;

internal enum TokenKind
{
    EndOfInput,
    Number,
    Name,
    LiteralString,
    HexString,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
}

internal readonly ref struct Token
{
    public Token(TokenKind kind, int start, int end)
    {
        Kind = kind;
        Start = start;
        End = end;
    }

    public TokenKind Kind { get; }

    public int Start { get; }

    public int End { get; }
}

/// <summary>
/// Tokenizer and object parser over a byte buffer. Every read is bounds checked and
/// nesting is capped, so hostile input fails with <see cref="PdfFormatException"/>
/// rather than a crash or stack overflow.
/// </summary>
internal sealed class PdfParser
{
    public const int MaxNesting = 256;

    private readonly ReadOnlyMemory<byte> _data;

    public PdfParser(ReadOnlyMemory<byte> data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public ReadOnlySpan<byte> Span => _data.Span;

    public void SkipWhitespaceAndComments()
    {
        ReadOnlySpan<byte> s = _data.Span;
        int p = Position;
        while (p < s.Length)
        {
            byte b = s[p];
            if (PdfSyntax.IsWhitespace(b))
            {
                p++;
            }
            else if (b == (byte)'%')
            {
                while (p < s.Length && s[p] != (byte)'\n' && s[p] != (byte)'\r') p++;
            }
            else
            {
                break;
            }
        }

        Position = p;
    }

    public Token NextToken()
    {
        SkipWhitespaceAndComments();
        ReadOnlySpan<byte> s = _data.Span;
        int start = Position;
        if (start >= s.Length) return new Token(TokenKind.EndOfInput, start, start);

        byte b = s[start];
        switch (b)
        {
            case (byte)'[':
                Position = start + 1;
                return new Token(TokenKind.ArrayStart, start, start + 1);
            case (byte)']':
                Position = start + 1;
                return new Token(TokenKind.ArrayEnd, start, start + 1);
            case (byte)'<':
                if (start + 1 < s.Length && s[start + 1] == (byte)'<')
                {
                    Position = start + 2;
                    return new Token(TokenKind.DictStart, start, start + 2);
                }

                Position = SkipHexString(s, start);
                return new Token(TokenKind.HexString, start, Position);
            case (byte)'>':
                if (start + 1 < s.Length && s[start + 1] == (byte)'>')
                {
                    Position = start + 2;
                    return new Token(TokenKind.DictEnd, start, start + 2);
                }

                throw new PdfFormatException($"Unexpected '>' at offset {start}.");
            case (byte)'(':
                Position = SkipLiteralString(s, start);
                return new Token(TokenKind.LiteralString, start, Position);
            case (byte)'/':
            {
                int p = start + 1;
                while (p < s.Length && PdfSyntax.IsRegular(s[p])) p++;
                Position = p;
                return new Token(TokenKind.Name, start, p);
            }

            case (byte)')':
            case (byte)'{':
            case (byte)'}':
                // Stray delimiters are tolerated as one byte keywords so a caller can skip them.
                Position = start + 1;
                return new Token(TokenKind.Keyword, start, start + 1);
            default:
            {
                int p = start;
                bool numeric = true;
                while (p < s.Length && PdfSyntax.IsRegular(s[p]))
                {
                    byte c = s[p];
                    if (!PdfSyntax.IsDigit(c) && c != (byte)'.' && c != (byte)'+' && c != (byte)'-') numeric = false;
                    p++;
                }

                Position = p;
                if (numeric && HasDigit(s[start..p])) return new Token(TokenKind.Number, start, p);
                return new Token(TokenKind.Keyword, start, p);
            }
        }
    }

    private static bool HasDigit(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (PdfSyntax.IsDigit(b)) return true;
        }

        return false;
    }

    public bool TryReadKeyword(ReadOnlySpan<byte> keyword)
    {
        int saved = Position;
        Token t = NextToken();
        if (t.Kind == TokenKind.Keyword && Span[t.Start..t.End].SequenceEqual(keyword)) return true;
        Position = saved;
        return false;
    }

    public bool TryReadInteger(out long value)
    {
        int saved = Position;
        Token t = NextToken();
        if (t.Kind == TokenKind.Number && TryParseInteger(Span[t.Start..t.End], out value)) return true;
        Position = saved;
        value = 0;
        return false;
    }

    public static bool TryParseInteger(ReadOnlySpan<byte> s, out long value)
    {
        value = 0;
        int i = 0;
        bool negative = false;
        if (s.Length > 0 && (s[0] == (byte)'+' || s[0] == (byte)'-'))
        {
            negative = s[0] == (byte)'-';
            i = 1;
        }

        if (i >= s.Length || s.Length - i > 18) return false;
        for (; i < s.Length; i++)
        {
            if (!PdfSyntax.IsDigit(s[i])) return false;
            value = value * 10 + (s[i] - '0');
        }

        if (negative) value = -value;
        return true;
    }

    /// <summary>Parses one direct object. References "n g R" are recognised by lookahead.</summary>
    public PdfObject ParseObject(int depth = 0)
    {
        if (depth > MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");

        Token t = NextToken();
        ReadOnlySpan<byte> s = Span;
        switch (t.Kind)
        {
            case TokenKind.Number:
            {
                ReadOnlySpan<byte> text = s[t.Start..t.End];
                if (TryParseInteger(text, out long n) && n >= 0 && n <= int.MaxValue)
                {
                    int afterFirst = Position;
                    if (TryReadInteger(out long gen) && gen >= 0 && gen <= 65535 && TryReadKeyword("R"u8))
                    {
                        return new PdfReference((int)n, (int)gen);
                    }

                    Position = afterFirst;
                }

                return PdfNumber.Parse(text);
            }

            case TokenKind.Name:
                return new PdfName(DecodeName(s[(t.Start + 1)..t.End]));
            case TokenKind.LiteralString:
                return new PdfString(DecodeLiteralString(s[(t.Start + 1)..t.End]));
            case TokenKind.HexString:
                return new PdfString(DecodeHexString(s[(t.Start + 1)..t.End]), wasHex: true);
            case TokenKind.ArrayStart:
            {
                var items = new List<PdfObject>();
                while (true)
                {
                    int saved = Position;
                    Token next = NextToken();
                    if (next.Kind == TokenKind.ArrayEnd) break;
                    if (next.Kind == TokenKind.EndOfInput) throw new PdfFormatException("Unterminated array.");
                    if (next.Kind == TokenKind.DictEnd) throw new PdfFormatException("Unexpected '>>' inside array.");
                    if (next.Kind == TokenKind.Keyword && IsObjectBoundary(s[next.Start..next.End]))
                    {
                        throw new PdfFormatException("Unterminated array.");
                    }

                    Position = saved;
                    items.Add(ParseObject(depth + 1));
                }

                return new PdfArray(items);
            }

            case TokenKind.DictStart:
                return ParseDictionaryBody(depth);
            case TokenKind.Keyword:
            {
                ReadOnlySpan<byte> k = s[t.Start..t.End];
                if (k.SequenceEqual("true"u8)) return PdfBoolean.True;
                if (k.SequenceEqual("false"u8)) return PdfBoolean.False;
                if (k.SequenceEqual("null"u8)) return PdfNull.Instance;
                throw new PdfFormatException($"Unexpected keyword '{Encoding.Latin1.GetString(k)}' at offset {t.Start}.");
            }

            case TokenKind.EndOfInput:
                throw new PdfFormatException("Unexpected end of input.");
            default:
                throw new PdfFormatException($"Unexpected token at offset {t.Start}.");
        }
    }

    private static bool IsObjectBoundary(ReadOnlySpan<byte> keyword) =>
        keyword.SequenceEqual("endobj"u8) || keyword.SequenceEqual("stream"u8) || keyword.SequenceEqual("obj"u8);

    private PdfDictionary ParseDictionaryBody(int depth)
    {
        var dict = new PdfDictionary();
        while (true)
        {
            Token key = NextToken();
            if (key.Kind == TokenKind.DictEnd) return dict;
            if (key.Kind == TokenKind.EndOfInput) throw new PdfFormatException("Unterminated dictionary.");
            if (key.Kind != TokenKind.Name)
            {
                ReadOnlySpan<byte> k = Span[key.Start..key.End];
                if (key.Kind == TokenKind.Keyword && IsObjectBoundary(k)) throw new PdfFormatException("Unterminated dictionary.");

                // Malformed producers sometimes emit a stray value. Skip it the way viewers do.
                continue;
            }

            string name = DecodeName(Span[(key.Start + 1)..key.End]);
            int saved = Position;
            Token peek = NextToken();
            if (peek.Kind == TokenKind.DictEnd)
            {
                // Key without a value: equivalent to null, which means absent.
                return dict;
            }

            Position = saved;
            PdfObject value = ParseObject(depth + 1);
            dict.Set(name, value);
        }
    }

    private static int SkipLiteralString(ReadOnlySpan<byte> s, int start)
    {
        int depth = 0;
        int p = start;
        while (p < s.Length)
        {
            byte b = s[p];
            if (b == (byte)'\\')
            {
                p += 2;
                continue;
            }

            if (b == (byte)'(') depth++;
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0) return p + 1;
            }

            p++;
        }

        throw new PdfFormatException($"Unterminated literal string at offset {start}.");
    }

    private static int SkipHexString(ReadOnlySpan<byte> s, int start)
    {
        int end = s[(start + 1)..].IndexOf((byte)'>');
        if (end < 0) throw new PdfFormatException($"Unterminated hex string at offset {start}.");
        return start + 1 + end + 1;
    }

    /// <summary>Decodes the body of a literal string, without the enclosing parentheses.</summary>
    public static byte[] DecodeLiteralString(ReadOnlySpan<byte> body)
    {
        // The token span still ends with ')'; strip it.
        if (body.Length > 0 && body[^1] == (byte)')') body = body[..^1];
        if (body.IndexOfAny((byte)'\\', (byte)'\r') < 0) return body.ToArray();

        var result = new byte[body.Length];
        int o = 0;
        for (int i = 0; i < body.Length; i++)
        {
            byte b = body[i];
            if (b == (byte)'\r')
            {
                // A bare end-of-line in a literal string reads as a single LF.
                result[o++] = (byte)'\n';
                if (i + 1 < body.Length && body[i + 1] == (byte)'\n') i++;
                continue;
            }

            if (b != (byte)'\\')
            {
                result[o++] = b;
                continue;
            }

            if (++i >= body.Length) break;
            byte e = body[i];
            switch (e)
            {
                case (byte)'n': result[o++] = (byte)'\n'; break;
                case (byte)'r': result[o++] = (byte)'\r'; break;
                case (byte)'t': result[o++] = (byte)'\t'; break;
                case (byte)'b': result[o++] = (byte)'\b'; break;
                case (byte)'f': result[o++] = (byte)'\f'; break;
                case (byte)'(': result[o++] = (byte)'('; break;
                case (byte)')': result[o++] = (byte)')'; break;
                case (byte)'\\': result[o++] = (byte)'\\'; break;
                case (byte)'\r':
                    if (i + 1 < body.Length && body[i + 1] == (byte)'\n') i++;
                    break;
                case (byte)'\n':
                    break;
                default:
                    if (e is >= (byte)'0' and <= (byte)'7')
                    {
                        int value = e - '0';
                        for (int k = 0; k < 2 && i + 1 < body.Length && body[i + 1] is >= (byte)'0' and <= (byte)'7'; k++)
                        {
                            value = value * 8 + (body[++i] - '0');
                        }

                        result[o++] = (byte)(value & 0xFF);
                    }
                    else
                    {
                        // Unknown escape: the backslash is ignored.
                        result[o++] = e;
                    }

                    break;
            }
        }

        return result.AsSpan(0, o).ToArray();
    }

    /// <summary>Decodes the body of a hex string, without the leading '&lt;'.</summary>
    public static byte[] DecodeHexString(ReadOnlySpan<byte> body)
    {
        if (body.Length > 0 && body[^1] == (byte)'>') body = body[..^1];
        var result = new byte[(body.Length + 1) / 2];
        int o = 0;
        int high = -1;
        foreach (byte b in body)
        {
            int v = PdfSyntax.HexValue(b);
            if (v < 0) continue;
            if (high < 0)
            {
                high = v;
            }
            else
            {
                result[o++] = (byte)((high << 4) | v);
                high = -1;
            }
        }

        if (high >= 0) result[o++] = (byte)(high << 4);
        return result.AsSpan(0, o).ToArray();
    }

    public static string DecodeName(ReadOnlySpan<byte> body)
    {
        if (body.IndexOf((byte)'#') < 0) return Encoding.Latin1.GetString(body);

        Span<byte> buffer = body.Length <= 256 ? stackalloc byte[body.Length] : new byte[body.Length];
        int o = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'#' && i + 2 < body.Length)
            {
                int h = PdfSyntax.HexValue(body[i + 1]);
                int l = PdfSyntax.HexValue(body[i + 2]);
                if (h >= 0 && l >= 0)
                {
                    buffer[o++] = (byte)((h << 4) | l);
                    i += 2;
                    continue;
                }
            }

            buffer[o++] = body[i];
        }

        return Encoding.Latin1.GetString(buffer[..o]);
    }
}
