using PdfNetStack.Objects;

namespace PdfNetStack.Optimization;

/// <summary>
/// Removes resource entries that no content can name: fonts, XObjects, graphics states,
/// patterns, shadings, color spaces and property lists a resource dictionary lists but none of
/// the content streams using it mentions. Only category dictionaries the
/// <see cref="ContentAnalysis"/> saw completely are touched; everything else is kept.
/// </summary>
internal static class ResourcePruner
{
    // Default color spaces replace device colors implicitly (ISO 32000-1, 8.6.5.6); no operator names them.
    private static readonly HashSet<string> Implicit = new(StringComparer.Ordinal) { "DefaultGray", "DefaultRGB", "DefaultCMYK" };

    /// <summary>True when an entry named <paramref name="key"/> may go, given the names content uses.</summary>
    public static bool MayRemove(HashSet<string> used, string key) => !used.Contains(key) && !Implicit.Contains(key);

    /// <returns>Entries removed.</returns>
    public static int Run(ContentAnalysis analysis)
    {
        if (!analysis.Complete) return 0;
        int removed = 0;
        foreach (KeyValuePair<PdfDictionary, HashSet<string>> entry in analysis.PrunableCategories)
        {
            foreach (string key in entry.Key.Keys.ToList())
            {
                if (!MayRemove(entry.Value, key)) continue;
                entry.Key.Remove(key);
                removed++;
            }
        }

        return removed;
    }
}
