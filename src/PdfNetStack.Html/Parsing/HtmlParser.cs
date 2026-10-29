using System.Globalization;
using System.Text;
using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Parsing;

/// <summary>
/// Parses HTML into a document tree the way browsers do for well formed and ordinary broken
/// markup (WHATWG HTML, 13.2): implied html, head and body, void elements, raw text in script and
/// style, character references, and the implied end tags of paragraphs, list items, table rows
/// and cells. Scripting is not run.
/// </summary>
internal sealed class HtmlParser
{
    private const int MaxDepth = 512;

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param", "keygen",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "xmp", "iframe", "noembed", "noframes" };

    private static readonly HashSet<string> Rcdata = new(StringComparer.Ordinal) { "title", "textarea" };

    private static readonly HashSet<string> HeadContent = new(StringComparer.Ordinal) { "title", "meta", "link", "style", "script", "base", "noscript", "template" };

    // Start tags that end an open paragraph.
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main", "menu", "nav", "ol",
        "p", "pre", "section", "summary", "table", "ul", "listing",
    };

    private readonly string _s;
    private readonly HtmlDocument _doc = new();
    private readonly List<HtmlElement> _stack = [];
    private readonly HtmlElement _html = new("html");
    private readonly HtmlElement _head = new("head");
    private readonly HtmlElement _body = new("body");
    private bool _inBody;
    private int _at;

    private HtmlParser(string html)
    {
        _s = html.Replace("\r\n", "\n").Replace('\r', '\n');
        _doc.Append(_html);
        _html.Append(_head);
        _html.Append(_body);
    }

    public static HtmlDocument Parse(string html)
    {
        Compat.ThrowIfNull(html, nameof(html));
        var parser = new HtmlParser(html);
        parser.Run();
        return parser._doc;
    }

    private HtmlElement Current => _stack.Count > 0 ? _stack[^1] : (_inBody ? _body : _head);

    private void Run()
    {
        var text = new StringBuilder();
        while (_at < _s.Length)
        {
            char c = _s[_at];
            if (c == '<' && _at + 1 < _s.Length)
            {
                char n = _s[_at + 1];
                if (char.IsLetter(n) || n == '/' || n == '!' || n == '?')
                {
                    Flush(text);
                    Markup();
                    continue;
                }
            }

            if (c == '&')
            {
                text.Append(CharacterReference(inAttribute: false));
                continue;
            }

            text.Append(c == '\0' ? (char)0xFFFD : c);
            _at++;
        }

        Flush(text);
    }

    private void Flush(StringBuilder text)
    {
        if (text.Length == 0) return;
        string data = text.ToString();
        text.Clear();
        bool whitespace = data.All(IsSpace);

        // Text in a table goes before the table (foster parenting); whitespace there is dropped.
        HtmlElement parent = Current;
        if (parent.Name is "table" or "tbody" or "thead" or "tfoot" or "tr")
        {
            if (whitespace) return;
            HtmlElement table = _stack.LastOrDefault(e => e.Name == "table") ?? parent;
            (table.Parent ?? _body).InsertBefore(new HtmlText(data), table);
            return;
        }

        if (!_inBody && _stack.Count == 0)
        {
            if (whitespace) return;
            _inBody = true;
            parent = _body;
        }

        parent.Append(new HtmlText(data));
    }

    private void Markup()
    {
        if (Starts("<!--"))
        {
            int end = _s.IndexOf("-->", _at + 4, StringComparison.Ordinal);
            _at = end < 0 ? _s.Length : end + 3;
            return;
        }

        if (_s[_at + 1] is '!' or '?')
        {
            int end = _s.IndexOf('>', _at);
            _at = end < 0 ? _s.Length : end + 1;
            return;
        }

        bool endTag = _s[_at + 1] == '/';
        _at += endTag ? 2 : 1;
        int nameStart = _at;
        while (_at < _s.Length && !IsSpace(_s[_at]) && _s[_at] is not ('/' or '>')) _at++;
        string name = _s.Substring(nameStart, _at - nameStart).ToLowerInvariant();
        var attributes = new List<KeyValuePair<string, string>>();
        bool selfClosing = false;
        while (_at < _s.Length && _s[_at] != '>')
        {
            char c = _s[_at];
            if (IsSpace(c))
            {
                _at++;
                continue;
            }

            if (c == '/')
            {
                selfClosing = true;
                _at++;
                continue;
            }

            selfClosing = false;
            int aStart = _at;
            while (_at < _s.Length && !IsSpace(_s[_at]) && _s[_at] is not ('/' or '>' or '=')) _at++;
            string attrName = _s.Substring(aStart, Math.Max(1, _at - aStart)).ToLowerInvariant();
            if (_at == aStart) _at++;
            while (_at < _s.Length && IsSpace(_s[_at])) _at++;
            string value = "";
            if (_at < _s.Length && _s[_at] == '=')
            {
                _at++;
                while (_at < _s.Length && IsSpace(_s[_at])) _at++;
                value = AttributeValue();
            }

            attributes.Add(new(attrName, value));
        }

        _at = Math.Min(_s.Length, _at + 1);
        if (name.Length == 0) return;
        if (endTag) EndTag(name);
        else StartTag(name, attributes, selfClosing);
    }

    private string AttributeValue()
    {
        var sb = new StringBuilder();
        if (_at < _s.Length && _s[_at] is '"' or '\'')
        {
            char quote = _s[_at++];
            while (_at < _s.Length && _s[_at] != quote)
            {
                if (_s[_at] == '&') sb.Append(CharacterReference(inAttribute: true));
                else sb.Append(_s[_at++]);
            }

            _at = Math.Min(_s.Length, _at + 1);
            return sb.ToString();
        }

        while (_at < _s.Length && !IsSpace(_s[_at]) && _s[_at] != '>')
        {
            if (_s[_at] == '&') sb.Append(CharacterReference(inAttribute: true));
            else sb.Append(_s[_at++]);
        }

        return sb.ToString();
    }

    private void StartTag(string name, List<KeyValuePair<string, string>> attributes, bool selfClosing)
    {
        var element = new HtmlElement(name);
        foreach (KeyValuePair<string, string> a in attributes) element.AddAttribute(a.Key, a.Value);

        switch (name)
        {
            case "html":
                foreach (KeyValuePair<string, string> a in attributes) _html.AddAttribute(a.Key, a.Value);
                return;
            case "head":
                return;
            case "body":
                foreach (KeyValuePair<string, string> a in attributes) _body.AddAttribute(a.Key, a.Value);
                _inBody = true;
                _stack.RemoveAll(e => IsHeadElement(e));
                return;
        }

        if (!_inBody && _stack.Count == 0 && !HeadContent.Contains(name)) _inBody = true;

        if (ClosesParagraph.Contains(name)) CloseParagraph();
        switch (name)
        {
            case "li":
                CloseUntilScope("li", ["ul", "ol"]);
                break;
            case "dt" or "dd":
                CloseUntilScope("dt", ["dl"]);
                CloseUntilScope("dd", ["dl"]);
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                if (Current.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") Pop();
                break;
            case "a":
                if (_stack.Any(e => e.Name == "a")) EndTag("a");
                break;
            case "option":
                if (Current.Name == "option") Pop();
                break;
            case "optgroup":
                if (Current.Name == "option") Pop();
                if (Current.Name == "optgroup") Pop();
                break;
            case "tr":
                CloseUntilScope("tr", ["table", "tbody", "thead", "tfoot"]);
                if (Current.Name == "table") Push(new HtmlElement("tbody"));
                break;
            case "td" or "th":
                CloseUntilScope("td", ["tr", "table"]);
                CloseUntilScope("th", ["tr", "table"]);
                if (Current.Name is "table" or "tbody" or "thead" or "tfoot")
                {
                    if (Current.Name == "table") Push(new HtmlElement("tbody"));
                    Push(new HtmlElement("tr"));
                }

                break;
            case "tbody" or "thead" or "tfoot":
                while (Current.Name is "tbody" or "thead" or "tfoot" or "tr" or "td" or "th" && _stack.Any(e => e.Name == "table")) Pop();
                break;
            case "caption" or "colgroup":
                CloseUntilScope("caption", ["table"]);
                break;
        }

        // In SVG and MathML a trailing slash closes any element (HTML, 13.2.6.5): <path/><path/> are siblings.
        if (Void.Contains(name) || (selfClosing && (name is "svg" or "math" || _stack.Any(open => open.Name is "svg" or "math"))))
        {
            Current.Append(element);
            return;
        }

        if (RawText.Contains(name) || Rcdata.Contains(name))
        {
            string close = "</" + name;
            int end = IndexOfIgnoreCase(_s, close, _at);
            string content = _s.Substring(_at, (end < 0 ? _s.Length : end) - _at);
            if (Rcdata.Contains(name)) content = DecodeText(content);
            if (content.Length > 0) element.Append(new HtmlText(content));
            Current.Append(element);
            _at = end < 0 ? _s.Length : Math.Max(_s.IndexOf('>', end) + 1, end + close.Length);
            if (_at <= end) _at = _s.Length;
            return;
        }

        Push(element);
    }

    private void EndTag(string name)
    {
        if (name is "p" or "br" && !_inBody && _stack.Count == 0) _inBody = true;
        switch (name)
        {
            case "br":
                Current.Append(new HtmlElement("br"));
                return;
            case "body" or "html":
                return;
            case "p" when !_stack.Any(e => e.Name == "p"):
                Current.Append(new HtmlElement("p"));
                return;
        }

        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name == name)
            {
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }

            // An end tag does not reach through a table or a cell.
            if (_stack[i].Name is "table" or "td" or "th" && name is not ("table" or "td" or "th" or "tr" or "tbody" or "thead" or "tfoot")) return;
        }
    }

    private void Push(HtmlElement element)
    {
        if (_stack.Count >= MaxDepth) Pop();
        Current.Append(element);
        _stack.Add(element);
    }

    private void Pop()
    {
        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
    }

    private void CloseParagraph()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name == "p")
            {
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }

            if (_stack[i].Name is "button" or "table" or "td" or "th" or "div" or "li" or "blockquote" or "section" or "article") return;
        }
    }

    /// <summary>Closes an open <paramref name="name"/> unless one of the boundary elements is nearer.</summary>
    private void CloseUntilScope(string name, string[] boundary)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name == name)
            {
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }

            if (Array.IndexOf(boundary, _stack[i].Name) >= 0) return;
        }
    }

    private bool IsHeadElement(HtmlElement e) => ReferenceEquals(e.Parent, _head);

    private bool Starts(string prefix) => string.CompareOrdinal(_s, _at, prefix, 0, prefix.Length) == 0;

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private static int IndexOfIgnoreCase(string s, string value, int start) =>
        s.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes character references in RCDATA content.</summary>
    private static string DecodeText(string text)
    {
        if (text.IndexOf('&') < 0) return text;
        var parser = new HtmlParser(text);
        var sb = new StringBuilder();
        while (parser._at < parser._s.Length)
        {
            if (parser._s[parser._at] == '&') sb.Append(parser.CharacterReference(false));
            else sb.Append(parser._s[parser._at++]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A character reference at the cursor (13.2.5.72): numeric, or the longest named reference.
    /// In attributes, a legacy name without its semicolon followed by = or a letter or digit stays literal.
    /// </summary>
    private string CharacterReference(bool inAttribute)
    {
        int start = _at++;
        if (_at < _s.Length && _s[_at] == '#')
        {
            int at = _at + 1;
            bool hex = at < _s.Length && _s[at] is 'x' or 'X';
            if (hex) at++;
            int digits = at;
            long value = 0;
            while (at < _s.Length && (hex ? Uri.IsHexDigit(_s[at]) : char.IsDigit(_s[at])))
            {
                value = Math.Min(value * (hex ? 16 : 10) + Convert.ToInt32(_s[at].ToString(), hex ? 16 : 10), 0x110000);
                at++;
            }

            if (at == digits)
            {
                return "&";
            }

            if (at < _s.Length && _s[at] == ';') at++;
            _at = at;
            return NumericCharacter(value);
        }

        // Longest match among the names starting here.
        int best = -1, bestLength = 0;
        int max = Math.Min(_s.Length - _at, 32);
        for (int length = 1; length <= max; length++)
        {
            int i = Array.BinarySearch(HtmlEntities.Names, _s.Substring(_at, length), StringComparer.Ordinal);
            if (i >= 0)
            {
                best = i;
                bestLength = length;
            }
        }

        if (best < 0) return "&";
        string matched = HtmlEntities.Names[best];
        if (inAttribute && matched[^1] != ';' && _at + bestLength < _s.Length
            && (char.IsLetterOrDigit(_s[_at + bestLength]) || _s[_at + bestLength] == '='))
        {
            return "&";
        }

        _at += bestLength;
        int first = HtmlEntities.CodePoints[2 * best], second = HtmlEntities.CodePoints[2 * best + 1];
        return char.ConvertFromUtf32(first) + (second != 0 ? char.ConvertFromUtf32(second) : "");
    }

    private static readonly int[] Windows1252 =
    [
        0x20AC, 0x81, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x8D, 0x017D, 0x8F,
        0x90, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x9D, 0x017E, 0x0178,
    ];

    private static string NumericCharacter(long value)
    {
        if (value is >= 0x80 and <= 0x9F) value = Windows1252[value - 0x80];
        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) value = 0xFFFD;
        return char.ConvertFromUtf32((int)value);
    }
}
