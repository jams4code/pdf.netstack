using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Signing;

/// <summary>The hash a signature uses.</summary>
public enum PdfDigestAlgorithm
{
    /// <summary>SHA-256, the usual choice.</summary>
    Sha256,

    /// <summary>SHA-384.</summary>
    Sha384,

    /// <summary>SHA-512.</summary>
    Sha512,
}

/// <summary>A visible signature: a box on a page with the signer's name and the date.</summary>
/// <param name="PageIndex">Zero based page index.</param>
/// <param name="X">Left edge, points from the page's left.</param>
/// <param name="Y">Top edge, points from the page's top.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public sealed record PdfSignatureAppearance(int PageIndex, double X, double Y, double Width, double Height)
{
    /// <summary>The text shown; null for "Digitally signed by", the signer's name, the date and the reason.</summary>
    public string? Text { get; init; }

    /// <summary>The font of the text; null for Helvetica. Use an embedded font for PDF/A documents.</summary>
    public PdfFont? Font { get; init; }
}

/// <summary>How to sign.</summary>
public sealed record PdfSignatureOptions
{
    /// <summary>Why the document is signed.</summary>
    public string? Reason { get; init; }

    /// <summary>Where it is signed.</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The signature field's name; null picks Signature1, Signature2 and so on.</summary>
    public string? FieldName { get; init; }

    /// <summary>The hash. Default SHA-256.</summary>
    public PdfDigestAlgorithm Digest { get; init; } = PdfDigestAlgorithm.Sha256;

    /// <summary>Intermediate certificates to embed, so validators can build the chain to a trusted root.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    /// <summary>A visible signature, or null for an invisible one.</summary>
    public PdfSignatureAppearance? Appearance { get; init; }

    /// <summary>The signing time recorded in the signature dictionary; null for now.</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// A time stamp authority to timestamp the signature value (PAdES B-T); null for none (B-B).
    /// The token goes in the CMS signature as an unsigned attribute.
    /// </summary>
    public PdfTimestampClient? Timestamp { get; init; }
}

/// <summary>What <see cref="PdfSigner.Verify(byte[])"/> found about one signature or document timestamp.</summary>
public sealed class PdfSignatureInfo
{
    internal PdfSignatureInfo()
    {
    }

    /// <summary>The signature field's fully qualified name.</summary>
    public string FieldName { get; internal set; } = "";

    /// <summary>The signer's common name, from the certificate; the authority's for a document timestamp.</summary>
    public string? SignerName { get; internal set; }

    /// <summary>The signing certificate, for the caller's trust decision; the authority's for a document timestamp.</summary>
    public X509Certificate2? Certificate { get; internal set; }

    /// <summary>The time the signer's software recorded (/M), not a trusted timestamp.</summary>
    public DateTimeOffset? SigningTime { get; internal set; }

    /// <summary>The reason given.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The location given.</summary>
    public string? Location { get; internal set; }

    /// <summary>The signature format, such as ETSI.CAdES.detached, adbe.pkcs7.detached, or ETSI.RFC3161 for a document timestamp.</summary>
    public string? SubFilter { get; internal set; }

    /// <summary>True for a document timestamp (/DocTimeStamp): an RFC 3161 token over the document, with no signer.</summary>
    public bool IsDocumentTimestamp { get; internal set; }

    /// <summary>True when the signed bytes still hash to the value the signature covers.</summary>
    public bool DigestMatches { get; internal set; }

    /// <summary>True when the signature verifies with the certificate's public key.</summary>
    public bool SignatureValid { get; internal set; }

    /// <summary>True when the signature covers the whole file: nothing was appended after it.</summary>
    public bool CoversWholeDocument { get; internal set; }

    /// <summary>The signature timestamp (PAdES B-T), or the token of a document timestamp; null when there is none.</summary>
    public PdfTimestampInfo? Timestamp { get; internal set; }

    /// <summary>
    /// The signer's certificate checks: chain, validity, usage and revocation. Null unless
    /// <see cref="PdfSigner.Verify(byte[], PdfValidationOptions)"/> was given options. For a
    /// document timestamp, the authority's checks.
    /// </summary>
    public PdfCertificateValidation? Validation { get; internal set; }

    /// <summary>Why verification failed, or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The signed content is intact and the signature is cryptographically valid. Trust in the certificate is a separate decision.</summary>
    public bool IsIntact => DigestMatches && SignatureValid;
}

/// <summary>
/// PAdES baseline signatures (ETSI EN 319 142-1): B-B with a CAdES detached CMS signature, B-T
/// with an RFC 3161 signature timestamp, B-LT with validation data in the document security
/// store, and B-LTA with a document timestamp over it all. Every step is an incremental update,
/// so earlier signatures stay valid.
/// </summary>
/// <remarks>
/// The private key may be RSA or ECDSA, from a file or any key store X509Certificate2 can reach
/// (Windows certificate store, smart cards through their providers). Encrypted documents are not
/// supported. Time stamp authorities and revocation data are reached through the application;
/// the library never opens a network connection.
/// </remarks>
public static class PdfSigner
{
    /// <summary>Signs <paramref name="pdf"/> and returns the signed file.</summary>
    /// <exception cref="ArgumentException">The certificate has no usable private key.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">A timestamp was requested and the authority refused it or replied with an invalid token.</exception>
    public static byte[] Sign(byte[] pdf, X509Certificate2 certificate, PdfSignatureOptions? options = null)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(certificate, nameof(certificate));
        options ??= new PdfSignatureOptions();
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(certificate));

        var update = new IncrementalUpdate(pdf);
        PdfDocument doc = update.Doc;
        DateTimeOffset when = options.SigningTime ?? DateTimeOffset.UtcNow;

        // The signature value is written by hand; reserve its number.
        int signatureNumber = doc.NextNumber++;
        PdfPage page = doc.Pages[Math.Max(0, Math.Min(options.Appearance?.PageIndex ?? 0, doc.Pages.Count - 1))];
        PdfDictionary widget = Widget(options.FieldName ?? UniqueFieldName(doc, "Signature"), signatureNumber, page);
        if (options.Appearance is PdfSignatureAppearance look) Appear(doc, page, widget, look, certificate, options, when);
        update.AddField(page, doc.AddObject(widget));

        byte[][] chain = [.. options.Chain.Select(c => c.RawData)];
        int reserve = 8192 + certificate.RawData.Length + chain.Sum(c => c.Length) + (options.Timestamp?.ReservedSize ?? 0);
        HashAlgorithmName hash = HashName(options.Digest);
        PdfTimestampClient? tsa = options.Timestamp;
        Func<byte[], byte[]>? stamp = tsa is null ? null : value => tsa.GetToken(value);
        return update.Seal(
            new IncrementalUpdate.Placeholder(signatureNumber, "/Type/Sig/Filter/Adobe.PPKLite/SubFilter/ETSI.CAdES.detached", reserve, sink =>
            {
                sink.WriteAscii("/M");
                PdfSerializer.WriteString(sink, Encoding.ASCII.GetBytes(Documents.PdfText.FormatDate(when)));
                WriteText(sink, "Name", certificate.GetNameInfo(X509NameType.SimpleName, false));
                WriteText(sink, "Reason", options.Reason);
                WriteText(sink, "Location", options.Location);
                WriteText(sink, "ContactInfo", options.ContactInfo);
            }),
            hash,
            digest => Cms.Sign(digest, hash, certificate, chain, stamp));
    }

    /// <summary>
    /// Adds a document timestamp (/DocTimeStamp, an RFC 3161 token with SubFilter ETSI.RFC3161)
    /// over the whole file in an incremental update. After <see cref="AddValidationData"/> this
    /// makes a PAdES B-LTA document; repeating both later extends its life before the algorithms
    /// or certificates involved weaken or expire.
    /// </summary>
    /// <param name="pdf">The document, signed or not.</param>
    /// <param name="timestampAuthority">The authority to ask.</param>
    /// <param name="fieldName">The timestamp field's name; null picks Timestamp1, Timestamp2 and so on.</param>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">The authority refused the request or replied with an invalid token.</exception>
    public static byte[] AddDocumentTimestamp(byte[] pdf, PdfTimestampClient timestampAuthority, string? fieldName = null)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(timestampAuthority, nameof(timestampAuthority));
        var update = new IncrementalUpdate(pdf);
        PdfDocument doc = update.Doc;
        int number = doc.NextNumber++;
        PdfPage page = doc.Pages[0];
        update.AddField(page, doc.AddObject(Widget(fieldName ?? UniqueFieldName(doc, "Timestamp"), number, page)));
        return update.Seal(
            new IncrementalUpdate.Placeholder(number, "/Type/DocTimeStamp/Filter/Adobe.PPKLite/SubFilter/ETSI.RFC3161", timestampAuthority.ReservedSize, null),
            HashName(timestampAuthority.Digest),
            timestampAuthority.GetTokenForDigest);
    }

    /// <summary>
    /// Adds long term validation data (PAdES B-LT) in an incremental update: the document
    /// security store (DSS) receives the certificate chains of every signature and timestamp in
    /// the file, with OCSP responses and CRLs for each certificate, and a VRI entry per signature.
    /// Chains are built from the certificates in the signatures, in the store already, and in
    /// <paramref name="certificates"/>, up to a self-signed root. Revocation data comes from
    /// <paramref name="revocation"/> and is kept only when it matches the certificate and is
    /// signed by its issuer or the issuer's OCSP responder.
    /// </summary>
    /// <param name="pdf">A signed or timestamped document.</param>
    /// <param name="revocation">Supplies OCSP responses and CRLs; <see cref="PdfRevocationValues.None"/> to embed certificates only.</param>
    /// <param name="certificates">Extra certificates for the chains, such as intermediate and root CAs.</param>
    /// <exception cref="InvalidOperationException">The document has no readable signature or timestamp.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="PdfFormatException">The document's certificates and validation data need an unreasonable number of signature checks.</exception>
    public static byte[] AddValidationData(byte[] pdf, IPdfRevocationSource revocation, IReadOnlyList<X509Certificate2>? certificates = null)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(revocation, nameof(revocation));
        try
        {
            return CollectValidationData(pdf, revocation, certificates, new SignatureChecks());
        }
        catch (SignatureChecks.ExhaustedException e)
        {
            throw new PdfFormatException(e.Message);
        }
    }

    private static byte[] CollectValidationData(byte[] pdf, IPdfRevocationSource revocation, IReadOnlyList<X509Certificate2>? certificates, SignatureChecks checks)
    {
        var update = new IncrementalUpdate(pdf);
        PdfDocument doc = update.Doc;

        // Every signature and timestamp, with the certificates its chains start from.
        var signatures = new List<(string Key, List<X509Certificate2> Leaves)>();
        var pool = new List<X509Certificate2>(certificates ?? []);
        foreach (PdfSignatureField field in doc.Form.Fields.OfType<PdfSignatureField>())
        {
            if (doc.Resolve(field.Dictionary["V"]) is not PdfDictionary sig || doc.Resolve(sig["Contents"]) is not PdfString contents) continue;
            try
            {
                var leaves = new List<X509Certificate2>();
                CmsSignedData cms = CmsSignedData.Parse(contents.Value);
                pool.AddRange(cms.Certificates);
                if (cms.Signer is X509Certificate2 signer) leaves.Add(signer);
                if (cms.TimestampToken is byte[] token)
                {
                    CmsSignedData tsa = CmsSignedData.Parse(token);
                    pool.AddRange(tsa.Certificates);
                    if (tsa.Signer is X509Certificate2 authority) leaves.Add(authority);
                }

                // VRI names follow common practice: the whole Contents of a CMS signature, the token itself for a document timestamp.
                bool timestamp = sig.GetName("SubFilter") == "ETSI.RFC3161";
                signatures.Add((VriKey(timestamp ? cms.Encoded : contents.Value), leaves));
            }
            catch (FormatException)
            {
                // A damaged signature gets no validation data; the others still do.
            }
        }

        if (signatures.Count == 0) throw new InvalidOperationException("The document has no readable signature or timestamp.");
        pool.AddRange(SecurityStore.Read(doc).Certificates);

        var store = new DssWriter(doc);
        var fetched = new Dictionary<string, (List<PdfObject> Ocsps, List<PdfObject> Crls, List<X509Certificate2> Responders)>();
        foreach ((string key, List<X509Certificate2> leaves) in signatures)
        {
            var certs = new List<PdfObject>();
            var ocsps = new List<PdfObject>();
            var crls = new List<PdfObject>();
            var pending = new Queue<X509Certificate2>(leaves);
            var seen = new HashSet<string>();
            while (pending.Count > 0)
            {
                List<X509Certificate2> chain = CertificateValidator.BuildChain(pending.Dequeue(), pool, Pkix.IsSelfIssued, [], checks);
                for (int i = 0; i < chain.Count; i++)
                {
                    X509Certificate2 cert = chain[i];
                    string id = Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA256, cert.RawData));
                    if (!seen.Add(id)) continue;
                    certs.Add(store.Add("Certs", cert.RawData));
                    if (i + 1 >= chain.Count || Pkix.IsSelfIssued(cert) || Pkix.HasExtension(cert, Pkix.OcspNoCheck)) continue;

                    if (!fetched.TryGetValue(id, out var found))
                    {
                        found = Fetch(store, revocation, cert, chain[i + 1], pool, checks);
                        fetched[id] = found;
                    }

                    ocsps.AddRange(found.Ocsps);
                    crls.AddRange(found.Crls);

                    // A delegated OCSP responder needs validation data too: its chain and, unless exempt, its status.
                    foreach (X509Certificate2 responder in found.Responders) pending.Enqueue(responder);
                }
            }

            store.Vri(key, certs, ocsps, crls);
        }

        store.WriteTo(update);
        return update.Write();
    }

    /// <summary>Checks every signature of a file: byte range, digest and cryptographic validity, and any timestamp.</summary>
    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] pdf) => Verify(pdf, null);

    /// <summary>
    /// Checks every signature and document timestamp of a file and, with
    /// <paramref name="options"/>, validates the certificates of signers and time stamp
    /// authorities: chain to a trust anchor, validity, usage and revocation from the document
    /// security store and supplied data. Certificates are checked at the time of a valid
    /// timestamp when there is one, else now, unless the options set a time.
    /// </summary>
    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] pdf, PdfValidationOptions? options)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        PdfDocument doc = PdfDocument.Open(pdf);
        CertificateValidator? validator = options is null ? null : new CertificateValidator(options, options.UseDocumentSecurityStore ? SecurityStore.Read(doc) : new SecurityStore());
        var results = new List<PdfSignatureInfo>();
        foreach (PdfSignatureField field in doc.Form.Fields.OfType<PdfSignatureField>())
        {
            if (doc.Resolve(field.Dictionary["V"]) is not PdfDictionary sig) continue;
            var info = new PdfSignatureInfo
            {
                FieldName = field.Name,
                SubFilter = sig.GetName("SubFilter"),
                Reason = doc.Resolve(sig["Reason"]) is PdfString r ? r.ToText() : null,
                Location = doc.Resolve(sig["Location"]) is PdfString l ? l.ToText() : null,
                SigningTime = doc.Resolve(sig["M"]) is PdfString m ? Documents.PdfText.ParseDate(m.ToText()) : null,
            };
            info.IsDocumentTimestamp = info.SubFilter == "ETSI.RFC3161";
            results.Add(info);
            try
            {
                long[] range = doc.Resolve(sig["ByteRange"]) is PdfArray { Count: 4 } a ? [.. a.Select(v => ((PdfNumber)doc.Resolve(v)!).IntegerValue)] : [];
                if (range.Length != 4 || range[0] != 0 || range[1] < 0 || range[2] < range[1] || range[3] < 0 || range[2] + range[3] > pdf.Length)
                {
                    info.Problem = "The byte range is invalid.";
                    continue;
                }

                // The gap must be exactly this signature's Contents string: '<', hex digits, '>'.
                // The CMS is read from the gap itself and must match the dictionary's Contents, so
                // a byte range pointing anywhere else cannot pass.
                byte[]? fromGap = GapContents(pdf, (int)range[1], (int)range[2]);
                byte[] declared = ((PdfString)doc.Resolve(sig["Contents"])!).Value;
                if (fromGap is null || !fromGap.AsSpan().SequenceEqual(declared))
                {
                    info.Problem = "The byte range does not exclude exactly the signature's Contents.";
                    continue;
                }

                info.CoversWholeDocument = range[2] + range[3] == pdf.Length;
                int end = (int)(range[2] + range[3]);
                CmsSignedData cms;
                TimestampToken? token = null;
                if (info.IsDocumentTimestamp)
                {
                    token = TimestampToken.Parse(fromGap);
                    cms = token.Cms;
                    info.Certificate = cms.Signer;
                    info.SignerName = cms.Signer?.GetNameInfo(X509NameType.SimpleName, false);
                    info.DigestMatches = Hash(token.ImprintHash, pdf, (int)range[1], (int)range[2], end).AsSpan().SequenceEqual(token.Imprint);
                    info.Timestamp = Describe(token, info.DigestMatches);
                    info.SignatureValid = info.Timestamp.SignatureValid;
                    if (!info.DigestMatches) info.Problem = "The document changed after it was timestamped.";
                    else if (!info.SignatureValid) info.Problem = info.Timestamp.Problem;
                }
                else
                {
                    cms = Cms.Verify(fromGap, pdf, (int)range[1], (int)range[2], end, info);
                    if (cms.TimestampToken is byte[] bytes)
                    {
                        try
                        {
                            token = TimestampToken.Parse(bytes);
                            info.Timestamp = Describe(token, Pkix.Hash(token.ImprintHash, cms.SignatureValue).AsSpan().SequenceEqual(token.Imprint));
                        }
                        catch (FormatException e)
                        {
                            info.Timestamp = new PdfTimestampInfo { Problem = "The signature timestamp cannot be read: " + e.Message };
                        }
                    }
                }

                if (validator is not null) Validate(info, cms, token, validator, options!);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or CryptographicException or NullReferenceException or ArgumentOutOfRangeException)
            {
                info.Problem = "The signature cannot be read: " + e.Message;
            }
        }

        return results;
    }

    /// <summary>The VRI key of a signature: the uppercase hex SHA-1 of its bytes, as PAdES names it.</summary>
    internal static string VriKey(byte[] signature)
    {
        // PAdES defines the name with SHA-1; it identifies, it does not sign.
        return Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA1, signature));
    }

    /// <summary>The OCSP responses and CRLs for one certificate that match it and verify, added to the store.</summary>
    private static (List<PdfObject> Ocsps, List<PdfObject> Crls, List<X509Certificate2> Responders) Fetch(
        DssWriter store, IPdfRevocationSource source, X509Certificate2 cert, X509Certificate2 issuer, List<X509Certificate2> pool, SignatureChecks checks)
    {
        var ocsps = new List<PdfObject>();
        var crls = new List<PdfObject>();
        var responders = new List<X509Certificate2>();
        PdfRevocationValues values = source.GetRevocationValues(new PdfRevocationRequest(cert, issuer)) ?? PdfRevocationValues.None;
        foreach (byte[] data in values.OcspResponses)
        {
            if (data is null || Ocsp.TryParse(data) is not Ocsp response || response.Find(cert, issuer) is null) continue;
            if (response.Signer(issuer, pool, checks) is not X509Certificate2 signer) continue;
            ocsps.Add(store.Add("OCSPs", response.Encoded));
            if (!CertificateValidator.Same(signer, issuer) && !responders.Any(r => CertificateValidator.Same(r, signer)))
            {
                responders.Add(signer);
                pool.Add(signer);
            }
        }

        foreach (byte[] data in values.Crls)
        {
            if (data is null || Crl.TryParse(data) is not Crl crl || crl.CriticalExtension is not null || !crl.IsFrom(cert) || !crl.IsSignedBy(issuer, checks)) continue;
            crls.Add(store.Add("CRLs", crl.Encoded));
        }

        return (ocsps, crls, responders);
    }

    private static PdfTimestampInfo Describe(TimestampToken token, bool imprintMatches)
    {
        var info = new PdfTimestampInfo
        {
            Time = token.GenTime,
            Certificate = token.Cms.Signer,
            AuthorityName = token.Cms.Signer?.GetNameInfo(X509NameType.SimpleName, false),
            PolicyOid = token.Policy,
            SerialNumber = Compat.ToHex(token.SerialNumber),
            ImprintMatches = imprintMatches,
            SignatureValid = token.Verify(),
        };
        if (!info.ImprintMatches) info.Problem = "The timestamp does not cover what it is attached to.";
        else if (!info.SignatureValid) info.Problem = "The timestamp's signature is invalid" + (token.Cms.Problem is string p ? ": " + p : ".");
        return info;
    }

    /// <summary>Validates the authority's certificates at the timestamp's time, then the signer's at the best time available.</summary>
    private static void Validate(PdfSignatureInfo info, CmsSignedData cms, TimestampToken? token, CertificateValidator validator, PdfValidationOptions options)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PdfCertificateValidation? authority = null;
        if (token is not null && info.Timestamp is { Certificate: X509Certificate2 tsa } stamp)
        {
            (DateTimeOffset time, PdfValidationTimeSource source) = options.ValidationTime is DateTimeOffset v ? (v, PdfValidationTimeSource.Caller)
                : stamp.IsIntact ? (stamp.Time, PdfValidationTimeSource.Timestamp) : (now, PdfValidationTimeSource.Current);

            // A signature timestamp may lean on certificates the signature carries, such as a shared intermediate.
            IEnumerable<X509Certificate2> embedded = info.IsDocumentTimestamp ? token.Cms.Certificates : token.Cms.Certificates.Concat(cms.Certificates);
            authority = validator.Validate(tsa, embedded, CertificateRole.TimestampAuthority, time, source);
            stamp.Validation = authority;
        }

        if (info.IsDocumentTimestamp)
        {
            info.Validation = authority;
            return;
        }

        if (info.Certificate is not X509Certificate2 signer) return;

        // The timestamp proves when the signature existed only when its own authority validated: an
        // authority that failed or could not be checked must not move the signer's check into the past.
        bool timestamped = info.Timestamp is { IsIntact: true } && authority?.Status == PdfCheckStatus.Passed;
        (DateTimeOffset at, PdfValidationTimeSource from) = options.ValidationTime is DateTimeOffset given ? (given, PdfValidationTimeSource.Caller)
            : timestamped ? (info.Timestamp!.Time, PdfValidationTimeSource.Timestamp) : (now, PdfValidationTimeSource.Current);
        info.Validation = validator.Validate(signer, cms.Certificates, CertificateRole.Signer, at, from);
    }

    /// <summary>The bytes of a hex string spanning exactly [start, end), or null when the span is anything else.</summary>
    private static byte[]? GapContents(byte[] pdf, int start, int end)
    {
        if (end - start < 2 || pdf[start] != '<' || pdf[end - 1] != '>') return null;
        var bytes = new List<byte>((end - start) / 2);
        int high = -1;
        for (int i = start + 1; i < end - 1; i++)
        {
            byte c = pdf[i];
            if (c is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f') continue;
            int v = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1,
            };
            if (v < 0) return null;
            if (high < 0)
            {
                high = v;
            }
            else
            {
                bytes.Add((byte)(high << 4 | v));
                high = -1;
            }
        }

        if (high >= 0) bytes.Add((byte)(high << 4));
        return [.. bytes];
    }

    private static string UniqueFieldName(PdfDocument doc, string prefix)
    {
        var names = new HashSet<string>(doc.Form.Fields.Select(f => f.Name));
        for (int i = 1; ; i++)
        {
            if (!names.Contains(prefix + i)) return prefix + i;
        }
    }

    /// <summary>A signature field merged with its widget, invisible until an appearance is set.</summary>
    private static PdfDictionary Widget(string fieldName, int signatureNumber, PdfPage page) => new()
    {
        ["Type"] = new PdfName("Annot"),
        ["Subtype"] = new PdfName("Widget"),
        ["FT"] = new PdfName("Sig"),
        ["T"] = PdfString.FromText(fieldName),
        ["V"] = new PdfReference(signatureNumber, 0),
        ["F"] = PdfNumber.FromInteger(132), // print, locked
        ["P"] = new PdfReference(page.Number, 0),
        ["Rect"] = PdfArray.Of(0, 0, 0, 0),
    };

    private static void Appear(PdfDocument doc, PdfPage page, PdfDictionary widget, PdfSignatureAppearance look, X509Certificate2 cert, PdfSignatureOptions options, DateTimeOffset when)
    {
        PdfRectangle box = page.MediaBox;
        widget.Set("Rect", PdfArray.Of(box.Left + look.X, box.Top - look.Y - look.Height, box.Left + look.X + look.Width, box.Top - look.Y));
        string text = look.Text ?? "Digitally signed by " + (cert.GetNameInfo(X509NameType.SimpleName, false) ?? "") + "\nDate: "
            + when.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + (options.Reason is string reason ? "\nReason: " + reason : "");
        PdfForm appearance = PdfForm.Create(doc, look.Width, look.Height);
        using (PdfGraphics g = appearance.CreateGraphics())
        {
            g.DrawRectangle(new PdfPen(PdfColor.Gray(0.6), 0.75), PdfColor.Gray(0.97), 0.5, 0.5, look.Width - 1, look.Height - 1);
            double size = Math.Max(5, Math.Min(10, look.Height / (text.Split('\n').Length * 1.4)));
            g.IntersectClip(3, 3, look.Width - 6, look.Height - 6);
            g.DrawText(text, new PdfTextStyle(look.Font ?? PdfStandardFont.Helvetica, size) { Brush = PdfColor.Gray(0.15) }, 5, 4, Math.Max(1, look.Width - 10));
        }

        widget.Set("AP", new PdfDictionary { ["N"] = appearance.Reference });
    }

    private static void WriteText(ByteSink sink, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        PdfSerializer.WriteName(sink, key);
        PdfSerializer.WriteString(sink, PdfString.FromText(value!).Value);
    }

    internal static HashAlgorithmName HashName(PdfDigestAlgorithm digest) => digest switch
    {
        PdfDigestAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        PdfDigestAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    /// <summary>The hash of the file outside the Contents hex string.</summary>
    internal static byte[] Hash(HashAlgorithmName name, byte[] data, int gapStart, int gapEnd, int? end = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data, 0, gapStart);
        hash.AppendData(data, gapEnd, (end ?? data.Length) - gapEnd);
        return hash.GetHashAndReset();
    }
}

/// <summary>
/// An incremental update of a file (ISO 32000-1, 7.5.6): changed and new objects appended after
/// the original bytes, with a cross-reference section in the file's own style, and optionally a
/// signature dictionary whose byte range and contents are filled in last.
/// </summary>
internal sealed class IncrementalUpdate
{
    private const int ByteRangeDigits = 10;
    private readonly byte[] _original;
    private readonly PdfFile _file;
    private readonly int _firstNew;
    private readonly SortedSet<int> _changed = [];

    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    public IncrementalUpdate(byte[] pdf)
    {
        _original = pdf;
        _file = PdfFile.Open(pdf, 256L << 20);
        if (_file.Trailer["Encrypt"] is not null and not PdfNull) throw new NotSupportedException("Signing encrypted documents is not supported.");
        Doc = PdfDocument.Open(pdf);
        int size = _file.Trailer["Size"] is PdfNumber { IsInteger: true } s ? (int)Math.Min(s.IntegerValue, int.MaxValue) : Doc.NextNumber;
        _firstNew = Math.Max(size, Doc.NextNumber);
        Doc.NextNumber = _firstNew;
    }

    public PdfDocument Doc { get; }

    /// <summary>Marks an existing object as changed, so the update writes it again.</summary>
    public void Changed(int number) => _changed.Add(number);

    /// <summary>Lists a signature widget on its page and in the form, and says the form has signatures.</summary>
    public void AddField(PdfPage page, PdfReference widgetRef)
    {
        if (page.Dictionary["Annots"] is PdfReference annotsRef && Doc.Resolve(annotsRef) is PdfArray annots)
        {
            annots.Add(widgetRef);
            Changed(annotsRef.Number);
        }
        else
        {
            var list = Doc.Resolve(page.Dictionary["Annots"]) as PdfArray ?? new PdfArray();
            var copy = new PdfArray();
            foreach (PdfObject o in list) copy.Add(o);
            copy.Add(widgetRef);
            page.Dictionary.Set("Annots", copy);
            Changed(page.Number);
        }

        PdfDictionary form;
        if (Doc.Catalog["AcroForm"] is PdfReference formRef && Doc.Resolve(formRef) is PdfDictionary existingForm)
        {
            form = existingForm;
            Changed(formRef.Number);
        }
        else
        {
            form = Doc.Resolve(Doc.Catalog["AcroForm"]) as PdfDictionary ?? new PdfDictionary();
            Doc.Catalog.Set("AcroForm", form);
            Changed(Doc.CatalogNumber);
        }

        if (form["Fields"] is PdfReference fieldsRef && Doc.Resolve(fieldsRef) is PdfArray fieldsArray)
        {
            fieldsArray.Add(widgetRef);
            Changed(fieldsRef.Number);
        }
        else
        {
            var fields = new PdfArray();
            if (Doc.Resolve(form["Fields"]) is PdfArray old)
            {
                foreach (PdfObject o in old) fields.Add(o);
            }

            fields.Add(widgetRef);
            form.Set("Fields", fields);
        }

        form.Set("SigFlags", PdfNumber.FromInteger(3));
    }

    /// <summary>Writes the update with no signature.</summary>
    public byte[] Write() => WriteUpdate(null).Output;

    /// <summary>
    /// Writes the update with a signature dictionary, fills its byte range, hashes the file
    /// outside the contents, and writes what <paramref name="contents"/> makes of the digest.
    /// </summary>
    /// <exception cref="InvalidOperationException">The contents are larger than the space reserved.</exception>
    public byte[] Seal(Placeholder placeholder, HashAlgorithmName hash, Func<byte[], byte[]> contents)
    {
        (byte[] output, int byteRangeAt, int contentsStart, int contentsEnd) = WriteUpdate(placeholder);

        // The byte range covers everything but the Contents hex string, delimiters included.
        long[] range = [0, contentsStart, contentsEnd, output.Length - contentsEnd];
        byte[] digits = Encoding.ASCII.GetBytes(string.Join(" ", range.Select(r => r.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture))));
        digits.CopyTo(output, byteRangeAt);

        byte[] value = contents(PdfSigner.Hash(hash, output, contentsStart, contentsEnd));
        if (2 * value.Length > contentsEnd - contentsStart - 2) throw new InvalidOperationException("The signature is larger than the space reserved for it.");
        Encoding.ASCII.GetBytes(Compat.ToHex(value)).CopyTo(output, contentsStart + 1);
        return output;
    }

    /// <summary>Appends the objects, the signature dictionary with placeholders, and a cross-reference section matching the file's style.</summary>
    private (byte[] Output, int ByteRangeAt, int ContentsStart, int ContentsEnd) WriteUpdate(Placeholder? placeholder)
    {
        Doc.FinishFonts();

        // Everything new is written, plus the existing objects that changed.
        var objects = new SortedDictionary<int, PdfObject>();
        foreach (int n in _changed) objects[n] = Doc.Objects[n];
        foreach (KeyValuePair<int, PdfObject> o in Doc.Objects)
        {
            if (o.Key >= _firstNew) objects[o.Key] = o.Value;
        }

        var sink = new ByteSink(_original.Length + (placeholder?.Reserve ?? 0) * 2 + 16384);
        sink.Write(_original);
        if (sink.LastByte is not ((byte)'\n' or (byte)'\r')) sink.Write((byte)'\n');
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();

        foreach ((int number, PdfObject obj) in objects)
        {
            int generation = _file.Exists(number) ? _file.GenerationOf(number) : 0;
            offsets[number] = (sink.Length, generation);
            sink.WriteAscii($"{number} {generation} obj\n");
            if (obj is PdfStream stream)
            {
                PdfDictionary d = PdfDocument.Clone(stream.Dictionary);
                d.Set("Length", PdfNumber.FromInteger(stream.Data.Length));
                serializer.WriteDictionary(sink, d, skipKey: null);
                sink.Write("\nstream\n"u8);
                sink.Write(stream.Data.Span);
                sink.Write("\nendstream"u8);
            }
            else
            {
                serializer.Write(sink, obj);
            }

            sink.Write("\nendobj\n"u8);
        }

        int byteRangeAt = 0, contentsStart = 0, contentsEnd = 0;
        if (placeholder is Placeholder p)
        {
            offsets[p.Number] = (sink.Length, 0);
            sink.WriteAscii($"{p.Number} 0 obj\n<<{p.Head}/ByteRange[");
            byteRangeAt = sink.Length;
            sink.WriteAscii(string.Join(" ", Enumerable.Repeat(new string('0', ByteRangeDigits), 4)));
            sink.WriteAscii("]/Contents");
            contentsStart = sink.Length;
            sink.Write((byte)'<');
            for (int i = 0; i < 2 * p.Reserve; i++) sink.Write((byte)'0');
            sink.Write((byte)'>');
            contentsEnd = sink.Length;
            p.Tail?.Invoke(sink);
            sink.Write(">>\nendobj\n"u8);
        }

        int maxNumber = offsets.Keys.Max();
        PdfDictionary trailer = new()
        {
            ["Size"] = PdfNumber.FromInteger(Math.Max(maxNumber + (_file.UsesXrefStreams ? 2 : 1), _file.Trailer["Size"] is PdfNumber { IsInteger: true } sz ? sz.IntegerValue : 0)),
            ["Root"] = _file.Trailer["Root"]!,
            ["Prev"] = PdfNumber.FromInteger(LastStartXref(_original)),
        };
        if (_file.Trailer["Info"] is PdfObject info) trailer.Set("Info", info);
        if (_file.Resolve(_file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);

        long xref = sink.Length;
        if (_file.UsesXrefStreams)
        {
            int xrefNumber = maxNumber + 1;
            offsets[xrefNumber] = (xref, 0);
            var rows = new List<byte>();
            var index = new PdfArray();
            foreach ((int start, int count) in Runs(offsets.Keys))
            {
                index.Add(PdfNumber.FromInteger(start));
                index.Add(PdfNumber.FromInteger(count));
                for (int n = start; n < start + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    rows.Add(1);
                    for (int k = 3; k >= 0; k--) rows.Add((byte)(offset >> (8 * k)));
                    rows.Add((byte)(generation >> 8));
                    rows.Add((byte)generation);
                }
            }

            trailer.Set("Type", new PdfName("XRef"));
            trailer.Set("Size", PdfNumber.FromInteger(Math.Max(xrefNumber + 1, ((PdfNumber)trailer["Size"]!).IntegerValue)));
            trailer.Set("Index", index);
            trailer.Set("W", PdfArray.Of(1, 4, 2));
            trailer.Set("Length", PdfNumber.FromInteger(rows.Count));
            sink.WriteAscii($"{xrefNumber} 0 obj\n");
            serializer.WriteDictionary(sink, trailer, skipKey: null);
            sink.Write("\nstream\n"u8);
            sink.Write([.. rows]);
            sink.Write("\nendstream\nendobj\n"u8);
        }
        else
        {
            sink.Write("xref\n"u8);
            foreach ((int start, int count) in Runs(offsets.Keys))
            {
                sink.WriteAscii($"{start} {count}\n");
                for (int n = start; n < start + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    sink.WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " " + generation.ToString("D5", CultureInfo.InvariantCulture) + " n\r\n");
                }
            }

            sink.Write("trailer\n"u8);
            serializer.WriteDictionary(sink, trailer, skipKey: null);
            sink.Write((byte)'\n');
        }

        sink.WriteAscii($"startxref\n{xref}\n%%EOF\n");
        return (sink.ToArray(), byteRangeAt, contentsStart, contentsEnd);
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> numbers)
    {
        int start = -1, previous = -2;
        foreach (int n in numbers)
        {
            if (n != previous + 1)
            {
                if (start >= 0) yield return (start, previous - start + 1);
                start = n;
            }

            previous = n;
        }

        if (start >= 0) yield return (start, previous - start + 1);
    }

    private static long LastStartXref(byte[] pdf)
    {
        int at = pdf.AsSpan().LastIndexOf("startxref"u8);
        if (at < 0) throw new PdfFormatException("startxref not found.");
        int i = at + 9;
        while (i < pdf.Length && pdf[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') i++;
        long value = 0;
        while (i < pdf.Length && pdf[i] is >= (byte)'0' and <= (byte)'9') value = value * 10 + (pdf[i++] - '0');
        return value;
    }

    /// <summary>A signature or timestamp dictionary written by hand: its number, its entries before ByteRange, the bytes reserved for Contents, and entries after Contents.</summary>
    public readonly record struct Placeholder(int Number, string Head, int Reserve, Action<ByteSink>? Tail);
}
