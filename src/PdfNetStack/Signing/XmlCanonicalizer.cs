using System.Text;
using System.Xml;

namespace PdfNetStack.Signing;

/// <summary>The canonicalization algorithms an XML signature can name.</summary>
internal enum XmlCanonicalization
{
    /// <summary>Canonical XML 1.0 (W3C, 2001).</summary>
    Inclusive,

    /// <summary>Canonical XML 1.1 (W3C, 2008).</summary>
    Inclusive11,

    /// <summary>Exclusive XML Canonicalization 1.0 (W3C, 2002).</summary>
    Exclusive,
}

/// <summary>
/// Canonical XML of a subtree, the bytes an XML signature digests and signs. The node set is
/// always a whole subtree (an element, or the document) minus at most one element below it: what
/// a same document reference ("" or "#id") and the enveloped signature transform select.
/// </summary>
/// <remarks>
/// The rules, from the W3C recommendations: UTF-8 output with no XML declaration or DTD; empty
/// elements written as start and end tags; namespace declarations, then attributes, each sorted
/// (declarations by prefix with the default first, attributes by namespace URI then local name
/// with unqualified ones first); text escaping &amp;, &lt;, &gt; and carriage return; attribute
/// values escaping &amp;, &lt;, the quote, tab, line feed and carriage return; whitespace outside
/// the document element dropped, with a line feed between the element and processing
/// instructions or comments outside it. Inclusive canonicalization writes on each element the
/// namespace declarations that differ from what its nearest written ancestor declared, and on the
/// apex every namespace in scope there, and copies the xml: attributes of ancestors outside the
/// subtree onto the apex (version 1.1 copies only xml:lang and xml:space, and xml:base on an
/// ancestor is refused rather than resolved). Exclusive canonicalization writes only the
/// namespaces an element visibly uses (its own prefix, its attributes' prefixes) plus the
/// InclusiveNamespaces prefix list, each where no written ancestor already declared the same
/// value, and copies no xml: attributes. Depth is bounded by the caller, which checked it when the
/// document was read.
/// </remarks>
internal static class XmlCanonicalizer
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>The canonical form of <paramref name="apex"/> (an element or the document), leaving out <paramref name="omit"/> and everything below it.</summary>
    /// <param name="prefixes">For exclusive canonicalization, the InclusiveNamespaces prefix list, "#default" naming the default namespace.</param>
    /// <exception cref="FormatException">The subtree holds something canonicalization cannot write, such as an entity reference or an xml:base attribute version 1.1 would have to resolve.</exception>
    public static byte[] Canonicalize(XmlNode apex, XmlElement? omit, XmlCanonicalization method, bool comments, IReadOnlyCollection<string>? prefixes = null)
    {
        var writer = new Writer(omit, method, comments, prefixes);
        if (apex is XmlDocument document)
        {
            writer.Document(document);
        }
        else if (apex is XmlElement element)
        {
            writer.Apex(element);
        }
        else
        {
            throw new FormatException("Only an element or a whole document can be canonicalized.");
        }

        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private sealed class Writer
    {
        private static readonly Dictionary<string, string> NoNamespaces = new(StringComparer.Ordinal);
        private readonly StringBuilder _out = new();
        private readonly XmlElement? _omit;
        private readonly XmlCanonicalization _method;
        private readonly bool _comments;
        private readonly HashSet<string> _prefixes = new(StringComparer.Ordinal);

        public Writer(XmlElement? omit, XmlCanonicalization method, bool comments, IReadOnlyCollection<string>? prefixes)
        {
            _omit = omit;
            _method = method;
            _comments = comments;
            if (prefixes is not null)
            {
                foreach (string p in prefixes) _prefixes.Add(p == "#default" ? "" : p);
            }
        }

        public override string ToString() => _out.ToString();

        public void Document(XmlDocument document)
        {
            bool afterRoot = false;
            foreach (XmlNode child in document.ChildNodes)
            {
                switch (child)
                {
                    case XmlElement e:
                        Element(e, NoNamespaces, NoNamespaces, null);
                        afterRoot = true;
                        break;
                    case XmlProcessingInstruction or XmlComment:
                        if (child is XmlComment && !_comments) break;
                        if (afterRoot) _out.Append('\n');
                        Leaf(child);
                        if (!afterRoot) _out.Append('\n');
                        break;
                }
            }
        }

        /// <summary>An element whose parent is outside the node set: every namespace in scope counts, and inclusive forms inherit xml: attributes.</summary>
        public void Apex(XmlElement apex)
        {
            var inScope = new Dictionary<string, string>(StringComparer.Ordinal);
            var inherited = new List<XmlAttribute>();
            var ancestors = new List<XmlElement>();
            for (XmlNode? n = apex.ParentNode; n is XmlElement a; n = n.ParentNode) ancestors.Add(a);
            for (int i = ancestors.Count - 1; i >= 0; i--) Declare(ancestors[i], inScope);
            if (_method != XmlCanonicalization.Exclusive)
            {
                // The nearest ancestor's value wins; the apex's own attributes win over all of them.
                foreach (XmlElement a in ancestors)
                {
                    foreach (XmlAttribute attribute in a.Attributes)
                    {
                        if (attribute.NamespaceURI != XmlNamespace) continue;
                        if (_method == XmlCanonicalization.Inclusive11)
                        {
                            if (attribute.LocalName == "base") throw new FormatException("Canonical XML 1.1 of an element below an xml:base attribute is not supported.");
                            if (attribute.LocalName is not ("lang" or "space")) continue;
                        }

                        if (inherited.Exists(x => x.LocalName == attribute.LocalName) || apex.HasAttribute(attribute.LocalName, XmlNamespace)) continue;
                        inherited.Add(attribute);
                    }
                }
            }

            Element(apex, inScope, NoNamespaces, inherited);
        }

        /// <param name="parentScope">The namespaces in scope at the parent: prefix to URI, "" for the default.</param>
        /// <param name="rendered">What the nearest written ancestors declared, prefix to URI.</param>
        /// <param name="inherited">xml: attributes copied from outside the node set, for the apex only.</param>
        private void Element(XmlElement e, Dictionary<string, string> parentScope, Dictionary<string, string> rendered, List<XmlAttribute>? inherited)
        {
            if (ReferenceEquals(e, _omit)) return;
            Dictionary<string, string> scope = parentScope;
            foreach (XmlAttribute a in e.Attributes)
            {
                if (a.NamespaceURI != XmlnsNamespace) continue;
                if (ReferenceEquals(scope, parentScope)) scope = new Dictionary<string, string>(parentScope, StringComparer.Ordinal);
                Declare(a, scope);
            }

            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            if (_method == XmlCanonicalization.Exclusive)
            {
                candidates.Add(e.Prefix);
                foreach (XmlAttribute a in e.Attributes)
                {
                    if (a.NamespaceURI != XmlnsNamespace && a.Prefix.Length > 0 && a.Prefix != "xml") candidates.Add(a.Prefix);
                }

                foreach (string p in _prefixes)
                {
                    if (p.Length == 0 || scope.ContainsKey(p)) candidates.Add(p);
                }
            }
            else
            {
                foreach (string p in scope.Keys) candidates.Add(p);
                candidates.Add("");
            }

            Dictionary<string, string> written = rendered;
            var declarations = new List<(string Prefix, string Uri)>();
            foreach (string p in candidates)
            {
                if (p == "xml") continue;
                bool known = scope.TryGetValue(p, out string? uri);
                if (!known && p.Length > 0) continue;
                uri ??= "";
                string before = rendered.TryGetValue(p, out string? b) ? b : "";
                if (uri == before) continue;
                declarations.Add((p, uri));
                if (ReferenceEquals(written, rendered)) written = new Dictionary<string, string>(rendered, StringComparer.Ordinal);
                written[p] = uri;
            }

            var attributes = new List<XmlAttribute>();
            foreach (XmlAttribute a in e.Attributes)
            {
                if (a.NamespaceURI != XmlnsNamespace) attributes.Add(a);
            }

            if (inherited is not null) attributes.AddRange(inherited);
            attributes.Sort((x, y) =>
            {
                int c = CodePoints(x.NamespaceURI, y.NamespaceURI);
                return c != 0 ? c : CodePoints(x.LocalName, y.LocalName);
            });

            _out.Append('<').Append(e.Name);
            foreach ((string prefix, string uri) in declarations)
            {
                _out.Append(prefix.Length == 0 ? " xmlns" : " xmlns:" + prefix).Append("=\"");
                Attribute(uri);
                _out.Append('"');
            }

            foreach (XmlAttribute a in attributes)
            {
                _out.Append(' ').Append(a.Name).Append("=\"");
                Attribute(a.Value);
                _out.Append('"');
            }

            _out.Append('>');
            foreach (XmlNode child in e.ChildNodes)
            {
                if (child is XmlElement c) Element(c, scope, written, null);
                else Leaf(child);
            }

            _out.Append("</").Append(e.Name).Append('>');
        }

        private void Leaf(XmlNode node)
        {
            switch (node)
            {
                case XmlText or XmlCDataSection or XmlWhitespace or XmlSignificantWhitespace:
                    Text(node.Value ?? "");
                    break;
                case XmlProcessingInstruction pi:
                    _out.Append("<?").Append(pi.Target);
                    if (pi.Data.Length > 0) _out.Append(' ').Append(pi.Data);
                    _out.Append("?>");
                    break;
                case XmlComment comment:
                    if (_comments) _out.Append("<!--").Append(comment.Value).Append("-->");
                    break;
                case XmlEntityReference:
                    throw new FormatException("An entity reference cannot be canonicalized.");
            }
        }

        private void Text(string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': _out.Append("&amp;"); break;
                    case '<': _out.Append("&lt;"); break;
                    case '>': _out.Append("&gt;"); break;
                    case '\r': _out.Append("&#xD;"); break;
                    default: _out.Append(c); break;
                }
            }
        }

        private void Attribute(string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': _out.Append("&amp;"); break;
                    case '<': _out.Append("&lt;"); break;
                    case '"': _out.Append("&quot;"); break;
                    case '\t': _out.Append("&#x9;"); break;
                    case '\n': _out.Append("&#xA;"); break;
                    case '\r': _out.Append("&#xD;"); break;
                    default: _out.Append(c); break;
                }
            }
        }

        private static void Declare(XmlElement e, Dictionary<string, string> scope)
        {
            foreach (XmlAttribute a in e.Attributes)
            {
                if (a.NamespaceURI == XmlnsNamespace) Declare(a, scope);
            }
        }

        /// <summary>xmlns="..." sets the default namespace ("" undeclares it); xmlns:p="..." binds p.</summary>
        private static void Declare(XmlAttribute declaration, Dictionary<string, string> scope)
        {
            string prefix = declaration.Prefix.Length == 0 ? "" : declaration.LocalName;
            if (prefix == "xml") return;
            scope[prefix] = declaration.Value;
        }

        /// <summary>Orders by Unicode code point, as the recommendations sort; ordinal UTF-16 order differs for characters above the surrogates.</summary>
        private static int CodePoints(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                char x = a[i], y = b[i];
                if (x == y) continue;
                bool sx = char.IsSurrogate(x), sy = char.IsSurrogate(y);
                if (sx != sy) return sx ? 1 : -1;
                return x < y ? -1 : 1;
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
