namespace PdfNetStack.Imaging;

/// <summary>The first frame of a GIF, placed on its logical screen: palette indexes and, where pixels are transparent, alpha.</summary>
internal sealed class GifImage
{
    public GifImage(int width, int height, byte[] palette, byte[] indexes, byte[]? alpha)
    {
        Width = width;
        Height = height;
        Palette = palette;
        Indexes = indexes;
        Alpha = alpha;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGB triples.</summary>
    public byte[] Palette { get; }

    /// <summary>One palette index per pixel, rows top to bottom.</summary>
    public byte[] Indexes { get; }

    /// <summary>0 or 255 per pixel, or null when every pixel is opaque.</summary>
    public byte[]? Alpha { get; }
}

/// <summary>
/// Reads GIF 87a and 89a (the GIF89a specification, CompuServe 1990): color tables, the
/// graphic control extension's transparent color, interlaced rows and variable length LZW codes.
/// Only the first frame is read, as a still image shows it. Damaged image data ends the frame
/// early rather than failing, as browsers draw it.
/// </summary>
internal static class GifDecoder
{
    private const int MaxCodes = 4096;

    public static bool IsGif(ReadOnlySpan<byte> data) => data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' && data[4] is (byte)'7' or (byte)'9' && data[5] == 'a';

    /// <param name="maxPixels">Ceiling for the canvas area, at most <see cref="PngImage.MaxPixels"/>.</param>
    /// <exception cref="FormatException">The data is not a GIF, has no image, or is too large.</exception>
    public static GifImage Read(ReadOnlySpan<byte> data, long maxPixels = PngImage.MaxPixels)
    {
        if (!IsGif(data) || data.Length < 13) throw new FormatException("Not a GIF image.");
        int screenWidth = U16(data, 6), screenHeight = U16(data, 8), flags = data[10];
        int pos = 13;
        byte[]? global = null;
        if ((flags & 0x80) != 0) global = Table(data, ref pos, flags);
        int transparent = -1;

        while (pos < data.Length)
        {
            switch (data[pos++])
            {
                case 0x21:
                    if (pos >= data.Length) throw new FormatException("GIF ends inside an extension.");
                    int label = data[pos++];

                    // Graphic control (section 23): the transparent color index of the next image.
                    if (label == 0xF9 && pos + 5 <= data.Length && data[pos] >= 4 && (data[pos + 1] & 1) != 0) transparent = data[pos + 4];
                    SkipBlocks(data, ref pos);
                    break;
                case 0x2C:
                    return Frame(data, ref pos, screenWidth, screenHeight, global, transparent, maxPixels);
                case 0x3B:
                    throw new FormatException("GIF has no image.");
                default:
                    throw new FormatException("GIF block is not recognized.");
            }
        }

        throw new FormatException("GIF has no image.");
    }

    private static GifImage Frame(ReadOnlySpan<byte> data, ref int pos, int screenWidth, int screenHeight, byte[]? global, int transparent, long maxPixels)
    {
        if (pos + 9 > data.Length) throw new FormatException("GIF image descriptor is truncated.");
        int left = U16(data, pos), top = U16(data, pos + 2), width = U16(data, pos + 4), height = U16(data, pos + 6), flags = data[pos + 8];
        pos += 9;
        byte[] palette = (flags & 0x80) != 0 ? Table(data, ref pos, flags) : global ?? Grays();
        bool interlaced = (flags & 0x40) != 0;

        // The logical screen frames the image; files that declare none, or a smaller one, get the frame's.
        int canvasWidth = Math.Max(screenWidth, left + width), canvasHeight = Math.Max(screenHeight, top + height);
        if (width == 0 || height == 0 || (long)canvasWidth * canvasHeight > maxPixels) throw new FormatException("GIF image size is invalid or too large.");
        if (pos >= data.Length) throw new FormatException("GIF image data is missing.");
        int minimum = data[pos++];
        if (minimum is < 2 or > 11) throw new FormatException("GIF image data has an invalid code size.");

        byte[] pixels = Lzw(Blocks(data, ref pos), minimum, width * height);
        var indexes = new byte[canvasWidth * canvasHeight];
        byte[]? alpha = null;
        bool covered = left == 0 && top == 0 && width == canvasWidth && height == canvasHeight;
        if (!covered || transparent >= 0)
        {
            alpha = new byte[indexes.Length];
            if (covered) alpha.AsSpan().Fill(255);
        }

        for (int row = 0; row < height; row++)
        {
            int y = top + (interlaced ? InterlacedRow(row, height) : row);
            for (int x = 0; x < width; x++)
            {
                byte index = pixels[(row * width) + x];
                int at = (y * canvasWidth) + left + x;
                indexes[at] = index;
                if (alpha is not null) alpha[at] = index == transparent ? (byte)0 : (byte)255;
            }
        }

        return new GifImage(canvasWidth, canvasHeight, palette, indexes, alpha is not null && Array.IndexOf(alpha, (byte)0) >= 0 ? alpha : null);
    }

    /// <summary>The image row stored at <paramref name="row"/> of an interlaced frame: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1.</summary>
    private static int InterlacedRow(int row, int height)
    {
        int pass1 = (height + 7) / 8, pass2 = (height + 3) / 8, pass3 = (height + 1) / 4;
        if (row < pass1) return row * 8;
        row -= pass1;
        if (row < pass2) return (row * 8) + 4;
        row -= pass2;
        if (row < pass3) return (row * 4) + 2;
        return ((row - pass3) * 2) + 1;
    }

    /// <summary>
    /// Variable length LZW (section 22 and Appendix F): codes from minimum + 1 bits up to 12, least
    /// significant bit first, with clear and end codes. Output past the frame's pixels is dropped;
    /// pixels the data never reaches stay 0.
    /// </summary>
    private static byte[] Lzw(byte[] data, int minimum, int count)
    {
        var output = new byte[count];
        var prefix = new short[MaxCodes];
        var suffix = new byte[MaxCodes];
        var stack = new byte[MaxCodes + 1];
        int clear = 1 << minimum, end = clear + 1, next = clear + 2, size = minimum + 1, previous = -1, first = 0;
        int written = 0, bits = 0, buffer = 0, pos = 0;
        while (written < count)
        {
            while (bits < size)
            {
                if (pos >= data.Length) return output;
                buffer |= data[pos++] << bits;
                bits += 8;
            }

            int code = buffer & ((1 << size) - 1);
            buffer >>= size;
            bits -= size;
            if (code == clear)
            {
                (next, size, previous) = (clear + 2, minimum + 1, -1);
                continue;
            }

            if (code == end) break;
            if (previous < 0)
            {
                if (code >= clear) return output;
                output[written++] = (byte)code;
                (previous, first) = (code, code);
                continue;
            }

            // A code not yet in the table repeats the previous string plus its first character.
            int depth = 0, current = code;
            if (code >= next)
            {
                if (code > next) return output;
                stack[depth++] = (byte)first;
                current = previous;
            }

            while (current >= clear)
            {
                if (current >= next || depth >= MaxCodes) return output;
                stack[depth++] = suffix[current];
                current = prefix[current];
            }

            first = current;
            stack[depth++] = (byte)current;
            while (depth > 0 && written < count) output[written++] = stack[--depth];

            if (next < MaxCodes)
            {
                prefix[next] = (short)previous;
                suffix[next] = (byte)first;
                next++;
                if (next == 1 << size && size < 12) size++;
            }

            previous = code;
        }

        return output;
    }

    private static byte[] Table(ReadOnlySpan<byte> data, ref int pos, int flags)
    {
        int length = 3 << ((flags & 7) + 1);
        if (pos + length > data.Length) throw new FormatException("GIF color table is truncated.");
        byte[] table = data.Slice(pos, length).ToArray();
        pos += length;
        return table;
    }

    private static byte[] Grays()
    {
        var table = new byte[3 * 256];
        for (int i = 0; i < 256; i++) table[3 * i] = table[(3 * i) + 1] = table[(3 * i) + 2] = (byte)i;
        return table;
    }

    /// <summary>Data sub-blocks joined: each a length byte then that many bytes, ended by a zero length.</summary>
    private static byte[] Blocks(ReadOnlySpan<byte> data, ref int pos)
    {
        using var joined = new MemoryStream();
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (length == 0) break;
            int available = Math.Min(length, data.Length - pos);
            joined.Write(data.Slice(pos, available).ToArray(), 0, available);
            pos += available;
        }

        return joined.ToArray();
    }

    private static void SkipBlocks(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (length == 0) return;
            pos += length;
        }
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => data[at] | (data[at + 1] << 8);
}
