using System.IO.Compression;

namespace PdfNetStack.Imaging;

/// <summary>
/// A PNG read for embedding in PDF (ISO/IEC 15948). Non-interlaced images without an alpha
/// channel keep their compressed data: PDF's Flate filter with PNG predictors decodes it as is.
/// Others are decoded and split into color samples and an alpha channel, the soft mask.
/// </summary>
internal sealed class PngImage
{
    /// <summary>Largest image accepted, in pixels: about 16k by 16k.</summary>
    public const long MaxPixels = 1L << 28;

    private PngImage()
    {
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int BitDepth { get; private set; }

    /// <summary>PNG color type: 0 gray, 2 RGB, 3 palette, 4 gray and alpha, 6 RGB and alpha.</summary>
    public int ColorType { get; private set; }

    public bool Interlaced { get; private set; }

    /// <summary>RGB triples, for palette images.</summary>
    public byte[]? Palette { get; private set; }

    /// <summary>The tRNS chunk: palette alphas, or the transparent gray or RGB sample.</summary>
    public byte[]? Transparency { get; private set; }

    public byte[]? IccProfile { get; private set; }

    public double DpiX { get; private set; }

    public double DpiY { get; private set; }

    /// <summary>The concatenated IDAT data: one zlib stream.</summary>
    public byte[] Data { get; private set; } = [];

    /// <summary>Color channels without alpha: 1 for gray and palette, 3 for RGB.</summary>
    public int Colors => ColorType is 2 or 6 ? 3 : 1;

    public bool HasAlphaChannel => ColorType is 4 or 6;

    /// <exception cref="FormatException">Not a valid PNG.</exception>
    public static PngImage Read(ReadOnlySpan<byte> d)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length < 8 || !d.Slice(0, 8).SequenceEqual(signature)) throw new FormatException("Not a PNG file.");
        var png = new PngImage();
        using var idat = new MemoryStream();
        bool header = false;
        int at = 8;
        while (at + 8 <= d.Length)
        {
            long length = (long)d[at] << 24 | (long)d[at + 1] << 16 | (long)d[at + 2] << 8 | d[at + 3];
            if (length > d.Length - at - 12) throw new FormatException("PNG chunk is truncated.");
            ReadOnlySpan<byte> type = d.Slice(at + 4, 4);
            ReadOnlySpan<byte> data = d.Slice(at + 8, (int)length);
            at += 12 + (int)length;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length < 13) throw new FormatException("PNG header is truncated.");
                png.Width = (int)Math.Min(int.MaxValue, (long)data[0] << 24 | (long)data[1] << 16 | (long)data[2] << 8 | data[3]);
                png.Height = (int)Math.Min(int.MaxValue, (long)data[4] << 24 | (long)data[5] << 16 | (long)data[6] << 8 | data[7]);
                png.BitDepth = data[8];
                png.ColorType = data[9];
                png.Interlaced = data[12] == 1;
                header = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                png.Palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                png.Transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data.ToArray(), 0, data.Length);
            }
            else if (type.SequenceEqual("iCCP"u8))
            {
                int nul = data.IndexOf((byte)0);
                if (nul > 0 && nul + 2 < data.Length) png.IccProfile = Inflate(data.Slice(nul + 2).ToArray(), 4 << 20);
            }
            else if (type.SequenceEqual("pHYs"u8) && data.Length >= 9 && data[8] == 1)
            {
                png.DpiX = ((long)data[0] << 24 | (long)data[1] << 16 | (long)data[2] << 8 | data[3]) * 0.0254;
                png.DpiY = ((long)data[4] << 24 | (long)data[5] << 16 | (long)data[6] << 8 | data[7]) * 0.0254;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (!header) throw new FormatException("PNG has no header.");
        bool validDepth = png.ColorType switch
        {
            0 => png.BitDepth is 1 or 2 or 4 or 8 or 16,
            3 => png.BitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => png.BitDepth is 8 or 16,
            _ => false,
        };
        if (!validDepth) throw new FormatException("PNG color type and bit depth are invalid.");
        if (png.Width <= 0 || png.Height <= 0 || (long)png.Width * png.Height > MaxPixels) throw new FormatException("PNG size is out of range.");
        if (png.ColorType == 3 && (png.Palette is null || png.Palette.Length < 3)) throw new FormatException("PNG palette is missing.");
        png.Data = idat.ToArray();
        if (png.Data.Length < 2) throw new FormatException("PNG has no image data.");
        return png;
    }

    /// <summary>Bytes per row of unfiltered samples for a width.</summary>
    public long RowBytes(int width) => ((long)width * Channels * BitDepth + 7) / 8;

    private int Channels => ColorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };

    /// <summary>
    /// The image unfiltered and deinterlaced: rows of samples as PNG stores them, alpha included.
    /// </summary>
    /// <exception cref="FormatException">The data is damaged.</exception>
    public byte[] Decode()
    {
        long rowBytes = RowBytes(Width);
        long size = rowBytes * Height;
        if (size > int.MaxValue / 2) throw new FormatException("PNG is too large to decode.");
        int bpp = Math.Max(1, Channels * BitDepth / 8);
        long expected = Interlaced ? PassesSize() : (rowBytes + 1) * Height;
        byte[] raw = Inflate(Data, expected);
        if (raw.Length < expected) throw new FormatException("PNG image data is truncated.");
        if (!Interlaced) return Unfilter(raw, 0, Width, Height, bpp);

        // Adam7: seven passes over a grid, each its own filtered sub-image.
        var image = new byte[size];
        int offset = 0;
        foreach ((int x0, int y0, int dx, int dy) in Passes)
        {
            int w = (Width - x0 + dx - 1) / dx, h = (Height - y0 + dy - 1) / dy;
            if (w <= 0 || h <= 0) continue;
            byte[] pass = Unfilter(raw, offset, w, h, bpp);
            offset += (int)((RowBytes(w) + 1) * h);
            int passRow = (int)RowBytes(w);
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++) CopyPixel(pass, r * passRow, c, image, (y0 + r * dy) * rowBytes, x0 + c * dx);
            }
        }

        return image;
    }

    private static readonly (int X, int Y, int Dx, int Dy)[] Passes =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    private long PassesSize()
    {
        long total = 0;
        foreach ((int x0, int y0, int dx, int dy) in Passes)
        {
            int w = (Width - x0 + dx - 1) / dx, h = (Height - y0 + dy - 1) / dy;
            if (w > 0 && h > 0) total += (RowBytes(w) + 1) * h;
        }

        return total;
    }

    private void CopyPixel(byte[] from, long fromRow, int fromX, byte[] to, long toRow, int toX)
    {
        int bits = Channels * BitDepth;
        if (bits >= 8)
        {
            int n = bits / 8;
            Array.Copy(from, fromRow + (long)fromX * n, to, toRow + (long)toX * n, n);
            return;
        }

        int fromBit = fromX * bits, toBit = toX * bits;
        int value = (from[fromRow + fromBit / 8] >> (8 - bits - fromBit % 8)) & ((1 << bits) - 1);
        int shift = 8 - bits - toBit % 8;
        long index = toRow + toBit / 8;
        to[index] = (byte)((to[index] & ~(((1 << bits) - 1) << shift)) | (value << shift));
    }

    private byte[] Unfilter(byte[] raw, int offset, int width, int height, int bpp)
    {
        int stride = (int)RowBytes(width);
        var output = new byte[(long)stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[offset + y * (stride + 1)];
            int src = offset + y * (stride + 1) + 1, dst = y * stride, up = dst - stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? output[dst + x - bpp] : 0;
                int b = y > 0 ? output[up + x] : 0;
                int c = x >= bpp && y > 0 ? output[up + x - bpp] : 0;
                int v = raw[src + x];
                output[dst + x] = (byte)(filter switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + ((a + b) >> 1),
                    4 => v + Paeth(a, b, c),
                    _ => throw new FormatException("PNG filter type is invalid."),
                });
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Inflates a zlib stream, refusing output beyond <paramref name="limit"/> bytes.</summary>
    private static byte[] Inflate(byte[] zlib, long limit)
    {
        if (zlib.Length < 2) throw new FormatException("PNG compressed data is truncated.");
        try
        {
            using var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 2), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > limit) break;
            }

            return output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new FormatException("PNG compressed data is damaged.", e);
        }
    }
}
