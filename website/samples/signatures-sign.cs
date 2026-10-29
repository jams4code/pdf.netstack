#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: signatures-visible contract-signed.pdf 0.3
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Signing;

// A throwaway self-signed certificate keeps the sample self-contained. Real use loads one from a
// .pfx file (X509CertificateLoader.LoadPkcs12FromFile) or from the certificate store.
using RSA key = RSA.Create(2048);
var request = new CertificateRequest("CN=Sample Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

#region sign
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), cert, new PdfSignatureOptions
{
    Reason = "Approved",
    Location = "Brussels",
    // A visible box on page 1: x and y from the top left of the page, in points.
    Appearance = new PdfSignatureAppearance(PageIndex: 0, X: 340, Y: 30, Width: 210, Height: 60),
});
File.WriteAllBytes("contract-signed.pdf", signed);
#endregion

#region verify
foreach (PdfSignatureInfo s in PdfSigner.Verify(File.ReadAllBytes("contract-signed.pdf")))
{
    Console.WriteLine($"{s.FieldName}: {s.SignerName}, {s.SubFilter}");
    Console.WriteLine($"  intact {s.IsIntact}, whole file {s.CoversWholeDocument}, problem {s.Problem ?? "none"}");
}
#endregion

#region countersign
// A second signature goes into a new incremental update; the first one stays valid.
byte[] twice = PdfSigner.Sign(signed, cert, new PdfSignatureOptions
{
    Reason = "Countersigned",
    Digest = PdfDigestAlgorithm.Sha384,
});
File.WriteAllBytes("contract-countersigned.pdf", twice);
foreach (PdfSignatureInfo s in PdfSigner.Verify(twice))
{
    Console.WriteLine($"{s.FieldName}: intact {s.IsIntact}, whole file {s.CoversWholeDocument}");
}
#endregion

#region tamper
byte[] tampered = (byte[])signed.Clone();
int at = Array.LastIndexOf(tampered, (byte)'%');   // a byte inside the signed range, near the end
tampered[at] = (byte)'#';
PdfSignatureInfo check = PdfSigner.Verify(tampered)[0];
Console.WriteLine($"Tampered: intact {check.IsIntact}, digest matches {check.DigestMatches}");
#endregion
