using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// Supplies revocation data: OCSP responses and CRLs. The library asks for each certificate it
/// needs; the application fetches them however it likes (HTTP, a cache, files) or returns none.
/// The library never opens a connection itself.
/// </summary>
public interface IPdfRevocationSource
{
    /// <summary>The OCSP responses and CRLs the application has for <see cref="PdfRevocationRequest.Certificate"/>; empty when it has none.</summary>
    PdfRevocationValues GetRevocationValues(PdfRevocationRequest request);
}

/// <summary>
/// DER encoded revocation data: OCSP responses (RFC 6960 OCSPResponse, or a BasicOCSPResponse)
/// and CRLs (RFC 5280 CertificateList). As a source it offers all of them for every certificate;
/// the library keeps only those that match and verify.
/// </summary>
public sealed class PdfRevocationValues : IPdfRevocationSource
{
    /// <summary>No revocation data.</summary>
    public static PdfRevocationValues None { get; } = new();

    /// <summary>OCSP responses, DER encoded.</summary>
    public IReadOnlyList<byte[]> OcspResponses { get; init; } = [];

    /// <summary>Certificate revocation lists, DER encoded.</summary>
    public IReadOnlyList<byte[]> Crls { get; init; } = [];

    /// <inheritdoc/>
    PdfRevocationValues IPdfRevocationSource.GetRevocationValues(PdfRevocationRequest request) => this;
}

/// <summary>A certificate whose revocation status is needed, with what the application needs to fetch it.</summary>
public sealed class PdfRevocationRequest
{
    internal PdfRevocationRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        Certificate = certificate;
        Issuer = issuer;
        OcspUrls = Pkix.OcspUrls(certificate);
        CrlUrls = Pkix.CrlUrls(certificate);
    }

    /// <summary>The certificate to check.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificate that issued it.</summary>
    public X509Certificate2 Issuer { get; }

    /// <summary>OCSP responder addresses from the certificate's authority information access extension.</summary>
    public IReadOnlyList<string> OcspUrls { get; }

    /// <summary>CRL addresses from the certificate's CRL distribution points extension.</summary>
    public IReadOnlyList<string> CrlUrls { get; }

    /// <summary>
    /// A DER encoded OCSP request (RFC 6960) for the certificate, to POST to an address in
    /// <see cref="OcspUrls"/> with content type application/ocsp-request. The certificate is
    /// identified with SHA-1 hashes, which every responder accepts; this identifies, it does not sign.
    /// </summary>
    public byte[] CreateOcspRequest()
    {
        byte[] certId = Ocsp.CertId(HashAlgorithmName.SHA1, Certificate, Issuer);
        return Der.Seq(Der.Seq(Der.Seq(Der.Seq(certId))));
    }
}

/// <summary>An OCSP response (RFC 6960): its single responses and who signed it.</summary>
internal sealed class Ocsp
{
    private DerNode _basic = null!;

    private Ocsp()
    {
    }

    /// <summary>The bytes as given: an OCSPResponse or a BasicOCSPResponse.</summary>
    public byte[] Encoded { get; private set; } = [];

    public DateTimeOffset ProducedAt { get; private set; }

    public List<X509Certificate2> Certificates { get; } = [];

    public List<Entry> Responses { get; } = [];

    /// <summary>A CertID for a certificate, hashing the issuer's name and key.</summary>
    public static byte[] CertId(HashAlgorithmName hash, X509Certificate2 certificate, X509Certificate2 issuer) => Der.Seq(
        Der.Seq(Der.Oid(HashOidAny(hash)), Der.NullValue()),
        Der.Octets(Pkix.Hash(hash, certificate.IssuerName.RawData)),
        Der.Octets(Pkix.Hash(hash, Pkix.SubjectPublicKey(issuer))),
        Der.IntRaw(Pkix.SerialNumber(certificate)));

    private static string HashOidAny(HashAlgorithmName hash) => hash == HashAlgorithmName.SHA1 ? "1.3.14.3.2.26" : Pkix.HashOid(hash);

    /// <summary>The parsed response, or null when it is not a successful basic OCSP response.</summary>
    public static Ocsp? TryParse(byte[] der)
    {
        try
        {
            DerNode top = DerNode.Parse(der);
            var result = new Ocsp { Encoded = top.Encoded.ToArray() };
            DerNode basic = top;
            if (top.At(0).Tag == Der.Enumerated)
            {
                // OCSPResponse: status 0 is successful; responseBytes [0] { type, OCTET STRING }.
                if (top.At(0).SmallInteger() != 0) return null;
                DerNode bytes = top.At(1).At(0);
                if (bytes.At(0).OidValue() != "1.3.6.1.5.5.7.48.1.1") return null;
                basic = DerNode.Parse(bytes.At(1).Content.ToArray());
            }

            result._basic = basic;
            List<DerNode> data = basic.At(0).Children();
            int at = data.Count > 0 && data[0].Tag == 0xA0 ? 1 : 0;
            if (data.Count < at + 3) return null;
            result.ProducedAt = data[at + 1].TimeValue();
            foreach (DerNode single in data[at + 2].Children())
            {
                List<DerNode> parts = single.Children();
                if (parts.Count < 3) continue;
                List<DerNode> id = parts[0].Children();
                if (id.Count < 4) continue;
                var entry = new Entry
                {
                    Hash = Pkix.LookupHash(id[0].At(0).OidValue()),
                    NameHash = id[1].Content.ToArray(),
                    KeyHash = id[2].Content.ToArray(),
                    Serial = id[3].Content.ToArray(),
                    Status = parts[1].Tag switch { 0x80 => 0, 0xA1 => 1, _ => 2 },
                    ThisUpdate = parts[2].TimeValue(),
                };
                if (entry.Status == 1) entry.RevocationTime = parts[1].At(0).TimeValue();
                foreach (DerNode optional in parts.Skip(3))
                {
                    if (optional.Tag == 0xA0) entry.NextUpdate = optional.At(0).TimeValue();
                }

                result.Responses.Add(entry);
            }

            List<DerNode> outer = basic.Children();
            if (outer.Count > 3 && outer[3].Tag == 0xA0)
            {
                foreach (DerNode c in outer[3].At(0).Children()) result.Certificates.Add(Pkix.Load(c.Encoded.ToArray()));
            }

            return result;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The single response about a certificate, or null.</summary>
    public Entry? Find(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        byte[] serial = Pkix.SerialNumber(certificate);
        byte[]? key = null;
        foreach (Entry e in Responses)
        {
            if (e.Hash is not HashAlgorithmName h || !e.Serial.AsSpan().SequenceEqual(serial)) continue;
            if (!Pkix.Hash(h, certificate.IssuerName.RawData).AsSpan().SequenceEqual(e.NameHash)) continue;
            key ??= Pkix.SubjectPublicKey(issuer);
            if (Pkix.Hash(h, key).AsSpan().SequenceEqual(e.KeyHash)) return e;
        }

        return null;
    }

    /// <summary>
    /// The certificate that signed the response: the issuing CA itself, or a responder the CA
    /// delegated to (issued by it, marked for OCSP signing, valid when the response was produced).
    /// Null when no such certificate verifies the signature.
    /// </summary>
    public X509Certificate2? Signer(X509Certificate2 issuer, IEnumerable<X509Certificate2> candidates, SignatureChecks checks)
    {
        if (checks.VerifySigned(_basic, issuer)) return issuer;
        foreach (X509Certificate2 responder in Certificates.Concat(candidates))
        {
            if (!checks.IsIssuedBy(responder, issuer)) continue;
            if (ProducedAt < Pkix.NotBefore(responder) || ProducedAt > Pkix.NotAfter(responder)) continue;
            try
            {
                if (Pkix.ExtendedKeyUsages(responder, out _) is not { } usages || !usages.Contains(Pkix.OcspSigningUsage)) continue;
            }
            catch (FormatException)
            {
                continue;
            }

            if (checks.VerifySigned(_basic, responder)) return responder;
        }

        return null;
    }

    /// <summary>One SingleResponse: status 0 good, 1 revoked, 2 unknown.</summary>
    public sealed class Entry
    {
        public HashAlgorithmName? Hash { get; init; }

        public byte[] NameHash { get; init; } = [];

        public byte[] KeyHash { get; init; } = [];

        public byte[] Serial { get; init; } = [];

        public int Status { get; init; }

        public DateTimeOffset? RevocationTime { get; set; }

        public DateTimeOffset ThisUpdate { get; init; }

        public DateTimeOffset? NextUpdate { get; set; }
    }
}

/// <summary>A certificate revocation list (RFC 5280, 5.1).</summary>
internal sealed class Crl
{
    private DerNode _signed = null!;
    private DerNode? _revoked;

    private Crl()
    {
    }

    public byte[] Encoded { get; private set; } = [];

    public byte[] Issuer { get; private set; } = [];

    public DateTimeOffset ThisUpdate { get; private set; }

    public DateTimeOffset? NextUpdate { get; private set; }

    /// <summary>
    /// The first critical CRL extension, or null. The usual ones mark a delta CRL or a list
    /// partitioned by distribution point or reason, where a certificate missing from the list
    /// proves nothing; RFC 5280, 5.2, forbids using a CRL with a critical extension the reader
    /// does not process, so such a list is not used.
    /// </summary>
    public string? CriticalExtension { get; private set; }

    /// <summary>The parsed list, or null when it is not a CRL.</summary>
    public static Crl? TryParse(byte[] der)
    {
        try
        {
            DerNode signed = DerNode.Parse(der);
            List<DerNode> tbs = signed.At(0).Children();
            int at = tbs.Count > 0 && tbs[0].Tag == Der.Integer ? 1 : 0;
            if (tbs.Count < at + 3) return null;
            var crl = new Crl { _signed = signed, Encoded = signed.Encoded.ToArray(), Issuer = tbs[at + 1].Encoded.ToArray(), ThisUpdate = tbs[at + 2].TimeValue() };
            for (int i = at + 3; i < tbs.Count; i++)
            {
                if (tbs[i].Tag is Der.UtcTime or Der.GeneralizedTime) crl.NextUpdate = tbs[i].TimeValue();
                else if (tbs[i].Tag == Der.Sequence) crl._revoked = tbs[i];
                else if (tbs[i].Tag == 0xA0) crl.CriticalExtension = FirstCritical(tbs[i].At(0));
            }

            return crl;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool IsFrom(X509Certificate2 certificate) => Issuer.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    /// <summary>Extensions: SEQUENCE OF { extnID, critical BOOLEAN DEFAULT FALSE, extnValue }.</summary>
    private static string? FirstCritical(DerNode extensions)
    {
        foreach (DerNode extension in extensions.Children())
        {
            List<DerNode> parts = extension.Children();
            if (parts.Count == 3 && parts[1].Tag == Der.Boolean && parts[1].Content.Length == 1 && parts[1].Content[0] != 0) return parts[0].OidValue();
        }

        return null;
    }

    /// <summary>Whether the issuer signed the list; its key usage, when present, must allow CRL signing.</summary>
    public bool IsSignedBy(X509Certificate2 issuer, SignatureChecks checks)
    {
        try
        {
            if (Pkix.KeyUsage(issuer) is X509KeyUsageFlags usage && (usage & X509KeyUsageFlags.CrlSign) == 0) return false;
        }
        catch (CryptographicException)
        {
            return false;
        }

        return checks.VerifySigned(_signed, issuer);
    }

    /// <summary>When the certificate was revoked, or null when it is not listed.</summary>
    public DateTimeOffset? RevokedAt(X509Certificate2 certificate)
    {
        if (_revoked is null) return null;
        byte[] serial = Pkix.SerialNumber(certificate);
        foreach (DerNode entry in _revoked.Children())
        {
            if (entry.At(0).Content.SequenceEqual(serial)) return entry.At(1).TimeValue();
        }

        return null;
    }
}
