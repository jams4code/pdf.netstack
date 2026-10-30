namespace PdfNetStack.Html.Layout;

/// <summary>
/// What a laid out tree's subtrees cover, worked out once and shared by the painters of all its
/// pages. Each page's painter otherwise visits every box of the document, most of them on other
/// pages. A subtree is silent on a page when nothing in it reaches the page's band and painting it
/// would write nothing at all: no transform, overflow clip or stacking context in it, which write
/// graphics state even where nothing shows, and no list marker with inline boxes, which paint
/// wherever they are. The tree must not move while the index is in use.
/// </summary>
internal sealed class PaintIndex
{
    private readonly Dictionary<BoxFragment, (double Top, double Bottom, bool Quiet, bool Layered)> _subtrees = [];
    private readonly Dictionary<BoxFragment, List<Painter.Layer>?> _layers = [];

    /// <summary>Indexes every box in <paramref name="root"/>.</summary>
    public PaintIndex(BoxFragment root) => Box(root);

    /// <summary>
    /// True when painting <paramref name="f"/> on a page whose visible band runs from
    /// <paramref name="top"/> to <paramref name="bottom"/> writes nothing: every fragment in it
    /// misses the band and nothing in it writes graphics state.
    /// </summary>
    public bool Silent(BoxFragment f, double top, double bottom) =>
        _subtrees.TryGetValue(f, out var s) && s.Quiet && (s.Bottom < top || s.Top > bottom);

    /// <summary>Whether a box paints in its stacking context's layers rather than in tree order, found once.</summary>
    public bool Layered(BoxFragment f) => _subtrees.TryGetValue(f, out var s) ? s.Layered : Painter.Layered(f);

    /// <summary>The layered boxes of a stacking context, found once.</summary>
    public List<Painter.Layer>? Layers(BoxFragment root)
    {
        if (!_layers.TryGetValue(root, out List<Painter.Layer>? layers)) _layers[root] = layers = Painter.Layers(root);
        return layers;
    }

    // Math.Min and Math.Max carry a NaN through, and a NaN extent is never silent.
    private (double Top, double Bottom, bool Quiet) Box(BoxFragment f)
    {
        double top = f.Y, bottom = f.Bottom;
        bool quiet = !Painter.HasTransform(f.Style) && !Painter.Clips(f) && !Painter.FormsStackingContext(f);
        foreach (Fragment child in f.Children)
        {
            (double t, double b, bool q) = child switch
            {
                BoxFragment box => Box(box),
                LineFragment line => Line(line, marker: false),
                _ => (child.Y, child.Bottom, true),
            };
            (top, bottom, quiet) = (Math.Min(top, t), Math.Max(bottom, b), quiet && q);
        }

        if (f.Marker is LineFragment marker)
        {
            (double t, double b, bool q) = Line(marker, marker: true);
            (top, bottom, quiet) = (Math.Min(top, t), Math.Max(bottom, b), quiet && q);
        }

        _subtrees[f] = (top, bottom, quiet, Painter.Layered(f));
        return (top, bottom, quiet);
    }

    private (double Top, double Bottom, bool Quiet) Line(LineFragment line, bool marker)
    {
        double top = line.Y, bottom = line.Bottom;
        bool quiet = true;
        foreach (Fragment item in line.Items)
        {
            (double t, double b, bool q) = item switch
            {
                BoxFragment box => Box(box),
                InlineFragment => (item.Y, item.Bottom, !marker),
                _ => (item.Y, item.Bottom, true),
            };
            (top, bottom, quiet) = (Math.Min(top, t), Math.Max(bottom, b), quiet && q);
        }

        return (top, bottom, quiet);
    }
}
