using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// CMS SignedData (RFC 5652) for PDF signatures: detached, with the CAdES signed attributes PAdES
/// requires (content type, message digest, ESS signing certificate v2; RFC 5035), and optionally
/// a signature timestamp as an unsigned attribute (RFC 3161, appendix A).
/// </summary>
internal static class Cms
{
    public const string Data = "1.2.840.113549.1.7.1";
    public const string SignedData = "1.2.840.113549.1.7.2";
    public const string TstInfo = "1.2.840.113549.1.9.16.1.4";
    public const string ContentTypeAttribute = "1.2.840.113549.1.9.3";
    public const string MessageDigestAttribute = "1.2.840.113549.1.9.4";
    public const string SigningCertificate = "1.2.840.113549.1.9.16.2.12";
    public const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Signs a digest; <paramref name="timestamp"/> turns the signature value into a timestamp token to attach.</summary>
    public static byte[] Sign(byte[] digest, HashAlgorithmName hash, X509Certificate2 certificate, byte[][] chain, Func<byte[], byte[]>? timestamp = null)
    {
        byte[] digestAlgorithm = Der.Seq(Der.Oid(Pkix.HashOid(hash)));
        byte[] serial = Pkix.SerialNumber(certificate);
        byte[] issuerSerial = Der.Seq(certificate.IssuerName.RawData, Der.IntRaw(serial));

        // ESS signing certificate v2: the certificate's hash binds it to the signature. SHA-256,
        // the default, is not written out.
        byte[] certHash = Pkix.Hash(hash, certificate.RawData);
        byte[] essIssuerSerial = Der.Seq(Der.Seq(Der.Context(4, true, certificate.IssuerName.RawData)), Der.IntRaw(serial));
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

        // The timestamp covers the signature value, and goes in the unsigned attributes [1].
        byte[] unsigned = [];
        if (timestamp is not null)
        {
            byte[] token = timestamp(signature);
            byte[] set = Der.SetOf(Der.Seq(Der.Oid(SignatureTimeStampToken), Der.SetOf(token)));
            unsigned = Der.Concat([0xA1], set.AsSpan(1).ToArray());
        }

        byte[] signerInfo = Der.Seq(Der.Int(1), issuerSerial, digestAlgorithm, signedAttributes, signatureAlgorithm, Der.Octets(signature), unsigned);
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
    public static CmsSignedData Verify(byte[] cms, byte[] pdf, int gapStart, int gapEnd, int end, PdfSignatureInfo info)
    {
        CmsSignedData parsed = CmsSignedData.Parse(cms);
        info.Certificate = parsed.Signer;
        info.SignerName = parsed.Signer?.GetNameInfo(X509NameType.SimpleName, false);

        byte[] actual = PdfSigner.Hash(parsed.Hash, pdf, gapStart, gapEnd, end);
        byte[]? message = parsed.HasSignedAttributes ? parsed.MessageDigest : actual;
        info.DigestMatches = message is not null && message.AsSpan().SequenceEqual(actual);
        info.SignatureValid = parsed.VerifySignature(() => [.. pdf.AsSpan(0, gapStart).ToArray(), .. pdf.AsSpan(gapEnd, end - gapEnd).ToArray()]);
        if (!info.DigestMatches) info.Problem = "The document changed after it was signed.";
        else if (!info.SignatureValid) info.Problem = parsed.Problem ?? "The signature does not verify with the signer's certificate.";
        return parsed;
    }

    private static (byte[] Signature, byte[] Algorithm) SignBytes(byte[] data, HashAlgorithmName hash, X509Certificate2 certificate)
    {
        using (RSA? rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is not null)
            {
                return (rsa.SignData(data, hash, RSASignaturePadding.Pkcs1), Der.Seq(Der.Oid(Pkix.RsaEncryption), Der.NullValue()));
            }
        }

        using ECDsa ecdsa = certificate.GetECDsaPrivateKey() ?? throw new ArgumentException("The certificate's key must be RSA or ECDSA.", nameof(certificate));
        byte[] raw = ecdsa.SignData(data, hash);
        return (Pkix.P1363ToDer(raw), Der.Seq(Der.Oid(Pkix.EcdsaOid(hash))));
    }
}

/// <summary>A parsed CMS SignedData with one signer: a PDF signature or an RFC 3161 timestamp token.</summary>
internal sealed class CmsSignedData
{
    private DerNode? _signedAttributes;
    private DerNode _signatureAlgorithm = null!;

    private CmsSignedData()
    {
    }

    /// <summary>The DER encoding, without the zero padding a PDF Contents string carries.</summary>
    public byte[] Encoded { get; private set; } = [];

    public string ContentType { get; private set; } = "";

    /// <summary>The encapsulated content, or null for a detached signature.</summary>
    public byte[]? Content { get; private set; }

    public List<X509Certificate2> Certificates { get; } = [];

    public X509Certificate2? Signer { get; private set; }

    public HashAlgorithmName Hash { get; private set; }

    public bool HasSignedAttributes => _signedAttributes is not null;

    public byte[]? MessageDigest { get; private set; }

    public byte[] SignatureValue { get; private set; } = [];

    /// <summary>The signature timestamp token in the unsigned attributes, or null.</summary>
    public byte[]? TimestampToken { get; private set; }

    /// <summary>Why <see cref="VerifySignature"/> failed, when it can tell.</summary>
    public string? Problem { get; private set; }

    private string? SignedContentType { get; set; }

    private (HashAlgorithmName? Hash, byte[] Value)? EssCertificateHash { get; set; }

    /// <exception cref="FormatException">The bytes are not a CMS SignedData with a signer.</exception>
    public static CmsSignedData Parse(byte[] der)
    {
        DerNode contentInfo = DerNode.Parse(der);
        var result = new CmsSignedData { Encoded = contentInfo.Encoded.ToArray() };
        if (contentInfo.At(0).OidValue() != Cms.SignedData) throw new FormatException("Not a CMS SignedData.");
        List<DerNode> sd = contentInfo.At(1).At(0).Children();
        if (sd.Count < 4) throw new FormatException("CMS SignedData is truncated.");

        DerNode encapsulated = sd[2];
        result.ContentType = encapsulated.At(0).OidValue();
        if (encapsulated.Children().Count > 1)
        {
            // eContent [0] EXPLICIT OCTET STRING
            DerNode content = encapsulated.At(1).At(0);
            if (content.Tag != Der.OctetString) throw new FormatException("Encapsulated content is not an octet string.");
            result.Content = content.Content.ToArray();
        }

        DerNode? signerInfos = null;
        foreach (DerNode node in sd.Skip(3))
        {
            if (node.Tag == 0xA0)
            {
                foreach (DerNode c in node.Children())
                {
                    // Other certificate formats (attribute certificates) carry other tags.
                    if (c.Tag == Der.Sequence) result.Certificates.Add(Pkix.Load(c.Encoded.ToArray()));
                }
            }
            else if (node.Tag == Der.Set)
            {
                signerInfos = node;
            }
        }

        List<DerNode> si = (signerInfos ?? throw new FormatException("No signer information.")).At(0).Children();
        if (si.Count < 5) throw new FormatException("Signer information is truncated.");
        DerNode sid = si[1];
        if (sid.Tag == Der.Sequence)
        {
            DerNode issuer = sid.At(0);
            byte[] serial = sid.At(1).Content.ToArray();
            result.Signer = result.Certificates.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(issuer.Encoded) && Pkix.SerialNumber(c).AsSpan().SequenceEqual(serial));
        }
        else if (sid.Tag == 0x80)
        {
            // subjectKeyIdentifier [0]
            byte[] ski = sid.Content.ToArray();
            result.Signer = result.Certificates.FirstOrDefault(c => c.Extensions["2.5.29.14"] is X509SubjectKeyIdentifierExtension e && string.Equals(e.SubjectKeyIdentifier, Compat.ToHex(ski), StringComparison.OrdinalIgnoreCase));
        }

        result.Signer ??= result.Certificates.Count == 1 ? result.Certificates[0] : null;
        result.Hash = Pkix.HashFromOid(si[2].At(0).OidValue());

        int at = 3;
        if (si[at].Tag == 0xA0)
        {
            result._signedAttributes = si[at++];
            foreach (DerNode attribute in result._signedAttributes.Children())
            {
                string type = attribute.At(0).OidValue();
                DerNode value = attribute.At(1).At(0);
                switch (type)
                {
                    case Cms.MessageDigestAttribute:
                        result.MessageDigest = value.Content.ToArray();
                        break;
                    case Cms.ContentTypeAttribute:
                        result.SignedContentType = value.OidValue();
                        break;
                    case Cms.SigningCertificateV2:
                    {
                        // ESSCertIDv2: hashAlgorithm (default SHA-256), certHash, issuerSerial.
                        DerNode id = value.At(0).At(0);
                        List<DerNode> parts = id.Children();
                        if (parts.Count > 0 && parts[0].Tag == Der.Sequence)
                        {
                            HashAlgorithmName? h = Pkix.LookupHash(parts[0].At(0).OidValue());
                            result.EssCertificateHash = (h, id.At(1).Content.ToArray());
                        }
                        else
                        {
                            result.EssCertificateHash = (HashAlgorithmName.SHA256, id.At(0).Content.ToArray());
                        }

                        break;
                    }

                    case Cms.SigningCertificate when result.EssCertificateHash is null:
                        result.EssCertificateHash = (HashAlgorithmName.SHA1, value.At(0).At(0).At(0).Content.ToArray());
                        break;
                }
            }
        }

        if (si.Count < at + 2) throw new FormatException("Signer information is truncated.");
        result._signatureAlgorithm = si[at];
        DerNode signature = si[at + 1];
        if (signature.Tag != Der.OctetString) throw new FormatException("Signature value is not an octet string.");
        result.SignatureValue = signature.Content.ToArray();

        if (si.Count > at + 2 && si[at + 2].Tag == 0xA1)
        {
            foreach (DerNode attribute in si[at + 2].Children())
            {
                if (attribute.At(0).OidValue() == Cms.SignatureTimeStampToken) result.TimestampToken = attribute.At(1).At(0).Encoded.ToArray();
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the signer's key verifies the signature: over the signed attributes when there are
    /// any (their content type and certificate hash checked too), else over the detached content.
    /// </summary>
    public bool VerifySignature(Func<byte[]> detachedContent)
    {
        if (Signer is null)
        {
            Problem = "The signer's certificate is not in the signature.";
            return false;
        }

        byte[] signedInput;
        if (_signedAttributes is not null)
        {
            // Verify over the attributes as a SET (tag 0x31), as they were signed.
            signedInput = _signedAttributes.Encoded.ToArray();
            signedInput[0] = Der.Set;
            if (SignedContentType is not null && SignedContentType != ContentType)
            {
                Problem = "The signed content type attribute does not match the content.";
                return false;
            }

            if (EssCertificateHash is ({ } h, byte[] expected) && !Pkix.Hash(h, Signer.RawData).AsSpan().SequenceEqual(expected))
            {
                Problem = "The signing certificate attribute names a different certificate.";
                return false;
            }
        }
        else
        {
            signedInput = detachedContent();
        }

        try
        {
            if (Pkix.Verify(signedInput, SignatureValue, _signatureAlgorithm, Signer, Hash)) return true;
            Problem = "The signature does not verify with the signer's certificate.";
        }
        catch (FormatException e)
        {
            Problem = e.Message;
        }

        return false;
    }
}
