using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfNetStack.Signing;

/// <summary>
/// Name constraints along a path (RFC 5280, 4.2.1.10 and 6.1.3 (b)): the permitted subtrees of
/// each CA intersect, the excluded subtrees add up, and every later certificate's subject and
/// alternative names must fall inside the first and outside the second. DNS names, email
/// addresses, URIs (by host), directory names and IP addresses are compared; a constraint on any
/// other name form fails a certificate that carries a name of that form. Hosts are compared in
/// one canonical form in every name form, directory strings after RFC 4518 preparation, and a
/// name that cannot be read or judged fails closed: it counts as excluded and as not permitted.
/// </summary>
internal sealed class NameConstraints
{
    public const string Oid = "2.5.29.30";
    private const string SubjectAltName = "2.5.29.17";
    private const string EmailAddress = "1.2.840.113549.1.9.1";
    private const int MaxSubtrees = 256;

    /// <summary>
    /// False when the runtime has no Unicode data for NFKC normalization and IDNA mapping, as in
    /// .NET's invariant globalization mode, where both return their input unchanged. Hosts and
    /// directory strings with characters outside ASCII then cannot be put in canonical form, and
    /// fail closed instead of slipping past an excluded subtree written in another form.
    /// </summary>
    private static readonly bool UnicodeMapping = "\uFF21".Normalize(NormalizationForm.FormKC) == "A";

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

    /// <summary>
    /// True or false for a name the constraint covers or not; null when the name or the
    /// constraint cannot be judged. Undecidable fails closed: an exclusion treats it as a match,
    /// a permitted subtree as no match.
    /// </summary>
    private static bool? Matches(int form, byte[] name, byte[] constraint) => form switch
    {
        Dns => DnsMatches(Ascii(name), Ascii(constraint)),
        Rfc822 => MailMatches(Ascii(name), Ascii(constraint)),
        Uri => Ascii(name) is string uri && UriHost(uri) is string host ? HostMatches(host, Ascii(constraint)) : null,
        Directory => DirectoryMatches(name, constraint),
        Ip => IpMatches(name, constraint),
        _ => null,
    };

    /// <summary>A DNS constraint covers the name itself and every name below it; a leading period means below only, and an empty one covers everything.</summary>
    private static bool? DnsMatches(string? name, string? constraint)
    {
        if (constraint is null || CanonicalHost(name) is not string host) return null;
        if (constraint.Length == 0) return true;
        bool below = constraint[0] == '.';
        if (CanonicalHost(below ? constraint.Substring(1) : constraint) is not string domain) return null;
        return (!below && host == domain) || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    /// <summary>A mailbox, every mailbox at a host, or with a leading period every mailbox in a domain below it.</summary>
    private static bool? MailMatches(string? name, string? constraint)
    {
        if (name is null || constraint is null) return null;
        int at = name.LastIndexOf('@');
        if (at <= 0 || CanonicalHost(name.Substring(at + 1)) is not string host) return null;
        int c = constraint.LastIndexOf('@');
        if (c > 0)
        {
            // The local part is case sensitive, the host is not.
            if (CanonicalHost(constraint.Substring(c + 1)) is not string mailHost) return null;
            return string.Equals(name.Substring(0, at), constraint.Substring(0, c), StringComparison.Ordinal) && host == mailHost;
        }

        return HostMatches(host, constraint);
    }

    /// <summary>A canonical host against a host, or with a leading period the hosts below a domain.</summary>
    private static bool? HostMatches(string host, string? constraint)
    {
        if (constraint is null || constraint.Length == 0) return null;
        bool below = constraint[0] == '.';
        if (CanonicalHost(below ? constraint.Substring(1) : constraint) is not string domain) return null;
        return below ? host.EndsWith("." + domain, StringComparison.Ordinal) : host == domain;
    }

    /// <summary>
    /// The canonical host of a URI with an authority, read by <see cref="System.Uri"/>; null for a
    /// URI without an authority, with an IP literal, or with anything a reader could take two ways:
    /// white space, control characters, backslashes (which browsers read as slashes) or percent
    /// escapes in the authority.
    /// </summary>
    private static string? UriHost(string uri)
    {
        foreach (char ch in uri)
        {
            if (ch <= ' ' || ch == 0x7F || ch == '\\') return null;
        }

        int scheme = uri.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0) return null;
        string authority = uri.Substring(scheme + 3);
        int end = authority.IndexOfAny(['/', '?', '#']);
        if (end >= 0) authority = authority.Substring(0, end);
        if (authority.IndexOfAny(['%']) >= 0) return null;
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out System.Uri? parsed) || parsed.HostNameType != UriHostNameType.Dns) return null;
        return CanonicalHost(parsed.IdnHost);
    }

    /// <summary>
    /// A host name in the one form every name form is compared in: one trailing dot removed,
    /// internationalized labels in their ASCII (punycode) form, lower case. Null when it is not a
    /// plain host name: empty labels, characters outside letters, digits, hyphens, underscores and
    /// a wildcard, or labels too long.
    /// </summary>
    private static string? CanonicalHost(string? host)
    {
        if (host is null || host.Length is 0 or > 1024) return null;
        if (host[host.Length - 1] == '.') host = host.Substring(0, host.Length - 1);
        if (host.Length == 0) return null;
        string ascii = host;
        if (host.Any(ch => ch > 0x7F))
        {
            if (!UnicodeMapping) return null;
            try
            {
                ascii = new IdnMapping().GetAscii(host);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        ascii = ascii.ToLowerInvariant();
        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63) return null;
            foreach (char ch in label)
            {
                if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '*')) return null;
            }
        }

        return ascii;
    }

    /// <summary>
    /// The constraint's relative distinguished names begin the name's, compared attribute by
    /// attribute. True, false, or null when an attribute cannot be compared.
    /// </summary>
    private static bool? DirectoryMatches(byte[] name, byte[] constraint)
    {
        List<DerNode> n = DerNode.Parse(name).Children(), c = DerNode.Parse(constraint).Children();
        if (c.Count > n.Count) return false;
        bool undecided = false;
        for (int i = 0; i < c.Count; i++)
        {
            List<DerNode> a = n[i].Children(), b = c[i].Children();
            if (a.Count != b.Count) return false;
            foreach (DerNode wanted in b)
            {
                // Each attribute of the constraint needs an equal one in the name's RDN.
                bool? found = false;
                foreach (DerNode candidate in a)
                {
                    bool? same = SameAttribute(candidate, wanted);
                    if (same == true)
                    {
                        found = true;
                        break;
                    }

                    if (same is null) found = null;
                }

                if (found == false) return false;
                if (found is null) undecided = true;
            }
        }

        return undecided ? null : true;
    }

    /// <summary>
    /// Same type and value. Strings of any directory string type compare after RFC 4518
    /// preparation; other values compare byte for byte. Null when a value cannot be decoded or
    /// prepared, or a string is compared with a value of another kind.
    /// </summary>
    private static bool? SameAttribute(DerNode a, DerNode b)
    {
        if (a.At(0).OidValue() != b.At(0).OidValue()) return false;
        DerNode x = a.At(1), y = b.At(1);
        bool xs = IsString(x.Tag), ys = IsString(y.Tag);
        if (!xs && !ys) return x.Encoded.SequenceEqual(y.Encoded);
        if (!xs || !ys || Prepare(Text(x)) is not string s || Prepare(Text(y)) is not string t) return null;
        return string.Equals(s, t, StringComparison.Ordinal);
    }

    private static bool IsString(byte tag) => tag is 0x0C or 0x13 or 0x14 or 0x16 or 0x1A or 0x1C or 0x1E;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32 = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

    /// <summary>
    /// The text of a directory string: UTF8String, BMPString (UTF-16BE) and UniversalString
    /// (UTF-32BE) decoded strictly; PrintableString, IA5String, VisibleString and TeletexString
    /// as ASCII only, since TeletexString's other characters have no single reading. Null when
    /// the bytes do not decode.
    /// </summary>
    private static string? Text(DerNode value)
    {
        byte[] bytes = value.Content.ToArray();
        try
        {
            return value.Tag switch
            {
                0x0C => StrictUtf8.GetString(bytes),
                0x1E => bytes.Length % 2 == 0 ? StrictUtf16.GetString(bytes) : null,
                0x1C => bytes.Length % 4 == 0 ? StrictUtf32.GetString(bytes) : null,
                _ => Ascii(bytes),
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// String preparation after RFC 4518 for matching (RFC 5280, 7.1): characters mapped to
    /// nothing are dropped, other controls and separators become spaces, NFKC normalization,
    /// case folding, then insignificant space removal. Null when the string holds a character
    /// the preparation prohibits (unassigned, private use, surrogates, the replacement character).
    /// </summary>
    private static string? Prepare(string? text)
    {
        if (text is null) return null;
        var mapped = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is '­' or '᠆' or '͏' or (>= '᠋' and <= '᠍') or (>= '︀' and <= '️') or '￼' or '​') continue;
            if (ch is '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u0085') { mapped.Append(' '); continue; }
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) { mapped.Append(' '); continue; }
            mapped.Append(ch);
        }

        string normalized;
        try
        {
            if (!UnicodeMapping && mapped.ToString().Any(ch => ch > 0x7F)) return null;
            normalized = mapped.ToString().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return null;
        }

        foreach (char ch in normalized)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate || ch == '�') return null;
        }

        // Case folding: upper then lower case maps the case variants of a letter together.
        string folded = normalized.ToUpperInvariant().ToLowerInvariant();
        var b = new StringBuilder(folded.Length);
        foreach (string word in folded.Split([' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (b.Length > 0) b.Append(' ');
            b.Append(word);
        }

        return b.ToString();
    }

    /// <summary>An address against an address and mask of the same family; null when the constraint or address is malformed.</summary>
    private static bool? IpMatches(byte[] address, byte[] constraint)
    {
        if (address.Length is not (4 or 16) || constraint.Length is not (8 or 32)) return null;
        if (constraint.Length != 2 * address.Length) return false;
        for (int i = 0; i < address.Length; i++)
        {
            byte mask = constraint[address.Length + i];
            if ((address[i] & mask) != (constraint[i] & mask)) return false;
        }

        return true;
    }

    /// <summary>IA5 and the other ASCII string types: null when a byte lies outside ASCII.</summary>
    private static string? Ascii(byte[] value) => value.All(b => b < 0x80) ? Encoding.ASCII.GetString(value) : null;

    private static string Show(int form, byte[] name)
    {
        try
        {
            return form switch
            {
                Directory => new X500DistinguishedName(name).Name,
                Ip => name.Length == 4 ? string.Join(".", name) : Compat.ToHex(name),
                _ => Ascii(name) ?? Compat.Latin1.GetString(name),
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
