namespace PdfNetStack.Html.Css;

/// <summary>Evaluates calc(), min(), max() and clamp() (CSS Values Level 4, 10).</summary>
internal static class CssMath
{
    /// <summary>
    /// Evaluates a math function whose lengths are already in px. Percentages resolve against
    /// <paramref name="percentBasis"/>; when it is null they make the result unknown.
    /// </summary>
    /// <returns>The value, or null when it cannot be computed; <paramref name="isLength"/> tells a length from a number.</returns>
    public static double? Evaluate(CssValue function, double? percentBasis, out bool isLength)
    {
        isLength = false;
        Quantity? q = Function(function, percentBasis);
        if (q is not Quantity result || double.IsNaN(result.Value)) return null;
        isLength = result.IsLength;
        return double.IsInfinity(result.Value) ? (result.Value > 0 ? double.MaxValue : double.MinValue) : result.Value;
    }

    private readonly record struct Quantity(double Value, bool IsLength);

    private static Quantity? Function(CssValue f, double? basis) => f.Function switch
    {
        "calc" or "-webkit-calc" => Sum(f.Children, basis),
        "min" or "max" => MinMax(f.Function == "min", f.Children, basis),
        "clamp" => Clamp(f.Children, basis),
        _ => null,
    };

    private static List<List<CssValue>> Arguments(List<CssValue> children)
    {
        var args = new List<List<CssValue>> { new() };
        foreach (CssValue v in children)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma) args.Add([]);
            else args[^1].Add(v);
        }

        return args;
    }

    private static Quantity? MinMax(bool min, List<CssValue> children, double? basis)
    {
        Quantity? result = null;
        foreach (List<CssValue> arg in Arguments(children))
        {
            if (Sum(arg, basis) is not Quantity q || (result is Quantity r && r.IsLength != q.IsLength)) return null;
            result = result is not Quantity best ? q : new Quantity(min ? Math.Min(best.Value, q.Value) : Math.Max(best.Value, q.Value), q.IsLength);
        }

        return result;
    }

    private static Quantity? Clamp(List<CssValue> children, double? basis)
    {
        List<List<CssValue>> args = Arguments(children);
        if (args.Count != 3) return null;
        if (Sum(args[0], basis) is not Quantity lo || Sum(args[1], basis) is not Quantity value || Sum(args[2], basis) is not Quantity hi) return null;
        if (lo.IsLength != value.IsLength || hi.IsLength != value.IsLength) return null;
        return new Quantity(Math.Max(lo.Value, Math.Min(value.Value, hi.Value)), value.IsLength);
    }

    private static Quantity? Sum(List<CssValue> values, double? basis)
    {
        List<CssValue> items = values.Where(v => !v.IsWhitespace).ToList();
        int i = 0;
        Quantity? result = Product(items, ref i, basis);
        while (result is Quantity left && i < items.Count)
        {
            if (!items[i].IsToken || items[i].Token.Type != CssTokenType.Delim || items[i].Token.Value is not ("+" or "-")) return null;
            bool plus = items[i++].Token.Value == "+";
            if (Product(items, ref i, basis) is not Quantity right || right.IsLength != left.IsLength) return null;
            result = new Quantity(plus ? left.Value + right.Value : left.Value - right.Value, left.IsLength);
        }

        return i == items.Count ? result : null;
    }

    private static Quantity? Product(List<CssValue> items, ref int i, double? basis)
    {
        Quantity? result = Unit(items, ref i, basis);
        while (result is Quantity left && i < items.Count && items[i].IsToken && items[i].Token.Type == CssTokenType.Delim && items[i].Token.Value is "*" or "/")
        {
            bool times = items[i++].Token.Value == "*";
            if (Unit(items, ref i, basis) is not Quantity right) return null;
            if (times)
            {
                if (left.IsLength && right.IsLength) return null;
                result = new Quantity(left.Value * right.Value, left.IsLength || right.IsLength);
            }
            else
            {
                if (right.IsLength) return null;
                result = new Quantity(left.Value / right.Value, left.IsLength);
            }
        }

        return result;
    }

    private static Quantity? Unit(List<CssValue> items, ref int i, double? basis)
    {
        if (i >= items.Count) return null;
        CssValue v = items[i++];
        if (v.Function is not null) return Function(v, basis);
        if (v.Block == CssTokenType.LeftParen) return Sum(v.Children, basis);
        if (!v.IsToken) return null;
        CssToken t = v.Token;
        return t.Type switch
        {
            CssTokenType.Number => new Quantity(t.Number, false),
            CssTokenType.Dimension when t.Unit == "px" => new Quantity(t.Number, true),
            CssTokenType.Percentage when basis is double b => new Quantity(t.Number / 100 * b, true),
            CssTokenType.Ident => t.Value.ToLowerInvariant() switch
            {
                "pi" => new Quantity(Math.PI, false),
                "e" => new Quantity(Math.E, false),
                "infinity" => new Quantity(double.PositiveInfinity, false),
                "-infinity" => new Quantity(double.NegativeInfinity, false),
                _ => null,
            },
            _ => null,
        };
    }
}
