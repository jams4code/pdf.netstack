using System.Text;
using PdfNetStack.Fonts.OpenType;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// The predefined CJK CMaps (ISO 32000-1, 9.7.5.2, Table 118, and the rest of Adobe's set for the
/// Adobe-CNS1, Adobe-GB1, Adobe-Japan1, Adobe-Korea1 and Adobe-KR collections) and the CID to
/// Unicode mapping of each collection (9.10.2), from Adobe's cmap-resources and
/// mapping-resources-pdf.
/// </summary>
/// <remarks>
/// Each collection ships as one Brotli stream, embedded as CMaps.&lt;ordering&gt;.br and laid out
/// as tools/make_cjk_cmaps.py describes. A stream is decompressed when one of its CMaps or its CID
/// to Unicode table is first needed; the CMaps and tables read from it are kept for the life of the
/// process and shared, read only, by every document. The decompressed stream itself is only held
/// weakly, so it can be dropped once nothing more is read from it.
/// </remarks>
internal static partial class PredefinedCMaps
{
    private static readonly object Sync = new();
    private static readonly Dictionary<int, CMap> Loaded = [];
    private static readonly CidText?[] Texts = new CidText?[CollectionCount];
    private static readonly WeakReference<byte[]>?[] Decompressed = new WeakReference<byte[]>?[CollectionCount];

    /// <summary>The predefined CMap of this name, or null when it is not one of Adobe's CJK CMaps.</summary>
    public static CMap? Get(string name)
    {
        int index = Array.BinarySearch(Names, name, StringComparer.Ordinal);
        if (index < 0) return null;
        lock (Sync) return Load(index);
    }

    /// <summary>
    /// The CID to Unicode mapping of an Adobe character collection, by its /Ordering (Japan1, GB1,
    /// CNS1, Korea1 or KR); null for any other.
    /// </summary>
    public static CidText? ForOrdering(string? ordering)
    {
        int collection = ordering is null ? -1 : Array.IndexOf(Collections, ordering);
        if (collection < 0) return null;
        lock (Sync)
        {
            if (Texts[collection] is CidText cached) return cached;
            (_, int at, int inverse, int maxCid) = Layout[collection];
            var text = new CidText(Data(collection), at, Load(inverse), maxCid);
            Texts[collection] = text;
            return text;
        }
    }

    /// <summary>Reads a CMap and its parents. Called under the lock.</summary>
    private static CMap Load(int index)
    {
        if (Loaded.TryGetValue(index, out CMap? cmap)) return cmap;
        (byte collection, int offset) = Records[index];
        cmap = CMap.Packed(Data(collection), offset, Collections[collection], Load);
        Loaded[index] = cmap;
        return cmap;
    }

    /// <summary>A collection's decompressed stream. Called under the lock.</summary>
    private static byte[] Data(int collection)
    {
        if (Decompressed[collection] is { } weak && weak.TryGetTarget(out byte[]? kept)) return kept;
        string resource = "PdfNetStack.CMaps." + Collections[collection] + ".br";
        using Stream? stream = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The resource {resource} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] data = Brotli.Decompress(buffer.ToArray(), Layout[collection].Length);
        Decompressed[collection] = new WeakReference<byte[]>(data);
        return data;
    }

    /// <summary>Reads an unsigned LEB128 number of the packed data.</summary>
    internal static int Varint(ReadOnlySpan<byte> data, ref int at)
    {
        int value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[at++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
    }

    /// <summary>Reads a signed (zigzag) number of the packed data.</summary>
    internal static int Zigzag(ReadOnlySpan<byte> data, ref int at)
    {
        int v = Varint(data, ref at);
        return (v >>> 1) ^ -(v & 1);
    }

    /// <summary>
    /// A collection's CIDs to Unicode (the Adobe-&lt;ordering&gt;-UCS2 CMap): the collection's
    /// UTF-32 CMap inverted, each CID taking the lowest code point that maps to it, then corrected
    /// by the packed table of differences.
    /// </summary>
    internal sealed class CidText
    {
        private readonly int[] _scalars;
        private readonly Dictionary<int, string> _sequences = [];

        public CidText(byte[] data, int at, CMap inverse, int maxCid)
        {
            _scalars = new int[maxCid + 1];
            inverse.Invert(_scalars);
            ReadOnlySpan<byte> d = data;
            int runs = Varint(d, ref at), cid = 0, scalar = 0;
            for (int r = 0; r < runs; r++)
            {
                cid += Varint(d, ref at);
                int header = Varint(d, ref at);
                if ((header & 1) != 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i <= header >> 1; i++) sb.Append(char.ConvertFromUtf32(Varint(d, ref at)));
                    _sequences[cid] = sb.ToString();
                    _scalars[cid] = 0;
                    cid++;
                    continue;
                }

                for (int i = 0; i <= header >> 1; i++, cid++)
                {
                    scalar += Zigzag(d, ref at);
                    _scalars[cid] = scalar;
                    scalar++;
                }
            }
        }

        /// <summary>The text of a CID, or null when the collection gives it none.</summary>
        public string? Text(int cid)
        {
            if (cid <= 0 || cid >= _scalars.Length) return null;
            if (_scalars[cid] is int s and > 0) return char.ConvertFromUtf32(s);
            return _sequences.TryGetValue(cid, out string? text) ? text : null;
        }
    }
}
