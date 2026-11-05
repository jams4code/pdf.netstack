using System.Buffers.Binary;

namespace PdfNetStack.Imaging;

/// <summary>
/// Reads WebP files (RFC 9649): the simple lossy and lossless layouts and the extended layout with
/// an alpha chunk, an ICC profile and animation. An animated file gives its first frame, placed on
/// a transparent canvas the size the file declares.
/// </summary>
internal static class WebPDecoder
{
    public static bool IsWebP(ReadOnlySpan<byte> d) => d.Length >= 16 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

    /// <param name="maxPixels">Ceiling for the canvas area, at most <see cref="PngImage.MaxPixels"/>.</param>
    /// <exception cref="FormatException">The data is not a WebP this reads, or is damaged.</exception>
    public static DecodedImage Read(ReadOnlySpan<byte> data, long maxPixels = PngImage.MaxPixels)
    {
        if (!IsWebP(data)) throw new FormatException("Not a WebP image.");
        int end = (int)Math.Min(data.Length, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)));
        List<Chunk> chunks = Chunks(data, 12, end);
        if (chunks.Count == 0) throw new FormatException("WebP has no image data.");

        Chunk first = chunks[0];
        if (first.Is("VP8 ") || first.Is("VP8L")) return Frame(data, chunks, null, maxPixels);
        if (!first.Is("VP8X") || first.Length < 10) throw new FormatException("WebP layout is not recognized.");

        ReadOnlySpan<byte> header = data.Slice(first.Start, first.Length);
        int canvasWidth = U24(header, 4) + 1, canvasHeight = U24(header, 7) + 1;
        DecodedImage.CheckSize(canvasWidth, canvasHeight, 4, "WebP", maxPixels);
        byte[]? icc = chunks.FirstOrDefault(c => c.Is("ICCP")) is { Length: > 0 } profile ? data.Slice(profile.Start, profile.Length).ToArray() : null;

        if ((header[0] & 0x02) == 0) return Frame(data, chunks, icc, maxPixels);

        // Animation: the first frame, offset on a canvas that starts out transparent.
        Chunk frame = chunks.FirstOrDefault(c => c.Is("ANMF"));
        if (frame.Length < 16) throw new FormatException("WebP animation has no frame.");
        ReadOnlySpan<byte> f = data.Slice(frame.Start, frame.Length);
        int x0 = 2 * U24(f, 0), y0 = 2 * U24(f, 3), width = U24(f, 6) + 1, height = U24(f, 9) + 1;
        if ((long)x0 + width > canvasWidth || (long)y0 + height > canvasHeight) throw new FormatException("WebP frame lies outside the canvas.");
        DecodedImage image = Frame(data, Chunks(data, frame.Start + 16, frame.Start + frame.Length), icc, maxPixels);
        if (image.Width != width || image.Height != height) throw new FormatException("WebP frame size does not match its bitstream.");
        if (x0 == 0 && y0 == 0 && width == canvasWidth && height == canvasHeight) return image;

        var rgb = new byte[(long)canvasWidth * canvasHeight * 3];
        var alpha = new byte[(long)canvasWidth * canvasHeight];
        for (int y = 0; y < height; y++)
        {
            long to = ((long)(y0 + y) * canvasWidth) + x0, from = (long)y * width;
            Array.Copy(image.Samples, from * 3, rgb, to * 3, (long)width * 3);
            if (image.Alpha is byte[] a) Array.Copy(a, from, alpha, to, width);
            else alpha.AsSpan((int)to, width).Fill(255);
        }

        return new DecodedImage(canvasWidth, canvasHeight, 3, 8, rgb) { Alpha = alpha, IccProfile = icc };
    }

    /// <summary>A frame's optional alpha chunk and its VP8 or VP8L bitstream.</summary>
    private static DecodedImage Frame(ReadOnlySpan<byte> data, List<Chunk> chunks, byte[]? icc, long maxPixels)
    {
        int index = chunks.FindIndex(c => c.Is("VP8 ") || c.Is("VP8L"));
        if (index < 0) throw new FormatException("WebP has no bitstream.");
        Chunk bitstream = chunks[index];
        ReadOnlySpan<byte> stream = data.Slice(bitstream.Start, bitstream.Length);
        int width, height;
        if (bitstream.Is("VP8L"))
        {
            uint[] argb = Vp8LDecoder.Decode(stream, out width, out height, maxPixels);
            var rgb = new byte[(long)width * height * 3];
            var alpha = new byte[(long)width * height];
            for (long i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                rgb[3 * i] = (byte)(p >> 16);
                rgb[(3 * i) + 1] = (byte)(p >> 8);
                rgb[(3 * i) + 2] = (byte)p;
                alpha[i] = (byte)(p >> 24);
            }

            return new DecodedImage(width, height, 3, 8, rgb) { Alpha = DecodedImage.Translucent(alpha), IccProfile = icc };
        }

        byte[] color = Vp8Decoder.DecodeRgb(stream, out width, out height, maxPixels);
        byte[]? opacity = null;
        int alphaIndex = chunks.FindIndex(c => c.Is("ALPH"));
        if (alphaIndex >= 0 && alphaIndex < index) opacity = DecodedImage.Translucent(Alpha(data.Slice(chunks[alphaIndex].Start, chunks[alphaIndex].Length), width, height));
        return new DecodedImage(width, height, 3, 8, color) { Alpha = opacity, IccProfile = icc };
    }

    /// <summary>An alpha chunk (RFC 9649, 2.7.1.2): raw or lossless compressed, then unfiltered.</summary>
    private static byte[] Alpha(ReadOnlySpan<byte> chunk, int width, int height)
    {
        if (chunk.Length < 1) throw new FormatException("WebP alpha chunk is empty.");
        int method = chunk[0] & 3, filter = chunk[0] >> 2 & 3;
        ReadOnlySpan<byte> data = chunk.Slice(1);
        var alpha = new byte[(long)width * height];
        switch (method)
        {
            case 0:
                if (data.Length < alpha.Length) throw new FormatException("WebP alpha data is truncated.");
                data.Slice(0, alpha.Length).CopyTo(alpha);
                break;
            case 1:
                uint[] argb = Vp8LDecoder.DecodeStream(data, width, height);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(argb[i] >> 8);
                break;
            default:
                throw new FormatException("WebP alpha compression is not recognized.");
        }

        if (filter == 0) return alpha;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                int left = x > 0 ? alpha[i - 1] : 0, above = y > 0 ? alpha[i - width] : 0;
                int prediction;
                if (x == 0 && y == 0) prediction = 0;
                else if (y == 0) prediction = left;
                else if (x == 0) prediction = above;
                else
                {
                    prediction = filter switch
                    {
                        1 => left,
                        2 => above,
                        _ => Compat.Clamp(left + above - alpha[i - width - 1], 0, 255),
                    };
                }

                alpha[i] = (byte)(alpha[i] + prediction);
            }
        }

        return alpha;
    }

    /// <summary>The RIFF chunks between start and end; a chunk running past the end ends the list.</summary>
    private static List<Chunk> Chunks(ReadOnlySpan<byte> data, int start, int end)
    {
        var chunks = new List<Chunk>();
        long at = start;
        while (at + 8 <= end)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)at + 4));
            if (size > end - at - 8) break;
            chunks.Add(new Chunk(data.Slice((int)at, 4).ToArray(), (int)at + 8, (int)size));
            at += 8 + (long)size + (size & 1);
        }

        return chunks;
    }

    private static int U24(ReadOnlySpan<byte> d, int at) => d[at] | d[at + 1] << 8 | d[at + 2] << 16;

    private readonly struct Chunk(byte[] fourCC, int start, int length)
    {
        public int Start { get; } = start;

        public int Length { get; } = length;

        public bool Is(string name) => fourCC is not null && fourCC[0] == name[0] && fourCC[1] == name[1] && fourCC[2] == name[2] && fourCC[3] == name[3];
    }
}
