using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfNetStack.Signing;

/// <summary>
/// Name constraints along a path (RFC 5280, 4.2.1.10 and 6.1.3 (b)): the permitted subtrees of
/// each CA intersect, the excluded subtrees add up, and every later certificate's subject and
/// alternative names must fall inside the first and outside the second. DNS names, email
/// addresses, URIs (by host), directory names and IP addresses are compared; a constraint on any
/// other name form fails a certificate that carries a name of that form.
/// </summary>
internal sealed class NameConstraints
{
    public const string Oid = "2.5.29.30";
    private const string SubjectAltName = "2.5.29.17";
    private const string EmailAddress = "1.2.840.113549.1.9.1";
    private const int MaxSubtrees = 256;

    private const int Rfc822 = 1, Dns = 2, Directory = 4, Uri = 6, Ip = 7;

    // One list per constraining certificate: a name must match each list that constrains its form.
    private readonly List<List<Subtree>> _permitted = [];
    private readonly List<Subtree> _excluded = [];

    /// <summary>A GeneralName form (its context tag number) and its content: the encoded Name for a directory name.</summary>
    private sealed record Subtree(int Form, byte[] Value);

    /// <summary>True once any certificate added a constraint.</summary>
    public bool Any => _permitted.Count > 0 || _excluded.Count > 0;

    /// <summary>Adds the constraints a CA certificate imposes on the certificates below it.</summary>
    /// <exception cref="FormatException">The extension cannot be read, or uses minimum or maximum, which RFC 5280 leaves unused.</exception>
    public void Add(X509Certificate2 certificate)
    {
        if (certificate.Extensions[Oid] is not X509Extension e) return;
        foreach (DerNode part in DerNode.Parse(e.RawData).Children())
        {
            if (part.Tag is not (0xA0 or 0xA1)) throw new FormatException("Name constraints hold an unknown part.");
            var subtrees = new List<Subtree>();
            foreach (DerNode subtree in part.Children())
            {
                List<DerNode> fields = subtree.Children();
                if (fields.Count != 1) throw new FormatException("A name constraint sets a minimum or maximum.");
                subtrees.Add(new Subtree(fields[0].Tag & 0x1F, Value(fields[0])));
                if (subtrees.Count + _excluded.Count > MaxSubtrees) throw new FormatException("Name constraints are too many.");
            }

            if (part.Tag == 0xA0) _permitted.Add(subtrees);
            else _excluded.AddRange(subtrees);
        }
    }

    /// <summary>Why a certificate's names break the constraints added so far, or null when they do not.</summary>
    /// <exception cref="FormatException">The certificate's names cannot be read.</exception>
    public string? Check(X509Certificate2 certificate)
    {
        foreach ((int form, byte[] name) in Names(certificate))
        {
            if (form is not (Rfc822 or Dns or Directory or Uri or Ip))
            {
                if (_excluded.Any(s => s.Form == form) || _permitted.Any(l => l.Any(s => s.Form == form))) return "its " + FormName(form) + " cannot be checked against the name constraints";
                continue;
            }

            string shown = Show(form, name);
            if (_excluded.Any(s => s.Form == form && Matches(form, name, s.Value) != false)) return "its name " + shown + " is excluded by a name constraint";
            foreach (List<Subtree> permitted in _permitted)
            {
                List<Subtree> same = [.. permitted.Where(s => s.Form == form)];
                if (same.Count > 0 && !same.Any(s => Matches(form, name, s.Value) == true)) return "its name " + shown + " is outside the permitted names";
            }
        }

        return null;
    }

    /// <summary>The subject (when not empty), the alternative names, and, without alternative names, email addresses in the subject.</summary>
    private static List<(int Form, byte[] Name)> Names(X509Certificate2 certificate)
    {
        var names = new List<(int, byte[])>();
        byte[] subject = certificate.SubjectName.RawData;
        List<DerNode> rdns = DerNode.Parse(subject).Children();
        if (rdns.Count > 0) names.Add((Directory, subject));
        if (certificate.Extensions[SubjectAltName] is X509Extension e)
        {
            foreach (DerNode name in DerNode.Parse(e.RawData).Children()) names.Add((name.Tag & 0x1F, Value(name)));
        }
        else
        {
            foreach (DerNode rdn in rdns)
            {
                foreach (DerNode attribute in rdn.Children())
                {
                    if (attribute.At(0).OidValue() == EmailAddress) names.Add((Rfc822, attribute.At(1).Content.ToArray()));
                }
            }
        }

        return names;
    }

    /// <summary>A GeneralName's value: a directory name's inner Name encoding, otherwise the content bytes.</summary>
    private static byte[] Value(DerNode general) =>
        (general.Tag & 0x1F) == Directory ? general.At(0).Encoded.ToArray() : general.Content.ToArray();

    /// <summary>True or false for a name the constraint covers or not; null when the name cannot be judged, which an exclusion treats as a match.</summary>
    private static bool? Matches(int form, byte[] name, byte[] constraint) => form switch
    {
        Dns => DnsMatches(Ascii(name), Ascii(constraint)),
        Rfc822 => MailMatches(Ascii(name), Ascii(constraint)),
        Uri => Host(Ascii(name)) is string host ? HostMatches(host, Ascii(constraint)) : null,
        Directory => DirectoryMatches(name, constraint),
        Ip => IpMatches(name, constraint),
        _ => null,
    };

    /// <summary>A DNS constraint covers the name itself and every name below it; a leading period means below only.</summary>
    private static bool DnsMatches(string name, string constraint)
    {
        name = name.TrimEnd('.').ToLowerInvariant();
        constraint = constraint.TrimEnd('.').ToLowerInvariant();
        if (constraint.Length == 0) return true;
        if (constraint[0] == '.') return name.EndsWith(constraint, StringComparison.Ordinal);
        return name == constraint || name.EndsWith("." + constraint, StringComparison.Ordinal);
    }

    /// <summary>A mailbox, every mailbox at a host, or with a leading period every mailbox in a domain below it.</summary>
    private static bool MailMatches(string name, string constraint)
    {
        int at = name.LastIndexOf('@');
        if (at <= 0) return false;
        string host = name.Substring(at + 1);
        int c = constraint.LastIndexOf('@');
        if (c > 0)
        {
            // The local part is case sensitive, the host is not.
            return string.Equals(name.Substring(0, at), constraint.Substring(0, c), StringComparison.Ordinal)
                && string.Equals(host, constraint.Substring(c + 1), StringComparison.OrdinalIgnoreCase);
        }

        return HostMatches(host, constraint);
    }

    /// <summary>A host, or with a leading period the hosts below a domain.</summary>
    private static bool HostMatches(string host, string constraint) =>
        constraint.Length > 0 && constraint[0] == '.'
            ? host.EndsWith(constraint, StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, constraint, StringComparison.OrdinalIgnoreCase);

    /// <summary>The host of a URI with an authority; null for a URI without one or with an IP literal.</summary>
    private static string? Host(string uri)
    {
        int scheme = uri.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0) return null;
        string rest = uri.Substring(scheme + 3);
        int end = rest.IndexOfAny(['/', '?', '#']);
        if (end >= 0) rest = rest.Substring(0, end);
        int user = rest.LastIndexOf('@');
        if (user >= 0) rest = rest.Substring(user + 1);
        if (rest.Length > 0 && rest[0] == '[') return null;
        int port = rest.IndexOf(':');
        if (port >= 0) rest = rest.Substring(0, port);
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>The constraint's relative distinguished names begin the name's, compared attribute by attribute.</summary>
    private static bool DirectoryMatches(byte[] name, byte[] constraint)
    {
        List<DerNode> n = DerNode.Parse(name).Children(), c = DerNode.Parse(constraint).Children();
        if (c.Count > n.Count) return false;
        for (int i = 0; i < c.Count; i++)
        {
            List<DerNode> a = n[i].Children(), b = c[i].Children();
            if (a.Count != b.Count || !b.All(x => a.Any(y => SameAttribute(y, x)))) return false;
        }

        return true;
    }

    /// <summary>Same type and value; string values compare case insensitively with white space collapsed (RFC 5280, 7.1).</summary>
    private static bool SameAttribute(DerNode a, DerNode b)
    {
        if (a.At(0).OidValue() != b.At(0).OidValue()) return false;
        DerNode x = a.At(1), y = b.At(1);
        string? s = Text(x), t = Text(y);
        if (s is null || t is null) return x.Encoded.SequenceEqual(y.Encoded);
        return string.Equals(Normalize(s), Normalize(t), StringComparison.Ordinal);
    }

    private static string? Text(DerNode value) => value.Tag switch
    {
        0x0C => Encoding.UTF8.GetString(value.Content.ToArray()),
        0x13 or 0x16 or 0x1A or 0x14 => Compat.Latin1.GetString(value.Content.ToArray()),
        0x1E => Encoding.BigEndianUnicode.GetString(value.Content.ToArray()),
        _ => null,
    };

    private static string Normalize(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (string word in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (b.Length > 0) b.Append(' ');
            b.Append(word.ToUpperInvariant());
        }

        return b.ToString();
    }

    /// <summary>An address against an address and mask of the same family.</summary>
    private static bool IpMatches(byte[] address, byte[] constraint)
    {
        if (constraint.Length != 2 * address.Length || address.Length is not (4 or 16)) return false;
        for (int i = 0; i < address.Length; i++)
        {
            byte mask = constraint[address.Length + i];
            if ((address[i] & mask) != (constraint[i] & mask)) return false;
        }

        return true;
    }

    private static string Ascii(byte[] value) => Encoding.ASCII.GetString(value);

    private static string Show(int form, byte[] name)
    {
        try
        {
            return form switch
            {
                Directory => new X500DistinguishedName(name).Name,
                Ip => name.Length == 4 ? string.Join(".", name) : Compat.ToHex(name),
                _ => Ascii(name),
            };
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "(a directory name)";
        }
    }

    private static string FormName(int form) => form switch
    {
        0 => "other names",
        3 => "X.400 addresses",
        5 => "EDI party names",
        8 => "registered identifiers",
        _ => "names of form " + form,
    };
}
