using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>What a certificate at the start of a chain is used for.</summary>
internal enum CertificateRole
{
    Signer,
    TimestampAuthority,
}

/// <summary>
/// Builds certificate chains to caller supplied trust anchors and checks validity, usage and
/// revocation at one time (RFC 5280, 6.1, in the parts PAdES validation needs).
/// </summary>
internal sealed class CertificateValidator
{
    private const int MaxChainLength = 16;
    private readonly PdfValidationOptions _options;
    private readonly SecurityStore _store;
    private readonly Dictionary<string, PdfRevocationValues> _supplied = new();
    private readonly SignatureChecks _checks = new();

    public CertificateValidator(PdfValidationOptions options, SecurityStore store)
    {
        _options = options;
        _store = store;
    }

    /// <summary>Builds a chain upward from a certificate through the pool, stopping at a certificate <paramref name="isAnchor"/> accepts.</summary>
    /// <exception cref="SignatureChecks.ExhaustedException">The operation used up its signature checks.</exception>
    public static List<X509Certificate2> BuildChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> pool, Func<X509Certificate2, bool> isAnchor, IReadOnlyList<X509Certificate2> anchors, SignatureChecks checks)
    {
        var path = new List<X509Certificate2> { leaf };
        List<X509Certificate2> candidates = [.. anchors, .. pool];
        while (!isAnchor(path[path.Count - 1]) && path.Count < MaxChainLength)
        {
            X509Certificate2 current = path[path.Count - 1];
            X509Certificate2? issuer = candidates.FirstOrDefault(c => !path.Any(p => Same(p, c)) && checks.IsIssuedBy(current, c));
            if (issuer is null) break;
            path.Add(issuer);
        }

        return path;
    }

    public static bool Same(X509Certificate2 a, X509Certificate2 b) => a.RawData.AsSpan().SequenceEqual(b.RawData);

    public PdfCertificateValidation Validate(X509Certificate2 leaf, IEnumerable<X509Certificate2> embedded, CertificateRole role, DateTimeOffset time, PdfValidationTimeSource source)
    {
        try
        {
            return Check(leaf, embedded, role, time, source);
        }
        catch (SignatureChecks.ExhaustedException e)
        {
            return new PdfCertificateValidation
            {
                ValidationTime = time,
                TimeSource = source,
                Chain = PdfCheckStatus.Indeterminate,
                Certificates = [new PdfCertificateCheck(leaf) { Validity = PdfCheckStatus.NotChecked, KeyUsage = PdfCheckStatus.NotChecked, Revocation = PdfCheckStatus.NotChecked, Problem = e.Message }],
                Problems = [e.Message],
                Status = PdfCheckStatus.Indeterminate,
            };
        }
    }

    private PdfCertificateValidation Check(X509Certificate2 leaf, IEnumerable<X509Certificate2> embedded, CertificateRole role, DateTimeOffset time, PdfValidationTimeSource source)
    {
        IReadOnlyList<X509Certificate2> anchors = _options.TrustAnchors;
        List<X509Certificate2> pool = [.. embedded, .. _options.Certificates, .. _store.Certificates];
        bool IsAnchor(X509Certificate2 c) => anchors.Any(a => Same(a, c));
        List<X509Certificate2> path = BuildChain(leaf, pool, IsAnchor, anchors, _checks);
        bool anchored = IsAnchor(path[path.Count - 1]);

        var result = new PdfCertificateValidation { ValidationTime = time, TimeSource = source };
        var problems = new List<string>();
        if (anchors.Count == 0)
        {
            result.Chain = PdfCheckStatus.NotChecked;
            problems.Add("No trust anchors were supplied, so the chain was not checked.");
        }
        else if (anchored)
        {
            result.Chain = PdfCheckStatus.Passed;
        }
        else
        {
            result.Chain = PdfCheckStatus.Failed;
            problems.Add("No chain leads from " + Name(path[path.Count - 1]) + " to a trust anchor.");
        }

        var checks = new List<PdfCertificateCheck>();
        for (int i = 0; i < path.Count; i++)
        {
            X509Certificate2 cert = path[i];
            var check = new PdfCertificateCheck(cert);
            checks.Add(check);
            bool anchor = anchored && i == path.Count - 1;
            check.IsTrustAnchor = anchor;
            var why = new List<string>();

            // A trust anchor is taken as given, unless it is the signer's own certificate.
            if (anchor && i > 0)
            {
                continue;
            }

            check.Validity = time >= Pkix.NotBefore(cert) && time <= Pkix.NotAfter(cert) ? PdfCheckStatus.Passed : PdfCheckStatus.Failed;
            if (check.Validity == PdfCheckStatus.Failed) why.Add("it is not valid at " + time.ToString("u", System.Globalization.CultureInfo.InvariantCulture));

            check.KeyUsage = CheckUsage(cert, i == 0 ? role : null, path.Skip(1).Take(i - 1).Count(c => !Pkix.IsSelfIssued(c)), why);

            if (anchor || !_options.CheckRevocation)
            {
                check.Revocation = PdfCheckStatus.NotChecked;
            }
            else if (i + 1 < path.Count)
            {
                CheckRevocation(check, path[i + 1], time, pool, why);
            }
            else
            {
                check.Revocation = PdfCheckStatus.Indeterminate;
                why.Add("its issuer is unknown, so revocation cannot be checked");
            }

            if (why.Count > 0)
            {
                check.Problem = string.Join("; ", why);
                problems.Add(Name(cert) + ": " + check.Problem + ".");
            }
        }

        result.Certificates = checks;
        result.Problems = problems;
        var statuses = new List<PdfCheckStatus> { result.Chain };
        foreach (PdfCertificateCheck c in checks)
        {
            if (c.IsTrustAnchor && c != checks[0]) continue;
            statuses.Add(c.Validity);
            statuses.Add(c.KeyUsage);
            if (_options.CheckRevocation && !c.IsTrustAnchor) statuses.Add(c.Revocation);
        }

        result.Status = statuses.Contains(PdfCheckStatus.Failed) ? PdfCheckStatus.Failed
            : statuses.Any(s => s is PdfCheckStatus.Indeterminate or PdfCheckStatus.NotChecked) ? PdfCheckStatus.Indeterminate
            : PdfCheckStatus.Passed;
        return result;
    }

    /// <param name="role">The role of the first certificate, or null for a CA.</param>
    /// <param name="intermediatesBelow">How many non self-issued CA certificates sit between this one and the end of the chain.</param>
    private static PdfCheckStatus CheckUsage(X509Certificate2 cert, CertificateRole? role, int intermediatesBelow, List<string> why)
    {
        try
        {
            if (Pkix.UnknownCriticalExtension(cert) is string oid)
            {
                why.Add("it has a critical extension the library does not process (" + oid + ")");
                return PdfCheckStatus.Failed;
            }

            X509KeyUsageFlags? usage = Pkix.KeyUsage(cert);
            if (role is null)
            {
                if (Pkix.BasicConstraints(cert) is not (true, var limit))
                {
                    why.Add("it issues certificates but is not marked as a CA");
                    return PdfCheckStatus.Failed;
                }

                if (usage is X509KeyUsageFlags u && (u & X509KeyUsageFlags.KeyCertSign) == 0)
                {
                    why.Add("its key usage does not allow signing certificates");
                    return PdfCheckStatus.Failed;
                }

                if (limit is int max && intermediatesBelow > max)
                {
                    why.Add("its path length limit is exceeded");
                    return PdfCheckStatus.Failed;
                }

                return PdfCheckStatus.Passed;
            }

            if (usage is X509KeyUsageFlags k && (k & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            {
                why.Add("its key usage does not allow signatures");
                return PdfCheckStatus.Failed;
            }

            if (role == CertificateRole.TimestampAuthority && !TimestampToken.IsTimeStampingCertificate(cert, out string? problem))
            {
                why.Add(problem!.TrimEnd('.'));
                return PdfCheckStatus.Failed;
            }

            return PdfCheckStatus.Passed;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            why.Add("its extensions cannot be read");
            return PdfCheckStatus.Failed;
        }
    }

    private void CheckRevocation(PdfCertificateCheck check, X509Certificate2 issuer, DateTimeOffset time, List<X509Certificate2> pool, List<string> why)
    {
        if (Pkix.HasExtension(check.Certificate, Pkix.OcspNoCheck))
        {
            check.Revocation = PdfCheckStatus.Passed;
            check.RevocationSource = "exempt (OCSP no check)";
            return;
        }

        var ocsps = new List<(Ocsp Response, string Source)>();
        var crls = new List<(Crl List, string Source)>();
        if (_options.UseDocumentSecurityStore)
        {
            ocsps.AddRange(_store.Ocsps.Select(o => (o, "document security store")));
            crls.AddRange(_store.Crls.Select(c => (c, "document security store")));
        }

        if (_options.RevocationSource is IPdfRevocationSource supplied)
        {
            PdfRevocationValues values = Supplied(supplied, check.Certificate, issuer);
            ocsps.AddRange(values.OcspResponses.Select(Ocsp.TryParse).Where(o => o is not null).Select(o => (o!, "supplied")));
            crls.AddRange(values.Crls.Select(Crl.TryParse).Where(c => c is not null).Select(c => (c!, "supplied")));
        }

        bool unverified = false, stale = false, unknown = false, partial = false;
        string? good = null;
        DateTimeOffset? revokedLater = null;
        foreach ((Ocsp response, string source) in ocsps)
        {
            Ocsp.Entry? entry = response.Find(check.Certificate, issuer);
            if (entry is null) continue;
            if (response.Signer(issuer, pool, _checks) is null)
            {
                unverified = true;
                continue;
            }

            if (entry.Status == 1)
            {
                if (entry.RevocationTime is DateTimeOffset at && at <= time)
                {
                    Revoked(at, "OCSP (" + source + ")");
                    return;
                }

                // Revoked only after the validation time: not revoked at it.
                revokedLater = entry.RevocationTime;
                good ??= "OCSP (" + source + ")";
            }
            else if (entry.Status == 2)
            {
                unknown = true;
            }
            else if (Current(entry.ThisUpdate, entry.NextUpdate, time))
            {
                good ??= "OCSP (" + source + ")";
            }
            else
            {
                stale = true;
            }
        }

        foreach ((Crl crl, string source) in crls)
        {
            if (!crl.IsFrom(check.Certificate)) continue;
            if (crl.CriticalExtension is not null)
            {
                partial = true;
                continue;
            }

            if (!crl.IsSignedBy(issuer, _checks))
            {
                unverified = true;
                continue;
            }

            DateTimeOffset? at;
            try
            {
                at = crl.RevokedAt(check.Certificate);
            }
            catch (FormatException)
            {
                unverified = true;
                continue;
            }

            if (at is DateTimeOffset when && when <= time)
            {
                Revoked(when, "CRL (" + source + ")");
                return;
            }

            if (at is not null) revokedLater = at;
            if (Current(crl.ThisUpdate, crl.NextUpdate, time)) good ??= "CRL (" + source + ")";
            else stale = true;
        }

        check.RevocationTime = revokedLater;
        if (good is not null)
        {
            check.Revocation = PdfCheckStatus.Passed;
            check.RevocationSource = good;
            return;
        }

        if (!unverified && !stale && !unknown && !partial)
        {
            check.Revocation = PdfCheckStatus.NotChecked;
            why.Add("no revocation data was found");
            return;
        }

        check.Revocation = PdfCheckStatus.Indeterminate;
        why.Add(unknown ? "the OCSP responder does not know the certificate"
            : stale ? "the revocation data predates the validation time"
            : partial ? "the CRL is a delta or partitioned list (a critical extension), which the library does not process"
            : "the revocation data is not signed by the issuer or its responder");

        void Revoked(DateTimeOffset at, string source)
        {
            check.Revocation = PdfCheckStatus.Failed;
            check.RevocationTime = at;
            check.RevocationSource = source;
            why.Add("it was revoked at " + at.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Revocation data proves the status at a time when it was issued after it, or the time falls within its validity.</summary>
    private static bool Current(DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate, DateTimeOffset time) =>
        thisUpdate >= time || (nextUpdate is DateTimeOffset next && time <= next);

    private PdfRevocationValues Supplied(IPdfRevocationSource source, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        string key = Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA256, certificate.RawData));
        if (!_supplied.TryGetValue(key, out PdfRevocationValues? values))
        {
            values = source.GetRevocationValues(new PdfRevocationRequest(certificate, issuer)) ?? PdfRevocationValues.None;
            _supplied[key] = values;
        }

        return values;
    }

    private static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : c.Subject;
}
