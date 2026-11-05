using System.Buffers.Binary;

namespace PdfNetStack.Imaging;

/// <summary>
/// Reads Windows bitmaps (BMP): the OS/2 core header and the Windows info headers up to version 5,
/// 1, 2, 4 and 8 bit palette images, RLE4 and RLE8, 16, 24 and 32 bit color with bit field masks,
/// rows stored bottom up or top down, alpha and an embedded ICC profile.
/// </summary>
/// <remarks>
/// Palette images keep their indexes at their own depth. Run length coded images leave the pixels
/// their runs skip transparent, as browsers draw them. A 32 bit image without an alpha mask still
/// gets its fourth byte as alpha when any pixel's is not zero, again like browsers.
/// </remarks>
internal static class BmpDecoder
{
    private const int FileHeaderSize = 14;

    private const int Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, Jpeg = 4, Png = 5, AlphaBitFields = 6;

    public static bool IsBmp(ReadOnlySpan<byte> data) => data.Length >= 18 && data[0] == 'B' && data[1] == 'M';

    /// <summary>The JPEG or PNG a bitmap wraps (compression 4 or 5), or null for an ordinary bitmap.</summary>
    /// <exception cref="FormatException">The header is damaged.</exception>
    public static byte[]? Embedded(ReadOnlySpan<byte> data)
    {
        Header h = ReadHeader(data);
        if (h.Compression is not (Jpeg or Png)) return null;
        int length = h.ImageSize > 0 && h.ImageSize <= data.Length - h.PixelOffset ? (int)h.ImageSize : data.Length - h.PixelOffset;
        if (length <= 0) throw new FormatException("BMP embedded image is missing.");
        return data.Slice(h.PixelOffset, length).ToArray();
    }

    /// <param name="maxPixels">Ceiling for the image area, at most <see cref="PngImage.MaxPixels"/>.</param>
    /// <exception cref="FormatException">The data is not a bitmap this reads, or is damaged.</exception>
    public static DecodedImage Read(ReadOnlySpan<byte> data, long maxPixels = PngImage.MaxPixels)
    {
        Header h = ReadHeader(data);
        if (h.Compression is Jpeg or Png) throw new FormatException("BMP wraps a JPEG or PNG image.");
        int width = h.Width, height = h.Height;
        DecodedImage.CheckSize(width, height, 4, "BMP", maxPixels);

        return h.Compression switch
        {
            Rle8 or Rle4 => RunLength(data, h),
            _ when h.BitCount <= 8 => Indexed(data, h),
            _ => Direct(data, h),
        };
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (!IsBmp(data)) throw new FormatException("Not a BMP image.");
        var h = new Header();
        uint size = U32(data, FileHeaderSize);
        if (size < 12 || size > data.Length - FileHeaderSize) throw new FormatException("BMP header is truncated or invalid.");
        int at = FileHeaderSize;
        if (size == 12)
        {
            h.Width = U16(data, at + 4);
            h.Height = U16(data, at + 6);
            h.BitCount = U16(data, at + 10);
            h.Core = true;
        }
        else
        {
            if (size < 40) throw new FormatException("BMP header is not supported.");
            int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at + 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at + 8));
            if (height == int.MinValue) throw new FormatException("BMP height is invalid.");
            h.Width = width;
            h.TopDown = height < 0;
            h.Height = Math.Abs(height);
            h.BitCount = U16(data, at + 14);
            h.Compression = (int)Math.Min(int.MaxValue, U32(data, at + 16));
            h.ImageSize = U32(data, at + 20);
            h.DpiX = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at + 24))) * 0.0254;
            h.DpiY = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at + 28))) * 0.0254;
            h.ColorsUsed = U32(data, at + 32);

            // OS/2 2.x reuses compression 3 and 4 for Huffman and RLE24, which this does not read.
            if (size == 64 && h.Compression is 3 or 4) throw new FormatException("OS/2 BMP compression is not supported.");

            // Masks sit inside headers of version 2 and later, else right after the 40 byte header.
            h.PaletteOffset = at + (int)size;
            if (h.Compression is BitFields or AlphaBitFields)
            {
                int count = h.Compression == AlphaBitFields || size >= 56 ? 4 : 3;
                int masks = size >= 52 ? at + 40 : at + (int)size;
                if (masks + 4 * count > data.Length) throw new FormatException("BMP bit field masks are truncated.");
                h.Masks = new uint[4];
                for (int i = 0; i < count; i++) h.Masks[i] = U32(data, masks + 4 * i);
                if (size < 52) h.PaletteOffset += 4 * count;
            }

            // Version 5 can embed its ICC profile, placed relative to the header's start.
            if (size >= 124 && U32(data, FileHeaderSize + 56) == 0x4D424544)
            {
                long offset = FileHeaderSize + (long)U32(data, FileHeaderSize + 112), length = U32(data, FileHeaderSize + 116);
                if (length > 0 && offset + length <= data.Length) h.IccProfile = data.Slice((int)offset, (int)length).ToArray();
            }
        }

        if (h.Core) h.PaletteOffset = FileHeaderSize + 12;
        uint pixels = U32(data, 10);
        h.PixelOffset = pixels > 0 && pixels < data.Length ? (int)pixels : -1;
        bool validDepth = h.Compression switch
        {
            Rgb => h.BitCount is 1 or 2 or 4 or 8 or 16 or 24 or 32,
            Rle8 => h.BitCount == 8,
            Rle4 => h.BitCount == 4,
            BitFields or AlphaBitFields => h.BitCount is 16 or 32,
            Jpeg or Png => true,
            _ => false,
        };
        if (!validDepth) throw new FormatException($"BMP compression {h.Compression} with {h.BitCount} bits per pixel is not supported.");
        if (h.BitCount <= 8 && h.Compression is not (Jpeg or Png)) h.Palette = Palette(data, h);
        if (h.PixelOffset < 0) h.PixelOffset = h.Palette is null ? h.PaletteOffset : h.PaletteEnd;
        return h;
    }

    /// <summary>The color table as RGB triples, padded with black to 2^bits entries so every index has a color.</summary>
    private static byte[] Palette(ReadOnlySpan<byte> data, Header h)
    {
        int full = 1 << h.BitCount, entry = h.Core ? 3 : 4;
        int count = h.ColorsUsed > 0 && h.ColorsUsed < full ? (int)h.ColorsUsed : full;
        int end = h.PixelOffset > h.PaletteOffset ? h.PixelOffset : data.Length;
        int available = (end - h.PaletteOffset) / entry;
        count = Math.Max(0, Math.Min(count, available));
        if (count == 0) throw new FormatException("BMP color table is missing.");
        var palette = new byte[3 * full];
        for (int i = 0; i < count; i++)
        {
            int at = h.PaletteOffset + entry * i;
            palette[3 * i] = data[at + 2];
            palette[(3 * i) + 1] = data[at + 1];
            palette[(3 * i) + 2] = data[at];
        }

        h.PaletteEnd = h.PaletteOffset + entry * count;
        return palette;
    }

    /// <summary>Uncompressed palette images: rows copied without their 4 byte padding, indexes at their own depth.</summary>
    private static DecodedImage Indexed(ReadOnlySpan<byte> data, Header h)
    {
        int width = h.Width, height = h.Height, bits = h.BitCount;
        long stride = Stride(width, bits), rowBytes = DecodedImage.RowBytes(width, 1, bits);
        Rows(data, h, stride);
        var samples = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            long from = h.PixelOffset + stride * (h.TopDown ? y : height - 1 - y);
            data.Slice((int)from, (int)rowBytes).CopyTo(samples.AsSpan((int)(rowBytes * y)));
        }

        return new DecodedImage(width, height, 1, bits, samples) { Palette = h.Palette, DpiX = h.DpiX, DpiY = h.DpiY, IccProfile = h.IccProfile };
    }

    /// <summary>16, 24 and 32 bit pixels: channels pulled out by mask and scaled to 8 bits.</summary>
    private static DecodedImage Direct(ReadOnlySpan<byte> data, Header h)
    {
        int width = h.Width, height = h.Height, bits = h.BitCount, bytes = bits / 8;
        long stride = Stride(width, bits);
        Rows(data, h, stride);
        uint[] masks = h.Masks ?? (bits == 16 ? [0x7C00, 0x3E0, 0x1F, 0] : [0xFF0000, 0xFF00, 0xFF, 0]);

        // Without explicit masks a 32 bit pixel's spare byte may still be alpha (see the remarks).
        bool guessAlpha = h.Masks is null && bits == 32;
        if (guessAlpha) masks = [masks[0], masks[1], masks[2], 0xFF000000];
        Channel red = new(masks[0]), green = new(masks[1]), blue = new(masks[2]), alpha = new(masks[3]);

        var rgb = new byte[(long)width * height * 3];
        byte[]? a = alpha.Mask != 0 ? new byte[(long)width * height] : null;
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int row = (int)(h.PixelOffset + stride * (h.TopDown ? y : height - 1 - y));
            for (int x = 0; x < width; x++)
            {
                int at = row + x * bytes;
                uint v = bytes switch
                {
                    2 => (uint)(data[at] | data[at + 1] << 8),
                    3 => (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16),
                    _ => U32(data, at),
                };
                long p = (long)y * width + x;
                rgb[3 * p] = red.Value(v);
                rgb[(3 * p) + 1] = green.Value(v);
                rgb[(3 * p) + 2] = blue.Value(v);
                if (a is not null)
                {
                    a[p] = alpha.Value(v);
                    anyAlpha |= a[p] != 0;
                }
            }
        }

        // An alpha channel that is zero everywhere is an unused byte, not an invisible image.
        if (a is not null && !anyAlpha) a = null;
        return new DecodedImage(width, height, 3, 8, rgb) { Alpha = a is null ? null : DecodedImage.Translucent(a), DpiX = h.DpiX, DpiY = h.DpiY, IccProfile = h.IccProfile };
    }

    /// <summary>RLE8 and RLE4: runs, literal runs, line ends and jumps. Pixels never reached stay transparent.</summary>
    private static DecodedImage RunLength(ReadOnlySpan<byte> data, Header h)
    {
        int width = h.Width, height = h.Height;
        bool four = h.Compression == Rle4;
        var indexes = new byte[(long)width * height];
        var written = new byte[(long)width * height];
        int pos = h.PixelOffset, x = 0, y = 0;

        // Pixels past the right edge are dropped; x stops there so that long runs cannot overflow it.
        void Put(byte index)
        {
            if (x >= width) return;
            if (y < height)
            {
                long at = (long)(h.TopDown ? y : height - 1 - y) * width + x;
                indexes[at] = index;
                written[at] = 255;
            }

            x++;
        }

        while (pos + 1 < data.Length && y < height)
        {
            int count = data[pos], value = data[pos + 1];
            pos += 2;
            if (count > 0)
            {
                for (int i = 0; i < count; i++) Put((byte)(four ? (i % 2 == 0 ? value >> 4 : value & 15) : value));
                continue;
            }

            switch (value)
            {
                case 0:
                    x = 0;
                    y++;
                    break;
                case 1:
                    y = height;
                    break;
                case 2:
                    if (pos + 1 >= data.Length) y = height;
                    else
                    {
                        x = Math.Min(width, x + data[pos]);
                        y += data[pos + 1];
                        pos += 2;
                    }

                    break;
                default:
                    int literal = four ? (value + 1) / 2 : value;
                    for (int i = 0; i < value && pos + (four ? i / 2 : i) < data.Length; i++)
                    {
                        int b = data[pos + (four ? i / 2 : i)];
                        Put((byte)(four ? (i % 2 == 0 ? b >> 4 : b & 15) : b));
                    }

                    // Literal runs are padded to a 16 bit boundary.
                    pos += literal + (literal & 1);
                    break;
            }
        }

        return new DecodedImage(width, height, 1, 8, indexes) { Palette = h.Palette, Alpha = DecodedImage.Translucent(written), DpiX = h.DpiX, DpiY = h.DpiY, IccProfile = h.IccProfile };
    }

    /// <summary>Bytes per stored row: rows are padded to 4 bytes.</summary>
    private static long Stride(int width, int bits) => (((long)width * bits) + 31) / 32 * 4;

    private static void Rows(ReadOnlySpan<byte> data, Header h, long stride)
    {
        // The last row needs only its pixels, not its padding.
        long needed = stride * (h.Height - 1) + DecodedImage.RowBytes(h.Width, 1, h.BitCount);
        if (h.PixelOffset + needed > data.Length) throw new FormatException("BMP pixel data is truncated.");
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => data[at] | (data[at + 1] << 8);

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at));

    /// <summary>One color channel of a bit field pixel: its mask, the shift to its low bit and its maximum.</summary>
    private readonly struct Channel
    {
        private readonly int _shift;
        private readonly uint _max;

        public Channel(uint mask)
        {
            Mask = mask;
            _shift = 0;
            if (mask == 0) return;
            while ((mask >> _shift & 1) == 0) _shift++;
            _max = mask >> _shift;
        }

        public uint Mask { get; }

        /// <summary>The channel scaled to 0 to 255, rounding to nearest.</summary>
        public byte Value(uint pixel)
        {
            if (_max == 0) return 0;
            ulong v = (pixel & Mask) >> _shift;
            return (byte)((v * 255 + _max / 2) / _max);
        }
    }

    private sealed class Header
    {
        public bool Core;
        public int Width;
        public int Height;
        public bool TopDown;
        public int BitCount;
        public int Compression;
        public uint ImageSize;
        public uint ColorsUsed;
        public double DpiX;
        public double DpiY;
        public uint[]? Masks;
        public byte[]? Palette;
        public byte[]? IccProfile;
        public int PaletteOffset;
        public int PaletteEnd;
        public int PixelOffset;
    }
}
