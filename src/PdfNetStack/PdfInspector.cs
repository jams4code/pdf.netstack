using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack;

/// <summary>Size and count of one category of streams.</summary>
/// <param name="Count">Number of streams.</param>
/// <param name="Bytes">Encoded bytes stored in the file.</param>
public sealed record StreamBucket(int Count, long Bytes);

/// <summary>Structural facts about a PDF, gathered without modifying it.</summary>
public sealed record PdfInspection(
    long FileBytes,
    string Version,
    int PageCount,
    int ObjectCount,
    int ReachableObjects,
    int Revisions,
    bool IsLinearized,
    bool IsEncrypted,
    bool IsSigned,
    bool IsPdfA1,
    bool UsesObjectStreams,
    bool UsesXrefStreams,
    bool NeededRepair,
    IReadOnlyDictionary<string, StreamBucket> StreamsByKind,
    IReadOnlyDictionary<string, int> FilterUsage);

/// <summary>Reads PDF structure for diagnostics and corpus analysis.</summary>
public static class PdfInspector
{
    /// <summary>Inspects a PDF held in memory.</summary>
    /// <exception cref="PdfFormatException">The input is not a readable PDF.</exception>
    public static PdfInspection Inspect(ReadOnlyMemory<byte> input, long maxDecodedStreamBytes = 256L << 20)
    {
        PdfFile file = PdfFile.Open(input, maxDecodedStreamBytes);
        bool encrypted = file.Trailer["Encrypt"] is not null and not PdfNull;
        ObjectGraph graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, CancellationToken.None);

        var kinds = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        var filters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (int number in graph.Order)
        {
            if (file.GetObject(number) is not PdfStream s) continue;
            string kind = Classify(file, s.Dictionary);
            kinds[kind] = kinds.TryGetValue(kind, out var k) ? (k.Count + 1, k.Bytes + s.Data.Length) : (1, s.Data.Length);
            try
            {
                List<FilterSpec> chain = StreamCodec.GetFilters(s.Dictionary, file.Resolve);
                string key = chain.Count == 0 ? "(none)" : string.Join("+", chain.Select(f => f.Name));
                filters[key] = filters.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            catch (StreamDecodeException)
            {
                filters["(invalid)"] = filters.TryGetValue("(invalid)", out int c) ? c + 1 : 1;
            }
        }

        return new PdfInspection(
            input.Length,
            file.HeaderVersion,
            PdfOptimizer.CountPages(file),
            file.Xref.Count(e => e.Value.Type != XrefEntryType.Free),
            graph.Order.Count,
            file.RevisionCount,
            file.IsLinearized,
            encrypted,
            graph.HasSignatureMarkers || input.Span.IndexOf("/ByteRange"u8) >= 0,
            PdfOptimizer.IsPdfA1(file, maxDecodedStreamBytes),
            file.UsesObjectStreams,
            file.UsesXrefStreams,
            file.WasReconstructed || file.LengthsRepaired > 0 || file.UnreadableObjects > 0,
            kinds.ToDictionary(p => p.Key, p => new StreamBucket(p.Value.Count, p.Value.Bytes)),
            filters);
    }

    private static string Classify(PdfFile file, PdfDictionary d)
    {
        string? type = d.GetName("Type");
        string? subtype = d.GetName("Subtype");
        if (subtype == "Image") return "Images";
        if (subtype == "Form") return "Forms";
        if (type == "Metadata") return "Metadata";
        if (type == "EmbeddedFile") return "Attachments";
        if (d.ContainsKey("Length1") || d.ContainsKey("Length2") || subtype is "Type1C" or "CIDFontType0C" or "OpenType") return "Fonts";
        if (type == "XObject") return "Forms";
        if (d.ContainsKey("N") && d["Alternate"] is not null) return "ColorProfiles";
        if (type == "CMap" || d.ContainsKey("CMapName")) return "CMaps";
        return file.Resolve(d["PatternType"]) is not null || file.Resolve(d["ShadingType"]) is not null || file.Resolve(d["FunctionType"]) is not null
            ? "Patterns"
            : "ContentAndOther";
    }
}

/// <summary>Public entry point to the equivalence checker, for validating any PDF rewriting tool.</summary>
public static class PdfEquivalence
{
    /// <summary>
    /// Returns null when both PDFs describe the same document object for object, with stream
    /// content compared after decoding and JPEG images compared by DCT coefficients. Otherwise
    /// returns the first difference found.
    /// </summary>
    /// <param name="original">The source PDF.</param>
    /// <param name="candidate">The PDF to check against it.</param>
    /// <param name="maxDecodedStreamBytes">Ceiling for any single decoded stream.</param>
    /// <param name="allowJpegRequantization">
    /// Also accept JPEG images whose coefficients are exactly the source's requantized to coarser
    /// tables, as <see cref="PdfOptimizerOptions.JpegQuality"/> produces.
    /// </param>
    public static string? FindDifference(
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> candidate,
        long maxDecodedStreamBytes = 256L << 20,
        bool allowJpegRequantization = false)
    {
        PdfFile a = PdfFile.Open(original, maxDecodedStreamBytes);
        PdfFile b = PdfFile.Open(candidate, maxDecodedStreamBytes);
        return EquivalenceVerifier.Verify(a, b, [], maxDecodedStreamBytes, allowJpegRequantization, Environment.ProcessorCount, CancellationToken.None, out _);
    }
}
