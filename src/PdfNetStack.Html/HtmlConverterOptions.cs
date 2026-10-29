using PdfNetStack.Fonts;

namespace PdfNetStack.Html;

/// <summary>Settings for <see cref="HtmlConverter"/>.</summary>
public sealed class HtmlConverterOptions
{
    /// <summary>
    /// The address relative URLs resolve against: a file path's URI such as
    /// <c>new Uri(@"C:\reports\")</c>, or a web address when <see cref="ResourceLoader"/> fetches
    /// from the network. Null when the HTML only uses absolute URLs or data: URIs.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Fetches style sheets, images and fonts by absolute URL; return null for anything that
    /// should not load. Without a loader only data: URIs and files under <see cref="BaseUri"/>
    /// load, and nothing comes from the network: keep it that way for untrusted HTML, or check
    /// each address before fetching it.
    /// </summary>
    public Func<Uri, byte[]?>? ResourceLoader { get; init; }

    /// <summary>Fonts to use before installed ones, matched by family name, weight and style.</summary>
    public PdfFontCollection? Fonts { get; init; }

    /// <summary>
    /// Whether installed fonts are used, for families the HTML names and as fallbacks for
    /// characters such as Arabic. Default true. With false, output depends only on the HTML, the
    /// supplied fonts and the standard 14 fonts, and is the same on every machine.
    /// </summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// The page size and margins before the document's @page rules, as CSS. Default
    /// <c>size: A4; margin: 2cm</c>.
    /// </summary>
    public string PageStyle { get; init; } = "size: A4; margin: 2cm";

    /// <summary>A style sheet applied before the document's own, for house styles.</summary>
    public string? StyleSheet { get; init; }

    /// <summary>Headings become bookmarks (the bookmark-level property). Default true.</summary>
    public bool Bookmarks { get; init; } = true;
}
