using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>PAdES B-T, B-LT and B-LTA: timestamps, validation data, and certificate validation.</summary>
[TestClass]
public sealed class LongTermSignatureTests
{
    private static readonly Lazy<TestPki> SharedPki = new(() => new TestPki());

    private static byte[] Document()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Contract", PdfStandardFont.Helvetica, 20, PdfColor.Black, 72, 72);
        return doc.ToArray(new PdfSaveOptions { Optimize = false });
    }

    private static byte[] SignTimestamped(TestPki pki) =>
        PdfSigner.Sign(Document(), pki.Signer, new PdfSignatureOptions { Chain = [pki.Intermediate], Timestamp = pki.TimestampClient() });

    private static PdfValidationOptions Trusting(TestPki pki) => new() { TrustAnchors = [pki.Root] };

    [TestMethod]
    public void SignatureTimestampMakesBaselineT()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.IsNotNull(info.Timestamp);
        Assert.IsTrue(info.Timestamp.IsIntact, info.Timestamp.Problem);
        Assert.AreEqual(pki.Now, info.Timestamp.Time);
        Assert.AreEqual("Test Time Stamp Authority", info.Timestamp.AuthorityName);
        Assert.AreEqual("1.2.3.4.1", info.Timestamp.PolicyOid);
        Assert.IsFalse(info.IsDocumentTimestamp);
        Assert.IsNull(info.Validation);
    }

    [TestMethod]
    public void TimestampDigestFollowsTheClient()
    {
        var pki = new TestPki();
        byte[] signed = PdfSigner.Sign(Document(), pki.Signer, new PdfSignatureOptions { Timestamp = pki.TimestampClient(PdfDigestAlgorithm.Sha512) });
        Assert.IsTrue(PdfSigner.Verify(signed).Single().Timestamp!.IsIntact);
    }

    [TestMethod]
    public void DocumentTimestampCoversTheWholeFile()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        byte[] stamped = PdfSigner.AddDocumentTimestamp(signed, pki.TimestampClient());
        CollectionAssert.AreEqual(signed, stamped.AsSpan(0, signed.Length).ToArray());

        IReadOnlyList<PdfSignatureInfo> all = PdfSigner.Verify(stamped);
        Assert.HasCount(2, all);
        Assert.IsTrue(all[0].IsIntact, all[0].Problem);
        Assert.IsFalse(all[0].CoversWholeDocument);
        PdfSignatureInfo stamp = all[1];
        Assert.IsTrue(stamp.IsDocumentTimestamp);
        Assert.AreEqual("ETSI.RFC3161", stamp.SubFilter);
        Assert.AreEqual("Timestamp1", stamp.FieldName);
        Assert.IsTrue(stamp.IsIntact, stamp.Problem);
        Assert.IsTrue(stamp.CoversWholeDocument);
        Assert.AreEqual("Test Time Stamp Authority", stamp.SignerName);

        string text = Encoding.ASCII.GetString(stamped, signed.Length, stamped.Length - signed.Length);
        StringAssert.Contains(text, "/Type/DocTimeStamp");
    }

    [TestMethod]
    public void DocumentTimestampDetectsChanges()
    {
        TestPki pki = SharedPki.Value;
        byte[] stamped = PdfSigner.AddDocumentTimestamp(Document(), pki.TimestampClient());
        byte[] tampered = (byte[])stamped.Clone();
        tampered[20] ^= 1;
        PdfSignatureInfo info = PdfSigner.Verify(tampered).Single();
        Assert.IsFalse(info.DigestMatches);
        StringAssert.Contains(info.Problem, "timestamped");
    }

    [TestMethod]
    public void BadTimestampRepliesAreRefused()
    {
        foreach (TsaFault fault in new[] { TsaFault.Reject, TsaFault.Garbage, TsaFault.WrongImprint, TsaFault.WrongNonce, TsaFault.BadSignature })
        {
            var pki = new TestPki { Fault = fault };
            Assert.ThrowsExactly<CryptographicException>(() => pki.TimestampClient().GetToken([1, 2, 3]));
            Assert.ThrowsExactly<CryptographicException>(() => PdfSigner.Sign(Document(), pki.Signer, new PdfSignatureOptions { Timestamp = pki.TimestampClient() }));
        }

        var wrongUsage = new TestPki(tsaUsage: false);
        var e = Assert.ThrowsExactly<CryptographicException>(() => wrongUsage.TimestampClient().GetToken([1]));
        StringAssert.Contains(e.Message, "time stamping");
    }

    [TestMethod]
    public void TruncatedTimestampRepliesAreRefused()
    {
        TestPki pki = SharedPki.Value;
        byte[] good = pki.Respond(BuildRequest(pki));
        for (int length = 0; length < good.Length; length += 37)
        {
            byte[] cut = good.AsSpan(0, length).ToArray();
            var client = new PdfTimestampClient(_ => cut);
            Assert.ThrowsExactly<CryptographicException>(() => client.GetToken([7]));
        }
    }

    private static byte[] BuildRequest(TestPki pki)
    {
        byte[]? captured = null;
        var client = new PdfTimestampClient(r =>
        {
            captured = r;
            return pki.Respond(r);
        });
        client.GetToken([7]);
        return captured!;
    }

    [TestMethod]
    public void LongTermValidationDataGoesInTheSecurityStore()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        var requested = new List<PdfRevocationRequest>();
        var source = new RecordingSource(pki.GoodRevocation(), requested);
        byte[] lt = PdfSigner.AddValidationData(signed, source, [pki.Root]);
        CollectionAssert.AreEqual(signed, lt.AsSpan(0, signed.Length).ToArray());
        Assert.IsTrue(PdfSigner.Verify(lt).Single().IsIntact);

        // The signer, the intermediate and the authority are asked about; the root and the exempt responder are not.
        CollectionAssert.AreEquivalent(new[] { "Test Signer", "Test Issuing CA", "Test Time Stamp Authority" }, requested.Select(r => Name(r.Certificate)).ToArray());
        PdfRevocationRequest signerRequest = requested.First(r => Name(r.Certificate) == "Test Signer");
        CollectionAssert.AreEqual(new[] { TestPki.OcspUrl }, signerRequest.OcspUrls.ToArray());
        CollectionAssert.AreEqual(new[] { TestPki.CrlUrl }, signerRequest.CrlUrls.ToArray());

        PdfDocument doc = PdfDocument.Open(lt);
        var dss = (PdfDictionary)doc.Resolve(doc.Catalog["DSS"])!;
        Assert.HasCount(5, (PdfArray)doc.Resolve(dss["Certs"])!);
        Assert.HasCount(2, (PdfArray)doc.Resolve(dss["OCSPs"])!);
        Assert.HasCount(2, (PdfArray)doc.Resolve(dss["CRLs"])!);
        var vri = (PdfDictionary)doc.Resolve(dss["VRI"])!;
        Assert.HasCount(1, vri);
        string key = vri.Single().Key;
        Assert.AreEqual(40, key.Length);
        Assert.AreEqual(key.ToUpperInvariant(), key);
        var entry = (PdfDictionary)doc.Resolve(vri[key])!;
        Assert.IsNotNull(entry["Cert"]);
        Assert.IsNotNull(entry["OCSP"]);
        Assert.IsNotNull(entry["CRL"]);

        // Adding the same data again keeps one copy of each entry.
        byte[] again = PdfSigner.AddValidationData(lt, pki.GoodRevocation(), [pki.Root]);
        PdfDocument twice = PdfDocument.Open(again);
        var dss2 = (PdfDictionary)twice.Resolve(twice.Catalog["DSS"])!;
        Assert.HasCount(5, (PdfArray)twice.Resolve(dss2["Certs"])!);
        Assert.HasCount(2, (PdfArray)twice.Resolve(dss2["OCSPs"])!);
    }

    [TestMethod]
    public void BaselineLtaValidatesFromTheStoreAlone()
    {
        TestPki pki = SharedPki.Value;
        byte[] lt = PdfSigner.AddValidationData(SignTimestamped(pki), pki.GoodRevocation(), [pki.Root]);
        byte[] lta = PdfSigner.AddDocumentTimestamp(lt, pki.TimestampClient());
        lta = PdfSigner.AddValidationData(lta, pki.GoodRevocation(), [pki.Root]);

        IReadOnlyList<PdfSignatureInfo> all = PdfSigner.Verify(lta, Trusting(pki));
        Assert.HasCount(2, all);
        PdfSignatureInfo signature = all[0];
        Assert.IsTrue(signature.IsIntact, signature.Problem);
        PdfCertificateValidation v = signature.Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, string.Join("\n", v.Problems));
        Assert.AreEqual(PdfValidationTimeSource.Timestamp, v.TimeSource);
        Assert.AreEqual(pki.Now, v.ValidationTime);
        Assert.AreEqual(PdfCheckStatus.Passed, v.Chain);
        CollectionAssert.AreEqual(new[] { "Test Signer", "Test Issuing CA", "Test Root CA" }, v.Certificates.Select(c => Name(c.Certificate)).ToArray());
        Assert.AreEqual("OCSP (document security store)", v.Certificates[0].RevocationSource);
        Assert.AreEqual("CRL (document security store)", v.Certificates[1].RevocationSource);
        Assert.IsTrue(v.Certificates[2].IsTrustAnchor);
        Assert.AreEqual(PdfCheckStatus.NotChecked, v.Certificates[2].Revocation);
        Assert.AreEqual(PdfCheckStatus.Passed, signature.Timestamp!.Validation!.Status, string.Join("\n", signature.Timestamp.Validation.Problems));

        PdfSignatureInfo stamp = all[1];
        Assert.IsTrue(stamp.IsDocumentTimestamp);
        Assert.AreEqual(PdfCheckStatus.Passed, stamp.Validation!.Status, string.Join("\n", stamp.Validation.Problems));
    }

    [TestMethod]
    public void SuppliedRevocationDataCountsWithoutTheStore()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        PdfCertificateValidation bare = PdfSigner.Verify(signed, Trusting(pki)).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Indeterminate, bare.Status);
        Assert.AreEqual(PdfCheckStatus.NotChecked, bare.Certificates[0].Revocation);
        Assert.IsTrue(bare.Problems.Any(p => p.Contains("no revocation data")));

        PdfCertificateValidation supplied = PdfSigner.Verify(signed, Trusting(pki) with { RevocationSource = pki.GoodRevocation() }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, supplied.Status, string.Join("\n", supplied.Problems));
        Assert.AreEqual("OCSP (supplied)", supplied.Certificates[0].RevocationSource);

        PdfCertificateValidation skipped = PdfSigner.Verify(signed, Trusting(pki) with { CheckRevocation = false }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, skipped.Status, string.Join("\n", skipped.Problems));
    }

    [TestMethod]
    public void WithoutTrustAnchorsTheChainIsNotChecked()
    {
        TestPki pki = SharedPki.Value;
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), new PdfValidationOptions { RevocationSource = pki.GoodRevocation(), Certificates = [pki.Root] }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.NotChecked, v.Chain);
        Assert.AreEqual(PdfCheckStatus.Indeterminate, v.Status);
        Assert.HasCount(3, v.Certificates);
    }

    [TestMethod]
    public void AnUnrelatedAnchorFailsTheChain()
    {
        TestPki pki = SharedPki.Value;
        var other = new TestPki();
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), new PdfValidationOptions { TrustAnchors = [other.Root], RevocationSource = pki.GoodRevocation() }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Failed, v.Chain);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Status);
    }

    [TestMethod]
    public void RevokedBeforeTheTimestampFails()
    {
        TestPki pki = SharedPki.Value;
        var revocation = new PdfRevocationValues
        {
            OcspResponses = [pki.Ocsp(pki.Signer, pki.Intermediate, pki.Responder, revokedAt: pki.Now.AddHours(-1), thisUpdate: pki.Now.AddHours(1))],
            Crls = [pki.Crl(pki.Root, [])],
        };
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = revocation, CheckRevocation = true }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Failed, v.Status);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[0].Revocation);
        Assert.AreEqual(pki.Now.AddHours(-1), v.Certificates[0].RevocationTime);
    }

    [TestMethod]
    public void RevokedAfterTheTimestampPasses()
    {
        TestPki pki = SharedPki.Value;
        var revocation = new PdfRevocationValues
        {
            OcspResponses = [pki.Ocsp(pki.Signer, pki.Intermediate, pki.Responder, revokedAt: pki.Now.AddHours(1), thisUpdate: pki.Now.AddHours(2))],
            Crls = [pki.Crl(pki.Root, [])],
        };
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = revocation }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[0].Revocation, v.Certificates[0].Problem);
        Assert.AreEqual(pki.Now.AddHours(1), v.Certificates[0].RevocationTime);

        // Without the timestamp the signature is checked now, after the revocation.
        byte[] untimestamped = PdfSigner.Sign(Document(), pki.Signer, new PdfSignatureOptions { Chain = [pki.Intermediate] });
        PdfCertificateValidation now = PdfSigner.Verify(untimestamped, Trusting(pki) with { RevocationSource = revocation, ValidationTime = pki.Now.AddHours(3) }).Single().Validation!;
        Assert.AreEqual(PdfValidationTimeSource.Caller, now.TimeSource);
        Assert.AreEqual(PdfCheckStatus.Failed, now.Certificates[0].Revocation);
    }

    [TestMethod]
    public void RevokedOnTheCrlFails()
    {
        TestPki pki = SharedPki.Value;
        var revocation = new PdfRevocationValues { Crls = [pki.Crl(pki.Intermediate, [(pki.Signer, pki.Now.AddDays(-1))]), pki.Crl(pki.Root, [])] };
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = revocation }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[0].Revocation);
        Assert.AreEqual("CRL (supplied)", v.Certificates[0].RevocationSource);
    }

    [TestMethod]
    public void StaleOrUnverifiedRevocationDataIsIndeterminate()
    {
        TestPki pki = SharedPki.Value;
        var stale = new PdfRevocationValues { Crls = [pki.Crl(pki.Intermediate, [], pki.Now.AddDays(-10), pki.Now.AddDays(-3)), pki.Crl(pki.Root, [])] };
        PdfCertificateValidation v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = stale }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Indeterminate, v.Certificates[0].Revocation);
        StringAssert.Contains(v.Certificates[0].Problem, "predates");

        // A response signed by a key the issuer never delegated to is ignored.
        var other = new TestPki();
        var forged = new PdfRevocationValues { OcspResponses = [pki.Ocsp(pki.Signer, pki.Intermediate, other.Responder)], Crls = [pki.Crl(pki.Root, [])] };
        v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = forged }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Indeterminate, v.Certificates[0].Revocation);
        StringAssert.Contains(v.Certificates[0].Problem, "not signed");

        var unknown = new PdfRevocationValues { OcspResponses = [pki.Ocsp(pki.Signer, pki.Intermediate, pki.Responder, unknown: true)], Crls = [pki.Crl(pki.Root, [])] };
        v = PdfSigner.Verify(SignTimestamped(pki), Trusting(pki) with { RevocationSource = unknown }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Indeterminate, v.Certificates[0].Revocation);
    }

    [TestMethod]
    public void DeltaCrlsAreNotUsed()
    {
        // A delta CRL lists only recent changes, so the signer missing from it proves nothing.
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        var delta = new PdfRevocationValues { Crls = [pki.Crl(pki.Intermediate, [], delta: true), pki.Crl(pki.Root, [])] };
        PdfCertificateValidation v = PdfSigner.Verify(signed, Trusting(pki) with { RevocationSource = delta }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Indeterminate, v.Certificates[0].Revocation);
        StringAssert.Contains(v.Certificates[0].Problem, "delta");
        Assert.AreEqual(PdfCheckStatus.Passed, v.Certificates[1].Revocation, v.Certificates[1].Problem);

        PdfDocument doc = PdfDocument.Open(PdfSigner.AddValidationData(signed, delta, [pki.Root]));
        var dss = (PdfDictionary)doc.Resolve(doc.Catalog["DSS"])!;
        Assert.HasCount(1, (PdfArray)doc.Resolve(dss["CRLs"])!);
    }

    [TestMethod]
    public void ValidityAndUsageAreChecked()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        PdfCertificateValidation expired = PdfSigner.Verify(signed, Trusting(pki) with { CheckRevocation = false, ValidationTime = pki.Now.AddYears(3) }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Failed, expired.Certificates[0].Validity);
        Assert.AreEqual(PdfCheckStatus.Failed, expired.Status);

        var encryptionOnly = new TestPki(X509KeyUsageFlags.KeyEncipherment);
        byte[] wrongUsage = PdfSigner.Sign(Document(), encryptionOnly.Signer, new PdfSignatureOptions { Chain = [encryptionOnly.Intermediate] });
        PdfCertificateValidation usage = PdfSigner.Verify(wrongUsage, Trusting(encryptionOnly) with { CheckRevocation = false }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Failed, usage.Certificates[0].KeyUsage);
        StringAssert.Contains(usage.Certificates[0].Problem, "key usage");
    }

    [TestMethod]
    public void CaWithoutTheCaFlagFails()
    {
        TestPki pki = SharedPki.Value;
        X509Certificate2 fakeCa = TestPki.Issue("Not A CA", pki.Root, []);
        X509Certificate2 leaf = TestPki.Issue("Leaf", fakeCa, [new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)]);
        byte[] signed = PdfSigner.Sign(Document(), leaf, new PdfSignatureOptions { Chain = [fakeCa] });
        PdfCertificateValidation v = PdfSigner.Verify(signed, Trusting(pki) with { CheckRevocation = false }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, v.Chain);
        Assert.AreEqual(PdfCheckStatus.Failed, v.Certificates[1].KeyUsage);
        StringAssert.Contains(v.Certificates[1].Problem, "not marked as a CA");
    }

    [TestMethod]
    public void SelfSignedSignerTrustedDirectly()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Self", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        byte[] signed = PdfSigner.Sign(Document(), self);
        PdfCertificateValidation v = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [self] }).Single().Validation!;
        Assert.AreEqual(PdfCheckStatus.Passed, v.Status, string.Join("\n", v.Problems));
        Assert.HasCount(1, v.Certificates);
        Assert.IsTrue(v.Certificates[0].IsTrustAnchor);
        Assert.AreEqual(PdfValidationTimeSource.Current, v.TimeSource);
    }

    [TestMethod]
    public void OcspRequestNamesTheCertificate()
    {
        TestPki pki = SharedPki.Value;
        PdfRevocationRequest? request = null;
        PdfSigner.AddValidationData(SignTimestamped(pki), new RecordingSource(PdfRevocationValues.None, []) { OnRequest = r => request ??= r });
        Assert.IsNotNull(request);
        byte[] der = request.CreateOcspRequest();
        DerNode certId = DerNode.Parse(der).At(0).At(0).At(0).At(0);
        Assert.AreEqual("1.3.14.3.2.26", certId.At(0).At(0).OidValue());
        byte[] serial = request.Certificate.GetSerialNumber();
        Array.Reverse(serial);
        CollectionAssert.AreEqual(serial, certId.At(3).Content.ToArray());
        CollectionAssert.AreEqual(TestPki.Sha1(request.Issuer.PublicKey.EncodedKeyValue.RawData), certId.At(2).Content.ToArray());
    }

    [TestMethod]
    public void UnsignedDocumentsHaveNothingToValidate()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfSigner.AddValidationData(Document(), PdfRevocationValues.None));
    }

    [TestMethod]
    public void DamagedValidationDataIsSkipped()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        var junk = new PdfRevocationValues { OcspResponses = [[0x30, 0x03, 0x0A, 0x01, 0x00], [1, 2, 3], []], Crls = [[0x30, 0x00], [0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF]] };
        byte[] lt = PdfSigner.AddValidationData(signed, junk, [pki.Root]);
        PdfDocument doc = PdfDocument.Open(lt);
        var dss = (PdfDictionary)doc.Resolve(doc.Catalog["DSS"])!;
        Assert.IsNull(dss["OCSPs"]);
        Assert.IsNull(dss["CRLs"]);

        // Garbage streams placed in the store by someone else do not break verification.
        var update = new IncrementalUpdate(signed);
        PdfDocument edited = update.Doc;
        edited.Catalog.Set("DSS", new PdfDictionary
        {
            ["Certs"] = PdfArray.Of(edited.AddObject(PdfStream.FromData([0x30, 0x82, 0x00]))),
            ["OCSPs"] = PdfArray.Of(edited.AddObject(PdfStream.FromData([0x30, 0x01, 0x02])), PdfNumber.FromInteger(3)),
            ["CRLs"] = PdfArray.Of(edited.AddObject(PdfStream.FromData([0xFF]))),
        });
        update.Changed(edited.CatalogNumber);
        byte[] resaved = update.Write();
        PdfSignatureInfo info = PdfSigner.Verify(resaved, Trusting(pki)).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.IsNotNull(info.Validation);
    }

    [TestMethod]
    public void FloodsOfLookalikeCertificatesAreBounded()
    {
        // Certificates that copy the issuing CA's name each cost a signature check; the work stops at a fixed budget.
        TestPki pki = SharedPki.Value;
        byte[] signed = PdfSigner.Sign(Document(), pki.Signer);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(pki.Intermediate.SubjectName, key, HashAlgorithmName.SHA256);
        X509SignatureGenerator generator = X509SignatureGenerator.CreateForECDsa(key);
        X509Certificate2[] lookalikes =
        [
            .. Enumerable.Range(1, SignatureChecks.MaxChecks + 10).Select(i => request.Create(pki.Intermediate.SubjectName, generator, pki.Now.AddDays(-1), pki.Now.AddDays(1), BitConverter.GetBytes(i))),
        ];

        var watch = System.Diagnostics.Stopwatch.StartNew();
        PdfSignatureInfo info = PdfSigner.Verify(signed, Trusting(pki) with { Certificates = lookalikes }).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.AreEqual(PdfCheckStatus.Indeterminate, info.Validation!.Status);
        StringAssert.Contains(info.Validation.Problems.Single(), "signature checks");

        Assert.ThrowsExactly<PdfFormatException>(() => PdfSigner.AddValidationData(signed, PdfRevocationValues.None, lookalikes));
        Assert.IsLessThan(30_000L, watch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void DamagedTimestampTokensNeverThrow()
    {
        TestPki pki = SharedPki.Value;
        byte[] signed = SignTimestamped(pki);
        string text = Encoding.ASCII.GetString(signed);
        int start = text.IndexOf("/Contents<", StringComparison.Ordinal) + "/Contents<".Length;
        int end = text.IndexOf('>', start);
        var random = new Random(3161);
        for (int round = 0; round < 300; round++)
        {
            byte[] damaged = (byte[])signed.Clone();
            for (int k = 0; k < 1 + round % 4; k++)
            {
                // Hex digits stay hex digits, so the file still parses and the CMS is what breaks.
                int at = start + random.Next(Math.Min(end - start, 9000));
                damaged[at] = (byte)"0123456789ABCDEF"[random.Next(16)];
            }

            foreach (PdfSignatureInfo info in PdfSigner.Verify(damaged, Trusting(pki) with { RevocationSource = pki.GoodRevocation() }))
            {
                if (!info.IsIntact) Assert.IsNotNull(info.Problem);
            }
        }
    }

    private static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false);

    private sealed class RecordingSource(PdfRevocationValues values, List<PdfRevocationRequest> requests) : IPdfRevocationSource
    {
        public Action<PdfRevocationRequest>? OnRequest { get; init; }

        public PdfRevocationValues GetRevocationValues(PdfRevocationRequest request)
        {
            requests.Add(request);
            OnRequest?.Invoke(request);
            return values;
        }
    }
}
