using System.Globalization;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>The kind of a form field.</summary>
public enum PdfFieldKind
{
    /// <summary>A text box.</summary>
    Text,

    /// <summary>A check box.</summary>
    CheckBox,

    /// <summary>A group of radio buttons.</summary>
    RadioButton,

    /// <summary>A button that triggers an action and holds no value.</summary>
    PushButton,

    /// <summary>A drop down list, possibly editable.</summary>
    ComboBox,

    /// <summary>A scrolling list.</summary>
    ListBox,

    /// <summary>A signature field.</summary>
    Signature,

    /// <summary>A field of an unknown type.</summary>
    Unknown,
}

/// <summary>
/// The interactive form of a document (AcroForm, ISO 32000-1, 12.7): its fields, their values,
/// and flattening into plain page content.
/// </summary>
/// <remarks>
/// Setting a value regenerates the field's appearance, so the filled form looks right in every
/// viewer without relying on NeedAppearances. Documents that also carry an XFA form lose it when
/// values change, so viewers show the AcroForm values.
/// </remarks>
public sealed class PdfAcroForm
{
    internal const int MaxFieldNodes = 50_000;
    private const int MaxDepth = 64;
    private readonly PdfDocument _doc;

    internal PdfAcroForm(PdfDocument doc) => _doc = doc;

    /// <summary>True when the document has an interactive form.</summary>
    public bool Exists => Dictionary is not null;

    /// <summary>
    /// Every terminal field, in tree order, with its fully qualified name. Recomputed on each
    /// call, so it reflects fields added or removed through the object model.
    /// </summary>
    public IReadOnlyList<PdfField> Fields => Collect();

    /// <summary>When true, viewers rebuild field appearances themselves. Values set here always come with appearances.</summary>
    public bool NeedAppearances
    {
        get => Dictionary is PdfDictionary d && _doc.Resolve(d["NeedAppearances"]) is PdfBoolean { Value: true };
        set
        {
            if (Dictionary is not PdfDictionary d) return;
            if (value) d.Set("NeedAppearances", PdfBoolean.True);
            else d.Remove("NeedAppearances");
        }
    }

    internal PdfDictionary? Dictionary => _doc.Resolve(_doc.Catalog["AcroForm"]) as PdfDictionary;

    internal PdfDocument Document => _doc;

    /// <summary>The field with this fully qualified name ("address.city"), or null.</summary>
    public PdfField? this[string name] => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Draws every field's current appearance into its page and removes the form: the values
    /// stay visible and printable but can no longer be edited. Hidden fields are dropped.
    /// </summary>
    public void Flatten()
    {
        if (Dictionary is null) return;
        var widgets = new HashSet<int>();
        var byPage = new Dictionary<PdfPage, List<PdfWidget>>();
        foreach (PdfField field in Fields)
        {
            foreach (PdfWidget w in field.Widgets)
            {
                if (w.Reference is PdfReference r) widgets.Add(r.Number);
                if (w.Page is null) continue;
                if (!byPage.TryGetValue(w.Page, out List<PdfWidget>? list)) byPage[w.Page] = list = [];
                list.Add(w);
            }
        }

        foreach ((PdfPage page, List<PdfWidget> list) in byPage)
        {
            using PdfGraphics g = page.CreateGraphics();
            foreach (PdfWidget w in list)
            {
                if (w.Hidden || w.Appearance() is not PdfReference appearance) continue;
                var stream = (PdfStream)_doc.Resolve(appearance)!;
                stream.Dictionary.Set("Type", new PdfName("XObject"));
                stream.Dictionary.Set("Subtype", new PdfName("Form"));
                g.DrawPdfSpaceXObject(appearance, PlaceAppearance(stream.Dictionary, w.Rectangle));
            }
        }

        // Widgets leave their pages; other annotations stay.
        foreach (PdfPage page in _doc.Pages)
        {
            if (_doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            var kept = new PdfArray();
            foreach (PdfObject a in annots)
            {
                bool widget = (a is PdfReference r && widgets.Contains(r.Number))
                    || (_doc.Resolve(a) is PdfDictionary d && d.GetName("Subtype") == "Widget");
                if (!widget) kept.Add(a);
            }

            if (kept.Count == 0) page.Dictionary.Remove("Annots");
            else page.Dictionary.Set("Annots", kept);
        }

        _doc.Catalog.Remove("AcroForm");
    }

    /// <summary>
    /// The matrix that maps an appearance onto a widget rectangle (ISO 32000-1, 12.5.5): the
    /// bounding box, transformed by the form's own matrix, is scaled and moved onto the rectangle.
    /// </summary>
    internal PdfMatrix PlaceAppearance(PdfDictionary form, PdfRectangle rect)
    {
        PdfRectangle bbox = PdfRectangle.From(form["BBox"], _doc.Resolve) ?? new PdfRectangle(0, 0, rect.Width, rect.Height);
        PdfMatrix matrix = _doc.Resolve(form["Matrix"]) is PdfArray { Count: 6 } m && m.All(v => _doc.Resolve(v) is PdfNumber)
            ? new PdfMatrix(Num(m[0]), Num(m[1]), Num(m[2]), Num(m[3]), Num(m[4]), Num(m[5]))
            : PdfMatrix.Identity;
        PdfPoint[] corners =
        [
            matrix.Transform(new PdfPoint(bbox.Left, bbox.Bottom)), matrix.Transform(new PdfPoint(bbox.Right, bbox.Bottom)),
            matrix.Transform(new PdfPoint(bbox.Left, bbox.Top)), matrix.Transform(new PdfPoint(bbox.Right, bbox.Top)),
        ];
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y);
        double w = corners.Max(c => c.X) - minX, h = corners.Max(c => c.Y) - minY;
        double sx = w > 0 ? rect.Width / w : 1, sy = h > 0 ? rect.Height / h : 1;
        return new PdfMatrix(sx, 0, 0, sy, rect.Left - minX * sx, rect.Bottom - minY * sy);

        double Num(PdfObject o) => ((PdfNumber)_doc.Resolve(o)!).ToDouble();
    }

    /// <summary>Drops an XFA form, which viewers would show instead of the AcroForm values.</summary>
    internal void DropXfa() => Dictionary?.Remove("XFA");

    private List<PdfField> Collect()
    {
        var fields = new List<PdfField>();
        if (Dictionary is not PdfDictionary form || _doc.Resolve(form["Fields"]) is not PdfArray roots) return fields;
        Dictionary<int, PdfPage> pages = WidgetPages();
        var visited = new HashSet<PdfDictionary>();
        foreach (PdfObject root in roots) Walk(root, [], "", 0);
        return fields;

        // Ancestors and names come from the walk, not from /Parent, which files often get wrong.
        void Walk(PdfObject entry, List<PdfDictionary> ancestors, string prefix, int depth)
        {
            if (depth > MaxDepth || visited.Count >= MaxFieldNodes || _doc.Resolve(entry) is not PdfDictionary node || !visited.Add(node)) return;
            string name = _doc.Resolve(node["T"]) is PdfString t ? (prefix.Length == 0 ? t.ToText() : prefix + "." + t.ToText()) : prefix;
            var childFields = new List<PdfObject>();
            var widgets = new List<PdfObject>();
            if (_doc.Resolve(node["Kids"]) is PdfArray kids)
            {
                foreach (PdfObject kid in kids)
                {
                    if (_doc.Resolve(kid) is not PdfDictionary k) continue;
                    if (k["T"] is not null || (k.GetName("Subtype") != "Widget" && k["Kids"] is not null)) childFields.Add(kid);
                    else widgets.Add(kid);
                }
            }

            if (childFields.Count > 0)
            {
                List<PdfDictionary> chain = [.. ancestors, node];
                foreach (PdfObject kid in childFields) Walk(kid, chain, name, depth + 1);
                return;
            }

            if (widgets.Count == 0) widgets.Add(entry);
            fields.Add(PdfField.Create(this, node, ancestors, name, widgets.Select(w => new PdfWidget(this, w, pages)).ToList()));
        }
    }

    /// <summary>The page of every annotation that is referenced from a page's /Annots.</summary>
    private Dictionary<int, PdfPage> WidgetPages()
    {
        var map = new Dictionary<int, PdfPage>();
        foreach (PdfPage page in _doc.Pages)
        {
            if (_doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (a is PdfReference r) map.TryAdd(r.Number, page);
            }
        }

        return map;
    }
}

/// <summary>One widget, the visible part of a field on a page.</summary>
public sealed class PdfWidget
{
    private readonly PdfAcroForm _form;
    private readonly PdfObject _entry;

    internal PdfWidget(PdfAcroForm form, PdfObject entry, Dictionary<int, PdfPage> pages)
    {
        _form = form;
        _entry = entry;
        PdfDocument doc = form.Document;
        Dictionary = doc.Resolve(entry) as PdfDictionary ?? new PdfDictionary();
        if (entry is PdfReference r && pages.TryGetValue(r.Number, out PdfPage? page)) Page = page;
        else if (Dictionary["P"] is PdfReference p) Page = doc.Pages.FirstOrDefault(x => x.Number == p.Number);
    }

    /// <summary>The widget annotation dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The page the widget is on, or null when no page shows it.</summary>
    public PdfPage? Page { get; }

    /// <summary>The widget rectangle in PDF user space.</summary>
    public PdfRectangle Rectangle
    {
        get
        {
            PdfRectangle r = PdfRectangle.From(Dictionary["Rect"], _form.Document.Resolve) ?? default;
            return new PdfRectangle(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
        }
    }

    /// <summary>True when the widget is hidden (annotation flag 2).</summary>
    public bool Hidden => _form.Document.Resolve(Dictionary["F"]) is PdfNumber { IsInteger: true } f && (f.IntegerValue & 2) != 0;

    internal PdfReference? Reference => _entry as PdfReference;

    /// <summary>The on state names of a button widget: every normal appearance but Off.</summary>
    internal IReadOnlyList<string> OnStates =>
        _form.Document.Resolve(Dictionary["AP"]) is PdfDictionary ap && _form.Document.Resolve(ap["N"]) is PdfDictionary states
            ? [.. states.Select(e => e.Key).Where(k => k != "Off")]
            : [];

    /// <summary>The appearance stream shown now: the normal appearance, or the one the appearance state selects.</summary>
    internal PdfReference? Appearance()
    {
        PdfDocument doc = _form.Document;
        if (doc.Resolve(Dictionary["AP"]) is not PdfDictionary ap) return null;
        PdfObject? normal = ap["N"];
        if (doc.Resolve(normal) is PdfDictionary states)
        {
            string state = Dictionary.GetName("AS") ?? "Off";
            normal = states[state];
        }

        return normal as PdfReference;
    }
}

/// <summary>A form field. Subclasses carry the value of each kind; <see cref="Dictionary"/> gives the rest.</summary>
public abstract class PdfField
{
    private List<PdfDictionary> _ancestors = [];

    private protected PdfField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
    {
        Form = form;
        Dictionary = dictionary;
        Widgets = widgets;
    }

    /// <summary>The field dictionary (for a field with one widget, often the widget too).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The widgets that show the field.</summary>
    public IReadOnlyList<PdfWidget> Widgets { get; }

    /// <summary>The kind of field.</summary>
    public abstract PdfFieldKind Kind { get; }

    /// <summary>Fully qualified name: the partial names of the field and its ancestors, joined by dots.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The tooltip (TU), or null.</summary>
    public string? ToolTip => Doc.Resolve(Inherited("TU")) is PdfString s ? s.ToText() : null;

    /// <summary>True when the field cannot be changed by the person filling the form.</summary>
    public bool ReadOnly => (Flags & 1) != 0;

    /// <summary>True when the field must have a value before the form is submitted.</summary>
    public bool Required => (Flags & 2) != 0;

    /// <summary>The value as text: the text, the chosen option, or the selected button state; null when empty.</summary>
    public abstract string? Value { get; }

    internal PdfAcroForm Form { get; }

    internal PdfDocument Doc => Form.Document;

    internal long Flags => Doc.Resolve(Inherited("Ff")) is PdfNumber { IsInteger: true } n ? n.IntegerValue : 0;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Kind}) = {Value}";

    internal static PdfField Create(PdfAcroForm form, PdfDictionary node, List<PdfDictionary> ancestors, string name, IReadOnlyList<PdfWidget> widgets)
    {
        PdfDocument doc = form.Document;
        string? type = doc.Resolve(InheritedFrom(node, ancestors, "FT")) is PdfName n ? n.Value : null;
        long flags = doc.Resolve(InheritedFrom(node, ancestors, "Ff")) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
        PdfField field = type switch
        {
            "Tx" => new PdfTextField(form, node, widgets),
            "Btn" when (flags & (1 << 16)) != 0 => new PdfPushButtonField(form, node, widgets),
            "Btn" when (flags & (1 << 15)) != 0 => new PdfRadioButtonField(form, node, widgets),
            "Btn" => new PdfCheckBoxField(form, node, widgets),
            "Ch" => new PdfChoiceField(form, node, widgets),
            "Sig" => new PdfSignatureField(form, node, widgets),
            _ => new PdfUnknownField(form, node, widgets),
        };
        field.Name = name;
        field._ancestors = ancestors;
        return field;
    }

    /// <summary>An attribute from the field or the nearest ancestor that has it, then the AcroForm default.</summary>
    internal PdfObject? Inherited(string key) => InheritedFrom(Dictionary, _ancestors, key) ?? (key is "DA" or "Q" ? Form.Dictionary?[key] : null);

    private static PdfObject? InheritedFrom(PdfDictionary node, List<PdfDictionary> ancestors, string key)
    {
        if (node[key] is PdfObject own) return own;
        for (int i = ancestors.Count - 1; i >= 0; i--)
        {
            if (ancestors[i][key] is PdfObject value) return value;
        }

        return null;
    }

    /// <summary>Marks the document as changed by filling: drops XFA and records the new value.</summary>
    internal void SetValue(PdfObject? value)
    {
        if (ReadOnly) throw new InvalidOperationException($"The field {Name} is read only.");
        Form.DropXfa();
        if (value is null) Dictionary.Remove("V");
        else Dictionary.Set("V", value);
        Dictionary.Remove("RV");
    }
}

/// <summary>A text box.</summary>
public sealed class PdfTextField : PdfField
{
    internal PdfTextField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.Text;

    /// <inheritdoc/>
    public override string? Value => Text;

    /// <summary>The text; setting it redraws the field.</summary>
    /// <exception cref="InvalidOperationException">The field is read only.</exception>
    public string? Text
    {
        get => Doc.Resolve(Inherited("V")) switch
        {
            PdfString s => s.ToText(),
            PdfStream s => System.Text.Encoding.UTF8.GetString(Doc.GetStreamData(s)),
            _ => null,
        };
        set
        {
            if (value is not null && MaxLength is int max && value.Length > max) value = value.Substring(0, max);
            SetValue(value is null ? null : PdfString.FromText(value));
            foreach (PdfWidget w in Widgets) FieldAppearance.Text(this, w, value ?? "");
        }
    }

    /// <summary>True for a box that wraps lines.</summary>
    public bool Multiline => (Flags & (1 << 12)) != 0;

    /// <summary>True for a box that hides what is typed.</summary>
    public bool Password => (Flags & (1 << 13)) != 0;

    /// <summary>True for a box divided into <see cref="MaxLength"/> equal cells, one character each.</summary>
    public bool Comb => (Flags & (1 << 24)) != 0 && MaxLength is not null;

    /// <summary>The most characters the field takes, or null.</summary>
    public int? MaxLength => Doc.Resolve(Inherited("MaxLen")) is PdfNumber { IsInteger: true } n && n.IntegerValue > 0 ? (int)Math.Min(n.IntegerValue, 1 << 20) : null;
}

/// <summary>A check box.</summary>
public sealed class PdfCheckBoxField : PdfField
{
    internal PdfCheckBoxField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.CheckBox;

    /// <inheritdoc/>
    public override string? Value => Checked ? OnValue : null;

    /// <summary>The value the box has when checked, often "Yes" or "On".</summary>
    public string OnValue => Widgets.SelectMany(w => w.OnStates).FirstOrDefault() ?? "Yes";

    /// <summary>Whether the box is checked; setting it switches every widget.</summary>
    /// <exception cref="InvalidOperationException">The field is read only.</exception>
    public bool Checked
    {
        get => Doc.Resolve(Inherited("V")) is PdfName v ? v.Value != "Off" : Widgets.Any(w => w.Dictionary.GetName("AS") is string s && s != "Off");
        set
        {
            SetValue(new PdfName(value ? OnValue : "Off"));
            foreach (PdfWidget w in Widgets)
            {
                string on = w.OnStates.Count > 0 ? w.OnStates[0] : OnValue;
                if (w.OnStates.Count == 0) FieldAppearance.CheckBox(this, w, on);
                w.Dictionary.Set("AS", new PdfName(value ? on : "Off"));
            }
        }
    }
}

/// <summary>A group of radio buttons; one may be selected.</summary>
public sealed class PdfRadioButtonField : PdfField
{
    internal PdfRadioButtonField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.RadioButton;

    /// <inheritdoc/>
    public override string? Value => Selected;

    /// <summary>The value of each button, in widget order.</summary>
    public IReadOnlyList<string> Options => [.. Widgets.Where(w => w.OnStates.Count > 0).Select(w => w.OnStates[0])];

    /// <summary>The selected button's value, or null; setting it selects that button and clears the others.</summary>
    /// <exception cref="ArgumentException">No button has the value.</exception>
    /// <exception cref="InvalidOperationException">The field is read only.</exception>
    public string? Selected
    {
        get => Doc.Resolve(Inherited("V")) is PdfName v && v.Value != "Off" ? v.Value : null;
        set
        {
            if (value is not null && !Options.Contains(value)) throw new ArgumentException($"The field {Name} has no option '{value}'. Options: {string.Join(", ", Options)}.", nameof(value));
            SetValue(new PdfName(value ?? "Off"));
            foreach (PdfWidget w in Widgets)
            {
                string? on = w.OnStates.Count > 0 ? w.OnStates[0] : null;
                w.Dictionary.Set("AS", new PdfName(value is not null && on == value ? on : "Off"));
            }
        }
    }
}

/// <summary>A combo box or list box.</summary>
public sealed class PdfChoiceField : PdfField
{
    internal PdfChoiceField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => IsComboBox ? PdfFieldKind.ComboBox : PdfFieldKind.ListBox;

    /// <summary>True for a drop down list, false for a scrolling list.</summary>
    public bool IsComboBox => (Flags & (1 << 17)) != 0;

    /// <summary>True for a combo box that accepts text outside the options.</summary>
    public bool Editable => (Flags & (1 << 18)) != 0;

    /// <summary>True for a list box that allows several selections.</summary>
    public bool MultiSelect => (Flags & (1 << 21)) != 0;

    /// <summary>The options: export value and the text shown for it.</summary>
    public IReadOnlyList<(string Value, string Text)> Options
    {
        get
        {
            var options = new List<(string, string)>();
            if (Doc.Resolve(Inherited("Opt")) is not PdfArray opt) return options;
            foreach (PdfObject o in opt)
            {
                switch (Doc.Resolve(o))
                {
                    case PdfString s:
                        options.Add((s.ToText(), s.ToText()));
                        break;
                    case PdfArray { Count: >= 2 } pair when Doc.Resolve(pair[0]) is PdfString e && Doc.Resolve(pair[1]) is PdfString t:
                        options.Add((e.ToText(), t.ToText()));
                        break;
                }
            }

            return options;
        }
    }

    /// <inheritdoc/>
    public override string? Value => SelectedValues.Count > 0 ? SelectedValues[0] : null;

    /// <summary>The selected values; usually one.</summary>
    public IReadOnlyList<string> SelectedValues => Doc.Resolve(Inherited("V")) switch
    {
        PdfString s => [s.ToText()],
        PdfArray a => [.. a.Select(Doc.Resolve).OfType<PdfString>().Select(s => s.ToText())],
        _ => [],
    };

    /// <summary>Selects one value (or text, for an editable combo box) and redraws the field.</summary>
    /// <exception cref="ArgumentException">The value is not an option and the field is not editable.</exception>
    public void Select(string value)
    {
        Compat.ThrowIfNull(value, nameof(value));
        Select([value]);
    }

    /// <summary>Selects several values of a multiple selection list box and redraws it.</summary>
    public void Select(IEnumerable<string> values)
    {
        Compat.ThrowIfNull(values, nameof(values));
        string[] chosen = [.. values];
        if (chosen.Length > 1 && !MultiSelect) throw new ArgumentException($"The field {Name} takes one value.", nameof(values));
        IReadOnlyList<(string Value, string Text)> options = Options;
        foreach (string v in chosen)
        {
            if (!(IsComboBox && Editable) && !options.Any(o => o.Value == v || o.Text == v))
            {
                throw new ArgumentException($"The field {Name} has no option '{v}'.", nameof(values));
            }
        }

        // Each value may be given as the export value or as the text shown for it.
        string[] exports = [.. chosen.Select(v => options.Any(o => o.Value == v) ? v : options.Where(o => o.Text == v).Select(o => o.Value).DefaultIfEmpty(v).First())];
        SetValue(exports.Length switch
        {
            0 => null,
            1 => PdfString.FromText(exports[0]),
            _ => new PdfArray([.. exports.Select(e => (PdfObject)PdfString.FromText(e))]),
        });
        var indices = new PdfArray();
        for (int i = 0; i < options.Count; i++)
        {
            if (exports.Contains(options[i].Value)) indices.Add(PdfNumber.FromInteger(i));
        }

        if (indices.Count > 0 && !IsComboBox) Dictionary.Set("I", indices);
        else Dictionary.Remove("I");
        foreach (PdfWidget w in Widgets) FieldAppearance.Choice(this, w, exports);
    }
}

/// <summary>A push button: it triggers an action and has no value.</summary>
public sealed class PdfPushButtonField : PdfField
{
    internal PdfPushButtonField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.PushButton;

    /// <inheritdoc/>
    public override string? Value => null;
}

/// <summary>A signature field.</summary>
public sealed class PdfSignatureField : PdfField
{
    internal PdfSignatureField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.Signature;

    /// <summary>True when the field holds a signature.</summary>
    public bool IsSigned => Doc.Resolve(Inherited("V")) is PdfDictionary;

    /// <inheritdoc/>
    public override string? Value => IsSigned ? "signed" : null;
}

/// <summary>A field whose type this library does not know.</summary>
public sealed class PdfUnknownField : PdfField
{
    internal PdfUnknownField(PdfAcroForm form, PdfDictionary dictionary, IReadOnlyList<PdfWidget> widgets)
        : base(form, dictionary, widgets)
    {
    }

    /// <inheritdoc/>
    public override PdfFieldKind Kind => PdfFieldKind.Unknown;

    /// <inheritdoc/>
    public override string? Value => Doc.Resolve(Inherited("V")) is PdfString s ? s.ToText() : null;
}

/// <summary>Draws field appearances: borders and background from MK, text in the DA font and color.</summary>
internal static class FieldAppearance
{
    public static void Text(PdfTextField field, PdfWidget widget, string text)
    {
        if (field.Password) text = new string('*', text.Length);
        Draw(field, widget, (g, w, h, style, pad) =>
        {
            int q = Quadding(field);
            if (field.Comb && field.MaxLength is int cells)
            {
                double cell = w / cells;
                for (int i = 0; i < text.Length && i < cells; i++)
                {
                    string c = text.Substring(i, 1);
                    double cw = style.Font.MeasureString(c, style.Size);
                    g.DrawString(c, style.Font, style.Size, style.Brush!, cell * i + (cell - cw) / 2, Baseline(style, h));
                }

                return;
            }

            if (field.Multiline)
            {
                var p = new PdfParagraph { Alignment = q == 1 ? PdfTextAlignment.Center : q == 2 ? PdfTextAlignment.Right : PdfTextAlignment.Left }.Add(text, style);
                g.DrawText(p, pad, pad, Math.Max(1, w - 2 * pad));
                return;
            }

            DrawLine(g, text, style, w, h, pad, q);
        }, auto: field.Multiline ? 12 : 0);
    }

    public static void Choice(PdfChoiceField field, PdfWidget widget, IReadOnlyList<string> selected)
    {
        IReadOnlyList<(string Value, string Text)> options = field.Options;
        Draw(field, widget, (g, w, h, style, pad) =>
        {
            if (field.IsComboBox)
            {
                string shown = selected.Count == 0 ? "" : options.FirstOrDefault(o => o.Value == selected[0]).Text ?? selected[0];
                DrawLine(g, shown, style, w, h, pad, Quadding(field));
                return;
            }

            double line = style.Font.GetLineHeight(style.Size);
            double top = pad;
            foreach ((string value, string text) in options)
            {
                if (top > h) break;
                if (selected.Contains(value))
                {
                    g.DrawRectangle(null, PdfColor.Rgb(0.6, 0.75, 0.85), pad / 2, top, w - pad, line);
                }

                g.DrawString(text, style.Font, style.Size, style.Brush!, pad, top + style.Font.Ascent * style.Size);
                top += line;
            }
        }, auto: 12);
    }

    public static void CheckBox(PdfCheckBoxField field, PdfWidget widget, string on)
    {
        PdfDocument doc = field.Doc;
        PdfRectangle r = widget.Rectangle;
        double w = r.Width, h = r.Height;
        PdfForm off = PdfForm.Create(doc, w, h), check = PdfForm.Create(doc, w, h);
        using (PdfGraphics g = off.CreateGraphics()) Frame(field, widget, g, w, h);
        using (PdfGraphics g = check.CreateGraphics())
        {
            Frame(field, widget, g, w, h);
            double size = Math.Min(w, h) * 0.8;
            string mark = ((char)0x2714).ToString();
            double mw = PdfStandardFont.ZapfDingbats.MeasureString(mark, size);
            g.DrawString(mark, PdfStandardFont.ZapfDingbats, size, Color(field) ?? PdfColor.Black, (w - mw) / 2, (h + size * 0.7) / 2);
        }

        widget.Dictionary.Set("AP", new PdfDictionary
        {
            ["N"] = new PdfDictionary { [on] = check.Reference, ["Off"] = off.Reference },
        });
    }

    private static void DrawLine(PdfGraphics g, string text, PdfTextStyle style, double w, double h, double pad, int q)
    {
        double tw = style.Font.MeasureString(text, style.Size);
        double x = q switch { 1 => (w - tw) / 2, 2 => w - pad - tw, _ => pad };
        g.DrawString(text, style.Font, style.Size, style.Brush!, x, Baseline(style, h));
    }

    private static double Baseline(PdfTextStyle style, double h) =>
        (h - (style.Font.Ascent + style.Font.Descent) * style.Size) / 2 + style.Font.Ascent * style.Size;

    private static void Draw(PdfField field, PdfWidget widget, Action<PdfGraphics, double, double, PdfTextStyle, double> content, double auto)
    {
        PdfDocument doc = field.Doc;
        PdfRectangle r = widget.Rectangle;
        double w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
        (PdfFont font, double size) = Font(field);
        double border = BorderWidth(field, widget);
        double pad = border + 1;
        if (size <= 0) size = auto > 0 ? auto : Math.Max(4, Math.Min(12, (h - 2 * pad) / font.GetLineHeight(1)));
        var style = new PdfTextStyle(font, size) { Brush = Color(field) ?? PdfColor.Black };

        PdfForm form = PdfForm.Create(doc, w, h);
        form.AppendContent("/Tx BMC\n"u8);
        using (PdfGraphics g = form.CreateGraphics())
        {
            Frame(field, widget, g, w, h);
            using (g.Save())
            {
                g.IntersectClip(border, border, Math.Max(0, w - 2 * border), Math.Max(0, h - 2 * border));
                content(g, w, h, style, pad);
            }
        }

        form.AppendContent("EMC\n"u8);
        widget.Dictionary.Set("AP", new PdfDictionary { ["N"] = form.Reference });
    }

    /// <summary>The widget's background and border, from its MK and BS entries.</summary>
    private static void Frame(PdfField field, PdfWidget widget, PdfGraphics g, double w, double h)
    {
        PdfDocument doc = field.Doc;
        PdfDictionary? mk = doc.Resolve(widget.Dictionary["MK"]) as PdfDictionary;
        PdfColor? background = ColorFrom(doc, mk?["BG"]), border = ColorFrom(doc, mk?["BC"]);
        double bw = BorderWidth(field, widget);
        if (background is PdfColor bg) g.DrawRectangle(null, bg, 0, 0, w, h);
        if (border is not PdfColor bc || bw <= 0) return;
        string style = (doc.Resolve(widget.Dictionary["BS"]) as PdfDictionary)?.GetName("S") ?? "S";
        if (style == "U")
        {
            g.DrawRectangle(null, bc, 0, h - bw, w, bw);
            return;
        }

        var pen = new PdfPen(bc, bw) { DashPattern = style == "D" ? [3] : [] };
        g.DrawRectangle(pen, null, bw / 2, bw / 2, w - bw, h - bw);
    }

    private static double BorderWidth(PdfField field, PdfWidget widget)
    {
        PdfDocument doc = field.Doc;
        if (doc.Resolve(widget.Dictionary["BS"]) is PdfDictionary bs && doc.Resolve(bs["W"]) is PdfNumber w) return Math.Max(0, Math.Min(w.ToDouble(), 20));
        if (doc.Resolve(widget.Dictionary["Border"]) is PdfArray { Count: >= 3 } b && doc.Resolve(b[2]) is PdfNumber bw) return Math.Max(0, Math.Min(bw.ToDouble(), 20));
        return 1;
    }

    private static int Quadding(PdfField field) =>
        field.Doc.Resolve(field.Inherited("Q")) is PdfNumber { IsInteger: true } q && q.IntegerValue is 1 or 2 ? (int)q.IntegerValue : 0;

    /// <summary>
    /// The font and size of the DA string. The named font is taken from the form's resources: its
    /// embedded program when complete, else the standard font it names or resembles, else Helvetica.
    /// </summary>
    private static (PdfFont Font, double Size) Font(PdfField field)
    {
        PdfDocument doc = field.Doc;
        string da = doc.Resolve(field.Inherited("DA")) is PdfString s ? s.ToText() : "";
        string[] tokens = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? name = null;
        double size = 0;
        for (int i = 2; i < tokens.Length; i++)
        {
            if (tokens[i] != "Tf" || tokens[i - 2][0] != '/') continue;
            name = tokens[i - 2].Substring(1);
            double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out size);
        }

        PdfFont font = PdfStandardFont.Helvetica;
        if (name is not null)
        {
            PdfDictionary? dr = doc.Resolve(field.Form.Dictionary?["DR"]) as PdfDictionary;
            PdfDictionary? fontDict = doc.Resolve((doc.Resolve(dr?["Font"]) as PdfDictionary)?[name]) as PdfDictionary;
            font = Embedded(doc, fontDict) ?? Standard(fontDict?.GetName("BaseFont") ?? name);
        }

        return (font, Math.Max(0, Math.Min(size, 300)));
    }

    private static PdfFont? Embedded(PdfDocument doc, PdfDictionary? fontDict)
    {
        if (fontDict is null) return null;
        PdfDictionary? descriptor = doc.Resolve(fontDict["FontDescriptor"]) as PdfDictionary;
        if (descriptor is null && doc.Resolve(fontDict["DescendantFonts"]) is PdfArray { Count: > 0 } d && doc.Resolve(d[0]) is PdfDictionary cid)
        {
            descriptor = doc.Resolve(cid["FontDescriptor"]) as PdfDictionary;
        }

        if (doc.Resolve(descriptor?["FontFile2"] ?? descriptor?["FontFile3"]) is not PdfStream program) return null;
        string baseFont = fontDict.GetName("BaseFont") ?? "";
        if (baseFont.Length > 7 && baseFont[6] == '+') return null; // a subset lacks the glyphs new text needs
        try
        {
            PdfOpenTypeFont font = PdfOpenTypeFont.Load(doc.GetStreamData(program));
            return font.AllowsEmbedding ? font : null;
        }
        catch (Exception e) when (e is FontFormatException or PdfFormatException)
        {
            return null;
        }
    }

    private static PdfFont Standard(string name)
    {
        if (PdfStandardFont.FromName(name) is PdfStandardFont exact) return exact;
        string n = name.ToUpperInvariant();
        bool bold = n.Contains("BOLD") || n is "HEBO" or "TIBO" or "COBO";
        bool italic = n.Contains("ITALIC") || n.Contains("OBLIQUE");
        if (n is "ZADB" || n.Contains("DINGBAT")) return PdfStandardFont.ZapfDingbats;
        if (n.StartsWith("COUR", StringComparison.Ordinal) || n.Contains("COURIER") || n.Contains("MONO") || n.StartsWith("CO", StringComparison.Ordinal) && n.Length == 4)
        {
            return bold ? (italic ? PdfStandardFont.CourierBoldOblique : PdfStandardFont.CourierBold) : italic ? PdfStandardFont.CourierOblique : PdfStandardFont.Courier;
        }

        if (n.StartsWith("TI", StringComparison.Ordinal) || n.Contains("TIMES") || n.Contains("SERIF") && !n.Contains("SANS"))
        {
            return bold ? (italic ? PdfStandardFont.TimesBoldItalic : PdfStandardFont.TimesBold) : italic ? PdfStandardFont.TimesItalic : PdfStandardFont.TimesRoman;
        }

        return bold ? (italic ? PdfStandardFont.HelveticaBoldOblique : PdfStandardFont.HelveticaBold) : italic ? PdfStandardFont.HelveticaOblique : PdfStandardFont.Helvetica;
    }

    /// <summary>The fill color the DA string sets with g, rg or k.</summary>
    private static PdfColor? Color(PdfField field)
    {
        string da = field.Doc.Resolve(field.Inherited("DA")) is PdfString s ? s.ToText() : "";
        string[] t = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = t.Length - 1; i >= 0; i--)
        {
            double Arg(int k) => double.TryParse(t[i - k], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Max(0, Math.Min(1, v)) : 0;
            switch (t[i])
            {
                case "g" when i >= 1:
                    return PdfColor.Gray(Arg(1));
                case "rg" when i >= 3:
                    return PdfColor.Rgb(Arg(3), Arg(2), Arg(1));
                case "k" when i >= 4:
                    return PdfColor.Cmyk(Arg(4), Arg(3), Arg(2), Arg(1));
            }
        }

        return null;
    }

    private static PdfColor? ColorFrom(PdfDocument doc, PdfObject? o)
    {
        if (doc.Resolve(o) is not PdfArray a) return null;
        double[] c = [.. a.Select(v => doc.Resolve(v) is PdfNumber n ? Math.Max(0, Math.Min(1, n.ToDouble())) : 0)];
        return c.Length switch
        {
            1 => PdfColor.Gray(c[0]),
            3 => PdfColor.Rgb(c[0], c[1], c[2]),
            4 => PdfColor.Cmyk(c[0], c[1], c[2], c[3]),
            _ => null,
        };
    }
}
