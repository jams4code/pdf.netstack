#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Signing;

// A small certificate hierarchy made on the spot: a root CA, an issuing CA and a signer. Real
// use loads the signer from a .pfx or the certificate store, and trusts the roots it already has.
using X509Certificate2 root = Issue("CN=Sample Root CA", null, ca: true);
using X509Certificate2 issuing = Issue("CN=Sample Issuing CA", root, ca: true);
using X509Certificate2 signer = Issue("CN=Sample Signer", issuing, ca: false);

byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), signer, new PdfSignatureOptions
{
    Reason = "Approved",
    Chain = [issuing],   // embedded, so a validator can build the path to the root
});
File.WriteAllBytes("contract-signed-chain.pdf", signed);

#region validate
var options = new PdfValidationOptions
{
    TrustAnchors = [root],     // the roots you trust
    CheckRevocation = false,   // no OCSP or CRL data in this sample
};
foreach (PdfSignatureInfo s in PdfSigner.Verify(signed, options))
{
    PdfCertificateValidation v = s.Validation!;
    Console.WriteLine($"{s.FieldName}: intact {s.IsIntact}, certificates {v.Status}, chain {v.Chain}, at {v.TimeSource} time");
    foreach (PdfCertificateCheck c in v.Certificates)
    {
        Console.WriteLine($"  {c.Certificate.GetNameInfo(X509NameType.SimpleName, false)}: validity {c.Validity}, " +
            $"key usage {c.KeyUsage}, revocation {c.Revocation}{(c.IsTrustAnchor ? ", trust anchor" : "")}");
    }
}
#endregion

#region revocation
// With revocation checks on and no revocation data, the result is indeterminate, not passed.
PdfCertificateValidation strict = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [root] })[0].Validation!;
Console.WriteLine($"With revocation checks: {strict.Status}");
foreach (string problem in strict.Problems) Console.WriteLine($"  {problem}");
#endregion

static X509Certificate2 Issue(string subject, X509Certificate2? issuer, bool ca)
{
    using RSA key = RSA.Create(2048);
    var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
        ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
        : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow.AddYears(1);
    if (issuer is null) return request.CreateSelfSigned(from, to);
    byte[] serial = RandomNumberGenerator.GetBytes(9);
    serial[0] &= 0x7F;
    using X509Certificate2 issued = request.Create(issuer, from, issuer.NotAfter < to ? issuer.NotAfter : to, serial);
    return issued.CopyWithPrivateKey(key);
}
