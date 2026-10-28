using System.Collections;
using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>One page of a <see cref="PdfDocument"/>.</summary>
public sealed class PdfPage
{
    internal PdfPage(PdfDocument owner, int number, PdfDictionary dictionary)
    {
        Owner = owner;
        Number = number;
        Dictionary = dictionary;
        owner.RegisterPage(number);
    }

    internal PdfDocument Owner { get; }

    internal int Number { get; }

    internal PdfDictionary Dictionary { get; }

    /// <summary>The document this page belongs to.</summary>
    public PdfDocument Document => Owner;

    /// <summary>Zero based position in the document, or -1 once removed.</summary>
    public int Index => Owner.Pages.IndexOf(this);

    /// <summary>The page boundary. Defaults to US Letter when the file does not define one.</summary>
    public PdfRectangle MediaBox
    {
        get => PdfRectangle.From(Dictionary["MediaBox"], Owner.Resolve) ?? PdfRectangle.Letter;
        set => Dictionary.Set("MediaBox", value.ToArray());
    }

    /// <summary>The visible region. Defaults to the media box.</summary>
    public PdfRectangle CropBox
    {
        get => PdfRectangle.From(Dictionary["CropBox"], Owner.Resolve) ?? MediaBox;
        set => Dictionary.Set("CropBox", value.ToArray());
    }

    /// <summary>Media box width in points.</summary>
    public double Width => MediaBox.Width;

    /// <summary>Media box height in points.</summary>
    public double Height => MediaBox.Height;

    /// <summary>Clockwise rotation applied when displaying: 0, 90, 180 or 270.</summary>
    public int Rotation
    {
        get
        {
            int r = Owner.Resolve(Dictionary["Rotate"]) is PdfNumber { IsInteger: true } n ? (int)(n.IntegerValue % 360) : 0;
            return ((r % 360) + 360) % 360 / 90 * 90;
        }

        set
        {
            if (value % 90 != 0) throw new ArgumentOutOfRangeException(nameof(value), "Rotation must be a multiple of 90.");
            int normalized = ((value % 360) + 360) % 360;
            if (normalized == 0) Dictionary.Remove("Rotate");
            else Dictionary.Set("Rotate", PdfNumber.FromInteger(normalized));
        }
    }

    /// <summary>Rotates the page clockwise by <paramref name="degrees"/>, a multiple of 90.</summary>
    public void Rotate(int degrees) => Rotation = Rotation + degrees;

    /// <summary>Number of annotations (links, notes, form widgets) on the page.</summary>
    public int AnnotationCount => Owner.Resolve(Dictionary["Annots"]) is PdfArray a ? a.Count : 0;
}

/// <summary>The ordered pages of a document.</summary>
public sealed class PdfPageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument _owner;
    private readonly List<PdfPage> _pages = new();

    internal PdfPageCollection(PdfDocument owner) => _owner = owner;

    /// <inheritdoc />
    public int Count => _pages.Count;

    /// <inheritdoc />
    public PdfPage this[int index] => _pages[index];

    /// <summary>Position of a page, or -1.</summary>
    public int IndexOf(PdfPage page) => _pages.IndexOf(page);

    internal void Attach(PdfPage page) => _pages.Add(page);

    /// <summary>Appends a blank page.</summary>
    public PdfPage Add(PdfRectangle mediaBox) => Insert(_pages.Count, mediaBox);

    /// <summary>Inserts a blank page at <paramref name="index"/>.</summary>
    public PdfPage Insert(int index, PdfRectangle mediaBox)
    {
        Compat.ThrowIfGreaterThan((uint)index, (uint)_pages.Count, nameof(index));
        if (mediaBox.Width <= 0 || mediaBox.Height <= 0) throw new ArgumentException("Page size must be positive.", nameof(mediaBox));
        var dict = new PdfDictionary();
        dict.Set("Type", new PdfName("Page"));
        dict.Set("MediaBox", mediaBox.ToArray());
        dict.Set("Resources", new PdfDictionary());
        var page = new PdfPage(_owner, _owner.Add(dict), dict);
        _pages.Insert(index, page);
        return page;
    }

    /// <summary>Removes the page at <paramref name="index"/>. Links and bookmarks pointing to it become empty.</summary>
    public void RemoveAt(int index) => _pages.RemoveAt(index);

    /// <summary>Removes a page. Returns false when it is not in this document.</summary>
    public bool Remove(PdfPage page) => _pages.Remove(page);

    /// <summary>Moves a page to a new position.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        PdfPage page = _pages[fromIndex];
        _pages.RemoveAt(fromIndex);
        _pages.Insert(toIndex, page);
    }

    /// <summary>
    /// Copies pages, from this or another document, to the end of this document, or to
    /// <paramref name="index"/>. Links between pages copied in the same call keep working; links to
    /// pages that are not copied become empty. Returns the new pages.
    /// </summary>
    public IReadOnlyList<PdfPage> Import(IEnumerable<PdfPage> pages, int? index = null)
    {
        Compat.ThrowIfNull(pages, nameof(pages));
        List<PdfPage> source = pages.ToList();
        int at = index ?? _pages.Count;
        Compat.ThrowIfGreaterThan((uint)at, (uint)_pages.Count, nameof(index));

        // Import per source document, so links between pages of one source resolve, then restore
        // the caller's order. A page listed twice is copied twice.
        var copies = new Dictionary<PdfPage, Queue<PdfPage>>();
        foreach (IGrouping<PdfDocument, PdfPage> group in source.GroupBy(p => p.Owner))
        {
            List<PdfPage> originals = group.ToList();
            IReadOnlyList<PdfPage> imported = _owner.ImporterFor(group.Key).Import(originals);
            for (int i = 0; i < originals.Count; i++)
            {
                if (!copies.TryGetValue(originals[i], out Queue<PdfPage>? queue)) copies[originals[i]] = queue = new Queue<PdfPage>();
                queue.Enqueue(imported[i]);
            }
        }

        var ordered = source.Select(p => copies[p].Dequeue()).ToList();
        _pages.InsertRange(at, ordered);
        return ordered;
    }

    /// <summary>Removes every page.</summary>
    public void Clear() => _pages.Clear();

    /// <inheritdoc />
    public IEnumerator<PdfPage> GetEnumerator() => _pages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
