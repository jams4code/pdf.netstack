using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// A synthetic PKI for signature tests: a root, an intermediate CA, a signer, a time stamp
/// authority and a delegated OCSP responder, with a fake TSA, OCSP responder and CRL issuer. The
/// responses are assembled here from DER primitives, independently of the library's parsers.
/// </summary>
internal sealed class TestPki
{
    public const string OcspUrl = "http://ocsp.test/";
    public const string CrlUrl = "http://crl.test/intermediate.crl";
    private const string Sha256WithRsa = "1.2.840.113549.1.1.11";

    public TestPki(X509KeyUsageFlags signerUsage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, bool tsaUsage = true)
    {
        Now = DateTimeOffset.UtcNow;
        Now = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, Now.Second, TimeSpan.Zero);
        Root = Issue("Test Root CA", null, Ca(null));
        Intermediate = Issue("Test Issuing CA", Root, Ca(0));
        Signer = Issue("Test Signer", Intermediate,
        [
            new X509KeyUsageExtension(signerUsage, true),
            new X509Extension("1.3.6.1.5.5.7.1.1", Der.Seq(Der.Seq(Der.Oid("1.3.6.1.5.5.7.48.1"), Der.Tlv(0x86, Encoding.ASCII.GetBytes(OcspUrl)))), false),
            new X509Extension("2.5.29.31", Der.Seq(Der.Seq(Der.Context(0, true, Der.Context(0, true, Der.Tlv(0x86, Encoding.ASCII.GetBytes(CrlUrl)))))), false),
        ]);
        Tsa = Issue("Test Time Stamp Authority", Intermediate,
        [
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true),
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(tsaUsage ? "1.3.6.1.5.5.7.3.8" : "1.3.6.1.5.5.7.3.3") }, true),
        ]);
        Responder = Issue("Test OCSP Responder", Intermediate,
        [
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true),
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.9") }, false),
            new X509Extension("1.3.6.1.5.5.7.48.1.5", [0x05, 0x00], false),
        ]);
    }

    public DateTimeOffset Now { get; }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public X509Certificate2 Signer { get; }

    public X509Certificate2 Tsa { get; }

    public X509Certificate2 Responder { get; }

    /// <summary>The time the fake TSA certifies; null for <see cref="Now"/>.</summary>
    public DateTimeOffset? TimestampTime { get; set; }

    /// <summary>How the fake TSA misbehaves.</summary>
    public TsaFault Fault { get; set; }

    public int TimestampRequests { get; private set; }

    public PdfTimestampClient TimestampClient(PdfDigestAlgorithm digest = PdfDigestAlgorithm.Sha256) => new(Respond) { Digest = digest };

    /// <summary>All the revocation data, good for every certificate, issued at <see cref="Now"/>.</summary>
    public PdfRevocationValues GoodRevocation() => new()
    {
        OcspResponses = [Ocsp(Signer, Intermediate, Responder), Ocsp(Tsa, Intermediate, Responder)],
        Crls = [Crl(Intermediate, []), Crl(Root, [])],
    };

    public static X509Certificate2 Issue(string name, X509Certificate2? issuer, IEnumerable<X509Extension> extensions, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name + ", O=PdfNetStack Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (X509Extension e in extensions) request.CertificateExtensions.Add(e);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        DateTimeOffset from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset to = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        if (issuer is not null && to > issuer.NotAfter.ToUniversalTime()) to = issuer.NotAfter.ToUniversalTime();
        if (issuer is null) return request.CreateSelfSigned(from, to);
        var serial = new byte[9];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(serial);
        serial[0] &= 0x7F;

        // Signed through a generator, so the platform's checks on the issuer (CA flag, key usage) do not apply.
        using RSA issuerKey = issuer.GetRSAPrivateKey()!;
        using X509Certificate2 issued = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1), from, to, serial);
        return issued.CopyWithPrivateKey(key);
    }

#if NET8_0_OR_GREATER
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public static byte[] Sha1(byte[] data) => SHA1.HashData(data);
#else
    public static byte[] Sha256(byte[] data)
    {
        using var h = SHA256.Create();
        return h.ComputeHash(data);
    }

    public static byte[] Sha1(byte[] data)
    {
        using var h = SHA1.Create();
        return h.ComputeHash(data);
    }
#endif

    public static X509Extension[] Ca(int? pathLength) =>
    [
        new X509BasicConstraintsExtension(true, pathLength is not null, pathLength ?? 0, true),
        new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true),
    ];

    /// <summary>The fake TSA: answers a TimeStampReq with a token signed by <see cref="Tsa"/>.</summary>
    public byte[] Respond(byte[] request)
    {
        TimestampRequests++;
        List<DerNode> req = DerNode.Parse(request).Children();
        DerNode imprint = req[1];
        byte[]? nonce = req.FirstOrDefault(n => n.Tag == Der.Integer && n != req[0])?.Content.ToArray();
        if (Fault == TsaFault.Reject) return Der.Seq(Der.Seq(Der.Int(2)));
        if (Fault == TsaFault.Garbage) return [0x30, 0x82, 0x01];

        byte[] imprintBytes = imprint.Encoded.ToArray();
        if (Fault == TsaFault.WrongImprint)
        {
            imprintBytes = (byte[])imprintBytes.Clone();
            imprintBytes[^1] ^= 1;
        }

        if (Fault == TsaFault.WrongNonce && nonce is not null)
        {
            nonce = (byte[])nonce.Clone();
            nonce[^1] ^= 1;
        }

        byte[] tstInfo = Der.Seq(
            Der.Int(1),
            Der.Oid("1.2.3.4.1"),
            imprintBytes,
            Der.Int(TimestampRequests),
            Der.Time(TimestampTime ?? Now),
            nonce is null ? [] : Der.IntRaw(nonce));
        byte[] token = SignedData("1.2.840.113549.1.9.16.1.4", tstInfo, Tsa);
        if (Fault == TsaFault.BadSignature) token[^3] ^= 0x10;
        return Der.Seq(Der.Seq(Der.Int(0)), token);
    }

    /// <summary>A CMS SignedData encapsulating content, with the attributes RFC 3161 tokens carry.</summary>
    private static byte[] SignedData(string contentType, byte[] content, X509Certificate2 signer)
    {
        byte[] sha256 = Der.Seq(Der.Oid("2.16.840.1.101.3.4.2.1"));
        byte[] serial = signer.GetSerialNumber();
        Array.Reverse(serial);
        byte[][] attributes =
        [
            Der.Seq(Der.Oid("1.2.840.113549.1.9.3"), Der.SetOf(Der.Oid(contentType))),
            Der.Seq(Der.Oid("1.2.840.113549.1.9.4"), Der.SetOf(Der.Octets(Sha256(content)))),
            Der.Seq(Der.Oid("1.2.840.113549.1.9.16.2.47"), Der.SetOf(Der.Seq(Der.Seq(Der.Seq(Der.Octets(Sha256(signer.RawData))))))),
        ];
        byte[] set = Der.SetOf(attributes);
        using RSA key = signer.GetRSAPrivateKey()!;
        byte[] signature = key.SignData(set, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        byte[] signerInfo = Der.Seq(
            Der.Int(1),
            Der.Seq(signer.IssuerName.RawData, Der.IntRaw(serial)),
            sha256,
            Der.Concat([0xA0], set.AsSpan(1).ToArray()),
            Der.Seq(Der.Oid(Sha256WithRsa), Der.NullValue()),
            Der.Octets(signature));
        byte[] signedData = Der.Seq(
            Der.Int(3),
            Der.SetOf(sha256),
            Der.Seq(Der.Oid(contentType), Der.Context(0, true, Der.Octets(content))),
            Der.Context(0, true, signer.RawData),
            Der.SetOf(signerInfo));
        return Der.Seq(Der.Oid("1.2.840.113549.1.7.2"), Der.Context(0, true, signedData));
    }

    /// <summary>An OCSPResponse about one certificate, signed by <paramref name="by"/>.</summary>
    public byte[] Ocsp(X509Certificate2 certificate, X509Certificate2 issuer, X509Certificate2 by, DateTimeOffset? revokedAt = null, DateTimeOffset? thisUpdate = null, DateTimeOffset? nextUpdate = null, bool unknown = false)
    {
        DateTimeOffset produced = thisUpdate ?? Now;
        byte[] serial = certificate.GetSerialNumber();
        Array.Reverse(serial);
        byte[] issuerKey = issuer.PublicKey.EncodedKeyValue.RawData;
        byte[] certId = Der.Seq(
            Der.Seq(Der.Oid("1.3.14.3.2.26"), Der.NullValue()),
            Der.Octets(Sha1(certificate.IssuerName.RawData)),
            Der.Octets(Sha1(issuerKey)),
            Der.IntRaw(serial));
        byte[] status = unknown ? [0x82, 0x00] : revokedAt is DateTimeOffset at ? Der.Context(1, true, Der.Time(at)) : [0x80, 0x00];
        byte[] single = Der.Seq(certId, status, Der.Time(produced), nextUpdate is DateTimeOffset next ? Der.Context(0, true, Der.Time(next)) : []);
        byte[] tbs = Der.Seq(Der.Context(1, true, by.SubjectName.RawData), Der.Time(produced), Der.Seq(single));
        using RSA key = by.GetRSAPrivateKey()!;
        byte[] basic = Der.Seq(
            tbs,
            Der.Seq(Der.Oid(Sha256WithRsa), Der.NullValue()),
            Der.Bits(key.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            Der.Context(0, true, Der.Seq(by.RawData)));
        return Der.Seq(Der.Tlv(Der.Enumerated, [0]), Der.Context(0, true, Der.Seq(Der.Oid("1.3.6.1.5.5.7.48.1.1"), Der.Octets(basic))));
    }

    /// <summary>A CRL from <paramref name="issuer"/> listing <paramref name="revoked"/>; <paramref name="delta"/> marks it a delta CRL (a critical extension).</summary>
    public byte[] Crl(X509Certificate2 issuer, IEnumerable<(X509Certificate2 Certificate, DateTimeOffset At)> revoked, DateTimeOffset? thisUpdate = null, DateTimeOffset? nextUpdate = null, bool delta = false)
    {
        DateTimeOffset from = thisUpdate ?? Now;
        var entries = new List<byte[]>();
        foreach ((X509Certificate2 cert, DateTimeOffset at) in revoked)
        {
            byte[] serial = cert.GetSerialNumber();
            Array.Reverse(serial);
            entries.Add(Der.Seq(Der.IntRaw(serial), Utc(at)));
        }

        byte[] algorithm = Der.Seq(Der.Oid(Sha256WithRsa), Der.NullValue());
        byte[] tbs = Der.Seq(
            Der.Int(1),
            algorithm,
            issuer.SubjectName.RawData,
            Utc(from),
            Utc(nextUpdate ?? from.AddDays(7)),
            entries.Count > 0 ? Der.Seq([.. entries]) : [],
            delta ? Der.Context(0, true, Der.Seq(Der.Seq(Der.Oid("2.5.29.27"), Der.Bool(true), Der.Octets(Der.Int(1))))) : []);
        using RSA key = issuer.GetRSAPrivateKey()!;
        return Der.Seq(tbs, algorithm, Der.Bits(key.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));

        static byte[] Utc(DateTimeOffset t) => Der.Tlv(Der.UtcTime, Encoding.ASCII.GetBytes(t.UtcDateTime.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + "Z"));
    }
}

/// <summary>Ways the fake time stamp authority misbehaves.</summary>
internal enum TsaFault
{
    None,
    Reject,
    Garbage,
    WrongImprint,
    WrongNonce,
    BadSignature,
}
