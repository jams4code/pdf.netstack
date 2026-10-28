using System.IO.Compression;
using System.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// Writes PDFs by hand for tests, independent of the library's own writer, so a writer bug
/// cannot hide a reader bug. Object bodies are given as PDF syntax.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly SortedDictionary<int, byte[]> _objects = new();

    public string Version { get; set; } = "1.4";

    public int NextNumber => _objects.Count == 0 ? 1 : _objects.Keys.Max() + 1;

    public int Add(string body) => Set(NextNumber, body);

    public int Set(int number, string body)
    {
        _objects[number] = Encoding.Latin1.GetBytes(body);
        return number;
    }

    public int AddStream(string dictionaryBody, byte[] data) => SetStream(NextNumber, dictionaryBody, data);

    public int SetStream(int number, string dictionaryBody, byte[] data)
    {
        var ms = new MemoryStream();
        Put(ms, $"<<{dictionaryBody}/Length {data.Length}>>\nstream\n");
        ms.Write(data);
        Put(ms, "\nendstream");
        _objects[number] = ms.ToArray();
        return number;
    }

    /// <summary>Writes a classic xref table file.</summary>
    public byte[] Build(string trailerExtra = "", int root = 1)
    {
        var ms = new MemoryStream();
        Put(ms, $"%PDF-{Version}\n%âãÏÓ\n");
        var offsets = WriteObjects(ms);
        long xref = ms.Position;
        int size = (_objects.Count == 0 ? 0 : _objects.Keys.Max()) + 1;
        Put(ms, $"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
        {
            Put(ms, offsets.TryGetValue(n, out long o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        Put(ms, $"trailer\n<</Size {size}/Root {root} 0 R{trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Writes a file with an xref stream; objects listed in <paramref name="packed"/> go into one object stream.</summary>
    public byte[] BuildWithXrefStream(ISet<int>? packed = null, bool predictor = true, int root = 1)
    {
        packed ??= new HashSet<int>();
        var ms = new MemoryStream();
        Put(ms, $"%PDF-1.5\n");
        int max = _objects.Keys.Max();
        int objStm = max + 1;
        int xrefNum = max + 2;
        var entries = new (int Type, long F1, int F2)[xrefNum + 1];
        entries[0] = (0, 0, 65535);

        foreach ((int n, byte[] body) in _objects)
        {
            if (packed.Contains(n)) continue;
            entries[n] = (1, ms.Position, 0);
            Put(ms, $"{n} 0 obj\n");
            ms.Write(body);
            Put(ms, "\nendobj\n");
        }

        if (packed.Count > 0)
        {
            var header = new StringBuilder();
            var bodyStream = new MemoryStream();
            int index = 0;
            foreach (int n in packed.OrderBy(x => x))
            {
                header.Append(n).Append(' ').Append(bodyStream.Position).Append(' ');
                bodyStream.Write(_objects[n]);
                bodyStream.WriteByte((byte)'\n');
                entries[n] = (2, objStm, index++);
            }

            byte[] head = Encoding.ASCII.GetBytes(header.ToString());
            byte[] content = [.. head, .. bodyStream.ToArray()];
            byte[] compressed = Deflate(content);
            entries[objStm] = (1, ms.Position, 0);
            Put(ms, $"{objStm} 0 obj\n<</Type/ObjStm/N {packed.Count}/First {head.Length}/Filter/FlateDecode/Length {compressed.Length}>>\nstream\n");
            ms.Write(compressed);
            Put(ms, "\nendstream\nendobj\n");
        }

        long xrefOffset = ms.Position;
        entries[xrefNum] = (1, xrefOffset, 0);
        const int row = 7;
        var table = new byte[entries.Length * row];
        for (int n = 0; n < entries.Length; n++)
        {
            (int t, long f1, int f2) = entries[n];
            int o = n * row;
            table[o] = (byte)t;
            table[o + 1] = (byte)(f1 >> 24);
            table[o + 2] = (byte)(f1 >> 16);
            table[o + 3] = (byte)(f1 >> 8);
            table[o + 4] = (byte)f1;
            table[o + 5] = (byte)(f2 >> 8);
            table[o + 6] = (byte)f2;
        }

        string parms = "";
        if (predictor)
        {
            // PNG Up predictor on every row.
            var predicted = new byte[entries.Length * (row + 1)];
            for (int r = 0; r < entries.Length; r++)
            {
                predicted[r * (row + 1)] = 2;
                for (int i = 0; i < row; i++)
                {
                    byte above = r == 0 ? (byte)0 : table[(r - 1) * row + i];
                    predicted[r * (row + 1) + 1 + i] = (byte)(table[r * row + i] - above);
                }
            }

            table = predicted;
            parms = "/DecodeParms<</Predictor 12/Columns 7>>";
        }

        byte[] data = Deflate(table);
        Put(ms, $"{xrefNum} 0 obj\n<</Type/XRef/Size {xrefNum + 1}/W[1 4 2]/Root {root} 0 R/Filter/FlateDecode{parms}/Length {data.Length}>>\nstream\n");
        ms.Write(data);
        Put(ms, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Appends an incremental update replacing or adding the given objects.</summary>
    public static byte[] AppendUpdate(byte[] original, IReadOnlyDictionary<int, string> objects, int size, int root = 1)
    {
        string text = Encoding.Latin1.GetString(original);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(at + 9)..].Trim().Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);

        var ms = new MemoryStream();
        ms.Write(original);
        var offsets = new SortedDictionary<int, long>();
        foreach ((int n, string body) in objects)
        {
            offsets[n] = ms.Position;
            Put(ms, $"{n} 0 obj\n{body}\nendobj\n");
        }

        long xref = ms.Position;
        Put(ms, "xref\n");
        foreach ((int n, long o) in offsets) Put(ms, $"{n} 1\n{o:D10} 00000 n \n");
        Put(ms, $"trailer\n<</Size {size}/Root {root} 0 R/Prev {prev}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private Dictionary<int, long> WriteObjects(MemoryStream ms)
    {
        var offsets = new Dictionary<int, long>();
        foreach ((int n, byte[] body) in _objects)
        {
            offsets[n] = ms.Position;
            Put(ms, $"{n} 0 obj\n");
            ms.Write(body);
            Put(ms, "\nendobj\n");
        }

        return offsets;
    }

    public static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void Put(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

    /// <summary>A small valid document: catalog 1, pages 2, then one page, content and font per page.</summary>
    public static PdfBuilder Simple(params string[] pageTexts)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        int font = 3;
        b.Set(font, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        var kids = new List<int>();
        int next = 4;
        foreach (string text in pageTexts)
        {
            int content = next++;
            int page = next++;
            b.SetStream(content, "", Encoding.ASCII.GetBytes($"BT /F1 24 Tf 72 700 Td ({text}) Tj ET"));
            b.Set(page, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 {font} 0 R>>>>/Contents {content} 0 R>>");
            kids.Add(page);
        }

        b.Set(2, $"<</Type/Pages/Kids[{string.Join(" ", kids.Select(k => k + " 0 R"))}]/Count {kids.Count}>>");
        return b;
    }
}
