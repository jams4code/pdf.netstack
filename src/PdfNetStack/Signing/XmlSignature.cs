using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfNetStack.Signing;

/// <summary>
/// Verifies the enveloped XML signature (XMLDSig, W3C 2008) on a document's root element against
/// certificates the caller trusts for it, as ETSI TS 119 612 signs trusted lists.
/// </summary>
/// <remarks>
/// Every reference must be a same document reference ("" or "#id") and digest correctly; one of
/// them must cover the whole document with the enveloped signature transform, so the content the
/// caller reads is the content that was signed. Transforms: enveloped signature and one final
/// canonicalization (Canonical XML 1.0 or 1.1 and Exclusive 1.0, with or without comments).
/// Digests: SHA-256, 384 and 512. Signatures: RSA PKCS #1 v1.5, RSA-PSS (MGF1 with the same
/// hash, any salt length, keys marked rsaEncryption or id-RSASSA-PSS) and ECDSA, with SHA-256,
/// 384 or 512. SHA-1, XPath and XSLT transforms and references
/// outside the document are refused. Element identifiers (Id, ID or id) must be unique, which
/// stops a reference from being pointed at a copy. The certificate in KeyInfo is not trusted: the
/// signature must verify with one of the certificates supplied. XAdES signed properties are
/// digested as any other reference; their content is not interpreted.
/// </remarks>
internal static class XmlSignature
{
    public const string Namespace = "http://www.w3.org/2000/09/xmldsig#";
    private const string Enveloped = Namespace + "enveloped-signature";
    private const string ExclusiveNamespace = "http://www.w3.org/2001/10/xml-exc-c14n#";
    private const string Xades = "http://uri.etsi.org/01903/v1.3.2#";
    private const int MaxReferences = 16;

    /// <summary>
    /// The certificate the root's signature verifies with and the signing time its signed XAdES
    /// properties claim (null without one), or the reason it does not verify.
    /// </summary>
    public static (X509Certificate2? Signer, DateTimeOffset? SigningTime, string? Problem) Verify(XmlDocument document, IEnumerable<X509Certificate2> certificates)
    {
        try
        {
            return Check(document, certificates);
        }
        catch (FormatException e)
        {
            return (null, null, e.Message);
        }
    }

    private static (X509Certificate2? Signer, DateTimeOffset? SigningTime, string? Problem) Check(XmlDocument document, IEnumerable<X509Certificate2> certificates)
    {
        XmlElement root = document.DocumentElement ?? throw new FormatException("The document has no root element.");
        List<XmlElement> signatures = Children(root, "Signature");
        if (signatures.Count == 0) return (null, null, "The list is not signed.");
        if (signatures.Count > 1) return (null, null, "The list carries more than one signature.");
        XmlElement signature = signatures[0];
        Dictionary<string, XmlElement> ids = Identifiers(document);

        List<XmlElement> parts = Elements(signature);
        if (parts.Count < 2 || !Is(parts[0], "SignedInfo") || !Is(parts[1], "SignatureValue")) throw new FormatException("The signature does not start with SignedInfo and SignatureValue.");
        XmlElement signedInfo = parts[0];
        List<XmlElement> info = Elements(signedInfo);
        if (info.Count < 3 || !Is(info[0], "CanonicalizationMethod") || !Is(info[1], "SignatureMethod")) throw new FormatException("SignedInfo is incomplete.");
        List<XmlElement> references = info.Skip(2).ToList();
        if (references.Exists(r => !Is(r, "Reference"))) throw new FormatException("SignedInfo holds something other than references.");
        if (references.Count > MaxReferences) throw new FormatException("The signature has too many references.");

        bool coversDocument = false;
        DateTimeOffset? signingTime = null;
        foreach (XmlElement reference in references)
        {
            if (!reference.HasAttribute("URI")) throw new FormatException("A reference has no URI.");
            string uri = reference.GetAttribute("URI");
            XmlNode target = uri.Length == 0 ? document
                : uri[0] == '#' && ids.TryGetValue(uri.Substring(1), out XmlElement? element) ? element
                : throw new FormatException("A reference points outside the document or to an unknown identifier (" + uri + ").");

            XmlElement? omit = null;
            (XmlCanonicalization Method, bool Comments, IReadOnlyCollection<string>? Prefixes)? c14n = null;
            List<XmlElement> children = Elements(reference);
            int at = 0;
            if (at < children.Count && Is(children[at], "Transforms"))
            {
                foreach (XmlElement transform in Elements(children[at]))
                {
                    if (!Is(transform, "Transform")) throw new FormatException("Transforms holds something other than transforms.");
                    if (c14n is not null) throw new FormatException("Canonicalization must be the last transform.");
                    string algorithm = transform.GetAttribute("Algorithm");
                    if (algorithm == Enveloped) omit = signature;
                    else c14n = Canonicalization(transform) ?? throw new FormatException("The transform " + algorithm + " is not supported.");
                }

                at++;
            }

            if (at + 1 >= children.Count || !Is(children[at], "DigestMethod") || !Is(children[at + 1], "DigestValue")) throw new FormatException("A reference has no digest.");
            HashAlgorithmName hash = Digest(children[at].GetAttribute("Algorithm"));

            // A same document reference selects the node set without comments, whatever the canonicalization says.
            (XmlCanonicalization method, _, IReadOnlyCollection<string>? prefixes) = c14n ?? (XmlCanonicalization.Inclusive, false, null);
            if (omit is not null && !IsWithin(signature, target)) omit = null;
            byte[] canonical = XmlCanonicalizer.Canonicalize(target, omit, method, comments: false, prefixes);
            byte[] expected = Base64(children[at + 1].InnerText, "digest value");
            if (!Pkix.Hash(hash, canonical).AsSpan().SequenceEqual(expected)) return (null, null, "The signed content changed: the digest of reference \"" + uri + "\" does not match.");
            if (omit is not null && (target is XmlDocument || ReferenceEquals(target, root))) coversDocument = true;
            if (target is XmlElement { LocalName: "SignedProperties", NamespaceURI: Xades } properties && IsWithin(properties, signature)) signingTime = SigningTime(properties);
        }

        if (!coversDocument) return (null, null, "No reference covers the whole list with the enveloped signature transform.");

        (XmlCanonicalization Method, bool Comments, IReadOnlyCollection<string>? Prefixes) c = Canonicalization(info[0])
            ?? throw new FormatException("The canonicalization " + info[0].GetAttribute("Algorithm") + " is not supported.");
        byte[] signed = XmlCanonicalizer.Canonicalize(signedInfo, null, c.Method, c.Comments, c.Prefixes);
        byte[] value = Base64(parts[1].InnerText, "signature value");
        (HashAlgorithmName signatureHash, RSASignaturePadding? padding) = SignatureMethod(info[1].GetAttribute("Algorithm"));
        foreach (X509Certificate2 certificate in certificates)
        {
            if (Verifies(signed, value, signatureHash, padding, certificate)) return (certificate, signingTime, null);
        }

        return (null, null, "The signature does not verify with any certificate supplied for the list.");
    }

    private static bool Verifies(byte[] data, byte[] signature, HashAlgorithmName hash, RSASignaturePadding? padding, X509Certificate2 certificate)
    {
        try
        {
            if (padding is not null)
            {
                using RSA? rsa = RsaKey(certificate);
                if (rsa is null) return false;
                if (rsa.VerifyData(data, signature, hash, padding)) return true;
                return padding == RSASignaturePadding.Pss && Pss(rsa, Pkix.Hash(hash, data), signature, hash);
            }

            // XMLDSig ECDSA values are r || s, the form .NET verifies.
            using ECDsa? ec = certificate.GetECDsaPublicKey();
            return ec is not null && signature.Length == 2 * ((ec.KeySize + 7) / 8) && ec.VerifyData(data, signature, hash);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The certificate's RSA key, also when its algorithm is id-RSASSA-PSS (RFC 4055), which some
    /// list signing certificates use and the platform does not always load. The key bits are the
    /// same RSAPublicKey either way.
    /// </summary>
    private static RSA? RsaKey(X509Certificate2 certificate)
    {
        try
        {
            if (certificate.GetRSAPublicKey() is RSA rsa) return rsa;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            // Read below from the key bits.
        }

        if (certificate.PublicKey.Oid.Value != "1.2.840.113549.1.1.10") return null;
        try
        {
            List<DerNode> key = DerNode.Parse(Pkix.SubjectPublicKey(certificate)).Children();
            if (key.Count != 2) return null;
            var parsed = RSA.Create();
            parsed.ImportParameters(new RSAParameters { Modulus = Magnitude(key[0]), Exponent = Magnitude(key[1]) });
            return parsed;
        }
        catch (FormatException)
        {
            return null;
        }

        static byte[] Magnitude(DerNode integer)
        {
            ReadOnlySpan<byte> v = integer.Content;
            while (v.Length > 1 && v[0] == 0) v = v.Slice(1);
            return v.ToArray();
        }
    }

    /// <summary>
    /// EMSA-PSS verification (RFC 8017, 9.1.2) with MGF1 over the same hash and the salt length
    /// read from the encoded message. .NET verifies only salts as long as the hash; RFC 6931 lets
    /// a signer choose, and some trusted list operators use the longest salt the key allows.
    /// </summary>
    private static bool Pss(RSA rsa, byte[] digest, byte[] signature, HashAlgorithmName hash)
    {
        RSAParameters key = rsa.ExportParameters(false);
        byte[] modulus = key.Modulus!;
        if (signature.Length != modulus.Length) return false;
        BigInteger n = Unsigned(modulus), s = Unsigned(signature);
        if (s >= n) return false;
        BigInteger m = BigInteger.ModPow(s, Unsigned(key.Exponent!), n);

        int modBits = modulus.Length * 8;
        for (int i = 0; i < modulus.Length && modulus[i] == 0; i++) modBits -= 8;
        for (byte top = modulus.First(b => b != 0); (top & 0x80) == 0; top <<= 1) modBits--;
        int emBits = modBits - 1, emLength = (emBits + 7) / 8, hashLength = digest.Length;
        if (emLength < hashLength + 2) return false;
        byte[] little = m.ToByteArray();
        int significant = little.Length;
        while (significant > 0 && little[significant - 1] == 0) significant--;
        if (significant > emLength) return false;
        var em = new byte[emLength];
        for (int i = 0; i < significant; i++) em[emLength - 1 - i] = little[i];
        if (em[emLength - 1] != 0xBC) return false;

        int dbLength = emLength - hashLength - 1;
        byte[] h = em.AsSpan(dbLength, hashLength).ToArray();
        byte unused = (byte)(0xFF << (8 - (8 * emLength - emBits)));
        if ((em[0] & unused) != 0) return false;
        var db = new byte[dbLength];
        byte[] mask = Mgf1(h, dbLength, hash);
        for (int i = 0; i < dbLength; i++) db[i] = (byte)(em[i] ^ mask[i]);
        db[0] &= (byte)~unused;
        int one = 0;
        while (one < dbLength && db[one] == 0) one++;
        if (one == dbLength || db[one] != 1) return false;

        // M' = eight zero bytes, the message hash, the salt.
        byte[] prime = [.. new byte[8], .. digest, .. db.AsSpan(one + 1).ToArray()];
        return Pkix.Hash(hash, prime).AsSpan().SequenceEqual(h);
    }

    private static byte[] Mgf1(byte[] seed, int length, HashAlgorithmName hash)
    {
        var output = new byte[length];
        var block = new byte[seed.Length + 4];
        seed.CopyTo(block, 0);
        for (int counter = 0, at = 0; at < length; counter++)
        {
            block[seed.Length] = (byte)(counter >> 24);
            block[seed.Length + 1] = (byte)(counter >> 16);
            block[seed.Length + 2] = (byte)(counter >> 8);
            block[seed.Length + 3] = (byte)counter;
            byte[] chunk = Pkix.Hash(hash, block);
            int n = Math.Min(chunk.Length, length - at);
            Array.Copy(chunk, 0, output, at, n);
            at += n;
        }

        return output;
    }

    /// <summary>A non-negative integer from big endian bytes.</summary>
    private static BigInteger Unsigned(byte[] bigEndian)
    {
        var little = new byte[bigEndian.Length + 1];
        for (int i = 0; i < bigEndian.Length; i++) little[i] = bigEndian[bigEndian.Length - 1 - i];
        return new BigInteger(little);
    }

    /// <summary>The canonicalization a Transform or CanonicalizationMethod names, or null when it is not one.</summary>
    private static (XmlCanonicalization, bool, IReadOnlyCollection<string>?)? Canonicalization(XmlElement element)
    {
        switch (element.GetAttribute("Algorithm"))
        {
            case "http://www.w3.org/TR/2001/REC-xml-c14n-20010315": return (XmlCanonicalization.Inclusive, false, null);
            case "http://www.w3.org/TR/2001/REC-xml-c14n-20010315#WithComments": return (XmlCanonicalization.Inclusive, true, null);
            case "http://www.w3.org/2006/12/xml-c14n11": return (XmlCanonicalization.Inclusive11, false, null);
            case "http://www.w3.org/2006/12/xml-c14n11#WithComments": return (XmlCanonicalization.Inclusive11, true, null);
            case ExclusiveNamespace: return (XmlCanonicalization.Exclusive, false, Prefixes(element));
            case ExclusiveNamespace + "WithComments": return (XmlCanonicalization.Exclusive, true, Prefixes(element));
            default: return null;
        }

        static IReadOnlyCollection<string>? Prefixes(XmlElement element)
        {
            foreach (XmlNode child in element.ChildNodes)
            {
                if (child is XmlElement { LocalName: "InclusiveNamespaces", NamespaceURI: ExclusiveNamespace } list)
                {
                    return list.GetAttribute("PrefixList").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                }
            }

            return null;
        }
    }

    private static HashAlgorithmName Digest(string uri) => uri switch
    {
        "http://www.w3.org/2001/04/xmlenc#sha256" => HashAlgorithmName.SHA256,
        "http://www.w3.org/2001/04/xmldsig-more#sha384" => HashAlgorithmName.SHA384,
        "http://www.w3.org/2001/04/xmlenc#sha512" => HashAlgorithmName.SHA512,
        _ => throw new FormatException("The digest method " + uri + " is not supported."),
    };

    /// <summary>The hash and, for RSA, the padding of a SignatureMethod; ECDSA has no padding.</summary>
    private static (HashAlgorithmName, RSASignaturePadding?) SignatureMethod(string uri) => uri switch
    {
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256" => (HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384" => (HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512" => (HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1),
        "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1" => (HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
        "http://www.w3.org/2007/05/xmldsig-more#sha384-rsa-MGF1" => (HashAlgorithmName.SHA384, RSASignaturePadding.Pss),
        "http://www.w3.org/2007/05/xmldsig-more#sha512-rsa-MGF1" => (HashAlgorithmName.SHA512, RSASignaturePadding.Pss),
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256" => (HashAlgorithmName.SHA256, null),
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384" => (HashAlgorithmName.SHA384, null),
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512" => (HashAlgorithmName.SHA512, null),
        _ => throw new FormatException("The signature method " + uri + " is not supported."),
    };

    /// <summary>SignedSignatureProperties/SigningTime of XAdES signed properties, or null.</summary>
    /// <exception cref="FormatException">The time is not an xs:dateTime.</exception>
    private static DateTimeOffset? SigningTime(XmlElement properties)
    {
        foreach (XmlNode group in properties.ChildNodes)
        {
            if (group is not XmlElement { LocalName: "SignedSignatureProperties", NamespaceURI: Xades } signature) continue;
            foreach (XmlNode child in signature.ChildNodes)
            {
                if (child is XmlElement { LocalName: "SigningTime", NamespaceURI: Xades } time) return XmlConvert.ToDateTimeOffset(time.InnerText.Trim());
            }
        }

        return null;
    }

    /// <summary>Every element identifier in the document; a duplicate is refused, since it would let a reference be pointed at a copy.</summary>
    private static Dictionary<string, XmlElement> Identifiers(XmlDocument document)
    {
        var ids = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        var pending = new Stack<XmlElement>();
        if (document.DocumentElement is XmlElement root) pending.Push(root);
        while (pending.Count > 0)
        {
            XmlElement e = pending.Pop();
            if (Id(e) is string id)
            {
                if (ids.ContainsKey(id)) throw new FormatException("Two elements share the identifier \"" + id + "\".");
                ids[id] = e;
            }

            foreach (XmlNode child in e.ChildNodes)
            {
                if (child is XmlElement c) pending.Push(c);
            }
        }

        return ids;
    }

    private static string? Id(XmlElement e)
    {
        string? found = null;
        foreach (XmlAttribute a in e.Attributes)
        {
            if (a.NamespaceURI.Length > 0 || a.LocalName is not ("Id" or "ID" or "id")) continue;
            if (found is not null && found != a.Value) throw new FormatException("An element carries two different identifiers.");
            found = a.Value;
        }

        return found;
    }

    private static bool IsWithin(XmlNode node, XmlNode ancestor)
    {
        for (XmlNode? n = node; n is not null; n = n.ParentNode)
        {
            if (ReferenceEquals(n, ancestor)) return true;
        }

        return false;
    }

    private static byte[] Base64(string text, string what)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new FormatException("The signature's " + what + " is not base64.");
        }
    }

    private static bool Is(XmlElement e, string name) => e.LocalName == name && e.NamespaceURI == Namespace;

    private static List<XmlElement> Children(XmlElement parent, string name) => Elements(parent).Where(e => Is(e, name)).ToList();

    private static List<XmlElement> Elements(XmlElement parent)
    {
        var list = new List<XmlElement>();
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is XmlElement e) list.Add(e);
        }

        return list;
    }
}
