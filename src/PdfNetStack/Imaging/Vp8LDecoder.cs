namespace PdfNetStack.Imaging;

/// <summary>
/// Decodes WebP lossless image streams (RFC 9649, section 3) to ARGB pixels: the predictor, color,
/// subtract green and color indexing transforms, the color cache, meta prefix codes and LZ77
/// backward references with the distance map.
/// </summary>
/// <remarks>
/// Input is checked as it is read: code lengths must form complete prefix codes, symbols and
/// copies must stay in range, and reading past the end of the data is an error. Prefix code
/// tables are capped in total size so that a small file cannot demand a large amount of memory.
/// </remarks>
internal sealed class Vp8LDecoder
{
    private const int Signature = 0x2F;
    private const int MaxTableEntries = 1 << 24;
    private const int LengthCodes = 24;
    private const int Predictor = 0, CrossColor = 1, SubtractGreen = 2, ColorIndexing = 3;

    private static readonly int[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly byte[] _data;
    private int _pos;
    private ulong _buffer;
    private int _count;
    private int _tableEntries;

    private Vp8LDecoder(byte[] data) => _data = data;

    /// <summary>Width and height from a VP8L header, without decoding.</summary>
    /// <exception cref="FormatException">The header is not VP8L.</exception>
    public static (int Width, int Height) Size(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != Signature) throw new FormatException("WebP lossless signature is missing.");
        uint bits = (uint)(data[1] | data[2] << 8 | data[3] << 16 | data[4] << 24);
        return ((int)(bits & 0x3FFF) + 1, (int)(bits >> 14 & 0x3FFF) + 1);
    }

    /// <summary>A VP8L chunk: header, then the image stream.</summary>
    /// <param name="maxPixels">Ceiling for the image area, at most <see cref="PngImage.MaxPixels"/>.</param>
    /// <exception cref="FormatException">The data is damaged or too large.</exception>
    public static uint[] Decode(ReadOnlySpan<byte> data, out int width, out int height, long maxPixels = PngImage.MaxPixels)
    {
        (width, height) = Size(data);
        var decoder = new Vp8LDecoder(data.ToArray()) { _pos = 1 };
        decoder.ReadBits(28);
        decoder.ReadBits(1);
        if (decoder.ReadBits(3) != 0) throw new FormatException("WebP lossless version is not 0.");
        DecodedImage.CheckSize(width, height, 4, "WebP", maxPixels);
        return decoder.ImageStream(width, height);
    }

    /// <summary>An image stream without a header, of known size: the lossless form of an alpha chunk.</summary>
    /// <exception cref="FormatException">The data is damaged.</exception>
    public static uint[] DecodeStream(ReadOnlySpan<byte> data, int width, int height) => new Vp8LDecoder(data.ToArray()).ImageStream(width, height);

    private uint[] ImageStream(int width, int height)
    {
        var transforms = new List<Transform>();
        int xsize = width;
        bool[] seen = new bool[4];
        while (ReadBits(1) == 1)
        {
            int type = (int)ReadBits(2);
            if (seen[type]) throw new FormatException("WebP lossless transform repeats.");
            seen[type] = true;
            var t = new Transform(type, xsize);
            switch (type)
            {
                case Predictor or CrossColor:
                    t.Bits = (int)ReadBits(3) + 2;
                    t.Data = EntropyCoded(Subsample(xsize, t.Bits), Subsample(height, t.Bits));
                    break;
                case ColorIndexing:
                    int colors = (int)ReadBits(8) + 1;
                    t.Bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                    uint[] palette = EntropyCoded(colors, 1);
                    for (int i = 1; i < colors; i++) palette[i] = Add(palette[i], palette[i - 1]);
                    t.Data = palette;
                    xsize = Subsample(xsize, t.Bits);
                    break;
            }

            transforms.Add(t);
        }

        uint[] pixels = SpatiallyCoded(xsize, height);
        for (int i = transforms.Count - 1; i >= 0; i--) pixels = Inverse(transforms[i], pixels, height);
        return pixels;
    }

    /// <summary>The main image: color cache, optional meta prefix codes by block, then the pixels.</summary>
    private uint[] SpatiallyCoded(int width, int height)
    {
        int cacheBits = CacheBits();
        if (ReadBits(1) == 0) return Pixels(width, height, cacheBits, [Group(cacheBits)], null, 0, 0);

        int prefixBits = (int)ReadBits(3) + 2;
        int blocksWide = Subsample(width, prefixBits);
        uint[] entropy = EntropyCoded(blocksWide, Subsample(height, prefixBits));
        var meta = new int[entropy.Length];
        int groups = 0;
        for (int i = 0; i < entropy.Length; i++)
        {
            meta[i] = (int)(entropy[i] >> 8 & 0xFFFF);
            groups = Math.Max(groups, meta[i] + 1);
        }

        var codes = new PrefixGroup[groups];
        for (int i = 0; i < groups; i++) codes[i] = Group(cacheBits);
        return Pixels(width, height, cacheBits, codes, meta, prefixBits, blocksWide);
    }

    /// <summary>A subresolution image: transform data, the entropy image or the palette. No meta codes.</summary>
    private uint[] EntropyCoded(int width, int height)
    {
        int cacheBits = CacheBits();
        return Pixels(width, height, cacheBits, [Group(cacheBits)], null, 0, 0);
    }

    private int CacheBits()
    {
        if (ReadBits(1) == 0) return 0;
        int bits = (int)ReadBits(4);
        if (bits is < 1 or > 11) throw new FormatException("WebP lossless color cache size is invalid.");
        return bits;
    }

    private PrefixGroup Group(int cacheBits) => new(
        Code(256 + LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0)), Code(256), Code(256), Code(256), Code(40));

    /// <summary>Pixels coded as literals, backward references and color cache indexes (RFC 9649, 3.7.2.3).</summary>
    private uint[] Pixels(int width, int height, int cacheBits, PrefixGroup[] groups, int[]? meta, int prefixBits, int blocksWide)
    {
        int total = width * height;
        var pixels = new uint[total];
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int cacheShift = 32 - cacheBits;
        PrefixGroup group = groups[0];
        int pos = 0, x = 0, y = 0;
        while (pos < total)
        {
            if (meta is not null && (x & ((1 << prefixBits) - 1)) == 0) group = groups[meta[(y >> prefixBits) * blocksWide + (x >> prefixBits)]];
            int symbol = Read(group.Green);
            if (symbol < 256)
            {
                uint red = (uint)Read(group.Red), blue = (uint)Read(group.Blue), alpha = (uint)Read(group.Alpha);
                uint argb = alpha << 24 | red << 16 | (uint)symbol << 8 | blue;
                pixels[pos++] = argb;
                if (cache is not null) cache[(0x1E35A7BDu * argb) >> cacheShift] = argb;
                if (++x == width) (x, y) = (0, y + 1);
                continue;
            }

            if (symbol < 256 + LengthCodes)
            {
                int length = PrefixValue(symbol - 256);
                int code = PrefixValue(Read(group.Distance));
                int distance = Distance(code, width);
                if (distance > pos || length > total - pos) throw new FormatException("WebP lossless backward reference is out of range.");
                for (int i = 0; i < length; i++)
                {
                    uint argb = pixels[pos - distance];
                    pixels[pos++] = argb;
                    if (cache is not null) cache[(0x1E35A7BDu * argb) >> cacheShift] = argb;
                }

                x += length;
                y += x / width;
                x %= width;

                // A copy that crosses into a new block row or column picks that block's codes up next.
                if (meta is not null && pos < total) group = groups[meta[(y >> prefixBits) * blocksWide + (x >> prefixBits)]];
                continue;
            }

            int index = symbol - 256 - LengthCodes;
            if (cache is null || index >= cache.Length) throw new FormatException("WebP lossless color cache index is out of range.");
            pixels[pos++] = cache[index];
            if (++x == width) (x, y) = (0, y + 1);
        }

        return pixels;
    }

    /// <summary>A length or distance from its prefix code and extra bits.</summary>
    private int PrefixValue(int code)
    {
        if (code < 4) return code + 1;
        int extra = (code - 2) >> 1;
        int offset = (2 + (code & 1)) << extra;
        return offset + (int)ReadBits(extra) + 1;
    }

    /// <summary>A distance code to a distance in pixels: the first 120 name nearby pixels (RFC 9649, 3.6.2.2.1).</summary>
    private static int Distance(int code, int width)
    {
        if (code > 120) return code - 120;
        int dx = WebPTables.DistanceMap[2 * (code - 1)], dy = WebPTables.DistanceMap[(2 * (code - 1)) + 1];
        long distance = dx + ((long)dy * width);
        return distance < 1 ? 1 : (int)Math.Min(int.MaxValue, distance);
    }

    private uint[] Inverse(Transform t, uint[] pixels, int height)
    {
        int width = t.Width;
        switch (t.Type)
        {
            case Predictor:
                Unpredict(pixels, width, height, t.Bits, t.Data!);
                return pixels;
            case CrossColor:
                int blocksWide = Subsample(width, t.Bits);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        uint m = t.Data![(y >> t.Bits) * blocksWide + (x >> t.Bits)];
                        int greenToRed = (sbyte)m, greenToBlue = (sbyte)(m >> 8), redToBlue = (sbyte)(m >> 16);
                        int i = (y * width) + x;
                        uint argb = pixels[i];
                        int green = (sbyte)(argb >> 8);
                        int red = (int)(argb >> 16 & 0xFF) + ((greenToRed * green) >> 5) & 0xFF;
                        int blue = (int)(argb & 0xFF) + ((greenToBlue * green) >> 5);
                        blue = blue + ((redToBlue * (sbyte)red) >> 5) & 0xFF;
                        pixels[i] = (argb & 0xFF00FF00) | (uint)red << 16 | (uint)blue;
                    }
                }

                return pixels;
            case SubtractGreen:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint argb = pixels[i], green = argb >> 8 & 0xFF;
                    uint red = (argb >> 16) + green & 0xFF, blue = argb + green & 0xFF;
                    pixels[i] = (argb & 0xFF00FF00) | red << 16 | blue;
                }

                return pixels;
            default:
                return Unindex(pixels, width, height, t.Bits, t.Data!);
        }
    }

    /// <summary>Adds each pixel's prediction from its neighbors, by the mode of its block (RFC 9649, 3.5.1).</summary>
    private static void Unpredict(uint[] p, int width, int height, int bits, uint[] modes)
    {
        int blocksWide = Subsample(width, bits);
        p[0] = Add(p[0], 0xFF000000);
        for (int x = 1; x < width; x++) p[x] = Add(p[x], p[x - 1]);
        for (int y = 1; y < height; y++)
        {
            int row = y * width;
            p[row] = Add(p[row], p[row - width]);
            for (int x = 1; x < width; x++)
            {
                int i = row + x;
                int mode = (int)(modes[(y >> bits) * blocksWide + (x >> bits)] >> 8 & 0xF);
                uint l = p[i - 1], t = p[i - width], tl = p[i - width - 1];

                // Past the right edge, the pixel above and to the right is the first of this row.
                uint tr = p[i - width + 1];
                uint prediction = mode switch
                {
                    1 => l,
                    2 => t,
                    3 => tr,
                    4 => tl,
                    5 => Average(Average(l, tr), t),
                    6 => Average(l, tl),
                    7 => Average(l, t),
                    8 => Average(tl, t),
                    9 => Average(t, tr),
                    10 => Average(Average(l, tl), Average(t, tr)),
                    11 => Select(l, t, tl),
                    12 => ClampAddSubtractFull(l, t, tl),
                    13 => ClampAddSubtractHalf(Average(l, t), tl),
                    _ => 0xFF000000,
                };
                p[i] = Add(p[i], prediction);
            }
        }
    }

    /// <summary>Palette indexes, possibly 2, 4 or 8 to a pixel, to colors (RFC 9649, 3.5.4).</summary>
    private static uint[] Unindex(uint[] packed, int width, int height, int bits, uint[] palette)
    {
        var output = new uint[width * height];
        int packedWidth = Subsample(width, bits), perPixel = 8 >> bits, mask = (1 << perPixel) - 1, xMask = (1 << bits) - 1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint green = packed[(y * packedWidth) + (x >> bits)] >> 8 & 0xFF;
                int index = bits == 0 ? (int)green : (int)(green >> ((x & xMask) * perPixel)) & mask;
                output[(y * width) + x] = index < palette.Length ? palette[index] : 0;
            }
        }

        return output;
    }

    private static uint Add(uint a, uint b) => (((a & 0xFF00FF00) + (b & 0xFF00FF00)) & 0xFF00FF00) | (((a & 0x00FF00FF) + (b & 0x00FF00FF)) & 0x00FF00FF);

    private static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static uint Select(uint l, uint t, uint tl)
    {
        int distanceToLeft = 0, distanceToTop = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int cl = (int)(l >> shift & 0xFF), ct = (int)(t >> shift & 0xFF), ctl = (int)(tl >> shift & 0xFF);
            int estimate = cl + ct - ctl;
            distanceToLeft += Math.Abs(estimate - cl);
            distanceToTop += Math.Abs(estimate - ct);
        }

        return distanceToLeft < distanceToTop ? l : t;
    }

    private static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int v = (int)(a >> shift & 0xFF) + (int)(b >> shift & 0xFF) - (int)(c >> shift & 0xFF);
            result |= (uint)Compat.Clamp(v, 0, 255) << shift;
        }

        return result;
    }

    private static uint ClampAddSubtractHalf(uint a, uint b)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = (int)(a >> shift & 0xFF), cb = (int)(b >> shift & 0xFF);
            result |= (uint)Compat.Clamp(ca + ((ca - cb) / 2), 0, 255) << shift;
        }

        return result;
    }

    private static int Subsample(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    // ---------------------------------------------------------------- prefix codes

    /// <summary>Reads a prefix code's lengths, simple or normal (RFC 9649, 3.7.2.1), and builds its table.</summary>
    private PrefixCode Code(int alphabet)
    {
        var lengths = new int[alphabet];
        if (ReadBits(1) == 1)
        {
            int symbols = (int)ReadBits(1) + 1;
            int first = (int)ReadBits(ReadBits(1) == 1 ? 8 : 1);
            if (first < alphabet) lengths[first] = 1;
            if (symbols == 2)
            {
                int second = (int)ReadBits(8);
                if (second < alphabet) lengths[second] = 1;
            }

            return Build(lengths);
        }

        var lengthLengths = new int[CodeLengthOrder.Length];
        int count = (int)ReadBits(4) + 4;
        for (int i = 0; i < count; i++) lengthLengths[CodeLengthOrder[i]] = (int)ReadBits(3);
        PrefixCode lengthCode = Build(lengthLengths);

        int limit = alphabet;
        if (ReadBits(1) == 1)
        {
            int bits = 2 + (2 * (int)ReadBits(3));
            limit = 2 + (int)ReadBits(bits);
            if (limit > alphabet) throw new FormatException("WebP lossless code lists more symbols than its alphabet.");
        }

        int symbol = 0, previous = 8;
        while (symbol < alphabet && limit-- > 0)
        {
            int length = Read(lengthCode);
            if (length < 16)
            {
                lengths[symbol++] = length;
                if (length != 0) previous = length;
                continue;
            }

            int repeat = length switch { 16 => 3 + (int)ReadBits(2), 17 => 3 + (int)ReadBits(3), _ => 11 + (int)ReadBits(7) };
            if (symbol + repeat > alphabet) throw new FormatException("WebP lossless code lengths overrun the alphabet.");
            int value = length == 16 ? previous : 0;
            for (int i = 0; i < repeat; i++) lengths[symbol++] = value;
        }

        return Build(lengths);
    }

    /// <summary>
    /// A canonical prefix code's lookup table: a root table of up to 8 bits, with second level
    /// tables for longer codes. A code with one symbol takes no bits. Incomplete and
    /// oversubscribed codes are errors.
    /// </summary>
    private PrefixCode Build(int[] lengths)
    {
        var counts = new int[16];
        int symbols = 0, single = 0, longest = 0;
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] == 0) continue;
            counts[lengths[s]]++;
            symbols++;
            single = s;
            longest = Math.Max(longest, lengths[s]);
        }

        if (symbols == 0) throw new FormatException("WebP lossless prefix code is empty.");
        if (symbols == 1) return new PrefixCode([single], 0);

        int left = 1;
        for (int length = 1; length < 16; length++)
        {
            left = (left << 1) - counts[length];
            if (left < 0) throw new FormatException("WebP lossless prefix code is oversubscribed.");
        }

        if (left != 0) throw new FormatException("WebP lossless prefix code is incomplete.");

        // Canonical codes, then each reversed: the stream is read least significant bit first.
        var next = new int[16];
        for (int length = 1, code = 0; length < 16; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        int root = Math.Min(longest, 8), rootSize = 1 << root;
        var reversed = new int[lengths.Length];
        var subBits = new int[rootSize];
        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length == 0) continue;
            reversed[s] = ReverseBits(next[length]++, length);
            if (length > root)
            {
                int low = reversed[s] & (rootSize - 1);
                subBits[low] = Math.Max(subBits[low], length - root);
            }
        }

        int size = rootSize;
        var offsets = new int[rootSize];
        for (int low = 0; low < rootSize; low++)
        {
            if (subBits[low] == 0) continue;
            offsets[low] = size;
            size += 1 << subBits[low];
        }

        _tableEntries += size;
        if (_tableEntries > MaxTableEntries) throw new FormatException("WebP lossless prefix codes are too large.");
        var table = new int[size];
        for (int low = 0; low < rootSize; low++)
        {
            if (subBits[low] != 0) table[low] = Link | subBits[low] << 16 | offsets[low];
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length == 0) continue;
            int code = reversed[s], entry = length << 16 | s;
            if (length <= root)
            {
                for (int i = code; i < rootSize; i += 1 << length) table[i] = entry;
                continue;
            }

            int low = code & (rootSize - 1), bits = subBits[low], high = code >> root;
            for (int i = high; i < 1 << bits; i += 1 << (length - root)) table[offsets[low] + i] = entry;
        }

        return new PrefixCode(table, root);
    }

    private const int Link = 1 << 30;

    private static int ReverseBits(int code, int length)
    {
        int r = 0;
        for (int i = 0; i < length; i++) r |= (code >> i & 1) << (length - 1 - i);
        return r;
    }

    private int Read(PrefixCode code)
    {
        if (code.RootBits == 0) return code.Table[0];
        Fill();
        int entry = code.Table[(int)_buffer & ((1 << code.RootBits) - 1)];
        if ((entry & Link) != 0)
        {
            int bits = entry >> 16 & 0xFF, offset = entry & 0xFFFF;
            entry = code.Table[offset + ((int)(_buffer >> code.RootBits) & ((1 << bits) - 1))];
        }

        Skip(entry >> 16);
        return entry & 0xFFFF;
    }

    // ---------------------------------------------------------------- bits, least significant first

    private void Fill()
    {
        while (_count <= 56 && _pos < _data.Length)
        {
            _buffer |= (ulong)_data[_pos++] << _count;
            _count += 8;
        }
    }

    private void Skip(int bits)
    {
        if (bits > _count) throw new FormatException("WebP lossless data is truncated.");
        _buffer >>= bits;
        _count -= bits;
    }

    private uint ReadBits(int bits)
    {
        if (bits == 0) return 0;
        Fill();
        uint value = (uint)(_buffer & ((1UL << bits) - 1));
        Skip(bits);
        return value;
    }

    private sealed class Transform(int type, int width)
    {
        public int Type { get; } = type;

        /// <summary>The image width this transform produces.</summary>
        public int Width { get; } = width;

        public int Bits { get; set; }

        public uint[]? Data { get; set; }
    }

    private sealed class PrefixCode(int[] table, int rootBits)
    {
        public int[] Table { get; } = table;

        public int RootBits { get; } = rootBits;
    }

    private readonly record struct PrefixGroup(PrefixCode Green, PrefixCode Red, PrefixCode Blue, PrefixCode Alpha, PrefixCode Distance);
}
