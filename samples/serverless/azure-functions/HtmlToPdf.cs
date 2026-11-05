using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using PdfNetStack.Fonts;
using PdfNetStack.Html;

namespace PdfFunctions;

/// <summary>
/// POST /api/html-to-pdf with HTML as the body returns the PDF. Only the fonts packaged in fonts/
/// and the standard 14 fonts are used, and nothing is fetched from the network: images and style
/// sheets must be inline (data: URIs or style elements).
/// </summary>
public sealed partial class HtmlToPdf(ILogger<HtmlToPdf> logger)
{
    private const int MaxHtmlBytes = 1 << 20;

    // Loaded once per worker process and shared by every invocation it runs, concurrent ones
    // included: fonts and options are thread safe.
    private static readonly HtmlConverterOptions Options = new()
    {
        Fonts = LoadFonts(),
        UseSystemFonts = false,
        MaxPages = 200,
    };

    [Function(nameof(HtmlToPdf))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "html-to-pdf")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        string? html = await ReadAsync(request.Body, cancellationToken);
        if (html is null) return await TextAsync(request, HttpStatusCode.RequestEntityTooLarge, "The HTML is over 1 MB.");

        // The PDF is written straight to the response body with asynchronous writes only, which the
        // isolated worker requires.
        HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/pdf");
        try
        {
            await HtmlConverter.ConvertToPdfAsync(html, response.Body, Options, cancellationToken);
        }
        catch (HtmlConversionException e)
        {
            // Raised by layout, before any byte reaches the body, so another response can go instead.
            return await TextAsync(request, HttpStatusCode.UnprocessableEntity, e.Message);
        }

        Converted(logger, html.Length);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Converted {Characters} characters to PDF.")]
    private static partial void Converted(ILogger logger, int characters);

    /// <summary>The body as UTF-8 text, or null once it passes <see cref="MaxHtmlBytes"/>.</summary>
    private static async Task<string?> ReadAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxHtmlBytes) return null;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task<HttpResponseData> TextAsync(HttpRequestData request, HttpStatusCode status, string message)
    {
        HttpResponseData response = request.CreateResponse(status);
        await response.WriteStringAsync(message);
        return response;
    }

    /// <summary>The .ttf, .otf, .woff and .woff2 files deployed in fonts/ next to the app.</summary>
    private static PdfFontCollection LoadFonts()
    {
        var fonts = new PdfFontCollection();
        fonts.AddDirectory(Path.Combine(AppContext.BaseDirectory, "fonts"));
        return fonts;
    }
}
