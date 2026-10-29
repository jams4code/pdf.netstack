namespace PdfNetStack.Imaging;

/// <summary>A TIFF page whose compressed data a PDF filter reads as is: CCITT fax or JPEG.</summary>
internal sealed class TiffEncodedImage
{
    public TiffEncodedImage(string filter, byte[] data, int width, int height, int colors)
    {
        Filter = filter;
        Data = data;
        Width = width;
        Height = height;
        Colors = colors;
    }

    /// <summary>CCITTFaxDecode or DCTDecode.</summary>
    public string Filter { get; }

    public byte[] Data { get; }

    public int Width { get; }

    public int Height { get; }

    public int Colors { get; }

    /// <summary>The CCITTFaxDecode parameters, for fax data.</summary>
    public CcittParameters? Ccitt { get; init; }

    /// <summary>The DCTDecode /ColorTransform, or null for the reader's default.</summary>
    public int? ColorTransform { get; init; }

    /// <summary>True when gray samples read 0 as white.</summary>
    public bool InvertedGray { get; init; }
}

/// <summary>
/// Reads TIFF 6.0 baseline images and the common extensions (TIFF Revision 6.0, Adobe 1992; Adobe
/// TIFF Technical Note 2 for new style JPEG; BigTIFF): strips and tiles, chunky and planar
/// samples, no compression, PackBits, LZW and Deflate with or without the horizontal predictor,
/// CCITT modified Huffman, Group 3 and Group 4, and JPEG. Bilevel, gray, palette, RGB, CMYK and
/// YCbCr (JPEG) pixels of 1 to 16 bits, with an alpha channel. Every page of a multi-page file can
/// be read.
/// </summary>
/// <remarks>
/// Old style JPEG (compression 6), floating point and signed samples, CIE Lab and uncompressed
/// subsampled YCbCr are not read. A page made of one strip of fax or JPEG data is handed over
/// compressed (<see cref="TiffPage.PassThrough"/>), with no decoding.
/// </remarks>
internal static class TiffDecoder
{
    /// <summary>The most pages followed in one file.</summary>
    public const int MaxPages = 1 << 16;

    public static bool IsTiff(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] is 42 or 43 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] is 42 or 43));

    /// <exception cref="FormatException">The data is not a TIFF or has no page.</exception>
    public static int CountPages(byte[] data) => Directories(data).Count;

    /// <exception cref="FormatException">The data is not a TIFF, or the page is missing or not readable.</exception>
    public static TiffPage ReadPage(byte[] data, int index)
    {
        List<long> directories = Directories(data);
        if (index < 0 || index >= directories.Count) throw new FormatException($"The TIFF has {directories.Count} pages; page {index} does not exist.");
        return new TiffPage(new TiffReader(data), directories[index]);
    }

    /// <summary>The offsets of the image file directories in chain order, stopping at a loop or a bad link.</summary>
    private static List<long> Directories(byte[] data)
    {
        if (!IsTiff(data)) throw new FormatException("Not a TIFF image.");
        var reader = new TiffReader(data);
        long offset = reader.Big ? (data.Length >= 16 ? reader.U64(8) : 0) : reader.U32(4);
        var seen = new HashSet<long>();
        var list = new List<long>();
        while (offset > 0 && list.Count < MaxPages && seen.Add(offset))
        {
            // Offsets can be as large as a long holds: compare without adding to them.
            long entries = reader.Big ? (offset <= data.Length - 8 ? reader.U64(offset) : -1) : (offset <= data.Length - 2 ? reader.U16(offset) : -1);
            int countSize = reader.Big ? 8 : 2, entrySize = reader.Big ? 20 : 12;
            if (entries < 0 || entries > (data.Length - offset - countSize) / entrySize) break;
            list.Add(offset);
            long next = offset + countSize + entries * entrySize;
            offset = next + (reader.Big ? 8 : 4) > data.Length ? 0 : reader.Big ? reader.U64(next) : reader.U32(next);
        }

        if (list.Count == 0) throw new FormatException("TIFF has no readable image directory.");
        return list;
    }
}

/// <summary>Byte order and offset size aware access to a TIFF file.</summary>
internal readonly struct TiffReader
{
    public TiffReader(byte[] data)
    {
        Data = data;
        Little = data[0] == 'I';
        Big = (Little ? data[2] : data[3]) == 43;
    }

    public byte[] Data { get; }

    public bool Little { get; }

    /// <summary>BigTIFF: 8 byte offsets and counts.</summary>
    public bool Big { get; }

    public int U16(long at) => Little ? Data[at] | Data[at + 1] << 8 : Data[at] << 8 | Data[at + 1];

    public long U32(long at) => (uint)(Little
        ? Data[at] | Data[at + 1] << 8 | Data[at + 2] << 16 | Data[at + 3] << 24
        : Data[at] << 24 | Data[at + 1] << 16 | Data[at + 2] << 8 | Data[at + 3]);

    /// <summary>An unsigned 64 bit value, saturated at long.MaxValue: such offsets are out of range anyway.</summary>
    public long U64(long at)
    {
        ulong low = (ulong)U32(at + (Little ? 0 : 4)), high = (ulong)U32(at + (Little ? 4 : 0));
        ulong v = high << 32 | low;
        return v > long.MaxValue ? long.MaxValue : (long)v;
    }
}

/// <summary>One page (image file directory) of a TIFF, its tags read and checked.</summary>
internal sealed class TiffPage
{
    private const int None = 1, CcittRle = 2, CcittGroup3 = 3, CcittGroup4 = 4, Lzw = 5, OldJpeg = 6, Jpeg = 7, Deflate = 8, PackBits = 32773, OldDeflate = 32946;

    private static readonly byte[] Reversed = BuildReversed();

    private readonly TiffReader _r;
    private readonly Dictionary<int, (int Type, long Count, long At)> _tags = new();
    private readonly long[] _offsets;
    private readonly long[] _counts;
    private readonly bool _tiled;
    private readonly int _chunkWidth;
    private readonly int _chunkHeight;
    private readonly int _across;
    private readonly int _down;
    private readonly int _planes;
    private readonly int _alphaIndex = -1;
    private readonly bool _associatedAlpha;

    internal TiffPage(TiffReader reader, long directory)
    {
        _r = reader;
        ReadDirectory(directory);

        long width = Int(256, 0), height = Int(257, 0);
        Samples = (int)Math.Min(64, Int(277, 1));
        long[] bits = Ints(258);
        Bits = bits.Length > 0 ? (int)Math.Min(64, bits[0]) : 1;
        if (bits.Any(b => b != Bits)) throw new FormatException("TIFF samples of different sizes are not supported.");
        Compression = (int)Math.Min(int.MaxValue, Int(259, None));
        Photometric = (int)Int(262, Samples >= 3 ? 2 : 1);
        Predictor = (int)Int(317, 1);
        _planes = Int(284, 1) == 2 && Samples > 1 ? Samples : 1;
        FillOrder = (int)Int(266, 1);
        Orientation = (int)Int(274, 1) is int o and >= 1 and <= 8 ? o : 1;

        Colors = Photometric switch
        {
            0 or 1 or 3 => 1,
            2 or 6 => 3,
            5 when Int(332, 1) == 1 => 4,
            _ => throw new FormatException($"TIFF photometric interpretation {Photometric} is not supported."),
        };
        if (Samples < Colors || Samples > 16) throw new FormatException("TIFF samples per pixel do not match the color space.");
        if (Ints(339).Any(f => f is not (1 or 4))) throw new FormatException("TIFF floating point and signed samples are not supported.");
        if (Photometric == 6 && Compression != Jpeg) throw new FormatException("TIFF YCbCr is only supported with JPEG compression.");

        // Below 8 bits only a lone sample is read: extra samples are split off a byte at a time.
        bool validBits = Samples == 1
            ? Photometric == 3 ? Bits is 1 or 2 or 4 or 8 : Bits is 1 or 2 or 4 or 8 or 16
            : Photometric == 3 ? Bits == 8 : Bits is 8 or 16;
        if (!validBits) throw new FormatException($"TIFF with {Bits} bit samples is not supported for this color space.");
        if (Compression is CcittRle or CcittGroup3 or CcittGroup4 && (Bits != 1 || Samples != 1)) throw new FormatException("TIFF fax data must be 1 bit.");
        if (Compression is CcittRle or CcittGroup3 or CcittGroup4 && Int(256, 0) > CcittDecoder.MaxColumns) throw new FormatException("TIFF fax page is too wide.");
        if (Compression == Jpeg && (Bits != 8 || Samples != Colors)) throw new FormatException("TIFF JPEG data must be 8 bit without extra samples.");
        if (Compression is not (None or CcittRle or CcittGroup3 or CcittGroup4 or Lzw or Jpeg or Deflate or PackBits or OldDeflate))
        {
            throw new FormatException(Compression == OldJpeg ? "Old style TIFF JPEG is not supported." : $"TIFF compression {Compression} is not supported.");
        }

        if (Predictor != 1 && (Predictor != 2 || Bits < 8)) throw new FormatException($"TIFF predictor {Predictor} is not supported with {Bits} bit samples.");
        DecodedImage.CheckSize(width, height, Math.Max(1, Samples * Bits / 8), "TIFF");
        Width = (int)width;
        Height = (int)height;

        // The first extra sample is alpha when the file says so: associated (premultiplied) or not.
        long[] extra = Ints(338);
        if (Samples > Colors && extra.Length > 0 && extra[0] is 1 or 2)
        {
            _alphaIndex = Colors;
            _associatedAlpha = extra[0] == 1;
        }

        if (Photometric == 3)
        {
            long[] map = Ints(320);
            int entries = 1 << Bits;
            if (map.Length < 3 * entries) throw new FormatException("TIFF color map is missing or short.");

            // Colors are 16 bit; a map whose values all fit in 8 bits was written that way by mistake.
            int shift = map.Take(3 * entries).All(v => v < 256) ? 0 : 8;
            Palette = new byte[3 * entries];
            for (int i = 0; i < entries; i++)
            {
                for (int c = 0; c < 3; c++) Palette[(3 * i) + c] = (byte)(map[(c * entries) + i] >> shift);
            }
        }

        _tiled = _tags.ContainsKey(322) || _tags.ContainsKey(324);
        if (_tiled)
        {
            long tw = Int(322, 0), tl = Int(323, 0);
            if (tw <= 0 || tl <= 0 || tw > 1 << 16 || tl > 1 << 16 || DecodedImage.RowBytes((int)tw, ChunkSamples, Bits) * tl > 1 << 26) throw new FormatException("TIFF tile size is invalid.");
            if (tw * Bits * ChunkSamples % 8 != 0) throw new FormatException("TIFF tiles must hold whole bytes per row.");
            (_chunkWidth, _chunkHeight) = ((int)tw, (int)tl);
            _offsets = Ints(324);
            _counts = Ints(325);
        }
        else
        {
            long rows = Int(278, Height);
            (_chunkWidth, _chunkHeight) = (Width, (int)(rows <= 0 || rows > Height ? Height : rows));
            _offsets = Ints(273);
            _counts = Ints(279);
        }

        _across = (Width + _chunkWidth - 1) / _chunkWidth;
        _down = (Height + _chunkHeight - 1) / _chunkHeight;
        long chunks = (long)_across * _down * _planes;
        if (_offsets.Length < chunks) throw new FormatException("TIFF has fewer strips or tiles than its size needs.");

        Icc = Bytes(34675);
        JpegTables = Bytes(347);
        double unit = Int(296, 2) switch { 2 => 1, 3 => 2.54, _ => 0 };
        DpiX = Rational(282) * unit;
        DpiY = Rational(283) * unit;
    }

    public int Width { get; }

    public int Height { get; }

    public int Bits { get; }

    public int Samples { get; }

    /// <summary>Color samples per pixel without extra samples: 1, 3 or 4.</summary>
    public int Colors { get; }

    public int Compression { get; }

    public int Photometric { get; }

    public int Predictor { get; }

    public int FillOrder { get; }

    public int Orientation { get; }

    public byte[]? Palette { get; }

    public byte[]? Icc { get; }

    public byte[]? JpegTables { get; }

    public double DpiX { get; }

    public double DpiY { get; }

    public bool HasAlpha => _alphaIndex >= 0;

    private int ChunkSamples => _planes > 1 ? 1 : Samples;

    /// <summary>
    /// The page's data for a PDF filter, when it is one strip of fax data, or of JPEG data whose
    /// frame matches the page; null when the page must be decoded instead.
    /// </summary>
    public TiffEncodedImage? PassThrough()
    {
        if (_tiled || _down != 1 || _planes != 1) return null;
        byte[] data = Chunk(0).ToArray();
        if (FillOrder == 2) Reverse(data);

        if (Compression is CcittRle or CcittGroup3 or CcittGroup4)
        {
            long options = Int(292, 0);
            if (Compression == CcittGroup3 && (options & 2) != 0) return null;
            return new TiffEncodedImage("CCITTFaxDecode", data, Width, Height, 1) { Ccitt = Fax(Width, Height), InvertedGray = Photometric == 1 };
        }

        if (Compression == Jpeg)
        {
            byte[] jpeg = WithTables(data);
            try
            {
                JpegHeader header = JpegHeader.Read(jpeg);
                if (header.Width != Width || header.Height != Height || header.Components != Colors) return null;
            }
            catch (FormatException)
            {
                return null;
            }

            return new TiffEncodedImage("DCTDecode", jpeg, Width, Height, Colors) { ColorTransform = JpegColorTransform, InvertedGray = Photometric == 0 };
        }

        return null;
    }

    /// <summary>The page decoded: color samples at the file's depth, alpha apart.</summary>
    /// <exception cref="FormatException">The data is damaged.</exception>
    public DecodedImage Decode()
    {
        int samples = Samples, bits = Bits, bytes = bits / 8;
        long rowBytes = DecodedImage.RowBytes(Width, samples, bits);
        var chunky = new byte[rowBytes * Height];
        int perPlane = _across * _down;
        for (int plane = 0; plane < _planes; plane++)
        {
            for (int i = 0; i < perPlane; i++)
            {
                int x0 = i % _across * _chunkWidth, y0 = i / _across * _chunkHeight;
                int rows = _tiled ? _chunkHeight : Math.Min(_chunkHeight, Height - y0);
                int cols = Math.Min(_chunkWidth, Width - x0), used = Math.Min(rows, Height - y0);
                byte[] chunk = DecodeChunk((plane * perPlane) + i, rows);
                long chunkRow = DecodedImage.RowBytes(_chunkWidth, ChunkSamples, bits);
                for (int r = 0; r < used; r++)
                {
                    long to = (y0 + r) * rowBytes;
                    if (_planes == 1)
                    {
                        long count = DecodedImage.RowBytes(cols, samples, bits);
                        Array.Copy(chunk, r * chunkRow, chunky, to + ((long)x0 * samples * bits / 8), count);
                        continue;
                    }

                    for (int c = 0; c < cols; c++) Array.Copy(chunk, (r * chunkRow) + ((long)c * bytes), chunky, to + ((((long)x0 + c) * samples) + plane) * bytes, bytes);
                }
            }
        }

        return Split(chunky);
    }

    /// <summary>Color samples, and alpha when there is one; other extra samples are dropped.</summary>
    private DecodedImage Split(byte[] chunky)
    {
        int colors = Colors, bits = Bits, bytes = bits / 8;
        bool inverted = Compression is CcittRle or CcittGroup3 or CcittGroup4 ? Photometric == 1 : Photometric == 0;
        if (Samples == colors)
        {
            return new DecodedImage(Width, Height, colors, bits, chunky) { Palette = Palette, InvertedGray = inverted, IccProfile = Icc, DpiX = DpiX, DpiY = DpiY, Orientation = Orientation };
        }

        long pixels = (long)Width * Height;
        int pixel = Samples * bytes, color = colors * bytes;
        var output = new byte[pixels * color];
        byte[]? alpha = _alphaIndex >= 0 ? new byte[pixels * bytes] : null;
        for (long p = 0; p < pixels; p++)
        {
            Array.Copy(chunky, p * pixel, output, p * color, color);
            if (alpha is null) continue;
            Array.Copy(chunky, (p * pixel) + (_alphaIndex * bytes), alpha, p * bytes, bytes);
            if (_associatedAlpha) Unpremultiply(output, p * color, colors, alpha, p * bytes, bytes);
        }

        if (alpha is not null && bytes == 1) alpha = DecodedImage.Translucent(alpha);
        return new DecodedImage(Width, Height, colors, bits, output) { Alpha = alpha, AlphaBits = bits, InvertedGray = inverted, IccProfile = Icc, DpiX = DpiX, DpiY = DpiY, Orientation = Orientation };
    }

    private static void Unpremultiply(byte[] color, long at, int colors, byte[] alpha, long alphaAt, int bytes)
    {
        int max = bytes == 1 ? 255 : 65535;
        int a = bytes == 1 ? alpha[alphaAt] : alpha[alphaAt] << 8 | alpha[alphaAt + 1];
        for (int c = 0; c < colors; c++)
        {
            long i = at + (c * bytes);
            long v = bytes == 1 ? color[i] : color[i] << 8 | color[i + 1];
            v = a == 0 ? 0 : Math.Min(max, ((v * max) + (a / 2)) / a);
            if (bytes == 1) color[i] = (byte)v;
            else (color[i], color[i + 1]) = ((byte)(v >> 8), (byte)v);
        }
    }

    /// <summary>One strip or tile decompressed, unpredicted and in big endian order: exactly its rows of samples.</summary>
    private byte[] DecodeChunk(int index, int rows)
    {
        int width = _chunkWidth, samples = ChunkSamples;
        long rowBytes = DecodedImage.RowBytes(width, samples, Bits);
        var output = new byte[rowBytes * rows];
        ReadOnlySpan<byte> raw = Chunk(index);
        if (FillOrder == 2)
        {
            byte[] copy = raw.ToArray();
            Reverse(copy);
            raw = copy;
        }

        switch (Compression)
        {
            case None:
                // As libtiff, a short raw strip is an error: data the file lacks is not made up.
                if (raw.Length < output.Length) throw new FormatException("TIFF strip or tile is truncated.");
                raw.Slice(0, output.Length).CopyTo(output);
                break;
            case PackBits:
                UnpackBits(raw, output);
                break;
            case Lzw:
                UnLzw(raw, output);
                break;
            case Deflate or OldDeflate:
                Inflate(raw, output);
                break;
            case CcittRle or CcittGroup3 or CcittGroup4:
                CcittDecoder.Decode(raw, Fax(width, rows), rows).AsSpan().CopyTo(output);
                break;
            default:
                DecodeJpeg(raw, output, width, rows);
                break;
        }

        if (Predictor == 2) Unpredict(output, rowBytes, samples);
        if (Bits == 16 && _r.Little)
        {
            for (int i = 0; i + 1 < output.Length; i += 2) (output[i], output[i + 1]) = (output[i + 1], output[i]);
        }

        return output;
    }

    /// <summary>The bytes of a strip or tile, clipped to the file.</summary>
    private ReadOnlySpan<byte> Chunk(int index)
    {
        long offset = _offsets[index];
        if (offset < 0 || offset >= _r.Data.Length) throw new FormatException("TIFF strip or tile lies outside the file.");
        long available = _r.Data.Length - offset;
        long count = index < _counts.Length && _counts[index] > 0 ? Math.Min(_counts[index], available) : available;
        return _r.Data.AsSpan((int)offset, (int)count);
    }

    /// <summary>The CCITTFaxDecode parameters TIFF's compression and fax options stand for.</summary>
    private CcittParameters Fax(int columns, int rows)
    {
        long t4 = Int(292, 0);
        return Compression switch
        {
            // Modified Huffman: one dimensional rows, each starting on a byte, no line ends.
            CcittRle => new CcittParameters { K = 0, EncodedByteAlign = true, Columns = columns, Rows = rows, EndOfBlock = false },
            CcittGroup3 => new CcittParameters { K = (t4 & 1) != 0 ? 4 : 0, EndOfLine = true, EncodedByteAlign = (t4 & 4) != 0, Columns = columns, Rows = rows, EndOfBlock = false },
            _ => new CcittParameters { K = -1, Columns = columns, Rows = rows, EndOfBlock = false },
        };
    }

    private int? JpegColorTransform => Photometric switch { 6 => 1, 2 => 0, _ => null };

    /// <summary>A strip's JPEG data with the shared tables of JPEGTables put in front of its frame.</summary>
    private byte[] WithTables(ReadOnlySpan<byte> data)
    {
        byte[]? tables = JpegTables;
        if (tables is null || tables.Length < 4 || data.Length < 2) return data.ToArray();
        int tableEnd = tables[^2] == 0xFF && tables[^1] == 0xD9 ? tables.Length - 2 : tables.Length;
        var merged = new byte[tableEnd + data.Length - 2];
        tables.AsSpan(0, tableEnd).CopyTo(merged);
        data.Slice(2).CopyTo(merged.AsSpan(tableEnd));
        return merged;
    }

    private void DecodeJpeg(ReadOnlySpan<byte> raw, byte[] output, int width, int rows)
    {
        Raster raster;
        try
        {
            raster = JpegDecoder.Decode(WithTables(raw), JpegColorTransform, Math.Min(Compat.MaxArrayLength, PngImage.MaxPixels * 4));
        }
        catch (JpegFormatException e)
        {
            throw new FormatException("TIFF JPEG data is damaged: " + e.Message, e);
        }

        if (raster.Channels != Samples) throw new FormatException("TIFF JPEG data does not match the page's samples.");
        int n = raster.Channels, w = Math.Min(width, raster.Width), h = Math.Min(rows, raster.Height);
        for (int y = 0; y < h; y++) Array.Copy(raster.Samples, (long)y * raster.Width * n, output, (long)y * width * n, (long)w * n);
    }

    /// <summary>Undoes horizontal differencing (predictor 2) on 8 or 16 bit samples, in the file's byte order.</summary>
    private void Unpredict(byte[] data, long rowBytes, int samples)
    {
        for (long row = 0; row + rowBytes <= data.Length; row += rowBytes)
        {
            if (Bits == 8)
            {
                for (long i = row + samples; i < row + rowBytes; i++) data[i] += data[i - samples];
                continue;
            }

            int stride = 2 * samples;
            for (long i = row + stride; i + 1 < row + rowBytes; i += 2)
            {
                int previous = Sample16(data, i - stride), sum = (previous + Sample16(data, i)) & 0xFFFF;
                if (_r.Little) (data[i], data[i + 1]) = ((byte)sum, (byte)(sum >> 8));
                else (data[i], data[i + 1]) = ((byte)(sum >> 8), (byte)sum);
            }
        }
    }

    private int Sample16(byte[] data, long at) => _r.Little ? data[at] | data[at + 1] << 8 : data[at] << 8 | data[at + 1];

    /// <summary>PackBits (Apple, TIFF section 9): literal runs and repeats, stopping when the output is full.</summary>
    private static void UnpackBits(ReadOnlySpan<byte> data, byte[] output)
    {
        int pos = 0, written = 0;
        while (pos < data.Length && written < output.Length)
        {
            int n = (sbyte)data[pos++];
            if (n >= 0)
            {
                int count = Math.Min(Math.Min(n + 1, data.Length - pos), output.Length - written);
                data.Slice(pos, count).CopyTo(output.AsSpan(written));
                pos += n + 1;
                written += count;
            }
            else if (n != -128 && pos < data.Length)
            {
                int count = Math.Min(1 - n, output.Length - written);
                output.AsSpan(written, count).Fill(data[pos++]);
                written += count;
            }
        }
    }

    /// <summary>
    /// TIFF LZW (section 13): codes of 9 to 12 bits, most significant bit first, with clear and end
    /// codes and the code size growing one code early. Stops when the output is full.
    /// </summary>
    private static void UnLzw(ReadOnlySpan<byte> data, byte[] output)
    {
        const int Clear = 256, End = 257, MaxCodes = 4096;
        var prefix = new short[MaxCodes];
        var suffix = new byte[MaxCodes];
        var stack = new byte[MaxCodes + 1];
        int next = 258, size = 9, previous = -1, first = 0, written = 0, bits = 0, pos = 0;
        uint buffer = 0;
        while (written < output.Length)
        {
            while (bits < size)
            {
                if (pos >= data.Length) return;
                buffer = buffer << 8 | data[pos++];
                bits += 8;
            }

            int code = (int)(buffer >> (bits - size)) & ((1 << size) - 1);
            bits -= size;
            if (code == Clear)
            {
                (next, size, previous) = (258, 9, -1);
                continue;
            }

            if (code == End) return;
            if (previous < 0)
            {
                if (code > 255) return;
                output[written++] = (byte)code;
                (previous, first) = (code, code);
                continue;
            }

            // A code not yet in the table repeats the previous string plus its first byte.
            int depth = 0, current = code;
            if (code >= next)
            {
                if (code > next) return;
                stack[depth++] = (byte)first;
                current = previous;
            }

            while (current > 255)
            {
                if (current >= next || current < 258 || depth >= MaxCodes) return;
                stack[depth++] = suffix[current];
                current = prefix[current];
            }

            first = current;
            stack[depth++] = (byte)current;
            while (depth > 0 && written < output.Length) output[written++] = stack[--depth];

            if (next < MaxCodes)
            {
                prefix[next] = (short)previous;
                suffix[next] = (byte)first;
                next++;
                if (next >= (1 << size) - 1 && size < 12) size++;
            }

            previous = code;
        }
    }

    /// <summary>Inflates a zlib stream into the output, stopping when it is full.</summary>
    private static void Inflate(ReadOnlySpan<byte> data, byte[] output)
    {
        try
        {
            using Stream inflate = Compat.ZlibDecompress(new MemoryStream(data.ToArray()));
            int written = 0, n;
            while (written < output.Length && (n = inflate.Read(output, written, output.Length - written)) > 0) written += n;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new FormatException("TIFF Deflate data is damaged.", e);
        }
    }

    private static void Reverse(byte[] data)
    {
        for (int i = 0; i < data.Length; i++) data[i] = Reversed[data[i]];
    }

    private static byte[] BuildReversed()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int b = 0; b < 8; b++) r |= (i >> b & 1) << (7 - b);
            table[i] = (byte)r;
        }

        return table;
    }

    private void ReadDirectory(long at)
    {
        byte[] data = _r.Data;
        bool big = _r.Big;
        long count = big ? _r.U64(at) : _r.U16(at);
        int entrySize = big ? 20 : 12, inline = big ? 8 : 4;
        long first = at + (big ? 8 : 2);
        for (long i = 0; i < count; i++)
        {
            long e = first + (i * entrySize);
            int tag = _r.U16(e), type = _r.U16(e + 2);
            long n = big ? _r.U64(e + 4) : _r.U32(e + 4);
            int size = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 or 16 or 17 or 18 => 8,
                _ => 0,
            };
            if (size == 0 || n <= 0 || n > data.Length / size) continue;
            long bytes = n * size;
            long value = e + (big ? 12 : 8);
            if (bytes > inline) value = big ? _r.U64(value) : _r.U32(value);
            if (value < 0 || value > data.Length - bytes) continue;
            _tags[tag] = (type, n, value);
        }
    }

    /// <summary>An integer tag's values, or none when the tag is missing or not an integer type.</summary>
    private long[] Ints(int tag)
    {
        if (!_tags.TryGetValue(tag, out var t)) return [];
        int size = t.Type switch { 1 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 13 => 4, 16 or 17 or 18 => 8, _ => 0 };
        if (size == 0) return [];
        var values = new long[t.Count];
        for (long i = 0; i < t.Count; i++)
        {
            long at = t.At + (i * size);
            values[i] = size switch { 1 => _r.Data[at], 2 => _r.U16(at), 4 => _r.U32(at), _ => _r.U64(at) };
        }

        return values;
    }

    private long Int(int tag, long fallback)
    {
        long[] values = Ints(tag);
        return values.Length > 0 ? values[0] : fallback;
    }

    private double Rational(int tag)
    {
        if (!_tags.TryGetValue(tag, out var t)) return 0;
        if (t.Type != 5) return Int(tag, 0);
        long numerator = _r.U32(t.At), denominator = _r.U32(t.At + 4);
        return denominator == 0 ? 0 : (double)numerator / denominator;
    }

    private byte[]? Bytes(int tag) => _tags.TryGetValue(tag, out var t) && t.Type is 1 or 7 ? _r.Data.AsSpan((int)t.At, (int)t.Count).ToArray() : null;
}
