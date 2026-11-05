using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using PdfNetStack.Fonts;
using PdfNetStack.Html;

namespace PdfLambda;

/// <summary>
/// Converts the HTML posted to the function URL into a PDF. Only the fonts packaged in fonts/
/// and the standard 14 fonts are used, and nothing is fetched from the network: images and style
/// sheets must be inline (data: URIs or style elements).
/// </summary>
internal static class HtmlHandler
{
    private const int MaxHtmlBytes = 1 << 20;

    // A synchronous Lambda response is at most 6 MB, and base64 makes the PDF a third larger.
    private const int MaxPdfBytes = 4 << 20;

    // Loaded once per instance. Fonts are thread safe and shared by every conversion.
    private static readonly HtmlConverterOptions Options = new()
    {
        Fonts = LoadFonts(),
        UseSystemFonts = false,
        MaxPages = 200,
    };

    public static Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        if (request.RequestContext?.Http?.Method != "POST") return Task.FromResult(Text(405, "POST the HTML as the request body."));
        string body = request.Body ?? "";
        string html = request.IsBase64Encoded ? Encoding.UTF8.GetString(Convert.FromBase64String(body)) : body;
        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes) return Task.FromResult(Text(413, "The HTML is over 1 MB."));

        byte[] pdf;
        try
        {
            pdf = HtmlConverter.ConvertToPdf(html, Options);
        }
        catch (HtmlConversionException e)
        {
            return Task.FromResult(Text(422, e.Message));
        }

        if (pdf.Length > MaxPdfBytes) return Task.FromResult(Text(413, "The PDF is too large for a synchronous response; write it to S3 instead."));
        context.Logger.LogInformation($"Converted {html.Length} characters to {pdf.Length} bytes.");
        return Task.FromResult(new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/pdf" },
            Body = Convert.ToBase64String(pdf),
            IsBase64Encoded = true,
        });
    }

    private static APIGatewayHttpApiV2ProxyResponse Text(int status, string message) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain; charset=utf-8" },
        Body = message,
    };

    /// <summary>The .ttf, .otf, .woff and .woff2 files deployed in fonts/ next to the executable.</summary>
    private static PdfFontCollection LoadFonts()
    {
        var fonts = new PdfFontCollection();
        fonts.AddDirectory(Path.Combine(AppContext.BaseDirectory, "fonts"));
        return fonts;
    }
}
