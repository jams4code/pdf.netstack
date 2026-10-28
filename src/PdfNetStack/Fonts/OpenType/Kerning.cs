using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Pair kerning: the lookups of every GPOS 'kern' feature (pair adjustment, formats 1 and 2, also
/// inside extension lookups), or the legacy 'kern' table when the font has no GPOS kerning. Only
/// the first glyph's x advance is applied; contextual and mark positioning come with shaping.
/// </summary>
internal sealed class Kerning
{
    private readonly byte[] _data;
    private readonly List<int[]> _lookups;
    private readonly int _kernPairs;
    private readonly int _kernPairCount;

    private Kerning(byte[] data, List<int[]> lookups, int kernPairs, int kernPairCount)
    {
        _data = data;
        _lookups = lookups;
        _kernPairs = kernPairs;
        _kernPairCount = kernPairCount;
    }

    /// <summary>Reads the font's kerning, or returns null when it has none.</summary>
    public static Kerning? Load(OpenTypeFile font)
    {
        try
        {
            List<int[]> lookups = GposLookups(font);
            if (lookups.Count > 0) return new Kerning(font.Data, lookups, -1, 0);
            (int pairs, int count) = KernTable(font);
            return count > 0 ? new Kerning(font.Data, [], pairs, count) : null;
        }
        catch (FontFormatException)
        {
            // Broken kerning data must not make a usable font unusable: draw without kerning.
            return null;
        }
    }

    /// <summary>Adjustment of the advance of <paramref name="left"/> when followed by <paramref name="right"/>, in font units.</summary>
    public int Get(int left, int right)
    {
        try
        {
            return Lookup(left, right);
        }
        catch (FontFormatException)
        {
            // Subtables are read on demand; a damaged one means no kerning for the pair.
            return 0;
        }
    }

    private int Lookup(int left, int right)
    {
        ReadOnlySpan<byte> d = _data;
        if (_kernPairs >= 0) return LegacyPair(d, left, right);

        int total = 0;
        foreach (int[] subtables in _lookups)
        {
            foreach (int subtable in subtables)
            {
                if (PairValue(d, subtable, left, right, out int value))
                {
                    total += value;
                    break;
                }
            }
        }

        return total;
    }

    private int LegacyPair(ReadOnlySpan<byte> d, int left, int right)
    {
        uint key = (uint)(left << 16 | right);
        int lo = 0, hi = _kernPairCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int at = _kernPairs + 6 * mid;
            uint k = U32(d, at);
            if (k == key) return I16(d, at + 4);
            if (k < key) lo = mid + 1;
            else hi = mid - 1;
        }

        return 0;
    }

    /// <summary>
    /// Whether a pair adjustment subtable applies to the pair (ISO/IEC 14496-22, GPOS lookup type
    /// 2), and the x advance it gives the first glyph.
    /// </summary>
    private static bool PairValue(ReadOnlySpan<byte> d, int subtable, int left, int right, out int value)
    {
        value = 0;
        int format = U16(d, subtable);
        int coverage = CoverageIndex(d, subtable + U16(d, subtable + 2), left);
        if (coverage < 0) return false;
        int format1 = U16(d, subtable + 4), format2 = U16(d, subtable + 6);
        int size1 = ValueSize(format1), size2 = ValueSize(format2);
        int advance = (format1 & 0x4) != 0 ? 2 * BitCount(format1 & 0x3) : -1;

        if (format == 1)
        {
            if (coverage >= U16(d, subtable + 8)) return false;
            int set = subtable + U16(d, subtable + 10 + 2 * coverage);
            int count = U16(d, set), record = 2 + size1 + size2;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int at = set + 2 + record * mid;
                int second = U16(d, at);
                if (second == right)
                {
                    if (advance >= 0) value = I16(d, at + 2 + advance);
                    return true;
                }

                if (second < right) lo = mid + 1;
                else hi = mid - 1;
            }

            return false;
        }

        if (format == 2)
        {
            int class1 = ClassOf(d, subtable + U16(d, subtable + 8), left);
            int class2 = ClassOf(d, subtable + U16(d, subtable + 10), right);
            int class1Count = U16(d, subtable + 12), class2Count = U16(d, subtable + 14);
            if (class1 >= class1Count || class2 >= class2Count) return false;
            int at = subtable + 16 + (class1 * class2Count + class2) * (size1 + size2);
            if (advance >= 0) value = I16(d, at + advance);
            return true;
        }

        return false;
    }

    /// <summary>Index of <paramref name="glyph"/> in a coverage table, or -1.</summary>
    internal static int CoverageIndex(ReadOnlySpan<byte> d, int table, int glyph)
    {
        int format = U16(d, table), count = U16(d, table + 2);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (format == 1)
            {
                int g = U16(d, table + 4 + 2 * mid);
                if (g == glyph) return mid;
                if (g < glyph) lo = mid + 1;
                else hi = mid - 1;
            }
            else if (format == 2)
            {
                int at = table + 4 + 6 * mid;
                int start = U16(d, at), end = U16(d, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return U16(d, at + 4) + glyph - start;
            }
            else
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Class of <paramref name="glyph"/> in a class definition table; 0 when not listed.</summary>
    internal static int ClassOf(ReadOnlySpan<byte> d, int table, int glyph)
    {
        int format = U16(d, table);
        if (format == 1)
        {
            int start = U16(d, table + 2), count = U16(d, table + 4);
            return glyph >= start && glyph < start + count ? U16(d, table + 6 + 2 * (glyph - start)) : 0;
        }

        if (format == 2)
        {
            int count = U16(d, table + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int at = table + 4 + 6 * mid;
                int start = U16(d, at), end = U16(d, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return U16(d, at + 4);
            }
        }

        return 0;
    }

    private static int ValueSize(int format) => 2 * BitCount(format & 0xFF);

    private static int BitCount(int v)
    {
        int n = 0;
        for (; v != 0; v &= v - 1) n++;
        return n;
    }

    /// <summary>Absolute offsets of the pair adjustment subtables of each 'kern' lookup, in lookup order.</summary>
    private static List<int[]> GposLookups(OpenTypeFile font)
    {
        var result = new List<int[]>();
        if (!font.HasTable("GPOS")) return result;
        ReadOnlySpan<byte> all = font.Data;
        ReadOnlySpan<byte> gpos = font.Table("GPOS");
        int baseOffset = font.TableOffset("GPOS");
        int features = baseOffset + U16(gpos, 6), lookups = baseOffset + U16(gpos, 8);

        var indices = new SortedSet<int>();
        int featureCount = U16(all, features);
        for (int i = 0; i < featureCount; i++)
        {
            int record = features + 2 + 6 * i;
            if (Tag(all, record) != "kern") continue;
            int feature = features + U16(all, record + 4);
            int n = U16(all, feature + 2);
            for (int k = 0; k < n; k++) indices.Add(U16(all, feature + 4 + 2 * k));
        }

        int lookupCount = U16(all, lookups);
        foreach (int index in indices)
        {
            if (index >= lookupCount) continue;
            int lookup = lookups + U16(all, lookups + 2 + 2 * index);
            int type = U16(all, lookup), count = U16(all, lookup + 4);
            var subtables = new List<int>(count);
            for (int s = 0; s < count; s++)
            {
                int subtable = lookup + U16(all, lookup + 6 + 2 * s);
                int subtableType = type;
                if (type == 9)
                {
                    subtableType = U16(all, subtable + 2);
                    subtable += Offset32(all, subtable + 4);
                }

                if (subtableType == 2) subtables.Add(subtable);
            }

            if (subtables.Count > 0) result.Add([.. subtables]);
        }

        return result;
    }

    /// <summary>The first format 0 horizontal subtable of a version 0 'kern' table: pair array offset and count.</summary>
    private static (int Pairs, int Count) KernTable(OpenTypeFile font)
    {
        ReadOnlySpan<byte> kern = font.Table("kern");
        if (kern.Length < 4 || U16(kern, 0) != 0) return (-1, 0);
        int baseOffset = font.TableOffset("kern");
        int tables = U16(kern, 2), at = 4;
        for (int i = 0; i < tables; i++)
        {
            int length = U16(kern, at + 2), coverage = U16(kern, at + 4);
            if (coverage >> 8 == 0 && (coverage & 0x7) == 0x1)
            {
                int count = U16(kern, at + 6);
                Check(kern, at + 14, 6 * count);
                return (baseOffset + at + 14, count);
            }

            at += length;
        }

        return (-1, 0);
    }
}
