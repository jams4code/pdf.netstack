using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfNetStack.Signing;

/// <summary>What a qualified certificate is issued for (ETSI EN 319 412-5, QcType).</summary>
public enum PdfQcType
{
    /// <summary>Electronic signatures (id-etsi-qct-esign).</summary>
    ElectronicSignature,

    /// <summary>Electronic seals (id-etsi-qct-eseal).</summary>
    ElectronicSeal,

    /// <summary>Website authentication (id-etsi-qct-web).</summary>
    WebsiteAuthentication,
}

/// <summary>
/// The qualification a signature has by what its certificate claims, named as ETSI TS 119 102-2
/// names signature qualifications. It is read from the certificate's QCStatements alone: whether
/// the issuer is a qualified trust service provider, and whether a key is really in a qualified
/// device, is confirmed by an EU trusted list, which the library does not download.
/// </summary>
public enum PdfQualification
{
    /// <summary>AdES: the certificate does not claim EU qualified status (no QcCompliance, or compliance with the legislation of a country outside the EU).</summary>
    AdES,

    /// <summary>AdES-QC: an EU qualified certificate that is neither for signatures nor for seals alone, without a key in a qualified device.</summary>
    AdESQC,

    /// <summary>AdESig-QC: an EU qualified certificate for electronic signatures, without a key in a qualified device.</summary>
    AdESigQC,

    /// <summary>AdESeal-QC: an EU qualified certificate for electronic seals, without a key in a qualified device.</summary>
    AdESealQC,

    /// <summary>QES: an EU qualified certificate with the key in a qualified device, neither for signatures nor for seals alone.</summary>
    QES,

    /// <summary>QESig: a qualified electronic signature, from a qualified certificate for signatures with the key in a qualified signature creation device.</summary>
    QESig,

    /// <summary>QESeal: a qualified electronic seal, from a qualified certificate for seals with the key in a qualified seal creation device.</summary>
    QESeal,
}

/// <summary>Where a PKI disclosure statement can be read (QcPDS), and its language.</summary>
/// <param name="Url">The statement's address.</param>
/// <param name="Language">Its language, an ISO 639-1 code.</param>
public sealed record PdfQcDisclosureStatement(string Url, string Language);

/// <summary>A transaction limit (QcLimitValue): the amount and the ISO 4217 currency, alphabetic or numeric.</summary>
/// <param name="Currency">The currency code, such as EUR or 978.</param>
/// <param name="Amount">The limit: amount times ten to the exponent.</param>
public sealed record PdfQcLimit(string Currency, decimal Amount);

/// <summary>
/// The qualified certificate statements of a certificate (RFC 3739 and ETSI EN 319 412-5), as
/// the certificate states them.
/// </summary>
public sealed class PdfQcStatements
{
    internal PdfQcStatements()
    {
    }

    /// <summary>QcCompliance: the certificate is issued as a qualified certificate.</summary>
    public bool IsQualified { get; internal set; }

    /// <summary>QcSSCD: the private key is in a qualified signature or seal creation device.</summary>
    public bool KeyInQualifiedDevice { get; internal set; }

    /// <summary>QcType: what the certificate is for. Empty when the statement is absent, which ETSI reads as electronic signatures.</summary>
    public IReadOnlyList<PdfQcType> Types { get; internal set; } = [];

    /// <summary>QcCClegislation: the countries, as ISO 3166 codes, whose law the certificate is qualified under when that is not EU law; empty for EU law.</summary>
    public IReadOnlyList<string> Legislations { get; internal set; } = [];

    /// <summary>QcLimitValue: the transaction limit, or null.</summary>
    public PdfQcLimit? Limit { get; internal set; }

    /// <summary>QcRetentionPeriod: the years registration data is kept after the certificate expires, or null.</summary>
    public int? RetentionYears { get; internal set; }

    /// <summary>QcPDS: where the PKI disclosure statements are.</summary>
    public IReadOnlyList<PdfQcDisclosureStatement> DisclosureStatements { get; internal set; } = [];

    /// <summary>The semantics identifier of the RFC 3739 statement, such as 0.4.0.194121.1.1 for a natural person; null when absent.</summary>
    public string? SemanticsIdentifier { get; internal set; }

    /// <summary>The identifiers of every statement, in order, including those the library does not interpret.</summary>
    public IReadOnlyList<string> StatementIds { get; internal set; } = [];

    /// <summary>The qualification these statements claim for a signature made with the certificate.</summary>
    public PdfQualification Qualification
    {
        get
        {
            if (!IsQualified || Legislations.Count > 0) return PdfQualification.AdES;
            bool signature = Types.Count == 0 || Types.All(t => t == PdfQcType.ElectronicSignature);
            bool seal = Types.Count > 0 && Types.All(t => t == PdfQcType.ElectronicSeal);
            return KeyInQualifiedDevice
                ? signature ? PdfQualification.QESig : seal ? PdfQualification.QESeal : PdfQualification.QES
                : signature ? PdfQualification.AdESigQC : seal ? PdfQualification.AdESealQC : PdfQualification.AdESQC;
        }
    }

    internal const string Oid = "1.3.6.1.5.5.7.1.3";
    private const string Etsi = "0.4.0.1862.1.";
    private const int MaxStatements = 64;

    /// <summary>The statements of a certificate, or null when it has none.</summary>
    /// <exception cref="FormatException">The extension cannot be read.</exception>
    internal static PdfQcStatements? Read(X509Certificate2 certificate)
    {
        if (certificate.Extensions[Oid] is not X509Extension e) return null;
        var result = new PdfQcStatements();
        var ids = new List<string>();
        var types = new List<PdfQcType>();
        var legislations = new List<string>();
        var statements = new List<PdfQcDisclosureStatement>();
        foreach (DerNode statement in DerNode.Parse(e.RawData).Children())
        {
            List<DerNode> parts = statement.Children();
            if (parts.Count == 0) throw new FormatException("A QC statement is empty.");
            string id = parts[0].OidValue();
            ids.Add(id);
            if (ids.Count > MaxStatements) throw new FormatException("A certificate has too many QC statements.");
            DerNode? info = parts.Count > 1 ? parts[1] : null;
            switch (id)
            {
                case Etsi + "1":
                    result.IsQualified = true;
                    break;
                case Etsi + "2" when info is not null:
                    result.Limit = MonetaryValue(info);
                    break;
                case Etsi + "3" when info is not null:
                    long years = info.SmallInteger();
                    result.RetentionYears = years is >= 0 and <= int.MaxValue ? (int)years : throw new FormatException("The retention period is out of range.");
                    break;
                case Etsi + "4":
                    result.KeyInQualifiedDevice = true;
                    break;
                case Etsi + "5" when info is not null:
                    foreach (DerNode location in info.Children()) statements.Add(new PdfQcDisclosureStatement(Ascii(location.At(0)), Ascii(location.At(1))));
                    break;
                case Etsi + "6" when info is not null:
                    foreach (DerNode type in info.Children())
                    {
                        switch (type.OidValue())
                        {
                            case Etsi + "6.1": types.Add(PdfQcType.ElectronicSignature); break;
                            case Etsi + "6.2": types.Add(PdfQcType.ElectronicSeal); break;
                            case Etsi + "6.3": types.Add(PdfQcType.WebsiteAuthentication); break;
                        }
                    }

                    break;
                case Etsi + "7" when info is not null:
                    foreach (DerNode country in info.Children()) legislations.Add(Ascii(country));
                    break;
                case "1.3.6.1.5.5.7.11.1" or "1.3.6.1.5.5.7.11.2" when info is not null:
                    // SemanticsInformation: an optional identifier, then optional registration authorities.
                    List<DerNode> semantics = info.Children();
                    if (semantics.Count > 0 && semantics[0].Tag == Der.ObjectIdentifier) result.SemanticsIdentifier = semantics[0].OidValue();
                    break;
            }
        }

        result.StatementIds = ids;
        result.Types = [.. types.Distinct()];
        result.Legislations = legislations;
        result.DisclosureStatements = statements;
        return result;
    }

    /// <summary>MonetaryValue: currency (alphabetic PrintableString or numeric INTEGER), amount, exponent.</summary>
    private static PdfQcLimit MonetaryValue(DerNode value)
    {
        List<DerNode> parts = value.Children();
        if (parts.Count != 3) throw new FormatException("A transaction limit is incomplete.");
        string currency = parts[0].Tag == Der.Integer ? parts[0].SmallInteger().ToString(System.Globalization.CultureInfo.InvariantCulture) : Ascii(parts[0]);
        long amount = parts[1].SmallInteger(), exponent = parts[2].SmallInteger();
        if (exponent is < -28 or > 28) throw new FormatException("A transaction limit's exponent is out of range.");
        try
        {
            decimal limit = amount;
            for (long i = 0; i < Math.Abs(exponent); i++) limit = exponent > 0 ? limit * 10 : limit / 10;
            return new PdfQcLimit(currency, limit);
        }
        catch (OverflowException)
        {
            throw new FormatException("A transaction limit is out of range.");
        }
    }

    private static string Ascii(DerNode value) => Encoding.ASCII.GetString(value.Content.ToArray());
}
