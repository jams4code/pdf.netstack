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
    using PdfDocument doc = PdfDocument.OpenOnDemand(stream);   // reads the xref and the page tree
    Console.WriteLine($"{doc.Pages.Count} pages");
    string text = doc.Pages[3].ExtractText();               // reads page 4's content and fonts only
    Console.WriteLine(text.Split('\n')[0]);
    doc.Info.Title = "Operating manual, archived";
    doc.SaveIncremental(stream);                            // appends the changes to the same file
}
long after = new FileInfo("archive.pdf").Length;
Console.WriteLine($"{before:N0} bytes, then {after:N0}: {after - before:N0} bytes appended");
#endregion

#region options
var options = new PdfLoadOptions
{
    Mode = PdfLoadMode.OnDemand,            // parse objects when used, read stream data when needed
    MaxDecodedStreamBytes = 64 << 20,       // no single stream decodes to more than 64 MiB
    MaxImagePixels = 50_000_000,            // larger images are refused before they are decoded
};
using (PdfDocument doc = PdfDocument.Open("archive.pdf", options))   // the file stays open until Dispose
{
    int characters = 0;
    for (int i = 0; i < doc.Pages.Count; i++)
    {
        characters += doc.Pages[i].ExtractText().Length;
        if (i % 50 == 49) doc.ReleaseUnusedObjects();   // frees what earlier pages parsed; edits are kept
    }

    Console.WriteLine($"{characters:N0} characters read on demand");
}
#endregion

#region async
// Stands in for a request body or a storage download: a stream read once, front to back.
await using (FileStream body = File.OpenRead("manual.pdf"))
{
    var spooled = new PdfLoadOptions
    {
        Mode = PdfLoadMode.OnDemand,
        SpoolThreshold = 8 << 20,                 // up to 8 MiB in memory, past it a temporary file
        TempDirectory = Path.GetTempPath(),       // "/tmp" on AWS Lambda
    };
    using PdfDocument doc = await PdfDocument.OpenAsync(body, spooled);   // asynchronous reads only
    Console.WriteLine($"{doc.Pages.Count} pages; the copy is deleted when the document is disposed");
}
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
    using PdfDocument doc = PdfDocument.OpenOnDemand(input);
    doc.Pages[0].Annotations.AddNote(520, 40, "Filed on 29 October", author: "Records");
    doc.SaveIncremental(output);                            // the signed bytes, then the update
}
foreach (PdfSignatureInfo s in PdfSigner.Verify(File.ReadAllBytes("contract-noted.pdf")))
{
    Console.WriteLine($"{s.FieldName}: intact {s.IsIntact}, whole file {s.CoversWholeDocument}");
}
#endregion
