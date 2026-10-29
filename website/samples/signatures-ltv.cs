#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// run: build-only needs a time stamping authority and OCSP or CRL services on the network
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Signing;

using var http = new HttpClient();
X509Certificate2 signer = X509CertificateLoader.LoadPkcs12FromFile("signer.pfx", Environment.GetEnvironmentVariable("PFX_PASSWORD"));
X509Certificate2[] chain = [X509CertificateLoader.LoadCertificateFromFile("issuing-ca.cer")];

#region timestamp
// The library builds the RFC 3161 request and checks the reply; your code carries the bytes.
// Use the address of the time stamping authority you have an agreement with.
var tsa = new PdfTimestampClient(request =>
{
    using var content = new ByteArrayContent(request);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
    using HttpResponseMessage reply = http.Send(new HttpRequestMessage(HttpMethod.Post, "https://tsa.example.com/") { Content = content });
    reply.EnsureSuccessStatusCode();
    return reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
});

// PAdES B-T: the signature carries a timestamp from the authority.
byte[] pdf = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), signer, new PdfSignatureOptions
{
    Reason = "Approved",
    Chain = chain,
    Timestamp = tsa,
});
#endregion

#region ltv
// PAdES B-LT: certificates, OCSP responses and CRLs go into the document security store, in an
// incremental update, so the signature can be checked after the certificates expire.
pdf = PdfSigner.AddValidationData(pdf, new HttpRevocationSource(http), chain);

// PAdES B-LTA: a document timestamp over everything, renewed before its own certificate expires.
pdf = PdfSigner.AddDocumentTimestamp(pdf, tsa);
File.WriteAllBytes("contract-lta.pdf", pdf);
#endregion

#region source
// Fetches OCSP responses and CRLs from the addresses each certificate lists.
sealed class HttpRevocationSource(HttpClient http) : IPdfRevocationSource
{
    public PdfRevocationValues GetRevocationValues(PdfRevocationRequest request)
    {
        var ocsp = new List<byte[]>();
        var crls = new List<byte[]>();
        foreach (string url in request.OcspUrls)
        {
            using var content = new ByteArrayContent(request.CreateOcspRequest());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");
            using HttpResponseMessage reply = http.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = content });
            if (reply.IsSuccessStatusCode) ocsp.Add(reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
        if (ocsp.Count == 0)
        {
            foreach (string url in request.CrlUrls)
            {
                crls.Add(http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            }
        }
        return new PdfRevocationValues { OcspResponses = ocsp, Crls = crls };
    }
}
#endregion
