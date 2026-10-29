using System.Text;
using PdfNetStack.Drawing;

namespace PdfNetStack.Html;

/// <summary>
/// Fetches style sheets, images and fonts for a conversion. By default only data: URIs and
/// files inside the base directory load: nothing is fetched from the network unless the caller
/// supplies a loader, so converting untrusted HTML cannot reach internal services or read files
/// elsewhere on the machine.
/// </summary>
internal sealed class Resources
{
    private const int MaxResourceBytes = 64 << 20;

    private readonly Uri? _base;
    private readonly Func<Uri, byte[]?>? _loader;
    private readonly string? _directory;
    private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);

    public Resources(Uri? baseUri, Func<Uri, byte[]?>? loader)
    {
        _base = baseUri;
        _loader = loader;
        if (baseUri is { IsAbsoluteUri: true, IsFile: true })
        {
            string path = Path.GetFullPath(baseUri.LocalPath);
            _directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (_directory is not null && !_directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) _directory += Path.DirectorySeparatorChar;
        }
    }

    /// <summary>The bytes behind a URL from the document, or null when it cannot or may not be loaded.</summary>
    public byte[]? Load(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return null;
        if (_bytes.TryGetValue(url, out byte[]? cached)) return cached;
        byte[]? data = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DataUri(url) : Fetch(url);
        if (data is { Length: > MaxResourceBytes }) data = null;
        _bytes[url] = data;
        return data;
    }

    private byte[]? Fetch(string url)
    {
        Uri? uri = Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute) ? absolute
            : _base is not null && Uri.TryCreate(_base, url, out Uri? relative) ? relative
            : null;
        if (uri is null) return null;
        if (_loader is not null) return _loader(uri);
        if (!uri.IsFile || _directory is null) return null;

        // Only files under the base directory, after resolving any ".." in the path.
        string path = Path.GetFullPath(uri.LocalPath);
        if (!path.StartsWith(_directory, OperatingSystem() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxResourceBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool OperatingSystem() => Path.DirectorySeparatorChar == '\\';

    /// <summary>A data: URI (RFC 2397): base64 or percent-encoded.</summary>
    internal static byte[]? DataUri(string url)
    {
        int comma = url.IndexOf(',');
        if (comma < 0) return null;
        string header = url.Substring(5, comma - 5);
        string payload = url.Substring(comma + 1);
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Convert.FromBase64String(Uri.UnescapeDataString(payload).Replace(" ", "").Replace("\n", "").Replace("\r", ""));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    /// <summary>A style sheet's text, decoded as UTF-8 unless a byte order mark says otherwise.</summary>
    public string? LoadText(string url)
    {
        byte[]? data = Load(url);
        if (data is null) return null;
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>An image, or null when it is missing or not a JPEG or PNG the library reads.</summary>
    public PdfImage? LoadImage(string url)
    {
        if (_images.TryGetValue(url, out PdfImage? cached)) return cached;
        PdfImage? image = null;
        if (Load(url) is byte[] data)
        {
            try
            {
                image = PdfImage.Load(data);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException or InvalidDataException or EndOfStreamException)
            {
                image = null;
            }
        }

        _images[url] = image;
        return image;
    }
}
