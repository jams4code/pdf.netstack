using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// X.509 (RFC 5280) pieces the library checks itself, so the same code runs on every target and
/// nothing reaches the network: signatures of certificates, CRLs and OCSP responses, extensions,
/// and the hash and signature algorithm identifiers.
/// </summary>
internal static class Pkix
{
    public const string KeyUsageOid = "2.5.29.15";
    public const string BasicConstraintsOid = "2.5.29.19";
    public const string ExtendedKeyUsageOid = "2.5.29.37";
    public const string TimeStampingUsage = "1.3.6.1.5.5.7.3.8";
    public const string OcspSigningUsage = "1.3.6.1.5.5.7.3.9";
    public const string OcspNoCheck = "1.3.6.1.5.5.7.48.1.5";
    public const string RsaEncryption = "1.2.840.113549.1.1.1";
    private const string RsaPss = "1.2.840.113549.1.1.10";
    private const string AuthorityInformationAccess = "1.3.6.1.5.5.7.1.1";
    private const string OcspAccessMethod = "1.3.6.1.5.5.7.48.1";
    private const string CrlDistributionPoints = "2.5.29.31";

    /// <summary>Critical extensions this library understands; any other critical extension fails the certificate (RFC 5280, 4.2).</summary>
    private static readonly HashSet<string> KnownCritical =
    [
        KeyUsageOid, BasicConstraintsOid, ExtendedKeyUsageOid, OcspNoCheck,
        "2.5.29.14", // subject key identifier
        "2.5.29.35", // authority key identifier
        "2.5.29.17", // subject alternative name
        CertificatePolicies.CertificatePoliciesOid,
        CertificatePolicies.PolicyMappingsOid,
        CertificatePolicies.PolicyConstraintsOid,
        CertificatePolicies.InhibitAnyPolicyOid,
        NameConstraints.Oid,
        PdfQcStatements.Oid,
    ];

    public static string HashOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2" : hash == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3" : "2.16.840.1.101.3.4.2.1";

    /// <exception cref="FormatException">The algorithm is unknown or SHA-1, which is refused for signatures.</exception>
    public static HashAlgorithmName HashFromOid(string oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.3.14.3.2.26" => throw new FormatException("SHA-1 signatures are not accepted: SHA-1 is broken for signatures."),
        _ => throw new FormatException("Unsupported digest algorithm " + oid + "."),
    };

    /// <summary>The hash named by an OID, SHA-1 included: OCSP certificate identifiers use it to find, not to sign.</summary>
    public static HashAlgorithmName? LookupHash(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    public static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data.ToArray());
        return hash.GetHashAndReset();
    }

    public static string EcdsaOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA384 ? "1.2.840.10045.4.3.3" : hash == HashAlgorithmName.SHA512 ? "1.2.840.10045.4.3.4" : "1.2.840.10045.4.3.2";

    /// <summary>The serial number as its DER INTEGER content: X509Certificate2 gives it little endian.</summary>
    public static byte[] SerialNumber(X509Certificate2 certificate)
    {
        byte[] serial = certificate.GetSerialNumber();
        Array.Reverse(serial);
        return serial;
    }

    /// <summary>
    /// A certificate from untrusted DER. The platform parses lazily and some damaged encodings
    /// only fail when a property is read, with exceptions of several types; everything the
    /// library reads is read here once, so a bad certificate fails now, as a FormatException.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a usable certificate.</exception>
    public static X509Certificate2 Load(byte[] der)
    {
        try
        {
            X509Certificate2 certificate = Compat.LoadCertificate(der);
            _ = certificate.SubjectName.RawData;
            _ = certificate.IssuerName.RawData;
            _ = certificate.NotBefore;
            _ = certificate.NotAfter;
            _ = certificate.GetSerialNumber();
            foreach (X509Extension e in certificate.Extensions) _ = e.Oid?.Value;
            _ = SubjectPublicKey(certificate);
            return certificate;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            throw new FormatException("A certificate cannot be read: " + e.Message, e);
        }
    }

    /// <summary>The subjectPublicKey bits of a certificate, which OCSP hashes to name the issuer.</summary>
    public static byte[] SubjectPublicKey(X509Certificate2 certificate)
    {
        List<DerNode> tbs = DerNode.Parse(certificate.RawData).At(0).Children();
        int at = tbs.Count > 0 && tbs[0].Tag == 0xA0 ? 1 : 0;
        if (tbs.Count < at + 6) throw new FormatException("Certificate is truncated.");
        return tbs[at + 5].At(1).BitStringValue();
    }

    /// <summary>
    /// Whether a signed structure (a certificate, a CRL, an OCSP basic response: SEQUENCE of the
    /// signed part, the algorithm and the signature bits) verifies with a certificate's key.
    /// </summary>
    public static bool VerifySigned(DerNode signed, X509Certificate2 key)
    {
        try
        {
            List<DerNode> parts = signed.Children();
            if (parts.Count < 3) return false;
            return Verify(parts[0].Encoded, parts[2].BitStringValue(), parts[1], key, null);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="certificate"/> names <paramref name="issuer"/> as its issuer and carries its signature.</summary>
    public static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)) return false;
        try
        {
            return VerifySigned(DerNode.Parse(certificate.RawData), issuer);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsSelfIssued(X509Certificate2 certificate) => certificate.IssuerName.RawData.AsSpan().SequenceEqual(certificate.SubjectName.RawData);

    /// <summary>
    /// Verifies a signature under an AlgorithmIdentifier. RSA with PKCS #1 v1.5 or PSS and ECDSA,
    /// with SHA-256, 384 or 512. A CMS signer may name plain rsaEncryption and give the hash
    /// separately in <paramref name="cmsDigest"/>. SHA-1 is refused.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, byte[] signature, DerNode algorithm, X509Certificate2 key, HashAlgorithmName? cmsDigest)
    {
        List<DerNode> alg = algorithm.Children();
        string oid = alg.Count > 0 ? alg[0].OidValue() : throw new FormatException("Signature algorithm is missing.");
        HashAlgorithmName hash;
        RSASignaturePadding? padding = null;
        bool ecdsa = false;
        switch (oid)
        {
            case "1.2.840.113549.1.1.11": hash = HashAlgorithmName.SHA256; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.12": hash = HashAlgorithmName.SHA384; padding = RSASignaturePadding.Pkcs1; break;
            case "1.2.840.113549.1.1.13": hash = HashAlgorithmName.SHA512; padding = RSASignaturePadding.Pkcs1; break;
            case RsaEncryption when cmsDigest is HashAlgorithmName h: hash = h; padding = RSASignaturePadding.Pkcs1; break;
            case RsaPss: hash = PssHash(alg.Count > 1 ? alg[1] : null); padding = RSASignaturePadding.Pss; break;
            case "1.2.840.10045.4.3.2": hash = HashAlgorithmName.SHA256; ecdsa = true; break;
            case "1.2.840.10045.4.3.3": hash = HashAlgorithmName.SHA384; ecdsa = true; break;
            case "1.2.840.10045.4.3.4": hash = HashAlgorithmName.SHA512; ecdsa = true; break;
            case "1.2.840.10045.2.1" when cmsDigest is HashAlgorithmName h: hash = h; ecdsa = true; break;
            default: return false;
        }

        byte[] bytes = data.ToArray();
        try
        {
            if (!ecdsa)
            {
                using RSA? rsa = key.GetRSAPublicKey();
                return rsa is not null && rsa.VerifyData(bytes, signature, hash, padding!);
            }

            using ECDsa? ec = key.GetECDsaPublicKey();
            if (ec is null) return false;
            byte[]? raw = DerToP1363(signature, (ec.KeySize + 7) / 8);
            return raw is not null && ec.VerifyData(bytes, raw, hash);
        }
        catch (CryptographicException)
        {
            // A key or signature the platform cannot use does not verify.
            return false;
        }
    }

    /// <summary>RSASSA-PSS parameters (RFC 4055): .NET verifies MGF1 with the message hash and a salt as long as the hash.</summary>
    private static HashAlgorithmName PssHash(DerNode? parameters)
    {
        if (parameters is null || parameters.Tag != Der.Sequence) throw new FormatException("RSASSA-PSS needs explicit parameters; the SHA-1 defaults are refused.");
        HashAlgorithmName? hash = null, mask = null;
        long salt = 20;
        foreach (DerNode p in parameters.Children())
        {
            switch (p.Tag)
            {
                case 0xA0: hash = HashFromOid(p.At(0).At(0).OidValue()); break;
                case 0xA1: mask = p.At(0).At(0).OidValue() == "1.2.840.113549.1.1.8" ? HashFromOid(p.At(0).At(1).At(0).OidValue()) : null; break;
                case 0xA2: salt = p.At(0).SmallInteger(); break;
            }
        }

        if (hash is not HashAlgorithmName h || mask != h) throw new FormatException("RSASSA-PSS parameters are not supported.");
        int length = h == HashAlgorithmName.SHA512 ? 64 : h == HashAlgorithmName.SHA384 ? 48 : 32;
        if (salt != length) throw new FormatException("RSASSA-PSS salt length is not supported.");
        return h;
    }

    /// <summary>ECDSA signatures in X.509 and CMS are SEQUENCE { r INTEGER, s INTEGER }; .NET verifies r || s.</summary>
    private static byte[]? DerToP1363(byte[] der, int size)
    {
        List<DerNode> parts = DerNode.Parse(der).Children();
        if (parts.Count != 2) return null;
        var raw = new byte[2 * size];
        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<byte> v = parts[i].Content;
            while (v.Length > size && v[0] == 0) v = v.Slice(1);
            if (v.Length > size) return null;
            v.CopyTo(raw.AsSpan(i * size + size - v.Length));
        }

        return raw;
    }

    /// <summary>ECDSA signatures for CMS: .NET signs as r || s.</summary>
    public static byte[] P1363ToDer(byte[] raw)
    {
        int half = raw.Length / 2;
        return Der.Seq(Der.IntRaw(Unsigned(raw.AsSpan(0, half))), Der.IntRaw(Unsigned(raw.AsSpan(half))));

        static byte[] Unsigned(ReadOnlySpan<byte> v)
        {
            int start = 0;
            while (start < v.Length - 1 && v[start] == 0) start++;
            ReadOnlySpan<byte> trimmed = v.Slice(start);
            return (trimmed[0] & 0x80) != 0 ? [0, .. trimmed.ToArray()] : trimmed.ToArray();
        }
    }

    public static bool HasExtension(X509Certificate2 certificate, string oid) => certificate.Extensions[oid] is not null;

    /// <summary>The extended key usages, or null when the certificate has no such extension.</summary>
    public static IReadOnlyList<string>? ExtendedKeyUsages(X509Certificate2 certificate, out bool critical)
    {
        critical = false;
        if (certificate.Extensions[ExtendedKeyUsageOid] is not X509Extension extension) return null;
        critical = extension.Critical;
        var usages = new List<string>();
        foreach (DerNode usage in DerNode.Parse(extension.RawData).Children()) usages.Add(usage.OidValue());
        return usages;
    }

    /// <summary>The key usage bits, or null when the certificate has no such extension.</summary>
    public static X509KeyUsageFlags? KeyUsage(X509Certificate2 certificate) =>
        certificate.Extensions[KeyUsageOid] is X509Extension e ? new X509KeyUsageExtension(e, e.Critical).KeyUsages : null;

    /// <summary>Basic constraints: whether the certificate is a CA, and its path length limit, or null without the extension.</summary>
    public static (bool Ca, int? PathLength)? BasicConstraints(X509Certificate2 certificate)
    {
        if (certificate.Extensions[BasicConstraintsOid] is not X509Extension e) return null;
        var bc = new X509BasicConstraintsExtension(e, e.Critical);
        return (bc.CertificateAuthority, bc.HasPathLengthConstraint ? bc.PathLengthConstraint : null);
    }

    /// <summary>The first critical extension this library does not process, or null.</summary>
    public static string? UnknownCriticalExtension(X509Certificate2 certificate)
    {
        foreach (X509Extension e in certificate.Extensions)
        {
            if (e.Critical && e.Oid?.Value is string oid && !KnownCritical.Contains(oid)) return oid;
        }

        return null;
    }

    /// <summary>OCSP responder addresses from the authority information access extension.</summary>
    public static IReadOnlyList<string> OcspUrls(X509Certificate2 certificate)
    {
        var urls = new List<string>();
        if (certificate.Extensions[AuthorityInformationAccess] is not X509Extension e) return urls;
        try
        {
            foreach (DerNode access in DerNode.Parse(e.RawData).Children())
            {
                List<DerNode> parts = access.Children();
                if (parts.Count == 2 && parts[0].OidValue() == OcspAccessMethod && parts[1].Tag == 0x86) urls.Add(Ascii(parts[1]));
            }
        }
        catch (FormatException)
        {
            // A damaged extension gives no addresses.
        }

        return urls;
    }

    /// <summary>CRL addresses from the distribution points extension.</summary>
    public static IReadOnlyList<string> CrlUrls(X509Certificate2 certificate)
    {
        var urls = new List<string>();
        if (certificate.Extensions[CrlDistributionPoints] is not X509Extension e) return urls;
        try
        {
            foreach (DerNode point in DerNode.Parse(e.RawData).Children())
            {
                foreach (DerNode field in point.Children())
                {
                    // distributionPoint [0] { fullName [0] GeneralNames }
                    if (field.Tag != 0xA0) continue;
                    foreach (DerNode name in field.Children())
                    {
                        if (name.Tag != 0xA0) continue;
                        foreach (DerNode general in name.Children())
                        {
                            if (general.Tag == 0x86) urls.Add(Ascii(general));
                        }
                    }
                }
            }
        }
        catch (FormatException)
        {
            // A damaged extension gives no addresses.
        }

        return urls;
    }

    private static string Ascii(DerNode node) => System.Text.Encoding.ASCII.GetString(node.Content.ToArray());

    public static DateTimeOffset NotBefore(X509Certificate2 certificate) => new(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);

    public static DateTimeOffset NotAfter(X509Certificate2 certificate) => new(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
}

/// <summary>
/// The signature checks of one operation on certificates, CRLs and OCSP responses. Validation data
/// comes from the document and can be hostile: thousands of certificates or responses that copy
/// the right names would otherwise cost a signature check for every pairing. Each certificate and
/// issuer pair is checked once, and the total is bounded.
/// </summary>
internal sealed class SignatureChecks
{
    /// <summary>Far above what real chains and revocation data need, which is tens of checks per signature.</summary>
    public const int MaxChecks = 4000;

    private readonly Dictionary<string, bool> _results = new(StringComparer.Ordinal);
    private int _count;

    /// <summary>Whether <paramref name="certificate"/> names <paramref name="issuer"/> as its issuer and carries its signature.</summary>
    /// <exception cref="ExhaustedException">The operation used up its checks.</exception>
    public bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)) return false;
        return Remember(certificate.RawData, issuer, () => Pkix.IsIssuedBy(certificate, issuer));
    }

    /// <summary>Whether a CRL or an OCSP basic response verifies with a certificate's key.</summary>
    /// <exception cref="ExhaustedException">The operation used up its checks.</exception>
    public bool VerifySigned(DerNode signed, X509Certificate2 key) => Remember(signed.Encoded.ToArray(), key, () => Pkix.VerifySigned(signed, key));

    private bool Remember(byte[] data, X509Certificate2 key, Func<bool> check)
    {
        string id = Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA256, data)) + Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA256, key.RawData));
        if (!_results.TryGetValue(id, out bool result))
        {
            if (++_count > MaxChecks) throw new ExhaustedException();
            result = check();
            _results[id] = result;
        }

        return result;
    }

    /// <summary>The validation data needs more signature checks than <see cref="MaxChecks"/>.</summary>
    public sealed class ExhaustedException : Exception
    {
        public ExhaustedException()
            : base("The validation data needs more than " + MaxChecks + " signature checks, so it was not evaluated.")
        {
        }
    }
}
