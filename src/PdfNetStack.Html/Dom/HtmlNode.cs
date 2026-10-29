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
    public string Data { get; internal set; } = data;

    public override string TextContent => Data;
}

/// <summary>An element with a lowercase name, attributes in source order, and children.</summary>
internal class HtmlElement(string name) : HtmlNode
{
    private readonly List<KeyValuePair<string, string>> _attributes = [];

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

    public string? GetAttribute(string name)
    {
        foreach (KeyValuePair<string, string> a in _attributes)
        {
            if (a.Key == name) return a.Value;
        }

        return null;
    }

    /// <summary>Sets an attribute; the first occurrence wins when parsing, as in browsers.</summary>
    internal void AddAttribute(string name, string value)
    {
        if (GetAttribute(name) is null) _attributes.Add(new(name, value));
    }

    public string Id => GetAttribute("id") ?? "";

    /// <summary>The class names, split on whitespace.</summary>
    public IReadOnlyList<string> ClassList =>
        (GetAttribute("class") ?? "").Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries);

    public void Append(HtmlNode node)
    {
        if (node is HtmlText t && Children.Count > 0 && Children[^1] is HtmlText last)
        {
            last.Data += t.Data;
            return;
        }

        node.Parent = this;
        Children.Add(node);
    }

    public void InsertBefore(HtmlNode node, HtmlNode reference)
    {
        int at = Children.IndexOf(reference);
        node.Parent = this;
        Children.Insert(at < 0 ? Children.Count : at, node);
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
