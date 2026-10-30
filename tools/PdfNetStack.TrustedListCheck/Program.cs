using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using PdfNetStack.Signing;

return await PdfNetStack.TrustedListCheck.Check.Run(args).ConfigureAwait(false);

namespace PdfNetStack.TrustedListCheck
{
    /// <summary>
    /// Reads the live EU list of trusted lists and every national XML list it points to with
    /// <see cref="PdfTrustedList"/>, and prints each list's signature check and service counts.
    /// A manual check that the library reads and verifies real lists; the tests never download.
    /// </summary>
    internal static class Check
    {
        private const string Usage = """
            Usage:
              dotnet run --project tools/PdfNetStack.TrustedListCheck -- <certificates> [lotl-url]
              dotnet run --project tools/PdfNetStack.TrustedListCheck -- --keyinfo [lotl-url]

            <certificates> is a file of PEM certificates that may sign the list of lists, as the
            Official Journal of the EU publishes them. --keyinfo instead takes the certificate the
            list carries in its own signature, which checks the canonicalization and signature
            arithmetic but is not a trust decision. The default URL is the EU list of trusted lists.
            """;

        private const string DefaultLotl = "https://ec.europa.eu/tools/lotl/eu-lotl.xml";

        public static async Task<int> Run(string[] args)
        {
            if (args.Length is < 1 or > 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string url = args.Length > 1 ? args[1] : DefaultLotl;
            byte[] lotlXml = await http.GetByteArrayAsync(new Uri(url)).ConfigureAwait(false);
            List<X509Certificate2> anchors = args[0] == "--keyinfo" ? KeyInfo(lotlXml) : Pem(File.ReadAllText(args[0]));
            PdfTrustedList lotl = PdfTrustedList.Load(lotlXml, anchors);
            Print(lotl, url);
            if (lotl.Signature != PdfCheckStatus.Passed) return 1;

            int failures = 0;
            foreach (PdfTrustedListPointer pointer in lotl.Pointers)
            {
                if (pointer.MimeType is string mime && !mime.Contains("xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (pointer.Type is string type && type.EndsWith("listofthelists", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    byte[] xml = await http.GetByteArrayAsync(new Uri(pointer.Location)).ConfigureAwait(false);
                    PdfTrustedList list = PdfTrustedList.Load(xml, lotl);
                    Print(list, pointer.Location);
                    if (list.Signature != PdfCheckStatus.Passed) failures++;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException)
                {
                    Console.WriteLine(pointer.Territory + "  cannot be read: " + e.Message);
                    failures++;
                }
            }

            Console.WriteLine(failures == 0 ? "Every list verified." : failures.ToString(CultureInfo.InvariantCulture) + " lists did not verify or could not be read.");
            return failures == 0 ? 0 : 1;
        }

        private static void Print(PdfTrustedList list, string from)
        {
            List<PdfTrustService> services = [.. list.Services];
            int granted = services.Count(s => s.TypeIdentifier == PdfTrustServiceTypes.CaQc && s.Status == PdfTrustServiceStatus.Granted);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{list.Territory,-3} #{list.SequenceNumber,-4} signature {list.Signature,-10} providers {list.Providers.Count,4} services {services.Count,5} granted CA/QC {granted,4}  {from}"));
            if (list.SignatureProblem is string problem) Console.WriteLine("    " + problem);
        }

        private static List<X509Certificate2> Pem(string text)
        {
            var certificates = new List<X509Certificate2>();
            const string Begin = "-----BEGIN CERTIFICATE-----", End = "-----END CERTIFICATE-----";
            for (int at = text.IndexOf(Begin, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Begin, at + 1, StringComparison.Ordinal))
            {
                int end = text.IndexOf(End, at, StringComparison.Ordinal);
                if (end < 0) break;
                string body = text.Substring(at + Begin.Length, end - at - Begin.Length);
                certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(body)));
            }

            return certificates;
        }

        /// <summary>The certificates in the list's own KeyInfo. Not a trust decision: only a check of the verification arithmetic.</summary>
        private static List<X509Certificate2> KeyInfo(byte[] xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument();
            using (var stream = new MemoryStream(xml))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                doc.Load(reader);
            }

            var names = new XmlNamespaceManager(doc.NameTable);
            names.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
            var certificates = new List<X509Certificate2>();
            foreach (XmlNode node in doc.SelectNodes("/*/ds:Signature/ds:KeyInfo/ds:X509Data/ds:X509Certificate", names)!)
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(node.InnerText)));
            }

            return certificates;
        }
    }
}
