namespace PdfNetStack;

/// <summary>Raised for input that cannot be interpreted as PDF syntax.</summary>
public sealed class PdfFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfFormatException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public PdfFormatException() { }
}
