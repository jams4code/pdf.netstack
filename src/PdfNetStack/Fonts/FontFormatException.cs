namespace PdfNetStack.Fonts;

/// <summary>Raised for a font file that is damaged, truncated, or of a kind that is not supported.</summary>
public sealed class FontFormatException : FormatException
{
    /// <summary>Creates the exception with a message.</summary>
    public FontFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public FontFormatException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public FontFormatException() { }
}
