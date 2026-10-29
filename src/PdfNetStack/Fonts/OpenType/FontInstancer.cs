using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Turns one location of a variable font into a static font: gvar deltas move TrueType points
/// and component offsets (with untouched points interpolated), CFF2 blends resolve into a CFF
/// table, HVAR or the phantom points give the advances, MVAR the font wide metrics and cvar the
/// control values. Layout tables stay as they are; the shaper applies their deltas itself.
/// Written from the OpenType specification (ISO/IEC 14496-22, font variations).
/// </summary>
internal static class FontInstancer
{
    /// <summary>Points a glyph may resolve to through its components; deeper or larger nesting is refused.</summary>
    private const int MaxResolvedPoints = 1 << 22, MaxComponentDepth = 16;

    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "fvar", "avar", "gvar", "cvar", "HVAR", "VVAR", "MVAR", "STAT", "CFF2",
    };

    // MVAR value tags and where each goes: table, offset, signed.
    private static readonly Dictionary<string, (string Table, int Offset, bool Signed)> MetricFields = new(StringComparer.Ordinal)
    {
        ["hasc"] = ("OS/2", 68, true), ["hdsc"] = ("OS/2", 70, true), ["hlgp"] = ("OS/2", 72, true),
        ["hcla"] = ("OS/2", 74, false), ["hcld"] = ("OS/2", 76, false), ["xhgt"] = ("OS/2", 86, true), ["cpht"] = ("OS/2", 88, true),
        ["sbxs"] = ("OS/2", 10, true), ["sbys"] = ("OS/2", 12, true), ["sbxo"] = ("OS/2", 14, true), ["sbyo"] = ("OS/2", 16, true),
        ["spxs"] = ("OS/2", 18, true), ["spys"] = ("OS/2", 20, true), ["spxo"] = ("OS/2", 22, true), ["spyo"] = ("OS/2", 24, true),
        ["strs"] = ("OS/2", 26, true), ["stro"] = ("OS/2", 28, true),
        ["hcrs"] = ("hhea", 18, true), ["hcrn"] = ("hhea", 20, true), ["hcof"] = ("hhea", 22, true),
        ["unds"] = ("post", 10, true), ["undo"] = ("post", 8, true),
    };

    /// <summary>
    /// The static font at <paramref name="coordinates"/> (normalized, one per fvar axis), as sfnt
    /// bytes. <paramref name="axisValues"/> are the same location in user units, for the weight,
    /// width and slant fields.
    /// </summary>
    /// <exception cref="FontFormatException">A variation table is damaged.</exception>
    public static byte[] Instantiate(OpenTypeFile font, FontVariations? variations, float[] coordinates, IReadOnlyList<double> axisValues)
    {
        int glyphCount = font.GlyphCount;
        var advances = new int[glyphCount];
        var lsbs = new int[glyphCount];
        ReadOnlySpan<byte> hmtx = font.Table("hmtx");
        int full = font.NumberOfHMetrics;
        for (int g = 0; g < glyphCount; g++)
        {
            advances[g] = font.Advance(g);
            int at = g < full ? 4 * g + 2 : 4 * full + 2 * (g - full);
            lsbs[g] = at + 2 <= hmtx.Length ? I16(hmtx, at) : 0;
        }

        // HVAR applies to the default advances; the phantom points give them only without it.
        int[] defaultAdvances = (int[])advances.Clone(), defaultLsbs = (int[])lsbs.Clone();
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        bool cff2 = font.HasTable("CFF2");
        short[]? box = null;
        if (cff2)
        {
            tables["CFF "] = Cff2Converter.ToCff(font.Table("CFF2"), coordinates, font.PostScriptName, glyphCount);
        }
        else
        {
            var outlines = new TrueTypeOutlines(font, coordinates, advances, lsbs);
            (tables["glyf"], tables["loca"], box) = outlines.Write();
        }

        ApplyHvar(font, coordinates, defaultAdvances, defaultLsbs, advances, lsbs);

        byte[] head = font.Table("head").ToArray();
        byte[] hhea = font.Table("hhea").ToArray();
        byte[] os2 = font.Table("OS/2").ToArray();
        byte[] post = font.Table("post").ToArray();
        var targets = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["OS/2"] = os2, ["hhea"] = hhea, ["post"] = post };
        ApplyMvar(font, coordinates, targets);
        SetStyle(variations, axisValues, os2, post, head);

        // indexToLocFormat: the new loca has long offsets.
        if (!cff2)
        {
            head[50] = 0;
            head[51] = 1;
        }

        if (box is not null)
        {
            for (int k = 0; k < 4; k++) SetI16(head, 36 + 2 * k, box[k]);
        }

        tables["head"] = head;
        tables["hhea"] = hhea;
        if (os2.Length > 0) tables["OS/2"] = os2;
        if (post.Length > 0) tables["post"] = post;
        tables["hmtx"] = WriteMetrics(font, cff2, advances, lsbs, hhea, tables.TryGetValue("glyf", out byte[]? glyf) ? (glyf, tables["loca"]) : null);
        if (font.HasTable("cvt ")) tables["cvt "] = ApplyCvar(font, coordinates);

        var list = new List<(string Tag, uint Checksum, byte[] Data)>();
        foreach (string tag in font.TableTags)
        {
            if (Dropped.Contains(tag) || tables.ContainsKey(tag)) continue;
            list.Add((tag, 0, font.Table(tag).ToArray()));
        }

        foreach (KeyValuePair<string, byte[]> t in tables) list.Add((t.Key, 0, t.Value));
        return SfntBuilder.Build(cff2 ? 0x4F54544Fu : 0x00010000u, list);
    }

    /// <summary>Advance and side bearing deltas from HVAR, when the font has it.</summary>
    private static void ApplyHvar(OpenTypeFile font, float[] coordinates, int[] defaultAdvances, int[] defaultLsbs, int[] advances, int[] lsbs)
    {
        if (!font.HasTable("HVAR")) return;
        byte[] data = font.Data;
        int table = font.TableOffset("HVAR");
        ReadOnlySpan<byte> d = data;
        ItemVariationStore store = ItemVariationStore.Read(data, table + Offset32(d, table + 4));
        DeltaSetIndexMap advanceMap = DeltaSetIndexMap.Read(data, table, Offset32(d, table + 8));
        DeltaSetIndexMap lsbMap = DeltaSetIndexMap.Read(data, table, Offset32(d, table + 12));
        for (int g = 0; g < advances.Length; g++)
        {
            (int outer, int inner) = advanceMap.Map(g);
            advances[g] = Math.Max(0, (int)Math.Round(defaultAdvances[g] + store.Delta(outer, inner, coordinates), MidpointRounding.AwayFromZero));
            if (lsbMap.IsIdentity) continue;
            (outer, inner) = lsbMap.Map(g);
            lsbs[g] = Round(defaultLsbs[g] + store.Delta(outer, inner, coordinates));
        }
    }

    /// <summary>
    /// MVAR deltas on OS/2, hhea and post. When hhea's ascender, descender and gap equal the
    /// typographic ones, they move with them.
    /// </summary>
    private static void ApplyMvar(OpenTypeFile font, float[] coordinates, Dictionary<string, byte[]> targets)
    {
        if (!font.HasTable("MVAR")) return;
        byte[] os2 = targets["OS/2"], hhea = targets["hhea"];
        bool synced = os2.Length >= 74 && hhea.Length >= 10
            && I16(os2, 68) == I16(hhea, 4) && I16(os2, 70) == I16(hhea, 6) && I16(os2, 72) == I16(hhea, 8);

        byte[] data = font.Data;
        int table = font.TableOffset("MVAR");
        ReadOnlySpan<byte> d = data;
        int recordSize = U16(d, table + 6), count = U16(d, table + 8), storeOffset = U16(d, table + 10);
        if (recordSize < 8 || storeOffset == 0) return;
        ItemVariationStore store = ItemVariationStore.Read(data, table + storeOffset);
        for (int i = 0; i < count; i++)
        {
            int r = table + 12 + i * recordSize;
            if (!MetricFields.TryGetValue(Tag(d, r), out (string Table, int Offset, bool Signed) field)) continue;
            byte[] target = targets[field.Table];
            if (field.Offset + 2 > target.Length) continue;
            int delta = Round(store.Delta(U16(d, r + 4), U16(d, r + 6), coordinates));
            if (delta == 0) continue;
            int value = (field.Signed ? I16(target, field.Offset) : U16(target, field.Offset)) + delta;
            SetI16(target, field.Offset, field.Signed ? Math.Max(short.MinValue, Math.Min(short.MaxValue, value)) : Math.Max(0, Math.Min(0xFFFF, value)));
        }

        if (!synced) return;
        for (int k = 0; k < 3; k++) SetI16(hhea, 4 + 2 * k, I16(os2, 68 + 2 * k));
    }

    /// <summary>Weight and width classes from the wght and wdth axes, the italic angle from slnt.</summary>
    private static void SetStyle(FontVariations? variations, IReadOnlyList<double> values, byte[] os2, byte[] post, byte[] head)
    {
        if (variations is null) return;
        for (int i = 0; i < variations.Axes.Count && i < values.Count; i++)
        {
            VariationAxis axis = variations.Axes[i];
            double v = Math.Max(axis.Minimum, Math.Min(axis.Maximum, values[i]));
            switch (axis.Tag)
            {
                case "wght" when os2.Length >= 6:
                    SetI16(os2, 4, (int)Math.Max(1, Math.Min(1000, Math.Round(v))));
                    break;
                case "wdth" when os2.Length >= 8:
                    double[] widths = [50, 62.5, 75, 87.5, 100, 112.5, 125, 150, 200];
                    int best = 0;
                    for (int k = 1; k < widths.Length; k++)
                    {
                        if (Math.Abs(widths[k] - v) < Math.Abs(widths[best] - v)) best = k;
                    }

                    SetI16(os2, 6, best + 1);
                    break;
                case "slnt" when post.Length >= 8:
                    int angle = (int)Math.Round(v * 65536);
                    post[4] = (byte)(angle >> 24);
                    post[5] = (byte)(angle >> 16);
                    post[6] = (byte)(angle >> 8);
                    post[7] = (byte)angle;
                    break;
                case "ital" when os2.Length >= 64 && head.Length >= 46:
                    bool italic = v >= 0.5;
                    int fsSelection = U16(os2, 62), macStyle = U16(head, 44);
                    SetI16(os2, 62, italic ? (fsSelection | 1) & ~0x40 : fsSelection & ~1);
                    SetI16(head, 44, italic ? macStyle | 2 : macStyle & ~2);
                    break;
            }
        }
    }

    /// <summary>The cvt with cvar deltas applied; control values the tuples do not name stay.</summary>
    private static byte[] ApplyCvar(OpenTypeFile font, float[] coordinates)
    {
        byte[] cvt = font.Table("cvt ").ToArray();
        if (!font.HasTable("cvar")) return cvt;
        int count = cvt.Length / 2;
        var deltas = new double[count];
        ReadOnlySpan<byte> cvar = font.Table("cvar");
        if (cvar.Length < 8) return cvt;
        TupleVariations.Apply(cvar, 4, 0, coordinates.Length, default, coordinates, deltas, null, null);
        for (int i = 0; i < count; i++) SetI16(cvt, 2 * i, Math.Max(short.MinValue, Math.Min(short.MaxValue, Round(I16(cvt, 2 * i) + deltas[i]))));
        return cvt;
    }

    /// <summary>
    /// A full hmtx (one advance and side bearing per glyph), with hhea's count and extremes
    /// updated; TrueType extremes come from the new glyph boxes.
    /// </summary>
    private static byte[] WriteMetrics(OpenTypeFile font, bool cff, int[] advances, int[] lsbs, byte[] hhea, (byte[] Glyf, byte[] Loca)? outlines)
    {
        int n = advances.Length;
        var w = new FontWriter(4 * n);
        int maxAdvance = 0, minLsb = int.MaxValue, minRsb = int.MaxValue, maxExtent = int.MinValue;
        for (int g = 0; g < n; g++)
        {
            w.U16(advances[g]);
            w.U16((ushort)(short)Math.Max(short.MinValue, Math.Min(short.MaxValue, lsbs[g])));
            maxAdvance = Math.Max(maxAdvance, advances[g]);
            if (cff || outlines is null) continue;
            (byte[] glyf, byte[] loca) = outlines.Value;
            int start = (int)U32(loca, 4 * g), end = (int)U32(loca, 4 * g + 4);
            if (end - start < 10 || I16(glyf, start) == 0) continue;
            int width = I16(glyf, start + 6) - I16(glyf, start + 2);
            minLsb = Math.Min(minLsb, lsbs[g]);
            minRsb = Math.Min(minRsb, advances[g] - lsbs[g] - width);
            maxExtent = Math.Max(maxExtent, lsbs[g] + width);
        }

        if (hhea.Length >= 36)
        {
            SetI16(hhea, 10, Math.Min(0xFFFF, maxAdvance));
            if (minLsb != int.MaxValue)
            {
                SetI16(hhea, 12, Clamp16(minLsb));
                SetI16(hhea, 14, Clamp16(minRsb));
                SetI16(hhea, 16, Clamp16(maxExtent));
            }

            SetI16(hhea, 34, n);
        }

        return w.ToArray();
    }

    private static int Clamp16(int v) => Math.Max(short.MinValue, Math.Min(short.MaxValue, v));

    /// <summary>Rounds half up, as fonts are instanced elsewhere.</summary>
    internal static int Round(double v) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Floor(v + 0.5)));

    internal static void SetI16(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 8);
        data[at + 1] = (byte)value;
    }

    /// <summary>The glyf table of an instance: every glyph read, moved by its gvar deltas and written back.</summary>
    private sealed class TrueTypeOutlines
    {
        private readonly OpenTypeFile _font;
        private readonly Glyph[] _glyphs;
        private readonly (double[] X, double[] Y)?[] _resolved;
        private int _resolvedPoints;

        public TrueTypeOutlines(OpenTypeFile font, float[] coordinates, int[] advances, int[] lsbs)
        {
            _font = font;
            int n = font.GlyphCount;
            _glyphs = new Glyph[n];
            _resolved = new (double[], double[])?[n];
            for (int g = 0; g < n; g++) _glyphs[g] = Glyph.Read(font, g);

            Variations gvar = Variations.Read(font);
            var phantomAdvance = new double[n];
            for (int g = 0; g < n; g++)
            {
                Glyph glyph = _glyphs[g];
                int count = glyph.PointCount + 4;
                var dx = new double[count];
                var dy = new double[count];
                gvar.Apply(g, coordinates, dx, dy, glyph);
                double left = glyph.XMin - lsbs[g];
                double newLeft = left + dx[count - 4];
                double newRight = left + advances[g] + dx[count - 3];
                for (int p = 0; p < glyph.PointCount; p++)
                {
                    glyph.X[p] = Round(glyph.X[p] + dx[p]);
                    glyph.Y[p] = Round(glyph.Y[p] + dy[p]);
                }

                phantomAdvance[g] = newRight - newLeft;
                glyph.Left = newLeft;
            }

            for (int g = 0; g < n; g++)
            {
                Glyph glyph = _glyphs[g];
                if (glyph.PointCount > 0 || glyph.Components.Length > 0) glyph.Box = Box(g);
                advances[g] = Math.Max(0, Round(phantomAdvance[g]));
                lsbs[g] = glyph.Box is short[] b ? Round(b[0] - glyph.Left) : 0;
            }

            // A component marked USE_MY_METRICS gives the composite its advance.
            for (int g = 0; g < n; g++)
            {
                foreach (Component c in _glyphs[g].Components)
                {
                    if ((c.Flags & 0x0200) != 0 && c.Glyph < n) advances[g] = advances[c.Glyph];
                }
            }
        }

        /// <summary>glyf, loca (long offsets) and the font's bounding box.</summary>
        public (byte[] Glyf, byte[] Loca, short[]? Box) Write()
        {
            var glyf = new FontWriter(_font.Table("glyf").Length + 16);
            var loca = new FontWriter(4 * (_glyphs.Length + 1));
            short[]? box = null;
            foreach (Glyph glyph in _glyphs)
            {
                loca.U32((uint)glyf.Length);
                glyph.Write(glyf);
                glyf.Pad(4);
                if (glyph.Box is not short[] b) continue;
                box = box is null ? (short[])b.Clone() : [Math.Min(box[0], b[0]), Math.Min(box[1], b[1]), Math.Max(box[2], b[2]), Math.Max(box[3], b[3])];
            }

            loca.U32((uint)glyf.Length);
            return (glyf.ToArray(), loca.ToArray(), box);
        }

        /// <summary>The bounding box of a glyph's points after its components are placed.</summary>
        private short[] Box(int g)
        {
            (double[] xs, double[] ys) = Resolve(g, 0);
            if (xs.Length == 0) return [0, 0, 0, 0];
            double x0 = xs.Min(), y0 = ys.Min(), x1 = xs.Max(), y1 = ys.Max();
            return [(short)Clamp16(Round(x0)), (short)Clamp16(Round(y0)), (short)Clamp16(Round(x1)), (short)Clamp16(Round(y1))];
        }

        /// <summary>A glyph's outline points in its own coordinates, components transformed and placed.</summary>
        private (double[] X, double[] Y) Resolve(int g, int depth)
        {
            if (_resolved[g] is (double[], double[]) done) return done;
            if (depth > MaxComponentDepth) throw new FontFormatException("Composite glyphs are nested too deeply.");
            Glyph glyph = _glyphs[g];
            if (glyph.Components.Length == 0)
            {
                _resolved[g] = (glyph.X, glyph.Y);
                return (glyph.X, glyph.Y);
            }

            var xs = new List<double>();
            var ys = new List<double>();
            for (int k = 0; k < glyph.Components.Length; k++)
            {
                Component c = glyph.Components[k];
                if (c.Glyph >= _glyphs.Length) continue;
                (double[] cx, double[] cy) = Resolve(c.Glyph, depth + 1);
                int first = xs.Count;
                for (int p = 0; p < cx.Length; p++)
                {
                    xs.Add(c.A * cx[p] + c.C * cy[p]);
                    ys.Add(c.B * cx[p] + c.D * cy[p]);
                }

                double ox, oy;
                if ((c.Flags & 0x0002) != 0)
                {
                    ox = glyph.X[k];
                    oy = glyph.Y[k];
                    if ((c.Flags & 0x0800) != 0 && (c.Flags & 0x1000) == 0)
                    {
                        (ox, oy) = (c.A * ox + c.C * oy, c.B * ox + c.D * oy);
                    }
                }
                else
                {
                    // Point matching: the component's point lands on the composite's point.
                    int parent = c.Arg1, child = first + c.Arg2;
                    if (parent >= first || child >= xs.Count) throw new FontFormatException("Composite glyph point index is out of range.");
                    ox = xs[parent] - xs[child];
                    oy = ys[parent] - ys[child];
                }

                for (int p = first; p < xs.Count; p++)
                {
                    xs[p] += ox;
                    ys[p] += oy;
                }

                _resolvedPoints += cx.Length;
                if (_resolvedPoints > MaxResolvedPoints) throw new FontFormatException("Composite glyphs resolve to too many points.");
            }

            (double[], double[]) result = ([.. xs], [.. ys]);
            _resolved[g] = result;
            return result;
        }
    }

    /// <summary>The glyph variations table (gvar): shared tuples and each glyph's variation data.</summary>
    private sealed class Variations
    {
        private readonly byte[]? _data;
        private readonly int _table, _axisCount, _sharedTuples, _sharedTupleCount, _dataArray;
        private readonly bool _longOffsets;

        private Variations(byte[]? data, int table, int axisCount, int sharedTuples, int sharedTupleCount, int dataArray, bool longOffsets)
        {
            _data = data;
            _table = table;
            _axisCount = axisCount;
            _sharedTuples = sharedTuples;
            _sharedTupleCount = sharedTupleCount;
            _dataArray = dataArray;
            _longOffsets = longOffsets;
        }

        /// <summary>The font's gvar; one that does not match the font's glyphs is ignored, as HarfBuzz does.</summary>
        public static Variations Read(OpenTypeFile font)
        {
            ReadOnlySpan<byte> gvar = font.Table("gvar");
            if (gvar.Length < 20 || U16(gvar, 12) != font.GlyphCount) return new Variations(null, 0, 0, 0, 0, 0, false);
            int table = font.TableOffset("gvar");
            int axisCount = U16(gvar, 4);
            if (axisCount > 64) throw new FontFormatException("Font gvar table has too many axes.");
            int sharedCount = U16(gvar, 6);
            int shared = table + Offset32(gvar, 8);
            Check(font.Data, shared, sharedCount * axisCount * 2);
            return new Variations(font.Data, table, axisCount, shared, sharedCount, table + Offset32(gvar, 16), (U16(gvar, 14) & 1) != 0);
        }

        /// <summary>Adds glyph <paramref name="g"/>'s deltas at the location; simple outlines interpolate untouched points.</summary>
        public void Apply(int g, float[] coordinates, double[] dx, double[] dy, Glyph glyph)
        {
            if (_data is null) return;
            ReadOnlySpan<byte> d = _data;
            int offsets = _table + 20;
            int start = _longOffsets ? Offset32(d, offsets + 4 * g) : 2 * U16(d, offsets + 2 * g);
            int end = _longOffsets ? Offset32(d, offsets + 4 * g + 4) : 2 * U16(d, offsets + 2 * g + 2);
            if (end <= start) return;
            Check(d, _dataArray + start, end - start);
            ReadOnlySpan<byte> shared = d.Slice(_sharedTuples, _sharedTupleCount * _axisCount * 2);
            ReadOnlySpan<byte> data = d.Slice(_dataArray + start, end - start);
            Action<bool[], double[], double[]?>? interpolate = glyph.Components.Length == 0 && glyph.PointCount > 0
                ? (touched, tx, ty) => glyph.Interpolate(touched, tx, ty!)
                : null;
            TupleVariations.Apply(data, 0, 0, _axisCount, shared, coordinates, dx, dy, interpolate);
        }
    }

    /// <summary>A component of a composite glyph: flags, glyph, arguments and its 2x2 transform.</summary>
    private sealed class Component
    {
        public int Flags, Glyph, Arg1, Arg2;
        public double A = 1, B, C, D = 1;
        public byte[] Transform = [];
    }

    /// <summary>A glyph of the glyf table, decoded far enough to move its points and write it back.</summary>
    private sealed class Glyph
    {
        public double XMin;
        public double Left;
        public short[]? Box;
        public ushort[] EndPoints = [];
        public byte[] Instructions = [];
        public byte[] Flags = [];
        public double[] X = [], Y = [];
        public Component[] Components = [];

        /// <summary>Outline points, or for a composite one point per component (its offset).</summary>
        public int PointCount => X.Length;

        public static Glyph Read(OpenTypeFile font, int g)
        {
            var glyph = new Glyph();
            (int start, int end) = font.GlyphRange(g);
            if (end - start < 10) return glyph;
            ReadOnlySpan<byte> d = font.Table("glyf").Slice(start, end - start);
            int contours = I16(d, 0);
            glyph.XMin = I16(d, 2);
            if (contours >= 0) glyph.ReadSimple(d, contours);
            else glyph.ReadComposite(d);
            return glyph;
        }

        private void ReadSimple(ReadOnlySpan<byte> d, int contours)
        {
            if (contours == 0) return;
            EndPoints = new ushort[contours];
            for (int i = 0; i < contours; i++)
            {
                EndPoints[i] = U16(d, 10 + 2 * i);
                if (i > 0 && EndPoints[i] <= EndPoints[i - 1]) throw new FontFormatException("Glyph contour end points are not increasing.");
            }

            int count = EndPoints[^1] + 1;
            int at = 10 + 2 * contours;
            int instructionLength = U16(d, at);
            Instructions = Slice(d, at + 2, instructionLength).ToArray();
            at += 2 + instructionLength;

            Flags = new byte[count];
            for (int p = 0; p < count;)
            {
                byte flag = U8(d, at++);
                Flags[p++] = flag;
                if ((flag & 0x08) == 0) continue;
                int repeat = U8(d, at++);
                for (int r = 0; r < repeat && p < count; r++) Flags[p++] = flag;
            }

            X = new double[count];
            Y = new double[count];
            int v = 0;
            for (int p = 0; p < count; p++)
            {
                byte f = Flags[p];
                if ((f & 0x02) != 0) v += (f & 0x10) != 0 ? U8(d, at++) : -U8(d, at++);
                else if ((f & 0x10) == 0) v += I16(d, (at += 2) - 2);
                X[p] = v;
            }

            v = 0;
            for (int p = 0; p < count; p++)
            {
                byte f = Flags[p];
                if ((f & 0x04) != 0) v += (f & 0x20) != 0 ? U8(d, at++) : -U8(d, at++);
                else if ((f & 0x20) == 0) v += I16(d, (at += 2) - 2);
                Y[p] = v;
            }
        }

        private void ReadComposite(ReadOnlySpan<byte> d)
        {
            var components = new List<Component>();
            int at = 10;
            bool instructions = false;
            int flags;
            do
            {
                flags = U16(d, at);
                var c = new Component { Flags = flags, Glyph = U16(d, at + 2) };
                at += 4;
                bool words = (flags & 0x0001) != 0, xy = (flags & 0x0002) != 0;
                if (words)
                {
                    c.Arg1 = xy ? I16(d, at) : U16(d, at);
                    c.Arg2 = xy ? I16(d, at + 2) : U16(d, at + 2);
                    at += 4;
                }
                else
                {
                    c.Arg1 = xy ? (sbyte)U8(d, at) : U8(d, at);
                    c.Arg2 = xy ? (sbyte)U8(d, at + 1) : U8(d, at + 1);
                    at += 2;
                }

                int transform = (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
                c.Transform = Slice(d, at, transform).ToArray();
                at += transform;
                switch (transform)
                {
                    case 2:
                        c.A = c.D = I16(c.Transform, 0) / 16384.0;
                        break;
                    case 4:
                        c.A = I16(c.Transform, 0) / 16384.0;
                        c.D = I16(c.Transform, 2) / 16384.0;
                        break;
                    case 8:
                        c.A = I16(c.Transform, 0) / 16384.0;
                        c.B = I16(c.Transform, 2) / 16384.0;
                        c.C = I16(c.Transform, 4) / 16384.0;
                        c.D = I16(c.Transform, 6) / 16384.0;
                        break;
                }

                instructions |= (flags & 0x0100) != 0;
                components.Add(c);
                if (components.Count > 0xFFFF) throw new FontFormatException("Composite glyph has too many components.");
            }
            while ((flags & 0x0020) != 0);

            if (instructions) Instructions = Slice(d, at + 2, U16(d, at)).ToArray();
            Components = [.. components];
            X = new double[Components.Length];
            Y = new double[Components.Length];
            for (int k = 0; k < Components.Length; k++)
            {
                if ((Components[k].Flags & 0x0002) == 0) continue;
                X[k] = Components[k].Arg1;
                Y[k] = Components[k].Arg2;
            }
        }

        /// <summary>
        /// Interpolates the deltas of the points a tuple does not name (IUP): per contour and axis,
        /// from the nearest named points before and after, by their original coordinates.
        /// </summary>
        public void Interpolate(bool[] touched, double[] dx, double[] dy)
        {
            int start = 0;
            foreach (ushort last in EndPoints)
            {
                Interpolate(touched, X, dx, start, last);
                Interpolate(touched, Y, dy, start, last);
                start = last + 1;
            }
        }

        private static void Interpolate(bool[] touched, double[] original, double[] delta, int start, int end)
        {
            int first = -1;
            for (int p = start; p <= end; p++)
            {
                if (!touched[p]) continue;
                first = p;
                break;
            }

            if (first < 0) return;
            int previous = first;
            int p2 = first;
            do
            {
                // The next touched point, cyclically within the contour.
                int next = p2;
                do
                {
                    next = next == end ? start : next + 1;
                }
                while (!touched[next]);

                double x1 = original[previous], x2 = original[next], d1 = delta[previous], d2 = delta[next];
                if (x1 > x2)
                {
                    (x1, x2) = (x2, x1);
                    (d1, d2) = (d2, d1);
                }

                for (int p = previous == end ? start : previous + 1; p != next; p = p == end ? start : p + 1)
                {
                    double x = original[p];
                    delta[p] = x1 == x2 ? (d1 == d2 ? d1 : 0)
                        : x <= x1 ? d1
                        : x >= x2 ? d2
                        : d1 + (x - x1) * (d2 - d1) / (x2 - x1);
                }

                previous = next;
                p2 = next;
            }
            while (p2 != first);
        }

        public void Write(FontWriter w)
        {
            if (Components.Length > 0)
            {
                WriteComposite(w);
                return;
            }

            if (PointCount == 0) return;
            w.U16(EndPoints.Length);
            foreach (short v in Box!) w.U16((ushort)v);
            foreach (ushort e in EndPoints) w.U16(e);
            w.U16(Instructions.Length);
            w.Bytes(Instructions);

            // One flag per point, with the short forms where the step fits a byte.
            var xs = new int[PointCount];
            var ys = new int[PointCount];
            int lastX = 0, lastY = 0;
            for (int p = 0; p < PointCount; p++)
            {
                xs[p] = (int)X[p] - lastX;
                ys[p] = (int)Y[p] - lastY;
                lastX = (int)X[p];
                lastY = (int)Y[p];
                int flag = Flags[p] & 0x41;
                flag |= xs[p] == 0 ? 0x10 : Math.Abs(xs[p]) < 256 ? 0x02 | (xs[p] > 0 ? 0x10 : 0) : 0;
                flag |= ys[p] == 0 ? 0x20 : Math.Abs(ys[p]) < 256 ? 0x04 | (ys[p] > 0 ? 0x20 : 0) : 0;
                w.U8(flag);
            }

            foreach (int v in xs) WriteStep(w, v);
            foreach (int v in ys) WriteStep(w, v);
        }

        private static void WriteStep(FontWriter w, int v)
        {
            if (v == 0) return;
            if (Math.Abs(v) < 256) w.U8(Math.Abs(v));
            else w.U16(Clamp16(v));
        }

        private void WriteComposite(FontWriter w)
        {
            w.U16(0xFFFF);
            foreach (short v in Box!) w.U16((ushort)v);
            for (int k = 0; k < Components.Length; k++)
            {
                Component c = Components[k];
                int flags = (c.Flags & ~(0x0020 | 0x0100)) | 0x0001;
                if (k < Components.Length - 1) flags |= 0x0020;
                else if (Instructions.Length > 0) flags |= 0x0100;
                w.U16(flags);
                w.U16(c.Glyph);
                if ((c.Flags & 0x0002) != 0)
                {
                    w.U16(Clamp16((int)X[k]));
                    w.U16(Clamp16((int)Y[k]));
                }
                else
                {
                    w.U16(c.Arg1);
                    w.U16(c.Arg2);
                }

                w.Bytes(c.Transform);
            }

            if (Instructions.Length == 0) return;
            w.U16(Instructions.Length);
            w.Bytes(Instructions);
        }
    }
}
