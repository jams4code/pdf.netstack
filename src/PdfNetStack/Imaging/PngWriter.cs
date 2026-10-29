using System.Buffers.Binary;
using System.IO.Compression;
using PdfNetStack.Filters;

namespace PdfNetStack.Imaging;

/// <summary>Writes 8 bit gray or RGB pixels as a PNG (ISO/IEC 15948), each row filtered by the minimum sum heuristic.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Write(int width, int height, int channels, ReadOnlySpan<byte> pixels)
    {
        if (channels is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(channels));
        int stride = width * channels;
        var filtered = new byte[(long)(stride + 1) * height];
        var candidate = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, stride);
            ReadOnlySpan<byte> up = y > 0 ? pixels.Slice((y - 1) * stride, stride) : default;
            Span<byte> target = filtered.AsSpan((y * (stride + 1)) + 1, stride);
            long best = long.MaxValue;
            for (byte type = 0; type <= 4; type++)
            {
                long sum = Filter(type, row, up, channels, candidate);
                if (sum >= best) continue;
                best = sum;
                filtered[y * (stride + 1)] = type;
                candidate.CopyTo(target);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], 0, 8);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = channels == 1 ? (byte)0 : (byte)2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", StreamCodec.Deflate(filtered, CompressionLevel.Optimal));
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Applies a filter (ISO/IEC 15948, 9.2) and returns the sum of the outputs as signed bytes.</summary>
    private static long Filter(byte type, ReadOnlySpan<byte> row, ReadOnlySpan<byte> up, int bpp, Span<byte> output)
    {
        long sum = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = up.IsEmpty ? 0 : up[i], c = i >= bpp && !up.IsEmpty ? up[i - bpp] : 0;
            int predicted = type switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            byte v = (byte)(row[i] - predicted);
            output[i] = v;
            sum += v < 128 ? v : 256 - v;
        }

        return sum;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length.ToArray(), 0, 4);
        byte[] typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(typeBytes, 0, 4);
        output.Write(data.ToArray(), 0, data.Length);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        output.Write(length.ToArray(), 0, 4);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
