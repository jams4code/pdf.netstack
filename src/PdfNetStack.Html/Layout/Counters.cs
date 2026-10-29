using System.Globalization;
using System.Text;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Counters in scope while the box tree is built (CSS Lists 3, 4): a counter reset on an element
/// is visible to the element, its descendants and its following siblings.
/// </summary>
internal sealed class CounterScopes
{
    private readonly List<(string Name, int Value, int Depth)> _active = [];
    private int _depth;

    /// <summary>Starts an element one level deeper.</summary>
    public void Enter() => _depth++;

    /// <summary>Ends an element: counters its children created go out of scope.</summary>
    public void Leave()
    {
        _active.RemoveAll(c => c.Depth > _depth);
        _depth--;
    }

    public void Reset(string name, int value) => _active.Add((name, value, _depth));

    public void Increment(string name, int by)
    {
        int i = Find(name);
        if (i < 0)
        {
            Reset(name, 0);
            i = _active.Count - 1;
        }

        _active[i] = (name, unchecked(_active[i].Value + by), _active[i].Depth);
    }

    public void Set(string name, int value)
    {
        int i = Find(name);
        if (i < 0) Reset(name, value);
        else _active[i] = (name, value, _active[i].Depth);
    }

    /// <summary>The innermost value, or 0 when no counter of that name is in scope.</summary>
    public int Value(string name)
    {
        int i = Find(name);
        return i < 0 ? 0 : _active[i].Value;
    }

    /// <summary>Every nested value, outermost first, for counters().</summary>
    public IEnumerable<int> Values(string name) => _active.Where(c => c.Name == name).Select(c => c.Value);

    private int Find(string name)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Name == name) return i;
        }

        return -1;
    }

    /// <summary>Parses counter-reset, counter-increment or counter-set: names, each with an optional integer.</summary>
    public static List<(string Name, int Value)> Parse(List<CssValue> value, int implicitValue)
    {
        var result = new List<(string, int)>();
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            CssValue v = items[i];
            if (!v.IsToken || v.Token.Type != CssTokenType.Ident || v.IsIdent("none")) continue;
            int n = implicitValue;
            if (i + 1 < items.Count && items[i + 1].IsToken && items[i + 1].Token is { Type: CssTokenType.Number, IsInteger: true } t)
            {
                n = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, t.Number));
                i++;
            }

            result.Add((v.Token.Value, n));
        }

        return result;
    }
}

/// <summary>Formats counter values (CSS Counter Styles 3, 6: the predefined styles).</summary>
internal static class CounterStyle
{
    public static string Format(int value, string style)
    {
        switch (style.ToLowerInvariant())
        {
            case "none":
                return "";
            case "disc":
                return ((char)0x2022).ToString();
            case "circle":
                return ((char)0x25E6).ToString();
            case "square":
                return ((char)0x25AA).ToString();
            case "disclosure-open":
                return ((char)0x25BE).ToString();
            case "disclosure-closed":
                return ((char)0x25B8).ToString();
            case "decimal-leading-zero":
                return value is >= 0 and < 10 ? "0" + value.ToString(CultureInfo.InvariantCulture) : Decimal(value);
            case "lower-roman":
                return Roman(value)?.ToLowerInvariant() ?? Decimal(value);
            case "upper-roman":
                return Roman(value) ?? Decimal(value);
            case "lower-alpha" or "lower-latin":
                return Alphabetic(value, 'a', 26) ?? Decimal(value);
            case "upper-alpha" or "upper-latin":
                return Alphabetic(value, 'A', 26) ?? Decimal(value);
            case "lower-greek":
                return Greek(value) ?? Decimal(value);
            case "arabic-indic":
                return Digits(value, (char)0x0660);
            case "persian":
                return Digits(value, (char)0x06F0);
            case "devanagari":
                return Digits(value, (char)0x0966);
            default:
                return Decimal(value);
        }
    }

    /// <summary>The text after a marker: a space after bullets, ". " after numbers.</summary>
    public static string Suffix(string style) =>
        style.ToLowerInvariant() is "disc" or "circle" or "square" or "disclosure-open" or "disclosure-closed" ? " " : style == "none" ? "" : ". ";

    private static string Decimal(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Digits(int value, char zero)
    {
        string d = Decimal(value);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d) sb.Append(c is >= '0' and <= '9' ? (char)(zero + (c - '0')) : c);
        return sb.ToString();
    }

    private static string? Roman(int value)
    {
        if (value is < 1 or > 3999) return null;
        int[] numbers = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var sb = new StringBuilder();
        for (int i = 0; i < numbers.Length; i++)
        {
            while (value >= numbers[i])
            {
                sb.Append(symbols[i]);
                value -= numbers[i];
            }
        }

        return sb.ToString();
    }

    /// <summary>Bijective base-n: a..z, aa, ab and so on.</summary>
    private static string? Alphabetic(int value, char first, int count)
    {
        if (value < 1) return null;
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)(first + (value % count)));
            value /= count;
        }

        return sb.ToString();
    }

    private static string? Greek(int value)
    {
        if (value < 1) return null;

        // Alpha to omega without the final sigma.
        int[] letters = [0x3B1, 0x3B2, 0x3B3, 0x3B4, 0x3B5, 0x3B6, 0x3B7, 0x3B8, 0x3B9, 0x3BA, 0x3BB, 0x3BC, 0x3BD, 0x3BE, 0x3BF, 0x3C0, 0x3C1, 0x3C3, 0x3C4, 0x3C5, 0x3C6, 0x3C7, 0x3C8, 0x3C9];
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)letters[value % letters.Length]);
            value /= letters.Length;
        }

        return sb.ToString();
    }
}
