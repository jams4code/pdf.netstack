using System.Buffers.Binary;

namespace PdfNetStack.Imaging;

/// <summary>
/// Reads a JPEG down to its quantized DCT coefficients, without an inverse DCT. Supports baseline,
/// extended sequential and progressive Huffman coded frames with 8 bit samples, restart markers
/// included. Arithmetic coding, lossless, hierarchical and 12 bit frames are rejected. Damaged
/// entropy data is an error rather than something to guess around, so callers leave it untouched.
/// </summary>
internal ref struct JpegReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _limit;
    private readonly ushort[]?[] _tables = new ushort[]?[4];
    private readonly JpegHuffmanTable?[] _dc = new JpegHuffmanTable?[4];
    private readonly JpegHuffmanTable?[] _ac = new JpegHuffmanTable?[4];
    private int _pos;
    private int _restartInterval;

    // Entropy decoder state. Once a marker or the end of data is reached the buffer is padded
    // with zero bits; consuming any of those means the data ended early.
    private ulong _bits;
    private int _bitCount;
    private int _phantomBits;
    private bool _atMarker;
    private int _eobRun;

    private JpegReader(ReadOnlySpan<byte> data, long limit)
    {
        _data = data;
        _limit = limit;
    }

    private enum ScanMode
    {
        Sequential,
        DcFirst,
        DcRefine,
        AcFirst,
        AcRefine,
    }

    /// <param name="coefficientByteLimit">Ceiling for the coefficient buffers, two bytes per coefficient.</param>
    /// <exception cref="JpegFormatException">The data is damaged or uses an unsupported coding process.</exception>
    public static JpegImage Read(ReadOnlySpan<byte> data, long coefficientByteLimit) => new JpegReader(data, coefficientByteLimit).ReadImage();

    private JpegImage ReadImage()
    {
        if (_data.Length < 4 || _data[0] != 0xFF || _data[1] != 0xD8) throw new JpegFormatException("Not a JPEG: missing SOI marker.");
        _pos = 2;

        JpegImage? image = null;
        var segments = new List<JpegSegment>();
        bool jfif = false;
        int? adobe = null;
        int scans = 0;
        try
        {
            bool done = false;
            while (!done)
            {
                int marker = NextMarker();
                switch (marker)
                {
                    case < 0:
                        // A missing EOI after complete scans is common and harmless; decoders accept it.
                        if (scans == 0) throw new JpegFormatException("JPEG data ends before the first scan.");
                        done = true;
                        break;
                    case 0xD9:
                        if (scans == 0) throw new JpegFormatException("JPEG has no scan.");
                        done = true;
                        break;
                    case 0xC0 or 0xC1 or 0xC2:
                        if (image is not null) throw new JpegFormatException("JPEG has more than one frame.");
                        image = ReadFrame(marker);
                        break;
                    case 0xC4:
                        ReadHuffmanTables();
                        break;
                    case 0xDB:
                        ReadQuantizationTables();
                        break;
                    case 0xDD:
                        ReadRestartInterval();
                        break;
                    case 0xDA:
                        ReadScan(image ?? throw new JpegFormatException("JPEG scan before frame header."));
                        scans++;
                        break;
                    case >= 0xD0 and <= 0xD7:
                        // A stray restart marker between scans carries no data; libjpeg skips it too.
                        break;
                    case (>= 0xE0 and <= 0xEF) or 0xFE:
                        ReadApplication(marker, segments, ref jfif, ref adobe);
                        break;
                    default:
                        throw new JpegFormatException(Compat.Format("Unsupported JPEG marker FF{0:X2}.", marker));
                }
            }
        }
        catch
        {
            image?.Dispose();
            throw;
        }

        image!.Segments.AddRange(segments);
        image.HasJfif = jfif;
        image.AdobeTransform = adobe;
        return image;
    }

    /// <summary>Returns the next marker code, or -1 at the end of the data.</summary>
    private int NextMarker()
    {
        if (_pos >= _data.Length) return -1;
        if (_data[_pos] != 0xFF) throw new JpegFormatException("Expected a JPEG marker.");
        while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
        return _pos < _data.Length ? _data[_pos++] : -1;
    }

    private ReadOnlySpan<byte> Segment()
    {
        if (_pos + 2 > _data.Length) throw new JpegFormatException("JPEG segment is truncated.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_pos));
        if (length < 2 || _pos + length > _data.Length) throw new JpegFormatException("JPEG segment length is invalid.");
        ReadOnlySpan<byte> segment = _data.Slice(_pos + 2, length - 2);
        _pos += length;
        return segment;
    }

    private void ReadApplication(int marker, List<JpegSegment> segments, ref bool jfif, ref int? adobe)
    {
        ReadOnlySpan<byte> s = Segment();
        if (marker == 0xE0 && s.StartsWith("JFIF\0"u8))
        {
            jfif = true;
            segments.Add(new JpegSegment(0xE0, s.ToArray()));
        }
        else if (marker == 0xEE && s.Length >= 12 && s.StartsWith("Adobe"u8))
        {
            adobe = s[11];
            segments.Add(new JpegSegment(0xEE, s.ToArray()));
        }
    }

    private void ReadQuantizationTables()
    {
        ReadOnlySpan<byte> s = Segment();
        int i = 0;
        while (i < s.Length)
        {
            int precision = s[i] >> 4;
            int index = s[i] & 15;
            i++;
            if (precision > 1 || index > 3) throw new JpegFormatException("Invalid quantization table header.");
            int size = precision == 0 ? 64 : 128;
            if (i + size > s.Length) throw new JpegFormatException("Quantization table is truncated.");
            var table = new ushort[64];
            for (int k = 0; k < 64; k++)
            {
                table[k] = precision == 0 ? s[i + k] : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 2 * k));
                if (table[k] == 0) throw new JpegFormatException("Quantization table contains zero.");
            }

            _tables[index] = table;
            i += size;
        }
    }

    private void ReadHuffmanTables()
    {
        ReadOnlySpan<byte> s = Segment();
        int i = 0;
        while (i < s.Length)
        {
            if (i + 17 > s.Length) throw new JpegFormatException("Huffman table is truncated.");
            int tableClass = s[i] >> 4;
            int index = s[i] & 15;
            if (tableClass > 1 || index > 3) throw new JpegFormatException("Invalid Huffman table header.");
            ReadOnlySpan<byte> counts = s.Slice(i + 1, 16);
            int total = 0;
            foreach (byte c in counts) total += c;
            if (total > 256 || i + 17 + total > s.Length) throw new JpegFormatException("Huffman table is truncated.");
            var table = new JpegHuffmanTable(counts, s.Slice(i + 17, total));
            if (tableClass == 0) _dc[index] = table;
            else _ac[index] = table;
            i += 17 + total;
        }
    }

    private void ReadRestartInterval()
    {
        ReadOnlySpan<byte> s = Segment();
        if (s.Length != 2) throw new JpegFormatException("Invalid restart interval segment.");
        _restartInterval = BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    private JpegImage ReadFrame(int marker)
    {
        ReadOnlySpan<byte> s = Segment();
        if (s.Length < 6) throw new JpegFormatException("Frame header is truncated.");
        if (s[0] != 8) throw new JpegFormatException("Only 8 bit JPEG samples are supported.");
        int height = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(1));
        int width = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(3));
        int count = s[5];
        if (height == 0) throw new JpegFormatException("JPEG height defined by DNL is not supported.");
        if (width == 0) throw new JpegFormatException("JPEG width is zero.");
        if (count is < 1 or > 4 || s.Length != 6 + 3 * count) throw new JpegFormatException("Invalid JPEG component count.");

        int maxH = 1, maxV = 1;
        for (int i = 0; i < count; i++)
        {
            int h = s[7 + 3 * i] >> 4, v = s[7 + 3 * i] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4 || s[8 + 3 * i] > 3) throw new JpegFormatException("Invalid JPEG component sampling.");
            for (int j = 0; j < i; j++)
            {
                if (s[6 + 3 * j] == s[6 + 3 * i]) throw new JpegFormatException("Duplicate JPEG component id.");
            }

            maxH = Math.Max(maxH, h);
            maxV = Math.Max(maxV, v);
        }

        long mcusX = (width + 8L * maxH - 1) / (8 * maxH);
        long mcusY = (height + 8L * maxV - 1) / (8 * maxV);
        long bytes = 0;
        for (int i = 0; i < count; i++)
        {
            bytes += mcusX * (s[7 + 3 * i] >> 4) * mcusY * (s[7 + 3 * i] & 15) * 64 * sizeof(short);
        }

        if (bytes > _limit || bytes > Compat.MaxArrayLength) throw new JpegFormatException("JPEG exceeds the decode limit.");

        var components = new JpegComponent[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                int h = s[7 + 3 * i] >> 4, v = s[7 + 3 * i] & 15;
                int samplesX = (width * h + maxH - 1) / maxH;
                int samplesY = (height * v + maxV - 1) / maxV;
                components[i] = new JpegComponent(s[6 + 3 * i], h, v, s[8 + 3 * i],
                    (samplesX + 7) / 8, (samplesY + 7) / 8, (int)mcusX * h, (int)mcusY * v);
            }
        }
        catch
        {
            foreach (JpegComponent? c in components) c?.Release();
            throw;
        }

        JpegCoding coding = marker switch
        {
            0xC0 => JpegCoding.Baseline,
            0xC1 => JpegCoding.ExtendedSequential,
            _ => JpegCoding.Progressive,
        };
        return new JpegImage(coding, width, height, components);
    }

    private void ReadScan(JpegImage image)
    {
        ReadOnlySpan<byte> s = Segment();
        int count = s.Length > 0 ? s[0] : 0;
        if (count is < 1 or > 4 || s.Length != 4 + 2 * count) throw new JpegFormatException("Invalid scan header.");

        var components = new JpegComponent[count];
        var dc = new JpegHuffmanTable?[count];
        var ac = new JpegHuffmanTable?[count];
        int start = s[1 + 2 * count];
        int end = s[2 + 2 * count];
        int high = s[3 + 2 * count] >> 4;
        int low = s[3 + 2 * count] & 15;

        ScanMode mode;
        if (image.Coding != JpegCoding.Progressive)
        {
            if (start != 0 || end != 63 || high != 0 || low != 0) throw new JpegFormatException("Invalid sequential scan parameters.");
            mode = ScanMode.Sequential;
        }
        else
        {
            bool dcScan = start == 0;
            if (dcScan ? end != 0 : end < start || end > 63 || count != 1) throw new JpegFormatException("Invalid progressive spectral selection.");
            if (high > 13 || low > 13 || (high != 0 && low != high - 1)) throw new JpegFormatException("Invalid progressive successive approximation.");
            mode = dcScan ? (high == 0 ? ScanMode.DcFirst : ScanMode.DcRefine) : (high == 0 ? ScanMode.AcFirst : ScanMode.AcRefine);
        }

        int blocksPerMcu = 0;
        for (int j = 0; j < count; j++)
        {
            int id = s[1 + 2 * j];
            JpegComponent component = Array.Find(image.Components, c => c.Id == id) ?? throw new JpegFormatException("Scan names an unknown component.");
            if (Array.IndexOf(components, component) >= 0) throw new JpegFormatException("Scan names a component twice.");
            components[j] = component;
            blocksPerMcu += component.Horizontal * component.Vertical;

            if (!component.Started)
            {
                component.Quantization = (ushort[])(_tables[component.TableIndex] ?? throw new JpegFormatException("Component uses an undefined quantization table.")).Clone();
                component.Started = true;
            }

            int dcIndex = s[2 + 2 * j] >> 4, acIndex = s[2 + 2 * j] & 15;
            if (dcIndex > 3 || acIndex > 3) throw new JpegFormatException("Invalid Huffman table selector.");
            bool needsDc = mode is ScanMode.Sequential or ScanMode.DcFirst;
            bool needsAc = mode is ScanMode.Sequential or ScanMode.AcFirst or ScanMode.AcRefine;
            if (needsDc) dc[j] = _dc[dcIndex] ?? throw new JpegFormatException("Scan uses an undefined DC table.");
            if (needsAc) ac[j] = _ac[acIndex] ?? throw new JpegFormatException("Scan uses an undefined AC table.");
        }

        if (count > 1 && blocksPerMcu > 10) throw new JpegFormatException("Interleaved scan has more than 10 blocks per MCU.");
        DecodeScan(image, components, dc, ac, mode, start, end, low);
    }

    private void DecodeScan(JpegImage image, JpegComponent[] components, JpegHuffmanTable?[] dc, JpegHuffmanTable?[] ac, ScanMode mode, int start, int end, int low)
    {
        _bits = 0;
        _bitCount = 0;
        _phantomBits = 0;
        _atMarker = false;
        _eobRun = 0;
        Span<int> predictors = stackalloc int[4];
        predictors.Clear();
        int nextRestart = 0;

        JpegComponent single = components[0];
        int mcus = components.Length == 1 ? single.BlocksPerLine * single.BlocksPerColumn : image.McusPerLine * image.McusPerColumn;
        for (int mcu = 0; mcu < mcus; mcu++)
        {
            if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
            {
                Restart(ref nextRestart);
                predictors.Clear();
                _eobRun = 0;
            }

            if (components.Length == 1)
            {
                // A single component scan covers the component's own blocks, not whole MCUs.
                DecodeBlock(single.Block(mcu / single.BlocksPerLine, mcu % single.BlocksPerLine), dc[0], ac[0], ref predictors[0], mode, start, end, low);
                continue;
            }

            int mcuRow = mcu / image.McusPerLine, mcuColumn = mcu % image.McusPerLine;
            for (int j = 0; j < components.Length; j++)
            {
                JpegComponent c = components[j];
                for (int v = 0; v < c.Vertical; v++)
                {
                    for (int h = 0; h < c.Horizontal; h++)
                    {
                        DecodeBlock(c.Block(mcuRow * c.Vertical + v, mcuColumn * c.Horizontal + h), dc[j], ac[j], ref predictors[j], mode, start, end, low);
                    }
                }
            }
        }

        // Leave the position on the marker that ends the scan; bytes before it carry no blocks.
        if (!_atMarker) SkipToMarker();
    }

    // ReadScan guarantees the tables each mode needs are present.
    private void DecodeBlock(Span<short> block, JpegHuffmanTable? dc, JpegHuffmanTable? ac, ref int predictor, ScanMode mode, int start, int end, int low)
    {
        switch (mode)
        {
            case ScanMode.Sequential:
                DecodeDc(block, dc!, ref predictor, 0);
                DecodeAcFirst(block, ac!, 1, 63, 0, progressive: false);
                break;
            case ScanMode.DcFirst:
                DecodeDc(block, dc!, ref predictor, low);
                break;
            case ScanMode.DcRefine:
                if (ReadBit()) block[0] = (short)((ushort)block[0] | (1 << low));
                break;
            case ScanMode.AcFirst:
                if (_eobRun > 0) _eobRun--;
                else DecodeAcFirst(block, ac!, start, end, low, progressive: true);
                break;
            default:
                DecodeAcRefine(block, ac!, start, end, low);
                break;
        }
    }

    private void DecodeDc(Span<short> block, JpegHuffmanTable table, ref int predictor, int low)
    {
        int size = DecodeSymbol(table);
        if (size > 15) throw new JpegFormatException("Invalid DC difference size.");
        predictor += size == 0 ? 0 : Extend(Receive(size), size);
        if (predictor is < short.MinValue or > short.MaxValue) throw new JpegFormatException("DC coefficient out of range.");
        block[0] = ToCoefficient(predictor << low);
    }

    /// <summary>Sequential AC decoding, and the first pass of a progressive AC band.</summary>
    private void DecodeAcFirst(Span<short> block, JpegHuffmanTable table, int start, int end, int low, bool progressive)
    {
        for (int k = start; k <= end;)
        {
            int symbol = DecodeSymbol(table);
            int run = symbol >> 4, size = symbol & 15;
            if (size == 0)
            {
                if (run == 15)
                {
                    k += 16;
                    continue;
                }

                // End of block. Only progressive bands may extend it into a run over later blocks;
                // a sequential decoder treats every other zero size symbol as a plain EOB.
                if (progressive) _eobRun = (1 << run) - 1 + (run > 0 ? Receive(run) : 0);
                return;
            }

            k += run;
            if (k > end) throw new JpegFormatException("AC coefficient run exceeds the block.");
            block[k++] = ToCoefficient(Extend(Receive(size), size) * (1 << low));
        }
    }

    /// <summary>Successive approximation refinement of a progressive AC band (ITU T.81 G.1.2.3).</summary>
    private void DecodeAcRefine(Span<short> block, JpegHuffmanTable table, int start, int end, int low)
    {
        int plus = 1 << low, minus = -plus;
        int k = start;
        if (_eobRun == 0)
        {
            for (; k <= end; k++)
            {
                int symbol = DecodeSymbol(table);
                int run = symbol >> 4, size = symbol & 15;
                int value = 0;
                if (size != 0)
                {
                    if (size != 1) throw new JpegFormatException("Invalid AC refinement size.");
                    value = ReadBit() ? plus : minus;
                }
                else if (run != 15)
                {
                    _eobRun = (1 << run) + (run > 0 ? Receive(run) : 0);
                    break;
                }

                // Refine nonzero history while skipping `run` zero coefficients; land on the next zero.
                for (; k <= end; k++)
                {
                    if (block[k] != 0) Refine(ref block[k], plus, minus);
                    else if (run-- == 0) break;
                }

                if (value != 0)
                {
                    if (k > end) throw new JpegFormatException("AC refinement exceeds the band.");
                    block[k] = (short)value;
                }
            }
        }

        if (_eobRun > 0)
        {
            for (; k <= end; k++)
            {
                if (block[k] != 0) Refine(ref block[k], plus, minus);
            }

            _eobRun--;
        }
    }

    private void Refine(ref short coefficient, int plus, int minus)
    {
        if (ReadBit() && (coefficient & plus) == 0) coefficient = ToCoefficient(coefficient + (coefficient >= 0 ? plus : minus));
    }

    private void Restart(ref int expected)
    {
        // The marker is byte aligned: bits left in the buffer are padding.
        _bits = 0;
        _bitCount = 0;
        _phantomBits = 0;
        if (!_atMarker) SkipToMarker();
        _atMarker = false;
        while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
        if (_pos >= _data.Length || _data[_pos] != 0xD0 + expected) throw new JpegFormatException("Restart marker missing or out of sequence.");
        _pos++;
        expected = (expected + 1) & 7;
    }

    private void SkipToMarker()
    {
        while (_pos + 1 < _data.Length && !(_data[_pos] == 0xFF && _data[_pos + 1] != 0x00)) _pos++;
        if (_pos + 1 >= _data.Length) _pos = _data.Length;
    }

    private void Fill()
    {
        while (_bitCount <= 56)
        {
            int b = 0;
            if (!_atMarker && _pos < _data.Length && _data[_pos] != 0xFF)
            {
                b = _data[_pos++];
            }
            else if (!_atMarker && _pos + 1 < _data.Length && _data[_pos + 1] == 0x00)
            {
                b = 0xFF;
                _pos += 2;
            }
            else
            {
                _atMarker = true;
                _phantomBits += 8;
            }

            _bits = (_bits << 8) | (byte)b;
            _bitCount += 8;
        }
    }

    private void Consume(int count)
    {
        _bitCount -= count;
        if (_bitCount < _phantomBits) throw new JpegFormatException("JPEG entropy coded data ends early.");
    }

    private int DecodeSymbol(JpegHuffmanTable table)
    {
        if (_bitCount < 16) Fill();
        int length = table.Decode((int)(_bits >> (_bitCount - 16)) & 0xFFFF, out int symbol);
        Consume(length);
        return symbol;
    }

    private int Receive(int count)
    {
        if (_bitCount < count) Fill();
        int value = (int)(_bits >> (_bitCount - count)) & ((1 << count) - 1);
        Consume(count);
        return value;
    }

    private bool ReadBit() => Receive(1) != 0;

    private static int Extend(int value, int size) => value < 1 << (size - 1) ? value - (1 << size) + 1 : value;

    private static short ToCoefficient(int value) =>
        value is >= short.MinValue and <= short.MaxValue ? (short)value : throw new JpegFormatException("DCT coefficient out of range.");
}
