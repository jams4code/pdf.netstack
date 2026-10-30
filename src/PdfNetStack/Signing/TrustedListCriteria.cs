using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfNetStack.Signing;

/// <summary>
/// The CriteriaList of a qualification element (ETSI TS 119 612, 5.5.9.2.1): which certificates
/// its qualifiers apply to. Evaluation is three valued: a criterion the library does not know,
/// or a certificate extension it cannot read, gives null, and the combination follows (all is
/// false when any part is false, else null when any is null; at least one is true when any is
/// true; none is the negation of at least one).
/// </summary>
internal sealed class TrustedListCriteria
{
    private enum Kind
    {
        All,
        AtLeastOne,
        None,
        KeyUsage,
        Policies,
        ExtendedKeyUsages,
        SubjectAttributes,
        Unknown,
    }

    /// <summary>A list that selects nothing.</summary>
    public static readonly TrustedListCriteria Nothing = new(Kind.AtLeastOne);

    private readonly Kind _kind;
    private readonly List<TrustedListCriteria> _children = [];
    private readonly List<(X509KeyUsageFlags Bit, bool Set)> _bits = [];
    private readonly List<string> _oids = [];

    private TrustedListCriteria(Kind kind) => _kind = kind;

    /// <summary>Reads a CriteriaList element, its nested lists and the other criteria of the specification.</summary>
    public static TrustedListCriteria Read(XmlElement list)
    {
        var result = new TrustedListCriteria(list.GetAttribute("assert") switch
        {
            "all" => Kind.All,
            "atLeastOne" => Kind.AtLeastOne,
            "none" => Kind.None,
            _ => Kind.Unknown,
        });
        if (result._kind == Kind.Unknown) return result;
        foreach (XmlElement e in PdfTrustedList.Elements(list))
        {
            if (e.LocalName == "otherCriteriaList")
            {
                foreach (XmlElement other in PdfTrustedList.Elements(e)) result._children.Add(Criterion(other));
            }
            else if (e.LocalName != "Description")
            {
                result._children.Add(Criterion(e));
            }
        }

        return result;
    }

    private static TrustedListCriteria Criterion(XmlElement e)
    {
        switch (e.LocalName)
        {
            case "CriteriaList":
                return Read(e);
            case "KeyUsage":
                var usage = new TrustedListCriteria(Kind.KeyUsage);
                foreach (XmlElement bit in PdfTrustedList.Elements(e).Where(b => b.LocalName == "KeyUsageBit"))
                {
                    X509KeyUsageFlags? flag = Bit(bit.GetAttribute("name"));
                    bool? set = bit.InnerText.Trim() switch { "true" or "1" => true, "false" or "0" => false, _ => null };
                    if (flag is null || set is null) return new TrustedListCriteria(Kind.Unknown);
                    usage._bits.Add((flag.Value, set.Value));
                }

                return usage;
            case "PolicySet":
                return Identifiers(e, Kind.Policies, "PolicyIdentifier");
            case "ExtendedKeyUsage":
                return Identifiers(e, Kind.ExtendedKeyUsages, "KeyPurposeId");
            case "CertSubjectDNAttribute":
                return Identifiers(e, Kind.SubjectAttributes, "AttributeOID");
            default:
                return new TrustedListCriteria(Kind.Unknown);
        }
    }

    /// <summary>The object identifiers in the Identifier element of each wrapper, as dotted strings (an OIDAsURN urn:oid: prefix removed).</summary>
    private static TrustedListCriteria Identifiers(XmlElement e, Kind kind, string wrapper)
    {
        var criteria = new TrustedListCriteria(kind);
        foreach (XmlElement w in PdfTrustedList.Elements(e).Where(x => x.LocalName == wrapper))
        {
            XmlElement? id = PdfTrustedList.Elements(w).FirstOrDefault(x => x.LocalName == "Identifier");
            if (id is null) return new TrustedListCriteria(Kind.Unknown);
            string oid = id.InnerText.Trim();
            if (oid.StartsWith("urn:oid:", StringComparison.OrdinalIgnoreCase)) oid = oid.Substring(8);
            criteria._oids.Add(oid);
        }

        return criteria;
    }

    private static X509KeyUsageFlags? Bit(string name) => name switch
    {
        "digitalSignature" => X509KeyUsageFlags.DigitalSignature,
        "nonRepudiation" or "contentCommitment" => X509KeyUsageFlags.NonRepudiation,
        "keyEncipherment" => X509KeyUsageFlags.KeyEncipherment,
        "dataEncipherment" => X509KeyUsageFlags.DataEncipherment,
        "keyAgreement" => X509KeyUsageFlags.KeyAgreement,
        "keyCertSign" => X509KeyUsageFlags.KeyCertSign,
        "crlSign" => X509KeyUsageFlags.CrlSign,
        "encipherOnly" => X509KeyUsageFlags.EncipherOnly,
        "decipherOnly" => X509KeyUsageFlags.DecipherOnly,
        _ => null,
    };

    /// <summary>Whether the certificate meets the criteria; null when that cannot be decided.</summary>
    public bool? Evaluate(X509Certificate2 certificate)
    {
        try
        {
            switch (_kind)
            {
                case Kind.All:
                case Kind.AtLeastOne:
                case Kind.None:
                    bool any = false, unknown = false, allTrue = true;
                    foreach (TrustedListCriteria child in _children)
                    {
                        bool? r = child.Evaluate(certificate);
                        if (r == true) any = true;
                        else if (r == false) allTrue = false;
                        else unknown = true;
                    }

                    return _kind switch
                    {
                        Kind.All => !allTrue ? false : unknown ? null : true,
                        Kind.AtLeastOne => any ? true : unknown ? null : false,
                        _ => any ? false : unknown ? null : true,
                    };
                case Kind.KeyUsage:
                    X509KeyUsageFlags flags = Pkix.KeyUsage(certificate) ?? X509KeyUsageFlags.None;
                    return _bits.TrueForAll(b => ((flags & b.Bit) != 0) == b.Set);
                case Kind.Policies:
                    IReadOnlyList<string> policies = CertificatePolicies.Asserted(certificate);
                    return _oids.TrueForAll(policies.Contains);
                case Kind.ExtendedKeyUsages:
                    IReadOnlyList<string> usages = Pkix.ExtendedKeyUsages(certificate, out _) ?? [];
                    return _oids.TrueForAll(usages.Contains);
                case Kind.SubjectAttributes:
                    var types = new HashSet<string>(StringComparer.Ordinal);
                    foreach (DerNode rdn in DerNode.Parse(certificate.SubjectName.RawData).Children())
                    {
                        foreach (DerNode attribute in rdn.Children()) types.Add(attribute.At(0).OidValue());
                    }

                    return _oids.TrueForAll(types.Contains);
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
