using System.Buffers;
#if NET8_0_OR_GREATER
using System.Numerics;
#endif

namespace PdfNetStack.Imaging;

/// <summary>
/// Writes a coefficient image as a sequential or progressive JPEG whose Huffman tables are built
/// from the image's own statistics. Restart markers are dropped and only the JFIF and Adobe
/// segments are kept, which is everything a decoder needs to reproduce the same samples.
/// </summary>
internal static class JpegWriter
{
    // Largest magnitude categories that 8 bit Huffman coding can express (ITU T.81 F.1.2).
    private const int MaxDcSize = 11;
    private const int MaxAcSize = 10;

    // Longest end-of-band run one EOBn symbol can carry, and the correction bits a refinement scan
    // may hold back before it flushes the run (as libjpeg bounds them).
    private const int MaxEobRun = 0x7FFF;
    private const int MaxCorrectionBits = 1000 - 63;

    /// <summary>The smaller of the sequential and, when allowed, the progressive coding.</summary>
    /// <returns>The encoded JPEG, or null when a coefficient falls outside what 8 bit coding can express.</returns>
    public static byte[]? TryWriteSmallest(JpegImage image, int capacityHint, bool allowProgressive)
    {
        byte[]? sequential = TryWrite(image, capacityHint);
        if (sequential is null || !allowProgressive) return sequential;
        byte[]? progressive = TryWriteProgressive(image, sequential.Length);
        return progressive is not null && progressive.Length < sequential.Length ? progressive : sequential;
    }

    /// <returns>The encoded JPEG, or null when a coefficient falls outside what 8 bit coding can express.</returns>
    public static byte[]? TryWrite(JpegImage image, int capacityHint)
    {
        JpegComponent[] components = image.Components;
        (List<ushort[]> tables, int[] tableOf, int[] classOf, int blocksPerMcu) = Layout(image);

        // One interleaved scan when the MCU allows it, otherwise one scan per component.
        bool interleaved = components.Length > 1 && blocksPerMcu <= 10;
        int classes = components.Length == 1 ? 1 : 2;
        var statistics = new Statistics(classes);
        if (!EncodeScans(image, interleaved, classOf, ref statistics)) return null;

        var dc = new JpegHuffmanCode[classes];
        var ac = new JpegHuffmanCode[classes];
        for (int c = 0; c < classes; c++)
        {
            dc[c] = JpegHuffmanCode.Optimal(statistics.Dc[c]);
            ac[c] = JpegHuffmanCode.Optimal(statistics.Ac[c]);
        }

        using var output = new ByteWriter(Math.Max(1024, capacityHint));
        bool extended = tables.Exists(t => Array.Exists(t, v => v > 255));
        WriteHeaders(output, image, tables, tableOf, extended ? (byte)0xC1 : (byte)0xC0);
        WriteHuffmanTables(output, dc, ac);

        var writer = new EntropyWriter(output, dc, ac);
        if (interleaved)
        {
            WriteScanHeader(output, components, classOf);
            EncodeScan(image, components, classOf, ref writer);
            writer.Flush();
        }
        else
        {
            for (int i = 0; i < components.Length; i++)
            {
                WriteScanHeader(output, [components[i]], [classOf[i]]);
                EncodeScan(image, [components[i]], [classOf[i]], ref writer);
                writer.Flush();
            }
        }

        output.Marker(0xD9);
        return output.ToArray();
    }

    /// <summary>
    /// Writes the image as a progressive JPEG with successive approximation, using the scan script
    /// libjpeg's simple progression uses (ITU T.81 G.1). Every scan gets its own optimal tables.
    /// </summary>
    /// <returns>The encoded JPEG, or null when the tables or coefficients do not fit progressive 8 bit coding.</returns>
    public static byte[]? TryWriteProgressive(JpegImage image, int capacityHint)
    {
        JpegComponent[] components = image.Components;
        (List<ushort[]> tables, int[] tableOf, int[] classOf, int blocksPerMcu) = Layout(image);
        if (tables.Exists(t => Array.Exists(t, v => v > 255))) return null;

        // The coefficients must fit the sequential limits, which also bound every progressive symbol.
        var check = new Statistics(2);
        if (!EncodeScans(image, interleaved: false, classOf, ref check)) return null;

        using var output = new ByteWriter(Math.Max(1024, capacityHint));
        WriteHeaders(output, image, tables, tableOf, 0xC2);
        foreach (ProgressiveScan scan in Script(components.Length, blocksPerMcu <= 10))
        {
            JpegComponent[] scanComponents = [.. scan.Components.Select(i => components[i])];
            // A scan of one component codes it with table 0, so no table is left without symbols.
            int[] scanClasses = scan.Components.Length == 1 ? [0] : [.. scan.Components.Select(i => classOf[i])];
            bool dc = scan.Start == 0;
            int classes = dc ? scanClasses.Max() + 1 : 1;
            JpegHuffmanCode[] dcCodes = [], acCodes = [];
            if (!(dc && scan.High > 0))
            {
                var statistics = new Statistics(classes);
                EncodeProgressiveScan(image, scanComponents, scanClasses, scan, ref statistics);
                if (dc)
                {
                    dcCodes = new JpegHuffmanCode[classes];
                    for (int c = 0; c < classes; c++) dcCodes[c] = JpegHuffmanCode.Optimal(statistics.Dc[c]);
                }
                else
                {
                    acCodes = [JpegHuffmanCode.Optimal(statistics.Ac[0])];
                }

                WriteProgressiveTables(output, dcCodes, acCodes);
            }

            output.Marker(0xDA);
            output.UInt16(6 + (2 * scanComponents.Length));
            output.Byte((byte)scanComponents.Length);
            for (int i = 0; i < scanComponents.Length; i++)
            {
                output.Byte((byte)scanComponents[i].Id);
                output.Byte((byte)(dc ? scanClasses[i] << 4 : 0));
            }

            output.Byte((byte)scan.Start);
            output.Byte((byte)scan.End);
            output.Byte((byte)((scan.High << 4) | scan.Low));

            var writer = new EntropyWriter(output, dcCodes, acCodes);
            EncodeProgressiveScan(image, scanComponents, scanClasses, scan, ref writer);
            writer.Flush();
        }

        output.Marker(0xD9);
        return output.ToArray();
    }

    /// <summary>Distinct quantization tables, each component's table and Huffman class, and the blocks in one MCU.</summary>
    private static (List<ushort[]> Tables, int[] TableOf, int[] ClassOf, int BlocksPerMcu) Layout(JpegImage image)
    {
        JpegComponent[] components = image.Components;
        var tables = new List<ushort[]>();
        var tableOf = new int[components.Length];
        var classOf = new int[components.Length];
        int blocksPerMcu = 0;
        for (int i = 0; i < components.Length; i++)
        {
            ushort[] q = components[i].Quantization;
            int index = tables.FindIndex(t => t.AsSpan().SequenceEqual(q));
            if (index < 0)
            {
                index = tables.Count;
                tables.Add(q);
            }

            tableOf[i] = index;
            classOf[i] = i == 0 ? 0 : 1;
            blocksPerMcu += components[i].Horizontal * components[i].Vertical;
        }

        return (tables, tableOf, classOf, blocksPerMcu);
    }

    private static void WriteHeaders(ByteWriter output, JpegImage image, List<ushort[]> tables, int[] tableOf, byte frameMarker)
    {
        output.Marker(0xD8);
        foreach (JpegSegment segment in image.Segments)
        {
            output.Marker(segment.Marker);
            output.UInt16(segment.Payload.Length + 2);
            output.Bytes(segment.Payload);
        }

        WriteQuantizationTables(output, tables);
        WriteFrameHeader(output, image, frameMarker, tableOf);
    }

    /// <param name="Components">Indexes into the frame's components.</param>
    /// <param name="Start">First coefficient of the band (Ss).</param>
    /// <param name="End">Last coefficient of the band (Se).</param>
    /// <param name="High">Bit position of the previous pass (Ah), 0 for a first pass.</param>
    /// <param name="Low">Bit position of this pass (Al).</param>
    private sealed record ProgressiveScan(int[] Components, int Start, int End, int High, int Low);

    /// <summary>
    /// The scan script of libjpeg's jpeg_simple_progression: for YCbCr, early low resolution luma
    /// and chroma bands, then refinement; for other component counts, the same per component.
    /// </summary>
    private static List<ProgressiveScan> Script(int count, bool interleaveDc)
    {
        var scans = new List<ProgressiveScan>();
        void Dc(int high, int low)
        {
            if (interleaveDc) scans.Add(new([.. Enumerable.Range(0, count)], 0, 0, high, low));
            else for (int c = 0; c < count; c++) scans.Add(new([c], 0, 0, high, low));
        }

        if (count == 3)
        {
            Dc(0, 1);
            scans.Add(new([0], 1, 5, 0, 2));
            scans.Add(new([2], 1, 63, 0, 1));
            scans.Add(new([1], 1, 63, 0, 1));
            scans.Add(new([0], 6, 63, 0, 2));
            scans.Add(new([0], 1, 63, 2, 1));
            Dc(1, 0);
            scans.Add(new([2], 1, 63, 1, 0));
            scans.Add(new([1], 1, 63, 1, 0));
            scans.Add(new([0], 1, 63, 1, 0));
            return scans;
        }

        Dc(0, 1);
        for (int c = 0; c < count; c++) scans.Add(new([c], 1, 5, 0, 2));
        for (int c = 0; c < count; c++) scans.Add(new([c], 6, 63, 0, 2));
        for (int c = 0; c < count; c++) scans.Add(new([c], 1, 63, 2, 1));
        Dc(1, 0);
        for (int c = 0; c < count; c++) scans.Add(new([c], 1, 63, 1, 0));
        return scans;
    }

    private static void WriteProgressiveTables(ByteWriter output, JpegHuffmanCode[] dc, JpegHuffmanCode[] ac)
    {
        int length = 2;
        foreach (JpegHuffmanCode code in dc.Concat(ac)) length += 17 + code.Symbols.Length;
        output.Marker(0xC4);
        output.UInt16(length);
        for (int c = 0; c < dc.Length; c++) Write((byte)c, dc[c]);
        for (int c = 0; c < ac.Length; c++) Write((byte)(0x10 | c), ac[c]);

        void Write(byte header, JpegHuffmanCode code)
        {
            output.Byte(header);
            output.Bytes(code.Counts);
            output.Bytes(code.Symbols);
        }
    }

    /// <summary>One progressive scan in coding order (ITU T.81 G.1.2): DC first or refinement, AC first or refinement.</summary>
    private static void EncodeProgressiveScan<TSink>(JpegImage image, JpegComponent[] components, int[] classOf, ProgressiveScan scan, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        if (scan.Start == 0)
        {
            Span<int> predictors = stackalloc int[4];
            predictors.Clear();
            foreach ((JpegComponent c, int index, int row, int column) in Blocks(image, components))
            {
                int value = c.Block(row, column)[0] >> scan.Low;
                if (scan.High > 0)
                {
                    sink.Bits(value & 1, 1);
                    continue;
                }

                int difference = value - predictors[index];
                predictors[index] = value;
                int size = BitLength(difference < 0 ? -difference : difference);
                sink.Dc(classOf[index], size);
                if (size > 0) sink.Bits(difference < 0 ? difference - 1 : difference, size);
            }

            return;
        }

        var state = new BandState();
        JpegComponent component = components[0];
        for (int row = 0; row < component.BlocksPerColumn; row++)
        {
            for (int column = 0; column < component.BlocksPerLine; column++)
            {
                Span<short> block = component.Block(row, column);
                if (scan.High == 0) AcFirst(block, scan, ref state, ref sink);
                else AcRefine(block, scan, ref state, ref sink);
            }
        }

        EmitEobRun(ref state, ref sink);
    }

    /// <summary>Blocks of a scan in coding order: MCU by MCU when interleaved, else the component's own blocks row by row.</summary>
    private static IEnumerable<(JpegComponent Component, int Index, int Row, int Column)> Blocks(JpegImage image, JpegComponent[] components)
    {
        if (components.Length == 1)
        {
            JpegComponent c = components[0];
            for (int row = 0; row < c.BlocksPerColumn; row++)
            {
                for (int column = 0; column < c.BlocksPerLine; column++) yield return (c, 0, row, column);
            }

            yield break;
        }

        for (int mcuRow = 0; mcuRow < image.McusPerColumn; mcuRow++)
        {
            for (int mcuColumn = 0; mcuColumn < image.McusPerLine; mcuColumn++)
            {
                for (int j = 0; j < components.Length; j++)
                {
                    JpegComponent c = components[j];
                    for (int v = 0; v < c.Vertical; v++)
                    {
                        for (int h = 0; h < c.Horizontal; h++) yield return (c, j, (mcuRow * c.Vertical) + v, (mcuColumn * c.Horizontal) + h);
                    }
                }
            }
        }
    }

    /// <summary>The end-of-band run and the correction bits that wait for it, carried from block to block.</summary>
    private sealed class BandState
    {
        public int EobRun { get; set; }

        /// <summary>Correction bits of the blocks inside the current run (refinement only).</summary>
        public List<int> Pending { get; } = [];

        /// <summary>Correction bits of the current block not yet written.</summary>
        public List<int> Block { get; } = [];
    }

    /// <summary>First AC pass: magnitudes shifted down by Al, zero runs and end-of-band runs (G.1.2.2).</summary>
    private static void AcFirst<TSink>(Span<short> block, ProgressiveScan scan, ref BandState state, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        int run = 0;
        for (int k = scan.Start; k <= scan.End; k++)
        {
            int value = block[k];
            int magnitude = (value < 0 ? -value : value) >> scan.Low;
            if (magnitude == 0)
            {
                run++;
                continue;
            }

            EmitEobRun(ref state, ref sink);
            for (; run > 15; run -= 16) sink.Ac(0, 0xF0);
            int size = BitLength(magnitude);
            sink.Ac(0, (run << 4) | size);
            sink.Bits(value < 0 ? ~magnitude : magnitude, size);
            run = 0;
        }

        if (run > 0 && ++state.EobRun == MaxEobRun) EmitEobRun(ref state, ref sink);
    }

    /// <summary>
    /// AC refinement (G.1.2.3): one more bit of every coefficient. Coefficients that become
    /// nonzero are coded with their run and sign; the next bit of those already nonzero follows as
    /// raw correction bits after the next coded symbol, or after the end-of-band run.
    /// </summary>
    private static void AcRefine<TSink>(Span<short> block, ProgressiveScan scan, ref BandState state, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        Span<int> magnitudes = stackalloc int[64];
        int lastNew = 0;
        for (int k = scan.Start; k <= scan.End; k++)
        {
            int value = block[k];
            magnitudes[k] = (value < 0 ? -value : value) >> scan.Low;
            if (magnitudes[k] == 1) lastNew = k;
        }

        int run = 0;
        state.Block.Clear();
        for (int k = scan.Start; k <= scan.End; k++)
        {
            int magnitude = magnitudes[k];
            if (magnitude == 0)
            {
                run++;
                continue;
            }

            // A zero run of 16 is only coded when a newly nonzero coefficient follows in this block.
            while (run > 15 && k <= lastNew)
            {
                EmitEobRun(ref state, ref sink);
                sink.Ac(0, 0xF0);
                run -= 16;
                FlushBits(state.Block, ref sink);
            }

            if (magnitude > 1)
            {
                state.Block.Add(magnitude & 1);
                continue;
            }

            EmitEobRun(ref state, ref sink);
            sink.Ac(0, (run << 4) | 1);
            sink.Bits(block[k] < 0 ? 0 : 1, 1);
            FlushBits(state.Block, ref sink);
            run = 0;
        }

        if (run > 0 || state.Block.Count > 0)
        {
            state.EobRun++;
            state.Pending.AddRange(state.Block);
            state.Block.Clear();
            if (state.EobRun == MaxEobRun || state.Pending.Count > MaxCorrectionBits) EmitEobRun(ref state, ref sink);
        }
    }

    private static void EmitEobRun<TSink>(ref BandState state, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        if (state.EobRun > 0)
        {
            int bits = BitLength(state.EobRun) - 1;
            sink.Ac(0, bits << 4);
            if (bits > 0) sink.Bits(state.EobRun, bits);
            state.EobRun = 0;
        }

        FlushBits(state.Pending, ref sink);
    }

    private static void FlushBits<TSink>(List<int> bits, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        foreach (int bit in bits) sink.Bits(bit, 1);
        bits.Clear();
    }

    private static bool EncodeScans<TSink>(JpegImage image, bool interleaved, int[] classOf, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        JpegComponent[] components = image.Components;
        if (interleaved) return EncodeScan(image, components, classOf, ref sink);
        for (int i = 0; i < components.Length; i++)
        {
            if (!EncodeScan(image, [components[i]], [classOf[i]], ref sink)) return false;
        }

        return true;
    }

    /// <summary>Visits the blocks of one scan in coding order. Both passes must use this, since DC prediction follows the order.</summary>
    private static bool EncodeScan<TSink>(JpegImage image, JpegComponent[] components, int[] classOf, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        Span<int> predictors = stackalloc int[4];
        predictors.Clear();
        if (components.Length == 1)
        {
            JpegComponent c = components[0];
            for (int row = 0; row < c.BlocksPerColumn; row++)
            {
                for (int column = 0; column < c.BlocksPerLine; column++)
                {
                    if (!EncodeBlock(c.Block(row, column), classOf[0], ref predictors[0], ref sink)) return false;
                }
            }

            return true;
        }

        for (int mcuRow = 0; mcuRow < image.McusPerColumn; mcuRow++)
        {
            for (int mcuColumn = 0; mcuColumn < image.McusPerLine; mcuColumn++)
            {
                for (int j = 0; j < components.Length; j++)
                {
                    JpegComponent c = components[j];
                    for (int v = 0; v < c.Vertical; v++)
                    {
                        for (int h = 0; h < c.Horizontal; h++)
                        {
                            Span<short> block = c.Block(mcuRow * c.Vertical + v, mcuColumn * c.Horizontal + h);
                            if (!EncodeBlock(block, classOf[j], ref predictors[j], ref sink)) return false;
                        }
                    }
                }
            }
        }

        return true;
    }

    private static bool EncodeBlock<TSink>(ReadOnlySpan<short> block, int tableClass, ref int predictor, ref TSink sink)
        where TSink : struct, IEntropySink
    {
        int difference = block[0] - predictor;
        predictor = block[0];
        int size = BitLength(difference < 0 ? -difference : difference);
        if (size > MaxDcSize) return false;
        sink.Dc(tableClass, size);
        if (size > 0) sink.Bits(difference < 0 ? difference - 1 : difference, size);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = block[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            for (; run > 15; run -= 16) sink.Ac(tableClass, 0xF0);
            size = BitLength(value < 0 ? -value : value);
            if (size > MaxAcSize) return false;
            sink.Ac(tableClass, (run << 4) | size);
            sink.Bits(value < 0 ? value - 1 : value, size);
            run = 0;
        }

        if (run > 0) sink.Ac(tableClass, 0x00);
        return true;
    }

    private static int BitLength(int value)
    {
#if NET8_0_OR_GREATER
        return 32 - BitOperations.LeadingZeroCount((uint)value);
#else
        int bits = 0;
        for (; value != 0; value >>= 1) bits++;
        return bits;
#endif
    }

    private static void WriteQuantizationTables(ByteWriter output, List<ushort[]> tables)
    {
        int length = 2;
        foreach (ushort[] t in tables) length += Array.Exists(t, v => v > 255) ? 129 : 65;
        output.Marker(0xDB);
        output.UInt16(length);
        for (int i = 0; i < tables.Count; i++)
        {
            bool wide = Array.Exists(tables[i], v => v > 255);
            output.Byte((byte)((wide ? 0x10 : 0) | i));
            foreach (ushort v in tables[i])
            {
                if (wide) output.UInt16(v);
                else output.Byte((byte)v);
            }
        }
    }

    private static void WriteFrameHeader(ByteWriter output, JpegImage image, byte marker, int[] tableOf)
    {
        output.Marker(marker);
        output.UInt16(8 + 3 * image.Components.Length);
        output.Byte(8);
        output.UInt16(image.Height);
        output.UInt16(image.Width);
        output.Byte((byte)image.Components.Length);
        for (int i = 0; i < image.Components.Length; i++)
        {
            JpegComponent c = image.Components[i];
            output.Byte((byte)c.Id);
            output.Byte((byte)((c.Horizontal << 4) | c.Vertical));
            output.Byte((byte)tableOf[i]);
        }
    }

    private static void WriteHuffmanTables(ByteWriter output, JpegHuffmanCode[] dc, JpegHuffmanCode[] ac)
    {
        int length = 2;
        for (int c = 0; c < dc.Length; c++) length += 34 + dc[c].Symbols.Length + ac[c].Symbols.Length;
        output.Marker(0xC4);
        output.UInt16(length);
        for (int c = 0; c < dc.Length; c++)
        {
            WriteTable(output, (byte)c, dc[c]);
            WriteTable(output, (byte)(0x10 | c), ac[c]);
        }

        static void WriteTable(ByteWriter output, byte header, JpegHuffmanCode code)
        {
            output.Byte(header);
            output.Bytes(code.Counts);
            output.Bytes(code.Symbols);
        }
    }

    private static void WriteScanHeader(ByteWriter output, JpegComponent[] components, int[] classOf)
    {
        output.Marker(0xDA);
        output.UInt16(6 + 2 * components.Length);
        output.Byte((byte)components.Length);
        for (int i = 0; i < components.Length; i++)
        {
            output.Byte((byte)components[i].Id);
            output.Byte((byte)((classOf[i] << 4) | classOf[i]));
        }

        output.Byte(0);
        output.Byte(63);
        output.Byte(0);
    }

    private interface IEntropySink
    {
        void Dc(int tableClass, int symbol);

        void Ac(int tableClass, int symbol);

        void Bits(int value, int count);
    }

    /// <summary>First pass: symbol frequencies per table class.</summary>
    private readonly struct Statistics : IEntropySink
    {
        public Statistics(int classes)
        {
            Dc = new long[classes][];
            Ac = new long[classes][];
            for (int c = 0; c < classes; c++)
            {
                Dc[c] = new long[256];
                Ac[c] = new long[256];
            }
        }

        public long[][] Dc { get; }

        public long[][] Ac { get; }

        void IEntropySink.Dc(int tableClass, int symbol) => Dc[tableClass][symbol]++;

        void IEntropySink.Ac(int tableClass, int symbol) => Ac[tableClass][symbol]++;

        public void Bits(int value, int count) { }
    }

    /// <summary>Second pass: Huffman codes and raw bits, with 0xFF byte stuffing.</summary>
    private struct EntropyWriter : IEntropySink
    {
        private readonly ByteWriter _output;
        private readonly JpegHuffmanCode[] _dc;
        private readonly JpegHuffmanCode[] _ac;
        private ulong _accumulator;
        private int _count;

        public EntropyWriter(ByteWriter output, JpegHuffmanCode[] dc, JpegHuffmanCode[] ac)
        {
            _output = output;
            _dc = dc;
            _ac = ac;
        }

        public void Dc(int tableClass, int symbol) => Bits(_dc[tableClass].Codes[symbol], _dc[tableClass].Lengths[symbol]);

        public void Ac(int tableClass, int symbol) => Bits(_ac[tableClass].Codes[symbol], _ac[tableClass].Lengths[symbol]);

        public void Bits(int value, int count)
        {
            _accumulator = (_accumulator << count) | ((uint)value & ((1u << count) - 1));
            _count += count;
            while (_count >= 8)
            {
                _count -= 8;
                byte b = (byte)(_accumulator >> _count);
                _output.Byte(b);
                if (b == 0xFF) _output.Byte(0);
            }
        }

        /// <summary>Pads the last byte with one bits, as the standard asks, before a marker.</summary>
        public void Flush()
        {
            if (_count > 0) Bits((1 << (8 - _count)) - 1, 8 - _count);
        }
    }

    private sealed class ByteWriter : IDisposable
    {
        private byte[] _buffer;
        private int _length;

        public ByteWriter(int capacity) => _buffer = ArrayPool<byte>.Shared.Rent(capacity);

        public void Byte(byte value)
        {
            if (_length == _buffer.Length) Grow(1);
            _buffer[_length++] = value;
        }

        public void UInt16(int value)
        {
            Byte((byte)(value >> 8));
            Byte((byte)value);
        }

        public void Marker(byte code)
        {
            Byte(0xFF);
            Byte(code);
        }

        public void Bytes(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length) Grow(data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        private void Grow(int needed)
        {
            long size = Math.Max((long)_length + needed, (long)_buffer.Length * 2);
            if (size > Compat.MaxArrayLength) throw new InvalidOperationException("Encoded JPEG exceeds the maximum array size.");
            byte[] grown = ArrayPool<byte>.Shared.Rent((int)size);
            _buffer.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }
    }
}
