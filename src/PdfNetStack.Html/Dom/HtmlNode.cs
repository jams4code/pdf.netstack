using System.Text;

namespace PdfNetStack.Html.Dom;

/// <summary>A node of the parsed document.</summary>
internal abstract class HtmlNode
{
    public HtmlElement? Parent { get; internal set; }

    /// <summary>The text of this node and its descendants.</summary>
    public abstract string TextContent { get; }
}

/// <summary>A run of text.</summary>
internal sealed class HtmlText(string data) : HtmlNode
{
    private string _data = data;

    // Adjacent runs are merged while parsing; a builder keeps that linear.
    private StringBuilder? _pending;

    public string Data
    {
        get
        {
            if (_pending is not null)
            {
                _data = _pending.ToString();
                _pending = null;
            }

            return _data;
        }

        internal set
        {
            _data = value;
            _pending = null;
        }
    }

    public override string TextContent => Data;

    internal void AppendData(string more)
    {
        _pending ??= new StringBuilder(_data);
        _pending.Append(more);
    }
}

/// <summary>An element with a lowercase name, attributes in source order, and children.</summary>
internal class HtmlElement(string name) : HtmlNode
{
    private readonly List<KeyValuePair<string, string>> _attributes = [];
    private readonly Dictionary<string, string> _index = new(StringComparer.Ordinal);
    private string[]? _classes;
    private List<HtmlElement>? _elementChildren;
    private Dictionary<string, int>? _typeCounts;
    private int _elementIndex;
    private int _typeIndex;

    public string Name { get; } = name;

    public List<HtmlNode> Children { get; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Attributes => _attributes;

    public IEnumerable<HtmlElement> ChildElements => Children.OfType<HtmlElement>();

    public override string TextContent
    {
        get
        {
            var sb = new StringBuilder();
            foreach (HtmlNode child in Children) sb.Append(child.TextContent);
            return sb.ToString();
        }
    }

    public string? GetAttribute(string name) => _index.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Sets an attribute; the first occurrence wins when parsing, as in browsers.</summary>
    internal void AddAttribute(string name, string value)
    {
        if (_index.ContainsKey(name)) return;
        _index.Add(name, value);
        _attributes.Add(new(name, value));
        if (name == "class") _classes = null;
    }

    public string Id => GetAttribute("id") ?? "";

    /// <summary>The class names, split on whitespace.</summary>
    public IReadOnlyList<string> ClassList =>
        _classes ??= (GetAttribute("class") ?? "").Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The parent element, or null for the root element.</summary>
    public HtmlElement? ParentElement => Parent is HtmlDocument ? null : Parent;

    /// <summary>Child elements, indexed once for sibling and position queries.</summary>
    internal List<HtmlElement> ElementChildren
    {
        get
        {
            if (_elementChildren is null) IndexChildren();
            return _elementChildren!;
        }
    }

    /// <summary>Position among sibling elements, from 0.</summary>
    internal int ElementIndex
    {
        get
        {
            if (Parent is null) return 0;
            if (Parent._elementChildren is null) Parent.IndexChildren();
            return _elementIndex;
        }
    }

    internal int ElementSiblingCount => Parent?.ElementChildren.Count ?? 1;

    /// <summary>Position among sibling elements with the same name, from 0.</summary>
    internal int TypeIndex
    {
        get
        {
            if (Parent is null) return 0;
            if (Parent._elementChildren is null) Parent.IndexChildren();
            return _typeIndex;
        }
    }

    internal int TypeCount
    {
        get
        {
            if (Parent is null) return 1;
            if (Parent._elementChildren is null) Parent.IndexChildren();
            return Parent._typeCounts![Name];
        }
    }

    internal HtmlElement? PreviousElementSibling => Parent is null || ElementIndex == 0 ? null : Parent.ElementChildren[_elementIndex - 1];

    private void IndexChildren()
    {
        var elements = new List<HtmlElement>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (HtmlNode n in Children)
        {
            if (n is not HtmlElement e) continue;
            e._elementIndex = elements.Count;
            counts.TryGetValue(e.Name, out int count);
            e._typeIndex = count;
            counts[e.Name] = count + 1;
            elements.Add(e);
        }

        _typeCounts = counts;
        _elementChildren = elements;
    }

    public void Append(HtmlNode node)
    {
        if (node is HtmlText t && Children.Count > 0 && Children[^1] is HtmlText last)
        {
            last.AppendData(t.Data);
            return;
        }

        node.Parent = this;
        Children.Add(node);
        _elementChildren = null;
    }

    public void InsertBefore(HtmlNode node, HtmlNode reference)
    {
        // The reference is an open table, so it is at or near the end: search from there.
        int at = Children.LastIndexOf(reference);
        if (at < 0) at = Children.Count;
        if (node is HtmlText t && at > 0 && Children[at - 1] is HtmlText previous)
        {
            previous.AppendData(t.Data);
            return;
        }

        node.Parent = this;
        Children.Insert(at, node);
        _elementChildren = null;
    }

    /// <summary>Descendant elements in document order.</summary>
    public IEnumerable<HtmlElement> Descendants()
    {
        var stack = new Stack<HtmlElement>();
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i] is HtmlElement e) stack.Push(e);
        }

        while (stack.Count > 0)
        {
            HtmlElement e = stack.Pop();
            yield return e;
            for (int i = e.Children.Count - 1; i >= 0; i--)
            {
                if (e.Children[i] is HtmlElement c) stack.Push(c);
            }
        }
    }

    public override string ToString() => "<" + Name + ">";
}

/// <summary>The document: an html element with head and body.</summary>
internal sealed class HtmlDocument : HtmlElement
{
    public HtmlDocument()
        : base("#document")
    {
    }

    public HtmlElement Html => ChildElements.First(e => e.Name == "html");

    public HtmlElement Head => Html.ChildElements.First(e => e.Name == "head");

    public HtmlElement Body => Html.ChildElements.First(e => e.Name == "body");

    /// <summary>The document title, from the title element.</summary>
    public string? Title => Head.ChildElements.FirstOrDefault(e => e.Name == "title")?.TextContent.Trim();
}
