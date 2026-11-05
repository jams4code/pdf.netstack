using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// The X.509 checks of ETSI EN 319 102-1 beyond chain, validity, usage and revocation: certificate
/// policies with mappings and constraints, name constraints, and the qualified status a
/// certificate's QCStatements claim. Every PKI is generated here.
/// </summary>
[TestClass]
public sealed class CertificatePolicyTests
{
    private const string P1 = "1.3.6.1.4.1.55555.1.1";
    private const string P2 = "1.3.6.1.4.1.55555.1.2";
    private const string P3 = "1.3.6.1.4.1.55555.1.3";
    private const string AnyPolicy = "2.5.29.32.0";
    private const string Etsi = "0.4.0.1862.1.";

    private static readonly X509Extension LeafUsage = new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true);

    /// <summary>A certificate with the subject exactly as given; self-signed when there is no issuer.</summary>
    private static X509Certificate2 Issue(string subject, X509Certificate2? issuer, IEnumerable<X509Extension> extensions) =>
        Issue(new X500DistinguishedName(subject), issuer, extensions);

    private static X509Certificate2 Issue(X500DistinguishedName subject, X509Certificate2? issuer, IEnumerable<X509Extension> extensions)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (X509Extension e in extensions) request.CertificateExtensions.Add(e);
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow.AddYears(1);
        if (issuer is null) return request.CreateSelfSigned(from, to);
        var serial = new byte[9];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(serial);
        serial[0] &= 0x7F;
        using RSA issuerKey = issuer.GetRSAPrivateKey()!;
        using X509Certificate2 issued = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1), from, to, serial);
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 Root(params X509Extension[] extra) => Issue("CN=Policy Root, O=PdfNetStack Tests", null, [.. TestPki.Ca(null), .. extra]);

    private static X509Certificate2 Ca(X509Certificate2 root, params X509Extension[] extra) => Issue("CN=Policy CA, O=PdfNetStack Tests", root, [.. TestPki.Ca(null), .. extra]);

    private static X509Certificate2 Leaf(X509Certificate2 ca, params X509Extension[] extra) => Issue("CN=Policy Signer, O=PdfNetStack Tests", ca, [LeafUsage, .. extra]);

    private static X509Extension Policies(params string[] oids) => new("2.5.29.32", Der.Seq([.. oids.Select(o => Der.Seq(Der.Oid(o)))]), false);

    private static X509Extension Mappings(params (string Issuer, string Subject)[] pairs) =>
        new("2.5.29.33", Der.Seq([.. pairs.Select(p => Der.Seq(Der.Oid(p.Issuer), Der.Oid(p.Subject)))]), true);

    private static X509Extension RequireExplicitPolicy(byte skip) => new("2.5.29.36", Der.Seq(Der.Tlv(0x80, [skip])), true);

    private static X509Extension Names(params byte[][] generalNames) => new("2.5.29.17", Der.Seq(generalNames), false);

    private static X509Extension Constraints(byte[][] permitted, byte[][] excluded) => new("2.5.29.30", Der.Seq(
        permitted.Length > 0 ? Der.Tlv(0xA0, Der.Concat([.. permitted.Select(p => Der.Seq(p))])) : [],
        excluded.Length > 0 ? Der.Tlv(0xA1, Der.Concat([.. excluded.Select(p => Der.Seq(p))])) : []), true);

    private static byte[] Dns(string name) => Der.Tlv(0x82, Encoding.ASCII.GetBytes(name));

    private static byte[] Email(string name) => Der.Tlv(0x81, Encoding.ASCII.GetBytes(name));

    private static byte[] Uri(string name) => Der.Tlv(0x86, Encoding.ASCII.GetBytes(name));

    private static byte[] Ip(params byte[] address) => Der.Tlv(0x87, address);

    private static byte[] Directory(string name) => Der.Context(4, true, new X500DistinguishedName(name).RawData);

    private static byte[] OtherName(string value) => Der.Tlv(0xA0, Der.Concat(Der.Oid("1.3.6.1.4.1.311.20.2.3"), Der.Context(0, true, Der.Tlv(0x0C, Encoding.UTF8.GetBytes(value)))));

    private static X509Extension QcStatements(params byte[][] statements) => new("1.3.6.1.5.5.7.1.3", Der.Seq(statements), false);

    private static byte[] Statement(string id, byte[]? info = null) => info is null ? Der.Seq(Der.Oid(id)) : Der.Seq(Der.Oid(id), info);

    private static PdfCertificateValidation Validate(X509Certificate2 root, X509Certificate2 ca, X509Certificate2 leaf, PdfValidationOptions? options = null)
    {
        PdfValidationOptions o = (options ?? new PdfValidationOptions()) with { TrustAnchors = [root], CheckRevocation = false };
        return new CertificateValidator(o, new SecurityStore()).Validate(leaf, [ca], CertificateRole.Signer, DateTimeOffset.UtcNow, PdfValidationTimeSource.Current);
    }

    private static string Problems(PdfCertificateValidation v) => string.Join("\n", v.Problems);

    [TestMethod]
    public void WithoutPoliciesTheChainPasses()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root);
        PdfCertificateValidation v = Validate(root, ca, Leaf(ca));
        Assert.AreEqual(PdfCheckStatus.Passed, v.Policy);
        Assert.IsEmpty(v.ValidPolicies);
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, Problems(v));
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[0].NameConstraints);
        Assert.AreEqual(PdfQualification.AdES, v.Qualification);
        Assert.IsNull(v.Certificates[0].QcStatements);
    }

    [TestMethod]
    public void AcceptablePoliciesMustBeAssertedAlongTheChain()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Policies(P1, P2));
        X509Certificate2 leaf = Leaf(ca, Policies(P1));
        PdfCertificateValidation v = Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P1] });
        Assert.AreEqual(PdfCheckStatus.Passed, v.Policy, Problems(v));
        CollectionAssert.AreEqual(new[] { P1 }, v.ValidPolicies.ToArray());
        CollectionAssert.AreEqual(new[] { P1 }, v.Certificates[0].Policies.ToArray());
        CollectionAssert.AreEqual(new[] { P1, P2 }, v.Certificates[1].Policies.ToArray());

        // Without acceptable policies the chain reports what it is valid for.
        CollectionAssert.AreEqual(new[] { P1 }, Validate(root, ca, leaf).ValidPolicies.ToArray());

        PdfCertificateValidation other = Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P2] });
        Assert.AreEqual(PdfCheckStatus.Failed, other.Policy);
        Assert.AreEqual(PdfCheckStatus.Failed, other.Status);
        StringAssert.Contains(Problems(other), "none of the acceptable certificate policies");

        // A leaf without the extension leaves no policy.
        PdfCertificateValidation none = Validate(root, ca, Leaf(ca), new PdfValidationOptions { AcceptablePolicies = [P1] });
        Assert.AreEqual(PdfCheckStatus.Failed, none.Policy);
    }

    [TestMethod]
    public void AnyPolicyStandsForEveryPolicyUnlessInhibited()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Policies(AnyPolicy));
        X509Certificate2 leaf = Leaf(ca, Policies(P3));
        PdfCertificateValidation v = Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P3] });
        Assert.AreEqual(PdfCheckStatus.Passed, v.Policy, Problems(v));
        CollectionAssert.AreEqual(new[] { P3 }, v.ValidPolicies.ToArray());

        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P3], InhibitAnyPolicy = true }).Policy);

        // An inhibitAnyPolicy extension of 0 on the CA has the same effect for the certificates below it.
        X509Certificate2 inhibiting = Ca(root, Policies(AnyPolicy), new X509Extension("2.5.29.54", Der.Int(0), true));
        X509Certificate2 below = Issue("CN=Sub CA, O=PdfNetStack Tests", inhibiting, [.. TestPki.Ca(null), Policies(AnyPolicy)]);
        X509Certificate2 signer = Leaf(below, Policies(P3));
        PdfCertificateValidation inhibited = new CertificateValidator(new PdfValidationOptions { TrustAnchors = [root], CheckRevocation = false, AcceptablePolicies = [P3] }, new SecurityStore())
            .Validate(signer, [inhibiting, below], CertificateRole.Signer, DateTimeOffset.UtcNow, PdfValidationTimeSource.Current);
        Assert.HasCount(4, inhibited.Certificates);
        Assert.AreEqual(PdfCheckStatus.Failed, inhibited.Policy);
        Assert.AreEqual(PdfCheckStatus.Passed, inhibited.Certificates[1].KeyUsage, "a critical inhibitAnyPolicy is now understood");
    }

    [TestMethod]
    public void PolicyMappingsAreFollowedUnlessInhibited()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Policies(P1), Mappings((P1, P2)));
        X509Certificate2 leaf = Leaf(ca, Policies(P2));
        PdfCertificateValidation v = Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P1] });
        Assert.AreEqual(PdfCheckStatus.Passed, v.Policy, Problems(v));

        // The chain is valid for the policy as the anchor's domain names it, before the mapping.
        CollectionAssert.AreEqual(new[] { P1 }, v.ValidPolicies.ToArray());
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[1].KeyUsage, Problems(v));

        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, leaf, new PdfValidationOptions { AcceptablePolicies = [P1], InhibitPolicyMapping = true }).Policy);

        // Mapping anyPolicy is not allowed.
        X509Certificate2 bad = Ca(root, Policies(P1), Mappings((AnyPolicy, P2)));
        PdfCertificateValidation refused = Validate(root, bad, Leaf(bad, Policies(P2)));
        Assert.AreEqual(PdfCheckStatus.Failed, refused.Policy);
        StringAssert.Contains(Problems(refused), "anyPolicy");
    }

    [TestMethod]
    public void ExplicitPolicyCanBeRequired()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Policies(P1), RequireExplicitPolicy(0));
        PdfCertificateValidation v = Validate(root, ca, Leaf(ca));
        Assert.AreEqual(PdfCheckStatus.Failed, v.Policy);
        StringAssert.Contains(Problems(v), "explicit certificate policy");
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[1].KeyUsage, "a critical policy constraints extension is now understood");

        Assert.AreEqual(PdfCheckStatus.Passed, Validate(root, ca, Leaf(ca, Policies(P1))).Policy);

        X509Certificate2 plainCa = Ca(root);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, plainCa, Leaf(plainCa), new PdfValidationOptions { RequireExplicitPolicy = true }).Policy);
    }

    [TestMethod]
    public void DnsNamesStayInsideThePermittedSubtrees()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([Dns("example.com")], [Dns("bad.example.com")]));
        PdfCertificateValidation inside = Validate(root, ca, Leaf(ca, Names(Dns("signer.example.com"), Dns("EXAMPLE.com"))));
        Assert.AreEqual(PdfCheckStatus.Passed, inside.Certificates[0].NameConstraints, Problems(inside));
        Assert.AreEqual(PdfCheckStatus.Passed, inside.Status, Problems(inside));
        Assert.AreEqual(PdfCheckStatus.Passed, inside.Certificates[1].KeyUsage, "a critical name constraints extension is now understood");

        PdfCertificateValidation outside = Validate(root, ca, Leaf(ca, Names(Dns("signer.example.org"))));
        Assert.AreEqual(PdfCheckStatus.Failed, outside.Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, outside.Status);
        StringAssert.Contains(outside.Certificates[0].Problem, "signer.example.org is outside the permitted names");

        // example.com does not cover notexample.com.
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Dns("notexample.com")))).Certificates[0].NameConstraints);

        PdfCertificateValidation excluded = Validate(root, ca, Leaf(ca, Names(Dns("x.bad.example.com"))));
        StringAssert.Contains(excluded.Certificates[0].Problem, "x.bad.example.com is excluded");
    }

    [TestMethod]
    public void EmailAndUriConstraints()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([Email(".example.com"), Email("boss@example.org"), Uri("files.example.com")], []));
        Assert.AreEqual(PdfCheckStatus.Passed, Validate(root, ca, Leaf(ca, Names(Email("anyone@mail.EXAMPLE.com"), Email("boss@example.org")))).Certificates[0].NameConstraints);

        // A leading period means the hosts below the domain, not the domain itself; a mailbox is exact.
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Email("anyone@example.com")))).Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Email("Boss@example.org")))).Certificates[0].NameConstraints);

        Assert.AreEqual(PdfCheckStatus.Passed, Validate(root, ca, Leaf(ca, Names(Uri("https://user@files.example.com:8443/a?b")))).Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Uri("https://other.example.com/")))).Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Uri("urn:isbn:0451450523")))).Certificates[0].NameConstraints);
    }

    [TestMethod]
    public void EmailInTheSubjectCountsWithoutAlternativeNames()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([Email("example.com")], []));
        X509Certificate2 inside = Issue("E=signer@example.com, CN=Mail Signer, O=PdfNetStack Tests", ca, [LeafUsage]);
        X509Certificate2 outside = Issue("E=signer@example.net, CN=Mail Signer, O=PdfNetStack Tests", ca, [LeafUsage]);
        Assert.AreEqual(PdfCheckStatus.Passed, Validate(root, ca, inside).Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, outside).Certificates[0].NameConstraints);
    }

    [TestMethod]
    public void DirectoryNamesMatchByLeadingNamesIgnoringCaseAndSpaces()
    {
        X509Certificate2 root = Root();
        X509Certificate2 subject = Leaf(Ca(root));
        byte[] encoded = subject.SubjectName.RawData;

        // The constraint is the subject's first relative distinguished name, in another case and spacing.
        DerNode first = DerNode.Parse(encoded).At(0);
        DerNode attribute = first.At(0);
        string value = Encoding.UTF8.GetString(attribute.At(1).Content.ToArray());
        byte[] constraint = Der.Seq(Der.SetOf(Der.Seq(attribute.At(0).Encoded.ToArray(), Der.Tlv(0x0C, Encoding.UTF8.GetBytes("  " + value.ToUpperInvariant().Replace(" ", "   ") + " ")))));
        X509Certificate2 ca = Ca(root, Constraints([Der.Context(4, true, constraint)], []));
        PdfCertificateValidation v = Validate(root, ca, Leaf(ca));
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[0].NameConstraints, Problems(v));

        X509Certificate2 elsewhere = Ca(root, Constraints([Directory("O=Elsewhere")], []));
        PdfCertificateValidation outside = Validate(root, elsewhere, Leaf(elsewhere));
        Assert.AreEqual(PdfCheckStatus.Failed, outside.Certificates[0].NameConstraints);
        StringAssert.Contains(outside.Certificates[0].Problem, "outside the permitted names");
    }

    [TestMethod]
    public void IpAddressesAndUnsupportedForms()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([Ip(10, 0, 0, 0, 255, 0, 0, 0)], [OtherName("blocked@example")]));
        Assert.AreEqual(PdfCheckStatus.Passed, Validate(root, ca, Leaf(ca, Names(Ip(10, 1, 2, 3)))).Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ca, Leaf(ca, Names(Ip(192, 168, 0, 1)))).Certificates[0].NameConstraints);

        // A constraint on other names cannot be judged, so a certificate carrying one fails; one without passes.
        PdfCertificateValidation other = Validate(root, ca, Leaf(ca, Names(OtherName("someone@example"))));
        Assert.AreEqual(PdfCheckStatus.Failed, other.Certificates[0].NameConstraints);
        StringAssert.Contains(other.Certificates[0].Problem, "cannot be checked");
    }

    [TestMethod]
    public void TheTrustAnchorsConstraintsApply()
    {
        X509Certificate2 root = Root(Constraints([], [Dns("example.com")]));
        X509Certificate2 ca = Ca(root, Names(Dns("ca.example.org")));
        PdfCertificateValidation v = Validate(root, ca, Leaf(ca, Names(Dns("signer.example.com"))));
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[1].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.NotChecked, v.Certificates[2].NameConstraints);
        Assert.IsTrue(v.Certificates[2].IsTrustAnchor);
    }

    [TestMethod]
    public void QcStatementsGiveTheClaimedQualification()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root);
        X509Certificate2 qualified = Leaf(ca, QcStatements(
            Statement(Etsi + "1"),
            Statement(Etsi + "4"),
            Statement(Etsi + "6", Der.Seq(Der.Oid(Etsi + "6.1"))),
            Statement(Etsi + "2", Der.Seq(Der.Tlv(0x13, "EUR"u8), Der.Int(5), Der.Int(3))),
            Statement(Etsi + "3", Der.Int(15)),
            Statement(Etsi + "5", Der.Seq(Der.Seq(Der.Tlv(0x16, "https://pds.example.com/en"u8), Der.Tlv(0x13, "en"u8)))),
            Statement("1.3.6.1.5.5.7.11.2", Der.Seq(Der.Oid("0.4.0.194121.1.1"))),
            Statement("1.2.3.4.5")));
        PdfCertificateValidation v = Validate(root, ca, qualified);
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, Problems(v));
        Assert.AreEqual(PdfQualification.QESig, v.Qualification);
        PdfQcStatements qc = v.Certificates[0].QcStatements!;
        Assert.IsTrue(qc.IsQualified);
        Assert.IsTrue(qc.KeyInQualifiedDevice);
        CollectionAssert.AreEqual(new[] { PdfQcType.ElectronicSignature }, qc.Types.ToArray());
        Assert.AreEqual(new PdfQcLimit("EUR", 5000m), qc.Limit);
        Assert.AreEqual(15, qc.RetentionYears);
        Assert.AreEqual(new PdfQcDisclosureStatement("https://pds.example.com/en", "en"), qc.DisclosureStatements.Single());
        Assert.AreEqual("0.4.0.194121.1.1", qc.SemanticsIdentifier);
        Assert.HasCount(8, qc.StatementIds);
        Assert.AreEqual("1.2.3.4.5", qc.StatementIds[7]);
        Assert.IsEmpty(qc.Legislations);

        // A seal certificate without a qualified device, and one qualified under another country's law.
        X509Certificate2 seal = Leaf(ca, QcStatements(Statement(Etsi + "1"), Statement(Etsi + "6", Der.Seq(Der.Oid(Etsi + "6.2")))));
        Assert.AreEqual(PdfQualification.AdESealQC, Validate(root, ca, seal).Qualification);
        X509Certificate2 web = Leaf(ca, QcStatements(Statement(Etsi + "1"), Statement(Etsi + "4"), Statement(Etsi + "6", Der.Seq(Der.Oid(Etsi + "6.3")))));
        Assert.AreEqual(PdfQualification.QES, Validate(root, ca, web).Qualification);
        X509Certificate2 foreign = Leaf(ca, QcStatements(Statement(Etsi + "1"), Statement(Etsi + "7", Der.Seq(Der.Tlv(0x13, "CH"u8)))));
        PdfCertificateValidation abroad = Validate(root, ca, foreign);
        Assert.AreEqual(PdfQualification.AdES, abroad.Qualification);
        CollectionAssert.AreEqual(new[] { "CH" }, abroad.Certificates[0].QcStatements!.Legislations.ToArray());

        // Without QcType, ETSI reads the certificate as one for signatures.
        X509Certificate2 untyped = Leaf(ca, QcStatements(Statement(Etsi + "1")));
        Assert.AreEqual(PdfQualification.AdESigQC, Validate(root, ca, untyped).Qualification);
    }

    [TestMethod]
    public void VerifyReportsPoliciesAndQualification()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Policies(P1));
        X509Certificate2 leaf = Leaf(ca, Policies(P1), QcStatements(Statement(Etsi + "1"), Statement(Etsi + "4")));
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Order", PdfStandardFont.Helvetica, 20, PdfColor.Black, 72, 72);
        byte[] signed = PdfSigner.Sign(doc.ToArray(new PdfSaveOptions { Optimize = false }), leaf, new PdfSignatureOptions { Chain = [ca] });
        PdfCertificateValidation v = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [root], CheckRevocation = false, AcceptablePolicies = [P1] }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, Problems(v));
        CollectionAssert.AreEqual(new[] { P1 }, v.ValidPolicies.ToArray());
        Assert.AreEqual(PdfQualification.QESig, v.Qualification);

        // Without a trust anchor the policies are not judged.
        PdfCertificateValidation unanchored = PdfSigner.Verify(signed, new PdfValidationOptions { CheckRevocation = false }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.NotChecked, unanchored.Policy);
    }

    [TestMethod]
    // Apple's loader refuses these certificates when the test creates them, so they cannot reach the validator there.
    [OSCondition(ConditionMode.Exclude, OperatingSystems.OSX)]
    public void UnreadableConstraintsFailEveryCertificateBelow()
    {
        // The anchor's own checks are not counted, so its unreadable constraints must fail what it issued.
        var garbage = new X509Extension("2.5.29.30", [0x30, 0x03, 0xA0, 0x05, 0x00], true);
        X509Certificate2 root = Root(garbage);
        X509Certificate2 ca = Ca(root);
        PdfCertificateValidation v = Validate(root, ca, Leaf(ca, Names(Dns("signer.example.com"))));
        Assert.AreEqual(PdfCheckStatus.Failed, v.Status);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[1].NameConstraints);
        StringAssert.Contains(Problems(v), "The name constraints of Policy Root cannot be read");
        StringAssert.Contains(v.Certificates[0].Problem, "above it cannot be read");

        X509Certificate2 goodRoot = Root();
        X509Certificate2 badCa = Ca(goodRoot, garbage);
        PdfCertificateValidation below = Validate(goodRoot, badCa, Leaf(badCa));
        Assert.AreEqual(PdfCheckStatus.Failed, below.Certificates[1].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, below.Certificates[0].NameConstraints);
        Assert.AreEqual(PdfCheckStatus.Failed, below.Status);
    }

    [TestMethod]
    public void HostsAreCanonicalAndAmbiguousOnesFailClosed()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([], [Uri("good.example.com"), Uri("evil.example"), Uri("xn--bcher-kva.example"), Email("evil.example"), Dns("evil.example")]));
        PdfCheckStatus Status(byte[] name) => Validate(root, ca, Leaf(ca, Names(name))).Certificates[0].NameConstraints;

        Assert.AreEqual(PdfCheckStatus.Passed, Status(Uri("https://fine.example.org/")));
        Assert.AreEqual(PdfCheckStatus.Passed, Status(Dns("fine.example.org")));

        // A backslash is a slash to a browser: the host is good.example.com, not other.example.
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Uri("https://good.example.com\\@other.example/")));

        // One trailing dot names the same host, in every form.
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Uri("https://EVIL.example./path")));
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Email("someone@evil.example.")));
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Dns("www.EVIL.example.")));

        // Bytes outside IA5 cannot be read as a host, so an exclusion covers them.
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Der.Tlv(0x86, Encoding.UTF8.GetBytes("https://b\u00FCcher.example/"))));
        Assert.AreEqual(PdfCheckStatus.Failed, Status(Uri("https://x%2eevil.example/")));

        // Permitted subtrees: canonical forms match, and what cannot be read is not permitted.
        X509Certificate2 permitting = Ca(root, Constraints([Uri(".example.com"), Uri("xn--bcher-kva.example"), Dns("Example.NET.")], []));
        PdfCheckStatus Allowed(byte[] name) => Validate(root, permitting, Leaf(permitting, Names(name))).Certificates[0].NameConstraints;
        Assert.AreEqual(PdfCheckStatus.Passed, Allowed(Uri("https://A.Example.COM./x")));
        Assert.AreEqual(PdfCheckStatus.Passed, Allowed(Uri("http://xn--bcher-kva.example:8080/")));
        Assert.AreEqual(PdfCheckStatus.Passed, Allowed(Dns("host.example.net")));
        Assert.AreEqual(PdfCheckStatus.Failed, Allowed(Uri("https://a.example.com\\x/")));
        Assert.AreEqual(PdfCheckStatus.Failed, Allowed(Uri("https://[2001:db8::1]/")));
        Assert.AreEqual(PdfCheckStatus.Failed, Allowed(Dns("bad..example.net")));
    }

    private static X500DistinguishedName Subject(byte organizationTag, byte[] organization) => new(Der.Seq(
        Der.SetOf(Der.Seq(Der.Oid("2.5.4.10"), Der.Tlv(organizationTag, organization))),
        Der.SetOf(Der.Seq(Der.Oid("2.5.4.3"), Der.Tlv(0x0C, "Directory Signer"u8)))));

    private static byte[] Organization(string name) => Der.Context(4, true, Der.Seq(Der.SetOf(Der.Seq(Der.Oid("2.5.4.10"), Der.Tlv(0x0C, Encoding.UTF8.GetBytes(name))))));

    /// <summary>
    /// Checks a certificate built with a malformed string. Windows loads these, so the validator must
    /// handle them. OpenSSL and Apple's loader may refuse one when the test creates it; then it can
    /// never reach the validator on that platform and there is nothing to check.
    /// </summary>
    private static void Expect(PdfCheckStatus expected, Func<PdfCheckStatus> check)
    {
        PdfCheckStatus actual;
        try
        {
            actual = check();
        }
        catch (CryptographicException) when (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void DirectoryStringsAreDecodedAndPrepared()
    {
        X509Certificate2 root = Root();
        X509Certificate2 ca = Ca(root, Constraints([], [Organization("Evil Corp")]));
        PdfCheckStatus Status(byte tag, byte[] organization) =>
            Validate(root, ca, Issue(Subject(tag, organization), ca, [LeafUsage])).Certificates[0].NameConstraints;

        Expect(PdfCheckStatus.Passed, () => Status(0x0C, "Good Corp"u8.ToArray()));

        // UniversalString is UTF-32BE; case and spacing do not matter.
        Expect(PdfCheckStatus.Failed, () => Status(0x1C, new UTF32Encoding(true, false).GetBytes("EVIL   corp")));

        // BMPString is UTF-16BE; full width letters fold to their compatibility forms.
        Expect(PdfCheckStatus.Failed, () => Status(0x1E, Encoding.BigEndianUnicode.GetBytes("\uFF25\uFF56\uFF49\uFF4C Corp")));

        // Soft hyphens are dropped; bytes that do not decode count as a match for an exclusion.
        Expect(PdfCheckStatus.Failed, () => Status(0x0C, Encoding.UTF8.GetBytes("Ev\u00ADil Corp")));
        Expect(PdfCheckStatus.Failed, () => Status(0x0C, [0x45, 0xFF, 0x69, 0x6C]));
        Expect(PdfCheckStatus.Failed, () => Status(0x1C, [0, 0, 0]));

        // In a permitted subtree the same decoding lets equal names pass, and undecodable ones not.
        X509Certificate2 permitting = Ca(root, Constraints([Organization("PdfNetStack Tests")], []));
        PdfCheckStatus Allowed(byte tag, byte[] organization) =>
            Validate(root, permitting, Issue(Subject(tag, organization), permitting, [LeafUsage])).Certificates[0].NameConstraints;
        Expect(PdfCheckStatus.Passed, () => Allowed(0x1C, new UTF32Encoding(true, false).GetBytes("pdfnetstack tests")));
        Expect(PdfCheckStatus.Passed, () => Allowed(0x13, "PDFNETSTACK TESTS"u8.ToArray()));
        Expect(PdfCheckStatus.Failed, () => Allowed(0x0C, [0x50, 0xC0]));
    }

    [TestMethod]
    // Apple's loader refuses these certificates when the test creates them, so they cannot reach the validator there.
    [OSCondition(ConditionMode.Exclude, OperatingSystems.OSX)]
    public void DamagedExtensionsFailWithoutThrowing()
    {
        X509Certificate2 root = Root();
        X509Certificate2 badConstraints = Ca(root, new X509Extension("2.5.29.30", [0x30, 0x03, 0xA0, 0x05, 0x00], true));
        PdfCertificateValidation names = Validate(root, badConstraints, Leaf(badConstraints));
        Assert.AreEqual(PdfCheckStatus.Failed, names.Certificates[1].NameConstraints);
        StringAssert.Contains(names.Certificates[1].Problem, "name constraints cannot be read");

        // A subtree with a minimum or maximum is not supported.
        X509Certificate2 ranged = Ca(root, new X509Extension("2.5.29.30", Der.Seq(Der.Tlv(0xA0, Der.Seq(Dns("example.com"), Der.Tlv(0x80, [1])))), true));
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, ranged, Leaf(ranged)).Certificates[1].NameConstraints);

        X509Certificate2 badPolicies = Ca(root, new X509Extension("2.5.29.32", [0x30, 0x02, 0x30, 0x00], false));
        PdfCertificateValidation policies = Validate(root, badPolicies, Leaf(badPolicies));
        Assert.AreEqual(PdfCheckStatus.Failed, policies.Policy);
        StringAssert.Contains(Problems(policies), "cannot be processed");

        X509Certificate2 manyPolicies = Ca(root, Policies([.. Enumerable.Range(0, 70).Select(i => "1.3.6.1.4.1.55555.2." + i)]));
        Assert.AreEqual(PdfCheckStatus.Failed, Validate(root, manyPolicies, Leaf(manyPolicies)).Policy);

        X509Certificate2 plainCa = Ca(root);
        X509Certificate2 badQc = Leaf(plainCa, new X509Extension("1.3.6.1.5.5.7.1.3", [0x30, 0x03, 0x30, 0x01, 0x06], false));
        PdfCertificateValidation qc = Validate(root, plainCa, badQc);
        Assert.AreEqual(PdfCheckStatus.Passed, qc.Status, Problems(qc));
        Assert.AreEqual(PdfQualification.AdES, qc.Qualification);
        Assert.IsNull(qc.Certificates[0].QcStatements);
        StringAssert.Contains(Problems(qc), "QC statements cannot be read");

        foreach (byte[] garbage in new[] { new byte[] { 0x30 }, [0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF], [0x30, 0x02, 0x30, 0x80], [0x30, 0x04, 0x30, 0x02, 0x06, 0x00] })
        {
            X509Certificate2 leaf = Leaf(plainCa, new X509Extension("1.3.6.1.5.5.7.1.3", garbage, false));
            Assert.Throws<FormatException>(() => PdfQcStatements.Read(leaf));
        }
    }
}
