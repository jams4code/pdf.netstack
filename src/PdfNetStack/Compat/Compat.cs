using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PdfNetStack;

/// <summary>
/// One call site for APIs that differ between .NET Standard 2.0 (.NET Framework, Mono, Unity) and
/// modern .NET. Modern targets call straight through to the built-in, faster implementations.
/// </summary>
internal static class Compat
{
#if NET8_0_OR_GREATER
    public const int MaxArrayLength = 0x7FFFFFC7;

    public static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>zlib level 9.</summary>
    public const CompressionLevel MaximumCompression = CompressionLevel.SmallestSize;
#else
    public const int MaxArrayLength = 0x7FFFFFC7;

    public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    /// <summary>The best level .NET Standard 2.0 offers.</summary>
    public const CompressionLevel MaximumCompression = CompressionLevel.Optimal;
#endif

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static void ThrowIfNull(object? value, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
    }

    public static void ThrowIfNullOrEmpty(string? value, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
        if (value.Length == 0) throw new ArgumentException("Value cannot be empty.", name);
    }

    public static void ThrowIfGreaterThan(uint value, uint max, string name)
    {
        if (value > max) throw new ArgumentOutOfRangeException(name);
    }

    public static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = "0123456789ABCDEF"[bytes[i] >> 4];
            chars[2 * i + 1] = "0123456789ABCDEF"[bytes[i] & 0xF];
        }

        return new string(chars);
    }

    public static string Format(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

    public static void WriteInt32LittleEndian(Span<byte> destination, int value) => BinaryPrimitives.WriteInt32LittleEndian(destination, value);

    // ---------------------------------------------------------------- hashing and randomness

#if NET8_0_OR_GREATER
    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] Sha384(ReadOnlySpan<byte> data) => SHA384.HashData(data);

    public static byte[] Sha512(ReadOnlySpan<byte> data) => SHA512.HashData(data);

#pragma warning disable CA5351 // MD5 is mandated by the PDF format for legacy encryption.
    public static byte[] Md5(ReadOnlySpan<byte> data) => MD5.HashData(data);
#pragma warning restore CA5351

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    public static void FillRandom(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
#else
    public static byte[] Sha256(ReadOnlySpan<byte> data)
    {
        using var h = SHA256.Create();
        return h.ComputeHash(data.ToArray());
    }

    public static byte[] Sha384(ReadOnlySpan<byte> data)
    {
        using var h = SHA384.Create();
        return h.ComputeHash(data.ToArray());
    }

    public static byte[] Sha512(ReadOnlySpan<byte> data)
    {
        using var h = SHA512.Create();
        return h.ComputeHash(data.ToArray());
    }

#pragma warning disable CA5351
    public static byte[] Md5(ReadOnlySpan<byte> data)
    {
        using var h = MD5.Create();
        return h.ComputeHash(data.ToArray());
    }
#pragma warning restore CA5351

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }

    public static void FillRandom(Span<byte> destination) => RandomBytes(destination.Length).CopyTo(destination);
#endif

    // ---------------------------------------------------------------- AES

    public static byte[] AesCbcEncrypt(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data, bool pkcs7)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
#if NET8_0_OR_GREATER
        return aes.EncryptCbc(data, iv, pkcs7 ? PaddingMode.PKCS7 : PaddingMode.None);
#else
        aes.Mode = CipherMode.CBC;
        aes.Padding = pkcs7 ? PaddingMode.PKCS7 : PaddingMode.None;
        aes.IV = iv.ToArray();
        using ICryptoTransform t = aes.CreateEncryptor();
        byte[] input = data.ToArray();
        return t.TransformFinalBlock(input, 0, input.Length);
#endif
    }

    public static byte[] AesCbcDecryptNoPadding(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
#if NET8_0_OR_GREATER
        return aes.DecryptCbc(data, iv, PaddingMode.None);
#else
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = iv.ToArray();
        using ICryptoTransform t = aes.CreateDecryptor();
        byte[] input = data.ToArray();
        return t.TransformFinalBlock(input, 0, input.Length);
#endif
    }

    public static byte[] AesEcbEncryptNoPadding(byte[] key, ReadOnlySpan<byte> data)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
#if NET8_0_OR_GREATER
        return aes.EncryptEcb(data, PaddingMode.None);
#else
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform t = aes.CreateEncryptor();
        byte[] input = data.ToArray();
        return t.TransformFinalBlock(input, 0, input.Length);
#endif
    }

    // ---------------------------------------------------------------- zlib

    /// <summary>A stream that inflates zlib (RFC 1950) data.</summary>
    public static Stream ZlibDecompress(Stream input)
    {
#if NET8_0_OR_GREATER
        return new ZLibStream(input, CompressionMode.Decompress);
#else
        int cmf = input.ReadByte();
        int flg = input.ReadByte();
        if (cmf < 0 || flg < 0 || (cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
        {
            throw new InvalidDataException("Invalid zlib header.");
        }

        return new DeflateStream(input, CompressionMode.Decompress);
#endif
    }

    /// <summary>
    /// True when the Adler-32 of <paramref name="inflated"/> matches the zlib trailer. Trailing
    /// end-of-line bytes after the trailer, common in PDF stream data, are tolerated.
    /// </summary>
    public static bool AdlerMatches(ReadOnlySpan<byte> compressed, ReadOnlySpan<byte> inflated)
    {
        uint a = 1, b = 0;
        foreach (byte x in inflated)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        uint adler = (b << 16) | a;
        int end = compressed.Length;
        for (int trim = 0; trim <= 2 && end >= 6; trim++)
        {
            ReadOnlySpan<byte> t = compressed.Slice(end - 4, 4);
            if (((uint)t[0] << 24 | (uint)t[1] << 16 | (uint)t[2] << 8 | t[3]) == adler) return true;
            if (compressed[end - 1] is not ((byte)'\r' or (byte)'\n' or (byte)' ')) break;
            end--;
        }

        return false;
    }

    /// <summary>Compresses into the zlib format: header, deflate data and Adler-32 checksum.</summary>
    public static void ZlibCompress(Stream output, ReadOnlySpan<byte> data, CompressionLevel level)
    {
#if NET8_0_OR_GREATER
        using var z = new ZLibStream(output, level, leaveOpen: true);
        z.Write(data);
#else
        output.WriteByte(0x78);
        output.WriteByte(level == CompressionLevel.Fastest ? (byte)0x01 : (byte)0xDA);
        byte[] buffer = data.ToArray();
        using (var d = new DeflateStream(output, level, leaveOpen: true))
        {
            d.Write(buffer, 0, buffer.Length);
        }

        uint a = 1, b = 0;
        foreach (byte x in buffer)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        uint adler = (b << 16) | a;
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
#endif
    }

    // ---------------------------------------------------------------- files

    public static void MoveOverwrite(string source, string destination)
    {
#if NET8_0_OR_GREATER
        File.Move(source, destination, overwrite: true);
#else
        if (File.Exists(destination)) File.Replace(source, destination, null);
        else File.Move(source, destination);
#endif
    }

    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        return File.ReadAllBytesAsync(path, ct);
#else
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllBytes(path));
#endif
    }
}
