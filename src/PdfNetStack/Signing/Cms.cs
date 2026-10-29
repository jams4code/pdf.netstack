using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// CMS SignedData (RFC 5652) for PDF signatures: detached, with the CAdES signed attributes PAdES
/// requires (content type, message digest, ESS signing certificate v2; RFC 5035).
/// </summary>
internal static class Cms
{
    private const string Data = "1.2.840.113549.1.7.1";
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string ContentTypeAttribute = "1.2.840.113549.1.9.3";
    private const string MessageDigestAttribute = "1.2.840.113549.1.9.4";
    private const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    private const string RsaEncryption = "1.2.840.113549.1.1.1";

    public static byte[] Sign(byte[] digest, HashAlgorithmName hash, X509Certificate2 certificate, byte[][] chain)
    {
        byte[] digestAlgorithm = Der.Seq(Der.Oid(HashOid(hash)));
        byte[] issuerSerial = Der.Seq(certificate.IssuerName.RawData, Der.IntRaw(SerialNumber(certificate)));

        // ESS signing certificate v2: the certificate's hash binds it to the signature. SHA-256,
        // the default, is not written out.
        byte[] certHash = HashData(hash, certificate.RawData);
        byte[] essIssuerSerial = Der.Seq(Der.Seq(Der.Context(4, true, certificate.IssuerName.RawData)), Der.IntRaw(SerialNumber(certificate)));
        byte[] essCertId = hash == HashAlgorithmName.SHA256
            ? Der.Seq(Der.Octets(certHash), essIssuerSerial)
            : Der.Seq(digestAlgorithm, Der.Octets(certHash), essIssuerSerial);

        byte[][] attributes =
        [
            Der.Seq(Der.Oid(ContentTypeAttribute), Der.SetOf(Der.Oid(Data))),
            Der.Seq(Der.Oid(MessageDigestAttribute), Der.SetOf(Der.Octets(digest))),
            Der.Seq(Der.Oid(SigningCertificateV2), Der.SetOf(Der.Seq(Der.Seq(essCertId)))),
        ];

        // The signature covers the attributes encoded as a SET; in the SignerInfo they carry tag [0].
        byte[] signedSet = Der.SetOf(attributes);
        byte[] signedAttributes = Der.Concat([0xA0], signedSet.AsSpan(1).ToArray());
        (byte[] signature, byte[] signatureAlgorithm) = SignBytes(signedSet, hash, certificate);

        byte[] signerInfo = Der.Seq(Der.Int(1), issuerSerial, digestAlgorithm, signedAttributes, signatureAlgorithm, Der.Octets(signature));
        byte[] certificates = Der.Context(0, true, Der.Concat([certificate.RawData, .. chain]));
        byte[] signedData = Der.Seq(
            Der.Int(1),
            Der.SetOf(digestAlgorithm),
            Der.Seq(Der.Oid(Data)),
            certificates,
            Der.SetOf(signerInfo));
        return Der.Seq(Der.Oid(SignedData), Der.Context(0, true, signedData));
    }

    /// <summary>Checks a detached CMS signature over the file outside the gap and records the result.</summary>
    public static void Verify(byte[] cms, byte[] pdf, int gapStart, int gapEnd, int end, PdfSignatureInfo info)
    {
        // The Contents string is zero padded after the DER structure; parse just the structure.
        DerNode contentInfo = DerNode.Parse(cms);
        List<DerNode> ci = contentInfo.Children();
        if (ci.Count < 2 || ci[0].OidValue() != SignedData) throw new FormatException("Not a CMS SignedData.");
        List<DerNode> sd = ci[1].Children()[0].Children();

        var certificates = new List<X509Certificate2>();
        DerNode? signerInfos = null;
        foreach (DerNode node in sd.Skip(3))
        {
            if (node.Tag == 0xA0) certificates.AddRange(node.Children().Select(c => Compat.LoadCertificate(c.Encoded.ToArray())));
            else if (node.Tag == Der.Set) signerInfos = node;
        }

        List<DerNode> si = (signerInfos ?? throw new FormatException("No signer information.")).Children()[0].Children();
        List<DerNode> sid = si[1].Children();
        byte[] serial = sid[1].Content.ToArray();
        X509Certificate2? signer = certificates.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(sid[0].Encoded) && SerialNumber(c).AsSpan().SequenceEqual(serial));
        signer ??= certificates.FirstOrDefault();
        info.Certificate = signer;
        info.SignerName = signer?.GetNameInfo(X509NameType.SimpleName, false);

        string digestOid = si[2].Children()[0].OidValue();
        HashAlgorithmName hash = HashFromOid(digestOid);
        byte[] actual = PdfSigner.Hash(hash, pdf, gapStart, gapEnd, end);

        int at = 3;
        byte[]? message = null;
        byte[] signedInput;
        if (si[at].Tag == 0xA0)
        {
            DerNode attributes = si[at++];
            foreach (DerNode attribute in attributes.Children())
            {
                List<DerNode> parts = attribute.Children();
                if (parts[0].OidValue() == MessageDigestAttribute) message = parts[1].Children()[0].Content.ToArray();
            }

            // Verify over the attributes as a SET (tag 0x31), as they were signed.
            signedInput = attributes.Encoded.ToArray();
            signedInput[0] = Der.Set;
        }
        else
        {
            message = actual;
            signedInput = pdf.AsSpan(0, gapStart).ToArray().Concat(pdf.AsSpan(gapEnd, end - gapEnd).ToArray()).ToArray();
        }

        info.DigestMatches = message is not null && message.AsSpan().SequenceEqual(actual);
        string algorithm = si[at].Children()[0].OidValue();
        byte[] signature = si[at + 1].Content.ToArray();
        info.SignatureValid = signer is not null && VerifyBytes(signedInput, signature, hash, algorithm, signer);
        if (!info.DigestMatches) info.Problem = "The document changed after it was signed.";
        else if (!info.SignatureValid) info.Problem = "The signature does not verify with the signer's certificate.";
    }

    private static (byte[] Signature, byte[] Algorithm) SignBytes(byte[] data, HashAlgorithmName hash, X509Certificate2 certificate)
    {
        using (RSA? rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is not null)
            {
                return (rsa.SignData(data, hash, RSASignaturePadding.Pkcs1), Der.Seq(Der.Oid(RsaEncryption), Der.NullValue()));
            }
        }

        using ECDsa ecdsa = certificate.GetECDsaPrivateKey() ?? throw new ArgumentException("The certificate's key must be RSA or ECDSA.", nameof(certificate));
        byte[] raw = ecdsa.SignData(data, hash);
        return (P1363ToDer(raw), Der.Seq(Der.Oid(EcdsaOid(hash))));
    }

    /// <summary>Verifies with the certificate's key; the signature algorithm must name that key type, and SHA-1 is refused.</summary>
    private static bool VerifyBytes(byte[] data, byte[] signature, HashAlgorithmName hash, string algorithm, X509Certificate2 certificate)
    {
        if (algorithm is "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1") return false; // SHA-1 with RSA or ECDSA
        using (RSA? rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                bool rsaAlgorithm = algorithm is RsaEncryption or "1.2.840.113549.1.1.11" or "1.2.840.113549.1.1.12" or "1.2.840.113549.1.1.13";
                return rsaAlgorithm && rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
            }
        }

        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is null || !algorithm.StartsWith("1.2.840.10045", StringComparison.Ordinal)) return false;
        return ecdsa.VerifyData(data, DerToP1363(signature, (ecdsa.KeySize + 7) / 8), hash);
    }

    /// <summary>The serial number as its DER INTEGER content: X509Certificate2 gives it little endian.</summary>
    private static byte[] SerialNumber(X509Certificate2 certificate)
    {
        byte[] serial = certificate.GetSerialNumber();
        Array.Reverse(serial);
        return serial;
    }

    /// <summary>ECDSA signatures in CMS are SEQUENCE { r INTEGER, s INTEGER }; .NET signs as r || s.</summary>
    private static byte[] P1363ToDer(byte[] raw)
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

    private static byte[] DerToP1363(byte[] der, int size)
    {
        List<DerNode> parts = DerNode.Parse(der).Children();
        var raw = new byte[2 * size];
        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<byte> v = parts[i].Content;
            while (v.Length > size && v[0] == 0) v = v.Slice(1);
            if (v.Length > size) throw new FormatException("ECDSA signature value is too long.");
            v.CopyTo(raw.AsSpan(i * size + size - v.Length));
        }

        return raw;
    }

    private static byte[] HashData(HashAlgorithmName name, byte[] data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static string HashOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2" : hash == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3" : "2.16.840.1.101.3.4.2.1";

    private static string EcdsaOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA384 ? "1.2.840.10045.4.3.3" : hash == HashAlgorithmName.SHA512 ? "1.2.840.10045.4.3.4" : "1.2.840.10045.4.3.2";

    private static HashAlgorithmName HashFromOid(string oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.3.14.3.2.26" => throw new FormatException("SHA-1 signatures are not accepted: SHA-1 is broken for signatures."),
        _ => throw new FormatException("Unsupported digest algorithm " + oid + "."),
    };
}
