using System.Globalization;
using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Css;

/// <summary>Selector specificity (Selectors Level 4, 17): ids, then classes, attributes and pseudo-classes, then types.</summary>
internal readonly record struct CssSpecificity(int A, int B, int C) : IComparable<CssSpecificity>
{
    public static CssSpecificity operator +(CssSpecificity x, CssSpecificity y) => new(x.A + y.A, x.B + y.B, x.C + y.C);

    public static bool operator <(CssSpecificity x, CssSpecificity y) => x.CompareTo(y) < 0;

    public static bool operator >(CssSpecificity x, CssSpecificity y) => x.CompareTo(y) > 0;

    public static bool operator <=(CssSpecificity x, CssSpecificity y) => x.CompareTo(y) <= 0;

    public static bool operator >=(CssSpecificity x, CssSpecificity y) => x.CompareTo(y) >= 0;

    public int CompareTo(CssSpecificity other) =>
        A != other.A ? A.CompareTo(other.A) : B != other.B ? B.CompareTo(other.B) : C.CompareTo(other.C);

    public static CssSpecificity Max(IEnumerable<CssSelector> selectors) =>
        selectors.Select(s => s.Specificity).DefaultIfEmpty().Max();
}

internal enum CssCombinator
{
    None,
    Descendant,
    Child,
    NextSibling,
    SubsequentSibling,
}

/// <summary>A compound selector: an optional type and conditions that all hold for one element.</summary>
internal sealed class CssCompound
{
    /// <summary>The lowercase element name, or null for any element.</summary>
    public string? Tag { get; set; }

    public List<CssCondition> Conditions { get; } = [];

    /// <summary>How this compound relates to the next one to its left.</summary>
    public CssCombinator Combinator { get; set; }

    public CssSpecificity Specificity =>
        Conditions.Aggregate(new CssSpecificity(0, 0, Tag is null ? 0 : 1), (s, c) => s + c.Specificity);

    public bool Matches(HtmlElement e)
    {
        if (Tag is not null && Tag != e.Name) return false;
        foreach (CssCondition c in Conditions)
        {
            if (!c.Matches(e)) return false;
        }

        return true;
    }
}

/// <summary>A complex selector such as <c>article &gt; p.note::before</c>, parsed and ready to match.</summary>
internal sealed class CssSelector
{
    // Bounds that keep matching recursion shallow on hostile style sheets.
    private const int MaxCompounds = 128;
    private const int MaxNesting = 8;

    private static readonly HashSet<string> PseudoElements = new(StringComparer.Ordinal)
    {
        "before", "after", "marker", "first-line", "first-letter", "placeholder", "selection",
    };

    private CssSelector(CssCompound[] compounds, string? pseudoElement, string text)
    {
        Compounds = compounds;
        PseudoElement = pseudoElement;
        Text = text;
        Specificity = compounds.Aggregate(new CssSpecificity(0, 0, pseudoElement is null ? 0 : 1), (s, c) => s + c.Specificity);
    }

    private enum Result
    {
        Matched,
        RestartFromLaterSibling,
        RestartFromDescendant,
        NotMatchedGlobally,
    }

    /// <summary>Compounds from right to left: the first is the subject.</summary>
    public CssCompound[] Compounds { get; }

    /// <summary>The pseudo-element, such as <c>before</c>, or null when the selector targets the element itself.</summary>
    public string? PseudoElement { get; }

    public CssSpecificity Specificity { get; }

    public string Text { get; }

    public override string ToString() => Text;

    /// <summary>Whether the element matches, ignoring any pseudo-element.</summary>
    public bool Matches(HtmlElement element) => Match(0, element) == Result.Matched;

    /// <summary>Parses a selector list as text, for tests and the API.</summary>
    public static List<CssSelector>? Parse(string text) =>
        ParseList(CssParser.ComponentValues(CssTokenizer.Tokenize(text)));

    /// <summary>Parses a selector list; one invalid selector makes the whole list invalid, as for style rules.</summary>
    public static List<CssSelector>? ParseList(List<CssValue> values) => ParseList(values, 0, forgiving: false);

    // Matching from right to left. On failure the result says where a retry could still succeed, so
    // a selector like "a b c d" against a deep tree does not retry every combination of ancestors.
    private Result Match(int index, HtmlElement element)
    {
        CssCompound compound = Compounds[index];
        if (!compound.Matches(element)) return Result.RestartFromLaterSibling;
        if (index == Compounds.Length - 1) return Result.Matched;

        CssCombinator combinator = compound.Combinator;
        bool sibling = combinator is CssCombinator.NextSibling or CssCombinator.SubsequentSibling;
        HtmlElement? next = element;
        while (true)
        {
            next = sibling ? next.PreviousElementSibling : next.ParentElement;
            if (next is null) return sibling ? Result.RestartFromDescendant : Result.NotMatchedGlobally;
            Result result = Match(index + 1, next);
            if (result is Result.Matched or Result.NotMatchedGlobally || combinator == CssCombinator.NextSibling) return result;
            if (combinator == CssCombinator.Child) return Result.RestartFromDescendant;
            if (result == Result.RestartFromDescendant && combinator == CssCombinator.SubsequentSibling) return result;
        }
    }

    private static List<CssSelector>? ParseList(List<CssValue> values, int depth, bool forgiving)
    {
        var selectors = new List<CssSelector>();
        foreach (List<CssValue> part in Split(values))
        {
            CssSelector? selector = ParseComplex(part, depth);
            if (selector is not null) selectors.Add(selector);
            else if (!forgiving) return null;
        }

        return selectors.Count > 0 || forgiving ? selectors : null;
    }

    private static IEnumerable<List<CssValue>> Split(List<CssValue> values)
    {
        var part = new List<CssValue>();
        foreach (CssValue v in values)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma)
            {
                yield return part;
                part = [];
            }
            else
            {
                part.Add(v);
            }
        }

        yield return part;
    }

    private static CssSelector? ParseComplex(List<CssValue> part, int depth)
    {
        var compounds = new List<CssCompound>();
        var combinators = new List<CssCombinator>();
        string? pseudoElement = null;
        int i = 0;
        SkipWhitespace(part, ref i);
        if (i >= part.Count) return null;
        while (true)
        {
            CssCompound? compound = ParseCompound(part, ref i, depth, ref pseudoElement);
            if (compound is null || compounds.Count >= MaxCompounds) return null;
            compounds.Add(compound);
            bool space = SkipWhitespace(part, ref i);
            if (i >= part.Count) break;
            if (pseudoElement is not null) return null;

            CssCombinator combinator = CssCombinator.Descendant;
            if (part[i].IsToken && part[i].Token.Type == CssTokenType.Delim)
            {
                combinator = part[i].Token.Value switch
                {
                    ">" => CssCombinator.Child,
                    "+" => CssCombinator.NextSibling,
                    "~" => CssCombinator.SubsequentSibling,
                    _ => CssCombinator.None,
                };
                if (combinator == CssCombinator.None)
                {
                    if (!space) return null;
                    combinator = CssCombinator.Descendant;
                }
                else
                {
                    i++;
                    SkipWhitespace(part, ref i);
                    if (i >= part.Count) return null;
                }
            }
            else if (!space)
            {
                return null;
            }

            combinators.Add(combinator);
        }

        // Store right to left, each compound holding the combinator to its left neighbour.
        compounds.Reverse();
        for (int k = 0; k < compounds.Count - 1; k++) compounds[k].Combinator = combinators[combinators.Count - 1 - k];
        return new CssSelector([.. compounds], pseudoElement, string.Concat(part.Select(v => v.ToString())).Trim());
    }

    private static CssCompound? ParseCompound(List<CssValue> part, ref int i, int depth, ref string? pseudoElement)
    {
        var compound = new CssCompound();
        bool any = false;
        if (i < part.Count && part[i].IsToken && part[i].Token.Type == CssTokenType.Ident)
        {
            compound.Tag = part[i++].Token.Value.ToLowerInvariant();
            any = true;
        }
        else if (IsDelim(part, i, "*"))
        {
            i++;
            any = true;
        }

        // A namespace prefix (ns|name, *|name, |name): HTML has one namespace, so it is skipped.
        if (IsDelim(part, i, "|"))
        {
            i++;
            if (i < part.Count && part[i].IsToken && part[i].Token.Type == CssTokenType.Ident) compound.Tag = part[i++].Token.Value.ToLowerInvariant();
            else if (IsDelim(part, i, "*")) (compound.Tag, i) = (null, i + 1);
            else return null;
            any = true;
        }

        while (i < part.Count && pseudoElement is null)
        {
            CssValue v = part[i];
            if (v.IsToken && v.Token.Type == CssTokenType.Hash)
            {
                if (!v.Token.IsId) return null;
                compound.Conditions.Add(new IdCondition(v.Token.Value));
                i++;
            }
            else if (IsDelim(part, i, "."))
            {
                i++;
                if (i >= part.Count || !part[i].IsToken || part[i].Token.Type != CssTokenType.Ident) return null;
                compound.Conditions.Add(new ClassCondition(part[i++].Token.Value));
            }
            else if (v.Block == CssTokenType.LeftBracket)
            {
                AttributeCondition? attribute = AttributeCondition.Parse(v.Children);
                if (attribute is null) return null;
                compound.Conditions.Add(attribute);
                i++;
            }
            else if (v.IsToken && v.Token.Type == CssTokenType.Colon)
            {
                i++;
                bool element = i < part.Count && part[i].IsToken && part[i].Token.Type == CssTokenType.Colon;
                if (element) i++;
                if (i >= part.Count) return null;
                CssValue p = part[i++];
                if (p.IsToken && p.Token.Type == CssTokenType.Ident)
                {
                    string name = p.Token.Value.ToLowerInvariant();
                    // CSS 2 allowed one colon for these four.
                    if (element || name is "before" or "after" or "first-line" or "first-letter")
                    {
                        if (!PseudoElements.Contains(name)) return null;
                        pseudoElement = name;
                    }
                    else
                    {
                        CssCondition? condition = PseudoCondition.Create(name);
                        if (condition is null) return null;
                        compound.Conditions.Add(condition);
                    }
                }
                else if (p.Function is string function && !element)
                {
                    CssCondition? condition = Functional(function, p.Children, depth);
                    if (condition is null) return null;
                    compound.Conditions.Add(condition);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                break;
            }

            any = true;
        }

        return any ? compound : null;
    }

    private static CssCondition? Functional(string name, List<CssValue> args, int depth)
    {
        switch (name)
        {
            case "not" or "is" or "where" or "matches" or "-webkit-any":
                if (depth >= MaxNesting) return null;
                List<CssSelector>? list = ParseList(args, depth + 1, forgiving: name != "not");
                return list is null ? null : new LogicalCondition(name, list);
            case "nth-child" or "nth-last-child" or "nth-of-type" or "nth-last-of-type":
                return NthCondition.Parse(name, args, depth >= MaxNesting ? null : a => ParseList(a, depth + 1, forgiving: false));
            case "lang":
                var ranges = args
                    .Where(a => a.IsToken && a.Token.Type is CssTokenType.Ident or CssTokenType.String)
                    .Select(a => a.Token.Value)
                    .ToList();
                return ranges.Count == 0 ? null : new LangCondition(ranges);
            case "dir":
                CssValue? dir = args.FirstOrDefault(a => !a.IsWhitespace);
                return dir is not null && (dir.IsIdent("ltr") || dir.IsIdent("rtl")) ? new DirCondition(dir.Token.Value.ToLowerInvariant()) : null;
            default:
                return null;
        }
    }

    internal static bool SkipWhitespace(List<CssValue> values, ref int i)
    {
        int start = i;
        while (i < values.Count && values[i].IsWhitespace) i++;
        return i > start;
    }

    private static bool IsDelim(List<CssValue> values, int i, string delim) =>
        i < values.Count && values[i].IsToken && values[i].Token.Type == CssTokenType.Delim && values[i].Token.Value == delim;
}

/// <summary>One test inside a compound selector.</summary>
internal abstract class CssCondition
{
    public virtual CssSpecificity Specificity => new(0, 1, 0);

    public abstract bool Matches(HtmlElement e);
}

internal sealed class IdCondition(string id) : CssCondition
{
    public override CssSpecificity Specificity => new(1, 0, 0);

    public override bool Matches(HtmlElement e) => e.GetAttribute("id") == id;
}

internal sealed class ClassCondition(string name) : CssCondition
{
    public override bool Matches(HtmlElement e)
    {
        foreach (string c in e.ClassList)
        {
            if (c == name) return true;
        }

        return false;
    }
}

internal sealed class AttributeCondition(string name, string op, string value, bool ignoreCase) : CssCondition
{
    // HTML attributes whose values compare without case (HTML, 4.16.2 "Case-sensitivity of selectors").
    private static readonly HashSet<string> CaseInsensitive = new(StringComparer.Ordinal)
    {
        "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset", "checked", "clear", "codetype",
        "color", "compact", "declare", "defer", "dir", "direction", "disabled", "enctype", "face", "frame",
        "hreflang", "http-equiv", "lang", "language", "link", "media", "method", "multiple", "nohref", "noresize",
        "noshade", "nowrap", "readonly", "rel", "rev", "rules", "scope", "scrolling", "selected", "shape",
        "target", "text", "type", "valign", "valuetype", "vlink",
    };

    public static AttributeCondition? Parse(List<CssValue> children)
    {
        int i = 0;
        CssSelector.SkipWhitespace(children, ref i);
        if (i >= children.Count || !children[i].IsToken) return null;

        // Optional namespace prefix.
        if (Delim(children, i) == "|" || (Delim(children, i) == "*" && Delim(children, i + 1) == "|")) i += Delim(children, i) == "|" ? 1 : 2;
        if (i >= children.Count || !children[i].IsToken || children[i].Token.Type != CssTokenType.Ident) return null;
        string name = children[i++].Token.Value.ToLowerInvariant();
        if (Delim(children, i) == "|" && i + 1 < children.Count && children[i + 1].IsToken && children[i + 1].Token.Type == CssTokenType.Ident)
        {
            name = children[i + 1].Token.Value.ToLowerInvariant();
            i += 2;
        }

        CssSelector.SkipWhitespace(children, ref i);
        if (i >= children.Count) return new AttributeCondition(name, "", "", false);

        string op;
        if (Delim(children, i) == "=")
        {
            op = "=";
            i++;
        }
        else if (Delim(children, i) is "~" or "|" or "^" or "$" or "*" && Delim(children, i + 1) == "=")
        {
            op = Delim(children, i) + "=";
            i += 2;
        }
        else
        {
            return null;
        }

        CssSelector.SkipWhitespace(children, ref i);
        if (i >= children.Count || !children[i].IsToken || children[i].Token.Type is not (CssTokenType.Ident or CssTokenType.String)) return null;
        string value = children[i++].Token.Value;
        CssSelector.SkipWhitespace(children, ref i);
        bool ignoreCase = CaseInsensitive.Contains(name);
        if (i < children.Count && children[i].IsToken && children[i].Token.Type == CssTokenType.Ident)
        {
            string flag = children[i++].Token.Value.ToLowerInvariant();
            if (flag is not ("i" or "s")) return null;
            ignoreCase = flag == "i";
            CssSelector.SkipWhitespace(children, ref i);
        }

        return i == children.Count ? new AttributeCondition(name, op, value, ignoreCase) : null;
    }

    public override bool Matches(HtmlElement e)
    {
        string? actual = e.GetAttribute(name);
        if (actual is null) return false;
        StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return op switch
        {
            "" => true,
            "=" => string.Equals(actual, value, cmp),
            "~=" => value.Length > 0 && !value.Any(IsSpace) && actual.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries).Any(w => string.Equals(w, value, cmp)),
            "|=" => string.Equals(actual, value, cmp) || actual.StartsWith(value + "-", cmp),
            "^=" => value.Length > 0 && actual.StartsWith(value, cmp),
            "$=" => value.Length > 0 && actual.EndsWith(value, cmp),
#pragma warning disable CA2249 // .NET Standard 2.0 has no Contains overload with a comparison.
            "*=" => value.Length > 0 && actual.IndexOf(value, cmp) >= 0,
#pragma warning restore CA2249
            _ => false,
        };
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static string? Delim(List<CssValue> values, int i) =>
        i < values.Count && values[i].IsToken && values[i].Token.Type == CssTokenType.Delim ? values[i].Token.Value : null;
}

/// <summary>Pseudo-classes without arguments.</summary>
internal sealed class PseudoCondition : CssCondition
{
    // Interactive states never hold in a printed document, but the selectors stay valid.
    private static readonly HashSet<string> Dynamic = new(StringComparer.Ordinal)
    {
        "hover", "active", "focus", "focus-within", "focus-visible", "visited", "target", "target-within",
        "playing", "paused", "autofill", "user-invalid", "user-valid", "placeholder-shown", "indeterminate",
    };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "root", "empty", "first-child", "last-child", "only-child", "first-of-type", "last-of-type",
        "only-of-type", "link", "any-link", "checked", "disabled", "enabled", "required", "optional",
        "read-only", "read-write", "defined",
    };

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "button", "input", "select", "textarea", "optgroup", "option", "fieldset",
    };

    private readonly string _name;

    private PseudoCondition(string name) => _name = name;

    public static PseudoCondition? Create(string name) =>
        Known.Contains(name) || Dynamic.Contains(name) ? new PseudoCondition(name) : null;

    public override bool Matches(HtmlElement e) => _name switch
    {
        "root" => e.ParentElement is null,
        "empty" => e.Children.All(c => c is HtmlText t && t.Data.Length == 0),
        "first-child" => e.ElementIndex == 0,
        "last-child" => e.ElementIndex == e.ElementSiblingCount - 1,
        "only-child" => e.ElementSiblingCount == 1,
        "first-of-type" => e.TypeIndex == 0,
        "last-of-type" => e.TypeIndex == e.TypeCount - 1,
        "only-of-type" => e.TypeCount == 1,
        "link" or "any-link" => e.Name is "a" or "area" && e.GetAttribute("href") is not null,
        "checked" => (e.Name == "input" && e.GetAttribute("checked") is not null) || (e.Name == "option" && e.GetAttribute("selected") is not null),
        "disabled" => FormControls.Contains(e.Name) && e.GetAttribute("disabled") is not null,
        "enabled" => FormControls.Contains(e.Name) && e.GetAttribute("disabled") is null,
        "required" => e.Name is "input" or "select" or "textarea" && e.GetAttribute("required") is not null,
        "optional" => e.Name is "input" or "select" or "textarea" && e.GetAttribute("required") is null,
        "read-only" => !(e.Name is "input" or "textarea" && e.GetAttribute("readonly") is null && e.GetAttribute("disabled") is null),
        "read-write" => e.Name is "input" or "textarea" && e.GetAttribute("readonly") is null && e.GetAttribute("disabled") is null,
        "defined" => true,
        _ => false,
    };
}

/// <summary>:not(), :is(), :where().</summary>
internal sealed class LogicalCondition(string name, List<CssSelector> selectors) : CssCondition
{
    public override CssSpecificity Specificity => name == "where" ? default : CssSpecificity.Max(selectors);

    public override bool Matches(HtmlElement e)
    {
        bool any = false;
        foreach (CssSelector s in selectors)
        {
            if (s.PseudoElement is null && s.Matches(e))
            {
                any = true;
                break;
            }
        }

        return name == "not" ? !any : any;
    }
}

/// <summary>The :nth-* family: an+b, counted from the start or end, among all siblings or one type.</summary>
internal sealed class NthCondition(string name, int a, int b, List<CssSelector>? of) : CssCondition
{
    public override CssSpecificity Specificity => new CssSpecificity(0, 1, 0) + (of is null ? default : CssSpecificity.Max(of));

    public static NthCondition? Parse(string name, List<CssValue> args, Func<List<CssValue>, List<CssSelector>?>? parseOf)
    {
        // Split off "of S" (nth-child and nth-last-child only).
        int ofAt = args.FindIndex(v => v.IsIdent("of"));
        List<CssValue> formula = ofAt < 0 ? args : args.Take(ofAt).ToList();
        List<CssSelector>? of = null;
        if (ofAt >= 0)
        {
            if (name is not ("nth-child" or "nth-last-child") || parseOf is null) return null;
            of = parseOf(args.Skip(ofAt + 1).ToList());
            if (of is null) return null;
        }

        return TryParseFormula(formula, out int a, out int b) ? new NthCondition(name, a, b, of) : null;
    }

    /// <summary>Parses the An+B microsyntax (CSS Syntax Level 3, 6) from its tokens.</summary>
    internal static bool TryParseFormula(List<CssValue> values, out int a, out int b)
    {
        a = b = 0;

        // Rebuild compact text. A number directly after the n part is a signed offset, so its sign is restored.
        var text = new System.Text.StringBuilder();
        CssToken? previous = null;
        foreach (CssValue v in values)
        {
            if (v.IsWhitespace) continue;
            if (!v.IsToken) return false;
            CssToken t = v.Token;
            switch (t.Type)
            {
                case CssTokenType.Number when t.IsInteger:
                    bool signed = previous is { Type: CssTokenType.Ident or CssTokenType.Dimension } && text[text.Length - 1] is not ('-' or '+');
                    text.Append(signed && t.Number >= 0 ? "+" : "").Append(t.Number.ToString(CultureInfo.InvariantCulture));
                    break;
                case CssTokenType.Dimension when t.IsInteger:
                    text.Append(t.Number.ToString(CultureInfo.InvariantCulture)).Append(t.Unit);
                    break;
                case CssTokenType.Ident:
                    text.Append(t.Value.ToLowerInvariant());
                    break;
                case CssTokenType.Delim when t.Value is "+" or "-":
                    text.Append(t.Value);
                    break;
                default:
                    return false;
            }

            previous = t;
        }

        string s = text.ToString();
        if (s == "odd") (a, b) = (2, 1);
        else if (s == "even") (a, b) = (2, 0);
        else
        {
            int n = s.IndexOf('n');
            if (n < 0) return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b);
            string head = s.Substring(0, n), tail = s.Substring(n + 1);
            if (head is "" or "+") a = 1;
            else if (head == "-") a = -1;
            else if (!int.TryParse(head, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out a)) return false;
            if (tail.Length > 0 && (tail[0] is not ('+' or '-') || !int.TryParse(tail, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b))) return false;
        }

        return true;
    }

    public override bool Matches(HtmlElement e)
    {
        int position;
        if (of is not null)
        {
            if (!of.Any(s => s.Matches(e))) return false;
            List<HtmlElement> siblings = e.Parent?.ElementChildren ?? [e];
            int index = e.ElementIndex;
            position = 1;
            if (name == "nth-child")
            {
                for (int k = 0; k < index; k++) position += of.Any(s => s.Matches(siblings[k])) ? 1 : 0;
            }
            else
            {
                for (int k = index + 1; k < siblings.Count; k++) position += of.Any(s => s.Matches(siblings[k])) ? 1 : 0;
            }
        }
        else
        {
            position = name switch
            {
                "nth-child" => e.ElementIndex + 1,
                "nth-last-child" => e.ElementSiblingCount - e.ElementIndex,
                "nth-of-type" => e.TypeIndex + 1,
                _ => e.TypeCount - e.TypeIndex,
            };
        }

        // Some n >= 0 with a*n + b == position.
        long diff = (long)position - b;
        return a == 0 ? diff == 0 : diff / a >= 0 && diff % a == 0;
    }
}

/// <summary>:lang(): the nearest lang attribute, matched by prefix as in BCP 47 extended filtering.</summary>
internal sealed class LangCondition(List<string> ranges) : CssCondition
{
    public override bool Matches(HtmlElement e)
    {
        string? lang = null;
        for (HtmlElement? x = e; x is not null && lang is null; x = x.ParentElement) lang = x.GetAttribute("lang");
        if (string.IsNullOrEmpty(lang)) return false;
        return ranges.Any(r => r == "*" || string.Equals(lang, r, StringComparison.OrdinalIgnoreCase) || lang!.StartsWith(r + "-", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>:dir(): the nearest dir attribute, left to right by default.</summary>
internal sealed class DirCondition(string direction) : CssCondition
{
    public override bool Matches(HtmlElement e)
    {
        for (HtmlElement? x = e; x is not null; x = x.ParentElement)
        {
            string? dir = x.GetAttribute("dir")?.ToLowerInvariant();
            if (dir is "ltr" or "rtl") return dir == direction;
        }

        return direction == "ltr";
    }
}
