using System.Globalization;
using System.Text;

namespace PdfNetStack.Html.Css;

internal enum CssTokenType
{
    Ident,
    Function,
    AtKeyword,
    Hash,
    String,
    BadString,
    Url,
    BadUrl,
    Delim,
    Number,
    Percentage,
    Dimension,
    Whitespace,
    Cdo,
    Cdc,
    Colon,
    Semicolon,
    Comma,
    LeftBracket,
    RightBracket,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
}

/// <summary>A CSS token (CSS Syntax Level 3, 4).</summary>
internal readonly record struct CssToken(CssTokenType Type, string Value = "", double Number = 0, string Unit = "", bool IsId = false, bool IsInteger = false)
{
    public bool Is(CssTokenType type, string value) => Type == type && string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Type switch
    {
        CssTokenType.Ident => Value,
        CssTokenType.Function => Value + "(",
        CssTokenType.AtKeyword => "@" + Value,
        CssTokenType.Hash => "#" + Value,
        CssTokenType.String => "\"" + Value + "\"",
        CssTokenType.Url => "url(" + Value + ")",
        CssTokenType.Delim => Value,
        CssTokenType.Number => Number.ToString(CultureInfo.InvariantCulture),
        CssTokenType.Percentage => Number.ToString(CultureInfo.InvariantCulture) + "%",
        CssTokenType.Dimension => Number.ToString(CultureInfo.InvariantCulture) + Unit,
        CssTokenType.Whitespace => " ",
        CssTokenType.Colon => ":",
        CssTokenType.Semicolon => ";",
        CssTokenType.Comma => ",",
        _ => Value,
    };
}

/// <summary>Tokenizes CSS text (CSS Syntax Level 3, 4.3).</summary>
internal static class CssTokenizer
{
    public static List<CssToken> Tokenize(string css)
    {
        var tokens = new List<CssToken>();
        string s = css.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n');
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }

            if (IsSpace(c))
            {
                while (i < s.Length && IsSpace(s[i])) i++;
                tokens.Add(new CssToken(CssTokenType.Whitespace));
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    tokens.Add(ReadString(s, ref i));
                    continue;
                case '#':
                    if (i + 1 < s.Length && (IsNameChar(s[i + 1]) || StartsEscape(s, i + 1)))
                    {
                        i++;
                        bool isId = StartsIdentifier(s, i);
                        tokens.Add(new CssToken(CssTokenType.Hash, ReadName(s, ref i), IsId: isId));
                        continue;
                    }

                    break;
                case '(':
                    tokens.Add(new CssToken(CssTokenType.LeftParen));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new CssToken(CssTokenType.RightParen));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new CssToken(CssTokenType.LeftBracket));
                    i++;
                    continue;
                case ']':
                    tokens.Add(new CssToken(CssTokenType.RightBracket));
                    i++;
                    continue;
                case '{':
                    tokens.Add(new CssToken(CssTokenType.LeftBrace));
                    i++;
                    continue;
                case '}':
                    tokens.Add(new CssToken(CssTokenType.RightBrace));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new CssToken(CssTokenType.Comma));
                    i++;
                    continue;
                case ':':
                    tokens.Add(new CssToken(CssTokenType.Colon));
                    i++;
                    continue;
                case ';':
                    tokens.Add(new CssToken(CssTokenType.Semicolon));
                    i++;
                    continue;
                case '<' when string.CompareOrdinal(s, i, "<!--", 0, 4) == 0:
                    tokens.Add(new CssToken(CssTokenType.Cdo));
                    i += 4;
                    continue;
                case '-' when string.CompareOrdinal(s, i, "-->", 0, 3) == 0:
                    tokens.Add(new CssToken(CssTokenType.Cdc));
                    i += 3;
                    continue;
                case '@' when StartsIdentifier(s, i + 1):
                    i++;
                    tokens.Add(new CssToken(CssTokenType.AtKeyword, ReadName(s, ref i)));
                    continue;
            }

            if (StartsNumber(s, i))
            {
                tokens.Add(ReadNumeric(s, ref i));
                continue;
            }

            if (StartsIdentifier(s, i))
            {
                tokens.Add(ReadIdentLike(s, ref i));
                continue;
            }

            tokens.Add(new CssToken(CssTokenType.Delim, c.ToString()));
            i++;
        }

        return tokens;
    }

    private static CssToken ReadString(string s, ref int i)
    {
        char quote = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == quote)
            {
                i++;
                return new CssToken(CssTokenType.String, sb.ToString());
            }

            if (c == '\n') return new CssToken(CssTokenType.BadString, sb.ToString());
            if (c == '\\')
            {
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i += 2;
                    continue;
                }

                i++;
                sb.Append(ReadEscape(s, ref i));
                continue;
            }

            sb.Append(c);
            i++;
        }

        return new CssToken(CssTokenType.String, sb.ToString());
    }

    private static CssToken ReadNumeric(string s, ref int i)
    {
        int start = i;
        bool integer = true;
        if (s[i] is '+' or '-') i++;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i + 1 < s.Length && s[i] == '.' && char.IsDigit(s[i + 1]))
        {
            integer = false;
            i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }

        if (i + 1 < s.Length && s[i] is 'e' or 'E' && (char.IsDigit(s[i + 1]) || (s[i + 1] is '+' or '-' && i + 2 < s.Length && char.IsDigit(s[i + 2]))))
        {
            integer = false;
            i += 2;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }

#pragma warning disable CA1846 // .NET Standard 2.0 has no span overload.
        double value = double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
#pragma warning restore CA1846
        if (i < s.Length && s[i] == '%')
        {
            i++;
            return new CssToken(CssTokenType.Percentage, Number: value);
        }

        if (StartsIdentifier(s, i))
        {
            string unit = ReadName(s, ref i);
            return new CssToken(CssTokenType.Dimension, Number: value, Unit: unit.ToLowerInvariant(), IsInteger: integer);
        }

        return new CssToken(CssTokenType.Number, Number: value, IsInteger: integer);
    }

    private static CssToken ReadIdentLike(string s, ref int i)
    {
        string name = ReadName(s, ref i);
        if (i < s.Length && s[i] == '(')
        {
            i++;
            if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase))
            {
                int j = i;
                while (j < s.Length && IsSpace(s[j])) j++;
                if (j < s.Length && s[j] is not ('"' or '\'')) return ReadUrl(s, ref i);
            }

            return new CssToken(CssTokenType.Function, name);
        }

        return new CssToken(CssTokenType.Ident, name);
    }

    private static CssToken ReadUrl(string s, ref int i)
    {
        var sb = new StringBuilder();
        while (i < s.Length && IsSpace(s[i])) i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ')')
            {
                i++;
                return new CssToken(CssTokenType.Url, sb.ToString().TrimEnd());
            }

            if (c == '\\')
            {
                i++;
                sb.Append(ReadEscape(s, ref i));
                continue;
            }

            sb.Append(c);
            i++;
        }

        return new CssToken(CssTokenType.Url, sb.ToString().TrimEnd());
    }

    private static string ReadName(string s, ref int i)
    {
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            if (IsNameChar(s[i]))
            {
                sb.Append(s[i++]);
            }
            else if (StartsEscape(s, i))
            {
                i++;
                sb.Append(ReadEscape(s, ref i));
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static string ReadEscape(string s, ref int i)
    {
        if (i >= s.Length) return char.ConvertFromUtf32(0xFFFD);
        int start = i;
        while (i < s.Length && i - start < 6 && Uri.IsHexDigit(s[i])) i++;
        if (i == start) return s[i++].ToString();
#pragma warning disable CA1846 // .NET Standard 2.0 has no span overload.
        int value = int.Parse(s.Substring(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
#pragma warning restore CA1846
        if (i < s.Length && IsSpace(s[i])) i++;
        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) value = 0xFFFD;
        return char.ConvertFromUtf32(value);
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n';

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c > 0x7F;

    private static bool IsNameChar(char c) => IsNameStart(c) || char.IsDigit(c) || c == '-';

    private static bool StartsEscape(string s, int i) => i + 1 < s.Length && s[i] == '\\' && s[i + 1] != '\n';

    private static bool StartsIdentifier(string s, int i)
    {
        if (i >= s.Length) return false;
        if (s[i] == '-')
        {
            return i + 1 < s.Length && (IsNameStart(s[i + 1]) || s[i + 1] == '-' || StartsEscape(s, i + 1));
        }

        return IsNameStart(s[i]) || StartsEscape(s, i);
    }

    private static bool StartsNumber(string s, int i)
    {
        if (i >= s.Length) return false;
        char c = s[i];
        if (c is '+' or '-')
        {
            return i + 1 < s.Length && (char.IsDigit(s[i + 1]) || (s[i + 1] == '.' && i + 2 < s.Length && char.IsDigit(s[i + 2])));
        }

        return char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]));
    }
}

/// <summary>A component value: a token, a block or a function with its arguments.</summary>
internal sealed class CssValue
{
    public CssValue(CssToken token) => Token = token;

    public CssValue(CssTokenType block, string? function, List<CssValue> children)
    {
        Block = block;
        Function = function;
        Children = children;
    }

    public CssToken Token { get; }

    /// <summary>The opening token of a block: brace, bracket or parenthesis; for functions, LeftParen.</summary>
    public CssTokenType? Block { get; }

    public string? Function { get; }

    public List<CssValue> Children { get; } = [];

    public bool IsToken => Block is null;

    public bool IsWhitespace => IsToken && Token.Type == CssTokenType.Whitespace;

    public bool IsIdent(string name) => IsToken && Token.Is(CssTokenType.Ident, name);

    public override string ToString() => IsToken
        ? Token.ToString()
        : (Function is not null ? Function + "(" : Block switch { CssTokenType.LeftBrace => "{", CssTokenType.LeftBracket => "[", _ => "(" })
          + string.Concat(Children.Select(c => c.ToString()))
          + (Block switch { CssTokenType.LeftBrace => "}", CssTokenType.LeftBracket => "]", _ => ")" });
}
