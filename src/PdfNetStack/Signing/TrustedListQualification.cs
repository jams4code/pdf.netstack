using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// The qualification of a signature confirmed against the trusted lists in
/// <see cref="PdfValidationOptions.TrustedLists"/>, as ETSI TS 119 615 describes: the CA/QC
/// service that issued the certificate, its status at the certificate's issuance and at the
/// signing time, the qualifiers that apply, and the certificate's QCStatements.
/// </summary>
public sealed class PdfTrustedListQualification
{
    internal PdfTrustedListQualification()
    {
    }

    /// <summary>
    /// The qualification at the signing time: what the lists confirm, with the certificate's
    /// QCStatements where the lists leave a point to them. AdES when the certificate is not an EU
    /// qualified certificate at its issuance or at the signing time; Indeterminate when the lists
    /// do not decide. Always Indeterminate when the certificate checks
    /// (<see cref="PdfCertificateValidation.Status"/>) did not pass: the lists are then not consulted.
    /// </summary>
    public PdfQualification Qualification { get; internal set; }

    /// <summary>The qualification at the certificate's issuance (its notBefore), by the same rules.</summary>
    public PdfQualification AtIssuance { get; internal set; }

    /// <summary>The CA/QC service that decided, or null when none did.</summary>
    public PdfTrustService? Service { get; internal set; }

    /// <summary>How the decision was reached, in plain words, including lists that could not be used.</summary>
    public IReadOnlyList<string> Reasons { get; internal set; } = [];
}

/// <summary>
/// Qualification against trusted lists (ETSI TS 119 615, 4.3 and 4.4, as ETSI TS 119 172-4 uses
/// it for signatures). Only lists whose signature passed and that have not expired are used.
/// </summary>
/// <remarks>
/// A service is found by its digital identity, compared with the public key of a CA certificate
/// of the chain: a listed certificate with the same subjectPublicKey, or a listed subject key
/// identifier equal to the SHA-1 of that subjectPublicKey (RFC 5280, 4.2.1.2, method 1). The
/// subject key identifier extension a certificate carries is never used, since whoever makes the
/// certificate chooses it. Every entry of a service, its history included, names the same
/// service, so a CA that an older entry names by key identifier is still found. At a time, the
/// entry in force is the one with the latest starting time not after it.
/// <para>
/// Trust anchors: a certificate of a service counts as an anchor at a validation time only when
/// the entry in force then is a CA/QC or CA/PKC service (also TSA/QTST or TSA for a time stamp
/// authority's chain) with an approved status (granted, recognised at national level, or before
/// eIDAS under supervision, in cessation or accredited) and no critical extension the library
/// does not interpret. OCSP, CRL and other services, and withdrawn, deprecated, ceased or revoked
/// ones, never anchor a chain.
/// </para>
/// <para>
/// Qualification is only decided for a certificate whose checks passed. The entry in force
/// decides when it is a CA/QC entry with status granted (or, before
/// eIDAS, under supervision, in cessation or accredited) and no critical extension the library
/// does not interpret. Its qualifiers apply when their criteria select the certificate: NotQualified
/// and QCStatement settle whether it is qualified (otherwise QcCompliance does), QCWithSSCD,
/// QCWithQSCD and QCQSCDManagedOnBehalf or QCNoSSCD and QCNoQSCD settle the device (otherwise
/// QcSSCD does), QCForESig, QCForESeal and QCForWSA settle the type (otherwise QcType does, and a
/// certificate without one from before eIDAS marked QCForLegalPerson is for seals). A service whose
/// additional service information names the types it is qualified for must name the certificate's.
/// The signature is qualified when the certificate is qualified at its issuance and at the signing
/// time; type and device are those at the signing time. Services that decide must agree;
/// conflicting qualifiers, conflicting services and criteria that cannot be evaluated give
/// Indeterminate. Without a deciding service the certificate is not qualified (AdES).
/// </para>
/// <para>
/// A time stamp authority is qualified when its certificate checks passed, the token is intact,
/// and a TSA/QTST service whose identity is the authority's own key, or the key of a CA of its
/// chain that the list names as that TSA/QTST service, was granted at the validation time. A CA
/// listed only as a CA service does not make a time stamp authority qualified.
/// </para>
/// </remarks>
internal sealed class TrustedListQualifier
{
    private const string Qualifier = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/";
    private static readonly DateTimeOffset Eidas = new(2016, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _unusable = [];
    private readonly bool _anyUsable;
    private readonly Dictionary<string, List<PdfTrustService>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PdfTrustService>> _byKeyId = new(StringComparer.Ordinal);
    private readonly List<(X509Certificate2 Certificate, PdfTrustService Service)> _certificates = [];

    public TrustedListQualifier(IReadOnlyList<PdfTrustedList> lists, DateTimeOffset now)
    {
        foreach (PdfTrustedList list in lists)
        {
            if (list.Unusable(now) is string why)
            {
                _unusable.Add("The " + list.Describe() + " is not used: " + why + ".");
                continue;
            }

            _anyUsable = true;
            foreach (PdfTrustService service in list.Services)
            {
                foreach (PdfTrustServiceInformation entry in service.History.Prepend(service))
                {
                    foreach (X509Certificate2 c in entry.Certificates)
                    {
                        _certificates.Add((c, service));
                        if (Key(c) is string key) Add(_byKey, key, service);
                    }

                    foreach (byte[] ski in entry.SubjectKeyIdentifiers) Add(_byKeyId, Compat.ToHex(ski), service);
                }
            }
        }
    }

    /// <summary>Whether any list can be used: its signature passed and it has not expired.</summary>
    public bool AnyUsable => _anyUsable;

    /// <summary>
    /// The certificates of the usable lists that anchor a chain for <paramref name="role"/> at
    /// <paramref name="time"/>: those of services whose entry in force then may anchor it.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Anchors(CertificateRole role, DateTimeOffset time)
    {
        var anchors = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal);
        foreach ((X509Certificate2 c, PdfTrustService service) in _certificates)
        {
            if (service.At(time) is PdfTrustServiceInformation entry && MayAnchor(entry, role)) anchors[Compat.ToHex(c.RawData)] = c;
        }

        return [.. anchors.Values];
    }

    /// <summary>Why a certificate the lists name is not a trust anchor at a time, or null when they do not name it.</summary>
    public string? NotAnAnchor(X509Certificate2 certificate, CertificateRole role, DateTimeOffset time)
    {
        List<PdfTrustService> services = [.. Find(certificate).Distinct()];
        if (services.Count == 0) return null;
        IEnumerable<string> states = services.Select(s => "\"" + s.Name + "\" (" + (s.At(time) is PdfTrustServiceInformation e ? e.TypeIdentifier + ", " + e.StatusUri : "no status") + ")");
        return "The trusted lists name it as " + string.Join(", ", states) + " at " + Format(time) + ", which does not make it a trust anchor for "
            + (role == CertificateRole.Signer ? "a signer" : "a time stamp authority") + ".";
    }

    /// <summary>The qualification when the certificate checks did not pass: Indeterminate, without consulting the lists.</summary>
    public PdfTrustedListQualification NotConfirmed(PdfCheckStatus status)
    {
        var reasons = new List<string>(_unusable)
        {
            "The certificate checks did not pass (" + status + "), so qualification is not confirmed.",
        };
        return new PdfTrustedListQualification { Qualification = PdfQualification.Indeterminate, AtIssuance = PdfQualification.Indeterminate, Reasons = reasons };
    }

    /// <summary>The qualification of a signature made with the first certificate of <paramref name="path"/> at <paramref name="time"/>.</summary>
    public PdfTrustedListQualification Signer(IReadOnlyList<X509Certificate2> path, PdfQcStatements? statements, DateTimeOffset time)
    {
        var reasons = new List<string>(_unusable);
        var result = new PdfTrustedListQualification { Reasons = reasons };
        if (!_anyUsable)
        {
            reasons.Add("No usable trusted list was supplied, so qualification cannot be confirmed.");
            return Done(PdfQualification.Indeterminate, PdfQualification.Indeterminate);
        }

        X509Certificate2 leaf = path[0];
        if (path.Count == 1)
        {
            if (Pkix.IsSelfIssued(leaf))
            {
                reasons.Add("The certificate is self-issued; no trust service issued it.");
                return Done(PdfQualification.AdES, PdfQualification.AdES);
            }

            reasons.Add("The issuer's certificate was not found, so its trust service cannot be looked up.");
            return Done(PdfQualification.Indeterminate, PdfQualification.Indeterminate);
        }

        List<PdfTrustService> services = [.. path.Skip(1).SelectMany(Find).Distinct().Where(s => s.History.Prepend(s).Any(e => e.TypeIdentifier == PdfTrustServiceTypes.CaQc))];
        if (services.Count == 0)
        {
            reasons.Add("No CA/QC service of the trusted lists issued the certificate or a CA above it, so it is not an EU qualified certificate.");
            return Done(PdfQualification.AdES, PdfQualification.AdES);
        }

        DateTimeOffset issued = Pkix.NotBefore(leaf);
        var atIssuance = new List<(PdfTrustService Service, Outcome Outcome)>();
        var atSigning = new List<(PdfTrustService Service, Outcome Outcome)>();
        foreach (PdfTrustService service in services)
        {
            reasons.Add("Found the CA/QC service \"" + service.Name + "\" of \"" + service.Provider.Name + "\" in the " + service.Provider.TrustedList.Describe() + ".");
            Outcome issue = Evaluate(service, leaf, statements, issued, "at issuance", reasons);
            Outcome signing = Evaluate(service, leaf, statements, time, "at the signing time", reasons);
            if (issue.Decides) atIssuance.Add((service, issue));
            if (issue.Decides && signing.Decides) atSigning.Add((service, signing));
        }

        PdfQualification initial = Combine(atIssuance, "at issuance", reasons, out _);
        PdfQualification final = Combine(atSigning, "at the signing time", reasons, out PdfTrustService? decided);
        result.Service = decided ?? atIssuance.FirstOrDefault().Service;
        if (atIssuance.Count == 0) reasons.Add("No service was granted at the certificate's issuance, so it is not an EU qualified certificate.");
        else if (atSigning.Count == 0) reasons.Add("No service was granted at both the issuance and the signing time, so the signature is not qualified.");

        PdfQualification overall = initial == PdfQualification.Indeterminate || final == PdfQualification.Indeterminate ? PdfQualification.Indeterminate
            : initial == PdfQualification.AdES ? PdfQualification.AdES
            : final;
        return Done(overall, initial);

        PdfTrustedListQualification Done(PdfQualification qualification, PdfQualification issuance)
        {
            result.Qualification = qualification;
            result.AtIssuance = issuance;
            return result;
        }
    }

    /// <summary>
    /// Whether a time stamp authority is a qualified time stamp service at the validation time of
    /// its certificate checks: only when those passed and the token is intact, a TSA/QTST service,
    /// granted then, whose identity is the authority's key or the key of a CA of its chain that
    /// the list names as that TSA/QTST service.
    /// </summary>
    public (PdfCheckStatus Status, PdfTrustService? Service, IReadOnlyList<string> Reasons) Timestamp(PdfCertificateValidation authority, bool intact)
    {
        var reasons = new List<string>(_unusable);
        if (!intact)
        {
            reasons.Add("The timestamp does not verify, so its authority is not confirmed as qualified.");
            return (PdfCheckStatus.Indeterminate, null, reasons);
        }

        if (authority.Status != PdfCheckStatus.Passed)
        {
            reasons.Add("The time stamp authority's certificate checks did not pass (" + authority.Status + "), so it is not confirmed as qualified.");
            return (PdfCheckStatus.Indeterminate, null, reasons);
        }

        if (!_anyUsable)
        {
            reasons.Add("No usable trusted list was supplied, so the time stamp authority cannot be confirmed as qualified.");
            return (PdfCheckStatus.Indeterminate, null, reasons);
        }

        // The authority's own key first; a CA above it counts only as the identity of a TSA/QTST service.
        DateTimeOffset time = authority.ValidationTime;
        List<PdfTrustService> services = [.. authority.Certificates.Select(c => c.Certificate).SelectMany(Find).Distinct()
            .Where(s => s.History.Prepend(s).Any(e => e.TypeIdentifier == PdfTrustServiceTypes.TsaQtst))];
        if (services.Count == 0)
        {
            reasons.Add("No TSA/QTST service of the trusted lists is the time stamp authority.");
            return (PdfCheckStatus.Failed, null, reasons);
        }

        foreach (PdfTrustService service in services)
        {
            PdfTrustServiceInformation? entry = service.At(time);
            string name = "The TSA/QTST service \"" + service.Name + "\" of \"" + service.Provider.Name + "\" (" + service.Provider.TrustedList.Describe() + ")";
            if (entry is null)
            {
                reasons.Add(name + " had no status at " + Format(time) + ".");
            }
            else if (entry.TypeIdentifier != PdfTrustServiceTypes.TsaQtst || !entry.IsQualifiedStatus)
            {
                reasons.Add(name + " was not a granted TSA/QTST service at " + Format(time) + " (" + entry.StatusUri + ").");
            }
            else if (entry.UnknownCriticalExtensions.Count > 0)
            {
                reasons.Add(name + " has critical extensions the library does not interpret: " + string.Join(", ", entry.UnknownCriticalExtensions) + ".");
                return (PdfCheckStatus.Indeterminate, service, reasons);
            }
            else
            {
                reasons.Add(name + " was granted at " + Format(time) + ".");
                return (PdfCheckStatus.Passed, service, reasons);
            }
        }

        return (PdfCheckStatus.Failed, null, reasons);
    }

    /// <summary>What one service says at one time: whether it decides (granted CA/QC), and the qualification it gives.</summary>
    private readonly record struct Outcome(bool Decides, PdfQualification Qualification);

    private static Outcome Evaluate(PdfTrustService service, X509Certificate2 leaf, PdfQcStatements? statements, DateTimeOffset time, string when, List<string> reasons)
    {
        string at = when + " (" + Format(time) + ")";
        PdfTrustServiceInformation? entry = service.At(time);
        if (entry is null)
        {
            reasons.Add("The service had no status " + at + ".");
            return new Outcome(false, PdfQualification.AdES);
        }

        if (entry.TypeIdentifier != PdfTrustServiceTypes.CaQc || !entry.IsQualifiedStatus)
        {
            reasons.Add("The service was not a granted CA/QC service " + at + ": " + entry.TypeIdentifier + ", " + entry.StatusUri + ".");
            return new Outcome(false, PdfQualification.AdES);
        }

        if (entry.UnknownCriticalExtensions.Count > 0)
        {
            reasons.Add("The service " + at + " has critical extensions the library does not interpret: " + string.Join(", ", entry.UnknownCriticalExtensions) + ".");
            return new Outcome(true, PdfQualification.Indeterminate);
        }

        var qualifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfServiceQualification q in entry.Qualifications)
        {
            bool? applies = q.Criteria.Evaluate(leaf);
            if (applies is null)
            {
                reasons.Add("The service " + at + " has qualification criteria the library cannot evaluate for the certificate.");
                return new Outcome(true, PdfQualification.Indeterminate);
            }

            if (applies == true)
            {
                foreach (string u in q.Qualifiers)
                {
                    if (u.StartsWith(Qualifier, StringComparison.Ordinal)) qualifiers.Add(u.Substring(Qualifier.Length));
                }
            }
        }

        string applied = qualifiers.Count == 0 ? "no qualifiers apply" : "qualifiers " + string.Join(", ", qualifiers.OrderBy(q => q, StringComparer.Ordinal)) + " apply";
        bool notQualified = qualifiers.Contains("NotQualified"), statement = qualifiers.Contains("QCStatement");
        if (notQualified && statement) return Conflict("NotQualified and QCStatement");

        bool qualified;
        if (notQualified)
        {
            qualified = false;
        }
        else if (statement)
        {
            qualified = true;
        }
        else
        {
            qualified = statements is { IsQualified: true, Legislations.Count: 0 };
        }

        if (!qualified)
        {
            reasons.Add("The service was granted " + at + " and " + applied + "; the certificate is not qualified" + (notQualified ? " (NotQualified)." : statements is { IsQualified: true } ? " under EU law (QcCClegislation)." : " (no QcCompliance)."));
            return new Outcome(true, PdfQualification.AdES);
        }

        bool with = qualifiers.Contains("QCWithSSCD") || qualifiers.Contains("QCWithQSCD") || qualifiers.Contains("QCQSCDManagedOnBehalf");
        bool without = qualifiers.Contains("QCNoSSCD") || qualifiers.Contains("QCNoQSCD");
        if (with && without) return Conflict("a qualified device and no qualified device");
        bool device = with || (!without && statements?.KeyInQualifiedDevice == true);

        var types = new List<PdfQcType>();
        if (qualifiers.Contains("QCForESig")) types.Add(PdfQcType.ElectronicSignature);
        if (qualifiers.Contains("QCForESeal")) types.Add(PdfQcType.ElectronicSeal);
        if (qualifiers.Contains("QCForWSA")) types.Add(PdfQcType.WebsiteAuthentication);
        if (types.Count > 1) return Conflict("more than one certificate type");
        if (types.Count == 0)
        {
            if (statements is { Types.Count: > 0 }) types.AddRange(statements.Types);
            else if (qualifiers.Contains("QCForLegalPerson") && Pkix.NotBefore(leaf) < Eidas) types.Add(PdfQcType.ElectronicSeal);
        }

        var allowed = new List<PdfQcType>();
        foreach (string info in entry.AdditionalServiceInformation)
        {
            switch (info)
            {
                case Qualifier + "ForeSignatures": allowed.Add(PdfQcType.ElectronicSignature); break;
                case Qualifier + "ForeSeals": allowed.Add(PdfQcType.ElectronicSeal); break;
                case Qualifier + "ForWebSiteAuthentication": allowed.Add(PdfQcType.WebsiteAuthentication); break;
            }
        }

        IReadOnlyList<PdfQcType> effective = types.Count > 0 ? types : [PdfQcType.ElectronicSignature];
        if (allowed.Count > 0 && !effective.All(allowed.Contains))
        {
            reasons.Add("The service was granted " + at + " but is not qualified to issue certificates of the certificate's type (" + string.Join(", ", effective) + ").");
            return new Outcome(true, PdfQualification.AdES);
        }

        PdfQualification qualification = PdfQcStatements.Classify(types, device);
        reasons.Add("The service was granted " + at + " and " + applied + ": " + qualification + ".");
        return new Outcome(true, qualification);

        Outcome Conflict(string what)
        {
            reasons.Add("The service " + at + " gives conflicting qualifiers (" + what + ").");
            return new Outcome(true, PdfQualification.Indeterminate);
        }
    }

    /// <summary>The services that decide must agree; with none, the certificate is not qualified.</summary>
    private static PdfQualification Combine(List<(PdfTrustService Service, Outcome Outcome)> deciding, string when, List<string> reasons, out PdfTrustService? service)
    {
        service = deciding.Count > 0 ? deciding[0].Service : null;
        if (deciding.Count == 0) return PdfQualification.AdES;
        PdfQualification first = deciding[0].Outcome.Qualification;
        if (deciding.TrueForAll(d => d.Outcome.Qualification == first)) return first;
        reasons.Add("The services that apply " + when + " disagree (" + string.Join(", ", deciding.Select(d => d.Outcome.Qualification)) + ").");
        service = null;
        return PdfQualification.Indeterminate;
    }

    /// <summary>Whether a service entry may anchor a chain for a role: an approved CA service, or for a time stamp authority also a TSA service.</summary>
    private static bool MayAnchor(PdfTrustServiceInformation entry, CertificateRole role) =>
        entry.UnknownCriticalExtensions.Count == 0
        && (entry.IsQualifiedStatus || entry.Status == PdfTrustServiceStatus.RecognisedAtNationalLevel)
        && entry.TypeIdentifier switch
        {
            PdfTrustServiceTypes.CaQc or PdfTrustServiceTypes.CaPkc => true,
            PdfTrustServiceTypes.TsaQtst or PdfTrustServiceTypes.Tsa => role == CertificateRole.TimestampAuthority,
            _ => false,
        };

    /// <summary>
    /// The services whose identity is this certificate's public key: a listed certificate with the
    /// same key, or a listed subject key identifier equal to the SHA-1 of the key. The
    /// certificate's own subject key identifier extension is not trusted.
    /// </summary>
    private IEnumerable<PdfTrustService> Find(X509Certificate2 certificate)
    {
        var found = new List<PdfTrustService>();
        if (Key(certificate) is not string key) return found;
        if (_byKey.TryGetValue(key, out List<PdfTrustService>? byKey)) found.AddRange(byKey);
        if (KeyIdentifier(certificate) is string id && _byKeyId.TryGetValue(id, out List<PdfTrustService>? byId)) found.AddRange(byId);
        return found;
    }

    private static string? Key(X509Certificate2 certificate)
    {
        try
        {
            return Compat.ToHex(Pkix.SubjectPublicKey(certificate));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The SHA-1 of the certificate's subjectPublicKey (RFC 5280, 4.2.1.2, method 1), computed here, never read from its extension.</summary>
    private static string? KeyIdentifier(X509Certificate2 certificate)
    {
        try
        {
            return Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA1, Pkix.SubjectPublicKey(certificate)));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            // A key that cannot be read matches nothing.
            return null;
        }
    }

    private static void Add(Dictionary<string, List<PdfTrustService>> index, string key, PdfTrustService service)
    {
        if (!index.TryGetValue(key, out List<PdfTrustService>? list)) index[key] = list = [];
        if (!list.Contains(service)) list.Add(service);
    }

    private static string Format(DateTimeOffset time) => time.ToString("u", CultureInfo.InvariantCulture);
}
