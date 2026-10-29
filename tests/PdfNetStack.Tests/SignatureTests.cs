using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class SignatureTests
{
    private static X509Certificate2 Rsa(string name = "Jamal Abdelkhalek")
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=JADEV", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 Ecdsa()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=EC Signer", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] Document(bool objectStreams = true)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Contract", PdfStandardFont.Helvetica, 20, PdfColor.Black, 72, 72);
        doc.Pages.Add(PdfRectangle.A4);
        return doc.ToArray(new PdfSaveOptions { UseObjectStreams = objectStreams, Optimize = false });
    }

    [TestMethod]
    public void SignsAndVerifiesWithRsa()
    {
        byte[] original = Document();
        byte[] signed = PdfSigner.Sign(original, Rsa(), new PdfSignatureOptions { Reason = "Approval", Location = "Geneva" });

        // An incremental update: the original bytes are untouched at the start.
        CollectionAssert.AreEqual(original, signed.AsSpan(0, original.Length).ToArray());
        PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.IsTrue(info.CoversWholeDocument);
        Assert.AreEqual("Jamal Abdelkhalek", info.SignerName);
        Assert.AreEqual("Approval", info.Reason);
        Assert.AreEqual("Geneva", info.Location);
        Assert.AreEqual("ETSI.CAdES.detached", info.SubFilter);
        Assert.AreEqual("Signature1", info.FieldName);

        PdfDocument reopened = PdfDocument.Open(signed);
        Assert.HasCount(2, reopened.Pages);
        Assert.AreEqual(3, ((PdfNumber)((PdfDictionary)reopened.Resolve(reopened.Catalog["AcroForm"])!)["SigFlags"]!).ToDouble());
    }

    [TestMethod]
    public void SignsAndVerifiesWithEcdsaAndSha384()
    {
        byte[] signed = PdfSigner.Sign(Document(), Ecdsa(), new PdfSignatureOptions { Digest = PdfDigestAlgorithm.Sha384 });
        PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.AreEqual("EC Signer", info.SignerName);
    }

    [TestMethod]
    public void TamperingIsDetected()
    {
        byte[] signed = PdfSigner.Sign(Document(), Rsa());
        byte[] tampered = (byte[])signed.Clone();
        int at = tampered.AsSpan().IndexOf("Contract"u8);
        if (at < 0)
        {
            // Content is compressed: change a byte near the start of the body instead.
            at = 40;
        }

        tampered[at] ^= 0x01;
        PdfSignatureInfo info = PdfSigner.Verify(tampered).Single();
        Assert.IsFalse(info.DigestMatches);
        Assert.IsFalse(info.IsIntact);
        Assert.IsNotNull(info.Problem);
    }

    [TestMethod]
    public void SecondSignatureKeepsTheFirstValid()
    {
        byte[] once = PdfSigner.Sign(Document(), Rsa("First Signer"));
        byte[] twice = PdfSigner.Sign(once, Rsa("Second Signer"), new PdfSignatureOptions { Reason = "Countersigned" });

        IReadOnlyList<PdfSignatureInfo> all = PdfSigner.Verify(twice);
        Assert.HasCount(2, all);
        Assert.IsTrue(all.All(s => s.IsIntact), string.Join("; ", all.Select(s => s.Problem)));
        Assert.AreEqual("Signature1", all[0].FieldName);
        Assert.AreEqual("Signature2", all[1].FieldName);
        Assert.IsFalse(all[0].CoversWholeDocument);
        Assert.IsTrue(all[1].CoversWholeDocument);
    }

    [TestMethod]
    public void ClassicCrossReferenceFilesAreUpdatedInKind()
    {
        byte[] signed = PdfSigner.Sign(Document(objectStreams: false), Rsa());
        string tail = System.Text.Encoding.ASCII.GetString(signed, signed.Length - 600, 600);
        StringAssert.Contains(tail, "\ntrailer\n");
        Assert.IsTrue(PdfSigner.Verify(signed).Single().IsIntact);
        Assert.HasCount(2, PdfDocument.Open(signed).Pages);
    }

    [TestMethod]
    public void VisibleSignaturesHaveAnAppearance()
    {
        var look = new PdfSignatureAppearance(1, 350, 700, 200, 60);
        byte[] signed = PdfSigner.Sign(Document(), Rsa(), new PdfSignatureOptions { Reason = "Approval", Appearance = look });
        PdfDocument doc = PdfDocument.Open(signed);
        PdfAnnotation widget = doc.Pages[1].Annotations.Single();
        Assert.AreEqual("Widget", widget.Subtype);
        Assert.AreEqual(new PdfRectangle(350, 82, 550, 142), widget.Rectangle);
        Assert.IsNotNull(widget.Dictionary["AP"]);
        Assert.IsTrue(PdfSigner.Verify(signed).Single().IsIntact);
    }

    [TestMethod]
    public void RefusesWhatItCannotSign()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Pages.Add(PdfRectangle.A4);
        doc.Security.Encrypt("u", "o");
        byte[] encrypted = doc.ToArray();
        Assert.ThrowsExactly<NotSupportedException>(() => PdfSigner.Sign(encrypted, Rsa()));

        using X509Certificate2 withKey = Rsa();
        using X509Certificate2 publicOnly = Compat.LoadCertificate(withKey.RawData);
        Assert.ThrowsExactly<ArgumentException>(() => PdfSigner.Sign(Document(), publicOnly));
    }
}
