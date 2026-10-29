using PdfNetStack.Drawing;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Layout;

/// <summary>A box of the box tree (CSS 2.1, 9.2): what layout positions and painting draws.</summary>
internal abstract class Box
{
    protected Box(ComputedStyle style, HtmlElement? element)
    {
        Style = style;
        Element = element;
    }

    public ComputedStyle Style { get; }

    /// <summary>The element that generated the box; null for anonymous boxes and text.</summary>
    public HtmlElement? Element { get; }

    public Box? Parent { get; internal set; }

    public List<Box> Children { get; } = [];

    /// <summary>True for boxes the builder made to fix up the tree, such as the anonymous block around loose text.</summary>
    public bool IsAnonymous { get; init; }

    public virtual bool IsBlockLevel => false;

    public bool IsInlineLevel => !IsBlockLevel;

    public void Add(Box child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void Insert(int index, Box child)
    {
        child.Parent = this;
        Children.Insert(index, child);
    }

    public void SetChildren(IEnumerable<Box> children)
    {
        List<Box> list = [.. children];
        Children.Clear();
        foreach (Box c in list) Add(c);
    }

    public override string ToString() => GetType().Name.Replace("Box", "") + (Element is null ? "" : "<" + Element.Name + ">");
}

/// <summary>A block-level box that contains blocks or lines: display block, list-item, flow-root.</summary>
internal class BlockBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    public override bool IsBlockLevel => true;

    /// <summary>The list marker of a list item, laid out outside or inside the first line.</summary>
    public Box? Marker { get; set; }
}

/// <summary>An inline-level box whose inside is a block formatting context: display inline-block.</summary>
internal sealed class InlineBlockBox(ComputedStyle style, HtmlElement? element) : BlockBox(style, element)
{
    public override bool IsBlockLevel => false;
}

/// <summary>An inline box: display inline. Split around blocks it contains, its parts keep the element.</summary>
internal sealed class InlineBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    /// <summary>False on a part after a split, so the start edge (border, padding, margin) is drawn once.</summary>
    public bool HasStartEdge { get; set; } = true;

    /// <summary>False on a part before a split.</summary>
    public bool HasEndEdge { get; set; } = true;
}

/// <summary>A run of text with collapsed white space, styled by its parent.</summary>
internal sealed class TextBox(ComputedStyle style, string text) : Box(style, null)
{
    public string Text { get; internal set; } = text;

    public override string ToString() => "Text\"" + Text + "\"";
}

/// <summary>A forced line break: the br element.</summary>
internal sealed class LineBreakBox(ComputedStyle style, HtmlElement? element) : Box(style, element);

/// <summary>A replaced element such as img: an image with intrinsic dimensions.</summary>
internal sealed class ReplacedBox(ComputedStyle style, HtmlElement? element, PdfImage image) : Box(style, element)
{
    public PdfImage Image { get; } = image;

    /// <summary>Intrinsic width in CSS px, from the pixel size and resolution.</summary>
    public double IntrinsicWidth => Image.PixelWidth * 96 / (Image.HorizontalResolution > 0 ? Image.HorizontalResolution : 96);

    public double IntrinsicHeight => Image.PixelHeight * 96 / (Image.VerticalResolution > 0 ? Image.VerticalResolution : 96);

    public override bool IsBlockLevel => Style.Keyword("display") is "block" or "list-item" or "flex" or "grid" or "table" or "flow-root";
}

/// <summary>A flex container (CSS Flexbox 1); its children are the flex items.</summary>
internal sealed class FlexBox(ComputedStyle style, HtmlElement? element) : BlockBox(style, element)
{
    public bool IsInline { get; init; }

    public override bool IsBlockLevel => !IsInline;
}

/// <summary>The table wrapper's table box: row groups, rows, columns and captions inside.</summary>
internal sealed class TableBox(ComputedStyle style, HtmlElement? element) : BlockBox(style, element)
{
    public bool IsInline { get; init; }

    public override bool IsBlockLevel => !IsInline;
}

internal sealed class TableRowGroupBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    /// <summary>table-header-group: repeated at the top of every page the table spans.</summary>
    public bool IsHeader => Style.Keyword("display") == "table-header-group";

    public bool IsFooter => Style.Keyword("display") == "table-footer-group";

    public override bool IsBlockLevel => true;
}

internal sealed class TableRowBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    public override bool IsBlockLevel => true;
}

internal sealed class TableCellBox(ComputedStyle style, HtmlElement? element) : BlockBox(style, element)
{
    public int ColumnSpan { get; init; } = 1;

    public int RowSpan { get; init; } = 1;
}

internal sealed class TableColumnBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    public int Span { get; init; } = 1;

    public override bool IsBlockLevel => true;
}

internal sealed class TableColumnGroupBox(ComputedStyle style, HtmlElement? element) : Box(style, element)
{
    public int Span { get; init; } = 1;

    public override bool IsBlockLevel => true;
}

internal sealed class TableCaptionBox(ComputedStyle style, HtmlElement? element) : BlockBox(style, element);
