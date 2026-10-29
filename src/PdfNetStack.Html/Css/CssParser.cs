namespace PdfNetStack.Html.Css;

/// <summary>A property and its value as written.</summary>
internal sealed record CssDeclaration(string Property, List<CssValue> Value, bool Important)
{
    public override string ToString() => Property + ": " + string.Concat(Value.Select(v => v.ToString())).Trim() + (Important ? " !important" : "");
}

internal abstract record CssRule;

/// <summary>A style rule: selectors and their declarations.</summary>
internal sealed record CssStyleRule(List<CssSelector> Selectors, List<CssDeclaration> Declarations) : CssRule;

/// <summary>An @media rule and the rules inside it.</summary>
internal sealed record CssMediaRule(List<CssValue> Query, List<CssRule> Rules) : CssRule;

/// <summary>An @page rule: page selectors such as :first, declarations, and margin boxes (@top-center and the like).</summary>
internal sealed record CssPageRule(string? Name, string? Pseudo, List<CssDeclaration> Declarations, Dictionary<string, List<CssDeclaration>> MarginBoxes) : CssRule;

/// <summary>An @font-face rule.</summary>
internal sealed record CssFontFaceRule(List<CssDeclaration> Declarations) : CssRule;

/// <summary>An @import rule.</summary>
internal sealed record CssImportRule(string Url, List<CssValue> Media) : CssRule;

/// <summary>Parses style sheets and declaration lists (CSS Syntax Level 3, 5).</summary>
internal static class CssParser
{
    private const int MaxDepth = 64;

    public static List<CssRule> ParseStyleSheet(string css)
    {
        List<CssValue> values = ComponentValues(CssTokenizer.Tokenize(css));
        return Rules(values, 0);
    }

    /// <summary>Parses the contents of a style attribute.</summary>
    public static List<CssDeclaration> ParseDeclarations(string css) => Declarations(ComponentValues(CssTokenizer.Tokenize(css)));

    /// <summary>Turns tokens into component values: blocks and functions hold their contents.</summary>
    public static List<CssValue> ComponentValues(List<CssToken> tokens)
    {
        int i = 0;
        return Consume(tokens, ref i, null, 0);
    }

    private static List<CssValue> Consume(List<CssToken> tokens, ref int i, CssTokenType? closing, int depth)
    {
        var values = new List<CssValue>();
        while (i < tokens.Count)
        {
            CssToken t = tokens[i++];
            if (closing is CssTokenType close && t.Type == close) return values;
            CssTokenType? opens = t.Type switch
            {
                CssTokenType.LeftBrace => CssTokenType.RightBrace,
                CssTokenType.LeftBracket => CssTokenType.RightBracket,
                CssTokenType.LeftParen or CssTokenType.Function => CssTokenType.RightParen,
                _ => null,
            };
            if (opens is CssTokenType end)
            {
                List<CssValue> inner = depth < MaxDepth ? Consume(tokens, ref i, end, depth + 1) : [];
                CssTokenType block = t.Type == CssTokenType.Function ? CssTokenType.LeftParen : t.Type;
                values.Add(new CssValue(block, t.Type == CssTokenType.Function ? t.Value.ToLowerInvariant() : null, inner));
            }
            else
            {
                values.Add(new CssValue(t));
            }
        }

        return values;
    }

    private static List<CssRule> Rules(List<CssValue> values, int depth)
    {
        var rules = new List<CssRule>();
        var prelude = new List<CssValue>();
        string? at = null;
        foreach (CssValue v in values)
        {
            if (v.IsToken && v.Token.Type is CssTokenType.Cdo or CssTokenType.Cdc) continue;
            if (prelude.Count == 0 && at is null && v.IsWhitespace) continue;
            if (prelude.Count == 0 && at is null && v.IsToken && v.Token.Type == CssTokenType.AtKeyword)
            {
                at = v.Token.Value.ToLowerInvariant();
                continue;
            }

            if (v.IsToken && v.Token.Type == CssTokenType.Semicolon && at is not null)
            {
                if (at == "import") AddImport(rules, prelude);
                at = null;
                prelude = [];
                continue;
            }

            if (v.Block == CssTokenType.LeftBrace && v.Function is null)
            {
                CssRule? rule = at is null ? StyleRule(prelude, v.Children) : AtRule(at, prelude, v.Children, depth);
                if (rule is not null) rules.Add(rule);
                at = null;
                prelude = [];
                continue;
            }

            prelude.Add(v);
        }

        return rules;
    }

    private static void AddImport(List<CssRule> rules, List<CssValue> prelude)
    {
        CssValue? target = prelude.FirstOrDefault(p => !p.IsWhitespace);
        string? url = target switch
        {
            { IsToken: true, Token.Type: CssTokenType.Url or CssTokenType.String } => target.Token.Value,
            { Function: "url" } => target.Children.FirstOrDefault(c => c.IsToken && c.Token.Type == CssTokenType.String)?.Token.Value,
            _ => null,
        };
        if (url is not null) rules.Add(new CssImportRule(url, [.. prelude.SkipWhile(p => p != target).Skip(1)]));
    }

    private static CssRule? StyleRule(List<CssValue> prelude, List<CssValue> block)
    {
        List<CssSelector>? selectors = CssSelector.ParseList(prelude);
        return selectors is null ? null : new CssStyleRule(selectors, Declarations(block));
    }

    private static CssRule? AtRule(string name, List<CssValue> prelude, List<CssValue> block, int depth)
    {
        switch (name)
        {
            case "media" when depth < MaxDepth:
                return new CssMediaRule(prelude, Rules(block, depth + 1));
            case "page":
                return PageRule(prelude, block);
            case "font-face":
                return new CssFontFaceRule(Declarations(block));
            default:
                return null; // @keyframes, @supports and others are not used for print layout
        }
    }

    private static CssPageRule PageRule(List<CssValue> prelude, List<CssValue> block)
    {
        string? pageName = null, pseudo = null;
        for (int i = 0; i < prelude.Count; i++)
        {
            CssValue v = prelude[i];
            if (v.IsToken && v.Token.Type == CssTokenType.Colon && i + 1 < prelude.Count && prelude[i + 1].IsToken)
            {
                pseudo = prelude[++i].Token.Value.ToLowerInvariant();
            }
            else if (v.IsToken && v.Token.Type == CssTokenType.Ident)
            {
                pageName = v.Token.Value;
            }
        }

        // Margin boxes are nested at-rules; the rest are declarations.
        var margins = new Dictionary<string, List<CssDeclaration>>(StringComparer.Ordinal);
        var own = new List<CssValue>();
        string? box = null;
        foreach (CssValue v in block)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.AtKeyword)
            {
                box = v.Token.Value.ToLowerInvariant();
                continue;
            }

            if (box is not null && v.Block == CssTokenType.LeftBrace && v.Function is null)
            {
                margins[box] = Declarations(v.Children);
                box = null;
                continue;
            }

            if (box is null) own.Add(v);
        }

        return new CssPageRule(pageName, pseudo, Declarations(own), margins);
    }

    /// <summary>A declaration list: name, colon, value, optionally !important, separated by semicolons.</summary>
    private static List<CssDeclaration> Declarations(List<CssValue> values)
    {
        var declarations = new List<CssDeclaration>();
        var current = new List<CssValue>();
        foreach (CssValue v in values.Append(new CssValue(new CssToken(CssTokenType.Semicolon))))
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Semicolon)
            {
                if (Declaration(current) is CssDeclaration d) declarations.Add(d);
                current = [];
                continue;
            }

            current.Add(v);
        }

        return declarations;
    }

    private static CssDeclaration? Declaration(List<CssValue> values)
    {
        int i = 0;
        while (i < values.Count && values[i].IsWhitespace) i++;
        if (i >= values.Count || !values[i].IsToken || values[i].Token.Type != CssTokenType.Ident) return null;
        string name = values[i].Token.Value;
        name = name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();
        i++;
        while (i < values.Count && values[i].IsWhitespace) i++;
        if (i >= values.Count || !values[i].IsToken || values[i].Token.Type != CssTokenType.Colon) return null;
        List<CssValue> value = [.. values.Skip(i + 1)];

        // !important at the end.
        bool important = false;
        int last = value.Count - 1;
        while (last >= 0 && value[last].IsWhitespace) last--;
        if (last >= 1 && value[last].IsIdent("important"))
        {
            int bang = last - 1;
            while (bang >= 0 && value[bang].IsWhitespace) bang--;
            if (bang >= 0 && value[bang].IsToken && value[bang].Token is { Type: CssTokenType.Delim, Value: "!" })
            {
                important = true;
                value = value.Take(bang).ToList();
            }
        }

        while (value.Count > 0 && value[0].IsWhitespace) value.RemoveAt(0);
        while (value.Count > 0 && value[^1].IsWhitespace) value.RemoveAt(value.Count - 1);
        return value.Count == 0 ? null : new CssDeclaration(name, value, important);
    }
}
