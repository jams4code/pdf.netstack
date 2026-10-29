using System.Globalization;
using System.Text;
using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>How a viewer arranges pages (catalog /PageLayout).</summary>
public enum PdfPageLayout
{
    /// <summary>One page at a time.</summary>
    SinglePage,

    /// <summary>Pages in one continuous column.</summary>
    OneColumn,

    /// <summary>Two continuous columns, odd pages on the left.</summary>
    TwoColumnLeft,

    /// <summary>Two continuous columns, odd pages on the right.</summary>
    TwoColumnRight,

    /// <summary>Two pages at a time, odd pages on the left.</summary>
    TwoPageLeft,

    /// <summary>Two pages at a time, odd pages on the right.</summary>
    TwoPageRight,
}

/// <summary>Which panel a viewer opens with (catalog /PageMode).</summary>
public enum PdfPageMode
{
    /// <summary>No panel.</summary>
    UseNone,

    /// <summary>The bookmarks panel.</summary>
    UseOutlines,

    /// <summary>The page thumbnails.</summary>
    UseThumbs,

    /// <summary>Full screen.</summary>
    FullScreen,

    /// <summary>The layers panel.</summary>
    UseOC,

    /// <summary>The attachments panel.</summary>
    UseAttachments,
}

/// <summary>Numbering style of page labels.</summary>
public enum PdfPageLabelStyle
{
    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>A to Z, then AA to ZZ.</summary>
    UpperLetters,

    /// <summary>a to z, then aa to zz.</summary>
    LowerLetters,

    /// <summary>The prefix alone.</summary>
    None,
}

/// <summary>How viewers present the document: layout, panels, window and where it opens.</summary>
public sealed class PdfViewerSettings
{
    private readonly PdfDocument _doc;

    internal PdfViewerSettings(PdfDocument doc) => _doc = doc;

    /// <summary>Page arrangement, or null for the viewer's default.</summary>
    public PdfPageLayout? PageLayout
    {
        get => Enum.TryParse(_doc.Catalog.GetName("PageLayout"), out PdfPageLayout v) ? v : null;
        set => SetName(_doc.Catalog, "PageLayout", value?.ToString());
    }

    /// <summary>The panel shown on opening, or null for none.</summary>
    public PdfPageMode? PageMode
    {
        get => Enum.TryParse(_doc.Catalog.GetName("PageMode"), out PdfPageMode v) ? v : null;
        set => SetName(_doc.Catalog, "PageMode", value?.ToString());
    }

    /// <summary>Show the document title, not the file name, in the window title.</summary>
    public bool DisplayDocumentTitle { get => Flag("DisplayDocTitle"); set => SetFlag("DisplayDocTitle", value); }

    /// <summary>Hide the viewer's toolbars.</summary>
    public bool HideToolbar { get => Flag("HideToolbar"); set => SetFlag("HideToolbar", value); }

    /// <summary>Hide the viewer's menu bar.</summary>
    public bool HideMenubar { get => Flag("HideMenubar"); set => SetFlag("HideMenubar", value); }

    /// <summary>Hide scroll bars and navigation controls.</summary>
    public bool HideWindowUI { get => Flag("HideWindowUI"); set => SetFlag("HideWindowUI", value); }

    /// <summary>Size the window to the first page.</summary>
    public bool FitWindow { get => Flag("FitWindow"); set => SetFlag("FitWindow", value); }

    /// <summary>Center the window on the screen.</summary>
    public bool CenterWindow { get => Flag("CenterWindow"); set => SetFlag("CenterWindow", value); }

    /// <summary>Reading order right to left, as for Arabic and Hebrew books: facing pages are mirrored.</summary>
    public bool RightToLeft
    {
        get => Preferences(false)?.GetName("Direction") == "R2L";
        set => SetName(Preferences(true)!, "Direction", value ? "R2L" : null);
    }

    /// <summary>Opens the document at a page, scrolled to <paramref name="top"/> points from its top, at a zoom factor (1 for 100%).</summary>
    public void OpenAt(PdfPage page, double? top = null, double? zoom = null)
    {
        Compat.ThrowIfNull(page, nameof(page));
        if (!ReferenceEquals(page.Owner, _doc)) throw new ArgumentException("The page belongs to another document.", nameof(page));
        PdfObject y = top is double t ? PdfNumber.FromDouble(page.MediaBox.Top - t) : PdfNull.Instance;
        PdfObject z = zoom is double f ? PdfNumber.FromDouble(f) : PdfNull.Instance;
        _doc.Catalog.Set("OpenAction", PdfArray.Of(new PdfReference(page.Number, 0), new PdfName("XYZ"), PdfNull.Instance, y, z));
    }

    /// <summary>Opens the document at a page, fitted in the window.</summary>
    public void OpenAtFit(PdfPage page)
    {
        Compat.ThrowIfNull(page, nameof(page));
        if (!ReferenceEquals(page.Owner, _doc)) throw new ArgumentException("The page belongs to another document.", nameof(page));
        _doc.Catalog.Set("OpenAction", PdfArray.Of(new PdfReference(page.Number, 0), new PdfName("Fit")));
    }

    /// <summary>The page the document opens at, or null.</summary>
    public PdfPage? OpenPage => PdfAnnotationCollection.ResolveDestination(_doc,
        _doc.Resolve(_doc.Catalog["OpenAction"]) is PdfDictionary action ? _doc.Resolve(action["D"]) : _doc.Catalog["OpenAction"]);

    private PdfDictionary? Preferences(bool create)
    {
        if (_doc.Resolve(_doc.Catalog["ViewerPreferences"]) is PdfDictionary existing)
        {
            if (!create || _doc.Catalog["ViewerPreferences"] is PdfDictionary) return existing;
            PdfDictionary copy = PdfDocument.Clone(existing);
            _doc.Catalog.Set("ViewerPreferences", copy);
            return copy;
        }

        if (!create) return null;
        var created = new PdfDictionary();
        _doc.Catalog.Set("ViewerPreferences", created);
        return created;
    }

    private bool Flag(string key) => _doc.Resolve(Preferences(false)?[key]) is PdfBoolean { Value: true };

    private void SetFlag(string key, bool value)
    {
        PdfDictionary prefs = Preferences(true)!;
        if (value) prefs.Set(key, PdfBoolean.True);
        else prefs.Remove(key);
    }

    private static void SetName(PdfDictionary d, string key, string? value)
    {
        if (value is null) d.Remove(key);
        else d.Set(key, new PdfName(value));
    }
}

/// <summary>
/// Page labels: the page numbers a viewer shows, such as i, ii, iii for a preface and then 1, 2,
/// 3. Each range starts at a page index and runs to the next range.
/// </summary>
public sealed class PdfPageLabels
{
    private readonly PdfDocument _doc;

    internal PdfPageLabels(PdfDocument doc) => _doc = doc;

    /// <summary>The ranges, by first page index.</summary>
    public IReadOnlyList<(int StartPage, PdfPageLabelStyle Style, string Prefix, int FirstNumber)> Ranges
    {
        get
        {
            var ranges = new List<(int, PdfPageLabelStyle, string, int)>();
            if (_doc.Resolve(_doc.Catalog["PageLabels"]) is not PdfDictionary tree) return ranges;
            foreach ((PdfObject key, PdfObject value) in TreeReader.Entries(_doc, tree, "Nums"))
            {
                if (_doc.Resolve(key) is not PdfNumber { IsInteger: true } start || _doc.Resolve(value) is not PdfDictionary label) continue;
                PdfPageLabelStyle style = label.GetName("S") switch
                {
                    "D" => PdfPageLabelStyle.Decimal,
                    "R" => PdfPageLabelStyle.UpperRoman,
                    "r" => PdfPageLabelStyle.LowerRoman,
                    "A" => PdfPageLabelStyle.UpperLetters,
                    "a" => PdfPageLabelStyle.LowerLetters,
                    _ => PdfPageLabelStyle.None,
                };
                string prefix = _doc.Resolve(label["P"]) is PdfString p ? p.ToText() : "";
                int first = _doc.Resolve(label["St"]) is PdfNumber { IsInteger: true } st ? (int)Math.Max(1, Math.Min(st.IntegerValue, int.MaxValue)) : 1;
                ranges.Add(((int)Math.Max(0, Math.Min(start.IntegerValue, int.MaxValue)), style, prefix, first));
            }

            ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return ranges;
        }
    }

    /// <summary>Starts a range at page <paramref name="startPage"/> (zero based), replacing one that starts there.</summary>
    public void Add(int startPage, PdfPageLabelStyle style, string? prefix = null, int firstNumber = 1)
    {
        Compat.ThrowIfNegative(startPage, nameof(startPage));
        if (firstNumber < 1) throw new ArgumentOutOfRangeException(nameof(firstNumber), "Page labels start at 1 or more.");
        var ranges = Ranges.Where(r => r.StartPage != startPage).ToList();
        ranges.Add((startPage, style, prefix ?? "", firstNumber));
        if (!ranges.Any(r => r.StartPage == 0)) ranges.Add((0, PdfPageLabelStyle.Decimal, "", 1));
        ranges.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));

        var nums = new PdfArray();
        foreach ((int start, PdfPageLabelStyle s, string p, int first) in ranges)
        {
            var label = new PdfDictionary { ["Type"] = new PdfName("PageLabel") };
            string? code = s switch
            {
                PdfPageLabelStyle.Decimal => "D",
                PdfPageLabelStyle.UpperRoman => "R",
                PdfPageLabelStyle.LowerRoman => "r",
                PdfPageLabelStyle.UpperLetters => "A",
                PdfPageLabelStyle.LowerLetters => "a",
                _ => null,
            };
            if (code is not null) label.Set("S", new PdfName(code));
            if (p.Length > 0) label.Set("P", PdfString.FromText(p));
            if (first != 1) label.Set("St", PdfNumber.FromInteger(first));
            nums.Add(PdfNumber.FromInteger(start));
            nums.Add(label);
        }

        _doc.Catalog.Set("PageLabels", new PdfDictionary { ["Nums"] = nums });
    }

    /// <summary>Removes all page labels; viewers number pages 1, 2, 3 again.</summary>
    public void Clear() => _doc.Catalog.Remove("PageLabels");

    /// <summary>The label of the page at <paramref name="pageIndex"/>, as a viewer shows it.</summary>
    public string GetLabel(int pageIndex)
    {
        Compat.ThrowIfNegative(pageIndex, nameof(pageIndex));
        var range = Ranges.LastOrDefault(r => r.StartPage <= pageIndex);
        if (range.Prefix is null) return (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
        long n = (long)range.FirstNumber + pageIndex - range.StartPage;
        return range.Prefix + range.Style switch
        {
            PdfPageLabelStyle.Decimal => n.ToString(CultureInfo.InvariantCulture),
            PdfPageLabelStyle.UpperRoman => Roman(n),
            PdfPageLabelStyle.LowerRoman => Roman(n).ToLowerInvariant(),
            PdfPageLabelStyle.UpperLetters => Letters(n),
            PdfPageLabelStyle.LowerLetters => Letters(n).ToLowerInvariant(),
            _ => "",
        };
    }

    private static string Roman(long n)
    {
        if (n > 100_000) return n.ToString(CultureInfo.InvariantCulture);
        (int Value, string Symbol)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new StringBuilder();
        foreach ((int value, string symbol) in table)
        {
            while (n >= value)
            {
                sb.Append(symbol);
                n -= value;
            }
        }

        return sb.ToString();
    }

    /// <summary>A to Z, then AA to ZZ, then AAA: the letter repeats, as the PDF specification defines.</summary>
    private static string Letters(long n)
    {
        if (n > 26 * 1000) return n.ToString(CultureInfo.InvariantCulture);
        char letter = (char)('A' + (n - 1) % 26);
        return new string(letter, (int)((n - 1) / 26) + 1);
    }
}

/// <summary>A file embedded in the document (catalog /Names /EmbeddedFiles).</summary>
public sealed class PdfAttachment
{
    private readonly PdfDocument _doc;
    private readonly PdfDictionary _spec;

    internal PdfAttachment(PdfDocument doc, string name, PdfDictionary spec)
    {
        _doc = doc;
        Name = name;
        _spec = spec;
    }

    /// <summary>The name in the attachment list, usually the file name.</summary>
    public string Name { get; }

    /// <summary>The file name the attachment is saved as.</summary>
    public string FileName => _doc.Resolve(_spec["UF"]) is PdfString uf ? uf.ToText() : _doc.Resolve(_spec["F"]) is PdfString f ? f.ToText() : Name;

    /// <summary>The description, or null.</summary>
    public string? Description => _doc.Resolve(_spec["Desc"]) is PdfString d ? d.ToText() : null;

    /// <summary>The MIME type, such as "text/xml", or null.</summary>
    public string? MimeType => Stream?.Dictionary.GetName("Subtype");

    /// <summary>The file specification dictionary.</summary>
    public PdfDictionary Dictionary => _spec;

    private PdfStream? Stream =>
        _doc.Resolve(_spec["EF"]) is PdfDictionary ef && _doc.Resolve(ef["UF"] ?? ef["F"]) is PdfStream s ? s : null;

    /// <summary>The file's bytes.</summary>
    /// <exception cref="PdfFormatException">The attachment has no data or it cannot be decoded.</exception>
    public byte[] GetData() => _doc.GetStreamData(Stream ?? throw new PdfFormatException($"Attachment {Name} has no embedded data."));
}

/// <summary>The files embedded in a document, shown in a viewer's attachments panel.</summary>
public sealed class PdfAttachmentCollection : IReadOnlyList<PdfAttachment>
{
    private readonly PdfDocument _doc;

    internal PdfAttachmentCollection(PdfDocument doc) => _doc = doc;

    /// <inheritdoc/>
    public int Count => Read().Count;

    /// <inheritdoc/>
    public PdfAttachment this[int index] => Read()[index];

    /// <summary>The attachment with this name, or null.</summary>
    public PdfAttachment? this[string name] => Read().FirstOrDefault(a => a.Name == name);

    /// <summary>Embeds a file, replacing an attachment of the same name.</summary>
    public PdfAttachment Add(string name, byte[] data, string? mimeType = null, string? description = null, DateTimeOffset? modified = null)
    {
        Compat.ThrowIfNull(name, nameof(name));
        Compat.ThrowIfNull(data, nameof(data));
        if (name.Length == 0) throw new ArgumentException("An attachment needs a name.", nameof(name));
        PdfStream file = PdfStream.FromData(data);
        file.Dictionary.Set("Type", new PdfName("EmbeddedFile"));
        if (mimeType is not null) file.Dictionary.Set("Subtype", new PdfName(mimeType));
        var parameters = new PdfDictionary { ["Size"] = PdfNumber.FromInteger(data.Length) };
        if (modified is DateTimeOffset m) parameters.Set("ModDate", new PdfString(Encoding.ASCII.GetBytes(PdfText.FormatDate(m)).AsSpan()));
        file.Dictionary.Set("Params", parameters);
        PdfReference stream = _doc.AddObject(file);

        var spec = new PdfDictionary
        {
            ["Type"] = new PdfName("Filespec"),
            ["F"] = PdfString.FromText(name),
            ["UF"] = PdfString.FromText(name),
            ["EF"] = new PdfDictionary { ["F"] = stream, ["UF"] = stream },
        };
        if (description is not null) spec.Set("Desc", PdfString.FromText(description));

        List<(string Name, PdfObject Spec)> entries = [.. Entries().Where(e => e.Name != name)];
        entries.Add((name, _doc.AddObject(spec)));
        Write(entries);
        return new PdfAttachment(_doc, name, spec);
    }

    /// <summary>Removes the attachment with this name.</summary>
    public bool Remove(string name)
    {
        List<(string Name, PdfObject Spec)> entries = Entries();
        int removed = entries.RemoveAll(e => e.Name == name);
        if (removed > 0) Write(entries);
        return removed > 0;
    }

    /// <inheritdoc/>
    public IEnumerator<PdfAttachment> GetEnumerator() => Read().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private List<PdfAttachment> Read() =>
        [.. Entries().Select(e => (e.Name, Spec: _doc.Resolve(e.Spec) as PdfDictionary)).Where(e => e.Spec is not null).Select(e => new PdfAttachment(_doc, e.Name, e.Spec!))];

    private List<(string Name, PdfObject Spec)> Entries()
    {
        var entries = new List<(string, PdfObject)>();
        if (_doc.Resolve(_doc.Catalog["Names"]) is not PdfDictionary names || _doc.Resolve(names["EmbeddedFiles"]) is not PdfDictionary tree) return entries;
        foreach ((PdfObject key, PdfObject value) in TreeReader.Entries(_doc, tree, "Names"))
        {
            if (_doc.Resolve(key) is PdfString s) entries.Add((s.ToText(), value));
        }

        return entries;
    }

    /// <summary>Rewrites the tree as one sorted leaf; the catalog's /Names dictionary is copied before changing.</summary>
    private void Write(List<(string Name, PdfObject Spec)> entries)
    {
        PdfDictionary names = _doc.Resolve(_doc.Catalog["Names"]) is PdfDictionary existing ? PdfDocument.Clone(existing) : new PdfDictionary();
        if (entries.Count == 0)
        {
            names.Remove("EmbeddedFiles");
        }
        else
        {
            var array = new PdfArray();
            foreach ((string name, PdfObject spec) in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                array.Add(PdfString.FromText(name));
                array.Add(spec);
            }

            names.Set("EmbeddedFiles", new PdfDictionary { ["Names"] = array });
        }

        if (names.Count == 0) _doc.Catalog.Remove("Names");
        else _doc.Catalog.Set("Names", names);
    }
}

/// <summary>Reads name and number trees (ISO 32000-1, 7.9.6 and 7.9.7) with bounds on depth and size.</summary>
internal static class TreeReader
{
    private const int MaxDepth = 32;
    private const int MaxNodes = 10_000;

    public static List<(PdfObject Key, PdfObject Value)> Entries(PdfDocument doc, PdfDictionary root, string leafKey)
    {
        var entries = new List<(PdfObject, PdfObject)>();
        var visited = new HashSet<PdfDictionary>();
        Walk(root, 0);
        return entries;

        void Walk(PdfDictionary node, int depth)
        {
            if (depth > MaxDepth || visited.Count >= MaxNodes || !visited.Add(node)) return;
            if (doc.Resolve(node[leafKey]) is PdfArray leaf)
            {
                for (int i = 0; i + 1 < leaf.Count; i += 2) entries.Add((leaf[i], leaf[i + 1]));
            }

            if (doc.Resolve(node["Kids"]) is PdfArray kids)
            {
                foreach (PdfObject kid in kids)
                {
                    if (doc.Resolve(kid) is PdfDictionary k) Walk(k, depth + 1);
                }
            }
        }
    }
}
