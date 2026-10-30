using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// ETSI TS 119 612 trusted lists and the qualification of ETSI TS 119 615. Every list and PKI is
/// generated here, and the lists are signed with Microsoft's SignedXml or with transforms from the
/// same package, so the library's own canonicalization and XMLDSig checks meet an independent
/// implementation. No real list is downloaded.
/// </summary>
[TestClass]
public sealed class TrustedListTests
{
    private const string EuGeneric = "http://uri.etsi.org/TrstSvc/TrustedList/TSLType/EUgeneric";
    private const string EuLotl = "http://uri.etsi.org/TrstSvc/TrustedList/TSLType/EUlistofthelists";
    private const string Ext = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/";
    private const string Etsi = "0.4.0.1862.1.";
    private const string Policy = "0.4.0.194112.1.2";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Lazy<X509Certificate2> LotlSigner = new(() => TestPki.Issue("List of Lists Signer", null, []));
    private static readonly Lazy<X509Certificate2> NationalSigner = new(() => TestPki.Issue("National List Signer", null, []));
    private static readonly Lazy<X509Certificate2> Ca = new(() => TestPki.Issue("Qualified CA", null, TestPki.Ca(null), Now.AddYears(-5)));

    /// <summary>A service, or one entry of its history, as the test lists write it.</summary>
    private sealed class Svc
    {
        public string Type { get; init; } = PdfTrustServiceTypes.CaQc;

        public string Status { get; init; } = "granted";

        public DateTimeOffset Start { get; init; } = Now.AddYears(-3);

        public X509Certificate2? Certificate { get; init; }

        public byte[]? Ski { get; init; }

        public string Name { get; init; } = "Test Qualified CA";

        public List<(string[] Qualifiers, string Criteria)> Qualifications { get; init; } = [];

        public List<string> Additional { get; init; } = [];

        public string? Extension { get; init; }

        public List<Svc> History { get; init; } = [];
    }

    private static string KeyUsage(string bit = "nonRepudiation", bool set = true) =>
        "<ecc:CriteriaList assert=\"atLeastOne\"><ecc:KeyUsage><ecc:KeyUsageBit name=\"" + bit + "\">" + (set ? "true" : "false") + "</ecc:KeyUsageBit></ecc:KeyUsage></ecc:CriteriaList>";

    private static string Policies(string assert, params string[] oids) =>
        "<ecc:CriteriaList assert=\"" + assert + "\"><ecc:PolicySet>" + string.Concat(oids.Select(o => "<ecc:PolicyIdentifier><xades:Identifier>" + o + "</xades:Identifier></ecc:PolicyIdentifier>")) + "</ecc:PolicySet></ecc:CriteriaList>";

    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string B64(byte[] data) => Convert.ToBase64String(data);

    private static string Information(Svc s, string element)
    {
        var x = new StringBuilder();
        x.Append("<tsl:").Append(element).Append('>');
        x.Append("<tsl:ServiceTypeIdentifier>").Append(s.Type).Append("</tsl:ServiceTypeIdentifier>");
        x.Append("<tsl:ServiceName><tsl:Name xml:lang=\"en\">").Append(s.Name).Append("</tsl:Name></tsl:ServiceName>");
        x.Append("<tsl:ServiceDigitalIdentity>");
        if (s.Certificate is not null) x.Append("<tsl:DigitalId><tsl:X509Certificate>").Append(B64(s.Certificate.RawData)).Append("</tsl:X509Certificate></tsl:DigitalId>");
        if (s.Ski is not null) x.Append("<tsl:DigitalId><tsl:X509SubjectName>CN=Old Name</tsl:X509SubjectName></tsl:DigitalId><tsl:DigitalId><tsl:X509SKI>").Append(B64(s.Ski)).Append("</tsl:X509SKI></tsl:DigitalId>");
        x.Append("</tsl:ServiceDigitalIdentity>");
        x.Append("<tsl:ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/").Append(s.Status).Append("</tsl:ServiceStatus>");
        x.Append("<tsl:StatusStartingTime>").Append(Time(s.Start)).Append("</tsl:StatusStartingTime>");
        x.Append("<tsl:ServiceInformationExtensions>");
        foreach ((string[] qualifiers, string criteria) in s.Qualifications)
        {
            x.Append("<tsl:Extension Critical=\"true\"><ecc:Qualifications><ecc:QualificationElement><ecc:Qualifiers>");
            foreach (string q in qualifiers) x.Append("<ecc:Qualifier uri=\"").Append(Ext).Append(q).Append("\"/>");
            x.Append("</ecc:Qualifiers>").Append(criteria).Append("</ecc:QualificationElement></ecc:Qualifications></tsl:Extension>");
        }

        foreach (string a in s.Additional) x.Append("<tsl:Extension Critical=\"true\"><tsl:AdditionalServiceInformation><tsl:URI xml:lang=\"en\">").Append(Ext).Append(a).Append("</tsl:URI></tsl:AdditionalServiceInformation></tsl:Extension>");
        if (s.Extension is not null) x.Append(s.Extension);
        x.Append("<tsl:Extension Critical=\"false\"><tsl:ExpiredCertsRevocationInfo>2016-06-30T22:00:00Z</tsl:ExpiredCertsRevocationInfo></tsl:Extension>");
        x.Append("</tsl:ServiceInformationExtensions>");
        x.Append("</tsl:").Append(element).Append('>');
        return x.ToString();
    }

    private static string Pointer(string territory, string type, params X509Certificate2[] signers) =>
        "<tsl:OtherTSLPointer><tsl:ServiceDigitalIdentities>"
        + string.Concat(signers.Select(c => "<tsl:ServiceDigitalIdentity><tsl:DigitalId><tsl:X509Certificate>" + B64(c.RawData) + "</tsl:X509Certificate></tsl:DigitalId></tsl:ServiceDigitalIdentity>"))
        + "</tsl:ServiceDigitalIdentities><tsl:TSLLocation>https://tsl.example.test/" + territory + ".xml</tsl:TSLLocation><tsl:AdditionalInformation>"
        + "<tsl:OtherInformation><tsl:TSLType>" + type + "</tsl:TSLType></tsl:OtherInformation>"
        + "<tsl:OtherInformation><tsl:SchemeTerritory>" + territory + "</tsl:SchemeTerritory></tsl:OtherInformation>"
        + "<tsl:OtherInformation><tslx:MimeType>application/vnd.etsi.tsl+xml</tslx:MimeType></tsl:OtherInformation>"
        + "</tsl:AdditionalInformation></tsl:OtherTSLPointer>";

    /// <summary>A list's XML, unsigned. The layout has comments, line breaks and several namespace prefixes, as real lists do.</summary>
    private static string ListXml(string territory, IEnumerable<Svc> services, string type = EuGeneric, string pointers = "", DateTimeOffset? next = null, DateTimeOffset? issued = null)
    {
        var x = new StringBuilder();
        x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        x.Append("<tsl:TrustServiceStatusList xmlns:tsl=\"http://uri.etsi.org/02231/v2#\" xmlns:ecc=\"http://uri.etsi.org/TrstSvc/SvcInfoExt/eSigDir-1999-93-EC-TrustedList/#\"");
        x.Append(" xmlns:xades=\"http://uri.etsi.org/01903/v1.3.2#\" xmlns:tslx=\"http://uri.etsi.org/02231/v2/additionaltypes#\" Id=\"TSL-").Append(territory).Append("\" TSLTag=\"http://uri.etsi.org/19612/TSLTag\">\n");
        x.Append("  <!-- synthetic list for tests -->\n");
        x.Append("  <tsl:SchemeInformation>\n    <tsl:TSLVersionIdentifier>6</tsl:TSLVersionIdentifier>\n    <tsl:TSLSequenceNumber>42</tsl:TSLSequenceNumber>\n");
        x.Append("    <tsl:TSLType>").Append(type).Append("</tsl:TSLType>\n");
        x.Append("    <tsl:SchemeOperatorName><tsl:Name xml:lang=\"fr\">Opérateur d'essai</tsl:Name><tsl:Name xml:lang=\"en\">Test Operator &amp; Co</tsl:Name></tsl:SchemeOperatorName>\n");
        x.Append("    <tsl:SchemeTerritory>").Append(territory).Append("</tsl:SchemeTerritory>\n");
        if (pointers.Length > 0) x.Append("    <tsl:PointersToOtherTSL>").Append(pointers).Append("</tsl:PointersToOtherTSL>\n");
        x.Append("    <tsl:ListIssueDateTime>").Append(Time(issued ?? Now.AddHours(-1))).Append("</tsl:ListIssueDateTime>\n");
        x.Append("    <tsl:NextUpdate><tsl:dateTime>").Append(Time(next ?? Now.AddMonths(6))).Append("</tsl:dateTime></tsl:NextUpdate>\n");
        x.Append("  </tsl:SchemeInformation>\n");
        x.Append("  <tsl:TrustServiceProviderList>\n    <tsl:TrustServiceProvider>\n");
        x.Append("      <tsl:TSPInformation><tsl:TSPName><tsl:Name xml:lang=\"en\">Test Provider</tsl:Name></tsl:TSPName><tsl:TSPTradeName><tsl:Name xml:lang=\"en\">VATXX-0000000000</tsl:Name></tsl:TSPTradeName></tsl:TSPInformation>\n");
        x.Append("      <tsl:TSPServices>\n");
        foreach (Svc s in services)
        {
            x.Append("        <tsl:TSPService>").Append(Information(s, "ServiceInformation"));
            if (s.History.Count > 0) x.Append("<tsl:ServiceHistory>").Append(string.Concat(s.History.Select(h => Information(h, "ServiceHistoryInstance")))).Append("</tsl:ServiceHistory>");
            x.Append("</tsl:TSPService>\n");
        }

        x.Append("      </tsl:TSPServices>\n    </tsl:TrustServiceProvider>\n  </tsl:TrustServiceProviderList>\n</tsl:TrustServiceStatusList>\n");
        return x.ToString();
    }

    /// <summary>Signs a list as trusted lists are signed: an enveloped signature on the whole document, with Microsoft's SignedXml.</summary>
    private static byte[] Sign(string xml, X509Certificate2 signer, bool exclusive = true, bool signedProperties = false)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        using RSA key = signer.GetRSAPrivateKey()!;
        var signed = new PropertiesSignedXml(doc.DocumentElement!) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = exclusive ? SignedXml.XmlDsigExcC14NTransformUrl : SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var whole = new Reference("") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        whole.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        whole.AddTransform(exclusive ? new XmlDsigExcC14NTransform() : new XmlDsigC14NTransform());
        signed.AddReference(whole);
        if (signedProperties)
        {
            // XAdES signed properties with the signing time, in an Object, referenced by identifier.
            var properties = new XmlDocument();
            properties.LoadXml("<xades:QualifyingProperties xmlns:xades=\"http://uri.etsi.org/01903/v1.3.2#\"><xades:SignedProperties Id=\"signed-properties\">"
                + "<xades:SignedSignatureProperties><xades:SigningTime>" + Time(Now) + "</xades:SigningTime></xades:SignedSignatureProperties></xades:SignedProperties></xades:QualifyingProperties>");
            signed.AddObject(new DataObject { Data = properties.ChildNodes });
            signed.Properties = properties;
            var props = new Reference("#signed-properties") { DigestMethod = SignedXml.XmlDsigSHA512Url };
            props.AddTransform(new XmlDsigExcC14NTransform());
            signed.AddReference(props);
        }

        var info = new KeyInfo();
        info.AddClause(new KeyInfoX509Data(signer));
        signed.KeyInfo = info;
        signed.ComputeSignature();
        doc.DocumentElement!.AppendChild(doc.ImportNode(signed.GetXml(), true));
        return Encoding.UTF8.GetBytes(doc.OuterXml);
    }

    /// <summary>
    /// Signs with a ds: prefixed signature built by hand, for algorithms SignedXml does not offer.
    /// Both canonical forms come from the package's transforms, never from the library.
    /// </summary>
    private static byte[] SignByHand(string xml, Func<byte[], byte[]> sign, string signatureMethod, string c14n, string digestMethod = "http://www.w3.org/2001/04/xmlenc#sha256")
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        Transform Make() => c14n switch
        {
            SignedXml.XmlDsigExcC14NTransformUrl => new XmlDsigExcC14NTransform(),
            SignedXml.XmlDsigC14NTransformUrl => new XmlDsigC14NTransform(),
            _ => throw new ArgumentException(c14n),
        };

        Transform document = Make();
        document.LoadInput(doc);
        byte[] digest = TestPki.Sha256(((Stream)document.GetOutput(typeof(Stream))).ReadAllBytes());

        const string Ds = "http://www.w3.org/2000/09/xmldsig#";
        string signedInfo = "<ds:SignedInfo><ds:CanonicalizationMethod Algorithm=\"" + c14n + "\"/><ds:SignatureMethod Algorithm=\"" + signatureMethod + "\"/>"
            + "<ds:Reference URI=\"\"><ds:Transforms><ds:Transform Algorithm=\"http://www.w3.org/2000/09/xmldsig#enveloped-signature\"/><ds:Transform Algorithm=\"" + c14n + "\"/></ds:Transforms>"
            + "<ds:DigestMethod Algorithm=\"" + digestMethod + "\"/><ds:DigestValue>" + B64(digest) + "</ds:DigestValue></ds:Reference></ds:SignedInfo>";

        // SignedInfo canonicalized on its own, carrying the namespaces in scope where it will sit.
        var standalone = new XmlDocument { PreserveWhitespace = true };
        string scope = string.Concat(doc.DocumentElement!.Attributes.Cast<XmlAttribute>().Where(a => a.Prefix == "xmlns").Select(a => " " + a.Name + "=\"" + a.Value + "\""));
        standalone.LoadXml(signedInfo.Replace("<ds:SignedInfo>", "<ds:SignedInfo xmlns:ds=\"" + Ds + "\"" + scope + ">"));
        Transform info = Make();
        info.LoadInput(standalone);
        byte[] value = sign(((Stream)info.GetOutput(typeof(Stream))).ReadAllBytes());

        var signature = new XmlDocument { PreserveWhitespace = true };
        signature.LoadXml("<ds:Signature xmlns:ds=\"" + Ds + "\" Id=\"sig\">" + signedInfo + "<ds:SignatureValue>" + B64(value) + "</ds:SignatureValue></ds:Signature>");
        doc.DocumentElement!.AppendChild(doc.ImportNode(signature.DocumentElement!, true));
        return Encoding.UTF8.GetBytes(doc.OuterXml);
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, string name = "Qualified Signer", DateTimeOffset? notBefore = null, params byte[][] statements)
    {
        var extensions = new List<X509Extension> { new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true) };
        if (statements.Length > 0) extensions.Add(new X509Extension("1.3.6.1.5.5.7.1.3", Der.Seq(statements), false));
        extensions.Add(new X509Extension("2.5.29.32", Der.Seq(Der.Seq(Der.Oid(Policy))), false));
        return TestPki.Issue(name, issuer, extensions, notBefore ?? Now.AddDays(-30));
    }

    private static byte[] Statement(string id, byte[]? info = null) => info is null ? Der.Seq(Der.Oid(id)) : Der.Seq(Der.Oid(id), info);

    private static readonly byte[] QcCompliance = Statement(Etsi + "1");
    private static readonly byte[] QcSscd = Statement(Etsi + "4");
    private static readonly byte[] ForSignatures = Statement(Etsi + "6", Der.Seq(Der.Oid(Etsi + "6.1")));
    private static readonly byte[] ForSeals = Statement(Etsi + "6", Der.Seq(Der.Oid(Etsi + "6.2")));

    private static PdfTrustedList National(params Svc[] services) => PdfTrustedList.Load(Sign(ListXml("XX", services), NationalSigner.Value), [NationalSigner.Value]);

    private static PdfTrustedListQualification Qualify(X509Certificate2 leaf, X509Certificate2 issuer, params PdfTrustedList[] lists) => Validate(leaf, issuer, lists).TrustedListQualification!;

    private static PdfCertificateValidation Validate(X509Certificate2 leaf, X509Certificate2 issuer, PdfTrustedList[] lists, bool asAnchors = true, DateTimeOffset? at = null) =>
        new CertificateValidator(new PdfValidationOptions { TrustedLists = lists, CheckRevocation = false, TrustedListsAsTrustAnchors = asAnchors }, new SecurityStore())
            .Validate(leaf, [issuer], CertificateRole.Signer, at ?? Now, PdfValidationTimeSource.Caller);

    private static string Why(PdfTrustedListQualification q) => string.Join("\n", q.Reasons);

    [TestMethod]
    public void ReadsTheListOfListsAndANationalList()
    {
        X509Certificate2 second = TestPki.Issue("National List Signer 2", null, []);
        string pointers = Pointer("XX", EuGeneric, NationalSigner.Value, second) + Pointer("EU", EuLotl, LotlSigner.Value);
        PdfTrustedList lotl = PdfTrustedList.Load(Sign(ListXml("EU", [], EuLotl, pointers), LotlSigner.Value), [LotlSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Passed, lotl.Signature, lotl.SignatureProblem);
        Assert.IsTrue(lotl.IsListOfLists);
        Assert.AreEqual("EU", lotl.Territory);
        Assert.HasCount(2, lotl.Pointers);
        PdfTrustedListPointer pointer = lotl.Pointers[0];
        Assert.AreEqual("XX", pointer.Territory);
        Assert.AreEqual(EuGeneric, pointer.Type);
        Assert.AreEqual("application/vnd.etsi.tsl+xml", pointer.MimeType);
        Assert.AreEqual("https://tsl.example.test/XX.xml", pointer.Location);
        Assert.HasCount(2, pointer.Certificates);

        X509Certificate2 old = TestPki.Issue("Old CA Key", null, TestPki.Ca(null));
        var service = new Svc
        {
            Certificate = Ca.Value,
            Qualifications = [(["QCWithQSCD", "QCForESig"], KeyUsage())],
            Additional = ["ForeSignatures"],
            History = [new Svc { Status = "undersupervision", Start = Now.AddYears(-12), Ski = SubjectKeyIdentifier(old) }],
        };
        byte[] xml = Sign(ListXml("XX", [service, new Svc { Type = PdfTrustServiceTypes.TsaQtst, Name = "Test QTST", Certificate = old }]), second);
        PdfTrustedList national = PdfTrustedList.Load(xml, lotl);
        Assert.AreEqual(PdfCheckStatus.Passed, national.Signature, national.SignatureProblem);
        Assert.AreEqual(second.Thumbprint, national.SignerCertificate!.Thumbprint);
        Assert.AreEqual(6, national.Version);
        Assert.AreEqual(42, national.SequenceNumber);
        Assert.AreEqual(EuGeneric, national.Type);
        Assert.IsFalse(national.IsListOfLists);
        Assert.AreEqual("Test Operator & Co", national.OperatorName);
        Assert.AreEqual(Time(Now.AddHours(-1)), Time(national.IssueDate));
        Assert.AreEqual(Time(Now.AddMonths(6)), Time(national.NextUpdate!.Value));
        PdfTrustServiceProvider provider = national.Providers.Single();
        Assert.AreEqual("Test Provider", provider.Name);
        CollectionAssert.AreEqual(new[] { "VATXX-0000000000" }, provider.TradeNames.ToArray());
        Assert.HasCount(2, national.Services.ToList());

        PdfTrustService ca = provider.Services[0];
        Assert.AreSame(provider, ca.Provider);
        Assert.AreEqual(PdfTrustServiceTypes.CaQc, ca.TypeIdentifier);
        Assert.AreEqual(PdfTrustServiceStatus.Granted, ca.Status);
        Assert.IsTrue(ca.IsQualifiedStatus);
        Assert.AreEqual(Ca.Value.Thumbprint, ca.Certificates.Single().Thumbprint);
        CollectionAssert.AreEqual(new[] { Ext + "ForeSignatures" }, ca.AdditionalServiceInformation.ToArray());
        CollectionAssert.AreEqual(new[] { Ext + "QCWithQSCD", Ext + "QCForESig" }, ca.Qualifications.Single().Qualifiers.ToArray());
        Assert.IsEmpty(ca.UnknownCriticalExtensions);
        PdfTrustServiceInformation history = ca.History.Single();
        Assert.AreEqual(PdfTrustServiceStatus.UnderSupervision, history.Status);
        Assert.IsTrue(history.IsQualifiedStatus);
        CollectionAssert.AreEqual(SubjectKeyIdentifier(old), history.SubjectKeyIdentifiers.Single());
        CollectionAssert.AreEqual(new[] { "CN=Old Name" }, history.SubjectNames.ToArray());

        // The entry in force: none before the history, the history, then the current information.
        Assert.IsNull(ca.At(Now.AddYears(-20)));
        Assert.AreSame(history, ca.At(Now.AddYears(-5)));
        Assert.AreSame(ca, ca.At(Now));

        // The qualification criteria are public too.
        Assert.IsTrue(ca.Qualifications[0].Applies(Leaf(Ca.Value)));
    }

    [TestMethod]
    public void TheSignatureMustCoverTheListAndComeFromASuppliedCertificate()
    {
        string xml = ListXml("XX", [new Svc { Certificate = Ca.Value }]);
        byte[] signed = Sign(xml, NationalSigner.Value);
        Assert.AreEqual(PdfCheckStatus.Passed, PdfTrustedList.Load(signed, [NationalSigner.Value]).Signature);

        // Changed after signing: a service name.
        byte[] changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed).Replace("Test Qualified CA", "Test Qualified CB"));
        PdfTrustedList tampered = PdfTrustedList.Load(changed, [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Failed, tampered.Signature);
        StringAssert.Contains(tampered.SignatureProblem, "digest");

        // Another certificate, no certificate, no signature.
        X509Certificate2 stranger = TestPki.Issue("Stranger", null, []);
        PdfTrustedList wrong = PdfTrustedList.Load(signed, [stranger]);
        Assert.AreEqual(PdfCheckStatus.Failed, wrong.Signature);
        StringAssert.Contains(wrong.SignatureProblem, "any certificate supplied");
        Assert.AreEqual(PdfCheckStatus.NotChecked, PdfTrustedList.Load(signed, []).Signature);
        PdfTrustedList unsigned = PdfTrustedList.Load(Encoding.UTF8.GetBytes(xml), [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Failed, unsigned.Signature);
        StringAssert.Contains(unsigned.SignatureProblem, "not signed");

        // The same signature, with the list's content wrapped away and a copy of it pointed to by identifier.
        PdfTrustedList wrapped = PdfTrustedList.Load(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed).Replace("<tsl:SchemeInformation>", "<tsl:SchemeInformation Id=\"TSL-XX\">")), [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Failed, wrapped.Signature);
        StringAssert.Contains(wrapped.SignatureProblem, "share the identifier");

        // A list issued after its signer's certificate expired.
        X509Certificate2 expired = TestPki.Issue("Expired List Signer", null, [], Now.AddYears(-3), Now.AddYears(-2));
        PdfTrustedList late = PdfTrustedList.Load(Sign(xml, expired), [expired]);
        Assert.AreEqual(PdfCheckStatus.Failed, late.Signature);
        StringAssert.Contains(late.SignatureProblem, "validity");

        // A closed list issued long ago and signed again now: the signed XAdES signing time counts, not the issue date.
        string closed = ListXml("XX", [], issued: Now.AddYears(-6));
        Assert.AreEqual(PdfCheckStatus.Failed, PdfTrustedList.Load(Sign(closed, NationalSigner.Value), [NationalSigner.Value]).Signature);
        PdfTrustedList resigned = PdfTrustedList.Load(Sign(closed, NationalSigner.Value, signedProperties: true), [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Passed, resigned.Signature, resigned.SignatureProblem);
    }

    [TestMethod]
    public void ANationalListNeedsAVerifiedPointer()
    {
        PdfTrustedList lotl = PdfTrustedList.Load(Sign(ListXml("EU", [], EuLotl, Pointer("XX", EuGeneric, NationalSigner.Value)), LotlSigner.Value), [LotlSigner.Value]);
        X509Certificate2 rogue = TestPki.Issue("Rogue List Signer", null, []);
        PdfTrustedList forged = PdfTrustedList.Load(Sign(ListXml("XX", [new Svc { Certificate = Ca.Value }]), rogue), lotl);
        Assert.AreEqual(PdfCheckStatus.Failed, forged.Signature);

        PdfTrustedList other = PdfTrustedList.Load(Sign(ListXml("YY", []), NationalSigner.Value), lotl);
        Assert.AreEqual(PdfCheckStatus.Failed, other.Signature);
        StringAssert.Contains(other.SignatureProblem, "no pointer");

        PdfTrustedList unverifiedLotl = PdfTrustedList.Load(Sign(ListXml("EU", [], EuLotl, Pointer("XX", EuGeneric, NationalSigner.Value)), LotlSigner.Value), [rogue]);
        PdfTrustedList national = PdfTrustedList.Load(Sign(ListXml("XX", []), NationalSigner.Value), unverifiedLotl);
        Assert.AreEqual(PdfCheckStatus.Failed, national.Signature);
        StringAssert.Contains(national.SignatureProblem, "list of lists");

        using var stream = new MemoryStream(Sign(ListXml("XX", []), NationalSigner.Value));
        Assert.AreEqual(PdfCheckStatus.Passed, PdfTrustedList.Load(stream, lotl).Signature);
    }

    [TestMethod]
    public void TheSignatureFormsOfTrustedListsVerify()
    {
        string xml = ListXml("XX", [new Svc { Certificate = Ca.Value }]);

        // Inclusive canonicalization of SignedInfo puts the root's namespaces on it; a second reference points into the signature.
        Assert.AreEqual(PdfCheckStatus.Passed, PdfTrustedList.Load(Sign(xml, NationalSigner.Value, exclusive: false, signedProperties: true), [NationalSigner.Value]).Signature);
        Assert.AreEqual(PdfCheckStatus.Passed, PdfTrustedList.Load(Sign(xml, NationalSigner.Value, exclusive: true, signedProperties: true), [NationalSigner.Value]).Signature);

        // ECDSA P-256 with a ds: prefix, values as r || s.
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=EC List Signer", ec, HashAlgorithmName.SHA256);
        using X509Certificate2 ecSigner = request.CreateSelfSigned(Now.AddDays(-10), Now.AddDays(10));
        byte[] ecList = SignByHand(xml, d => ec.SignData(d, HashAlgorithmName.SHA256), "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256", SignedXml.XmlDsigExcC14NTransformUrl);
        PdfTrustedList ecLoaded = PdfTrustedList.Load(ecList, [NationalSigner.Value, ecSigner]);
        Assert.AreEqual(PdfCheckStatus.Passed, ecLoaded.Signature, ecLoaded.SignatureProblem);
        Assert.AreSame(ecSigner, ecLoaded.SignerCertificate);

        // RSA-PSS under inclusive canonicalization.
        using RSA rsa = NationalSigner.Value.GetRSAPrivateKey()!;
        byte[] pss = SignByHand(xml, d => rsa.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1", SignedXml.XmlDsigC14NTransformUrl);
        PdfTrustedList pssLoaded = PdfTrustedList.Load(pss, [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Passed, pssLoaded.Signature, pssLoaded.SignatureProblem);

        // RSA-PSS with a salt longer than the hash, from a certificate whose key is id-RSASSA-PSS, as one national list is signed.
        using RSA pssKey = RSA.Create(2048);
        X509Certificate2 pssCertificate = PssKeyCertificate(pssKey);
        Assert.AreEqual("1.2.840.113549.1.1.10", pssCertificate.PublicKey.Oid.Value);
        byte[] salted = SignByHand(xml, d => PssSign(pssKey, d, 64), "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1", SignedXml.XmlDsigExcC14NTransformUrl);
        PdfTrustedList saltedLoaded = PdfTrustedList.Load(salted, [pssCertificate]);
        Assert.AreEqual(PdfCheckStatus.Passed, saltedLoaded.Signature, saltedLoaded.SignatureProblem);
        byte[] forged = SignByHand(xml, d => PssSign(pssKey, [.. d, 0], 64), "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1", SignedXml.XmlDsigExcC14NTransformUrl);
        Assert.AreEqual(PdfCheckStatus.Failed, PdfTrustedList.Load(forged, [pssCertificate]).Signature);

        // SHA-1 digests are refused.
        byte[] sha1 = SignByHand(xml, d => rsa.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", SignedXml.XmlDsigExcC14NTransformUrl, "http://www.w3.org/2000/09/xmldsig#sha1");
        PdfTrustedList refused = PdfTrustedList.Load(sha1, [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Failed, refused.Signature);
        StringAssert.Contains(refused.SignatureProblem, "not supported");
    }

    [TestMethod]
    public void CanonicalizationMatchesTheReferenceImplementation()
    {
        const string xml = "<?xml version=\"1.0\"?>\n<?before data?>\n<!-- before -->\n"
            + "<root xmlns=\"urn:default\" xmlns:a=\"urn:a\" xmlns:unused=\"urn:unused\" xml:lang=\"en\" b:attr=\"1\" xmlns:b=\"urn:b\" z=\"last\" a:y=\"2\" plain=\"&lt;&amp;&quot;&#9;&#10;&#13;'&gt;\">\n"
            + "  <a:child xmlns:a=\"urn:a\" xmlns:c=\"urn:c\">text &amp; &lt; &gt; &#13; é中\U0001F600 <![CDATA[<cdata> & ]]></a:child>\n"
            + "  <empty/>\n  <reset xmlns=\"\"><inner a:q=\"x\" xmlns:a=\"urn:a2\"/><a:back xmlns=\"urn:default\"/></reset>\n"
            + "  <!-- inside --><?pi inside?>\n  <unused:none/>\n</root>\n<!-- after -->\n<?after?>";
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        foreach ((Transform reference, XmlCanonicalization method, bool comments) in new (Transform, XmlCanonicalization, bool)[]
        {
            (new XmlDsigC14NTransform(false), XmlCanonicalization.Inclusive, false),
            (new XmlDsigC14NTransform(true), XmlCanonicalization.Inclusive, true),
            (new XmlDsigExcC14NTransform(false), XmlCanonicalization.Exclusive, false),
            (new XmlDsigExcC14NTransform(true), XmlCanonicalization.Exclusive, true),
            (new XmlDsigExcC14NTransform(false, "b unused"), XmlCanonicalization.Exclusive, false),
        })
        {
            reference.LoadInput(doc);
            string expected = Encoding.UTF8.GetString(((Stream)reference.GetOutput(typeof(Stream))).ReadAllBytes());
            IReadOnlyCollection<string>? prefixes = reference is XmlDsigExcC14NTransform { InclusiveNamespacesPrefixList: { Length: > 0 } list } ? list.Split(' ') : null;
            string actual = Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(doc, null, method, comments, prefixes));
            Assert.AreEqual(expected, actual, method + (comments ? " with comments" : ""));
        }

        // A subtree under inclusive canonicalization carries the namespaces and xml: attributes in scope; exclusive only what it uses.
        var child = (XmlElement)doc.DocumentElement!.ChildNodes[1]!;
        Assert.AreEqual("<a:child xmlns=\"urn:default\" xmlns:a=\"urn:a\" xmlns:b=\"urn:b\" xmlns:c=\"urn:c\" xmlns:unused=\"urn:unused\" xml:lang=\"en\">text &amp; &lt; &gt; &#xD; é中\U0001F600 &lt;cdata&gt; &amp; </a:child>",
            Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(child, null, XmlCanonicalization.Inclusive, false)));
        Assert.AreEqual("<a:child xmlns:a=\"urn:a\">text &amp; &lt; &gt; &#xD; é中\U0001F600 &lt;cdata&gt; &amp; </a:child>",
            Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(child, null, XmlCanonicalization.Exclusive, false)));
        Assert.AreEqual("<a:child xmlns=\"urn:default\" xmlns:a=\"urn:a\" xmlns:b=\"urn:b\" xmlns:c=\"urn:c\" xmlns:unused=\"urn:unused\" xml:lang=\"en\">text &amp; &lt; &gt; &#xD; é中\U0001F600 &lt;cdata&gt; &amp; </a:child>",
            Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(child, null, XmlCanonicalization.Inclusive11, false)));

        // Version 1.1 does not resolve xml:base for a subtree, so it refuses one below it.
        var based = new XmlDocument();
        based.LoadXml("<a xml:base=\"http://example.test/\"><b/></a>");
        Assert.ThrowsExactly<FormatException>(() => XmlCanonicalizer.Canonicalize(based.DocumentElement!.FirstChild!, null, XmlCanonicalization.Inclusive11, false));
    }

    [TestMethod]
    public void HostileListsFailAsFormatErrors()
    {
        string[] bad =
        [
            "<!DOCTYPE x [<!ENTITY e \"boom\">]><x>&e;</x>",
            "<!DOCTYPE x SYSTEM \"http://example.test/x.dtd\"><x/>",
            "<?xml version=\"1.0\" encoding=\"x-unknown-charset\"?><a/>",
            "<tsl:TrustServiceStatusList xmlns:tsl=\"http://uri.etsi.org/02231/v2#\">",
            "<other/>",
            "<tsl:TrustServiceStatusList xmlns:tsl=\"http://uri.etsi.org/02231/v2#\"/>",
            ListXml("XX", []).Replace(Time(Now.AddHours(-1)), "yesterday"),
            ListXml("XX", []).Replace("<tsl:TSLSequenceNumber>42<", "<tsl:TSLSequenceNumber>99999999999<"),
            ListXml("XX", []).Replace("<tsl:SchemeTerritory>XX</tsl:SchemeTerritory>", ""),
            ListXml("XX", [new Svc { Certificate = Ca.Value }]).Replace("Critical=\"false\"", "Critical=\"maybe\""),
            string.Concat(Enumerable.Repeat("<a>", 100)) + string.Concat(Enumerable.Repeat("</a>", 100)),
            "",
        ];
        foreach (string xml in bad)
        {
            Assert.ThrowsExactly<FormatException>(() => PdfTrustedList.Load(Encoding.UTF8.GetBytes(xml), [NationalSigner.Value]), xml.Length > 60 ? xml.Substring(0, 60) : xml);
        }

        Assert.ThrowsExactly<FormatException>(() => PdfTrustedList.Load(new EndlessStream(), [NationalSigner.Value]));

        // Truncated signed lists fail as format errors at every cut.
        byte[] signed = Sign(ListXml("XX", [new Svc { Certificate = Ca.Value }]), NationalSigner.Value);
        for (int cut = 0; cut < signed.Length - 1; cut += 997)
        {
            Assert.ThrowsExactly<FormatException>(() => PdfTrustedList.Load(signed.AsSpan(0, cut).ToArray(), [NationalSigner.Value]));
        }

        // A damaged signature is a failed check, not an exception.
        string text = Encoding.UTF8.GetString(signed);
        int at = text.IndexOf("<SignatureValue>", StringComparison.Ordinal) + "<SignatureValue>".Length;
        PdfTrustedList damaged = PdfTrustedList.Load(Encoding.UTF8.GetBytes(text.Remove(at, 2).Insert(at, "!!")), [NationalSigner.Value]);
        Assert.AreEqual(PdfCheckStatus.Failed, damaged.Signature);
        StringAssert.Contains(damaged.SignatureProblem, "base64");
    }

    [TestMethod]
    public void AGrantedServiceConfirmsWhatTheCertificateClaims()
    {
        PdfTrustedList list = National(new Svc { Certificate = Ca.Value, Additional = ["ForeSignatures", "ForeSeals"] });
        X509Certificate2 leaf = Leaf(Ca.Value, statements: [QcCompliance, QcSscd, ForSignatures]);
        PdfCertificateValidation v = Validate(leaf, Ca.Value, [list]);
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, string.Join("\n", v.Problems));
        Assert.AreEqual(PdfCheckStatus.Passed, v.Chain);
        PdfTrustedListQualification q = v.TrustedListQualification!;
        Assert.AreEqual(PdfQualification.QESig, q.Qualification, Why(q));
        Assert.AreEqual(PdfQualification.QESig, q.AtIssuance);
        Assert.AreSame(list.Services.Single(), q.Service);
        StringAssert.Contains(Why(q), "Found the CA/QC service \"Test Qualified CA\"");

        // A seal certificate from the same service; one that claims nothing.
        Assert.AreEqual(PdfQualification.AdESealQC, Qualify(Leaf(Ca.Value, statements: [QcCompliance, ForSeals]), Ca.Value, list).Qualification);
        PdfTrustedListQualification plain = Qualify(Leaf(Ca.Value), Ca.Value, list);
        Assert.AreEqual(PdfQualification.AdES, plain.Qualification);
        StringAssert.Contains(Why(plain), "no QcCompliance");

        // Without the lists as anchors, and without other anchors, the chain is not checked; the qualification still is.
        PdfCertificateValidation unanchored = Validate(leaf, Ca.Value, [list], asAnchors: false);
        Assert.AreEqual(PdfCheckStatus.NotChecked, unanchored.Chain);
        Assert.AreEqual(PdfQualification.QESig, unanchored.TrustedListQualification!.Qualification);

        // No lists, no confirmation.
        Assert.IsNull(new CertificateValidator(new PdfValidationOptions { CheckRevocation = false }, new SecurityStore())
            .Validate(leaf, [Ca.Value], CertificateRole.Signer, Now, PdfValidationTimeSource.Current).TrustedListQualification);
    }

    [TestMethod]
    public void TheStatusAtIssuanceAndAtSigningBothCount()
    {
        X509Certificate2 leaf = Leaf(Ca.Value, notBefore: Now.AddDays(-30), statements: [QcCompliance, QcSscd]);

        // Withdrawn after issuance: qualified at issuance, not at the signing time; a signature made before the withdrawal still is.
        PdfTrustedList withdrawn = National(new Svc { Certificate = Ca.Value, Status = "withdrawn", Start = Now.AddDays(-2), History = [new Svc { Certificate = Ca.Value, Start = Now.AddYears(-3) }] });
        PdfTrustedListQualification late = Qualify(leaf, Ca.Value, withdrawn);
        Assert.AreEqual(PdfQualification.AdES, late.Qualification, Why(late));
        Assert.AreEqual(PdfQualification.QESig, late.AtIssuance);
        StringAssert.Contains(Why(late), "withdrawn");
        Assert.AreEqual(PdfQualification.QESig, Validate(leaf, Ca.Value, [withdrawn], at: Now.AddDays(-5)).TrustedListQualification!.Qualification);

        // Granted only after the certificate was issued.
        PdfTrustedList recent = National(new Svc { Certificate = Ca.Value, Start = Now.AddDays(-3) });
        PdfTrustedListQualification after = Qualify(leaf, Ca.Value, recent);
        Assert.AreEqual(PdfQualification.AdES, after.Qualification);
        Assert.AreEqual(PdfQualification.AdES, after.AtIssuance);
        StringAssert.Contains(Why(after), "no status at issuance");

        // Supervised before eIDAS, granted since: the history decides the issuance.
        X509Certificate2 old = Leaf(Ca.Value, notBefore: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), statements: [QcCompliance]);
        PdfTrustedList transition = National(new Svc
        {
            Certificate = Ca.Value,
            Start = new DateTimeOffset(2016, 6, 30, 22, 0, 0, TimeSpan.Zero),
            History = [new Svc { Certificate = Ca.Value, Status = "undersupervision", Start = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero) }],
        });
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(old, Ca.Value, transition).Qualification);
    }

    [TestMethod]
    public void QualifiersSettleWhatTheCertificateLeavesOpen()
    {
        // The list says the key is in a qualified device and the certificate is for signatures; the certificate claims only compliance.
        X509Certificate2 leaf = Leaf(Ca.Value, statements: [QcCompliance]);
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], KeyUsage())] })).Qualification);

        // Criteria that do not select the certificate leave it as it claims.
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], KeyUsage("keyEncipherment"))] })).Qualification);
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], Policies("all", Policy, "1.2.3.4"))] })).Qualification);
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], Policies("all", Policy))] })).Qualification);
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], Policies("none", Policy))] })).Qualification);

        // QCStatement makes a certificate without QcCompliance qualified; NotQualified overrides QcCompliance.
        X509Certificate2 silent = Leaf(Ca.Value);
        Assert.AreEqual(PdfQualification.QESeal, Qualify(silent, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCStatement", "QCForESeal", "QCQSCDManagedOnBehalf"], KeyUsage())] })).Qualification);
        X509Certificate2 claims = Leaf(Ca.Value, statements: [QcCompliance, QcSscd]);
        Assert.AreEqual(PdfQualification.AdES, Qualify(claims, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["NotQualified"], KeyUsage())] })).Qualification);
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(claims, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCNoQSCD"], KeyUsage())] })).Qualification);

        // A service qualified for seals only does not make a signature certificate qualified.
        PdfTrustedListQualification seals = Qualify(claims, Ca.Value, National(new Svc { Certificate = Ca.Value, Additional = ["ForeSeals"] }));
        Assert.AreEqual(PdfQualification.AdES, seals.Qualification);
        StringAssert.Contains(Why(seals), "not qualified to issue");

        // Nested criteria lists and the other criteria of the specification.
        string nested = "<ecc:CriteriaList assert=\"all\">" + KeyUsage() + "<ecc:CriteriaList assert=\"atLeastOne\"><ecc:otherCriteriaList>"
            + "<tslx:CertSubjectDNAttribute><tslx:AttributeOID><xades:Identifier Qualifier=\"OIDAsURN\">urn:oid:2.5.4.10</xades:Identifier></tslx:AttributeOID></tslx:CertSubjectDNAttribute>"
            + "</ecc:otherCriteriaList></ecc:CriteriaList></ecc:CriteriaList>";
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], nested)] })).Qualification);
        string usage = "<ecc:CriteriaList assert=\"all\"><ecc:otherCriteriaList><tslx:ExtendedKeyUsage><tslx:KeyPurposeId><xades:Identifier>1.3.6.1.5.5.7.3.4</xades:Identifier></tslx:KeyPurposeId></tslx:ExtendedKeyUsage></ecc:otherCriteriaList></ecc:CriteriaList>";
        Assert.AreEqual(PdfQualification.AdESigQC, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], usage)] })).Qualification);
    }

    [TestMethod]
    public void WhatTheListsCannotDecideIsIndeterminate()
    {
        X509Certificate2 leaf = Leaf(Ca.Value, statements: [QcCompliance, QcSscd]);

        PdfTrustedListQualification conflict = Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], KeyUsage()), (["QCNoQSCD"], KeyUsage("digitalSignature"))] }));
        Assert.AreEqual(PdfQualification.Indeterminate, conflict.Qualification);
        StringAssert.Contains(Why(conflict), "conflicting qualifiers");
        Assert.AreEqual(PdfQualification.Indeterminate, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCForESig", "QCForESeal"], KeyUsage())] })).Qualification);

        PdfTrustedListQualification unknown = Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCWithQSCD"], "<ecc:CriteriaList assert=\"all\"><ecc:Future/></ecc:CriteriaList>")] }));
        Assert.AreEqual(PdfQualification.Indeterminate, unknown.Qualification);
        StringAssert.Contains(Why(unknown), "cannot evaluate");

        // A criterion that is false decides "all" even beside one the library does not know.
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Qualifications = [(["QCNoQSCD"], "<ecc:CriteriaList assert=\"all\">" + KeyUsage("keyEncipherment") + "<ecc:Future/></ecc:CriteriaList>")] })).Qualification);

        PdfTrustedListQualification critical = Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Extension = "<tsl:Extension Critical=\"true\"><tsl:FutureExtension/></tsl:Extension>" }));
        Assert.AreEqual(PdfQualification.Indeterminate, critical.Qualification);
        StringAssert.Contains(Why(critical), "FutureExtension");
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Extension = "<tsl:Extension Critical=\"false\"><tsl:FutureExtension/></tsl:Extension>" })).Qualification);

        // Two granted services for the same CA that disagree.
        PdfTrustedListQualification disagree = Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value }, new Svc { Certificate = Ca.Value, Name = "Other", Qualifications = [(["QCNoQSCD"], KeyUsage())] }));
        Assert.AreEqual(PdfQualification.Indeterminate, disagree.Qualification);
        StringAssert.Contains(Why(disagree), "disagree");

        // A list that cannot be used, and an expired one.
        PdfTrustedList unverified = PdfTrustedList.Load(Sign(ListXml("XX", [new Svc { Certificate = Ca.Value }]), NationalSigner.Value), [LotlSigner.Value]);
        PdfTrustedListQualification none = Qualify(leaf, Ca.Value, unverified);
        Assert.AreEqual(PdfQualification.Indeterminate, none.Qualification);
        StringAssert.Contains(Why(none), "is not used: its signature does not verify");
        PdfTrustedList expired = PdfTrustedList.Load(Sign(ListXml("XX", [new Svc { Certificate = Ca.Value }], next: Now.AddMinutes(-10), issued: Now.AddHours(-2)), NationalSigner.Value), [NationalSigner.Value]);
        PdfTrustedListQualification stale = Qualify(leaf, Ca.Value, expired);
        Assert.AreEqual(PdfQualification.Indeterminate, stale.Qualification);
        StringAssert.Contains(Why(stale), "expired");

        // A usable list beside the unusable one decides.
        Assert.AreEqual(PdfQualification.QESig, Qualify(leaf, Ca.Value, unverified, National(new Svc { Certificate = Ca.Value })).Qualification);
    }

    [TestMethod]
    public void ServicesAreFoundByKeyAndKeyIdentifier()
    {
        X509Certificate2 leaf = Leaf(Ca.Value, statements: [QcCompliance, QcSscd]);

        // Not listed: not an EU qualified certificate.
        X509Certificate2 otherCa = TestPki.Issue("Other CA", null, TestPki.Ca(null), Now.AddYears(-5));
        PdfTrustedListQualification absent = Qualify(leaf, Ca.Value, National(new Svc { Certificate = otherCa }));
        Assert.AreEqual(PdfQualification.AdES, absent.Qualification);
        StringAssert.Contains(Why(absent), "No CA/QC service");

        // Listed under a non qualified type only.
        Assert.AreEqual(PdfQualification.AdES, Qualify(leaf, Ca.Value, National(new Svc { Certificate = Ca.Value, Type = PdfTrustServiceTypes.CaPkc })).Qualification);

        // By subject key identifier alone, as history entries often name a CA; the CA is then not an anchor, so give it as one.
        PdfTrustedList byKeyId = National(new Svc { Ski = SubjectKeyIdentifier(Ca.Value), Certificate = otherCa });
        var options = new PdfValidationOptions { TrustAnchors = [Ca.Value], TrustedLists = [byKeyId], CheckRevocation = false };
        PdfCertificateValidation v = new CertificateValidator(options, new SecurityStore()).Validate(leaf, [], CertificateRole.Signer, Now, PdfValidationTimeSource.Caller);
        Assert.AreEqual(PdfCheckStatus.Passed, v.Chain);
        Assert.AreEqual(PdfQualification.QESig, v.TrustedListQualification!.Qualification, Why(v.TrustedListQualification));

        // The service may list a CA above the one that issued the certificate.
        X509Certificate2 intermediate = TestPki.Issue("Issuing CA", Ca.Value, TestPki.Ca(0), Now.AddYears(-1));
        X509Certificate2 deep = Leaf(intermediate, statements: [QcCompliance, QcSscd]);
        PdfCertificateValidation chained = new CertificateValidator(new PdfValidationOptions { TrustedLists = [National(new Svc { Certificate = Ca.Value })], CheckRevocation = false }, new SecurityStore())
            .Validate(deep, [intermediate], CertificateRole.Signer, Now, PdfValidationTimeSource.Caller);
        Assert.AreEqual(PdfCheckStatus.Passed, chained.Status, string.Join("\n", chained.Problems));
        Assert.AreEqual(PdfQualification.QESig, chained.TrustedListQualification!.Qualification);

        // A self-signed certificate, and one whose issuer is missing.
        X509Certificate2 self = TestPki.Issue("Self", null, []);
        Assert.AreEqual(PdfQualification.AdES, Qualify(self, self, National(new Svc { Certificate = Ca.Value })).Qualification);
        PdfTrustedListQualification orphan = new CertificateValidator(new PdfValidationOptions { TrustedLists = [National(new Svc { Certificate = Ca.Value })], CheckRevocation = false }, new SecurityStore())
            .Validate(deep, [], CertificateRole.Signer, Now, PdfValidationTimeSource.Caller).TrustedListQualification!;
        Assert.AreEqual(PdfQualification.Indeterminate, orphan.Qualification);
    }

    [TestMethod]
    public void VerifyConfirmsTheSignerAndTheTimestampAuthority()
    {
        var pki = new TestPki();
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Contract", PdfStandardFont.Helvetica, 20, PdfColor.Black, 72, 72);
        byte[] signed = PdfSigner.Sign(doc.ToArray(new PdfSaveOptions { Optimize = false }), pki.Signer, new PdfSignatureOptions { Chain = [pki.Intermediate], Timestamp = pki.TimestampClient() });

        // The intermediate is a CA/QC service whose qualifiers make every non-repudiation certificate a QES; the TSA is a QTST.
        PdfTrustedList list = National(
            new Svc { Certificate = pki.Intermediate, Start = Now.AddYears(-2), Qualifications = [(["QCStatement", "QCWithQSCD", "QCForESig"], KeyUsage())] },
            new Svc { Type = PdfTrustServiceTypes.TsaQtst, Name = "Test QTST", Certificate = pki.Tsa, Start = Now.AddYears(-2) });
        var options = new PdfValidationOptions { TrustedLists = [list], CheckRevocation = false };
        PdfSignatureInfo info = PdfSigner.Verify(signed, options).Single();
        Assert.AreEqual(PdfCheckStatus.Passed, info.Validation!.Status, string.Join("\n", info.Validation.Problems));
        Assert.AreEqual(PdfValidationTimeSource.Timestamp, info.Validation.TimeSource);
        Assert.AreEqual(PdfQualification.AdES, info.Validation.Qualification);
        Assert.AreEqual(PdfQualification.QESig, info.Validation.TrustedListQualification!.Qualification, Why(info.Validation.TrustedListQualification));
        Assert.AreEqual(PdfCheckStatus.Passed, info.Timestamp!.QualifiedTimestamp, string.Join("\n", info.Timestamp.QualifiedTimestampReasons));
        Assert.AreEqual("Test QTST", info.Timestamp.QualifiedTimestampService!.Name);
        Assert.IsNull(info.Timestamp.Validation!.TrustedListQualification);

        // A TSA the list does not name, or names as withdrawn.
        PdfTrustedList without = National(new Svc { Certificate = pki.Intermediate, Start = Now.AddYears(-2) });
        PdfTimestampInfo unnamed = PdfSigner.Verify(signed, options with { TrustedLists = [without] }).Single().Timestamp!;
        Assert.AreEqual(PdfCheckStatus.Failed, unnamed.QualifiedTimestamp);
        PdfTrustedList withdrawn = National(new Svc { Type = PdfTrustServiceTypes.TsaQtst, Certificate = pki.Tsa, Status = "withdrawn", Start = Now.AddDays(-1), History = [new Svc { Type = PdfTrustServiceTypes.TsaQtst, Certificate = pki.Tsa, Start = Now.AddYears(-2) }] });
        PdfTimestampInfo gone = PdfSigner.Verify(signed, options with { TrustedLists = [withdrawn] }).Single().Timestamp!;
        Assert.AreEqual(PdfCheckStatus.Failed, gone.QualifiedTimestamp);
        StringAssert.Contains(string.Join("\n", gone.QualifiedTimestampReasons), "withdrawn");

        // Without lists nothing is checked.
        PdfTimestampInfo plain = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [pki.Root], CheckRevocation = false }).Single().Timestamp!;
        Assert.AreEqual(PdfCheckStatus.NotChecked, plain.QualifiedTimestamp);
        Assert.IsEmpty(plain.QualifiedTimestampReasons);
    }

    /// <summary>A self-signed certificate whose public key algorithm is id-RSASSA-PSS. Its own signature no longer verifies, which the list check does not need.</summary>
    private static X509Certificate2 PssKeyCertificate(RSA key)
    {
        using X509Certificate2 plain = new CertificateRequest("CN=PSS List Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(Now.AddDays(-10), Now.AddDays(10));
        List<DerNode> certificate = DerNode.Parse(plain.RawData).Children();
        List<DerNode> tbs = certificate[0].Children();
        int spki = tbs[0].Tag == 0xA0 ? 6 : 5;
        byte[] pssSpki = Der.Seq(Der.Seq(Der.Oid("1.2.840.113549.1.1.10")), tbs[spki].At(1).Encoded.ToArray());
        byte[] newTbs = Der.Seq([.. tbs.Select((t, i) => i == spki ? pssSpki : t.Encoded.ToArray())]);
        return Pkix.Load(Der.Seq(newTbs, certificate[1].Encoded.ToArray(), certificate[2].Encoded.ToArray()));
    }

    /// <summary>RSASSA-PSS with SHA-256, MGF1 and a chosen salt length (RFC 8017, 9.1.1), computed here because .NET fixes the salt length.</summary>
    private static byte[] PssSign(RSA key, byte[] data, int saltLength)
    {
        RSAParameters p = key.ExportParameters(true);
        int emLength = p.Modulus!.Length, hashLength = 32;
        byte[] salt = new byte[saltLength];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
        byte[] h = TestPki.Sha256([.. new byte[8], .. TestPki.Sha256(data), .. salt]);
        var db = new byte[emLength - hashLength - 1];
        db[db.Length - saltLength - 1] = 1;
        salt.CopyTo(db, db.Length - saltLength);
        byte[] mask = new byte[db.Length];
        for (int counter = 0, at = 0; at < mask.Length; counter++, at += hashLength)
        {
            byte[] chunk = TestPki.Sha256([.. h, (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter]);
            Array.Copy(chunk, 0, mask, at, Math.Min(hashLength, mask.Length - at));
        }

        for (int i = 0; i < db.Length; i++) db[i] ^= mask[i];
        db[0] &= 0x7F;
        byte[] em = [.. db, .. h, 0xBC];
        System.Numerics.BigInteger s = System.Numerics.BigInteger.ModPow(Unsigned(em), Unsigned(p.D!), Unsigned(p.Modulus));
        byte[] little = s.ToByteArray();
        var signature = new byte[emLength];
        for (int i = 0; i < Math.Min(little.Length, emLength); i++) signature[emLength - 1 - i] = little[i];
        return signature;

        static System.Numerics.BigInteger Unsigned(byte[] bigEndian) => new([.. Enumerable.Reverse(bigEndian), 0]);
    }

    private static byte[] SubjectKeyIdentifier(X509Certificate2 certificate) => DerNode.Parse(certificate.Extensions["2.5.29.14"]!.RawData).Content.ToArray();

    /// <summary>SignedXml that also finds identifiers inside the signed properties it has not yet placed in the document.</summary>
    private sealed class PropertiesSignedXml(XmlElement element) : SignedXml(element)
    {
        public XmlDocument? Properties { get; set; }

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            base.GetIdElement(document, idValue) ?? (Properties?.SelectSingleNode("//*[@Id='" + idValue + "']") as XmlElement);
    }

    /// <summary>A stream that never ends.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++) buffer[i] = (byte)' ';
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class StreamTestExtensions
{
    public static byte[] ReadAllBytes(this Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
