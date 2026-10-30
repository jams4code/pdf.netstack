using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfNetStack.Signing;

/// <summary>The status of a trust service in a trusted list (ETSI TS 119 612, 5.5.4), current or past.</summary>
public enum PdfTrustServiceStatus
{
    /// <summary>A status this library does not know; it never counts as qualified.</summary>
    Unknown,

    /// <summary>granted: the service is qualified (eIDAS).</summary>
    Granted,

    /// <summary>withdrawn: the qualified status was withdrawn (eIDAS).</summary>
    Withdrawn,

    /// <summary>recognisedatnationallevel: a service approved at national level, not an EU qualified one.</summary>
    RecognisedAtNationalLevel,

    /// <summary>deprecatedatnationallevel: the national approval was withdrawn.</summary>
    DeprecatedAtNationalLevel,

    /// <summary>undersupervision: a supervised service before eIDAS, counted as qualified.</summary>
    UnderSupervision,

    /// <summary>supervisionincessation: supervised and ceasing before eIDAS, counted as qualified.</summary>
    SupervisionInCessation,

    /// <summary>supervisionceased: supervision ended before eIDAS.</summary>
    SupervisionCeased,

    /// <summary>supervisionrevoked: supervision revoked before eIDAS.</summary>
    SupervisionRevoked,

    /// <summary>accredited: an accredited service before eIDAS, counted as qualified.</summary>
    Accredited,

    /// <summary>accreditationceased: accreditation ended before eIDAS.</summary>
    AccreditationCeased,

    /// <summary>accreditationrevoked: accreditation revoked before eIDAS.</summary>
    AccreditationRevoked,
}

/// <summary>The service type identifiers of ETSI TS 119 612 (5.5.1.1) that qualification uses.</summary>
public static class PdfTrustServiceTypes
{
    /// <summary>A certification authority issuing qualified certificates.</summary>
    public const string CaQc = "http://uri.etsi.org/TrstSvc/Svctype/CA/QC";

    /// <summary>A certification authority issuing public key certificates that are not qualified.</summary>
    public const string CaPkc = "http://uri.etsi.org/TrstSvc/Svctype/CA/PKC";

    /// <summary>A qualified electronic time stamp service.</summary>
    public const string TsaQtst = "http://uri.etsi.org/TrstSvc/Svctype/TSA/QTST";

    /// <summary>A time stamp service that is not qualified.</summary>
    public const string Tsa = "http://uri.etsi.org/TrstSvc/Svctype/TSA";

    /// <summary>An OCSP responder for qualified certificates.</summary>
    public const string OcspQc = "http://uri.etsi.org/TrstSvc/Svctype/Certstatus/OCSP/QC";

    /// <summary>CRLs for qualified certificates.</summary>
    public const string CrlQc = "http://uri.etsi.org/TrstSvc/Svctype/Certstatus/CRL/QC";
}

/// <summary>
/// One element of a service's Qualifications extension (ETSI TS 119 612, 5.5.9.2): qualifiers,
/// such as QCWithQSCD or QCForESeal, that apply to the certificates its criteria select.
/// </summary>
public sealed class PdfServiceQualification
{
    internal PdfServiceQualification(IReadOnlyList<string> qualifiers, TrustedListCriteria criteria)
    {
        Qualifiers = qualifiers;
        Criteria = criteria;
    }

    /// <summary>The qualifier URIs, such as http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/QCWithQSCD.</summary>
    public IReadOnlyList<string> Qualifiers { get; }

    internal TrustedListCriteria Criteria { get; }

    /// <summary>
    /// Whether the criteria select a certificate: key usage bits, certificate policies, extended
    /// key usages and subject attributes, combined as the list asserts (all, at least one, none).
    /// Null when a criterion cannot be evaluated, such as one the library does not know.
    /// </summary>
    public bool? Applies(X509Certificate2 certificate)
    {
        Compat.ThrowIfNull(certificate, nameof(certificate));
        return Criteria.Evaluate(certificate);
    }
}

/// <summary>What a trusted list says about a service at one period: its current information or one entry of its history.</summary>
public class PdfTrustServiceInformation
{
    internal PdfTrustServiceInformation()
    {
    }

    /// <summary>The service type URI, such as <see cref="PdfTrustServiceTypes.CaQc"/>.</summary>
    public string TypeIdentifier { get; internal set; } = "";

    /// <summary>The service name, in English when the list gives it.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The status.</summary>
    public PdfTrustServiceStatus Status { get; internal set; }

    /// <summary>The status URI as the list writes it.</summary>
    public string StatusUri { get; internal set; } = "";

    /// <summary>When this status took effect. It lasts until the next later status of the service.</summary>
    public DateTimeOffset StatusStartingTime { get; internal set; }

    /// <summary>The certificates in the service digital identity.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; internal set; } = [];

    /// <summary>
    /// The subject key identifiers in the service digital identity (X509SKI). Validation matches
    /// them against the SHA-1 of a certificate's subjectPublicKey (RFC 5280, 4.2.1.2, method 1),
    /// never against the subject key identifier extension the certificate carries.
    /// </summary>
    public IReadOnlyList<byte[]> SubjectKeyIdentifiers { get; internal set; } = [];

    /// <summary>The subject names in the service digital identity (X509SubjectName), as written; reported, not used to match.</summary>
    public IReadOnlyList<string> SubjectNames { get; internal set; } = [];

    /// <summary>The Qualifications extension.</summary>
    public IReadOnlyList<PdfServiceQualification> Qualifications { get; internal set; } = [];

    /// <summary>The additional service information URIs, such as http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/ForeSignatures.</summary>
    public IReadOnlyList<string> AdditionalServiceInformation { get; internal set; } = [];

    /// <summary>The names of extensions marked critical that the library does not interpret. A status that has any is not relied on.</summary>
    public IReadOnlyList<string> UnknownCriticalExtensions { get; internal set; } = [];

    /// <summary>Whether this status counts as qualified: granted, or one of the supervised or accredited statuses before eIDAS.</summary>
    public bool IsQualifiedStatus => Status is PdfTrustServiceStatus.Granted or PdfTrustServiceStatus.UnderSupervision
        or PdfTrustServiceStatus.SupervisionInCessation or PdfTrustServiceStatus.Accredited;
}

/// <summary>A trust service of a trusted list, with its current information and its history.</summary>
public sealed class PdfTrustService : PdfTrustServiceInformation
{
    internal PdfTrustService(PdfTrustServiceProvider provider) => Provider = provider;

    /// <summary>The provider of the service.</summary>
    public PdfTrustServiceProvider Provider { get; }

    /// <summary>Earlier statuses, as the list orders them (usually newest first).</summary>
    public IReadOnlyList<PdfTrustServiceInformation> History { get; internal set; } = [];

    /// <summary>The information in force at a time: the current information or the history entry with the latest starting time not after it; null before the first.</summary>
    public PdfTrustServiceInformation? At(DateTimeOffset time)
    {
        PdfTrustServiceInformation? best = null;
        foreach (PdfTrustServiceInformation entry in History.Prepend(this))
        {
            if (entry.StatusStartingTime <= time && (best is null || entry.StatusStartingTime > best.StatusStartingTime)) best = entry;
        }

        return best;
    }
}

/// <summary>A trust service provider of a trusted list.</summary>
public sealed class PdfTrustServiceProvider
{
    internal PdfTrustServiceProvider(PdfTrustedList list) => TrustedList = list;

    /// <summary>The list that names the provider.</summary>
    public PdfTrustedList TrustedList { get; }

    /// <summary>The provider's name, in English when the list gives it.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Its trade names and registration identifiers, as written.</summary>
    public IReadOnlyList<string> TradeNames { get; internal set; } = [];

    /// <summary>Its services.</summary>
    public IReadOnlyList<PdfTrustService> Services { get; internal set; } = [];
}

/// <summary>A pointer to another trusted list (ETSI TS 119 612, 5.3.13), as the EU list of trusted lists points to each national list.</summary>
public sealed class PdfTrustedListPointer
{
    internal PdfTrustedListPointer()
    {
    }

    /// <summary>Where the list is published.</summary>
    public string Location { get; internal set; } = "";

    /// <summary>The territory of the list, such as BE; null when not given.</summary>
    public string? Territory { get; internal set; }

    /// <summary>The list's type URI; null when not given.</summary>
    public string? Type { get; internal set; }

    /// <summary>The media type of the published list, such as application/vnd.etsi.tsl+xml; null when not given.</summary>
    public string? MimeType { get; internal set; }

    /// <summary>The certificates that may sign the list.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; internal set; } = [];
}

/// <summary>
/// A trusted list of ETSI TS 119 612: the EU list of trusted lists or a national list, read from
/// XML the application supplies, with its signature checked against certificates the application
/// supplies. The library never downloads a list.
/// </summary>
/// <remarks>
/// The XML is read with DTDs prohibited, no external resolution, at most <see cref="MaxLength"/>
/// bytes and 64 levels of nesting. The signature is checked as XMLDSig with the rules of
/// ETSI TS 119 612 (an enveloped signature on the whole list); see <see cref="Signature"/>. Pass
/// the lists to <see cref="PdfValidationOptions.TrustedLists"/> to confirm qualified status.
/// </remarks>
public sealed class PdfTrustedList
{
    /// <summary>The largest list read, in bytes. National lists are a few megabytes at most.</summary>
    public const int MaxLength = 32 * 1024 * 1024;

    internal const string Namespace = "http://uri.etsi.org/02231/v2#";
    private const string AdditionalTypes = "http://uri.etsi.org/02231/v2/additionaltypes#";
    internal const string QualificationsNamespace = "http://uri.etsi.org/TrstSvc/SvcInfoExt/eSigDir-1999-93-EC-TrustedList/#";
    private const string StatusPrefix = "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/";
    private const int MaxDepth = 64;

    private PdfTrustedList()
    {
    }

    /// <summary>The TSLVersionIdentifier, 5 or 6 for the current specification.</summary>
    public int Version { get; private set; }

    /// <summary>The TSLSequenceNumber, which grows with each issue.</summary>
    public int SequenceNumber { get; private set; }

    /// <summary>The TSLType URI, such as http://uri.etsi.org/TrstSvc/TrustedList/TSLType/EUgeneric.</summary>
    public string Type { get; private set; } = "";

    /// <summary>Whether this is a list of lists, such as the EU list of trusted lists.</summary>
    public bool IsListOfLists => Type.EndsWith("listofthelists", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scheme territory, such as BE, or EU for the list of lists.</summary>
    public string Territory { get; private set; } = "";

    /// <summary>The scheme operator's name, in English when the list gives it.</summary>
    public string OperatorName { get; private set; } = "";

    /// <summary>When the list was issued.</summary>
    public DateTimeOffset IssueDate { get; private set; }

    /// <summary>When the next list is due at the latest; null for a closed list. A list past this time is expired and not used.</summary>
    public DateTimeOffset? NextUpdate { get; private set; }

    /// <summary>The pointers to other lists.</summary>
    public IReadOnlyList<PdfTrustedListPointer> Pointers { get; private set; } = [];

    /// <summary>The trust service providers.</summary>
    public IReadOnlyList<PdfTrustServiceProvider> Providers { get; private set; } = [];

    /// <summary>Every service of every provider.</summary>
    public IEnumerable<PdfTrustService> Services => Providers.SelectMany(p => p.Services);

    /// <summary>
    /// Passed when the list's XML signature verifies with one of the certificates supplied for
    /// it and was made within that certificate's validity (at the signing time of the signed
    /// XAdES properties, or else at the list's issue date); Failed otherwise;
    /// NotChecked when no certificate was supplied. Only a list whose signature passed is used
    /// for validation.
    /// </summary>
    public PdfCheckStatus Signature { get; private set; }

    /// <summary>The supplied certificate the signature verified with, or null.</summary>
    public X509Certificate2? SignerCertificate { get; private set; }

    /// <summary>Why the signature did not pass, or null.</summary>
    public string? SignatureProblem { get; private set; }

    /// <summary>Reads a list and checks its signature against the certificates published for it, such as those the Official Journal gives for the EU list of trusted lists.</summary>
    /// <param name="xml">The list's XML.</param>
    /// <param name="signingCertificates">The certificates that may sign the list.</param>
    /// <exception cref="FormatException">The bytes are not a well formed trusted list, or larger than <see cref="MaxLength"/>.</exception>
    public static PdfTrustedList Load(byte[] xml, IEnumerable<X509Certificate2> signingCertificates)
    {
        Compat.ThrowIfNull(xml, nameof(xml));
        Compat.ThrowIfNull(signingCertificates, nameof(signingCertificates));
        (PdfTrustedList list, XmlDocument document) = Read(xml);
        list.CheckSignature(document, [.. signingCertificates]);
        return list;
    }

    /// <summary>Reads a list from a stream and checks its signature against the certificates published for it.</summary>
    /// <param name="xml">The list's XML; read to its end, at most <see cref="MaxLength"/> bytes.</param>
    /// <param name="signingCertificates">The certificates that may sign the list.</param>
    /// <exception cref="FormatException">The stream does not hold a well formed trusted list, or holds more than <see cref="MaxLength"/> bytes.</exception>
    public static PdfTrustedList Load(Stream xml, IEnumerable<X509Certificate2> signingCertificates) => Load(ReadAll(xml), signingCertificates);

    /// <summary>
    /// Reads a list a list of lists points to, such as a national list from the EU list of
    /// trusted lists, and checks its signature against the certificates the pointers for its
    /// territory and type give. The list of lists must itself have passed its signature check.
    /// </summary>
    /// <param name="xml">The list's XML.</param>
    /// <param name="listOfLists">The list of lists that points to it.</param>
    /// <exception cref="FormatException">The bytes are not a well formed trusted list, or larger than <see cref="MaxLength"/>.</exception>
    public static PdfTrustedList Load(byte[] xml, PdfTrustedList listOfLists)
    {
        Compat.ThrowIfNull(xml, nameof(xml));
        Compat.ThrowIfNull(listOfLists, nameof(listOfLists));
        (PdfTrustedList list, XmlDocument document) = Read(xml);
        if (listOfLists.Signature != PdfCheckStatus.Passed)
        {
            list.Signature = PdfCheckStatus.Failed;
            list.SignatureProblem = "The list of lists it was checked against did not pass its own signature check.";
            return list;
        }

        List<X509Certificate2> certificates = [.. listOfLists.Pointers
            .Where(p => string.Equals(p.Territory, list.Territory, StringComparison.OrdinalIgnoreCase) && (p.Type is null || p.Type == list.Type))
            .SelectMany(p => p.Certificates)];
        if (certificates.Count == 0)
        {
            list.Signature = PdfCheckStatus.Failed;
            list.SignatureProblem = "The list of lists has no pointer to a list for " + list.Territory + " of this type.";
            return list;
        }

        list.CheckSignature(document, certificates);
        return list;
    }

    /// <summary>Reads a list from a stream and checks its signature against the pointers of a list of lists.</summary>
    /// <param name="xml">The list's XML; read to its end, at most <see cref="MaxLength"/> bytes.</param>
    /// <param name="listOfLists">The list of lists that points to it.</param>
    /// <exception cref="FormatException">The stream does not hold a well formed trusted list, or holds more than <see cref="MaxLength"/> bytes.</exception>
    public static PdfTrustedList Load(Stream xml, PdfTrustedList listOfLists) => Load(ReadAll(xml), listOfLists);

    /// <summary>Whether the list may be relied on now: its signature passed and it has not expired.</summary>
    internal string? Unusable(DateTimeOffset now) =>
        Signature != PdfCheckStatus.Passed ? "its signature " + (Signature == PdfCheckStatus.NotChecked ? "was not checked" : "does not verify") + (SignatureProblem is string p ? " (" + p.TrimEnd('.') + ")" : "")
        : NextUpdate is DateTimeOffset next && next < now ? "it expired at " + next.ToString("u", CultureInfo.InvariantCulture)
        : null;

    /// <summary>A short name for messages: territory, sequence number and type.</summary>
    internal string Describe() => (IsListOfLists ? "list of lists " : "trusted list ") + Territory + " #" + SequenceNumber.ToString(CultureInfo.InvariantCulture);

    private void CheckSignature(XmlDocument document, List<X509Certificate2> certificates)
    {
        if (certificates.Count == 0)
        {
            Signature = PdfCheckStatus.NotChecked;
            SignatureProblem = "No certificate was supplied for the list.";
            return;
        }

        (X509Certificate2? signer, DateTimeOffset? signingTime, string? problem) = XmlSignature.Verify(document, certificates);
        if (signer is null)
        {
            Signature = PdfCheckStatus.Failed;
            SignatureProblem = problem;
            return;
        }

        // A list may be signed again after its issue, as closed lists are; the signed XAdES signing time says when.
        DateTimeOffset signed = signingTime ?? IssueDate;
        if (signed < Pkix.NotBefore(signer) || signed > Pkix.NotAfter(signer))
        {
            Signature = PdfCheckStatus.Failed;
            SignatureProblem = "The list was signed outside the validity of the certificate that signed it.";
            return;
        }

        Signature = PdfCheckStatus.Passed;
        SignerCertificate = signer;
    }

    private static byte[] ReadAll(Stream xml)
    {
        Compat.ThrowIfNull(xml, nameof(xml));
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = xml.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > MaxLength) throw new FormatException("The trusted list is larger than " + MaxLength.ToString(CultureInfo.InvariantCulture) + " bytes.");
            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }

    private static (PdfTrustedList, XmlDocument) Read(byte[] xml)
    {
        if (xml.Length > MaxLength) throw new FormatException("The trusted list is larger than " + MaxLength.ToString(CultureInfo.InvariantCulture) + " bytes.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxLength,
        };
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            // A first pass bounds the nesting, so nothing later recurses deeper than that.
            using (var stream = new MemoryStream(xml, writable: false))
            using (XmlReader scan = XmlReader.Create(stream, settings))
            {
                while (scan.Read())
                {
                    if (scan.Depth > MaxDepth) throw new FormatException("The trusted list nests deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels.");
                }
            }

            using (var stream = new MemoryStream(xml, writable: false))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                document.Load(reader);
            }
        }
        catch (Exception e) when (e is XmlException or ArgumentException or NotSupportedException)
        {
            // Unknown encodings surface as argument or not supported errors.
            throw new FormatException("The trusted list is not well formed XML: " + e.Message, e);
        }

        try
        {
            return (Parse(document), document);
        }
        catch (Exception e) when (e is OverflowException or ArgumentException)
        {
            throw new FormatException("The trusted list cannot be read: " + e.Message, e);
        }
    }

    private static PdfTrustedList Parse(XmlDocument document)
    {
        XmlElement root = document.DocumentElement!;
        if (root.LocalName != "TrustServiceStatusList" || root.NamespaceURI != Namespace) throw new FormatException("The XML is not an ETSI TS 119 612 trusted list.");
        XmlElement scheme = Required(root, "SchemeInformation");
        var list = new PdfTrustedList
        {
            Version = Integer(Required(scheme, "TSLVersionIdentifier")),
            SequenceNumber = Integer(Required(scheme, "TSLSequenceNumber")),
            Type = Text(Required(scheme, "TSLType")),
            OperatorName = Multilingual(Child(scheme, "SchemeOperatorName")),
            Territory = Text(Required(scheme, "SchemeTerritory")),
            IssueDate = Date(Required(scheme, "ListIssueDateTime")),
        };
        if (Child(scheme, "NextUpdate") is XmlElement next && Child(next, "dateTime") is XmlElement at) list.NextUpdate = Date(at);

        var pointers = new List<PdfTrustedListPointer>();
        if (Child(scheme, "PointersToOtherTSL") is XmlElement others)
        {
            foreach (XmlElement p in Children(others, "OtherTSLPointer")) pointers.Add(Pointer(p));
        }

        list.Pointers = pointers;
        var providers = new List<PdfTrustServiceProvider>();
        if (Child(root, "TrustServiceProviderList") is XmlElement tsps)
        {
            foreach (XmlElement p in Children(tsps, "TrustServiceProvider")) providers.Add(Provider(list, p));
        }

        list.Providers = providers;
        return list;
    }

    private static PdfTrustedListPointer Pointer(XmlElement p)
    {
        var pointer = new PdfTrustedListPointer { Location = Child(p, "TSLLocation") is XmlElement l ? Text(l) : "" };
        var certificates = new List<X509Certificate2>();
        if (Child(p, "ServiceDigitalIdentities") is XmlElement identities)
        {
            foreach (XmlElement identity in Children(identities, "ServiceDigitalIdentity")) Identity(identity, certificates, null, null);
        }

        pointer.Certificates = certificates;
        if (Child(p, "AdditionalInformation") is XmlElement additional)
        {
            foreach (XmlElement info in Children(additional, "OtherInformation"))
            {
                foreach (XmlElement value in Elements(info))
                {
                    switch (value.LocalName)
                    {
                        case "TSLType" when value.NamespaceURI == Namespace: pointer.Type = Text(value); break;
                        case "SchemeTerritory" when value.NamespaceURI == Namespace: pointer.Territory = Text(value); break;
                        case "MimeType" when value.NamespaceURI == AdditionalTypes: pointer.MimeType = Text(value); break;
                    }
                }
            }
        }

        return pointer;
    }

    private static PdfTrustServiceProvider Provider(PdfTrustedList list, XmlElement p)
    {
        var provider = new PdfTrustServiceProvider(list);
        if (Child(p, "TSPInformation") is XmlElement info)
        {
            provider.Name = Multilingual(Child(info, "TSPName"));
            if (Child(info, "TSPTradeName") is XmlElement trade) provider.TradeNames = [.. Children(trade, "Name").Select(Text)];
        }

        var services = new List<PdfTrustService>();
        if (Child(p, "TSPServices") is XmlElement all)
        {
            foreach (XmlElement s in Children(all, "TSPService"))
            {
                var service = new PdfTrustService(provider);
                Information(Required(s, "ServiceInformation"), service);
                if (Child(s, "ServiceHistory") is XmlElement history)
                {
                    service.History = [.. Children(history, "ServiceHistoryInstance").Select(h => Information(h, new PdfTrustServiceInformation()))];
                }

                services.Add(service);
            }
        }

        provider.Services = services;
        return provider;
    }

    /// <summary>ServiceInformation and ServiceHistoryInstance share these elements.</summary>
    private static PdfTrustServiceInformation Information(XmlElement e, PdfTrustServiceInformation info)
    {
        info.TypeIdentifier = Text(Required(e, "ServiceTypeIdentifier"));
        info.Name = Multilingual(Child(e, "ServiceName"));
        info.StatusUri = Text(Required(e, "ServiceStatus"));
        info.Status = Status(info.StatusUri);
        info.StatusStartingTime = Date(Required(e, "StatusStartingTime"));
        var certificates = new List<X509Certificate2>();
        var skis = new List<byte[]>();
        var names = new List<string>();
        if (Child(e, "ServiceDigitalIdentity") is XmlElement identity) Identity(identity, certificates, skis, names);
        info.Certificates = certificates;
        info.SubjectKeyIdentifiers = skis;
        info.SubjectNames = names;

        var qualifications = new List<PdfServiceQualification>();
        var additional = new List<string>();
        var unknown = new List<string>();
        if (Child(e, "ServiceInformationExtensions") is XmlElement extensions)
        {
            foreach (XmlElement extension in Children(extensions, "Extension"))
            {
                bool critical = extension.GetAttribute("Critical") is { Length: > 0 } c && XmlConvert.ToBoolean(c);
                foreach (XmlElement content in Elements(extension))
                {
                    switch (content.LocalName)
                    {
                        case "Qualifications" when content.NamespaceURI == QualificationsNamespace:
                            foreach (XmlElement q in Elements(content).Where(q => q.LocalName == "QualificationElement")) qualifications.Add(Qualification(q));
                            break;
                        case "AdditionalServiceInformation" when content.NamespaceURI == Namespace:
                            if (Child(content, "URI") is XmlElement uri) additional.Add(Text(uri));
                            break;
                        case "TakenOverBy" or "ExpiredCertsRevocationInfo":
                            break;
                        default:
                            if (critical) unknown.Add(content.LocalName);
                            break;
                    }
                }
            }
        }

        info.Qualifications = qualifications;
        info.AdditionalServiceInformation = additional;
        info.UnknownCriticalExtensions = unknown;
        return info;
    }

    private static PdfServiceQualification Qualification(XmlElement q)
    {
        var qualifiers = new List<string>();
        TrustedListCriteria? criteria = null;
        foreach (XmlElement e in Elements(q))
        {
            if (e.LocalName == "Qualifiers")
            {
                foreach (XmlElement qualifier in Elements(e).Where(x => x.LocalName == "Qualifier")) qualifiers.Add(qualifier.GetAttribute("uri"));
            }
            else if (e.LocalName == "CriteriaList")
            {
                criteria = TrustedListCriteria.Read(e);
            }
        }

        // Without criteria nothing is selected; ETSI requires them.
        return new PdfServiceQualification(qualifiers, criteria ?? TrustedListCriteria.Nothing);
    }

    /// <summary>The DigitalId values of a service digital identity; certificates that cannot be read are skipped.</summary>
    private static void Identity(XmlElement identity, List<X509Certificate2> certificates, List<byte[]>? skis, List<string>? names)
    {
        foreach (XmlElement id in Children(identity, "DigitalId"))
        {
            foreach (XmlElement value in Elements(id))
            {
                if (value.NamespaceURI != Namespace) continue;
                switch (value.LocalName)
                {
                    case "X509Certificate":
                        try
                        {
                            certificates.Add(Pkix.Load(Convert.FromBase64String(value.InnerText)));
                        }
                        catch (FormatException)
                        {
                            // An unreadable certificate identifies nothing; the service can still match by its others.
                        }

                        break;
                    case "X509SKI" when skis is not null:
                        try
                        {
                            skis.Add(Convert.FromBase64String(value.InnerText));
                        }
                        catch (FormatException)
                        {
                            // Skipped, as an unreadable certificate.
                        }

                        break;
                    case "X509SubjectName" when names is not null:
                        names.Add(Text(value));
                        break;
                }
            }
        }
    }

    private static PdfTrustServiceStatus Status(string uri) => !uri.StartsWith(StatusPrefix, StringComparison.Ordinal) ? PdfTrustServiceStatus.Unknown : uri.Substring(StatusPrefix.Length) switch
    {
        "granted" => PdfTrustServiceStatus.Granted,
        "withdrawn" => PdfTrustServiceStatus.Withdrawn,
        "recognisedatnationallevel" => PdfTrustServiceStatus.RecognisedAtNationalLevel,
        "deprecatedatnationallevel" => PdfTrustServiceStatus.DeprecatedAtNationalLevel,
        "undersupervision" => PdfTrustServiceStatus.UnderSupervision,
        "supervisionincessation" => PdfTrustServiceStatus.SupervisionInCessation,
        "supervisionceased" => PdfTrustServiceStatus.SupervisionCeased,
        "supervisionrevoked" => PdfTrustServiceStatus.SupervisionRevoked,
        "accredited" => PdfTrustServiceStatus.Accredited,
        "accreditationceased" => PdfTrustServiceStatus.AccreditationCeased,
        "accreditationrevoked" => PdfTrustServiceStatus.AccreditationRevoked,
        _ => PdfTrustServiceStatus.Unknown,
    };

    /// <summary>A multilingual name: the English one, else the first; empty when there is none.</summary>
    private static string Multilingual(XmlElement? parent)
    {
        if (parent is null) return "";
        List<XmlElement> names = Children(parent, "Name");
        XmlElement? english = names.FirstOrDefault(n => n.GetAttribute("xml:lang").StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return english is not null ? Text(english) : names.Count > 0 ? Text(names[0]) : "";
    }

    private static int Integer(XmlElement e) =>
        int.TryParse(Text(e), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : throw new FormatException(e.LocalName + " is not an integer.");

    private static DateTimeOffset Date(XmlElement e)
    {
        try
        {
            return XmlConvert.ToDateTimeOffset(Text(e));
        }
        catch (FormatException)
        {
            throw new FormatException(e.LocalName + " is not a date and time.");
        }
    }

    private static string Text(XmlElement e) => e.InnerText.Trim();

    private static XmlElement Required(XmlElement parent, string name) => Child(parent, name) ?? throw new FormatException("The trusted list has no " + name + " in " + parent.LocalName + ".");

    private static XmlElement? Child(XmlElement parent, string name)
    {
        foreach (XmlNode n in parent.ChildNodes)
        {
            if (n is XmlElement e && e.LocalName == name && e.NamespaceURI == Namespace) return e;
        }

        return null;
    }

    private static List<XmlElement> Children(XmlElement parent, string name) => Elements(parent).Where(e => e.LocalName == name && e.NamespaceURI == Namespace).ToList();

    internal static List<XmlElement> Elements(XmlElement parent)
    {
        var list = new List<XmlElement>();
        foreach (XmlNode n in parent.ChildNodes)
        {
            if (n is XmlElement e) list.Add(e);
        }

        return list;
    }
}
