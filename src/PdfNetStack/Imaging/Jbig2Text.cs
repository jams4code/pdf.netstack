namespace PdfNetStack.Imaging;

/// <summary>
/// How a text region's values are coded: arithmetic integer contexts (T.88, Annex A) or Huffman
/// tables and raw bits. A symbol dictionary shares its own with the text regions it aggregates.
/// </summary>
internal sealed class Jbig2TextCoding
{
    public Jbig2IntegerDecoder? Integers { get; init; }

    public Jbig2BitReader? Bits { get; init; }

    public byte[] Iadt { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iafs { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iads { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iait { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iari { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iardw { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iardh { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iardx { get; } = Jbig2IntegerDecoder.Contexts();

    public byte[] Iardy { get; } = Jbig2IntegerDecoder.Contexts();

    /// <summary>IAID contexts, 2^<see cref="IdLength"/> of them.</summary>
    public byte[] Iaid { get; init; } = [];

    /// <summary>SBSYMCODELEN: IAID bits, or raw bits per symbol ID when Huffman coded without a table.</summary>
    public int IdLength { get; init; }

    /// <summary>The symbol ID Huffman table of a Huffman text region segment (7.4.3.1.7).</summary>
    public Jbig2HuffmanTable? SymbolCodes { get; init; }

    public Jbig2HuffmanTable? Fs { get; init; }

    public Jbig2HuffmanTable? Ds { get; init; }

    public Jbig2HuffmanTable? Dt { get; init; }

    public Jbig2HuffmanTable? Rdw { get; init; }

    public Jbig2HuffmanTable? Rdh { get; init; }

    public Jbig2HuffmanTable? Rdx { get; init; }

    public Jbig2HuffmanTable? Rdy { get; init; }

    public Jbig2HuffmanTable? Rsize { get; init; }

    /// <summary>The refinement contexts, kept across every refinement in the segment.</summary>
    public byte[] Refinement { get; init; } = Jbig2Generic.RefinementContexts();

    public bool Huffman => Bits is not null;

    public long Value(Jbig2HuffmanTable? table, byte[] contexts) => Bits is not null ? table!.Decode(Bits) : Integers!.Decode(contexts);

    public int Int(Jbig2HuffmanTable? table, byte[] contexts) => Bits is not null ? table!.DecodeInt(Bits) : Integers!.DecodeInt(contexts);
}

/// <summary>The text region decoding parameters (T.88, Table 9) that are not in <see cref="Jbig2TextCoding"/>.</summary>
internal sealed class Jbig2TextParameters
{
    public long Width { get; init; }

    public long Height { get; init; }

    public bool Refine { get; init; }

    public int DefaultPixel { get; init; }

    public long Instances { get; init; }

    public int LogStrips { get; init; }

    public bool Transposed { get; init; }

    /// <summary>REFCORNER: 0 bottom left, 1 top left, 2 bottom right, 3 top right.</summary>
    public int Corner { get; init; }

    public int Operator { get; init; }

    public int DsOffset { get; init; }

    public int RefinementTemplate { get; init; }

    public int[] RefinementAt { get; init; } = [-1, -1, -1, -1];
}

/// <summary>The text region (T.88, 6.4) and symbol dictionary (T.88, 6.5) decoding procedures.</summary>
internal static class Jbig2Text
{
    /// <summary>Symbols one decode may hold, which also caps IAID contexts at 2^21 bytes.</summary>
    public const int MaxSymbols = 1 << 21;

    /// <summary>6.4.5: places symbol instances, strip by strip, into a new region bitmap.</summary>
    public static Jbig2Bitmap DecodeRegion(Jbig2TextParameters p, Jbig2Symbols symbols, Jbig2TextCoding c, Jbig2Budget budget)
    {
        Jbig2Bitmap region = Jbig2Bitmap.Create(p.Width, p.Height, budget);
        if (p.DefaultPixel != 0) region.Fill(1);
        Draw(p, symbols, c, budget, region, 0, 0);
        return region;
    }

    /// <summary>
    /// 6.4.5 drawing straight into <paramref name="target"/>, where the region's top left is at
    /// (<paramref name="left"/>, <paramref name="top"/>): instances are cut to the region as they
    /// would be in a bitmap of its own.
    /// </summary>
    public static void Draw(Jbig2TextParameters p, Jbig2Symbols symbols, Jbig2TextCoding c, Jbig2Budget budget, Jbig2Bitmap target, long left, long top)
    {
        long strips = 1L << p.LogStrips;
        bool right = (p.Corner & 2) != 0, bottom = (p.Corner & 1) == 0;

        // 6.4.6: strip T values are scaled by SBSTRIPS.
        long stripT = -c.Value(c.Dt, c.Iadt) * strips, firstS = 0, instances = 0;
        while (instances < p.Instances)
        {
            stripT += Checked(c.Value(c.Dt, c.Iadt)) * strips;
            long curS = 0;
            for (bool first = true; ; first = false)
            {
                if (first)
                {
                    firstS += Checked(c.Value(c.Fs, c.Iafs));
                    curS = firstS;
                }
                else
                {
                    // 6.4.8: OOB ends the strip.
                    long ds = c.Value(c.Ds, c.Iads);
                    if (ds == Jbig2IntegerDecoder.Oob) break;
                    curS += ds + p.DsOffset;
                }

                long curT = strips == 1 ? 0 : c.Huffman ? c.Bits!.ReadBits(p.LogStrips) : Checked(c.Integers!.Decode(c.Iait));
                long t = stripT + curT;
                budget.Spend(Jbig2Budget.Step);
                Jbig2Bitmap ib = Instance(p, symbols, c, budget);

                // 6.4.5, 3 c) vi) to x): CURS moves by the size along S, before or after placing, by corner.
                long size = (p.Transposed ? ib.Height : ib.Width) - 1;
                bool before = p.Transposed ? bottom : right;
                if (before) curS += size;
                long x, y;
                if (!p.Transposed)
                {
                    x = right ? curS - ib.Width + 1 : curS;
                    y = bottom ? t - ib.Height + 1 : t;
                }
                else
                {
                    x = right ? t - ib.Width + 1 : t;
                    y = bottom ? curS - ib.Height + 1 : curS;
                }

                target.Compose(ib, left + x, top + y, p.Operator, budget, left, top, left + p.Width, top + p.Height);
                if (!before) curS += size;
                instances++;
            }
        }
    }

    private static long Checked(long value) =>
        value is Jbig2IntegerDecoder.Oob or < int.MinValue or > int.MaxValue ? throw new Jbig2FormatException("A text region value is out of range.") : value;

    /// <summary>6.4.10 and 6.4.11: the symbol ID, then the symbol or its refinement.</summary>
    private static Jbig2Bitmap Instance(Jbig2TextParameters p, Jbig2Symbols symbols, Jbig2TextCoding c, Jbig2Budget budget)
    {
        long id = c.Bits is null ? c.Integers!.DecodeId(c.Iaid, c.IdLength)
            : c.SymbolCodes is not null ? c.SymbolCodes.Decode(c.Bits)
            : c.Bits.ReadBits(c.IdLength);
        if (id < 0 || id >= symbols.Count) throw new Jbig2FormatException("A symbol ID is out of range.");
        Jbig2Bitmap symbol = symbols[(int)id];
        if (!p.Refine) return symbol;
        int ri = c.Bits is not null ? c.Bits.ReadBit() : c.Integers!.DecodeInt(c.Iari);
        if (ri == 0) return symbol;

        int rdw = c.Int(c.Rdw, c.Iardw), rdh = c.Int(c.Rdh, c.Iardh), rdx = c.Int(c.Rdx, c.Iardx), rdy = c.Int(c.Rdy, c.Iardy);
        long width = (long)symbol.Width + rdw, height = (long)symbol.Height + rdh;

        // Table 12: the reference is centred on the change in size, then offset.
        int dx = (rdw >> 1) + rdx, dy = (rdh >> 1) + rdy;
        if (c.Bits is null) return Jbig2Generic.Refine(c.Integers!.Arithmetic, c.Refinement, width, height, p.RefinementTemplate, symbol, dx, dy, false, p.RefinementAt, budget);
        return HuffmanRefine(c, width, height, p.RefinementTemplate, symbol, dx, dy, p.RefinementAt, budget);
    }

    /// <summary>
    /// 6.4.11, steps 5 to 7: with Huffman coding the refinement is arithmetic coded in its own
    /// byte aligned block whose size comes first.
    /// </summary>
    private static Jbig2Bitmap HuffmanRefine(Jbig2TextCoding c, long width, long height, int template, Jbig2Bitmap reference, int dx, int dy, int[] at, Jbig2Budget budget)
    {
        Jbig2BitReader bits = c.Bits!;
        long size = c.Rsize!.Decode(bits);
        bits.Align();
        int start = bits.BytePosition;
        if (size < 0 || size > bits.End - start) throw new Jbig2FormatException("A refinement's data size is out of range.");
        var decoder = new Jbig2ArithmeticDecoder(bits.Data, start, start + (int)size);
        Jbig2Bitmap result = Jbig2Generic.Refine(decoder, c.Refinement, width, height, template, reference, dx, dy, false, at, budget);
        bits.Seek(start + size);
        return result;
    }
}

/// <summary>Symbols available to a procedure: the input symbols followed by those decoded so far.</summary>
internal sealed class Jbig2Symbols(IReadOnlyList<Jbig2Bitmap> inputs, List<Jbig2Bitmap>? added = null)
{
    public int Count => inputs.Count + (added?.Count ?? 0);

    public Jbig2Bitmap this[int index] => index < inputs.Count ? inputs[index] : added![index - inputs.Count];
}

/// <summary>A symbol dictionary segment's fields (T.88, 7.4.2.1) and the tables it selected.</summary>
internal sealed class Jbig2SymbolParameters
{
    public bool Huffman { get; init; }

    public bool RefineAggregate { get; init; }

    public int Template { get; init; }

    public int[] At { get; init; } = [];

    public int RefinementTemplate { get; init; }

    public int[] RefinementAt { get; init; } = [-1, -1, -1, -1];

    public long Exported { get; init; }

    public long New { get; init; }

    public Jbig2HuffmanTable? Dh { get; init; }

    public Jbig2HuffmanTable? Dw { get; init; }

    public Jbig2HuffmanTable? BitmapSize { get; init; }

    public Jbig2HuffmanTable? AggregateInstances { get; init; }
}

internal static class Jbig2SymbolDictionary
{
    /// <summary>6.5.5: decodes the new symbols height class by height class, then the export flags.</summary>
    /// <param name="generic">Generic region contexts, fresh or kept from an earlier dictionary.</param>
    /// <param name="refinement">Refinement contexts, likewise.</param>
    public static List<Jbig2Bitmap> Decode(
        Jbig2SymbolParameters p, IReadOnlyList<Jbig2Bitmap> inputs, byte[] data, int start, int end, byte[] generic, byte[] refinement, Jbig2Budget budget)
    {
        if (p.New > Jbig2Text.MaxSymbols || inputs.Count + p.New > Jbig2Text.MaxSymbols) throw new Jbig2FormatException("A symbol dictionary has too many symbols.");
        int total = inputs.Count + (int)p.New;

        // 6.5.8.2.3: SBSYMCODELEN covers every symbol, at least one bit with Huffman coding.
        int idLength = Log2(total);
        if (p.Huffman) idLength = Math.Max(idLength, 1);

        Jbig2ArithmeticDecoder? arithmetic = p.Huffman ? null : new Jbig2ArithmeticDecoder(data, start, end);
        var integers = arithmetic is null ? null : new Jbig2IntegerDecoder(arithmetic);
        Jbig2BitReader? bits = p.Huffman ? new Jbig2BitReader(data, start, end) : null;

        // Table 17: the text region procedure that aggregates symbols uses these fixed tables.
        Jbig2TextCoding coding = p.Huffman
            ? new()
            {
                Bits = bits,
                IdLength = idLength,
                Fs = Jbig2HuffmanTable.Standard(6),
                Ds = Jbig2HuffmanTable.Standard(8),
                Dt = Jbig2HuffmanTable.Standard(11),
                Rdw = Jbig2HuffmanTable.Standard(15),
                Rdh = Jbig2HuffmanTable.Standard(15),
                Rdx = Jbig2HuffmanTable.Standard(15),
                Rdy = Jbig2HuffmanTable.Standard(15),
                Rsize = Jbig2HuffmanTable.Standard(1),
                Refinement = refinement,
            }
            : new() { Integers = integers, IdLength = idLength, Iaid = new byte[1 << idLength], Refinement = refinement };
        byte[] iadh = Jbig2IntegerDecoder.Contexts(), iadw = Jbig2IntegerDecoder.Contexts(), iaex = Jbig2IntegerDecoder.Contexts(), iaai = Jbig2IntegerDecoder.Contexts();

        var added = new List<Jbig2Bitmap>();
        var symbols = new Jbig2Symbols(inputs, added);
        var widths = new List<int>();
        long height = 0;
        while (added.Count + widths.Count < p.New)
        {
            // 6.5.6: the height class's height, as a change from the last one.
            budget.Spend(Jbig2Budget.Step);
            long dh = p.Huffman ? p.Dh!.Decode(bits!) : integers!.Decode(iadh);
            if (dh == Jbig2IntegerDecoder.Oob) throw new Jbig2FormatException("A height class has no height.");
            height += dh;
            if (height is < 0 or > int.MaxValue) throw new Jbig2FormatException("A height class height is out of range.");
            long width = 0, totalWidth = 0;
            widths.Clear();
            while (true)
            {
                // 6.5.7: widths change symbol by symbol; OOB ends the height class.
                long dw = p.Huffman ? p.Dw!.Decode(bits!) : integers!.Decode(iadw);
                if (dw == Jbig2IntegerDecoder.Oob) break;
                if (added.Count + widths.Count >= p.New) throw new Jbig2FormatException("A symbol dictionary has more symbols than it declares.");
                width += dw;
                totalWidth += width;
                if (width is < 0 or > int.MaxValue || totalWidth > int.MaxValue) throw new Jbig2FormatException("A symbol width is out of range.");
                if (p.Huffman && !p.RefineAggregate)
                {
                    budget.Spend(1);
                    widths.Add((int)width);
                }
                else if (p.RefineAggregate)
                {
                    added.Add(Aggregate(p, symbols, coding, iaai, width, height, budget));
                }
                else
                {
                    added.Add(Jbig2Generic.Decode(arithmetic!, generic, width, height, p.Template, false, null, p.At, budget));
                }
            }

            if (p.Huffman && !p.RefineAggregate)
            {
                Collective(p, bits!, totalWidth, (int)height, widths, added, budget);
                widths.Clear();
            }
        }

        if (symbols.Count != total) throw new Jbig2FormatException("A symbol dictionary has fewer symbols than it declares.");

        // 6.5.10: runs of symbols alternately not exported and exported.
        budget.Spend(total);
        var exported = new List<Jbig2Bitmap>();
        long index = 0;
        for (bool export = false; index < total; export = !export)
        {
            long run = p.Huffman ? Jbig2HuffmanTable.Standard(1).Decode(bits!) : integers!.Decode(iaex);
            if (run is Jbig2IntegerDecoder.Oob or < 0 || run > total - index) throw new Jbig2FormatException("An export run is out of range.");
            budget.Spend(Jbig2Budget.Step);
            if (export)
            {
                for (long i = index; i < index + run; i++) exported.Add(symbols[(int)i]);
            }

            index += run;
        }

        return exported;
    }

    /// <summary>6.5.8.2: a symbol refined from one symbol, or aggregated from several by a text region.</summary>
    private static Jbig2Bitmap Aggregate(Jbig2SymbolParameters p, Jbig2Symbols symbols, Jbig2TextCoding c, byte[] iaai, long width, long height, Jbig2Budget budget)
    {
        long instances = p.Huffman ? p.AggregateInstances!.Decode(c.Bits!) : c.Integers!.Decode(iaai);
        if (instances is Jbig2IntegerDecoder.Oob or < 1) throw new Jbig2FormatException("An aggregate symbol has no instances.");
        if (instances > 1)
        {
            // Table 17: one strip, top left corner, OR, no offset.
            var text = new Jbig2TextParameters
            {
                Width = width,
                Height = height,
                Refine = true,
                Instances = instances,
                Corner = 1,
                Operator = Jbig2Bitmap.Or,
                RefinementTemplate = p.RefinementTemplate,
                RefinementAt = p.RefinementAt,
            };
            return Jbig2Text.DecodeRegion(text, symbols, c, budget);
        }

        // 6.5.8.2.2: one instance is a plain refinement, offset but not resized.
        long id = c.Bits is not null ? c.Bits.ReadBits(c.IdLength) : c.Integers!.DecodeId(c.Iaid, c.IdLength);
        if (id >= symbols.Count) throw new Jbig2FormatException("A symbol ID is out of range.");
        int dx = c.Int(c.Rdx, c.Iardx), dy = c.Int(c.Rdy, c.Iardy);
        Jbig2Bitmap reference = symbols[(int)id];
        if (c.Bits is null) return Jbig2Generic.Refine(c.Integers!.Arithmetic, c.Refinement, width, height, p.RefinementTemplate, reference, dx, dy, false, p.RefinementAt, budget);

        Jbig2BitReader bits = c.Bits;
        long size = c.Rsize!.Decode(bits);
        bits.Align();
        int start = bits.BytePosition;
        if (size < 0 || size > bits.End - start) throw new Jbig2FormatException("A refinement's data size is out of range.");
        Jbig2Bitmap result = Jbig2Generic.Refine(new Jbig2ArithmeticDecoder(bits.Data, start, start + (int)size), c.Refinement, width, height, p.RefinementTemplate, reference, dx, dy, false, p.RefinementAt, budget);
        bits.Seek(start + size);
        return result;
    }

    /// <summary>6.5.9: a Huffman height class's symbols share one bitmap, uncompressed or MMR coded.</summary>
    private static void Collective(Jbig2SymbolParameters p, Jbig2BitReader bits, long totalWidth, int height, List<int> widths, List<Jbig2Bitmap> added, Jbig2Budget budget)
    {
        long size = p.BitmapSize!.Decode(bits);
        bits.Align();
        int start = bits.BytePosition;
        Jbig2Bitmap collective;
        if (size == 0)
        {
            collective = Jbig2Bitmap.Create(totalWidth, height, budget);
            long bytes = (long)collective.Stride * height;
            if (bytes > bits.End - start) throw new Jbig2FormatException("An uncompressed collective bitmap ends early.");
            Array.Copy(bits.Data, start, collective.Data, 0, bytes);
            size = bytes;
        }
        else
        {
            if (size < 0 || size > bits.End - start) throw new Jbig2FormatException("A collective bitmap's size is out of range.");
            collective = Jbig2Generic.DecodeMmr(bits.Data.AsSpan(start, (int)size), totalWidth, height, budget).Bitmap;
        }

        bits.Seek(start + size);
        int x = 0;
        foreach (int width in widths)
        {
            added.Add(collective.Columns(x, width, budget));
            x += width;
        }
    }

    /// <summary>The bits needed for <paramref name="count"/> distinct values: ceil(log2(count)).</summary>
    public static int Log2(long count)
    {
        int bits = 0;
        while ((1L << bits) < count) bits++;
        return bits;
    }
}
