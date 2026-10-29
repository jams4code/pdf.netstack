namespace PdfNetStack.Html.Css;

/// <summary>The output medium that media queries are evaluated against: a printed page.</summary>
/// <param name="Width">The page width in CSS pixels.</param>
/// <param name="Height">The page height in CSS pixels.</param>
internal sealed record CssMedia(double Width, double Height)
{
    /// <summary>A4 portrait.</summary>
    public static CssMedia Default { get; } = new(793.7, 1122.5);
}

/// <summary>Evaluates media query lists (Media Queries Level 4) for print.</summary>
internal static class MediaQuery
{
    /// <summary>Whether a media query list matches; an empty list matches everything.</summary>
    public static bool Matches(List<CssValue> query, CssMedia media)
    {
        if (query.All(v => v.IsWhitespace)) return true;

        // A comma separated list matches when any query does.
        var part = new List<CssValue>();
        foreach (CssValue v in query.Append(new CssValue(new CssToken(CssTokenType.Comma))))
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma)
            {
                if (Single(part.Where(p => !p.IsWhitespace).ToList(), media)) return true;
                part = [];
            }
            else
            {
                part.Add(v);
            }
        }

        return false;
    }

    public static bool Matches(string query, CssMedia media) =>
        Matches(CssParser.ComponentValues(CssTokenizer.Tokenize(query)), media);

    private static bool Single(List<CssValue> q, CssMedia media)
    {
        if (q.Count == 0) return false;
        int i = 0;
        bool negate = false;
        if (q[0].IsIdent("not") && q.Count > 1 && q[1].IsToken)
        {
            negate = true;
            i++;
        }
        else if (q[0].IsIdent("only"))
        {
            i++;
        }

        bool result;
        if (i < q.Count && q[i].IsToken && q[i].Token.Type == CssTokenType.Ident)
        {
            string type = q[i++].Token.Value.ToLowerInvariant();
            if (type is "and" or "or" or "not" or "only" or "layer") return false;
            result = type is "print" or "all";
            while (i < q.Count)
            {
                if (!q[i].IsIdent("and") || i + 1 >= q.Count) return false;
                bool? feature = Condition(q[i + 1], media);
                if (feature is null) return false;
                result &= feature.Value;
                i += 2;
            }
        }
        else
        {
            bool? condition = ConditionList(q, ref i, media);
            if (condition is null || i != q.Count) return false;
            result = condition.Value;
        }

        return negate ? !result : result;
    }

    /// <summary>A media condition: features joined by and/or, with not; null when malformed.</summary>
    private static bool? ConditionList(List<CssValue> q, ref int i, CssMedia media)
    {
        if (i < q.Count && q[i].IsIdent("not"))
        {
            i++;
            bool? inner = i < q.Count ? Condition(q[i++], media) : null;
            return !inner;
        }

        bool? result = i < q.Count ? Condition(q[i++], media) : null;
        if (result is null) return null;
        string? joiner = null;
        while (i < q.Count)
        {
            string? word = q[i].IsIdent("and") ? "and" : q[i].IsIdent("or") ? "or" : null;
            if (word is null || (joiner is not null && word != joiner) || i + 1 >= q.Count) return null;
            joiner = word;
            bool? next = Condition(q[i + 1], media);
            if (next is null) return null;
            result = word == "and" ? result.Value && next.Value : result.Value || next.Value;
            i += 2;
        }

        return result;
    }

    /// <summary>One parenthesized feature or nested condition.</summary>
    private static bool? Condition(CssValue block, CssMedia media)
    {
        if (block.Block != CssTokenType.LeftParen || block.Function is not null) return null;
        List<CssValue> inner = block.Children.Where(c => !c.IsWhitespace).ToList();
        if (inner.Count == 0) return null;

        // Nested condition: ((a) or (b)), (not (a)).
        if (inner[0].Block == CssTokenType.LeftParen || inner[0].IsIdent("not"))
        {
            int j = 0;
            bool? nested = ConditionList(inner, ref j, media);
            return j == inner.Count ? nested : null;
        }

        // Boolean feature: (color).
        if (inner.Count == 1 && inner[0].IsToken && inner[0].Token.Type == CssTokenType.Ident) return Feature(inner[0].Token.Value.ToLowerInvariant(), null, "", media);

        // Plain feature: (min-width: 10cm).
        if (inner.Count >= 3 && inner[0].IsToken && inner[0].Token.Type == CssTokenType.Ident && inner[1].IsToken && inner[1].Token.Type == CssTokenType.Colon)
        {
            string name = inner[0].Token.Value.ToLowerInvariant();
            string op = name.StartsWith("min-", StringComparison.Ordinal) ? ">=" : name.StartsWith("max-", StringComparison.Ordinal) ? "<=" : "=";
            if (op != "=") name = name.Substring(4);
            return Feature(name, inner.Skip(2).ToList(), op, media);
        }

        // Range syntax: (width >= 600px), (400px < width <= 800px).
        return Range(inner, media);
    }

    private static bool? Range(List<CssValue> inner, CssMedia media)
    {
        // Split into operands and comparison operators made of < > = delims.
        var operands = new List<List<CssValue>> { new() };
        var ops = new List<string>();
        for (int k = 0; k < inner.Count; k++)
        {
            CssValue v = inner[k];
            if (v.IsToken && v.Token.Type == CssTokenType.Delim && v.Token.Value is "<" or ">" or "=")
            {
                string op = v.Token.Value;
                if (op != "=" && k + 1 < inner.Count && inner[k + 1].IsToken && inner[k + 1].Token is { Type: CssTokenType.Delim, Value: "=" })
                {
                    op += "=";
                    k++;
                }

                ops.Add(op);
                operands.Add([]);
            }
            else
            {
                operands[^1].Add(v);
            }
        }

        if (ops.Count is < 1 or > 2 || operands.Any(o => o.Count == 0)) return null;
        int nameAt = operands.FindIndex(o => o.Count == 1 && o[0].IsToken && o[0].Token.Type == CssTokenType.Ident);
        if (nameAt < 0) return null;
        string name = operands[nameAt][0].Token.Value.ToLowerInvariant();
        bool result = true;
        for (int k = 0; k < ops.Count; k++)
        {
            // Normalize to "feature op value".
            bool featureLeft = k == nameAt;
            List<CssValue> value = featureLeft ? operands[k + 1] : operands[k];
            if (!featureLeft && k + 1 != nameAt) return null;
            string op = featureLeft ? ops[k] : Flip(ops[k]);
            bool? r = Feature(name, value, op, media);
            if (r is null) return null;
            result &= r.Value;
        }

        return result;
    }

    private static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    private static bool? Feature(string name, List<CssValue>? value, string op, CssMedia media)
    {
        switch (name)
        {
            case "width" or "device-width":
                return Compare(media.Width, value is null ? null : Length(value), op, value is null);
            case "height" or "device-height":
                return Compare(media.Height, value is null ? null : Length(value), op, value is null);
            case "aspect-ratio" or "device-aspect-ratio":
                return Compare(media.Width / media.Height, value is null ? null : Ratio(value), op, value is null);
            case "orientation":
                if (value is null) return true;
                string? o = value.Count == 1 && value[0].IsToken ? value[0].Token.Value.ToLowerInvariant() : null;
                return o is "portrait" or "landscape" ? o == (media.Height >= media.Width ? "portrait" : "landscape") : null;
            case "resolution":
                // Print output is resolution independent; treat it as 300 dpi.
                return Compare(300.0 / 96, value is null ? null : Resolution(value), op, value is null);
            case "color":
                return Compare(8, value is null ? null : Integer(value), op, value is null);
            case "color-index" or "monochrome" or "grid":
                return Compare(0, value is null ? null : Integer(value), op, value is null);
            case "hover" or "any-hover" or "pointer" or "any-pointer" or "update" or "scripting":
                return value is null ? false : Keyword(value) is string k ? k == "none" : null;
            case "prefers-color-scheme":
                return value is null ? true : Keyword(value) is string s ? s == "light" : null;
            case "prefers-reduced-motion" or "prefers-reduced-transparency" or "prefers-contrast" or "forced-colors" or "inverted-colors":
                return value is null ? false : Keyword(value) is string p ? p is "no-preference" or "none" : null;
            default:
                return null;
        }
    }

    private static bool? Compare(double actual, double? expected, string op, bool boolean)
    {
        if (boolean) return actual != 0;
        if (expected is not double e) return null;
        const double Epsilon = 1e-6;
        return op switch
        {
            "=" => Math.Abs(actual - e) < Epsilon,
            "<" => actual < e - Epsilon,
            "<=" => actual <= e + Epsilon,
            ">" => actual > e + Epsilon,
            ">=" => actual >= e - Epsilon,
            _ => null,
        };
    }

    private static string? Keyword(List<CssValue> value) =>
        value.Count == 1 && value[0].IsToken && value[0].Token.Type == CssTokenType.Ident ? value[0].Token.Value.ToLowerInvariant() : null;

    private static double? Integer(List<CssValue> value) =>
        value.Count == 1 && value[0].IsToken && value[0].Token is { Type: CssTokenType.Number, IsInteger: true } t ? t.Number : null;

    private static double? Length(List<CssValue> value)
    {
        if (value.Count != 1 || !value[0].IsToken) return null;
        CssToken t = value[0].Token;
        if (t.Type == CssTokenType.Number && t.Number == 0) return 0;
        if (t.Type != CssTokenType.Dimension) return null;

        // Relative units use the initial font size, 16px.
        double? scale = t.Unit switch
        {
            "px" => 1,
            "em" or "rem" => 16,
            "ex" => 8,
            "ch" => 8,
            "cm" => 96 / 2.54,
            "mm" => 96 / 25.4,
            "q" => 96 / 101.6,
            "in" => 96,
            "pt" => 96.0 / 72,
            "pc" => 16,
            _ => null,
        };
        return scale * t.Number;
    }

    private static double? Ratio(List<CssValue> value)
    {
        List<CssValue> v = value.Where(x => !x.IsWhitespace).ToList();
        if (v.Count == 1 && v[0].IsToken && v[0].Token.Type == CssTokenType.Number) return v[0].Token.Number;
        if (v.Count == 3 && v[0].IsToken && v[0].Token.Type == CssTokenType.Number && v[1].IsToken && v[1].Token is { Type: CssTokenType.Delim, Value: "/" } && v[2].IsToken && v[2].Token.Type == CssTokenType.Number && v[2].Token.Number != 0)
        {
            return v[0].Token.Number / v[2].Token.Number;
        }

        return null;
    }

    /// <summary>A resolution in dots per CSS pixel.</summary>
    private static double? Resolution(List<CssValue> value)
    {
        if (value.Count != 1 || !value[0].IsToken || value[0].Token.Type != CssTokenType.Dimension) return null;
        CssToken t = value[0].Token;
        return t.Unit switch
        {
            "dppx" or "x" => t.Number,
            "dpi" => t.Number / 96,
            "dpcm" => t.Number * 2.54 / 96,
            _ => null,
        };
    }
}
