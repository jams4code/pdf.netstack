#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region protect
PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Security.Encrypt(
    userPassword: "read",                      // asked for to open the file; "" lets anyone open it
    ownerPassword: "admin",                    // unlocks every permission
    permissions: PdfPermissions.Print | PdfPermissions.CopyContent);   // AES-256 by default
doc.Save("report-protected.pdf");
#endregion

#region aes128
PdfDocument legacy = PdfDocument.Open("report.pdf");
legacy.Security.Encrypt("read", "admin", PdfPermissions.Print, PdfEncryption.Aes128);   // PDF 1.6 readers
legacy.Save("report-aes128.pdf");
#endregion

#region open
PdfDocument asUser = PdfDocument.Open("report-protected.pdf", password: "read");
Console.WriteLine($"User: owner access {asUser.Security.HasOwnerAccess}, may print {asUser.Security.Permissions.HasFlag(PdfPermissions.Print)}");

try
{
    PdfDocument.Open("report-protected.pdf", password: "wrong");
}
catch (PdfPasswordException ex)
{
    Console.WriteLine(ex.Message);
}
#endregion

#region owner
PdfDocument opened = PdfDocument.Open("report-protected.pdf", password: "admin");
Console.WriteLine($"Owner: was encrypted {opened.Security.WasEncrypted}, will encrypt {opened.Security.WillEncrypt}");
opened.Pages.RemoveAt(0);
opened.Save("report-trimmed.pdf");             // keeps both passwords and the permissions

opened.Security.RemoveEncryption();
opened.Save("report-unprotected.pdf");         // or writes it without protection
#endregion

Console.WriteLine($"Trimmed copy opens with the user password: {PdfDocument.Open("report-trimmed.pdf", "read").Pages.Count} pages");
