#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// run: build-only (a function handler calls it; samples/serverless has complete apps)
using PdfNetStack.Fonts;
using PdfNetStack.Html;

byte[] pdf = Pdfs.Convert("<h1>Invoice 42</h1><p>Converted in a function.</p>");
Console.WriteLine($"{pdf.Length} bytes");

#region options
// Created once per instance and shared by every invocation, concurrent ones included.
internal static class Pdfs
{
    private static readonly HtmlConverterOptions Options = new()
    {
        Fonts = LoadFonts(),
        UseSystemFonts = false,   // the host has no fonts, or ones that can change
        MaxPages = 200,
    };

    public static byte[] Convert(string html) => HtmlConverter.ConvertToPdf(html, Options);

    // The fonts deployed with the function, in fonts/ next to the executable.
    private static PdfFontCollection LoadFonts()
    {
        var fonts = new PdfFontCollection();
        fonts.AddDirectory(Path.Combine(AppContext.BaseDirectory, "fonts"));
        return fonts;
    }
}
#endregion
