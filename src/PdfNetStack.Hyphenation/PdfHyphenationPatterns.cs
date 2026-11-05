using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfNetStack.Drawing;

namespace PdfNetStack.Hyphenation;

/// <summary>
/// Ready <see cref="PdfHyphenator"/>s for many languages, from TeX's hyph-utf8 patterns under
/// permissive licenses, with the minimum letters before and after a hyphen each source gives. A
/// language loads on first use and stays loaded; the hyphenators are shared and thread safe.
/// </summary>
/// <example>
/// <code>
/// var p = new PdfParagraph { Alignment = PdfTextAlignment.Justify, Hyphenator = PdfHyphenationPatterns.ForLanguage("de") };
/// var options = new HtmlConverterOptions { Hyphenators = PdfHyphenationPatterns.ForLanguage };
/// </code>
/// </example>
public static class PdfHyphenationPatterns
{
    private const string ResourcePrefix = "PdfNetStack.Hyphenation.";
    private const string ResourceSuffix = ".deflate";
    private const int MaxTagLength = 64;

    private static readonly Dictionary<string, Lazy<PdfHyphenator>> Sets = LoadSets();

    // Tags that name a set by another name, or a region whose spelling has its own set.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["en"] = "en-us",
        ["en-au"] = "en-gb",
        ["en-hk"] = "en-gb",
        ["en-ie"] = "en-gb",
        ["en-in"] = "en-gb",
        ["en-nz"] = "en-gb",
        ["en-sg"] = "en-gb",
        ["en-uk"] = "en-gb",
        ["en-za"] = "en-gb",
        ["de-1996"] = "de",
        ["el"] = "el-monoton",
        ["no"] = "nb",
    };

    /// <summary>The tags of the available pattern sets, such as <c>de</c>, <c>en-gb</c> and <c>el-polyton</c>, in order.</summary>
    public static IReadOnlyList<string> Languages { get; } = [.. Sets.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    /// <summary>
    /// The hyphenator for a language tag, or null when there is none. Case and region do not matter
    /// beyond spelling: <c>de-AT</c> and <c>de_CH</c> find German, <c>en</c> and <c>en-CA</c> American
    /// English, <c>en-GB</c> and <c>en-AU</c> British. German in the 1901 spelling, such as <c>de-1901</c>,
    /// finds nothing: its breaks differ and its patterns are not included.
    /// </summary>
    /// <param name="language">A language tag such as <c>fr</c> or <c>pt-BR</c>.</param>
    public static PdfHyphenator? ForLanguage(string language)
    {
        _ = language ?? throw new ArgumentNullException(nameof(language));
        if (language.Length == 0 || language.Length > MaxTagLength) return null;
        string tag = language.Replace('_', '-').ToLowerInvariant();
        if (tag.StartsWith("de-", StringComparison.Ordinal) && tag.Contains("1901")) return null;
        while (true)
        {
            string name = Aliases.TryGetValue(tag, out string? alias) ? alias : tag;
            if (Sets.TryGetValue(name, out Lazy<PdfHyphenator>? set)) return set.Value;
            int dash = tag.LastIndexOf('-');
            if (dash <= 0) return null;
            tag = tag.Substring(0, dash);
        }
    }

    private static Dictionary<string, Lazy<PdfHyphenator>> LoadSets()
    {
        var sets = new Dictionary<string, Lazy<PdfHyphenator>>(StringComparer.Ordinal);
        foreach (string resource in typeof(PdfHyphenationPatterns).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;
            string tag = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - ResourceSuffix.Length);
            sets[tag] = new Lazy<PdfHyphenator>(() => Load(tag));
        }

        return sets;
    }

    private static PdfHyphenator Load(string tag)
    {
        (int left, int right, IEnumerable<string> patterns, string[] exceptions) = Read(tag);
        return PdfHyphenator.Create(patterns, exceptions) with { MinLeft = left, MinRight = right };
    }

    /// <summary>A set's file: notice lines starting with #, the minimums, the patterns or "@tag" for another set's, an empty line, the exceptions.</summary>
    private static (int Left, int Right, IEnumerable<string> Patterns, string[] Exceptions) Read(string tag)
    {
        using Stream stream = typeof(PdfHyphenationPatterns).Assembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix)
            ?? throw new InvalidOperationException($"The hyphenation patterns \"{tag}\" are missing.");
        using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        string[] lines = reader.ReadToEnd().Split('\n');
        int i = 0;
        while (lines[i].Length > 0 && lines[i][0] == '#') i++;
        string[] minimums = lines[i++].Split(' ');
        int left = int.Parse(minimums[0], CultureInfo.InvariantCulture), right = int.Parse(minimums[1], CultureInfo.InvariantCulture);
        int blank = Array.IndexOf(lines, "", i);
        IEnumerable<string> patterns = lines.Skip(i).Take(blank - i);
        if (lines[i][0] == '@') patterns = Read(lines[i].Substring(1)).Patterns;
        return (left, right, patterns, [.. lines.Skip(blank + 1).Where(l => l.Length > 0)]);
    }
}
