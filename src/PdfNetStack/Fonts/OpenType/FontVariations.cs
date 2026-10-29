using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>A design axis of a variable font, from fvar: values in user units (a weight of 400, a width of 100).</summary>
internal sealed record VariationAxis(string Tag, double Minimum, double Default, double Maximum, int NameId, bool Hidden);

/// <summary>A named instance from fvar: its style name, optional PostScript name and axis values.</summary>
internal sealed record NamedInstance(int SubfamilyNameId, int PostScriptNameId, double[] Coordinates);

/// <summary>
/// The axes and named instances of a variable font (fvar), and the mapping of axis values to
/// normalized coordinates, through the axis variations table (avar) when the font has one.
/// </summary>
internal sealed class FontVariations
{
    /// <summary>Axes beyond this are not read; real fonts have a handful.</summary>
    private const int MaxAxes = 64;

    private readonly (double From, double To)[][] _segments;

    private FontVariations(List<VariationAxis> axes, List<NamedInstance> instances, (double, double)[][] segments)
    {
        Axes = axes;
        Instances = instances;
        _segments = segments;
    }

    public IReadOnlyList<VariationAxis> Axes { get; }

    public IReadOnlyList<NamedInstance> Instances { get; }

    /// <summary>The font's axes and instances, or null when it is not a variable font.</summary>
    /// <exception cref="FontFormatException">The fvar table is damaged.</exception>
    public static FontVariations? Load(OpenTypeFile font) => Read(font.Table("fvar"), font.Table("avar"));

    /// <summary>The axes and instances of an fvar table, with avar's mappings; null when there is no fvar.</summary>
    /// <exception cref="FontFormatException">The fvar table is damaged.</exception>
    public static FontVariations? Read(ReadOnlySpan<byte> fvar, ReadOnlySpan<byte> avar)
    {
        if (fvar.Length == 0) return null;
        int axesAt = U16(fvar, 4), axisCount = U16(fvar, 8), axisSize = U16(fvar, 10);
        int instanceCount = U16(fvar, 12), instanceSize = U16(fvar, 14);
        if (axisCount == 0) return null;
        if (axisCount > MaxAxes || axisSize < 20 || instanceSize < 4 + 4 * axisCount) throw new FontFormatException("Font fvar table is invalid.");

        var axes = new List<VariationAxis>(axisCount);
        for (int i = 0; i < axisCount; i++)
        {
            int a = axesAt + i * axisSize;
            double min = Fixed(fvar, a + 4), def = Fixed(fvar, a + 8), max = Fixed(fvar, a + 12);
            if (min > def || def > max) throw new FontFormatException("Font fvar axis range is invalid.");
            axes.Add(new VariationAxis(Tag(fvar, a), min, def, max, U16(fvar, a + 18), (U16(fvar, a + 16) & 1) != 0));
        }

        var instances = new List<NamedInstance>(instanceCount);
        int at = axesAt + axisCount * axisSize;
        for (int i = 0; i < instanceCount; i++, at += instanceSize)
        {
            var coordinates = new double[axisCount];
            for (int k = 0; k < axisCount; k++) coordinates[k] = Fixed(fvar, at + 4 + 4 * k);
            int postScript = instanceSize >= 6 + 4 * axisCount ? U16(fvar, at + 4 + 4 * axisCount) : 0xFFFF;
            instances.Add(new NamedInstance(U16(fvar, at), postScript, coordinates));
        }

        return new FontVariations(axes, instances, ReadAvar(avar, axisCount));
    }

    /// <summary>
    /// Normalized coordinates (-1 to 1, 0 at the defaults) for axis values in user units, clamped
    /// to each axis's range, mapped through avar and rounded to 2.14 as fonts store them.
    /// </summary>
    public float[] Normalize(IReadOnlyList<double> values)
    {
        var coordinates = new float[Axes.Count];
        for (int i = 0; i < coordinates.Length; i++)
        {
            VariationAxis axis = Axes[i];
            double v = Math.Max(axis.Minimum, Math.Min(axis.Maximum, values[i]));
            double n = v < axis.Default ? (v - axis.Default) / (axis.Default - axis.Minimum)
                : v > axis.Default ? (v - axis.Default) / (axis.Maximum - axis.Default)
                : 0;
            n = Map(_segments[i], Math.Max(-1, Math.Min(1, n)));
            coordinates[i] = (float)ToF2Dot14(n);
        }

        return coordinates;
    }

    private static double ToF2Dot14(double v) => Math.Round(Math.Max(-1, Math.Min(1, v)) * 16384) / 16384;

    /// <summary>Piecewise linear mapping through an axis's avar segments; identity without them.</summary>
    private static double Map((double From, double To)[] segments, double v)
    {
        if (segments.Length == 0) return v;
        if (v <= segments[0].From) return segments[0].To + (v - segments[0].From);
        for (int k = 1; k < segments.Length; k++)
        {
            (double from, double to) = segments[k];
            if (v > from) continue;
            (double prevFrom, double prevTo) = segments[k - 1];
            return from == prevFrom ? to : prevTo + (to - prevTo) * (v - prevFrom) / (from - prevFrom);
        }

        return segments[^1].To + (v - segments[^1].From);
    }

    /// <summary>The segment maps of avar version 1, one per axis; empty when the font has none or they are malformed.</summary>
    private static (double, double)[][] ReadAvar(ReadOnlySpan<byte> avar, int axisCount)
    {
        var maps = new (double, double)[axisCount][];
        for (int i = 0; i < axisCount; i++) maps[i] = [];
        if (avar.Length < 8 || U16(avar, 0) is not (1 or 2) || U16(avar, 6) != axisCount) return maps;
        int at = 8;
        for (int i = 0; i < axisCount; i++)
        {
            int count = U16(avar, at);
            var map = new (double, double)[count];
            for (int k = 0; k < count; k++) map[k] = (F2Dot14(avar, at + 2 + 4 * k), F2Dot14(avar, at + 4 + 4 * k));
            at += 2 + 4 * count;

            // The spec requires ascending maps through -1, 0 and 1; others are ignored as HarfBuzz does.
            bool valid = count >= 3;
            for (int k = 1; k < count && valid; k++) valid = map[k].Item1 >= map[k - 1].Item1;
            maps[i] = valid ? map : [];
        }

        return maps;
    }

    public static double Fixed(ReadOnlySpan<byte> d, int at) => (int)U32(d, at) / 65536.0;

    public static double F2Dot14(ReadOnlySpan<byte> d, int at) => I16(d, at) / 16384.0;

    /// <summary>
    /// The scalar of a region (or tuple) at <paramref name="coordinates"/>: the product over axes
    /// of how far the coordinate is towards the peak, 0 outside the region.
    /// </summary>
    public static double Scalar(float[] coordinates, ReadOnlySpan<double> start, ReadOnlySpan<double> peak, ReadOnlySpan<double> end)
    {
        double scalar = 1;
        for (int i = 0; i < peak.Length; i++)
        {
            double p = peak[i];
            if (p == 0) continue;
            double s = start[i], e = end[i];
            if (s > p || p > e || (s < 0 && e > 0)) continue;
            double v = i < coordinates.Length ? coordinates[i] : 0;
            if (v == p) continue;
            if (v <= s || v >= e) return 0;
            scalar *= v < p ? (v - s) / (p - s) : (e - v) / (e - p);
        }

        return scalar;
    }
}

/// <summary>
/// An item variation store (OpenType common table formats): regions of the design space and rows
/// of deltas, used by HVAR, MVAR, GDEF and CFF2.
/// </summary>
internal sealed class ItemVariationStore
{
    private readonly byte[] _data;
    private readonly int _axisCount;
    private readonly int[] _regions;
    private readonly int[] _sets;

    // Region scalars by location (the coordinate array itself) and data subtable.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(float[] Coordinates, int Outer), double[]> _scalarCache = new();

    private ItemVariationStore(byte[] data, int axisCount, int[] regions, int[] sets)
    {
        _data = data;
        _axisCount = axisCount;
        _regions = regions;
        _sets = sets;
    }

    /// <summary>Number of item variation data subtables.</summary>
    public int DataCount => _sets.Length;

    /// <summary>Reads the store at <paramref name="at"/> in <paramref name="data"/>.</summary>
    /// <exception cref="FontFormatException">The store is damaged.</exception>
    public static ItemVariationStore Read(byte[] data, int at)
    {
        ReadOnlySpan<byte> d = data;
        if (U16(d, at) != 1) throw new FontFormatException("Item variation store format is not supported.");
        int list = at + Offset32(d, at + 2);
        int axisCount = U16(d, list), regionCount = U16(d, list + 2);
        if (axisCount > 64) throw new FontFormatException("Item variation store has too many axes.");
        Check(d, list + 4, regionCount * axisCount * 6);
        var regions = new int[regionCount];
        for (int r = 0; r < regionCount; r++) regions[r] = list + 4 + r * axisCount * 6;
        int count = U16(d, at + 6);
        var sets = new int[count];
        for (int i = 0; i < count; i++)
        {
            int offset = Offset32(d, at + 8 + 4 * i);
            sets[i] = offset == 0 ? -1 : at + offset;
        }

        return new ItemVariationStore(data, axisCount, regions, sets);
    }

    /// <summary>The scalar of each region an item variation data subtable uses, in its order.</summary>
    public double[] Scalars(int dataIndex, float[] coordinates)
    {
        ReadOnlySpan<byte> d = _data;
        if ((uint)dataIndex >= (uint)_sets.Length || _sets[dataIndex] < 0) return [];
        int set = _sets[dataIndex];
        int regionIndexCount = U16(d, set + 4);
        var scalars = new double[regionIndexCount];
        Span<double> start = stackalloc double[_axisCount], peak = stackalloc double[_axisCount], end = stackalloc double[_axisCount];
        for (int k = 0; k < regionIndexCount; k++)
        {
            int region = U16(d, set + 6 + 2 * k);
            if (region >= _regions.Length) throw new FontFormatException("Item variation region index is out of range.");
            int r = _regions[region];
            for (int a = 0; a < _axisCount; a++)
            {
                start[a] = FontVariations.F2Dot14(d, r + 6 * a);
                peak[a] = FontVariations.F2Dot14(d, r + 6 * a + 2);
                end[a] = FontVariations.F2Dot14(d, r + 6 * a + 4);
            }

            scalars[k] = FontVariations.Scalar(coordinates, start, peak, end);
        }

        return scalars;
    }

    /// <summary>The interpolated delta of item <paramref name="inner"/> of data subtable <paramref name="outer"/>; 0 when out of range.</summary>
    public double Delta(int outer, int inner, float[] coordinates)
    {
        ReadOnlySpan<byte> d = _data;
        if ((uint)outer >= (uint)_sets.Length || _sets[outer] < 0) return 0;
        int set = _sets[outer];
        int itemCount = U16(d, set), wordCount = U16(d, set + 2), regionIndexCount = U16(d, set + 4);
        if (inner >= itemCount) return 0;
        bool longWords = (wordCount & 0x8000) != 0;
        wordCount &= 0x7FFF;
        if (wordCount > regionIndexCount) throw new FontFormatException("Item variation data is invalid.");
        int large = longWords ? 4 : 2, small = longWords ? 2 : 1;
        int rowSize = wordCount * large + (regionIndexCount - wordCount) * small;
        int row = set + 6 + 2 * regionIndexCount + inner * rowSize;
        Check(d, row, rowSize);
        double[] scalars = _scalarCache.GetOrAdd((coordinates, outer), key => Scalars(key.Outer, key.Coordinates));
        double delta = 0;
        for (int k = 0, at = row; k < regionIndexCount; k++)
        {
            int value;
            if (k < wordCount)
            {
                value = longWords ? (int)U32(d, at) : I16(d, at);
                at += large;
            }
            else
            {
                value = longWords ? I16(d, at) : (sbyte)U8(d, at);
                at += small;
            }

            if (scalars[k] != 0) delta += scalars[k] * value;
        }

        return delta;
    }
}

/// <summary>A delta set index map (HVAR, VVAR): from a glyph or item to an outer and inner index.</summary>
internal readonly struct DeltaSetIndexMap
{
    private readonly byte[]? _data;
    private readonly int _at;

    private DeltaSetIndexMap(byte[] data, int at)
    {
        _data = data;
        _at = at;
    }

    /// <summary>The map at <paramref name="offset"/> from <paramref name="table"/>, or an identity map (outer 0, inner the index) when the offset is 0.</summary>
    public static DeltaSetIndexMap Read(byte[] data, int table, int offset) => offset == 0 ? default : new DeltaSetIndexMap(data, table + offset);

    public bool IsIdentity => _data is null;

    public (int Outer, int Inner) Map(int index)
    {
        if (_data is null) return (0, index);
        ReadOnlySpan<byte> d = _data;
        int format = U8(d, _at), entryFormat = U8(d, _at + 1);
        long count = format == 0 ? U16(d, _at + 2) : U32(d, _at + 2);
        int data = _at + (format == 0 ? 4 : 6);
        if (count == 0) return (0, index);
        long i = Math.Min(index, count - 1);
        int size = ((entryFormat >> 4) & 3) + 1, innerBits = (entryFormat & 0xF) + 1;
        int at = (int)Math.Min(int.MaxValue, data + i * size);
        int entry = 0;
        for (int k = 0; k < size; k++) entry = entry << 8 | U8(d, at + k);
        return (entry >> innerBits, entry & ((1 << innerBits) - 1));
    }
}

/// <summary>
/// Tuple variation stores (gvar and cvar): each tuple has a region of the design space, the
/// points it moves and their deltas, packed as the OpenType specification describes.
/// </summary>
internal static class TupleVariations
{
    private const int EmbeddedPeak = 0x8000, IntermediateRegion = 0x4000, PrivatePoints = 0x2000, SharedPoints = 0x8000;

    /// <summary>
    /// Adds the deltas of one glyph's (or the cvt's) variation data to <paramref name="dx"/> and
    /// <paramref name="dy"/>, scaled for <paramref name="coordinates"/>. Tuples that name only some
    /// points are reported through <paramref name="partial"/>, which interpolates the others (IUP
    /// in gvar); without it, unnamed points do not move. <paramref name="dy"/> is null for cvar.
    /// The serialized data starts at <paramref name="dataBase"/> plus the header's data offset.
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> d, int at, int dataBase, int axisCount, ReadOnlySpan<byte> sharedTuples, float[] coordinates,
        double[] dx, double[]? dy, Action<bool[], double[], double[]?>? partial)
    {
        int pointCount = dx.Length;
        int header = U16(d, at);
        int tupleCount = header & 0x0FFF;
        int data = dataBase + U16(d, at + 2);
        int[]? shared = null;
        if ((header & SharedPoints) != 0) shared = ReadPoints(d, ref data, pointCount);

        int record = at + 4;
        var peak = new double[axisCount];
        var start = new double[axisCount];
        var end = new double[axisCount];
        for (int t = 0; t < tupleCount; t++)
        {
            int size = U16(d, record), index = U16(d, record + 2);
            record += 4;
            if ((index & EmbeddedPeak) != 0)
            {
                for (int a = 0; a < axisCount; a++) peak[a] = FontVariations.F2Dot14(d, record + 2 * a);
                record += 2 * axisCount;
            }
            else
            {
                int shared2 = (index & 0x0FFF) * 2 * axisCount;
                for (int a = 0; a < axisCount; a++) peak[a] = FontVariations.F2Dot14(sharedTuples, shared2 + 2 * a);
            }

            if ((index & IntermediateRegion) != 0)
            {
                for (int a = 0; a < axisCount; a++)
                {
                    start[a] = FontVariations.F2Dot14(d, record + 2 * a);
                    end[a] = FontVariations.F2Dot14(d, record + 2 * axisCount + 2 * a);
                }

                record += 4 * axisCount;
            }
            else
            {
                for (int a = 0; a < axisCount; a++)
                {
                    start[a] = Math.Min(peak[a], 0);
                    end[a] = Math.Max(peak[a], 0);
                }
            }

            int tupleData = data;
            data += size;
            double scalar = FontVariations.Scalar(coordinates, start, peak, end);
            if (scalar == 0) continue;

            int[]? points = (index & PrivatePoints) != 0 ? ReadPoints(d, ref tupleData, pointCount) : shared;
            int count = points?.Length ?? pointCount;
            int[] x = ReadDeltas(d, ref tupleData, count);
            int[]? y = dy is null ? null : ReadDeltas(d, ref tupleData, count);
            if (tupleData > data) throw new FontFormatException("Font variation data is truncated.");

            if (points is null)
            {
                for (int p = 0; p < pointCount; p++)
                {
                    dx[p] += scalar * x[p];
                    if (dy is not null) dy[p] += scalar * y![p];
                }

                continue;
            }

            var touched = new bool[pointCount];
            var tx = new double[pointCount];
            double[]? ty = dy is null ? null : new double[pointCount];
            for (int k = 0; k < points.Length; k++)
            {
                int p = points[k];
                if (p >= pointCount) continue;
                touched[p] = true;
                tx[p] = x[k];
                if (ty is not null) ty[p] = y![k];
            }

            partial?.Invoke(touched, tx, ty);
            for (int p = 0; p < pointCount; p++)
            {
                dx[p] += scalar * tx[p];
                if (dy is not null) dy[p] += scalar * ty![p];
            }
        }
    }

    /// <summary>Packed point numbers; null means all points.</summary>
    private static int[]? ReadPoints(ReadOnlySpan<byte> d, ref int at, int pointCount)
    {
        int count = U8(d, at++);
        if (count == 0) return null;
        if ((count & 0x80) != 0) count = (count & 0x7F) << 8 | U8(d, at++);
        if (count > Math.Max(pointCount, 1) * 2 + 0xFF) throw new FontFormatException("Font variation point count is invalid.");
        var points = new int[count];
        int n = 0, last = 0;
        while (n < count)
        {
            int control = U8(d, at++);
            int run = (control & 0x7F) + 1;
            bool words = (control & 0x80) != 0;
            for (int k = 0; k < run && n < count; k++)
            {
                last += words ? U16(d, (at += 2) - 2) : U8(d, at++);
                points[n++] = last;
            }
        }

        return points;
    }

    /// <summary>Packed deltas: runs of zeros, bytes, words or (newer fonts) 32 bit values.</summary>
    private static int[] ReadDeltas(ReadOnlySpan<byte> d, ref int at, int count)
    {
        var deltas = new int[count];
        int n = 0;
        while (n < count)
        {
            int control = U8(d, at++);
            int run = (control & 0x3F) + 1;
            for (int k = 0; k < run && n < count; k++)
            {
                deltas[n++] = (control & 0xC0) switch
                {
                    0x80 => 0,
                    0x40 => I16(d, (at += 2) - 2),
                    0xC0 => (int)U32(d, (at += 4) - 4),
                    _ => (sbyte)U8(d, at++),
                };
            }
        }

        return deltas;
    }
}
