using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// Certificate policy processing (RFC 5280, 6.1, with the policy graph of RFC 9618, which keeps
/// one node per policy and depth so hostile mappings cannot make it grow exponentially). It
/// finds the policies a path is valid for after policy mappings, policy constraints and inhibit
/// anyPolicy, intersected with the policies the relying party accepts. Policy qualifiers are not
/// read.
/// </summary>
internal static class CertificatePolicies
{
    public const string AnyPolicy = "2.5.29.32.0";
    public const string CertificatePoliciesOid = "2.5.29.32";
    public const string PolicyMappingsOid = "2.5.29.33";
    public const string PolicyConstraintsOid = "2.5.29.36";
    public const string InhibitAnyPolicyOid = "2.5.29.54";

    /// <summary>Far above real certificates, which assert a handful of policies and mappings.</summary>
    private const int MaxEntries = 64;

    /// <summary>Whether the path passes policy processing, and the acceptable policies it is valid for.</summary>
    public sealed record Result(bool Valid, IReadOnlyList<string> Policies);

    private sealed class Node(string policy)
    {
        public string Policy { get; } = policy;

        public HashSet<string> Expected { get; set; } = new(StringComparer.Ordinal) { policy };

        public HashSet<Node> Parents { get; } = [];
    }

    /// <summary>The policies a certificate asserts, in order; empty without the extension.</summary>
    /// <exception cref="FormatException">The extension cannot be read.</exception>
    public static IReadOnlyList<string> Asserted(X509Certificate2 certificate) => Read(certificate) ?? [];

    /// <param name="path">The certificates from the one a trust anchor issued down to the end entity; the anchor itself is not part of it.</param>
    /// <param name="options">The acceptable policies and the initial explicit policy, mapping and anyPolicy settings.</param>
    /// <exception cref="FormatException">A policy extension cannot be read, or maps anyPolicy.</exception>
    public static Result Process(IReadOnlyList<X509Certificate2> path, PdfValidationOptions options)
    {
        int n = path.Count;
        // Acceptable policies only mean something when the chain must be valid for one of them.
        int explicitPolicy = options.RequireExplicitPolicy || options.AcceptablePolicies.Count > 0 ? 0 : n + 1;
        int inhibitAny = options.InhibitAnyPolicy ? 0 : n + 1;
        int mapping = options.InhibitPolicyMapping ? 0 : n + 1;
        var levels = new List<Dictionary<string, Node>> { new(StringComparer.Ordinal) { [AnyPolicy] = new Node(AnyPolicy) } };
        bool graph = true;

        for (int i = 1; i <= n; i++)
        {
            X509Certificate2 certificate = path[i - 1];
            bool selfIssued = Pkix.IsSelfIssued(certificate);
            IReadOnlyList<string>? policies = Read(certificate);
            var current = new Dictionary<string, Node>(StringComparer.Ordinal);
            levels.Add(current);
            if (policies is null)
            {
                // (e) Without the extension no policy survives.
                graph = false;
            }
            else if (graph)
            {
                Dictionary<string, Node> previous = levels[i - 1];

                // (d)(1) Each asserted policy hangs under the nodes expecting it, or under anyPolicy.
                foreach (string policy in policies)
                {
                    if (policy == AnyPolicy) continue;
                    List<Node> parents = [.. previous.Values.Where(p => p.Policy != AnyPolicy && p.Expected.Contains(policy))];
                    if (parents.Count == 0 && previous.TryGetValue(AnyPolicy, out Node? any)) parents.Add(any);
                    if (parents.Count == 0) continue;
                    Node node = Child(current, policy);
                    node.Parents.UnionWith(parents);
                }

                // (d)(2) anyPolicy, unless inhibited, carries every expected policy not asserted explicitly.
                if (policies.Contains(AnyPolicy) && (inhibitAny > 0 || (i < n && selfIssued)))
                {
                    var asserted = new HashSet<string>(current.Keys, StringComparer.Ordinal);
                    foreach (Node parent in previous.Values)
                    {
                        foreach (string expected in parent.Expected)
                        {
                            if (!asserted.Contains(expected)) Child(current, expected).Parents.Add(parent);
                        }
                    }
                }

                // (d)(3) Nodes left without children go.
                graph = Prune(levels, i);
            }

            // (f)
            if (explicitPolicy == 0 && !graph) return new Result(false, []);
            if (i == n) break;

            // Preparation for the next certificate (6.1.4).
            List<(string Issuer, string Subject)> mappings = Mappings(certificate);
            if (mappings.Any(m => m.Issuer == AnyPolicy || m.Subject == AnyPolicy)) throw new FormatException("A policy mapping names anyPolicy.");
            if (graph && mappings.Count > 0)
            {
                foreach (IGrouping<string, (string Issuer, string Subject)> group in mappings.GroupBy(m => m.Issuer, StringComparer.Ordinal))
                {
                    var subjects = new HashSet<string>(group.Select(m => m.Subject), StringComparer.Ordinal);
                    if (mapping > 0)
                    {
                        if (current.TryGetValue(group.Key, out Node? mapped))
                        {
                            mapped.Expected = subjects;
                        }
                        else if (current.TryGetValue(AnyPolicy, out Node? any))
                        {
                            Node created = Child(current, group.Key);
                            created.Parents.UnionWith(any.Parents);
                            created.Expected = subjects;
                        }
                    }
                    else
                    {
                        current.Remove(group.Key);
                    }
                }

                if (mapping == 0) graph = Prune(levels, i);
            }

            if (!selfIssued)
            {
                if (explicitPolicy > 0) explicitPolicy--;
                if (mapping > 0) mapping--;
                if (inhibitAny > 0) inhibitAny--;
            }

            (int? requireExplicit, int? inhibitMapping) = Constraints(certificate);
            if (requireExplicit is int r) explicitPolicy = Math.Min(explicitPolicy, r);
            if (inhibitMapping is int m) mapping = Math.Min(mapping, m);
            if (InhibitAny(certificate) is int skip) inhibitAny = Math.Min(inhibitAny, skip);
        }

        // Wrap-up (6.1.5).
        if (explicitPolicy > 0) explicitPolicy--;
        if (n > 0 && Constraints(path[n - 1]).RequireExplicit == 0) explicitPolicy = 0;
        IReadOnlyList<string> valid = graph ? Acceptable(levels[n], options.AcceptablePolicies) : [];
        return new Result(explicitPolicy > 0 || valid.Count > 0, valid);
    }

    private static Node Child(Dictionary<string, Node> level, string policy)
    {
        if (!level.TryGetValue(policy, out Node? node))
        {
            node = new Node(policy);
            level[policy] = node;
        }

        return node;
    }

    /// <summary>Removes, from depth <paramref name="depth"/> - 1 up, the nodes no node below names as a parent. False when the root goes.</summary>
    private static bool Prune(List<Dictionary<string, Node>> levels, int depth)
    {
        for (int d = depth; d >= 1; d--)
        {
            var used = new HashSet<Node>(levels[d].Values.SelectMany(c => c.Parents));
            foreach (string key in levels[d - 1].Where(e => !used.Contains(e.Value)).Select(e => e.Key).ToList()) levels[d - 1].Remove(key);

            // Parents that were removed no longer count.
            foreach (Node node in levels[d].Values) node.Parents.IntersectWith(levels[d - 1].Values);
        }

        return levels[0].Count > 0;
    }

    /// <summary>
    /// The policies of the final nodes as the trust anchor's domain names them: the first policy
    /// below anyPolicy on each path, before mappings renamed it (RFC 5280, 6.1.5 (g)), then
    /// intersected with the acceptable policies, where none means any.
    /// </summary>
    private static IReadOnlyList<string> Acceptable(Dictionary<string, Node> final, IReadOnlyList<string> acceptable)
    {
        var authority = new Dictionary<Node, HashSet<string>>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in final.Values) found.UnionWith(Authority(node));
        if (acceptable.Count == 0) return [.. found.OrderBy(p => p, StringComparer.Ordinal)];
        if (found.Contains(AnyPolicy)) return [.. acceptable.Distinct(StringComparer.Ordinal)];
        return [.. acceptable.Where(found.Contains).Distinct(StringComparer.Ordinal)];

        HashSet<string> Authority(Node node)
        {
            if (authority.TryGetValue(node, out HashSet<string>? known)) return known;
            var policies = new HashSet<string>(StringComparer.Ordinal);
            foreach (Node parent in node.Parents)
            {
                if (parent.Policy == AnyPolicy) policies.Add(node.Policy);
                else policies.UnionWith(Authority(parent));
            }

            authority[node] = policies;
            return policies;
        }
    }

    private static List<string>? Read(X509Certificate2 certificate)
    {
        if (certificate.Extensions[CertificatePoliciesOid] is not X509Extension e) return null;
        var policies = new List<string>();
        foreach (DerNode information in DerNode.Parse(e.RawData).Children())
        {
            string policy = information.At(0).OidValue();
            if (!policies.Contains(policy)) policies.Add(policy);
            if (policies.Count > MaxEntries) throw new FormatException("A certificate asserts too many policies.");
        }

        return policies;
    }

    private static List<(string Issuer, string Subject)> Mappings(X509Certificate2 certificate)
    {
        var mappings = new List<(string, string)>();
        if (certificate.Extensions[PolicyMappingsOid] is not X509Extension e) return mappings;
        foreach (DerNode pair in DerNode.Parse(e.RawData).Children())
        {
            mappings.Add((pair.At(0).OidValue(), pair.At(1).OidValue()));
            if (mappings.Count > MaxEntries) throw new FormatException("A certificate has too many policy mappings.");
        }

        return mappings;
    }

    /// <summary>The policy constraints: requireExplicitPolicy [0] and inhibitPolicyMapping [1], each a number of certificates.</summary>
    private static (int? RequireExplicit, int? InhibitMapping) Constraints(X509Certificate2 certificate)
    {
        if (certificate.Extensions[PolicyConstraintsOid] is not X509Extension e) return (null, null);
        int? require = null, inhibit = null;
        foreach (DerNode field in DerNode.Parse(e.RawData).Children())
        {
            if (field.Tag == 0x80) require = SkipCerts(field);
            else if (field.Tag == 0x81) inhibit = SkipCerts(field);
        }

        return (require, inhibit);
    }

    private static int? InhibitAny(X509Certificate2 certificate) =>
        certificate.Extensions[InhibitAnyPolicyOid] is X509Extension e ? SkipCerts(DerNode.Parse(e.RawData)) : null;

    /// <summary>A SkipCerts INTEGER, explicit or implicitly tagged; large values are capped, since no path is that long.</summary>
    private static int SkipCerts(DerNode node)
    {
        ReadOnlySpan<byte> v = node.Content;
        if (v.Length == 0 || (v[0] & 0x80) != 0) throw new FormatException("A certificate count is not a non-negative integer.");
        if (v.Length > 4) return int.MaxValue;
        int value = 0;
        foreach (byte b in v) value = value << 8 | b;
        return value < 0 ? int.MaxValue : value;
    }
}
