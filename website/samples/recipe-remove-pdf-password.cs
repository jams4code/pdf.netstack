#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

// A protected file to start from.
PdfDocument original = PdfDocument.Open("report.pdf");
original.Security.Encrypt(userPassword: "open-me", ownerPassword: "owner-secret");
original.Save("report-protected.pdf");

#region short
PdfDocument doc = PdfDocument.Open("report-protected.pdf", password: "owner-secret");
doc.Security.RemoveEncryption();
doc.Save("report-unprotected.pdf");
#endregion

Console.WriteLine($"Encrypted again: {PdfDocument.Open("report-unprotected.pdf").Security.WasEncrypted}");
