#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack;
using PdfNetStack.Signing;

File.Copy("manual.pdf", "archive.pdf", overwrite: true);

#region on-demand
long before = new FileInfo("archive.pdf").Length;
using (var stream = new FileStream("archive.pdf", FileMode.Open, FileAccess.ReadWrite))
{
    PdfDocument doc = PdfDocument.OpenOnDemand(stream);     // reads the xref and the page tree
    Console.WriteLine($"{doc.Pages.Count} pages");
    string text = doc.Pages[3].ExtractText();               // reads page 4's content and fonts only
    Console.WriteLine(text.Split('\n')[0]);
    doc.Info.Title = "Operating manual, archived";
    doc.SaveIncremental(stream);                            // appends the changes to the same file
}
long after = new FileInfo("archive.pdf").Length;
Console.WriteLine($"{before:N0} bytes, then {after:N0}: {after - before:N0} bytes appended");
#endregion

// A throwaway self-signed certificate. Real use loads one from a .pfx file or the store.
using RSA key = RSA.Create(2048);
var request = new CertificateRequest("CN=Sample Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
File.WriteAllBytes("contract-signed.pdf", PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), cert));

#region signed
using (var input = File.OpenRead("contract-signed.pdf"))
using (var output = File.Create("contract-noted.pdf"))
{
    PdfDocument doc = PdfDocument.OpenOnDemand(input);
    doc.Pages[0].Annotations.AddNote(520, 40, "Filed on 29 October", author: "Records");
    doc.SaveIncremental(output);                            // the signed bytes, then the update
}
foreach (PdfSignatureInfo s in PdfSigner.Verify(File.ReadAllBytes("contract-noted.pdf")))
{
    Console.WriteLine($"{s.FieldName}: intact {s.IsIntact}, whole file {s.CoversWholeDocument}");
}
#endregion
