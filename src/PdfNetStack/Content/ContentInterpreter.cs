using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Content;

/// <summary>
/// Walks a page's content with its graphics state (ISO 32000-1, 8.4): the current transformation
/// matrix through q, Q and cm, form XObjects with their own matrix and resources, and inline
/// images. Subclasses see images, and text operators with the text state they apply under.
/// </summary>
/// <remarks>
/// Content comes from untrusted files, so the walk is bounded: forms nest at most
/// <see cref="MaxFormDepth"/> deep, a form never draws itself, and every page gets a budget of
/// operators shared by everything it draws.
/// </remarks>
internal abstract class ContentInterpreter
{
    public const int MaxFormDepth = 16;
    public const int MaxStateDepth = 256;
    public const long MaxOperations = 10_000_000;

    /// <summary>
    /// Bytes a page may decode and parse, together. A form drawn again is parsed again, so a
    /// small form holding one huge operand would otherwise cost far more than its operators.
    /// </summary>
    public const long MaxBytes = 256L << 20;

    private readonly Stack<(PdfMatrix Ctm, TextState Text)> _saved = new();
    private readonly HashSet<PdfStream> _activeForms = [];

    // Decoded once per form however often it is drawn; null when it cannot be decoded.
    private readonly Dictionary<PdfStream, byte[]?> _forms = [];
    private long _operations;
    private long _bytes;

    protected ContentInterpreter(PdfDocument document) => Document = document;

    protected PdfDocument Document { get; }

    /// <summary>The current transformation matrix: user space to the page's default user space.</summary>
    protected PdfMatrix Ctm { get; private set; } = PdfMatrix.Identity;

    /// <summary>The text state (ISO 32000-1, 9.3), current while text is shown.</summary>
    protected TextState Text { get; private set; } = new();

    /// <summary>Walks a page's content streams, in order, with the page's resources.</summary>
    public void Run(PdfPage page)
    {
        Compat.ThrowIfNull(page, nameof(page));
        PdfDictionary? resources = Document.Resolve(page.Dictionary["Resources"]) as PdfDictionary;
        PdfObject? contents = Document.Resolve(page.Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents switch
        {
            PdfArray array => array,
            PdfStream stream => [stream],
            _ => [],
        };

        // Content split across streams is one stream (ISO 32000-1, 7.8.2): concatenate before reading.
        using var joined = new MemoryStream();
        foreach (PdfObject part in parts)
        {
            if (Document.Resolve(part) is not PdfStream stream) continue;
            byte[]? data = Data(stream);
            if (data is null) continue;
            if (_bytes > MaxBytes) break;
            joined.Write(data, 0, data.Length);
            joined.WriteByte((byte)'\n');
        }

        Execute(joined.ToArray(), resources, 0);
    }

    /// <summary>An image XObject was drawn; the unit square maps to the page through <see cref="Ctm"/>.</summary>
    protected virtual void OnImage(PdfStream image, PdfDictionary? resources)
    {
    }

    /// <summary>An inline image was drawn.</summary>
    protected virtual void OnInlineImage(PdfDictionary image, ReadOnlyMemory<byte> data, PdfDictionary? resources)
    {
    }

    /// <summary>
    /// A text showing operator: Tj, ', " (one string) or TJ (strings and numbers). The text
    /// state is already updated for ' and ".
    /// </summary>
    protected virtual void OnShowText(List<PdfObject> items, PdfDictionary? resources)
    {
    }

    private byte[]? Data(PdfStream stream)
    {
        if (_bytes > MaxBytes) return null;
        try
        {
            byte[] data = Document.GetStreamData(stream);
            _bytes += data.Length;
            return data;
        }
        catch (PdfFormatException)
        {
            // A damaged stream draws nothing, as in viewers; the rest of the page still counts.
            return null;
        }
    }

    private void Execute(byte[] content, PdfDictionary? resources, int depth)
    {
        _bytes += content.Length;
        if (_bytes > MaxBytes) return;
        var reader = new ContentReader(content);
        var op = new ContentOperation();
        int baseDepth = _saved.Count;
        while (reader.Next(op))
        {
            if (++_operations > MaxOperations) return;
            List<PdfObject> o = op.Operands;
            switch (op.Operator)
            {
                case "q":
                    if (_saved.Count < MaxStateDepth) _saved.Push((Ctm, Text));
                    break;
                case "Q":
                    // A stream cannot restore state saved outside it (ISO 32000-1, 7.8.2).
                    if (_saved.Count > baseDepth) (Ctm, Text) = _saved.Pop();
                    break;
                case "cm" when Numbers(o, 6) is { } m:
                    Ctm = PdfMatrix.Multiply(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), Ctm);
                    break;
                case "Do" when o.Count > 0 && o[^1] is PdfName name:
                    Draw(name.Value, resources, depth);
                    break;
                case "BI" when op.InlineImage is not null:
                    OnInlineImage(op.InlineImage, op.InlineData, resources);
                    break;
                default:
                    TextOperator(op.Operator, o, resources);
                    break;
            }
        }

        // Unbalanced q inside a form or page is closed at its end.
        while (_saved.Count > baseDepth) (Ctm, Text) = _saved.Pop();
    }

    private void Draw(string name, PdfDictionary? resources, int depth)
    {
        if (Document.Resolve(resources?["XObject"]) is not PdfDictionary xobjects || Document.Resolve(xobjects[name]) is not PdfStream xobject) return;
        switch (xobject.Dictionary.GetName("Subtype"))
        {
            case "Image":
                OnImage(xobject, resources);
                break;
            case "Form" when depth < MaxFormDepth && _activeForms.Add(xobject):
                try
                {
                    PdfMatrix saved = Ctm;
                    TextState text = Text;
                    if (Numbers(Document.Resolve(xobject.Dictionary["Matrix"]) as PdfArray, 6) is { } m)
                    {
                        Ctm = PdfMatrix.Multiply(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), Ctm);
                    }

                    // A form without resources uses its parent's (ISO 32000-1, 7.8.3, for old files).
                    PdfDictionary? own = Document.Resolve(xobject.Dictionary["Resources"]) as PdfDictionary;
                    if (!_forms.TryGetValue(xobject, out byte[]? data)) _forms[xobject] = data = Data(xobject);
                    if (data is not null) Execute(data, own ?? resources, depth + 1);
                    Ctm = saved;
                    Text = text;
                }
                finally
                {
                    _activeForms.Remove(xobject);
                }

                break;
        }
    }

    /// <summary>Text state and positioning operators (ISO 32000-1, 9.3 and 9.4).</summary>
    private void TextOperator(string op, List<PdfObject> o, PdfDictionary? resources)
    {
        switch (op)
        {
            case "BT":
                Text = Text with { Matrix = PdfMatrix.Identity, LineMatrix = PdfMatrix.Identity };
                break;
            case "Tc" when Number(o, 1) is double tc:
                Text = Text with { CharacterSpacing = tc };
                break;
            case "Tw" when Number(o, 1) is double tw:
                Text = Text with { WordSpacing = tw };
                break;
            case "Tz" when Number(o, 1) is double tz:
                Text = Text with { HorizontalScaling = tz / 100 };
                break;
            case "TL" when Number(o, 1) is double tl:
                Text = Text with { Leading = tl };
                break;
            case "Ts" when Number(o, 1) is double ts:
                Text = Text with { Rise = ts };
                break;
            case "Tr" when Number(o, 1) is double tr && tr is >= 0 and <= 7:
                Text = Text with { RenderMode = (int)tr };
                break;
            case "Tf" when o.Count >= 2 && o[^2] is PdfName font && Number(o, 1) is double size:
                // Tf takes a name then a size: the size is the last operand.
                Text = Text with { FontName = font.Value, FontSize = size };
                break;
            case "Td" when Numbers(o, 2) is { } d:
                NextLine(d[0], d[1]);
                break;
            case "TD" when Numbers(o, 2) is { } d:
                Text = Text with { Leading = -d[1] };
                NextLine(d[0], d[1]);
                break;
            case "Tm" when Numbers(o, 6) is { } m:
                var matrix = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                Text = Text with { Matrix = matrix, LineMatrix = matrix };
                break;
            case "T*":
                NextLine(0, -Text.Leading);
                break;
            case "Tj" when o.Count > 0 && o[^1] is PdfString:
                OnShowText([o[^1]], resources);
                break;
            case "'" when o.Count > 0 && o[^1] is PdfString:
                NextLine(0, -Text.Leading);
                OnShowText([o[^1]], resources);
                break;
            case "\"" when o.Count >= 3 && o[^1] is PdfString && Number(o, 3) is double aw && Number(o, 2) is double ac:
                Text = Text with { WordSpacing = aw, CharacterSpacing = ac };
                NextLine(0, -Text.Leading);
                OnShowText([o[^1]], resources);
                break;
            case "TJ" when o.Count > 0 && o[^1] is PdfArray items:
                OnShowText([.. items], resources);
                break;
        }
    }

    private void NextLine(double tx, double ty)
    {
        PdfMatrix line = PdfMatrix.Multiply(PdfMatrix.CreateTranslation(tx, ty), Text.LineMatrix);
        Text = Text with { Matrix = line, LineMatrix = line };
    }

    /// <summary>Moves the text matrix after a glyph or adjustment, in unscaled text space units.</summary>
    protected void AdvanceText(double tx, double ty) =>
        Text = Text with { Matrix = PdfMatrix.Multiply(PdfMatrix.CreateTranslation(tx, ty), Text.Matrix) };

    /// <summary>
    /// The operand <paramref name="fromEnd"/> places from the end (1 is the last) as a finite
    /// number. Operators read their operands from the end, so a stray value before them is ignored.
    /// </summary>
    private static double? Number(List<PdfObject> o, int fromEnd)
    {
        if (o.Count < fromEnd || o[o.Count - fromEnd] is not PdfNumber n) return null;
        double v = n.ToDouble();
        return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    /// <summary>The last <paramref name="count"/> operands as numbers, or null when they are not.</summary>
    private static double[]? Numbers(IReadOnlyList<PdfObject>? o, int count)
    {
        if (o is null || o.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (o[o.Count - count + i] is not PdfNumber n) return null;
            double v = n.ToDouble();
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            result[i] = v;
        }

        return result;
    }
}

/// <summary>The text state parameters (ISO 32000-1, 9.3) with the text and line matrices.</summary>
internal sealed record TextState
{
    public double CharacterSpacing { get; init; }

    public double WordSpacing { get; init; }

    /// <summary>Tz as a fraction: 1 is 100 percent.</summary>
    public double HorizontalScaling { get; init; } = 1;

    public double Leading { get; init; }

    public double Rise { get; init; }

    public int RenderMode { get; init; }

    public string? FontName { get; init; }

    public double FontSize { get; init; }

    public PdfMatrix Matrix { get; init; } = PdfMatrix.Identity;

    public PdfMatrix LineMatrix { get; init; } = PdfMatrix.Identity;
}
