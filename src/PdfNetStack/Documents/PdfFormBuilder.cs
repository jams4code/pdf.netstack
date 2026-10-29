using System.Globalization;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>How a new form field looks and behaves. Options a field kind has no use for are ignored.</summary>
public sealed class PdfFieldOptions
{
    /// <summary>The font text is shown and typed in: one of the standard fonts, which every viewer has.</summary>
    public PdfStandardFont Font { get; init; } = PdfStandardFont.Helvetica;

    /// <summary>Font size in points; 0 fits the text to the field.</summary>
    public double FontSize { get; init; }

    /// <summary>Text color.</summary>
    public PdfColor TextColor { get; init; } = PdfColor.Black;

    /// <summary>Border color, or null for none.</summary>
    public PdfColor? BorderColor { get; init; } = PdfColor.Gray(0.6);

    /// <summary>Border width in points.</summary>
    public double BorderWidth { get; init; } = 1;

    /// <summary>Background color, or null for transparent.</summary>
    public PdfColor? BackgroundColor { get; init; } = PdfColor.White;

    /// <summary>The tooltip, also read by screen readers.</summary>
    public string? ToolTip { get; init; }

    /// <summary>The value cannot be changed by the person filling the form.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The field must have a value before the form is submitted.</summary>
    public bool Required { get; init; }

    /// <summary>Text fields: alignment of the text.</summary>
    public PdfTextAlignment Alignment { get; init; } = PdfTextAlignment.Left;

    /// <summary>Text fields: several lines, wrapped.</summary>
    public bool Multiline { get; init; }

    /// <summary>Text fields: shown as asterisks.</summary>
    public bool Password { get; init; }

    /// <summary>Text fields: the most characters it takes, or 0 for no limit.</summary>
    public int MaxLength { get; init; }

    /// <summary>Text fields with <see cref="MaxLength"/>: one character per equal cell, as on paper forms.</summary>
    public bool Comb { get; init; }

    /// <summary>Combo boxes: text outside the options may be typed.</summary>
    public bool Editable { get; init; }

    /// <summary>List boxes: several options may be selected.</summary>
    public bool MultiSelect { get; init; }
}

/// <content>Creating fields (ISO 32000-1, 12.7): each comes with its appearance, so it shows in every viewer.</content>
public sealed partial class PdfAcroForm
{
    private const int FlagReadOnly = 1;
    private const int FlagRequired = 1 << 1;
    private const int FlagMultiline = 1 << 12;
    private const int FlagPassword = 1 << 13;
    private const int FlagNoToggleToOff = 1 << 14;
    private const int FlagRadio = 1 << 15;
    private const int FlagPushButton = 1 << 16;
    private const int FlagCombo = 1 << 17;
    private const int FlagEdit = 1 << 18;
    private const int FlagMultiSelect = 1 << 21;
    private const int FlagComb = 1 << 24;

    /// <summary>Adds a text box. Positions are in points from the page's top left corner, as for drawing.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="value">The initial text, or null.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    /// <example>
    /// <code>
    /// PdfTextField name = doc.Form.AddTextField(page, "name", 72, 100, 200, 20);
    /// doc.Form.AddCheckBox(page, "subscribe", 72, 130, 14, 14, isChecked: true);
    /// doc.Form.AddComboBox(page, "country", 72, 160, 150, 20, ["Belgium", "France", "Morocco"], "France");
    /// </code>
    /// </example>
    public PdfTextField AddTextField(PdfPage page, string name, double x, double y, double width, double height, string? value = null, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        long flags = (options.Multiline ? FlagMultiline : 0) | (options.Password ? FlagPassword : 0) | (options.Comb && options.MaxLength > 0 ? FlagComb : 0);
        PdfDictionary widget = Widget(page, name, "Tx", flags, x, y, width, height, options);
        widget.Set("Q", PdfNumber.FromInteger(options.Alignment switch { PdfTextAlignment.Center => 1, PdfTextAlignment.Right => 2, _ => 0 }));
        if (options.MaxLength > 0) widget.Set("MaxLen", PdfNumber.FromInteger(options.MaxLength));
        var field = (PdfTextField)Register(page, widget, name);
        field.Text = value ?? "";
        if (value is null) field.Dictionary.Remove("V");
        return (PdfTextField)Finish(field, options);
    }

    /// <summary>Adds a check box.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge, from the page's left.</param>
    /// <param name="y">Top edge, from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="isChecked">Whether it starts checked.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    public PdfCheckBoxField AddCheckBox(PdfPage page, string name, double x, double y, double width, double height, bool isChecked = false, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        PdfDictionary widget = Widget(page, name, "Btn", 0, x, y, width, height, options);
        widget.Set("V", new PdfName("Off"));
        widget.Set("AS", new PdfName("Off"));
        var field = (PdfCheckBoxField)Register(page, widget, name);
        FieldAppearance.CheckBox(field, field.Widgets[0], "Yes");
        field.Checked = isChecked;
        return (PdfCheckBoxField)Finish(field, options);
    }

    /// <summary>Adds a group of radio buttons sharing one value: selecting one clears the others.</summary>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="buttons">Each button: its page, its box (from the page's top left) and the value it stands for.</param>
    /// <param name="selected">The value selected at first, or null.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    /// <exception cref="ArgumentException">There are no buttons, or two share a value.</exception>
    public PdfRadioButtonField AddRadioGroup(string name, IEnumerable<(PdfPage Page, double X, double Y, double Width, double Height, string Value)> buttons, string? selected = null, PdfFieldOptions? options = null)
    {
        Compat.ThrowIfNull(buttons, nameof(buttons));
        options ??= new PdfFieldOptions();
        CheckName(name);
        List<(PdfPage Page, double X, double Y, double Width, double Height, string Value)> list = [.. buttons];
        if (list.Count == 0) throw new ArgumentException("A radio group needs at least one button.", nameof(buttons));
        if (list.Select(b => b.Value).Distinct(StringComparer.Ordinal).Count() != list.Count) throw new ArgumentException("Each radio button needs its own value.", nameof(buttons));

        var parent = new PdfDictionary
        {
            ["FT"] = new PdfName("Btn"),
            ["T"] = PdfString.FromText(name),
            ["Ff"] = PdfNumber.FromInteger(FlagRadio | FlagNoToggleToOff),
            ["V"] = new PdfName("Off"),
        };
        PdfReference parentReference = _doc.AddObject(parent);
        var kids = new PdfArray();
        foreach ((PdfPage page, double x, double y, double w, double h, string value) in list)
        {
            CheckValue(value, nameof(buttons));
            PdfDictionary widget = Annotation(page, x, y, w, h, options);
            widget.Set("Parent", parentReference);
            widget.Set("AS", new PdfName("Off"));
            widget.Set("AP", new PdfDictionary
            {
                ["N"] = new PdfDictionary { [value] = Radio(w, h, options, true), ["Off"] = Radio(w, h, options, false) },
            });
            kids.Add(page.Annotations.AddAnnotation(widget));
        }

        parent.Set("Kids", kids);
        FieldArray().Add(parentReference);
        var field = (PdfRadioButtonField)this[name]!;
        if (selected is not null) field.Selected = selected;
        return (PdfRadioButtonField)Finish(field, options);
    }

    /// <summary>Adds a drop down list.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge, from the page's left.</param>
    /// <param name="y">Top edge, from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="choices">The options, as shown and as exported.</param>
    /// <param name="selected">The option selected at first, or null.</param>
    /// <param name="options">Look and behaviour; defaults when null. <see cref="PdfFieldOptions.Editable"/> allows other text.</param>
    public PdfChoiceField AddComboBox(PdfPage page, string name, double x, double y, double width, double height, IEnumerable<string> choices, string? selected = null, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        return Choice(page, name, x, y, width, height, choices, selected is null ? [] : [selected], FlagCombo | (options.Editable ? FlagEdit : 0), options);
    }

    /// <summary>Adds a scrolling list.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge, from the page's left.</param>
    /// <param name="y">Top edge, from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="choices">The options, as shown and as exported.</param>
    /// <param name="selected">The options selected at first; more than one needs <see cref="PdfFieldOptions.MultiSelect"/>.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    public PdfChoiceField AddListBox(PdfPage page, string name, double x, double y, double width, double height, IEnumerable<string> choices, IEnumerable<string>? selected = null, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        return Choice(page, name, x, y, width, height, choices, [.. selected ?? []], options.MultiSelect ? FlagMultiSelect : 0, options);
    }

    /// <summary>Adds an empty signature field, to be signed later by this library or a viewer.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge, from the page's left.</param>
    /// <param name="y">Top edge, from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    public PdfSignatureField AddSignatureField(PdfPage page, string name, double x, double y, double width, double height, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        PdfDictionary widget = Widget(page, name, "Sig", 0, x, y, width, height, options);
        widget.Remove("DA");
        widget.Set("AP", new PdfDictionary { ["N"] = Box(width, height, options, null) });
        return (PdfSignatureField)Finish(Register(page, widget, name), options);
    }

    /// <summary>Adds a push button with a caption, opening a web address when pressed if one is given.</summary>
    /// <param name="page">The page it shows on.</param>
    /// <param name="name">The field name, unique in the form, without dots.</param>
    /// <param name="x">Left edge, from the page's left.</param>
    /// <param name="y">Top edge, from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="caption">The text on the button.</param>
    /// <param name="uri">The address it opens, or null for a button scripts or submit actions are attached to later.</param>
    /// <param name="options">Look and behaviour; defaults when null.</param>
    public PdfPushButtonField AddPushButton(PdfPage page, string name, double x, double y, double width, double height, string caption, Uri? uri = null, PdfFieldOptions? options = null)
    {
        Compat.ThrowIfNull(caption, nameof(caption));
        options ??= new PdfFieldOptions { BackgroundColor = PdfColor.Gray(0.9) };
        PdfDictionary widget = Widget(page, name, "Btn", FlagPushButton, x, y, width, height, options);
        ((PdfDictionary)widget["MK"]!).Set("CA", PdfString.FromText(caption));
        widget.Set("AP", new PdfDictionary { ["N"] = Box(width, height, options, caption) });
        if (uri is not null)
        {
            if (!uri.IsAbsoluteUri) throw new ArgumentException("The button needs an absolute address.", nameof(uri));
            widget.Set("A", new PdfDictionary { ["S"] = new PdfName("URI"), ["URI"] = new PdfString(System.Text.Encoding.ASCII.GetBytes(uri.AbsoluteUri).AsSpan()) });
        }

        return (PdfPushButtonField)Finish(Register(page, widget, name), options);
    }

    private PdfChoiceField Choice(PdfPage page, string name, double x, double y, double width, double height, IEnumerable<string> choices, string[] selected, long flags, PdfFieldOptions options)
    {
        Compat.ThrowIfNull(choices, nameof(choices));
        PdfDictionary widget = Widget(page, name, "Ch", flags, x, y, width, height, options);
        widget.Set("Opt", new PdfArray([.. choices.Select(c => (PdfObject)PdfString.FromText(c ?? throw new ArgumentException("An option is null.", nameof(choices))))]));
        var field = (PdfChoiceField)Register(page, widget, name);
        if (selected.Length > 0) field.Select(selected);
        else FieldAppearance.Choice(field, field.Widgets[0], []);
        return (PdfChoiceField)Finish(field, options);
    }

    /// <summary>A field that is its own widget: field entries and widget annotation in one dictionary (ISO 32000-1, 12.7.3.1).</summary>
    private PdfDictionary Widget(PdfPage page, string name, string type, long flags, double x, double y, double width, double height, PdfFieldOptions options)
    {
        CheckName(name);
        PdfDictionary widget = Annotation(page, x, y, width, height, options);
        widget.Set("FT", new PdfName(type));
        widget.Set("T", PdfString.FromText(name));
        if (flags != 0) widget.Set("Ff", PdfNumber.FromInteger(flags));
        widget.Set("DA", PdfString.FromText(DefaultAppearance(options)));
        if (options.ToolTip is string tip) widget.Set("TU", PdfString.FromText(tip));
        return widget;
    }

    private PdfDictionary Annotation(PdfPage page, double x, double y, double width, double height, PdfFieldOptions options)
    {
        Compat.ThrowIfNull(page, nameof(page));
        if (!ReferenceEquals(page.Owner, _doc)) throw new ArgumentException("The page belongs to another document.", nameof(page));
        PdfRectangle rect = page.Annotations.ToPdf(x, y, width, height);
        var mk = new PdfDictionary();
        if (options.BorderColor is PdfColor bc) mk.Set("BC", PdfArray.Of(bc.Components()));
        if (options.BackgroundColor is PdfColor bg) mk.Set("BG", PdfArray.Of(bg.Components()));
        return new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Widget"),
            ["Rect"] = rect.ToArray(),
            ["P"] = new PdfReference(page.Number, 0),
            ["F"] = PdfNumber.FromInteger(4), // print
            ["MK"] = mk,
            ["BS"] = new PdfDictionary { ["W"] = PdfNumber.FromDouble(Math.Max(0, options.BorderWidth)), ["S"] = new PdfName("S") },
        };
    }

    private PdfField Register(PdfPage page, PdfDictionary widget, string name)
    {
        FieldArray().Add(page.Annotations.AddAnnotation(widget));
        return this[name]!;
    }

    /// <summary>Flags that would stop the appearance from being set go on last.</summary>
    private static PdfField Finish(PdfField field, PdfFieldOptions options)
    {
        long extra = (options.ReadOnly ? FlagReadOnly : 0) | (options.Required ? FlagRequired : 0);
        if (extra != 0) field.Dictionary.Set("Ff", PdfNumber.FromInteger(field.Flags | extra));
        return field;
    }

    private void CheckName(string name)
    {
        Compat.ThrowIfNull(name, nameof(name));
        if (name.Length == 0 || Compat.Contains(name, ".", StringComparison.Ordinal)) throw new ArgumentException("A field name must be non empty and have no dots.", nameof(name));
        if (this[name] is not null) throw new ArgumentException($"The form already has a field named {name}.", nameof(name));
    }

    private static void CheckValue(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value == "Off") throw new ArgumentException("A button value must be non empty and not Off.", parameter);
    }

    /// <summary>The form's /Fields array, creating the form with its default resources on first use.</summary>
    private PdfArray FieldArray()
    {
        if (Dictionary is not PdfDictionary form)
        {
            form = new PdfDictionary
            {
                ["Fields"] = new PdfArray(),
                ["DA"] = PdfString.FromText("/Helvetica 0 Tf 0 g"),
                ["DR"] = new PdfDictionary { ["Font"] = new PdfDictionary() },
            };
            _doc.Catalog.Set("AcroForm", _doc.AddObject(form));
        }

        if (_doc.Resolve(form["Fields"]) is not PdfArray fields)
        {
            fields = new PdfArray();
            form.Set("Fields", fields);
        }

        return fields;
    }

    /// <summary>The DA string, with the font added to the form's resources under its PostScript name.</summary>
    private string DefaultAppearance(PdfFieldOptions options)
    {
        FieldArray();
        PdfDictionary form = Dictionary!;
        if (_doc.Resolve(form["DR"]) is not PdfDictionary dr) form.Set("DR", dr = new PdfDictionary());
        if (_doc.Resolve(dr["Font"]) is not PdfDictionary fonts) dr.Set("Font", fonts = new PdfDictionary());
        string key = options.Font.Name;
        if (fonts[key] is null)
        {
            var font = new PdfDictionary
            {
                ["Type"] = new PdfName("Font"),
                ["Subtype"] = new PdfName("Type1"),
                ["BaseFont"] = new PdfName(key),
            };
            if (key is not ("Symbol" or "ZapfDingbats")) font.Set("Encoding", new PdfName("WinAnsiEncoding"));
            fonts.Set(key, _doc.AddObject(font));
        }

        double[] c = options.TextColor.ToRgb().Components();
        string size = Math.Max(0, options.FontSize).ToString("0.###", CultureInfo.InvariantCulture);
        string color = string.Join(" ", c.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        return $"/{key} {size} Tf {color} rg";
    }

    /// <summary>A radio button: a circle, filled at the center when on.</summary>
    private PdfReference Radio(double width, double height, PdfFieldOptions options, bool on)
    {
        PdfForm form = PdfForm.Create(_doc, width, height);
        using (PdfGraphics g = form.CreateGraphics())
        {
            double d = Math.Min(width, height), bw = Math.Max(0, options.BorderWidth);
            double left = (width - d) / 2, top = (height - d) / 2;
            PdfPen? pen = options.BorderColor is PdfColor bc && bw > 0 ? new PdfPen(bc, bw) : null;
            g.DrawEllipse(pen, options.BackgroundColor, left + (bw / 2), top + (bw / 2), d - bw, d - bw);
            if (on) g.DrawEllipse(null, options.TextColor, left + (d * 0.3), top + (d * 0.3), d * 0.4, d * 0.4);
        }

        return form.Reference;
    }

    /// <summary>A plain box: background and border, with a centered caption when given.</summary>
    private PdfReference Box(double width, double height, PdfFieldOptions options, string? caption)
    {
        PdfForm form = PdfForm.Create(_doc, width, height);
        using (PdfGraphics g = form.CreateGraphics())
        {
            double bw = Math.Max(0, options.BorderWidth);
            PdfPen? pen = options.BorderColor is PdfColor bc && bw > 0 ? new PdfPen(bc, bw) : null;
            if (pen is not null || options.BackgroundColor is not null) g.DrawRectangle(pen, options.BackgroundColor, bw / 2, bw / 2, width - bw, height - bw);
            if (caption is not null)
            {
                PdfFont font = options.Font;
                double size = options.FontSize > 0 ? options.FontSize : Math.Max(4, Math.Min(12, (height - (2 * bw) - 2) / font.GetLineHeight(1)));
                double tw = font.MeasureString(caption, size);
                double baseline = ((height - ((font.Ascent + font.Descent) * size)) / 2) + (font.Ascent * size);
                g.DrawString(caption, font, size, options.TextColor, (width - tw) / 2, baseline);
            }
        }

        return form.Reference;
    }
}
