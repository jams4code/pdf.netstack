using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>A border edge resolved between adjoining cells (CSS 2.1, 17.6.2.1).</summary>
internal sealed record CollapsedEdge(string Style, CssColor Color);

/// <summary>Four sides in CSS px.</summary>
internal readonly record struct Edges(double Top, double Right, double Bottom, double Left)
{
    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;
}

/// <summary>A laid out piece of a box: a rectangle in CSS px on the continuous canvas, y down.</summary>
internal abstract class Fragment
{
    /// <summary>The border box.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public double Bottom => Y + Height;

    /// <summary>Moves the fragment and everything in it.</summary>
    public virtual void Translate(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }
}

/// <summary>A block-level box, a table part or an atomic inline: borders, backgrounds and children.</summary>
internal sealed class BoxFragment(Box box) : Fragment
{
    public Box Box { get; } = box;

    public ComputedStyle Style => Box.Style;

    public Edges Margin { get; set; }

    public Edges Border { get; set; }

    public Edges Padding { get; set; }

    /// <summary>
    /// For cells in the collapsing border model: each side's winning style and color, top, right,
    /// bottom, left; the widths are in <see cref="Border"/>. Null to draw the box's own borders.
    /// </summary>
    public CollapsedEdge[]? CollapsedBorders { get; set; }

    /// <summary>Block children or lines, in order.</summary>
    public List<Fragment> Children { get; } = [];

    /// <summary>The outside list marker, placed at the first line.</summary>
    public LineFragment? Marker { get; set; }

    /// <summary>The baseline of the last line inside, for inline-block alignment; null when there is no line.</summary>
    public double? LastBaseline { get; set; }

    public double? FirstBaseline { get; set; }

    public double ContentX => X + Border.Left + Padding.Left;

    public double ContentY => Y + Border.Top + Padding.Top;

    public double ContentWidth => Width - Border.Horizontal - Padding.Horizontal;

    public double ContentHeight => Height - Border.Vertical - Padding.Vertical;

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        foreach (Fragment c in Children) c.Translate(dx, dy);
        Marker?.Translate(dx, dy);
        if (LastBaseline is double last) LastBaseline = last + dy;
        if (FirstBaseline is double first) FirstBaseline = first + dy;
    }

    public override string ToString() => $"{Box} {X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}";
}

/// <summary>A line box: text, inline box decorations and atomic inlines.</summary>
internal sealed class LineFragment : Fragment
{
    public double Baseline { get; set; }

    public List<Fragment> Items { get; } = [];

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        Baseline += dy;
        foreach (Fragment f in Items) f.Translate(dx, dy);
    }

    public override string ToString() => $"Line {X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}";
}

/// <summary>Shaped glyphs in one font and style, ready to draw at a baseline.</summary>
internal sealed class TextFragment(ComputedStyle style, PdfFont font, double size, List<ShapedGlyph> glyphs, string text) : Fragment
{
    public ComputedStyle Style { get; } = style;

    public PdfFont Font { get; } = font;

    /// <summary>The font size in CSS px.</summary>
    public double Size { get; } = size;

    public List<ShapedGlyph> Glyphs { get; } = glyphs;

    public string Text { get; } = text;

    public double Baseline { get; set; }

    /// <summary>Extra space added after each word space when the line is justified, in px.</summary>
    public double JustifySpace { get; set; }

    /// <summary>The styles whose text-decoration lines draw through this text, innermost first.</summary>
    public IReadOnlyList<ComputedStyle> Decorations { get; init; } = [];

    public override void Translate(double dx, double dy)
    {
        base.Translate(dx, dy);
        Baseline += dy;
    }

    public override string ToString() => $"Text\"{Text}\" {X:0.##},{Baseline:0.##} w{Width:0.##}";
}

/// <summary>The part of an inline box on one line: its background, borders and padding.</summary>
internal sealed class InlineFragment(InlineBox box) : Fragment
{
    public InlineBox Box { get; } = box;

    public Edges Border { get; set; }

    public Edges Padding { get; set; }

    /// <summary>True when the box starts on this line, so its start edge is drawn here.</summary>
    public bool StartEdge { get; set; }

    public bool EndEdge { get; set; }
}
