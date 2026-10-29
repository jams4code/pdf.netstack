#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// preview: html-assets assets.pdf 0.4
using System.Text;
using PdfNetStack;
using PdfNetStack.Html;

// A shared style sheet that, in production, would come from a web server.
var remote = new Dictionary<string, string>
{
    ["https://assets.example.com/brand.css"] = "h1 { color: #1e3a8a } .tag { background: #dbeafe; border-radius: 4pt; padding: 1pt 5pt }",
};

#region html
string html = """
    <html lang="en">
    <head>
      <link rel="stylesheet" href="https://assets.example.com/brand.css">
      <style>
        @font-face { font-family: Inter; src: url(fonts/Inter-Regular.woff2) format("woff2") }
        @font-face { font-family: Inter; font-weight: 700; src: url(fonts/Inter-Bold.woff2) format("woff2") }
        @font-face { font-family: "Noto Sans Arabic"; src: url(fonts/NotoSansArabic-Regular.woff2) }
        body { font-family: Inter, "Noto Sans Arabic", sans-serif; font-size: 10pt }
        code { font-family: monospace }
        .photo { width: 100%; height: 120pt; object-fit: cover; border-radius: 8pt; margin-bottom: 8pt }
        .row { display: flex; gap: 12pt; align-items: center }
      </style>
    </head>
    <body>
      <h1>Assets and fonts</h1>
      <img class="photo" src="photo.jpg" alt="Landscape photograph">
      <div class="row">
        <img src="logo.png" alt="Logo" width="90">
        <svg width="40" height="40" viewBox="0 0 40 40"><circle cx="20" cy="20" r="18" fill="#2563eb"/></svg>
        <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='40' height='40'%3E%3Crect width='40' height='40' rx='8' fill='%2314b8a6'/%3E%3C/svg%3E" alt="">
        <span class="tag">Inter from WOFF2</span>
      </div>
      <p>Arabic comes from the second family in the list: <span dir="rtl">مرحبا بالعالم</span></p>
      <p>Generic families need no embedded font: <code>monospace</code> is Courier.</p>
    </body>
    </html>
    """;
#endregion

#region loader
string folder = Path.GetFullPath(".") + Path.DirectorySeparatorChar;
var options = new HtmlConverterOptions
{
    BaseUri = new Uri(folder),                           // fonts/, photo.jpg and logo.png resolve here
    UseSystemFonts = false,                              // only @font-face and the standard 14 fonts
    ResourceLoader = uri =>
    {
        // With a loader, every address comes here: decide per address, null means "do not load".
        if (uri.IsFile) return Path.GetFullPath(uri.LocalPath).StartsWith(folder) ? File.ReadAllBytes(uri.LocalPath) : null;
        if (uri.Host != "assets.example.com") return null;
        return remote.TryGetValue(uri.AbsoluteUri, out string? css) ? Encoding.UTF8.GetBytes(css) : null;
    },
};
#endregion

PdfDocument doc = HtmlConverter.ConvertToDocument(html, options);
doc.Save("assets.pdf");
Console.WriteLine(doc.Pages[0].ExtractText());
