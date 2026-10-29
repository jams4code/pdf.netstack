using System.Runtime.InteropServices;
using PdfNetStack.Fonts.OpenType;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts;

/// <summary>
/// Fonts found by family name, weight, width and style, the way CSS matches them. Add font files
/// or directories, or use <see cref="System"/> for the fonts installed on the machine. Adding a
/// file reads only its names and style; the font itself loads when first found. Thread safe.
/// </summary>
/// <remarks>
/// A variable font counts for every weight, width and slant its axes cover: asking for weight 650
/// of a family whose font has a wght axis from 100 to 900 gives the instance at 650. Its named
/// instances are faces of the family too, and like any face can also be found by full name, such
/// as "Inter SemiBold".
/// </remarks>
/// <example>
/// <code>
/// PdfFont body = PdfFontCollection.System.Find("Segoe UI") ?? PdfStandardFont.Helvetica;
/// var fonts = new PdfFontCollection();
/// fonts.AddDirectory("fonts");
/// PdfFont? heading = fonts.Find("Inter", weight: 700);
/// </code>
/// </example>
public sealed class PdfFontCollection
{
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];
    private static readonly Lazy<PdfFontCollection> SystemFonts = new(LoadSystemFonts);

    private readonly object _lock = new();
    private readonly Dictionary<string, List<Face>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Face>> _fullNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fonts installed on this machine (Windows, macOS and Linux font directories), scanned on first use.</summary>
    public static PdfFontCollection System => SystemFonts.Value;

    /// <summary>Family names, sorted.</summary>
    public IReadOnlyList<string> Families
    {
        get
        {
            lock (_lock) return [.. _families.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Adds a loaded font under its family name. A variable font as loaded adds its whole design
    /// space and its named instances; an instance from <see cref="PdfOpenTypeFont.GetInstance(string)"/>
    /// adds that one location.
    /// </summary>
    public void Add(PdfOpenTypeFont font)
    {
        Compat.ThrowIfNull(font, nameof(font));
        foreach ((Face face, string? fullName, string? postScriptName) in Faces(new Source(null, 0) { Font = font }, font))
        {
            Register(face, font.FamilyName, null, fullName, postScriptName);
        }
    }

    /// <summary>Adds every font of a file: one, or all fonts of a collection. Returns how many were added.</summary>
    /// <exception cref="FontFormatException">The file is not a supported font.</exception>
    public int AddFile(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        List<Scanned> fonts = Scan(path);
        foreach (Scanned font in fonts)
        {
            foreach ((Face face, string? fullName, string? postScriptName) in font.Faces) Register(face, font.Family, font.Typographic, fullName, postScriptName);
        }

        return fonts.Count;
    }

    /// <summary>
    /// Adds the fonts in a directory with a font extension (.ttf, .otf, .ttc, .otc, .woff, .woff2).
    /// Files that are not readable fonts are skipped. Returns how many fonts were added.
    /// </summary>
    public int AddDirectory(string path, bool recursive = true)
    {
        Compat.ThrowIfNull(path, nameof(path));
        if (!Directory.Exists(path)) return 0;
        int added = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string file in files)
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                added += AddFile(file);
            }
            catch (Exception e) when (e is FontFormatException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // System font directories hold all sorts of files; one bad file must not stop the scan.
            }
        }

        return added;
    }

    /// <summary>
    /// The font of <paramref name="family"/> closest to the requested weight and style, chosen as
    /// CSS does (CSS Fonts 4, 5.2): for weights 400 to 500, heavier up to 500 first, then
    /// lighter, then heavier; below 400, lighter first; above 500, heavier first. An italic is
    /// preferred when asked for, an upright face otherwise. A variable font gives its instance at
    /// the weight asked when its wght axis covers it. Null when the family is unknown.
    /// </summary>
    /// <param name="family">Family name, case insensitive, such as "Inter" or "Times New Roman", or a face's full name.</param>
    /// <param name="weight">CSS weight, 1 to 1000: 400 regular, 700 bold.</param>
    /// <param name="italic">True to prefer an italic or oblique face.</param>
    public PdfOpenTypeFont? Find(string family, int weight = 400, bool italic = false)
    {
        Compat.ThrowIfNull(family, nameof(family));
        if (weight is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(weight), "CSS weights range from 1 to 1000.");
        return Find(family, new PdfFontQuery { Weight = weight, Style = italic ? PdfFontStyle.Italic : PdfFontStyle.Normal });
    }

    /// <summary>
    /// The font of <paramref name="family"/> that best matches <paramref name="query"/>, chosen as
    /// CSS does (CSS Fonts 4, 5.2): the closest width first, then style, then weight. A variable
    /// font gives its instance at the values found, clamped to its axes, with the query's optical
    /// size and variations applied over them (CSS Fonts 4, 7.2). Each location is made once, so
    /// finding it again returns the same font. Null when the family is unknown.
    /// </summary>
    /// <param name="family">Family name, case insensitive, such as "Inter", or a face's full or PostScript name, such as "Inter SemiBold".</param>
    /// <param name="query">The weight, width, style and variations wanted.</param>
    public PdfOpenTypeFont? Find(string family, PdfFontQuery query) => Find(family, query, null);

    /// <summary>As <see cref="Find(string, PdfFontQuery)"/>, with the instances made counted against <paramref name="budget"/>.</summary>
    internal PdfOpenTypeFont? Find(string family, PdfFontQuery query, InstanceBudget? budget)
    {
        Compat.ThrowIfNull(family, nameof(family));
        Compat.ThrowIfNull(query, nameof(query));
        List<Face> candidates;
        lock (_lock)
        {
            string name = family.Trim();
            if (!_families.TryGetValue(name, out List<Face>? faces) && !_fullNames.TryGetValue(name, out faces)) return null;
            candidates = [.. faces];
        }

        // Faces whose file does not load drop out and the match runs again on the rest.
        while (candidates.Count > 0)
        {
            List<Face> best = FontMatcher.Select(candidates, f => f.Traits, query);
            if (best.Count == 0) break;
            foreach (Face face in best)
            {
                PdfOpenTypeFont? font = Load(face.Source);
                if (font is not null) return FontMatcher.Instance(font, face.Traits, query, face.Location, budget: budget);
            }

            candidates.RemoveAll(best.Contains);
        }

        return null;
    }

    private void Register(Face face, string family, string? typographic, string? fullName, string? postScriptName)
    {
        lock (_lock)
        {
            // Typographic family (name ID 16) groups weights that the legacy family (ID 1) splits,
            // such as "Inter Medium" under "Inter"; a face is found under both.
            foreach (string? name in new[] { family, typographic })
            {
                if (string.IsNullOrWhiteSpace(name) || (name == typographic && string.Equals(family, typographic, StringComparison.OrdinalIgnoreCase))) continue;
                Index(_families, name!.Trim());
            }

            foreach (string? name in new[] { fullName, postScriptName })
            {
                if (string.IsNullOrWhiteSpace(name) || (name == postScriptName && string.Equals(fullName, postScriptName, StringComparison.OrdinalIgnoreCase))) continue;
                Index(_fullNames, name!.Trim());
            }
        }

        void Index(Dictionary<string, List<Face>> index, string name)
        {
            if (!index.TryGetValue(name, out List<Face>? faces)) index[name] = faces = [];
            faces.Add(face);
        }
    }

    private static PdfOpenTypeFont? Load(Source source)
    {
        lock (source)
        {
            if (source.Font is not null || source.Failed) return source.Font;
            try
            {
                source.Font = PdfOpenTypeFont.Load(source.Path!, source.Index);
            }
            catch (Exception e) when (e is FontFormatException or IOException or UnauthorizedAccessException)
            {
                source.Failed = true;
            }

            return source.Font;
        }
    }

    /// <summary>
    /// The faces of a loaded font, with their full and PostScript names: its named instances,
    /// then the font itself (its whole design space when variable).
    /// </summary>
    private static List<(Face Face, string? FullName, string? PostScriptName)> Faces(Source source, PdfOpenTypeFont font)
    {
        var faces = new List<(Face, string?, string?)>();
        if (font.IsVariable && font.Location is null)
        {
            foreach ((string style, string? postScript, double[] values) in font.NamedInstanceLocations)
            {
                faces.Add((new Face(source, font.TraitsAt(values), values), font.FamilyName + " " + style, postScript));
            }
        }

        faces.Add((new Face(source, font.Traits, font.Location), OpenTypeFile.ReadName(font.File.Table("name"), 4), font.Name));
        return faces;
    }


    /// <summary>Names and faces of each font in a file, from its name, OS/2, head, post and fvar tables only.</summary>
    private static List<Scanned> Scan(string path)
    {
        var result = new List<Scanned>();
        using FileStream stream = File.OpenRead(path);
        byte[] header = Read(stream, 0, 12);
        string signature = Tag(header, 0);
        if (signature is "wOFF" or "wOF2")
        {
            // Compressed: the names are only readable after decoding, so the fonts load now. A
            // WOFF2 file may hold a collection.
            stream.Dispose();
            byte[] data = File.ReadAllBytes(path);
            int count = PdfOpenTypeFont.CountFonts(data);
            for (int index = 0; index < count; index++)
            {
                PdfOpenTypeFont font = PdfOpenTypeFont.Load(data, index);
                result.Add(new Scanned(font.FamilyName, null, Faces(new Source(path, index) { Font = font }, font)));
            }

            return result;
        }

        var offsets = new List<long>();
        if (signature == "ttcf")
        {
            uint count = U32(header, 8);
            if (count is 0 or > 0xFFFF) throw new FontFormatException("Font collection is invalid.");
            byte[] table = Read(stream, 12, 4 * (int)count);
            for (int i = 0; i < count; i++) offsets.Add(U32(table, 4 * i));
        }
        else
        {
            offsets.Add(0);
        }

        for (int index = 0; index < offsets.Count; index++)
        {
            byte[] sfnt = Read(stream, offsets[index], 12);
            int tables = U16(sfnt, 4);
            byte[] records = Read(stream, offsets[index] + 12, 16 * tables);
            byte[]? name = null, os2 = null, head = null, post = null, fvar = null;
            for (int t = 0; t < tables; t++)
            {
                string tag = Tag(records, 16 * t);
                uint offset = U32(records, 16 * t + 8), length = U32(records, 16 * t + 12);
                if (length > 4 << 20) continue;
                switch (tag)
                {
                    case "name": name = Read(stream, offset, (int)length); break;
                    case "OS/2": os2 = Read(stream, offset, (int)length); break;
                    case "head": head = Read(stream, offset, (int)length); break;
                    case "post": post = Read(stream, offset, Math.Min((int)length, 32)); break;
                    case "fvar": fvar = Read(stream, offset, (int)length); break;
                }
            }

            if (name is null || head is null) continue;
            string? family = OpenTypeFile.ReadName(name, 1);
            if (family is null) continue;
            string? typographic = OpenTypeFile.ReadName(name, 16);
            FontVariations? variations = fvar is null ? null : FontVariations.Read(fvar, default);
            IReadOnlyList<VariationAxis> axes = variations?.Axes ?? [];
            var source = new Source(path, index);
            var faces = new List<(Face, string?, string?)>();
            foreach (NamedInstance instance in variations?.Instances ?? [])
            {
                string style = OpenTypeFile.ReadName(name, instance.SubfamilyNameId) ?? "Instance";
                string? postScript = instance.PostScriptNameId is > 0 and < 0xFFFF ? OpenTypeFile.ReadName(name, instance.PostScriptNameId) : null;
                faces.Add((new Face(source, FontTraits.Describe(os2, head, post, axes, instance.Coordinates), instance.Coordinates), (typographic ?? family) + " " + style, postScript));
            }

            faces.Add((new Face(source, FontTraits.Describe(os2, head, post, axes, null), null), OpenTypeFile.ReadName(name, 4), OpenTypeFile.ReadName(name, 6)));
            result.Add(new Scanned(family, typographic, faces));
        }

        return result;
    }

    private static byte[] Read(FileStream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > stream.Length) throw new FontFormatException("Font data is truncated.");
        var buffer = new byte[length];
        stream.Position = offset;
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buffer, read, length - read);
            if (n == 0) throw new FontFormatException("Font data is truncated.");
            read += n;
        }

        return buffer;
    }

    private static PdfFontCollection LoadSystemFonts()
    {
        var collection = new PdfFontCollection();
        foreach (string dir in SystemFontDirectories()) collection.AddDirectory(dir);
        return collection;
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }

    /// <summary>One font of a file, loaded when a face of it is first found.</summary>
    private sealed class Source(string? path, int index)
    {
        public string? Path { get; } = path;

        public int Index { get; } = index;

        public PdfOpenTypeFont? Font { get; set; }

        public bool Failed { get; set; }
    }

    /// <summary>
    /// A face: a font, how it matches, and for a named instance or an instance its axis values
    /// (user units, one per axis).
    /// </summary>
    private sealed class Face(Source source, FontTraits traits, IReadOnlyList<double>? location)
    {
        public Source Source { get; } = source;

        public FontTraits Traits { get; } = traits;

        public IReadOnlyList<double>? Location { get; } = location;
    }

    /// <summary>A font of a file: its family names, and its faces with their full and PostScript names.</summary>
    private sealed record Scanned(string Family, string? Typographic, List<(Face Face, string? FullName, string? PostScriptName)> Faces);
}
