using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>The outcome of one check.</summary>
public enum PdfCheckStatus
{
    /// <summary>The check was not made, for instance because no trust anchors or no revocation data were supplied.</summary>
    NotChecked,

    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>The check failed.</summary>
    Failed,

    /// <summary>The check was made but the data available cannot decide it, such as revocation data older than the validation time.</summary>
    Indeterminate,
}

/// <summary>Where the time certificates were checked at comes from.</summary>
public enum PdfValidationTimeSource
{
    /// <summary>The current time: the signature has no usable timestamp.</summary>
    Current,

    /// <summary>The time of a valid RFC 3161 timestamp: the signature timestamp, or the document timestamp itself.</summary>
    Timestamp,

    /// <summary>The time given in <see cref="PdfValidationOptions.ValidationTime"/>.</summary>
    Caller,
}

/// <summary>What <see cref="PdfSigner.Verify(byte[], PdfValidationOptions)"/> checks certificates against.</summary>
public sealed record PdfValidationOptions
{
    /// <summary>The certificates trusted as roots of a chain, usually root CAs. With none, chains are built but <see cref="PdfCertificateValidation.Chain"/> is not checked.</summary>
    public IReadOnlyList<X509Certificate2> TrustAnchors { get; init; } = [];

    /// <summary>Extra certificates, such as intermediate CAs, for building chains.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; init; } = [];

    /// <summary>Revocation data beyond what the document's security store (DSS) holds; null for none.</summary>
    public IPdfRevocationSource? RevocationSource { get; init; }

    /// <summary>Use the certificates, OCSP responses and CRLs in the document's security store. Default true.</summary>
    public bool UseDocumentSecurityStore { get; init; } = true;

    /// <summary>Check revocation of every certificate below the trust anchor. Default true.</summary>
    public bool CheckRevocation { get; init; } = true;

    /// <summary>
    /// The time to check certificates at; null to use the time of a valid timestamp, or the
    /// current time when there is none.
    /// </summary>
    public DateTimeOffset? ValidationTime { get; init; }

    /// <summary>
    /// The certificate policies the chain must be valid for, as object identifiers (the
    /// user-initial-policy-set of RFC 5280, 6.1.1). Empty, the default, accepts any policy, and
    /// the chain then passes the policy check unless a certificate's policy constraints require
    /// an explicit policy. With policies given, the chain must be valid for at least one of them
    /// or for anyPolicy.
    /// </summary>
    public IReadOnlyList<string> AcceptablePolicies { get; init; } = [];

    /// <summary>Require the chain to be valid for a policy even when no certificate asks for that (initial-explicit-policy). Default false.</summary>
    public bool RequireExplicitPolicy { get; init; }

    /// <summary>Ignore policy mappings: a mapped policy no longer counts (initial-policy-mapping-inhibit). Default false.</summary>
    public bool InhibitPolicyMapping { get; init; }

    /// <summary>Do not let anyPolicy in a certificate stand for every policy (initial-any-policy-inhibit). Default false.</summary>
    public bool InhibitAnyPolicy { get; init; }

    /// <summary>
    /// Trusted lists (ETSI TS 119 612) to confirm qualified status against, such as the EU list of
    /// trusted lists and the national lists it points to, read with
    /// <see cref="PdfTrustedList.Load(byte[], PdfTrustedList)"/>. Lists whose signature did not
    /// pass, or that have expired, are not used. With lists, every signer gets
    /// <see cref="PdfCertificateValidation.TrustedListQualification"/> and every timestamp
    /// <see cref="PdfTimestampInfo.QualifiedTimestamp"/>. Empty by default.
    /// </summary>
    public IReadOnlyList<PdfTrustedList> TrustedLists { get; init; } = [];

    /// <summary>
    /// Count certificates of the usable <see cref="TrustedLists"/> as trust anchors, beside
    /// <see cref="TrustAnchors"/>, as the EU trust model does. Default true.
    /// </summary>
    /// <remarks>
    /// Only the identities of CA services (CA/QC and CA/PKC) anchor a chain, and only for a
    /// validation time at which the service's entry in force (its current information or a
    /// history entry) has an approved status: granted, recognised at national level, or before
    /// eIDAS under supervision, in cessation or accredited, with no critical extension the
    /// library does not interpret. TSA/QTST and TSA services anchor a time stamp authority's
    /// chain under the same rules, never a signer's. OCSP, CRL and other services, and services
    /// that are withdrawn, deprecated or were never approved, anchor nothing. The default stays
    /// true because a list is only used when its XML signature verified against certificates the
    /// application supplied for it, and because without anchors chains are not checked, so the
    /// trusted list qualification would always be Indeterminate. Set it to false to anchor
    /// chains on <see cref="TrustAnchors"/> alone and use the lists only for qualification.
    /// </remarks>
    public bool TrustedListsAsTrustAnchors { get; init; } = true;
}

/// <summary>The checks made on one certificate of a chain.</summary>
public sealed class PdfCertificateCheck
{
    internal PdfCertificateCheck(X509Certificate2 certificate) => Certificate = certificate;

    /// <summary>The certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>True for the trust anchor that ends the chain. Its validity, usage and revocation are taken as given.</summary>
    public bool IsTrustAnchor { get; internal set; }

    /// <summary>The validation time lies within the certificate's validity period.</summary>
    public PdfCheckStatus Validity { get; internal set; }

    /// <summary>
    /// The certificate may be used for its role: key usage (digital signature or non-repudiation
    /// for a signer, certificate signing and the CA flag with its path length for a CA), the
    /// critical time stamping usage for a time stamp authority, and no critical extension the
    /// library does not understand.
    /// </summary>
    public PdfCheckStatus KeyUsage { get; internal set; }

    /// <summary>Revocation, from a verified OCSP response or CRL that is current at the validation time.</summary>
    public PdfCheckStatus Revocation { get; internal set; }

    /// <summary>Where the revocation answer came from, such as "OCSP (document security store)".</summary>
    public string? RevocationSource { get; internal set; }

    /// <summary>When the certificate was revoked, when revocation data says it was.</summary>
    public DateTimeOffset? RevocationTime { get; internal set; }

    /// <summary>
    /// The certificate's names (subject, alternative names, and email addresses in the subject
    /// when it has no alternative names) fall within the name constraints of the certificates
    /// above it, the trust anchor's included. Passed when no constraint applies. A self-issued
    /// CA certificate inside the chain is not checked, as RFC 5280 says. Failed when a name
    /// cannot be read or judged against a constraint, and when a certificate above has
    /// constraints that cannot be read.
    /// </summary>
    public PdfCheckStatus NameConstraints { get; internal set; }

    /// <summary>The certificate policies the certificate asserts, as object identifiers; empty when it asserts none.</summary>
    public IReadOnlyList<string> Policies { get; internal set; } = [];

    /// <summary>The certificate's qualified certificate statements, or null when it has none.</summary>
    public PdfQcStatements? QcStatements { get; internal set; }

    /// <summary>What did not pass, or null.</summary>
    public string? Problem { get; internal set; }
}

/// <summary>
/// The certificate checks for a signer or a time stamp authority: a chain to a trust anchor, then
/// validity, usage, name constraints and revocation of each certificate at the validation time,
/// certificate policies along the chain, and the qualified status the certificate claims.
/// </summary>
/// <remarks>
/// These are the X.509 checks of ETSI EN 319 102-1 (5.2.6), made as RFC 5280, 6.1 describes:
/// certificate policies with policy mappings, policy constraints and inhibit anyPolicy, computed
/// with the policy graph of RFC 9618; name constraints on DNS names, email addresses, URI hosts,
/// directory names and IP addresses. The trust anchor's own policies are not processed, as RFC
/// 5280 treats the anchor as input; its name constraints are applied. Policy qualifiers are not
/// read. A constraint on another name form fails a certificate that carries such a name, and any
/// other critical extension the library does not process fails the certificate. Qualified status
/// is what the QCStatements claim (ETSI EN 319 412-5), confirmed against the trusted lists the
/// application supplies (ETSI TS 119 615), which the library never downloads. Delta and
/// partitioned CRLs are not used.
/// </remarks>
public sealed class PdfCertificateValidation
{
    internal PdfCertificateValidation()
    {
    }

    /// <summary>The time certificates were checked at.</summary>
    public DateTimeOffset ValidationTime { get; internal set; }

    /// <summary>Where <see cref="ValidationTime"/> comes from.</summary>
    public PdfValidationTimeSource TimeSource { get; internal set; }

    /// <summary>
    /// The chain reaches a supplied trust anchor, or a trusted list anchor at the validation time
    /// (see <see cref="PdfValidationOptions.TrustedListsAsTrustAnchors"/>), and every link's
    /// signature verifies. Not checked when no trust anchors were supplied and no usable trusted
    /// list serves as anchors; the chain is still built as far as it goes. Failed when usable
    /// lists serve as anchors but none of their services anchors the chain at the validation time.
    /// </summary>
    public PdfCheckStatus Chain { get; internal set; }

    /// <summary>The chain, the signer or time stamp authority first.</summary>
    public IReadOnlyList<PdfCertificateCheck> Certificates { get; internal set; } = [];

    /// <summary>
    /// The chain is valid for an acceptable certificate policy, or needs none (RFC 5280, 6.1).
    /// Passed when no policies were asked for and no certificate requires one. Not checked when
    /// the chain does not reach a trust anchor.
    /// </summary>
    public PdfCheckStatus Policy { get; internal set; }

    /// <summary>
    /// The policies the chain is valid for, as the trust anchor's domain names them (before any
    /// mapping), limited to <see cref="PdfValidationOptions.AcceptablePolicies"/> when those were
    /// given. 2.5.29.32.0 (anyPolicy) when every policy is accepted along the chain.
    /// </summary>
    public IReadOnlyList<string> ValidPolicies { get; internal set; } = [];

    /// <summary>
    /// The qualification the first certificate's QCStatements claim, such as QESig. Meaningful only
    /// when <see cref="Status"/> is Passed. <see cref="TrustedListQualification"/> confirms it.
    /// </summary>
    public PdfQualification Qualification { get; internal set; }

    /// <summary>
    /// The qualification confirmed against <see cref="PdfValidationOptions.TrustedLists"/> at the
    /// validation time; null when no trusted lists were supplied, and for a time stamp authority.
    /// Indeterminate, with the reason, when <see cref="Status"/> is not Passed: the lists are only
    /// consulted for a certificate whose checks passed.
    /// </summary>
    public PdfTrustedListQualification? TrustedListQualification { get; internal set; }

    /// <summary>Everything that did not pass, in plain words.</summary>
    public IReadOnlyList<string> Problems { get; internal set; } = [];

    /// <summary>
    /// Failed when any check failed; Indeterminate when no check failed but one could not be
    /// made (no trust anchors, missing or stale revocation data); Passed when every check passed.
    /// </summary>
    public PdfCheckStatus Status { get; internal set; }
}

/// <summary>An RFC 3161 timestamp found in a signature or forming a document timestamp.</summary>
public sealed class PdfTimestampInfo
{
    internal PdfTimestampInfo()
    {
    }

    /// <summary>The time the authority certified.</summary>
    public DateTimeOffset Time { get; internal set; }

    /// <summary>The authority's certificate, or null when the token does not carry it.</summary>
    public X509Certificate2? Certificate { get; internal set; }

    /// <summary>The authority's common name.</summary>
    public string? AuthorityName { get; internal set; }

    /// <summary>The authority's policy, as an object identifier.</summary>
    public string? PolicyOid { get; internal set; }

    /// <summary>The token's serial number, in hexadecimal.</summary>
    public string? SerialNumber { get; internal set; }

    /// <summary>The timestamp's hash matches what it covers: the signature value, or the document's byte range.</summary>
    public bool ImprintMatches { get; internal set; }

    /// <summary>The token's signature verifies with the authority's certificate.</summary>
    public bool SignatureValid { get; internal set; }

    /// <summary>The authority's certificate checks, when validation options were given.</summary>
    public PdfCertificateValidation? Validation { get; internal set; }

    /// <summary>
    /// Whether the authority is a qualified time stamp service by
    /// <see cref="PdfValidationOptions.TrustedLists"/>. Passed only when the authority's
    /// certificate checks (<see cref="Validation"/>) passed, the timestamp is intact, and a
    /// TSA/QTST service was granted at the validation time whose identity is the public key of
    /// the authority's certificate, or of a CA of its chain that the list names as that TSA/QTST
    /// service (a CA listed only as a CA service does not count). Failed when no such service was
    /// granted; Indeterminate when the authority's checks did not pass, the timestamp is not
    /// intact, no list can be used, or the service has critical extensions the library does not
    /// interpret; NotChecked without trusted lists or without the authority's certificate.
    /// </summary>
    public PdfCheckStatus QualifiedTimestamp { get; internal set; }

    /// <summary>The TSA/QTST service that confirmed <see cref="QualifiedTimestamp"/>, or null.</summary>
    public PdfTrustService? QualifiedTimestampService { get; internal set; }

    /// <summary>How <see cref="QualifiedTimestamp"/> was decided, in plain words.</summary>
    public IReadOnlyList<string> QualifiedTimestampReasons { get; internal set; } = [];

    /// <summary>Why the timestamp failed, or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The timestamp covers what it claims and its signature is valid.</summary>
    public bool IsIntact => ImprintMatches && SignatureValid;
}
