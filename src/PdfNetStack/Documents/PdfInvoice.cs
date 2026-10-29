using System.Text.RegularExpressions;
using System.Xml;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>The Factur-X and ZUGFeRD profile of an invoice's XML, from the least data to the most.</summary>
public enum PdfInvoiceProfile
{
    /// <summary>MINIMUM: the invoice reference data only; not a complete invoice in every country.</summary>
    Minimum,

    /// <summary>BASIC WL: document level data without lines.</summary>
    BasicWL,

    /// <summary>BASIC: the EN 16931 core with lines.</summary>
    Basic,

    /// <summary>EN 16931 (also called COMFORT): the European standard's semantic model.</summary>
    EN16931,

    /// <summary>EXTENDED: EN 16931 with additional data.</summary>
    Extended,

    /// <summary>XRECHNUNG: the German public sector's EN 16931 specialisation.</summary>
    XRechnung,
}

/// <summary>How an associated file relates to the document (ISO 32000-2, /AFRelationship).</summary>
public enum PdfFileRelationship
{
    /// <summary>The file holds data the document's content is drawn from, as with MINIMUM and BASIC WL invoices.</summary>
    Data,

    /// <summary>The file is the source the document was made from.</summary>
    Source,

    /// <summary>The file is an equivalent representation of the document, as a full invoice XML is.</summary>
    Alternative,
}

/// <summary>Options for <see cref="PdfInvoice.Attach"/>.</summary>
public sealed record PdfInvoiceOptions
{
    /// <summary>
    /// The attachment's name. Default factur-x.xml, the name Factur-X and ZUGFeRD 2 readers look
    /// for; xrechnung.xml is accepted for the XRECHNUNG profile, as earlier ZUGFeRD 2 versions use.
    /// </summary>
    public string FileName { get; init; } = PdfInvoice.FacturXFileName;

    /// <summary>The relationship; null for Data with MINIMUM and BASIC WL, Alternative with the others.</summary>
    public PdfFileRelationship? Relationship { get; init; }

    /// <summary>The Factur-X version written to the metadata (fx:Version). Default 1.0.</summary>
    public string Version { get; init; } = "1.0";

    /// <summary>The attachment's modification date; null for now.</summary>
    public DateTimeOffset? Modified { get; init; }
}

/// <summary>
/// Factur-X and ZUGFeRD hybrid invoices: a PDF/A-3 document with the invoice XML embedded as an
/// associated file and described in the XMP metadata with the Factur-X extension schema.
/// </summary>
/// <remarks>
/// The XML must be well formed; its business rules (EN 16931 and the profile's restrictions) are
/// not validated, use a dedicated validator for that. Save the document with
/// <see cref="PdfConformance.PdfA3B"/> or <see cref="PdfConformance.PdfA3U"/>; saving checks that
/// the attachment is still there with an allowed name and relationship.
/// </remarks>
public static class PdfInvoice
{
    /// <summary>The attachment name Factur-X 1.0 and ZUGFeRD 2 define.</summary>
    public const string FacturXFileName = "factur-x.xml";

    /// <summary>The attachment name earlier ZUGFeRD 2 versions use for the XRECHNUNG profile.</summary>
    public const string XRechnungFileName = "xrechnung.xml";

    internal const string Namespace = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>Embeds <paramref name="xml"/> as the document's invoice and records the profile for the PDF/A-3 metadata.</summary>
    /// <returns>The attachment.</returns>
    /// <exception cref="ArgumentException">The XML is not well formed, or the file name or relationship is not allowed.</exception>
    public static PdfAttachment Attach(PdfDocument document, byte[] xml, PdfInvoiceProfile profile, PdfInvoiceOptions? options = null)
    {
        Compat.ThrowIfNull(document, nameof(document));
        Compat.ThrowIfNull(xml, nameof(xml));
        options ??= new PdfInvoiceOptions();
        if (!AllowedName(options.FileName, profile))
        {
            throw new ArgumentException($"The invoice attachment must be named {FacturXFileName}" + (profile == PdfInvoiceProfile.XRechnung ? $" or {XRechnungFileName}." : "."), nameof(options));
        }

        if (!IsVersion(options.Version)) throw new ArgumentException("The version must be digits and dots, such as 1.0.", nameof(options));
        CheckWellFormed(xml);

        PdfFileRelationship relationship = options.Relationship ?? (profile is PdfInvoiceProfile.Minimum or PdfInvoiceProfile.BasicWL ? PdfFileRelationship.Data : PdfFileRelationship.Alternative);
        if (relationship is not (PdfFileRelationship.Data or PdfFileRelationship.Source or PdfFileRelationship.Alternative)) throw new ArgumentException("Unknown relationship.", nameof(options));

        // Another invoice attachment would leave readers guessing which one counts.
        foreach (string other in new[] { FacturXFileName, XRechnungFileName, "zugferd-invoice.xml", "ZUGFeRD-invoice.xml" })
        {
            if (other != options.FileName) document.Attachments.Remove(other);
        }

        PdfAttachment attachment = document.Attachments.Add(options.FileName, xml, "text/xml", "Factur-X/ZUGFeRD invoice", options.Modified ?? DateTimeOffset.UtcNow);
        attachment.Dictionary.Set("AFRelationship", new PdfName(relationship.ToString()));
        document.Invoice = new InvoiceMetadata(options.FileName, Level(profile), options.Version);
        return attachment;
    }

    internal static string Level(PdfInvoiceProfile profile) => profile switch
    {
        PdfInvoiceProfile.Minimum => "MINIMUM",
        PdfInvoiceProfile.BasicWL => "BASIC WL",
        PdfInvoiceProfile.Basic => "BASIC",
        PdfInvoiceProfile.EN16931 => "EN 16931",
        PdfInvoiceProfile.Extended => "EXTENDED",
        PdfInvoiceProfile.XRechnung => "XRECHNUNG",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Digits and single dots, such as 1.0.</summary>
    private static bool IsVersion(string? version) =>
        !string.IsNullOrEmpty(version) && version!.Split('.').All(part => part.Length > 0 && part.All(c => c is >= '0' and <= '9'));

    private static bool AllowedName(string name, PdfInvoiceProfile profile) =>
        name == FacturXFileName || (profile == PdfInvoiceProfile.XRechnung && name == XRechnungFileName);

    /// <summary>Checks the XML parses, without resolving DTDs or external entities.</summary>
    private static void CheckWellFormed(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException e)
        {
            throw new ArgumentException("The invoice XML is not well formed: " + e.Message, nameof(xml), e);
        }
    }

    /// <summary>
    /// What a PDF/A-3 save needs about the invoice: the metadata recorded by <see cref="Attach"/>,
    /// or read back from the XMP of an opened invoice. Problems are added when the attachment is
    /// missing or has a relationship Factur-X does not allow.
    /// </summary>
    internal static InvoiceMetadata? Check(PdfDocument doc, List<string> problems)
    {
        InvoiceMetadata? invoice = doc.Invoice ?? FromXmp(doc);
        if (invoice is null) return null;
        PdfAttachment? attachment = doc.Attachments[invoice.FileName];
        if (attachment is null)
        {
            problems.Add($"The invoice attachment {invoice.FileName} is missing.");
            return invoice;
        }

        string? relationship = attachment.Dictionary.GetName("AFRelationship");
        if (relationship is not ("Data" or "Source" or "Alternative")) problems.Add($"The invoice attachment's relationship must be Data, Source or Alternative, not {relationship ?? "missing"}.");
        return invoice;
    }

    /// <summary>The Factur-X values of the document's current XMP, or null.</summary>
    private static InvoiceMetadata? FromXmp(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog["Metadata"]) is not PdfStream stream) return null;
        string xmp;
        try
        {
            xmp = System.Text.Encoding.UTF8.GetString(doc.GetStreamData(stream));
        }
        catch (PdfFormatException)
        {
            return null;
        }

        if (!Compat.Contains(xmp, Namespace, StringComparison.Ordinal)) return null;
        string? file = Value(xmp, "DocumentFileName"), level = Value(xmp, "ConformanceLevel"), version = Value(xmp, "Version");
        return file is null || level is null ? null : new InvoiceMetadata(file, level, version ?? "1.0");
    }

    /// <summary>An fx: property written as an element or as an attribute.</summary>
    private static string? Value(string xmp, string name)
    {
        // Each group is a single character class bounded by its delimiter, so matching is linear.
        Match m;
        try
        {
            m = Regex.Match(xmp, "<fx:" + name + ">([^<]*)</fx:" + name + ">|fx:" + name + "=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!m.Success) return null;
        string value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return System.Net.WebUtility.HtmlDecode(value).Trim();
    }
}

/// <summary>The Factur-X XMP properties: attachment name, conformance level and version.</summary>
internal sealed record InvoiceMetadata(string FileName, string ConformanceLevel, string Version);
