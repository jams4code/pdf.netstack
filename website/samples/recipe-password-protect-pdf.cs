#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Security.Encrypt(
    userPassword: "open-me",                     // needed to open the file
    ownerPassword: "owner-secret",               // needed to change it
    permissions: PdfPermissions.Print | PdfPermissions.CopyContent);   // AES-256 by default
doc.Save("report-protected.pdf");

PdfDocument check = PdfDocument.Open("report-protected.pdf", password: "open-me");
Console.WriteLine($"Opened with the user password: {check.Pages.Count} pages, {check.Security.Permissions}");
