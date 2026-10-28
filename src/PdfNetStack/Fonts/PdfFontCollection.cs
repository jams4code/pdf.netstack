using System.Runtime.InteropServices;
using PdfNetStack.Fonts.OpenType;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts;

/// <summary>
/// Fonts found by family name, weight and style, the way CSS matches them. Add font files or
/// directories, or use <see cref="System"/> for the fonts installed on the machine. Adding a file
/// reads only its names and style; the font itself loads when first found. Thread safe.
/// </summary>
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
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc", ".woff"];
    private static readonly Lazy<PdfFontCollection> SystemFonts = new(LoadSystemFonts);

    private readonly object _lock = new();
    private readonly Dictionary<string, List<Face>> _families = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Adds a loaded font under its family name.</summary>
    public void Add(PdfOpenTypeFont font)
    {
        Compat.ThrowIfNull(font, nameof(font));
        Register(new Face(null, 0, font.Weight, font.IsItalic) { Font = font }, font.FamilyName, null);
    }

    /// <summary>Adds every font of a file: one, or all fonts of a collection. Returns how many were added.</summary>
    /// <exception cref="FontFormatException">The file is not a supported font.</exception>
    public int AddFile(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        int added = 0;
        foreach ((Face face, string family, string? typographic) in Scan(path))
        {
            Register(face, family, typographic);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Adds the fonts in a directory with a font extension (.ttf, .otf, .ttc, .otc, .woff).
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
    /// CSS does (CSS Fonts Level 4, 5.2): for weights 400 to 500, heavier up to 500 first, then
    /// lighter, then heavier; below 400, lighter first; above 500, heavier first. An italic is
    /// preferred when asked for, an upright face otherwise. Null when the family is unknown.
    /// </summary>
    /// <param name="family">Family name, case insensitive, such as "Inter" or "Times New Roman".</param>
    /// <param name="weight">CSS weight, 1 to 1000: 400 regular, 700 bold.</param>
    /// <param name="italic">True to prefer an italic or oblique face.</param>
    public PdfOpenTypeFont? Find(string family, int weight = 400, bool italic = false)
    {
        Compat.ThrowIfNull(family, nameof(family));
        if (weight is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(weight), "CSS weights range from 1 to 1000.");
        List<Face> candidates;
        lock (_lock)
        {
            if (!_families.TryGetValue(family.Trim(), out List<Face>? faces)) return null;
            candidates = [.. faces];
        }

        foreach (Face face in candidates.OrderBy(f => f.Italic == italic ? 0 : 1).ThenBy(f => WeightRank(weight, f.Weight)))
        {
            PdfOpenTypeFont? font = Load(face);
            if (font is not null) return font;
        }

        return null;
    }

    /// <summary>Order of a face's weight for a requested weight; lower is better.</summary>
    internal static int WeightRank(int wanted, int weight)
    {
        int distance = Math.Abs(weight - wanted);
        if (weight == wanted) return 0;
        if (wanted is >= 400 and <= 500)
        {
            if (weight > wanted && weight <= 500) return 1000 + distance;
            if (weight < wanted) return 2000 + distance;
            return 3000 + distance;
        }

        bool preferred = wanted < 400 ? weight < wanted : weight > wanted;
        return (preferred ? 1000 : 2000) + distance;
    }

    private void Register(Face face, string family, string? typographic)
    {
        lock (_lock)
        {
            // Typographic family (name ID 16) groups weights that the legacy family (ID 1) splits,
            // such as "Inter Medium" under "Inter"; a face is found under both.
            foreach (string? name in new[] { family, typographic })
            {
                if (string.IsNullOrWhiteSpace(name) || (name == typographic && string.Equals(family, typographic, StringComparison.OrdinalIgnoreCase))) continue;
                if (!_families.TryGetValue(name!, out List<Face>? faces)) _families[name!] = faces = [];
                faces.Add(face);
            }
        }
    }

    private PdfOpenTypeFont? Load(Face face)
    {
        lock (face)
        {
            if (face.Font is not null || face.Failed) return face.Font;
            try
            {
                face.Font = PdfOpenTypeFont.Load(face.Path!, face.Index);
            }
            catch (Exception e) when (e is FontFormatException or IOException or UnauthorizedAccessException)
            {
                face.Failed = true;
            }

            return face.Font;
        }
    }

    /// <summary>Family, weight and style of each font in a file, from its name, OS/2 and head tables only.</summary>
    private static List<(Face Face, string Family, string? Typographic)> Scan(string path)
    {
        var result = new List<(Face, string, string?)>();
        using FileStream stream = File.OpenRead(path);
        byte[] header = Read(stream, 0, 12);
        string signature = Tag(header, 0);
        if (signature is "wOFF" or "wOF2")
        {
            stream.Dispose();
            PdfOpenTypeFont font = PdfOpenTypeFont.Load(path);
            result.Add((new Face(path, 0, font.Weight, font.IsItalic) { Font = font }, font.FamilyName, null));
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
            byte[]? name = null, os2 = null, head = null;
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
                }
            }

            if (name is null || head is null) continue;
            string? family = OpenTypeFile.ReadName(name, 1);
            if (family is null) continue;
            int macStyle = U16(head, 44);
            int weight = os2 is { Length: >= 64 } && U16(os2, 4) is >= 1 and <= 1000 and var w ? w : (macStyle & 1) != 0 ? 700 : 400;
            bool italic = (os2 is { Length: >= 64 } && (U16(os2, 62) & 0x201) != 0) || (macStyle & 2) != 0;
            result.Add((new Face(path, index, weight, italic), family, OpenTypeFile.ReadName(name, 16)));
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

    /// <summary>One font: where it is, how it matches, and the font once loaded.</summary>
    private sealed class Face(string? path, int index, int weight, bool italic)
    {
        public string? Path { get; } = path;

        public int Index { get; } = index;

        public int Weight { get; } = weight;

        public bool Italic { get; } = italic;

        public PdfOpenTypeFont? Font { get; set; }

        public bool Failed { get; set; }
    }
}
