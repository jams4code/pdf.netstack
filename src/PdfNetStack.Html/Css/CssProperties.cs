namespace PdfNetStack.Html.Css;

/// <summary>A longhand property: whether it inherits, its initial value and the grammar its values must match.</summary>
internal sealed class CssProperty
{
    internal CssProperty(string name, bool inherited, string initial, string grammar)
    {
        Name = name;
        Inherited = inherited;
        Initial = CssProperties.Trim(CssParser.ComponentValues(CssTokenizer.Tokenize(initial)));
        Grammar = CssGrammar.Parse(grammar);
    }

    public string Name { get; }

    public bool Inherited { get; }

    public List<CssValue> Initial { get; }

    internal CssGrammar Grammar { get; }
}

/// <summary>
/// A small value grammar: alternatives separated by |, each a type (length, percentage, lp for
/// length or percentage, number, integer, color, image) or a keyword, with an optional repeat
/// count such as {1,2}. * accepts anything.
/// </summary>
internal sealed class CssGrammar
{
    private static readonly HashSet<string> LengthUnits = new(StringComparer.Ordinal)
    {
        "px", "cm", "mm", "q", "in", "pt", "pc", "em", "rem", "ex", "rex", "ch", "rch", "cap", "ic", "lh", "rlh",
        "vw", "vh", "vmin", "vmax", "vi", "vb", "svw", "svh", "lvw", "lvh", "dvw", "dvh",
    };

    private readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private bool _any, _length, _percentage, _number, _integer, _color, _image;
    private int _min = 1, _max = 1;

    public static CssGrammar Parse(string spec)
    {
        var g = new CssGrammar();
        int brace = spec.IndexOf('{');
        if (brace >= 0)
        {
            string[] range = spec.Substring(brace + 1, spec.Length - brace - 2).Split(',');
            g._min = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
            g._max = int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture);
            spec = spec.Substring(0, brace);
        }

        foreach (string alt in spec.Split('|'))
        {
            switch (alt)
            {
                case "*": g._any = true; break;
                case "length": g._length = true; break;
                case "percentage": g._percentage = true; break;
                case "lp": g._length = g._percentage = true; break;
                case "number": g._number = true; break;
                case "integer": g._integer = true; break;
                case "color": g._color = true; break;
                case "image": g._image = true; break;
                default: g._keywords.Add(alt); break;
            }
        }

        return g;
    }

    public bool Matches(List<CssValue> value)
    {
        if (_any) return true;
        int count = 0;
        foreach (CssValue v in value)
        {
            if (v.IsWhitespace) continue;
            if (++count > _max || !MatchesOne(v)) return false;
        }

        return count >= _min;
    }

    public bool MatchesOne(CssValue v)
    {
        if (IsMath(v)) return _length || _percentage || _number || _integer;
        if (!v.IsToken)
        {
            return (_color && v.Function is not null && CssColor.Parse(v) is not null) || (_image && IsImage(v));
        }

        CssToken t = v.Token;
        return t.Type switch
        {
            CssTokenType.Ident => _keywords.Contains(t.Value) || (_color && CssColor.Parse(v) is not null),
            CssTokenType.Number => (_number || (_integer && t.IsInteger) || (_length && t.Number == 0)),
            CssTokenType.Percentage => _percentage,
            CssTokenType.Dimension => _length && LengthUnits.Contains(t.Unit),
            CssTokenType.Hash => _color && CssColor.Parse(v) is not null,
            CssTokenType.Url => _image,
            _ => false,
        };
    }

    public static bool IsLength(CssValue v) =>
        IsMath(v) || (v.IsToken && ((v.Token.Type == CssTokenType.Dimension && LengthUnits.Contains(v.Token.Unit)) || (v.Token.Type == CssTokenType.Number && v.Token.Number == 0)));

    public static bool IsLengthPercentage(CssValue v) => IsLength(v) || (v.IsToken && v.Token.Type == CssTokenType.Percentage);

    public static bool IsMath(CssValue v) => v.Function is "calc" or "min" or "max" or "clamp" or "-webkit-calc";

    public static bool IsImage(CssValue v) =>
        (v.IsToken && v.Token.Type == CssTokenType.Url)
        || v.Function is "url" or "linear-gradient" or "radial-gradient" or "conic-gradient" or "repeating-linear-gradient"
            or "repeating-radial-gradient" or "repeating-conic-gradient" or "image-set" or "-webkit-image-set";
}

/// <summary>The supported properties and shorthand expansion.</summary>
internal static class CssProperties
{
    // Static fields initialize in textual order: All is declared after the arrays Build uses.
    private static readonly HashSet<string> WideKeywords = new(StringComparer.OrdinalIgnoreCase) { "inherit", "initial", "unset", "revert", "revert-layer" };

    private static readonly string[] Sides = ["top", "right", "bottom", "left"];
    private static readonly string[] Corners = ["top-left", "top-right", "bottom-right", "bottom-left"];
    private const string BorderStyles = "none|hidden|dotted|dashed|solid|double|groove|ridge|inset|outset";

    /// <summary>Shorthands and the longhands they set.</summary>
    public static readonly Dictionary<string, string[]> Shorthands = new(StringComparer.Ordinal)
    {
        ["margin"] = [.. Sides.Select(s => "margin-" + s)],
        ["padding"] = [.. Sides.Select(s => "padding-" + s)],
        ["inset"] = Sides,
        ["border-width"] = [.. Sides.Select(s => "border-" + s + "-width")],
        ["border-style"] = [.. Sides.Select(s => "border-" + s + "-style")],
        ["border-color"] = [.. Sides.Select(s => "border-" + s + "-color")],
        ["border-top"] = ["border-top-width", "border-top-style", "border-top-color"],
        ["border-right"] = ["border-right-width", "border-right-style", "border-right-color"],
        ["border-bottom"] = ["border-bottom-width", "border-bottom-style", "border-bottom-color"],
        ["border-left"] = ["border-left-width", "border-left-style", "border-left-color"],
        ["border"] = [.. Sides.SelectMany(s => new[] { "border-" + s + "-width", "border-" + s + "-style", "border-" + s + "-color" })],
        ["border-radius"] = [.. Corners.Select(c => "border-" + c + "-radius")],
        ["outline"] = ["outline-width", "outline-style", "outline-color"],
        ["background"] = ["background-color", "background-image", "background-position", "background-size", "background-repeat", "background-attachment", "background-origin", "background-clip"],
        ["font"] = ["font-style", "font-variant-caps", "font-weight", "font-stretch", "font-size", "line-height", "font-family", "font-variant-ligatures", "font-variant-numeric", "font-feature-settings", "font-kerning"],
        ["list-style"] = ["list-style-type", "list-style-position", "list-style-image"],
        ["text-decoration"] = ["text-decoration-line", "text-decoration-style", "text-decoration-color", "text-decoration-thickness"],
        ["flex"] = ["flex-grow", "flex-shrink", "flex-basis"],
        ["flex-flow"] = ["flex-direction", "flex-wrap"],
        ["gap"] = ["row-gap", "column-gap"],
        ["overflow"] = ["overflow-x", "overflow-y"],
        ["page-break-before"] = ["break-before"],
        ["page-break-after"] = ["break-after"],
        ["page-break-inside"] = ["break-inside"],
        ["place-items"] = ["align-items", "justify-items"],
        ["place-content"] = ["align-content", "justify-content"],
        ["place-self"] = ["align-self", "justify-self"],
        ["font-variant"] = ["font-variant-caps", "font-variant-ligatures", "font-variant-numeric"],
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["word-wrap"] = "overflow-wrap",
        ["grid-gap"] = "gap",
        ["grid-row-gap"] = "row-gap",
        ["grid-column-gap"] = "column-gap",
        ["-webkit-print-color-adjust"] = "print-color-adjust",
    };

    public static readonly Dictionary<string, CssProperty> All = Build();

    public static bool IsCustom(string name) => name.StartsWith("--", StringComparison.Ordinal);

    /// <summary>The value without leading and trailing whitespace.</summary>
    public static List<CssValue> Trim(List<CssValue> value)
    {
        int start = 0, end = value.Count;
        while (start < end && value[start].IsWhitespace) start++;
        while (end > start && value[end - 1].IsWhitespace) end--;
        return start == 0 && end == value.Count ? value : value.GetRange(start, end - start);
    }

    public static bool IsInherited(string name) => IsCustom(name) || (All.TryGetValue(name, out CssProperty? p) && p.Inherited);

    public static bool IsWideKeyword(List<CssValue> value, out string keyword)
    {
        keyword = "";
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count != 1 || !items[0].IsToken || items[0].Token.Type != CssTokenType.Ident || !WideKeywords.Contains(items[0].Token.Value)) return false;
        keyword = items[0].Token.Value.ToLowerInvariant();
        return true;
    }

    public static bool ContainsVar(List<CssValue> value) =>
        value.Any(v => v.Function is "var" or "env" || (!v.IsToken && ContainsVar(v.Children)));

    /// <summary>
    /// Validates a declaration and expands shorthands into longhands. Returns null when the
    /// property is unknown or the value is invalid, so the declaration is dropped.
    /// </summary>
    public static List<CssDeclaration>? Expand(CssDeclaration d)
    {
        string name = Aliases.TryGetValue(d.Property, out string? alias) ? alias : d.Property;
        if (IsCustom(name)) return [d];
        bool shorthand = Shorthands.TryGetValue(name, out string[]? longhands);
        if (!shorthand && !All.ContainsKey(name)) return null;

        if (IsWideKeyword(d.Value, out _))
        {
            return shorthand ? [.. longhands!.Select(l => d with { Property = l })] : [d with { Property = name }];
        }

        // var() can only be checked once substituted: keep the raw value and expand it then.
        if (ContainsVar(d.Value))
        {
            return shorthand ? [.. longhands!.Select(l => d with { Property = l, Shorthand = name })] : [d with { Property = name }];
        }

        if (!shorthand)
        {
            return All[name].Grammar.Matches(d.Value) ? [d with { Property = name }] : null;
        }

        List<(string Property, List<CssValue> Value)>? parts = ExpandShorthand(name, d.Value);
        return parts?.Select(p => new CssDeclaration(p.Property, p.Value, d.Important)).ToList();
    }

    /// <summary>Expands a shorthand whose value has no var(); null when invalid.</summary>
    public static List<(string Property, List<CssValue> Value)>? ExpandShorthand(string name, List<CssValue> value)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count == 0) return null;
        switch (name)
        {
            case "margin" or "padding" or "inset" or "border-width" or "border-style" or "border-color":
                return Box(Shorthands[name], items);
            case "border-top" or "border-right" or "border-bottom" or "border-left" or "outline":
                return Border(Shorthands[name], items);
            case "border":
                List<(string, List<CssValue>)>? side = Border(Shorthands["border-top"], items);
                return side is null ? null : [.. Sides.SelectMany(s => side.Select(p => (p.Item1.Replace("-top-", "-" + s + "-"), p.Item2)))];
            case "border-radius":
                return Radius(items);
            case "background":
                return Background(value);
            case "font":
                return Font(value);
            case "list-style":
                return ListStyle(items);
            case "text-decoration":
                return TextDecoration(items);
            case "flex":
                return Flex(items);
            case "flex-flow":
                return AnyOrder(items, ("flex-direction", "row|row-reverse|column|column-reverse"), ("flex-wrap", "nowrap|wrap|wrap-reverse"));
            case "gap" or "overflow" or "place-items" or "place-content" or "place-self":
                return Pair(Shorthands[name], items);
            case "page-break-before" or "page-break-after" or "page-break-inside":
                return PageBreak(Shorthands[name][0], items);
            case "font-variant":
                return FontVariant(items);
            default:
                return null;
        }
    }

    private static List<CssValue> One(CssValue v) => [v];

    private static List<CssValue> Ident(string name) => [new CssValue(new CssToken(CssTokenType.Ident, name))];

    private static List<(string, List<CssValue>)>? Box(string[] longhands, List<CssValue> items)
    {
        if (items.Count > 4) return null;
        int[] map = items.Count switch
        {
            1 => [0, 0, 0, 0],
            2 => [0, 1, 0, 1],
            3 => [0, 1, 2, 1],
            _ => [0, 1, 2, 3],
        };
        var result = new List<(string, List<CssValue>)>();
        for (int k = 0; k < 4; k++)
        {
            CssValue v = items[map[k]];
            if (!All[longhands[k]].Grammar.MatchesOne(v)) return null;
            result.Add((longhands[k], One(v)));
        }

        return result;
    }

    /// <summary>Width, style and color in any order, each at most once.</summary>
    private static List<(string, List<CssValue>)>? Border(string[] longhands, List<CssValue> items)
    {
        CssValue? width = null, style = null, color = null;
        CssGrammar widths = All[longhands[0]].Grammar, styles = All[longhands[1]].Grammar, colors = All[longhands[2]].Grammar;
        foreach (CssValue v in items)
        {
            if (style is null && styles.MatchesOne(v)) style = v;
            else if (width is null && widths.MatchesOne(v)) width = v;
            else if (color is null && colors.MatchesOne(v)) color = v;
            else return null;
        }

        return
        [
            (longhands[0], width is null ? All[longhands[0]].Initial : One(width)),
            (longhands[1], style is null ? All[longhands[1]].Initial : One(style)),
            (longhands[2], color is null ? All[longhands[2]].Initial : One(color)),
        ];
    }

    private static List<(string, List<CssValue>)>? Radius(List<CssValue> items)
    {
        int slash = items.FindIndex(v => v.IsToken && v.Token is { Type: CssTokenType.Delim, Value: "/" });
        List<CssValue> horizontal = slash < 0 ? items : items.Take(slash).ToList();
        List<CssValue> vertical = slash < 0 ? horizontal : items.Skip(slash + 1).ToList();
        if (horizontal.Count is < 1 or > 4 || vertical.Count is < 1 or > 4) return null;
        if (!horizontal.Concat(vertical).All(CssGrammar.IsLengthPercentage)) return null;

        static CssValue Corner(List<CssValue> values, int corner) => values[values.Count switch
        {
            1 => 0,
            2 => corner % 2,
            3 => corner == 3 ? 1 : corner,
            _ => corner,
        }];

        var space = new CssValue(new CssToken(CssTokenType.Whitespace));
        return [.. Corners.Select((c, k) => ("border-" + c + "-radius", new List<CssValue> { Corner(horizontal, k), space, Corner(vertical, k) }))];
    }

    private static List<List<CssValue>> SplitCommas(List<CssValue> value)
    {
        var layers = new List<List<CssValue>> { new() };
        foreach (CssValue v in value)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma) layers.Add([]);
            else layers[^1].Add(v);
        }

        return layers;
    }

    private static List<CssValue> JoinCommas(IEnumerable<List<CssValue>> layers)
    {
        var result = new List<CssValue>();
        foreach (List<CssValue> layer in layers)
        {
            if (result.Count > 0)
            {
                result.Add(new CssValue(new CssToken(CssTokenType.Comma)));
                result.Add(new CssValue(new CssToken(CssTokenType.Whitespace)));
            }

            result.AddRange(layer);
        }

        return result;
    }

    private static readonly HashSet<string> PositionKeywords = new(StringComparer.OrdinalIgnoreCase) { "left", "right", "top", "bottom", "center" };
    private static readonly HashSet<string> RepeatKeywords = new(StringComparer.OrdinalIgnoreCase) { "repeat", "repeat-x", "repeat-y", "no-repeat", "space", "round" };
    private static readonly HashSet<string> BoxKeywords = new(StringComparer.OrdinalIgnoreCase) { "border-box", "padding-box", "content-box" };

    private static List<(string, List<CssValue>)>? Background(List<CssValue> value)
    {
        List<List<CssValue>> layers = SplitCommas(value);
        var images = new List<List<CssValue>>();
        var positions = new List<List<CssValue>>();
        var sizes = new List<List<CssValue>>();
        var repeats = new List<List<CssValue>>();
        var attachments = new List<List<CssValue>>();
        var origins = new List<List<CssValue>>();
        var clips = new List<List<CssValue>>();
        CssValue? color = null;
        var space = new CssValue(new CssToken(CssTokenType.Whitespace));

        for (int l = 0; l < layers.Count; l++)
        {
            List<CssValue> items = layers[l].Where(v => !v.IsWhitespace).ToList();
            if (items.Count == 0) return null;
            CssValue? image = null, attachment = null;
            List<CssValue>? position = null, size = null, repeat = null;
            var boxes = new List<CssValue>();
            for (int k = 0; k < items.Count; k++)
            {
                CssValue v = items[k];
                bool ident = v.IsToken && v.Token.Type == CssTokenType.Ident;
                if (image is null && (CssGrammar.IsImage(v) || v.IsIdent("none")))
                {
                    image = v;
                }
                else if (position is null && (CssGrammar.IsLengthPercentage(v) || (ident && PositionKeywords.Contains(v.Token.Value))))
                {
                    position = [];
                    while (k < items.Count && (CssGrammar.IsLengthPercentage(items[k]) || (items[k].IsToken && items[k].Token.Type == CssTokenType.Ident && PositionKeywords.Contains(items[k].Token.Value))))
                    {
                        if (position.Count > 0) position.Add(space);
                        position.Add(items[k++]);
                    }

                    if (position.Count > 7) return null;
                    if (k < items.Count && items[k].IsToken && items[k].Token is { Type: CssTokenType.Delim, Value: "/" })
                    {
                        k++;
                        size = [];
                        while (k < items.Count && size.Count < 3 && (CssGrammar.IsLengthPercentage(items[k]) || items[k].IsIdent("auto") || items[k].IsIdent("cover") || items[k].IsIdent("contain")))
                        {
                            if (size.Count > 0) size.Add(space);
                            size.Add(items[k++]);
                        }

                        if (size.Count == 0) return null;
                    }

                    k--;
                }
                else if (repeat is null && ident && RepeatKeywords.Contains(v.Token.Value))
                {
                    repeat = [v];
                    if (k + 1 < items.Count && items[k + 1].IsToken && items[k + 1].Token.Type == CssTokenType.Ident && RepeatKeywords.Contains(items[k + 1].Token.Value)
                        && !v.IsIdent("repeat-x") && !v.IsIdent("repeat-y") && !items[k + 1].IsIdent("repeat-x") && !items[k + 1].IsIdent("repeat-y"))
                    {
                        repeat.Add(space);
                        repeat.Add(items[++k]);
                    }
                }
                else if (attachment is null && ident && v.Token.Value.ToLowerInvariant() is "scroll" or "fixed" or "local")
                {
                    attachment = v;
                }
                else if (boxes.Count < 2 && ident && (BoxKeywords.Contains(v.Token.Value) || (boxes.Count == 1 && v.IsIdent("text"))))
                {
                    boxes.Add(v);
                }
                else if (color is null && l == layers.Count - 1 && CssColor.Parse(v) is not null)
                {
                    color = v;
                }
                else
                {
                    return null;
                }
            }

            images.Add(image is null ? Ident("none") : One(image));
            positions.Add(position ?? All["background-position"].Initial);
            sizes.Add(size ?? Ident("auto"));
            repeats.Add(repeat ?? Ident("repeat"));
            attachments.Add(attachment is null ? Ident("scroll") : One(attachment));
            origins.Add(boxes.Count > 0 ? One(boxes[0]) : Ident("padding-box"));
            clips.Add(boxes.Count > 1 ? One(boxes[1]) : boxes.Count == 1 ? One(boxes[0]) : Ident("border-box"));
        }

        return
        [
            ("background-color", color is null ? Ident("transparent") : One(color)),
            ("background-image", JoinCommas(images)),
            ("background-position", JoinCommas(positions)),
            ("background-size", JoinCommas(sizes)),
            ("background-repeat", JoinCommas(repeats)),
            ("background-attachment", JoinCommas(attachments)),
            ("background-origin", JoinCommas(origins)),
            ("background-clip", JoinCommas(clips)),
        ];
    }

    private static List<(string, List<CssValue>)>? Font(List<CssValue> value)
    {
        // [style || variant || weight || stretch]? size [/ line-height]? family
        int i = 0;
        CssValue? style = null, variant = null, weight = null, stretch = null;
        int normals = 0;
        while (true)
        {
            CssSelector.SkipWhitespace(value, ref i);
            if (i >= value.Count) return null;
            CssValue v = value[i];
            if (v.IsIdent("normal")) normals++;
            else if (style is null && All["font-style"].Grammar.MatchesOne(v)) style = v;
            else if (variant is null && (v.IsIdent("small-caps"))) variant = v;
            else if (weight is null && v.IsToken && v.Token.Type is CssTokenType.Ident or CssTokenType.Number && All["font-weight"].Grammar.MatchesOne(v)
                     && !(v.Token.Type == CssTokenType.Number && (v.Token.Number is < 1 or > 1000 || NextIsSizeSeparator(value, i)))) weight = v;
            else if (stretch is null && v.IsToken && v.Token.Type == CssTokenType.Ident && All["font-stretch"].Grammar.MatchesOne(v)) stretch = v;
            else break;
            i++;
            if (normals + (style is null ? 0 : 1) + (variant is null ? 0 : 1) + (weight is null ? 0 : 1) + (stretch is null ? 0 : 1) > 4) return null;
        }

        CssValue size = value[i++];
        if (!All["font-size"].Grammar.MatchesOne(size)) return null;
        List<CssValue>? lineHeight = null;
        CssSelector.SkipWhitespace(value, ref i);
        if (i < value.Count && value[i].IsToken && value[i].Token is { Type: CssTokenType.Delim, Value: "/" })
        {
            i++;
            CssSelector.SkipWhitespace(value, ref i);
            if (i >= value.Count || !All["line-height"].Grammar.MatchesOne(value[i])) return null;
            lineHeight = One(value[i++]);
        }

        List<CssValue> family = value.Skip(i).ToList();
        while (family.Count > 0 && family[0].IsWhitespace) family.RemoveAt(0);
        if (family.Count == 0 || !ValidFamily(family)) return null;

        return
        [
            ("font-style", style is null ? Ident("normal") : One(style)),
            ("font-variant-caps", variant is null ? Ident("normal") : One(variant)),
            ("font-weight", weight is null ? Ident("normal") : One(weight)),
            ("font-stretch", stretch is null ? Ident("normal") : One(stretch)),
            ("font-size", One(size)),
            ("line-height", lineHeight ?? Ident("normal")),
            ("font-family", family),
            ("font-variant-ligatures", Ident("normal")),
            ("font-variant-numeric", Ident("normal")),
            ("font-feature-settings", Ident("normal")),
            ("font-kerning", Ident("auto")),
        ];
    }

    // "font: 700 12px x" has a weight; "font: 12px/1.5 x" does not, and neither does a bare "0".
    private static bool NextIsSizeSeparator(List<CssValue> value, int i)
    {
        int j = i + 1;
        CssSelector.SkipWhitespace(value, ref j);
        return j < value.Count && value[j].IsToken && value[j].Token is { Type: CssTokenType.Delim, Value: "/" };
    }

    /// <summary>A comma separated list of family names: strings, or one or more identifiers.</summary>
    public static bool ValidFamily(List<CssValue> family)
    {
        foreach (List<CssValue> name in SplitCommas(family))
        {
            List<CssValue> items = name.Where(v => !v.IsWhitespace).ToList();
            if (items.Count == 0) return false;
            if (items.Count == 1 && items[0].IsToken && items[0].Token.Type == CssTokenType.String) continue;
            if (!items.All(v => v.IsToken && v.Token.Type == CssTokenType.Ident)) return false;
        }

        return true;
    }

    private static List<(string, List<CssValue>)>? ListStyle(List<CssValue> items)
    {
        CssValue? type = null, position = null, image = null;
        int nones = 0;
        foreach (CssValue v in items)
        {
            if (v.IsIdent("none")) nones++;
            else if (position is null && (v.IsIdent("inside") || v.IsIdent("outside"))) position = v;
            else if (image is null && CssGrammar.IsImage(v)) image = v;
            else if (type is null && v.IsToken && v.Token.Type is CssTokenType.Ident or CssTokenType.String) type = v;
            else return null;
        }

        if (nones > 2 || (nones == 2 && (type is not null || image is not null)) || (nones == 1 && type is not null && image is not null)) return null;
        List<CssValue> none = Ident("none");
        return
        [
            ("list-style-type", type is not null ? One(type) : nones > 0 ? none : Ident("disc")),
            ("list-style-position", position is null ? Ident("outside") : One(position)),
            ("list-style-image", image is not null ? One(image) : none),
        ];
    }

    private static List<(string, List<CssValue>)>? TextDecoration(List<CssValue> items)
    {
        var lines = new List<CssValue>();
        CssValue? style = null, color = null, thickness = null;
        var space = new CssValue(new CssToken(CssTokenType.Whitespace));
        foreach (CssValue v in items)
        {
            if (v.IsIdent("none") && lines.Count == 0) lines.Add(v);
            else if (v.IsToken && v.Token.Type == CssTokenType.Ident && v.Token.Value.ToLowerInvariant() is "underline" or "overline" or "line-through" or "blink"
                     && !lines.Any(l => l.IsIdent("none") || l.IsIdent(v.Token.Value)))
            {
                if (lines.Count > 0) lines.Add(space);
                lines.Add(v);
            }
            else if (style is null && All["text-decoration-style"].Grammar.MatchesOne(v)) style = v;
            else if (thickness is null && All["text-decoration-thickness"].Grammar.MatchesOne(v)) thickness = v;
            else if (color is null && CssColor.Parse(v) is not null) color = v;
            else return null;
        }

        return
        [
            ("text-decoration-line", lines.Count == 0 ? Ident("none") : lines),
            ("text-decoration-style", style is null ? Ident("solid") : One(style)),
            ("text-decoration-color", color is null ? Ident("currentcolor") : One(color)),
            ("text-decoration-thickness", thickness is null ? Ident("auto") : One(thickness)),
        ];
    }

    private static List<(string, List<CssValue>)>? Flex(List<CssValue> items)
    {
        static List<CssValue> Num(double n) => [new CssValue(new CssToken(CssTokenType.Number, Number: n))];
        List<CssValue> zeroPercent = [new CssValue(new CssToken(CssTokenType.Percentage, Number: 0))];

        if (items.Count == 1 && items[0].IsIdent("none")) return [("flex-grow", Num(0)), ("flex-shrink", Num(0)), ("flex-basis", Ident("auto"))];
        if (items.Count == 1 && items[0].IsIdent("auto")) return [("flex-grow", Num(1)), ("flex-shrink", Num(1)), ("flex-basis", Ident("auto"))];

        // Unitless numbers are grow then shrink, kept together; anything else is the basis.
        var numbers = new List<CssValue>();
        CssValue? basis = null;
        bool previousWasNumber = false;
        foreach (CssValue v in items)
        {
            bool number = v.IsToken && v.Token.Type == CssTokenType.Number && v.Token.Number >= 0;
            if (number && (numbers.Count == 0 || (numbers.Count == 1 && previousWasNumber))) numbers.Add(v);
            else if (basis is null && All["flex-basis"].Grammar.MatchesOne(v)) basis = v;
            else return null;
            previousWasNumber = number && numbers.Count > 0 && numbers[^1] == v;
        }

        if (numbers.Count == 0 && basis is null) return null;
        return
        [
            ("flex-grow", numbers.Count > 0 ? One(numbers[0]) : Num(1)),
            ("flex-shrink", numbers.Count > 1 ? One(numbers[1]) : Num(1)),
            ("flex-basis", basis is not null ? One(basis) : zeroPercent),
        ];
    }

    private static List<(string, List<CssValue>)>? AnyOrder(List<CssValue> items, params (string Property, string Grammar)[] parts)
    {
        var found = new CssValue?[parts.Length];
        CssGrammar[] grammars = [.. parts.Select(p => CssGrammar.Parse(p.Grammar))];
        foreach (CssValue v in items)
        {
            int k = 0;
            while (k < parts.Length && (found[k] is not null || !grammars[k].MatchesOne(v))) k++;
            if (k == parts.Length) return null;
            found[k] = v;
        }

        return [.. parts.Select((p, k) => (p.Property, found[k] is CssValue v ? One(v) : All[p.Property].Initial))];
    }

    private static List<(string, List<CssValue>)>? Pair(string[] longhands, List<CssValue> items)
    {
        if (items.Count > 2) return null;
        CssValue first = items[0], second = items.Count > 1 ? items[1] : items[0];
        if (!All[longhands[0]].Grammar.MatchesOne(first) || !All[longhands[1]].Grammar.MatchesOne(second)) return null;
        return [(longhands[0], One(first)), (longhands[1], One(second))];
    }

    private static List<(string, List<CssValue>)>? PageBreak(string longhand, List<CssValue> items)
    {
        if (items.Count != 1 || !items[0].IsToken || items[0].Token.Type != CssTokenType.Ident) return null;
        string? mapped = items[0].Token.Value.ToLowerInvariant() switch
        {
            "always" => longhand == "break-inside" ? null : "page",
            "avoid" => "avoid",
            "auto" => "auto",
            "left" or "right" => longhand == "break-inside" ? null : items[0].Token.Value.ToLowerInvariant(),
            _ => null,
        };
        return mapped is null ? null : [(longhand, Ident(mapped))];
    }

    private static List<(string, List<CssValue>)>? FontVariant(List<CssValue> items)
    {
        if (items.Count == 1 && (items[0].IsIdent("normal") || items[0].IsIdent("none")))
        {
            return [("font-variant-caps", Ident("normal")), ("font-variant-ligatures", One(items[0])), ("font-variant-numeric", Ident("normal"))];
        }

        CssValue? caps = null;
        var ligatures = new List<CssValue>();
        var numeric = new List<CssValue>();
        var space = new CssValue(new CssToken(CssTokenType.Whitespace));
        foreach (CssValue v in items)
        {
            if (caps is null && All["font-variant-caps"].Grammar.MatchesOne(v) && !v.IsIdent("normal")) caps = v;
            else if (All["font-variant-ligatures"].Grammar.MatchesOne(v) && !v.IsIdent("normal") && !v.IsIdent("none")) AddSpaced(ligatures, v);
            else if (All["font-variant-numeric"].Grammar.MatchesOne(v) && !v.IsIdent("normal")) AddSpaced(numeric, v);
            else return null;
        }

        void AddSpaced(List<CssValue> list, CssValue v)
        {
            if (list.Count > 0) list.Add(space);
            list.Add(v);
        }

        return
        [
            ("font-variant-caps", caps is null ? Ident("normal") : One(caps)),
            ("font-variant-ligatures", ligatures.Count == 0 ? Ident("normal") : ligatures),
            ("font-variant-numeric", numeric.Count == 0 ? Ident("normal") : numeric),
        ];
    }

    private static Dictionary<string, CssProperty> Build()
    {
        var all = new Dictionary<string, CssProperty>(StringComparer.Ordinal);
        void Add(string name, bool inherited, string initial, string grammar) => all[name] = new CssProperty(name, inherited, initial, grammar);
        const bool I = true, N = false;

        // Inherited: text and fonts.
        Add("color", I, "canvastext", "color");
        Add("font-family", I, "serif", "*");
        Add("font-size", I, "medium", "xx-small|x-small|small|medium|large|x-large|xx-large|xxx-large|larger|smaller|lp");
        Add("font-style", I, "normal", "normal|italic|oblique");
        Add("font-weight", I, "normal", "normal|bold|bolder|lighter|number");
        Add("font-stretch", I, "normal", "normal|ultra-condensed|extra-condensed|condensed|semi-condensed|semi-expanded|expanded|extra-expanded|ultra-expanded|percentage");
        Add("font-variant-caps", I, "normal", "normal|small-caps|all-small-caps|petite-caps|all-petite-caps|unicase|titling-caps");
        Add("font-variant-ligatures", I, "normal", "normal|none|common-ligatures|no-common-ligatures|discretionary-ligatures|no-discretionary-ligatures|historical-ligatures|no-historical-ligatures|contextual|no-contextual{1,4}");
        Add("font-variant-numeric", I, "normal", "normal|lining-nums|oldstyle-nums|proportional-nums|tabular-nums|diagonal-fractions|stacked-fractions|ordinal|slashed-zero{1,5}");
        Add("font-feature-settings", I, "normal", "*");
        Add("font-kerning", I, "auto", "auto|normal|none");
        Add("line-height", I, "normal", "normal|number|lp");
        Add("letter-spacing", I, "normal", "normal|lp");
        Add("word-spacing", I, "normal", "normal|lp");
        Add("text-align", I, "start", "start|end|left|right|center|justify|justify-all|match-parent|-webkit-center|-webkit-left|-webkit-right");
        Add("text-align-last", I, "auto", "auto|start|end|left|right|center|justify");
        Add("text-indent", I, "0", "lp");
        Add("text-transform", I, "none", "none|capitalize|uppercase|lowercase|full-width");
        Add("white-space", I, "normal", "normal|pre|nowrap|pre-wrap|pre-line|break-spaces");
        Add("direction", I, "ltr", "ltr|rtl");
        Add("visibility", I, "visible", "visible|hidden|collapse");
        Add("list-style-type", I, "disc", "*");
        Add("list-style-position", I, "outside", "inside|outside");
        Add("list-style-image", I, "none", "none|image");
        Add("border-collapse", I, "separate", "separate|collapse");
        Add("border-spacing", I, "0", "length{1,2}");
        Add("caption-side", I, "top", "top|bottom");
        Add("empty-cells", I, "show", "show|hide");
        Add("quotes", I, "auto", "*");
        Add("orphans", I, "2", "integer");
        Add("widows", I, "2", "integer");
        Add("hyphens", I, "manual", "none|manual|auto");
        Add("tab-size", I, "8", "number|length");
        Add("overflow-wrap", I, "normal", "normal|break-word|anywhere");
        Add("word-break", I, "normal", "normal|break-all|keep-all|break-word");
        Add("text-shadow", I, "none", "*");
        Add("writing-mode", I, "horizontal-tb", "horizontal-tb|vertical-rl|vertical-lr");
        Add("print-color-adjust", I, "economy", "economy|exact");
        Add("image-rendering", I, "auto", "*");

        // Not inherited: box model and layout.
        Add("display", N, "inline", "block|inline|inline-block|flex|inline-flex|grid|inline-grid|table|inline-table|table-row|table-cell|table-column|table-column-group|table-row-group|table-header-group|table-footer-group|table-caption|list-item|none|contents|flow-root|flow|run-in{1,3}");
        Add("position", N, "static", "*");
        foreach (string s in Sides) Add(s, N, "auto", "lp|auto");
        Add("float", N, "none", "none|left|right|inline-start|inline-end|footnote");
        Add("clear", N, "none", "none|left|right|both|inline-start|inline-end");
        Add("width", N, "auto", "lp|auto|min-content|max-content|fit-content");
        Add("height", N, "auto", "lp|auto|min-content|max-content|fit-content");
        Add("min-width", N, "auto", "lp|auto|min-content|max-content|fit-content");
        Add("min-height", N, "auto", "lp|auto|min-content|max-content|fit-content");
        Add("max-width", N, "none", "lp|none|min-content|max-content|fit-content");
        Add("max-height", N, "none", "lp|none|min-content|max-content|fit-content");
        Add("box-sizing", N, "content-box", "content-box|border-box");
        Add("aspect-ratio", N, "auto", "*");
        foreach (string s in Sides)
        {
            Add("margin-" + s, N, "0", "lp|auto");
            Add("padding-" + s, N, "0", "lp");
            Add("border-" + s + "-width", N, "medium", "length|thin|medium|thick");
            Add("border-" + s + "-style", N, "none", BorderStyles);
            Add("border-" + s + "-color", N, "currentcolor", "color");
        }

        foreach (string c in Corners) Add("border-" + c + "-radius", N, "0", "lp{1,2}");
        Add("overflow-x", N, "visible", "visible|hidden|clip|scroll|auto");
        Add("overflow-y", N, "visible", "visible|hidden|clip|scroll|auto");
        Add("text-overflow", N, "clip", "*");
        Add("z-index", N, "auto", "auto|integer");
        Add("opacity", N, "1", "number|percentage");
        Add("vertical-align", N, "baseline", "baseline|sub|super|text-top|text-bottom|middle|top|bottom|lp");
        Add("unicode-bidi", N, "normal", "normal|embed|isolate|bidi-override|isolate-override|plaintext");
        Add("table-layout", N, "auto", "auto|fixed");
        Add("object-fit", N, "fill", "fill|contain|cover|none|scale-down");
        Add("object-position", N, "50% 50%", "*");
        Add("transform", N, "none", "*");
        Add("transform-origin", N, "50% 50%", "*");
        Add("box-shadow", N, "none", "*");
        Add("mix-blend-mode", N, "normal", "normal|multiply|screen|overlay|darken|lighten|color-dodge|color-burn|hard-light|soft-light|difference|exclusion|hue|saturation|color|luminosity");
        Add("clip-path", N, "none", "*");
        Add("filter", N, "none", "*");

        // Backgrounds.
        Add("background-color", N, "transparent", "color");
        Add("background-image", N, "none", "*");
        Add("background-position", N, "0% 0%", "*");
        Add("background-size", N, "auto", "*");
        Add("background-repeat", N, "repeat", "*");
        Add("background-attachment", N, "scroll", "*");
        Add("background-origin", N, "padding-box", "*");
        Add("background-clip", N, "border-box", "*");

        // Decoration and outline.
        Add("text-decoration-line", N, "none", "none|underline|overline|line-through|blink{1,4}");
        Add("text-decoration-style", N, "solid", "solid|double|dotted|dashed|wavy");
        Add("text-decoration-color", N, "currentcolor", "color");
        Add("text-decoration-thickness", N, "auto", "auto|from-font|lp");
        Add("outline-width", N, "medium", "length|thin|medium|thick");
        Add("outline-style", N, "none", "auto|" + BorderStyles);
        Add("outline-color", N, "currentcolor", "color|invert");
        Add("outline-offset", N, "0", "length");

        // Generated content and paged media.
        Add("content", N, "normal", "*");
        Add("counter-reset", N, "none", "*");
        Add("counter-increment", N, "none", "*");
        Add("counter-set", N, "none", "*");
        Add("break-before", N, "auto", "auto|avoid|avoid-page|page|left|right|recto|verso|avoid-column|column");
        Add("break-after", N, "auto", "auto|avoid|avoid-page|page|left|right|recto|verso|avoid-column|column");
        Add("break-inside", N, "auto", "auto|avoid|avoid-page|avoid-column");
        Add("page", N, "auto", "*");
        Add("string-set", N, "none", "*");
        Add("bookmark-level", N, "none", "none|integer");
        Add("bookmark-label", N, "content(text)", "*");
        Add("bookmark-state", N, "open", "open|closed");
        Add("size", N, "auto", "*");

        // Flexbox and alignment.
        Add("flex-direction", N, "row", "row|row-reverse|column|column-reverse");
        Add("flex-wrap", N, "nowrap", "nowrap|wrap|wrap-reverse");
        Add("flex-grow", N, "0", "number");
        Add("flex-shrink", N, "1", "number");
        Add("flex-basis", N, "auto", "lp|auto|content|min-content|max-content|fit-content");
        Add("order", N, "0", "integer");
        Add("justify-content", N, "normal", "*");
        Add("justify-items", N, "legacy", "*");
        Add("justify-self", N, "auto", "*");
        Add("align-content", N, "normal", "*");
        Add("align-items", N, "normal", "*");
        Add("align-self", N, "auto", "*");
        Add("row-gap", N, "normal", "normal|lp");
        Add("column-gap", N, "normal", "normal|lp");
        return all;
    }
}
