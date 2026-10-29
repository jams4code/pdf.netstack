#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// Opens every PDF the samples wrote, so a sample that writes a broken file fails the run.
// Usage: dotnet run _check.cs -- <file.pdf>...
using PdfNetStack;

int failures = 0;
foreach (string path in args)
{
    try
    {
        PdfDocument doc = PdfDocument.Open(path);
        if (doc.Pages.Count == 0)
        {
            throw new PdfFormatException("no pages");
        }
        Console.WriteLine($"ok   {path}: {doc.Pages.Count} page(s)");
    }
    catch (PdfPasswordException)
    {
        // Encrypted on purpose: the samples that write these open them again with the password.
        Console.WriteLine($"ok   {path}: encrypted");
    }
    catch (Exception ex) when (ex is PdfFormatException or IOException)
    {
        Console.WriteLine($"FAIL {path}: {ex.Message}");
        failures++;
    }
}
return failures == 0 ? 0 : 1;
