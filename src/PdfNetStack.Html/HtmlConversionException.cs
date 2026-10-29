namespace PdfNetStack.Html;

/// <summary>
/// Raised when a document goes past a limit of the conversion, such as
/// <see cref="HtmlConverterOptions.MaxPages"/>. The HTML may be fine, only too large for the limits
/// set; raise them to convert it.
/// </summary>
public sealed class HtmlConversionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public HtmlConversionException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public HtmlConversionException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public HtmlConversionException() { }
}
