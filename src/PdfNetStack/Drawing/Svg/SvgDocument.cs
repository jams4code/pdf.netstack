using System.Globalization;
using System.Text;

namespace PdfNetStack.Drawing.Svg;

/// <summary>An element of an SVG document: a lowercase local name, attributes, children and text.</summary>
internal sealed class SvgElement(string name, SvgElement? parent)
{
    public string Name { get; } = name;

    public SvgElement? Parent { get; } = parent;

    /// <summary>Attributes by lowercase local name: SVG embedded in HTML arrives lowercased, files keep their case.</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SvgElement> Children { get; } = [];

    /// <summary>Text directly inside the element, for text, tspan and style.</summary>
    public StringBuilder Text { get; } = new();

    public string? this[string attribute] => Attributes.TryGetValue(attribute, out string? v) ? v : null;
}

/// <summary>
/// Parses SVG as XML (XML 1.0, well-formedness not enforced): elements, attributes, text, CDATA
/// and the predefined and numeric character references. Doctypes and their entities are skipped,
/// so entity expansion cannot be used to blow up memory. Size and depth are capped.
/// </summary>
internal static class SvgParser
{
    private const int MaxElements = 100_000;
    private const int MaxDepth = 256;

    /// <exception cref="FormatException">No svg root element.</exception>
    public static SvgElement Parse(string xml)
    {
        SvgElement? root = null;
        var stack = new Stack<SvgElement>();
        int elements = 0, i = 0;
        while (i < xml.Length)
        {
            int lt = xml.IndexOf('<', i);
            if (lt < 0) break;
            if (stack.Count > 0 && lt > i) Decode(xml, i, lt, stack.Peek().Text);
            i = lt;

            if (Starts(xml, i, "<!--")) i = Skip(xml, i, "-->");
            else if (Starts(xml, i, "<![CDATA["))
            {
                int end = xml.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                int stop = end < 0 ? xml.Length : end;
                if (stack.Count > 0) stack.Peek().Text.Append(xml, i + 9, stop - i - 9);
                i = end < 0 ? xml.Length : end + 3;
            }
            else if (Starts(xml, i, "<!DOCTYPE") || Starts(xml, i, "<!doctype")) i = SkipDoctype(xml, i);
            else if (Starts(xml, i, "<?") || Starts(xml, i, "<!")) i = Skip(xml, i, ">");
            else if (Starts(xml, i, "</"))
            {
                int end = xml.IndexOf('>', i);
                i = end < 0 ? xml.Length : end + 1;
                if (stack.Count > 0) stack.Pop();
            }
            else
            {
                (SvgElement? element, bool selfClosing, int next) = Tag(xml, i, stack.Count > 0 ? stack.Peek() : null);
                i = next;
                if (element is null) continue;
                if (++elements > MaxElements) break;
                if (stack.Count > 0) stack.Peek().Children.Add(element);
                else if (root is null) root = element;
                if (!selfClosing && stack.Count < MaxDepth) stack.Push(element);
            }
        }

        if (root is null || root.Name != "svg") throw new FormatException("The document has no svg root element.");
        return root;
    }

    private static bool Starts(string s, int i, string prefix) => string.CompareOrdinal(s, i, prefix, 0, prefix.Length) == 0;

    private static int Skip(string s, int i, string end)
    {
        int at = s.IndexOf(end, i, StringComparison.Ordinal);
        return at < 0 ? s.Length : at + end.Length;
    }

    // A doctype may hold an internal subset in brackets with '>' inside it.
    private static int SkipDoctype(string s, int i)
    {
        int depth = 0;
        for (int k = i; k < s.Length; k++)
        {
            if (s[k] == '[') depth++;
            else if (s[k] == ']') depth--;
            else if (s[k] == '>' && depth <= 0) return k + 1;
        }

        return s.Length;
    }

    private static string LocalName(string name)
    {
        int colon = name.IndexOf(':');
        return (colon < 0 ? name : name.Substring(colon + 1)).ToLowerInvariant();
    }

    private static (SvgElement? Element, bool SelfClosing, int Next) Tag(string s, int i, SvgElement? parent)
    {
        int k = i + 1;
        while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not ('/' or '>')) k++;
        string name = s.Substring(i + 1, k - i - 1);
        var element = new SvgElement(LocalName(name), parent);
        while (k < s.Length)
        {
            while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
            if (k >= s.Length) break;
            if (s[k] == '>') return (element, false, k + 1);
            if (s[k] == '/' && k + 1 < s.Length && s[k + 1] == '>') return (element, true, k + 2);
            if (s[k] == '/')
            {
                k++;
                continue;
            }

            int start = k;
            while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not ('=' or '>' or '/')) k++;
            string attribute = s.Substring(start, k - start);
            while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
            string value = "";
            if (k < s.Length && s[k] == '=')
            {
                k++;
                while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
                if (k < s.Length && s[k] is '"' or '\'')
                {
                    char quote = s[k++];
                    int end = s.IndexOf(quote, k);
                    if (end < 0) end = s.Length;
                    var sb = new StringBuilder();
                    Decode(s, k, end, sb);
                    value = sb.ToString();
                    k = Math.Min(s.Length, end + 1);
                }
                else
                {
                    int vs = k;
                    while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] != '>') k++;
                    value = s.Substring(vs, k - vs);
                }
            }

            // xlink:href and href are the same; namespaces are otherwise dropped.
            string local = attribute.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase) ? "" : LocalName(attribute);
            if (local.Length > 0 && attribute.Length > 0 && !element.Attributes.ContainsKey(local)) element.Attributes[local] = value;
        }

        return (element, false, s.Length);
    }

    /// <summary>Appends text with the predefined entities and numeric character references decoded; anything else stays as written.</summary>
    private static void Decode(string s, int start, int end, StringBuilder sb)
    {
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c != '&')
            {
                sb.Append(c);
                continue;
            }

            int semi = s.IndexOf(';', i, Math.Min(12, end - i));
            if (semi < 0)
            {
                sb.Append(c);
                continue;
            }

            string entity = s.Substring(i + 1, semi - i - 1);
            string? text = entity switch
            {
                "lt" => "<",
                "gt" => ">",
                "amp" => "&",
                "quot" => "\"",
                "apos" => "'",
                _ => null,
            };
            if (text is null && entity.Length > 1 && entity[0] == '#')
            {
                bool hex = entity[1] is 'x' or 'X';
                if (int.TryParse(hex ? entity.Substring(2) : entity.Substring(1), hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out int cp)
                    && cp is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                {
                    text = char.ConvertFromUtf32(cp);
                }
            }

            if (text is null)
            {
                sb.Append(c);
                continue;
            }

            sb.Append(text);
            i = semi;
        }
    }
}
