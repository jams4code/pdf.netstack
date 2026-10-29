using PdfNetStack.Content;
using PdfNetStack.Documents;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Reads the letters a page draws, in content order: each glyph's text, box and baseline from the
/// text state and the font (ISO 32000-1, 9.4.4), with spans marked with /ActualText (14.9.4)
/// replaced by their text.
/// </summary>
internal sealed class TextExtractor(PdfDocument document) : ContentInterpreter(document)
{
    /// <summary>Letters read per page; text beyond them is ignored.</summary>
    public const int MaxLetters = 500_000;

    /// <summary>Distinct fonts loaded per page; text in further fonts is read with default metrics.</summary>
    private const int MaxFonts = 4096;

    private readonly Dictionary<PdfDictionary, TextFont> _fonts = [];
    private readonly Dictionary<string, TextFont> _missing = new(StringComparer.Ordinal);
    private readonly FontBudget _budget = new();

    // Open marked content sequences: the replacement text of an ActualText span, and the first letter in it.
    private readonly List<(string? ActualText, int Start)> _marked = [];
    private int _actualTextDepth;

    public List<PdfLetter> Letters { get; } = [];

    protected override void OnShowText(List<PdfObject> items, PdfDictionary? resources)
    {
        TextFont font = Font(resources, Text.FontName);
        foreach (PdfObject item in items)
        {
            if (item is PdfNumber number)
            {
                // A TJ adjustment, in thousandths of the font size, moves against the writing direction (9.4.3).
                double adjust = number.ToDouble();
                if (double.IsNaN(adjust) || double.IsInfinity(adjust)) continue;
                double shift = -adjust / 1000 * Text.FontSize;
                if (font.IsVertical) AdvanceText(0, shift);
                else AdvanceText(shift * Text.HorizontalScaling, 0);
                continue;
            }

            if (item is not PdfString s) continue;
            ReadOnlySpan<byte> bytes = s.Bytes;
            for (int at = 0; at < bytes.Length;)
            {
                int length = font.NextCode(bytes, at, out uint code);
                at += length;
                Show(font, code, length);
            }
        }
    }

    protected override void OnBeginMarkedContent(string tag, PdfDictionary? properties)
    {
        // An ActualText span inside another is part of the outer replacement.
        string? actualText = null;
        if (_actualTextDepth == 0 && properties is not null && Document.Resolve(properties["ActualText"]) is PdfString s)
        {
            actualText = PdfText.Decode(s.Value);
        }

        if (actualText is not null) _actualTextDepth++;
        _marked.Add((actualText, Letters.Count));
    }

    protected override void OnEndMarkedContent()
    {
        if (_marked.Count == 0) return;
        (string? actualText, int start) = _marked[^1];
        _marked.RemoveAt(_marked.Count - 1);
        if (actualText is null) return;
        _actualTextDepth--;
        Replace(start, actualText);
    }

    /// <summary>One glyph: a letter where the text rendering matrix puts it, then the text matrix moves by its displacement.</summary>
    private void Show(TextFont font, uint code, int length)
    {
        TextState t = Text;
        double size = t.FontSize;
        PdfMatrix m = PdfMatrix.Multiply(TextMatrix, Ctm);

        // Trm = [Tfs Th, 0, 0, Tfs, 0, Trise] x Tm x CTM (9.4.4).
        PdfMatrix trm = PdfMatrix.Multiply(new PdfMatrix(size * t.HorizontalScaling, 0, 0, size, 0, t.Rise), m);

        // Word spacing applies to the single byte code 32, in simple and composite fonts alike (9.3.3).
        double spacing = t.CharacterSpacing + (length == 1 && code == 32 ? t.WordSpacing : 0);
        double w0 = font.Width(code) / 1000;
        string text = font.Unicode(code, length) ?? "\uFFFD";
        double em = size * Math.Sqrt((m.C * m.C) + (m.D * m.D));
        if (!font.IsVertical)
        {
            if (Letters.Count < MaxLetters)
            {
                Add(text, trm, (0, w0), (font.Descent / 1000, font.Ascent / 1000), (0, 0), (w0, 0), font, em, t.RenderMode);
            }

            AdvanceText(((w0 * size) + spacing) * t.HorizontalScaling, 0);
        }
        else
        {
            // Vertical writing (9.7.4.3): the glyph hangs from its vertical origin, the position
            // vector away from the horizontal one, and the advance w1 moves down.
            (double w1, double vx, _) = font.VerticalMetrics(code);
            w1 /= 1000;
            vx /= 1000;
            if (Letters.Count < MaxLetters)
            {
                Add(text, trm, (-vx, -vx + w0), (w1, 0), (0, 0), (0, w1), font, em, t.RenderMode);
            }

            AdvanceText(0, (w1 * size) + spacing);
        }
    }

    private void Add(string text, PdfMatrix trm, (double Left, double Right) x, (double Bottom, double Top) y, (double X, double Y) start, (double X, double Y) end, TextFont font, double size, int mode)
    {
        PdfPoint a = trm.Transform(new(x.Left, y.Bottom)), b = trm.Transform(new(x.Right, y.Bottom));
        PdfPoint c = trm.Transform(new(x.Right, y.Top)), d = trm.Transform(new(x.Left, y.Top));
        var bounds = new PdfRectangle(Min(a.X, b.X, c.X, d.X), Min(a.Y, b.Y, c.Y, d.Y), Max(a.X, b.X, c.X, d.X), Max(a.Y, b.Y, c.Y, d.Y));
        PdfPoint from = trm.Transform(new(start.X, start.Y)), to = trm.Transform(new(end.X, end.Y));

        // The writing direction: the text space x axis, or y pointing down for vertical writing.
        double dx = font.IsVertical ? -trm.C : trm.A, dy = font.IsVertical ? -trm.D : trm.B;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        PdfPoint direction = length > 1e-12 ? new(dx / length, dy / length) : new(1, 0);
        if (!Finite(bounds.Left) || !Finite(bounds.Bottom) || !Finite(bounds.Right) || !Finite(bounds.Top) || !Finite(size) || !Finite(direction.X) || !Finite(direction.Y)) return;
        Letters.Add(new PdfLetter(text, bounds, from, to, direction, font.Name, size, mode));
    }

    /// <summary>Replaces the letters of an ActualText span by one letter holding its text, or drops them for empty text.</summary>
    private void Replace(int start, string text)
    {
        int count = Letters.Count - start;
        if (count <= 0) return;
        if (text.Length == 0)
        {
            Letters.RemoveRange(start, count);
            return;
        }

        PdfLetter first = Letters[start];
        PdfPoint d = first.Direction;
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        double min = double.MaxValue, max = double.MinValue;
        PdfPoint from = first.BaselineStart, to = first.BaselineEnd;
        for (int i = start; i < Letters.Count; i++)
        {
            PdfLetter l = Letters[i];
            left = Math.Min(left, l.Bounds.Left);
            bottom = Math.Min(bottom, l.Bounds.Bottom);
            right = Math.Max(right, l.Bounds.Right);
            top = Math.Max(top, l.Bounds.Top);

            // The baseline runs from the first point to the last along the writing direction, whatever order the glyphs came in.
            foreach (PdfPoint p in new[] { l.BaselineStart, l.BaselineEnd })
            {
                double u = (p.X * d.X) + (p.Y * d.Y);
                if (u < min) (min, from) = (u, p);
                if (u > max) (max, to) = (u, p);
            }
        }

        Letters.RemoveRange(start, count);
        Letters.Add(new PdfLetter(text, new PdfRectangle(left, bottom, right, top), from, to, d, first.FontName, first.FontSize, first.RenderMode));
    }

    private TextFont Font(PdfDictionary? resources, string? name)
    {
        name ??= "";
        if (Document.Resolve(resources?["Font"]) is PdfDictionary fonts && Document.Resolve(fonts[name]) is PdfDictionary dict)
        {
            if (_fonts.TryGetValue(dict, out TextFont? font)) return font;
            if (_fonts.Count < MaxFonts)
            {
                font = TextFont.Load(Document, dict, name, _budget);
                _fonts[dict] = font;
                return font;
            }
        }

        if (!_missing.TryGetValue(name, out TextFont? missing))
        {
            missing = TextFont.Missing(name);
            if (_missing.Count < MaxFonts) _missing[name] = missing;
        }

        return missing;
    }

    private static double Min(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private static double Max(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
