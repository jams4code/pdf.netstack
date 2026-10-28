using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>
/// The document information dictionary. Values are decoded from PDFDocEncoding or UTF-16 and
/// written back in whichever of the two fits.
/// </summary>
/// <remarks>Changing these values does not update XMP metadata. For PDF/A files keep both in sync.</remarks>
public sealed class PdfDocumentInfo
{
    private readonly PdfDocument _owner;

    internal PdfDocumentInfo(PdfDocument owner) => _owner = owner;

    /// <summary>Document title.</summary>
    public string? Title { get => this["Title"]; set => this["Title"] = value; }

    /// <summary>Person or organization that created the content.</summary>
    public string? Author { get => this["Author"]; set => this["Author"] = value; }

    /// <summary>Subject.</summary>
    public string? Subject { get => this["Subject"]; set => this["Subject"] = value; }

    /// <summary>Keywords.</summary>
    public string? Keywords { get => this["Keywords"]; set => this["Keywords"] = value; }

    /// <summary>Application that created the original content.</summary>
    public string? Creator { get => this["Creator"]; set => this["Creator"] = value; }

    /// <summary>Application that produced the PDF.</summary>
    public string? Producer { get => this["Producer"]; set => this["Producer"] = value; }

    /// <summary>Creation date.</summary>
    public DateTimeOffset? CreationDate { get => GetDate("CreationDate"); set => SetDate("CreationDate", value); }

    /// <summary>Last modification date.</summary>
    public DateTimeOffset? ModificationDate { get => GetDate("ModDate"); set => SetDate("ModDate", value); }

    /// <summary>Any text entry, including custom keys. Setting null removes the entry.</summary>
    public string? this[string key]
    {
        get => Dictionary(create: false) is { } d ? PdfText.GetString(d, key, _owner.Resolve) : null;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            if (value is null) Dictionary(create: false)?.Remove(key);
            else Dictionary(create: true)!.Set(key, PdfText.Encode(value));
        }
    }

    /// <summary>Keys present in the dictionary.</summary>
    public IReadOnlyList<string> Keys => Dictionary(create: false)?.Keys ?? [];

    private DateTimeOffset? GetDate(string key) => PdfText.ParseDate(this[key]);

    private void SetDate(string key, DateTimeOffset? value)
    {
        if (value is null) Dictionary(create: false)?.Remove(key);
        else Dictionary(create: true)!.Set(key, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfText.FormatDate(value.Value))));
    }

    private PdfDictionary? Dictionary(bool create)
    {
        if (_owner.InfoNumber is { } n && _owner.Objects.TryGetValue(n, out PdfObject? obj) && obj is PdfDictionary existing) return existing;
        if (!create) return null;
        var dict = new PdfDictionary();
        _owner.InfoNumber = _owner.Add(dict);
        return dict;
    }
}
