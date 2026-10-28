using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Subsets a TrueType outline font for embedding as a CIDFontType2. Glyph ids do not change, so
/// content written before the subset was known stays valid. The font ends at the highest glyph
/// used; below it, unused glyphs have no outline and zero metrics, which Flate reduces to almost
/// nothing. Hinting programs are kept.
/// </summary>
internal static class TrueTypeSubsetter
{
    // Tables a PDF viewer needs to render a CIDFontType2 (ISO 32000-1, 9.9), plus OS/2 for metrics.
    private static readonly string[] Copied = ["cvt ", "fpgm", "prep", "OS/2"];

    /// <summary>The glyphs plus .notdef and every component of a composite glyph, transitively.</summary>
    public static SortedSet<int> Closure(OpenTypeFile font, IEnumerable<int> glyphs)
    {
        var set = new SortedSet<int> { 0 };
        var pending = new Stack<int>();
        foreach (int g in glyphs)
        {
            if (g >= 0 && g < font.GlyphCount && set.Add(g)) pending.Push(g);
        }

        ReadOnlySpan<byte> glyf = font.Table("glyf");
        while (pending.Count > 0)
        {
            (int start, int end) = font.GlyphRange(pending.Pop());
            if (end - start < 10 || I16(glyf, start) >= 0) continue;

            // A composite: walk its component records (ISO/IEC 14496-22, glyf).
            int at = start + 10;
            int flags;
            do
            {
                flags = U16(glyf, at);
                int component = U16(glyf, at + 2);
                if (component < font.GlyphCount && set.Add(component)) pending.Push(component);
                at += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) at += 2;
                else if ((flags & 0x0040) != 0) at += 4;
                else if ((flags & 0x0080) != 0) at += 8;
                if (at > end) throw new FontFormatException("Composite glyph is truncated.");
            }
            while ((flags & 0x0020) != 0);
        }

        return set;
    }

    /// <summary>Builds the subset font: outlines for <paramref name="keep"/> only, same glyph ids.</summary>
    public static byte[] Subset(OpenTypeFile font, SortedSet<int> keep)
    {
        ReadOnlySpan<byte> glyf = font.Table("glyf");
        int count = keep.Max + 1;
        var newGlyf = new FontWriter(glyf.Length / 4);
        var loca = new FontWriter(4 * (count + 1));
        for (int g = 0; g < count; g++)
        {
            loca.U32((uint)newGlyf.Length);
            if (!keep.Contains(g)) continue;
            (int start, int end) = font.GlyphRange(g);
            newGlyf.Bytes(glyf.Slice(start, end - start));
            newGlyf.Pad(4);
        }

        loca.U32((uint)newGlyf.Length);

        byte[] head = font.Table("head").ToArray();
        head[50] = 0;
        head[51] = 1; // indexToLocFormat: long offsets

        byte[] maxp = font.Table("maxp").ToArray();
        maxp[4] = (byte)(count >> 8);
        maxp[5] = (byte)count;
        (byte[] hhea, byte[] hmtx) = Metrics(font, keep, count);

        var tables = new List<(string Tag, uint Checksum, byte[] Data)>
        {
            ("head", 0, head),
            ("hhea", 0, hhea),
            ("hmtx", 0, hmtx),
            ("maxp", 0, maxp),
            ("loca", 0, loca.ToArray()),
            ("glyf", 0, newGlyf.ToArray()),
        };
        foreach (string tag in Copied)
        {
            if (font.HasTable(tag)) tables.Add((tag, 0, font.Table(tag).ToArray()));
        }

        // post format 3: the metrics without glyph names.
        ReadOnlySpan<byte> post = font.Table("post");
        if (post.Length >= 32)
        {
            byte[] p = post.Slice(0, 32).ToArray();
            p[0] = 0;
            p[1] = 3;
            p[2] = 0;
            p[3] = 0;
            tables.Add(("post", 0, p));
        }

        return SfntBuilder.Build(0x00010000, tables);
    }

    /// <summary>
    /// hhea and hmtx for the first <paramref name="count"/> glyphs, with the metrics of unused glyphs
    /// zeroed. The last full entry keeps its advance: glyphs after it inherit it.
    /// </summary>
    private static (byte[] Hhea, byte[] Hmtx) Metrics(OpenTypeFile font, SortedSet<int> keep, int count)
    {
        ReadOnlySpan<byte> hmtx = font.Table("hmtx");
        int full = font.NumberOfHMetrics;
        int newFull = Math.Min(full, count);
        var w = new FontWriter(4 * count);
        for (int g = 0; g < count; g++)
        {
            bool used = keep.Contains(g);
            short lsb = !used ? (short)0 : g < full ? I16(hmtx, 4 * g + 2) : I16(hmtx, 4 * full + 2 * (g - full));
            if (g < newFull)
            {
                w.U16(used || g == newFull - 1 ? font.Advance(g) : 0);
            }

            w.U16((ushort)lsb);
        }

        byte[] hhea = font.Table("hhea").ToArray();
        hhea[34] = (byte)(newFull >> 8);
        hhea[35] = (byte)newFull;
        return (hhea, w.ToArray());
    }
}
