namespace PdfNetStack.Imaging;

/// <summary>
/// Decodes JBIG2 data as PDF embeds it (ITU-T T.88, ISO/IEC 14492; ISO 32000-1, 7.4.7): the
/// segments of one page, without the file header, after the segments of JBIG2Globals. Generic,
/// refinement, text and halftone regions with their dictionaries and code tables are decoded,
/// arithmetic, Huffman and MMR coded. The page is cut to the image's size whatever the data says,
/// and work is bounded by that size: data asking for more is rejected.
/// </summary>
internal static class Jbig2Decoder
{
    private const int HeaderMinimum = 11;

    /// <summary>The page as packed rows, 1 for black as T.88 codes it, <paramref name="width"/> by <paramref name="height"/>.</summary>
    /// <param name="data">The page's segments.</param>
    /// <param name="globals">Segments shared by the document's pages, decoded first, or null.</param>
    /// <exception cref="Jbig2FormatException">The data is damaged, unsupported or exceeds the work the size allows.</exception>
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new Jbig2FormatException("The image has no size.");

        // Every bitmap, symbol, pattern and drawn pixel is paid for out of a few times the page.
        var budget = new Jbig2Budget(Math.Max(1L << 24, 4L * width * height));
        var page = new Jbig2Page(width, height, budget);
        if (globals is not null) page.Run(globals, Start(globals));
        page.Run(data, Start(data));
        return page.Bitmap?.Data ?? throw new Jbig2FormatException("The data has no page information segment.");
    }

    /// <summary>Where segments start: after a file header (T.88, D.4) when some writer left one in.</summary>
    private static int Start(byte[] data)
    {
        ReadOnlySpan<byte> id = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 9 || !data.AsSpan(0, 8).SequenceEqual(id)) return 0;
        if ((data[8] & 1) == 0) throw new Jbig2FormatException("Random access organisation is not supported.");
        return (data[8] & 2) != 0 ? 9 : 13;
    }

    public static long UInt32(byte[] data, int at) => ((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];

    public static int Int32(byte[] data, int at) => (int)UInt32(data, at);

    public static int UInt16(byte[] data, int at) => (data[at] << 8) | data[at + 1];

    /// <summary>The state of one page: its bitmap and the segments later segments may refer to.</summary>
    private sealed class Jbig2Page(int width, int height, Jbig2Budget budget)
    {
        private readonly Dictionary<long, Segment> _segments = [];
        private bool _ended;

        public Jbig2Bitmap? Bitmap { get; private set; }

        private sealed class Segment
        {
            public List<Jbig2Bitmap>? Symbols { get; set; }

            public byte[]? GenericContexts { get; set; }

            public byte[]? RefinementContexts { get; set; }

            public Jbig2Bitmap[]? Patterns { get; set; }

            public Jbig2HuffmanTable? Table { get; set; }

            public Jbig2Bitmap? Region { get; set; }
        }

        /// <summary>The segments in sequential organisation (T.88, 7.1 and 7.2), up to end of page or file.</summary>
        public void Run(byte[] data, int position)
        {
            while (!_ended && data.Length - position >= HeaderMinimum)
            {
                // 7.2.2 to 7.2.7: number, flags, referred-to segments, page association, data length.
                long number = UInt32(data, position);
                int flags = data[position + 4], type = flags & 0x3F;
                int at = position + 5;
                long count = data[at] >> 5;
                if (count is 5 or 6) throw new Jbig2FormatException("A segment's referred-to count is invalid.");
                if (count == 7)
                {
                    Need(data, at, 4);
                    count = UInt32(data, at) & 0x1FFFFFFF;
                    at += 4 + (int)Math.Min(int.MaxValue / 2, (count + 8) / 8);
                }
                else
                {
                    at++;
                }

                int size = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
                Need(data, at, count * size);
                budget.Spend(count);
                var referred = new long[count];
                for (int i = 0; i < count; i++, at += size) referred[i] = size == 1 ? data[at] : size == 2 ? UInt16(data, at) : UInt32(data, at);
                at += (flags & 0x40) != 0 ? 4 : 1;
                Need(data, at, 4);
                long length = UInt32(data, at);
                at += 4;
                bool unknown = length == 0xFFFFFFFF;
                if (unknown) length = UnknownLength(data, at, type);
                Need(data, at, length);
                Process(number, type, referred, data, at, at + (int)length, unknown);
                position = at + (int)length;
            }
        }

        private static void Need(byte[] data, int at, long bytes)
        {
            if (at < 0 || bytes < 0 || bytes > data.Length - at) throw new Jbig2FormatException("A segment ends early.");
        }

        /// <summary>
        /// 7.2.7: an immediate generic region may leave its length unknown; its data then ends with
        /// 0xFFAC (arithmetic) or 0x0000 (MMR) and the number of rows, 4 bytes.
        /// </summary>
        private static long UnknownLength(byte[] data, int start, int type)
        {
            if (type != 38 || data.Length - start < 18) throw new Jbig2FormatException("Only an immediate generic region may have an unknown length.");
            bool mmr = (data[start + 17] & 1) != 0;
            byte first = mmr ? (byte)0x00 : (byte)0xFF, second = mmr ? (byte)0x00 : (byte)0xAC;
            for (int i = start + 18; i + 6 <= data.Length; i++)
            {
                if (data[i] == first && data[i + 1] == second) return i + 6 - start;
            }

            throw new Jbig2FormatException("The end of a generic region of unknown length is missing.");
        }

        private void Process(long number, int type, long[] referred, byte[] data, int start, int end, bool unknownLength)
        {
            switch (type)
            {
                case 0:
                    _segments[number] = SymbolDictionary(referred, data, start, end);
                    break;
                case 4 or 6 or 7:
                    Region(number, type == 4, TextRegion(referred, data, start, end));
                    break;
                case 16:
                    _segments[number] = new Segment { Patterns = Jbig2Halftone.DecodePatterns(data, start, end, budget) };
                    break;
                case 20 or 22 or 23:
                    Region(number, type == 20, HalftoneRegion(referred, data, start, end));
                    break;
                case 36 or 38 or 39:
                    Region(number, type == 36, GenericRegion(data, start, end, unknownLength));
                    break;
                case 40 or 42 or 43:
                    Region(number, type == 40, RefinementRegion(referred, data, start, end));
                    break;
                case 48:
                    PageInformation(data, start, end);
                    break;
                case 49 or 51:
                    // End of page, end of file.
                    _ended = true;
                    break;
                case 50 or 52 or 62:
                    // End of stripe: the page is already as high as the image. Profiles and extensions change nothing here.
                    break;
                case 53:
                    _segments[number] = new Segment { Table = Jbig2HuffmanTable.Read(data, start, end) };
                    break;
                default:
                    throw new Jbig2FormatException($"Segment type {type} is not supported.");
            }
        }

        /// <summary>7.4.8: the page's size is the image's; only the default pixel value is taken.</summary>
        private void PageInformation(byte[] data, int start, int end)
        {
            if (end - start < 19) throw new Jbig2FormatException("A page information segment is too short.");
            if (Bitmap is not null) return;
            Bitmap = Jbig2Bitmap.Create(width, height, budget);
            if ((data[start + 16] & 0x04) != 0) Bitmap.Fill(1);
        }

        /// <summary>7.4.1: a region segment's size, location and external combination operator.</summary>
        private static (long Width, long Height, long X, long Y, int Operator) RegionInfo(byte[] data, int start, int end)
        {
            if (end - start < 17) throw new Jbig2FormatException("A region segment is too short.");
            int op = data[start + 16] & 7;
            if (op > Jbig2Bitmap.Replace) throw new Jbig2FormatException("A combination operator is invalid.");
            return (UInt32(data, start), UInt32(data, start + 4), UInt32(data, start + 8), UInt32(data, start + 12), op);
        }

        /// <summary>An immediate region is drawn on the page; an intermediate one is kept for refinement.</summary>
        private void Region(long number, bool intermediate, (Jbig2Bitmap Bitmap, long X, long Y, int Operator) region)
        {
            if (intermediate)
            {
                _segments[number] = new Segment { Region = region.Bitmap };
                return;
            }

            Jbig2Bitmap page = Bitmap ?? throw new Jbig2FormatException("A region comes before the page information.");
            page.Compose(region.Bitmap, region.X, region.Y, region.Operator, budget);
        }

        /// <summary>AT pixel offsets: signed bytes, x then y (7.4.6.3).</summary>
        private static int[] AtPixels(byte[] data, int at, int pairs, int end)
        {
            if (end - at < 2 * pairs) throw new Jbig2FormatException("A segment is too short for its AT pixels.");
            var result = new int[2 * pairs];
            for (int i = 0; i < result.Length; i++) result[i] = (sbyte)data[at + i];
            return result;
        }

        /// <summary>7.4.6: a generic region segment.</summary>
        private (Jbig2Bitmap, long, long, int) GenericRegion(byte[] data, int start, int end, bool unknownLength)
        {
            (long w, long h, long x, long y, int op) = RegionInfo(data, start, end);
            if (end - start < 18) throw new Jbig2FormatException("A generic region segment is too short.");
            int flags = data[start + 17], at = start + 18;
            bool mmr = (flags & 1) != 0, typical = (flags & 8) != 0;
            int template = (flags >> 1) & 3;
            if ((flags & 0x10) != 0) throw new Jbig2FormatException("Extended generic templates are not supported.");

            // 7.2.7: with an unknown length the row count follows the end marker.
            if (unknownLength)
            {
                h = Math.Min(h, UInt32(data, end - 4));
                end -= 4;
            }

            if (mmr) return (Jbig2Generic.DecodeMmr(data.AsSpan(at, end - at), w, h, budget).Bitmap, x, y, op);
            int[] pixels = AtPixels(data, at, template == 0 ? 4 : 1, end);
            at += pixels.Length;
            var decoder = new Jbig2ArithmeticDecoder(data, at, end);
            return (Jbig2Generic.Decode(decoder, Jbig2Generic.GenericContexts(), w, h, template, typical, null, pixels, budget), x, y, op);
        }

        /// <summary>7.4.7: a refinement of an intermediate region, or of the page where the region lies.</summary>
        private (Jbig2Bitmap, long, long, int) RefinementRegion(long[] referred, byte[] data, int start, int end)
        {
            (long w, long h, long x, long y, int op) = RegionInfo(data, start, end);
            if (end - start < 18) throw new Jbig2FormatException("A refinement region segment is too short.");
            int flags = data[start + 17], at = start + 18, template = flags & 1;
            bool typical = (flags & 2) != 0;
            int[] pixels = template == 0 ? AtPixels(data, at, 2, end) : [-1, -1, -1, -1];
            if (template == 0) at += 4;

            Jbig2Bitmap? reference = null;
            foreach (long n in referred)
            {
                if (_segments.TryGetValue(n, out Segment? s) && s.Region is not null) reference = s.Region;
            }

            if (reference is null)
            {
                Jbig2Bitmap page = Bitmap ?? throw new Jbig2FormatException("A region comes before the page information.");
                reference = Jbig2Bitmap.Create(w, h, budget);
                reference.Compose(page, -x, -y, Jbig2Bitmap.Replace, budget);
            }

            var decoder = new Jbig2ArithmeticDecoder(data, at, end);
            return (Jbig2Generic.Refine(decoder, Jbig2Generic.RefinementContexts(), w, h, template, reference, 0, 0, typical, pixels, budget), x, y, op);
        }

        /// <summary>The symbols the referred-to symbol dictionaries export, in order (7.4.2.2 and 7.4.3.2).</summary>
        private List<Jbig2Bitmap> InputSymbols(long[] referred)
        {
            var symbols = new List<Jbig2Bitmap>();
            foreach (long n in referred)
            {
                if (Find(n).Symbols is List<Jbig2Bitmap> exported)
                {
                    if (symbols.Count + exported.Count > Jbig2Text.MaxSymbols) throw new Jbig2FormatException("Too many symbols.");
                    budget.Spend(exported.Count);
                    symbols.AddRange(exported);
                }
            }

            return symbols;
        }

        private Segment Find(long number) =>
            _segments.TryGetValue(number, out Segment? s) ? s : throw new Jbig2FormatException($"Segment {number} is referred to but missing.");

        /// <summary>The code tables of the referred-to table segments, in order, for the selections that ask for one.</summary>
        private Queue<Jbig2HuffmanTable> Tables(long[] referred) =>
            new(referred.Select(n => Find(n).Table).OfType<Jbig2HuffmanTable>());

        private static Jbig2HuffmanTable Custom(Queue<Jbig2HuffmanTable> tables) =>
            tables.Count > 0 ? tables.Dequeue() : throw new Jbig2FormatException("A custom code table is missing.");

        private static Jbig2HuffmanTable Select(int selection, Queue<Jbig2HuffmanTable> tables, params int[] standard) =>
            selection == 3 ? Custom(tables) : selection < standard.Length ? Jbig2HuffmanTable.Standard(standard[selection]) : throw new Jbig2FormatException("A Huffman table selection is invalid.");

        /// <summary>7.4.2: a symbol dictionary segment.</summary>
        private Segment SymbolDictionary(long[] referred, byte[] data, int start, int end)
        {
            if (end - start < 2) throw new Jbig2FormatException("A symbol dictionary segment is too short.");
            int flags = UInt16(data, start), at = start + 2;
            bool huffman = (flags & 1) != 0, refine = (flags & 2) != 0, contextUsed = (flags & 0x100) != 0, contextRetained = (flags & 0x200) != 0;
            int template = (flags >> 10) & 3, refinementTemplate = (flags >> 12) & 1;
            int[] pixels = [];
            if (!huffman)
            {
                pixels = AtPixels(data, at, template == 0 ? 4 : 1, end);
                at += pixels.Length;
            }

            int[] refinementPixels = [-1, -1, -1, -1];
            if (refine && refinementTemplate == 0)
            {
                refinementPixels = AtPixels(data, at, 2, end);
                at += 4;
            }

            Need(data, at, 8);
            long exported = UInt32(data, at), added = UInt32(data, at + 4);
            at += 8;

            // 7.4.2.1.6: custom tables are taken in this order: DH, DW, BMSIZE, AGGINST.
            Queue<Jbig2HuffmanTable> tables = Tables(referred);
            var p = new Jbig2SymbolParameters
            {
                Huffman = huffman,
                RefineAggregate = refine,
                Template = template,
                At = pixels,
                RefinementTemplate = refinementTemplate,
                RefinementAt = refinementPixels,
                Exported = exported,
                New = added,
                Dh = huffman ? Select((flags >> 2) & 3, tables, 4, 5) : null,
                Dw = huffman ? Select((flags >> 4) & 3, tables, 2, 3) : null,
                BitmapSize = huffman ? ((flags & 0x40) != 0 ? Custom(tables) : Jbig2HuffmanTable.Standard(1)) : null,
                AggregateInstances = huffman ? ((flags & 0x80) != 0 ? Custom(tables) : Jbig2HuffmanTable.Standard(1)) : null,
            };

            // 7.4.2.2: the contexts may carry on from the last referred-to dictionary.
            byte[] generic = Jbig2Generic.GenericContexts(), refinement = Jbig2Generic.RefinementContexts();
            if (contextUsed)
            {
                Segment? last = referred.Select(Find).LastOrDefault(s => s.Symbols is not null);
                if (last?.GenericContexts is null) throw new Jbig2FormatException("A symbol dictionary reuses contexts that were not kept.");
                generic = (byte[])last.GenericContexts.Clone();
                refinement = (byte[])last.RefinementContexts!.Clone();
            }

            List<Jbig2Bitmap> symbols = Jbig2SymbolDictionary.Decode(p, InputSymbols(referred), data, at, end, generic, refinement, budget);
            return new Segment
            {
                Symbols = symbols,
                GenericContexts = contextRetained ? generic : null,
                RefinementContexts = contextRetained ? refinement : null,
            };
        }

        /// <summary>7.4.3: a text region segment.</summary>
        private (Jbig2Bitmap, long, long, int) TextRegion(long[] referred, byte[] data, int start, int end)
        {
            (long w, long h, long x, long y, int op) = RegionInfo(data, start, end);
            int at = start + 17;
            Need(data, at, 2);
            int flags = UInt16(data, at);
            at += 2;
            bool huffman = (flags & 1) != 0, refine = (flags & 2) != 0;
            int huffmanFlags = 0;
            if (huffman)
            {
                Need(data, at, 2);
                huffmanFlags = UInt16(data, at);
                at += 2;
            }

            int refinementTemplate = (flags >> 15) & 1;
            int[] refinementPixels = [-1, -1, -1, -1];
            if (refine && refinementTemplate == 0)
            {
                refinementPixels = AtPixels(data, at, 2, end);
                at += 4;
            }

            Need(data, at, 4);
            long instances = UInt32(data, at);
            at += 4;

            // 7.4.3.1.1: SBDSOFFSET is a signed 5 bit field.
            int dsOffset = (flags >> 10) & 0x1F;
            if (dsOffset >= 16) dsOffset -= 32;
            var p = new Jbig2TextParameters
            {
                Width = w,
                Height = h,
                Refine = refine,
                DefaultPixel = (flags >> 9) & 1,
                Instances = instances,
                LogStrips = (flags >> 2) & 3,
                Corner = (flags >> 4) & 3,
                Transposed = (flags & 0x40) != 0,
                Operator = (flags >> 7) & 3,
                DsOffset = dsOffset,
                RefinementTemplate = refinementTemplate,
                RefinementAt = refinementPixels,
            };

            List<Jbig2Bitmap> symbols = InputSymbols(referred);
            Jbig2TextCoding coding;
            if (huffman)
            {
                // 7.4.3.1.6: custom tables in the order FS, DS, DT, RDW, RDH, RDX, RDY, RSIZE.
                Queue<Jbig2HuffmanTable> tables = Tables(referred);
                Jbig2HuffmanTable fs = Select(huffmanFlags & 3, tables, 6, 7), ds = Select((huffmanFlags >> 2) & 3, tables, 8, 9, 10), dt = Select((huffmanFlags >> 4) & 3, tables, 11, 12, 13);
                Jbig2HuffmanTable? rdw = null, rdh = null, rdx = null, rdy = null, rsize = null;
                if (refine)
                {
                    rdw = Select((huffmanFlags >> 6) & 3, tables, 14, 15);
                    rdh = Select((huffmanFlags >> 8) & 3, tables, 14, 15);
                    rdx = Select((huffmanFlags >> 10) & 3, tables, 14, 15);
                    rdy = Select((huffmanFlags >> 12) & 3, tables, 14, 15);
                    rsize = (huffmanFlags & 0x4000) != 0 ? Custom(tables) : Jbig2HuffmanTable.Standard(1);
                }

                var bits = new Jbig2BitReader(data, at, end);
                coding = new Jbig2TextCoding
                {
                    Bits = bits,
                    SymbolCodes = SymbolCodes(bits, symbols.Count),
                    Fs = fs,
                    Ds = ds,
                    Dt = dt,
                    Rdw = rdw,
                    Rdh = rdh,
                    Rdx = rdx,
                    Rdy = rdy,
                    Rsize = rsize,
                };
            }
            else
            {
                int length = Jbig2SymbolDictionary.Log2(symbols.Count);
                coding = new Jbig2TextCoding
                {
                    Integers = new Jbig2IntegerDecoder(new Jbig2ArithmeticDecoder(data, at, end)),
                    IdLength = length,
                    Iaid = new byte[1 << length],
                };
            }

            return (Jbig2Text.DecodeRegion(p, new Jbig2Symbols(symbols), coding, budget), x, y, op);
        }

        /// <summary>7.4.3.1.7: the symbol ID code lengths, themselves run length coded with a table of 35 run codes.</summary>
        private Jbig2HuffmanTable SymbolCodes(Jbig2BitReader bits, int symbols)
        {
            var runCodes = new int[35];
            for (int i = 0; i < runCodes.Length; i++) runCodes[i] = (int)bits.ReadBits(4);
            Jbig2HuffmanTable runTable = Jbig2HuffmanTable.FromLengths(runCodes);
            budget.Spend(symbols);
            var lengths = new int[symbols];
            for (int i = 0; i < symbols; )
            {
                int code = runTable.DecodeInt(bits);
                int repeat, length;
                switch (code)
                {
                    case < 32:
                        lengths[i++] = code;
                        continue;
                    case 32:
                        if (i == 0) throw new Jbig2FormatException("A symbol code length repeats nothing.");
                        repeat = 3 + (int)bits.ReadBits(2);
                        length = lengths[i - 1];
                        break;
                    case 33:
                        repeat = 3 + (int)bits.ReadBits(3);
                        length = 0;
                        break;
                    default:
                        repeat = 11 + (int)bits.ReadBits(7);
                        length = 0;
                        break;
                }

                if (repeat > symbols - i) throw new Jbig2FormatException("Symbol code lengths run past the symbols.");
                for (int k = 0; k < repeat; k++) lengths[i++] = length;
            }

            bits.Align();
            return Jbig2HuffmanTable.FromLengths(lengths);
        }

        /// <summary>7.4.5: a halftone region segment, drawing the patterns of the referred-to dictionary.</summary>
        private (Jbig2Bitmap, long, long, int) HalftoneRegion(long[] referred, byte[] data, int start, int end)
        {
            (long w, long h, long x, long y, int op) = RegionInfo(data, start, end);
            int at = start + 17;
            Need(data, at, 21);
            int flags = data[at];
            var p = new Jbig2Halftone.Parameters
            {
                Width = w,
                Height = h,
                Mmr = (flags & 1) != 0,
                Template = (flags >> 1) & 3,
                Skip = (flags & 8) != 0,
                Operator = (flags >> 4) & 7,
                DefaultPixel = (flags >> 7) & 1,
                GridWidth = UInt32(data, at + 1),
                GridHeight = UInt32(data, at + 5),
                GridX = Int32(data, at + 9),
                GridY = Int32(data, at + 13),
                VectorX = UInt16(data, at + 17),
                VectorY = UInt16(data, at + 19),
            };
            if (p.Operator > Jbig2Bitmap.Replace) throw new Jbig2FormatException("A combination operator is invalid.");
            Jbig2Bitmap[] patterns = referred.Select(Find).Select(s => s.Patterns).OfType<Jbig2Bitmap[]>().FirstOrDefault()
                ?? throw new Jbig2FormatException("A halftone region refers to no pattern dictionary.");
            return (Jbig2Halftone.DecodeRegion(p, patterns, data, at + 21, end, budget), x, y, op);
        }
    }
}
