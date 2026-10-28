using System.Security.Cryptography;
using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Security;

internal enum CryptMethod
{
    None,
    Rc4,
    AesV2,
    AesV3,
}

/// <summary>
/// The PDF standard security handler (ISO 32000-2, 7.6.4): password authentication and per object
/// encryption for revisions 2 to 6. One instance serves both directions, so a document opened with
/// its owner password can be written back with exactly the same encryption.
/// </summary>
/// <remarks>
/// RC4 and MD5 are required by the file format for revisions 2 to 4. They are used here only to read
/// and preserve such files; new encryption uses AES.
/// </remarks>
internal sealed class StandardSecurity
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private StandardSecurity(PdfDictionary dictionary, byte[] fileKey, int revision, int permissions,
        CryptMethod streams, CryptMethod strings, bool encryptMetadata, bool isOwner)
    {
        Dictionary = dictionary;
        FileKey = fileKey;
        Revision = revision;
        Permissions = permissions;
        StreamMethod = streams;
        StringMethod = strings;
        EncryptMetadata = encryptMetadata;
        IsOwner = isOwner;
    }

    public PdfDictionary Dictionary { get; }

    public byte[] FileKey { get; }

    public int Revision { get; }

    public int Permissions { get; }

    public CryptMethod StreamMethod { get; }

    public CryptMethod StringMethod { get; }

    public bool EncryptMetadata { get; }

    /// <summary>True when authenticated with the owner password, which grants every permission.</summary>
    public bool IsOwner { get; }

    // ---------------------------------------------------------------- opening

    /// <summary>Authenticates <paramref name="password"/> as owner, then as user. Null tries the empty password.</summary>
    /// <exception cref="PdfPasswordException">No password matched.</exception>
    public static StandardSecurity Open(PdfDictionary enc, byte[] id0, string? password, Func<PdfObject?, PdfObject?> resolve)
    {
        if (enc.GetName("Filter") != "Standard") throw new NotSupportedException($"Security handler '{enc.GetName("Filter")}' is not supported.");
        int v = Int(enc, "V", 0, resolve);
        int r = Int(enc, "R", 0, resolve);
        int p = Int(enc, "P", 0, resolve);
        bool encryptMetadata = resolve(enc["EncryptMetadata"]) is not PdfBoolean { Value: false };
        byte[] o = Bytes(enc, "O", resolve);
        byte[] u = Bytes(enc, "U", resolve);
        (CryptMethod streams, CryptMethod strings) = Methods(enc, v, resolve);
        int keyLength = v switch
        {
            1 => 5,
            5 => 32,
            4 => 16,
            _ => Compat.Clamp(Int(enc, "Length", 40, resolve) / 8, 5, 16),
        };

        string candidate = password ?? "";
        if (r >= 5)
        {
            byte[] oe = Bytes(enc, "OE", resolve);
            byte[] ue = Bytes(enc, "UE", resolve);
            byte[] pw = Utf8Password(candidate);
            if (o.Length >= 48 && u.Length >= 48)
            {
                if (Hash(r, pw, o.AsSpan(32, 8), u.AsSpan(0, 48)).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
                {
                    byte[] key = AesNoIv(Hash(r, pw, o.AsSpan(40, 8), u.AsSpan(0, 48)), oe, encrypt: false);
                    return new StandardSecurity(enc, key, r, p, streams, strings, encryptMetadata, isOwner: true);
                }

                if (Hash(r, pw, u.AsSpan(32, 8), []).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
                {
                    byte[] key = AesNoIv(Hash(r, pw, u.AsSpan(40, 8), []), ue, encrypt: false);
                    return new StandardSecurity(enc, key, r, p, streams, strings, encryptMetadata, isOwner: false);
                }
            }

            throw new PdfPasswordException();
        }

        if (r < 2) throw new NotSupportedException($"Security handler revision {r} is not supported.");
        if (o.Length < 32 || u.Length < 32) throw new PdfFormatException("Encryption dictionary has invalid /O or /U values.");

        // Owner first: recover the padded user password from /O, then authenticate with it.
        byte[] ownerKey = OwnerRc4Key(Latin1Password(candidate), r, keyLength);
        byte[] recovered = (byte[])o[..32].Clone();
        if (r == 2)
        {
            recovered = Rc4.Apply(ownerKey, recovered);
        }
        else
        {
            for (int i = 19; i >= 0; i--) recovered = Rc4.Apply(Xor(ownerKey, i), recovered);
        }

        byte[] asOwner = FileKeyR4(recovered, o, p, id0, r, keyLength, encryptMetadata);
        if (UserMatches(asOwner, u, id0, r))
        {
            return new StandardSecurity(enc, asOwner, r, p, streams, strings, encryptMetadata, isOwner: true);
        }

        byte[] asUser = FileKeyR4(Pad(Latin1Password(candidate)), o, p, id0, r, keyLength, encryptMetadata);
        if (UserMatches(asUser, u, id0, r))
        {
            return new StandardSecurity(enc, asUser, r, p, streams, strings, encryptMetadata, isOwner: false);
        }

        throw new PdfPasswordException();
    }

    // ---------------------------------------------------------------- creating

    /// <summary>Creates new encryption. AES-256 uses revision 6; AES-128 uses revision 4 and needs the document ID.</summary>
    public static StandardSecurity Create(string userPassword, string ownerPassword, int permissions, PdfEncryption algorithm, byte[] id0)
    {
        if (string.IsNullOrEmpty(ownerPassword)) ownerPassword = Compat.ToHex(Compat.RandomBytes(16));
        var enc = new PdfDictionary();
        enc.Set("Filter", new PdfName("Standard"));
        int p = permissions;

        if (algorithm == PdfEncryption.Aes256)
        {
            byte[] fileKey = Compat.RandomBytes(32);
            byte[] user = Utf8Password(userPassword);
            byte[] owner = Utf8Password(ownerPassword);
            byte[] uSalts = Compat.RandomBytes(16);
            byte[] u = [.. Hash(6, user, uSalts.AsSpan(0, 8), []), .. uSalts];
            byte[] ue = AesNoIv(Hash(6, user, uSalts.AsSpan(8, 8), []), fileKey, encrypt: true);
            byte[] oSalts = Compat.RandomBytes(16);
            byte[] o = [.. Hash(6, owner, oSalts.AsSpan(0, 8), u), .. oSalts];
            byte[] oe = AesNoIv(Hash(6, owner, oSalts.AsSpan(8, 8), u), fileKey, encrypt: true);

            byte[] perms = new byte[16];
            Compat.WriteInt32LittleEndian(perms.AsSpan(0, 4), p);
            perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
            perms[8] = (byte)'T';
            perms[9] = (byte)'a';
            perms[10] = (byte)'d';
            perms[11] = (byte)'b';
            Compat.FillRandom(perms.AsSpan(12));
            byte[] permsEncrypted = Compat.AesEcbEncryptNoPadding(fileKey, perms);

            enc.Set("V", PdfNumber.FromInteger(5));
            enc.Set("R", PdfNumber.FromInteger(6));
            enc.Set("Length", PdfNumber.FromInteger(256));
            enc.Set("CF", CryptFilters("AESV3", 32));
            enc.Set("StmF", new PdfName("StdCF"));
            enc.Set("StrF", new PdfName("StdCF"));
            enc.Set("O", new PdfString(o, true));
            enc.Set("U", new PdfString(u, true));
            enc.Set("OE", new PdfString(oe, true));
            enc.Set("UE", new PdfString(ue, true));
            enc.Set("P", PdfNumber.FromInteger(p));
            enc.Set("Perms", new PdfString(permsEncrypted, true));
            return new StandardSecurity(enc, fileKey, 6, p, CryptMethod.AesV3, CryptMethod.AesV3, true, isOwner: true);
        }

        const int r4 = 4, n = 16;
        byte[] userPad = Pad(Latin1Password(userPassword));
        byte[] ownerKey = OwnerRc4Key(Latin1Password(ownerPassword), r4, n);
        byte[] oValue = Rc4.Apply(ownerKey, userPad);
        for (int i = 1; i <= 19; i++) oValue = Rc4.Apply(Xor(ownerKey, i), oValue);
        byte[] key = FileKeyR4(userPad, oValue, p, id0, r4, n, encryptMetadata: true);
        byte[] uValue = [.. UserHashR3(key, id0), .. new byte[16]];

        enc.Set("V", PdfNumber.FromInteger(4));
        enc.Set("R", PdfNumber.FromInteger(4));
        enc.Set("Length", PdfNumber.FromInteger(128));
        enc.Set("CF", CryptFilters("AESV2", 16));
        enc.Set("StmF", new PdfName("StdCF"));
        enc.Set("StrF", new PdfName("StdCF"));
        enc.Set("O", new PdfString(oValue, true));
        enc.Set("U", new PdfString(uValue, true));
        enc.Set("P", PdfNumber.FromInteger(p));
        return new StandardSecurity(enc, key, r4, p, CryptMethod.AesV2, CryptMethod.AesV2, true, isOwner: true);
    }

    /// <summary>Revision 3 encryption with RC4, for tests of the legacy read path only.</summary>
    internal static StandardSecurity CreateLegacyRc4(string userPassword, string ownerPassword, int permissions, byte[] id0, int keyBytes)
    {
        const int r = 3;
        byte[] userPad = Pad(Latin1Password(userPassword));
        byte[] ownerKey = OwnerRc4Key(Latin1Password(ownerPassword), r, keyBytes);
        byte[] o = Rc4.Apply(ownerKey, userPad);
        for (int i = 1; i <= 19; i++) o = Rc4.Apply(Xor(ownerKey, i), o);
        byte[] key = FileKeyR4(userPad, o, permissions, id0, r, keyBytes, encryptMetadata: true);
        var enc = new PdfDictionary();
        enc.Set("Filter", new PdfName("Standard"));
        enc.Set("V", PdfNumber.FromInteger(2));
        enc.Set("R", PdfNumber.FromInteger(r));
        enc.Set("Length", PdfNumber.FromInteger(keyBytes * 8));
        enc.Set("O", new PdfString(o, true));
        enc.Set("U", new PdfString([.. UserHashR3(key, id0), .. new byte[16]], true));
        enc.Set("P", PdfNumber.FromInteger(permissions));
        return new StandardSecurity(enc, key, r, permissions, CryptMethod.Rc4, CryptMethod.Rc4, true, isOwner: true);
    }

    private static PdfDictionary CryptFilters(string method, int length)
    {
        var std = new PdfDictionary();
        std.Set("Type", new PdfName("CryptFilter"));
        std.Set("CFM", new PdfName(method));
        std.Set("AuthEvent", new PdfName("DocOpen"));
        std.Set("Length", PdfNumber.FromInteger(length));
        var cf = new PdfDictionary();
        cf.Set("StdCF", std);
        return cf;
    }

    // ---------------------------------------------------------------- per object

    public byte[] Decrypt(int number, int generation, ReadOnlySpan<byte> data, bool isString) =>
        Transform(number, generation, data, isString ? StringMethod : StreamMethod, encrypt: false);

    public byte[] Encrypt(int number, int generation, ReadOnlySpan<byte> data, bool isString) =>
        Transform(number, generation, data, isString ? StringMethod : StreamMethod, encrypt: true);

    private byte[] Transform(int number, int generation, ReadOnlySpan<byte> data, CryptMethod method, bool encrypt)
    {
        switch (method)
        {
            case CryptMethod.None:
                return data.ToArray();
            case CryptMethod.Rc4:
                return Rc4.Apply(ObjectKey(number, generation, aes: false), data);
            default:
            {
                byte[] key = method == CryptMethod.AesV3 ? FileKey : ObjectKey(number, generation, aes: true);
                if (encrypt)
                {
                    byte[] iv = Compat.RandomBytes(16);
                    return [.. iv, .. Compat.AesCbcEncrypt(key, iv, data, pkcs7: true)];
                }

                return data.Length < 16 ? [] : DecryptLenient(key, data);
            }
        }
    }

    // Damaged files sometimes carry truncated blocks or bad padding; viewers decrypt what they can.
    private static byte[] DecryptLenient(byte[] key, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> iv = data[..16];
        ReadOnlySpan<byte> body = data[16..];
        body = body[..(body.Length / 16 * 16)];
        if (body.IsEmpty) return [];
        byte[] plain = Compat.AesCbcDecryptNoPadding(key, iv, body);
        int pad = plain[^1];
        if (pad is >= 1 and <= 16 && pad <= plain.Length)
        {
            bool valid = true;
            for (int i = plain.Length - pad; i < plain.Length; i++) valid &= plain[i] == pad;
            if (valid) return plain[..^pad];
        }

        return plain;
    }

    private byte[] ObjectKey(int number, int generation, bool aes)
    {
        Span<byte> input = stackalloc byte[FileKey.Length + 9];
        FileKey.CopyTo(input);
        int o = FileKey.Length;
        input[o++] = (byte)number;
        input[o++] = (byte)(number >> 8);
        input[o++] = (byte)(number >> 16);
        input[o++] = (byte)generation;
        input[o++] = (byte)(generation >> 8);
        if (aes)
        {
            "sAlT"u8.CopyTo(input[o..]);
            o += 4;
        }

#pragma warning disable CA5351 // MD5 is mandated by the PDF format for these revisions.
        byte[] hash = Compat.Md5(input[..o]);
#pragma warning restore CA5351
        return hash[..Math.Min(FileKey.Length + 5, 16)];
    }

    // ---------------------------------------------------------------- algorithms

    private static (CryptMethod Streams, CryptMethod Strings) Methods(PdfDictionary enc, int v, Func<PdfObject?, PdfObject?> resolve)
    {
        if (v < 4) return (CryptMethod.Rc4, CryptMethod.Rc4);
        var cf = resolve(enc["CF"]) as PdfDictionary;

        CryptMethod Lookup(string key)
        {
            string name = enc.GetName(key) ?? "Identity";
            if (name == "Identity") return CryptMethod.None;
            if (resolve(cf?[name]) is not PdfDictionary filter) return CryptMethod.None;
            return filter.GetName("CFM") switch
            {
                "V2" => CryptMethod.Rc4,
                "AESV2" => CryptMethod.AesV2,
                "AESV3" => CryptMethod.AesV3,
                "None" or null => CryptMethod.None,
                string other => throw new NotSupportedException($"Crypt filter method '{other}' is not supported."),
            };
        }

        return (Lookup("StmF"), Lookup("StrF"));
    }

    private static byte[] FileKeyR4(byte[] paddedPassword, byte[] o, int p, byte[] id0, int r, int n, bool encryptMetadata)
    {
        var input = new List<byte>(32 + 32 + 4 + id0.Length + 4);
        input.AddRange(paddedPassword.AsSpan(0, 32).ToArray());
        input.AddRange(o.AsSpan(0, 32).ToArray());
        input.AddRange(BitConverter.GetBytes(p));
        input.AddRange(id0);
        if (r >= 4 && !encryptMetadata) input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
#pragma warning disable CA5351
        byte[] hash = Compat.Md5(input.ToArray());
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++) hash = Compat.Md5(hash.AsSpan(0, n));
        }
#pragma warning restore CA5351
        return hash[..n];
    }

    private static bool UserMatches(byte[] key, byte[] u, byte[] id0, int r) =>
        r == 2 ? Rc4.Apply(key, Padding).AsSpan().SequenceEqual(u.AsSpan(0, 32)) : UserHashR3(key, id0).AsSpan().SequenceEqual(u.AsSpan(0, 16));

    private static byte[] UserHashR3(byte[] key, byte[] id0)
    {
#pragma warning disable CA5351
        byte[] x = Compat.Md5([.. Padding, .. id0]);
#pragma warning restore CA5351
        x = Rc4.Apply(key, x);
        for (int i = 1; i <= 19; i++) x = Rc4.Apply(Xor(key, i), x);
        return x;
    }

    private static byte[] OwnerRc4Key(byte[] ownerPassword, int r, int n)
    {
#pragma warning disable CA5351
        byte[] hash = Compat.Md5(Pad(ownerPassword));
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++) hash = Compat.Md5(hash);
        }
#pragma warning restore CA5351
        return hash[..n];
    }

    private static byte[] Xor(byte[] key, int value)
    {
        var result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ value);
        return result;
    }

    private static byte[] Pad(byte[] password)
    {
        var result = new byte[32];
        int n = Math.Min(32, password.Length);
        password.AsSpan(0, n).CopyTo(result);
        Padding.AsSpan(0, 32 - n).CopyTo(result.AsSpan(n));
        return result;
    }

    private static byte[] Latin1Password(string password) => Compat.Latin1.GetBytes(password);

    private static byte[] Utf8Password(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }

    /// <summary>Revision 5 is a single SHA-256; revision 6 is the iterated hash of ISO 32000-2, algorithm 2.B.</summary>
    private static byte[] Hash(int r, byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userKey)
    {
        byte[] k = Compat.Sha256([.. password, .. salt, .. userKey]);
        if (r == 5) return k;

        for (int round = 0; ; round++)
        {
            int unit = password.Length + k.Length + userKey.Length;
            var k1 = new byte[unit * 64];
            for (int i = 0; i < 64; i++)
            {
                int o = i * unit;
                password.CopyTo(k1, o);
                k.CopyTo(k1, o + password.Length);
                userKey.CopyTo(k1.AsSpan(o + password.Length + k.Length));
            }

            byte[] e = Compat.AesCbcEncrypt(k[..16], k.AsSpan(16, 16), k1, pkcs7: false);
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += e[i];
            k = (sum % 3) switch
            {
                0 => Compat.Sha256(e),
                1 => Compat.Sha384(e),
                _ => Compat.Sha512(e),
            };

            if (round >= 63 && e[^1] <= round - 31) break;
        }

        return k[..32];
    }

    private static byte[] AesNoIv(byte[] key, byte[] data, bool encrypt)
    {
        if (data.Length < 32) throw new PdfPasswordException();
        var iv = new byte[16];
        return encrypt ? Compat.AesCbcEncrypt(key, iv, data.AsSpan(0, 32), pkcs7: false) : Compat.AesCbcDecryptNoPadding(key, iv, data.AsSpan(0, 32));
    }

    private static int Int(PdfDictionary d, string key, int fallback, Func<PdfObject?, PdfObject?> resolve) =>
        resolve(d[key]) is PdfNumber { IsInteger: true } n ? (int)n.IntegerValue : fallback;

    private static byte[] Bytes(PdfDictionary d, string key, Func<PdfObject?, PdfObject?> resolve) =>
        resolve(d[key]) is PdfString s ? s.Value : [];
}

/// <summary>RC4 stream cipher, required to read PDF encryption revisions 2 to 4.</summary>
internal static class Rc4
{
    public static byte[] Apply(byte[] key, ReadOnlySpan<byte> data)
    {
        Span<byte> s = stackalloc byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }
}
