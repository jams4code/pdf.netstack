using System.Globalization;
using System.Security;
using System.Text;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack;

/// <summary>A standard the saved file conforms to.</summary>
public enum PdfConformance
{
    /// <summary>No particular standard.</summary>
    None,

    /// <summary>PDF/A-2b (ISO 19005-2), archival with reliable visual appearance.</summary>
    PdfA2B,

    /// <summary>PDF/A-2u: PDF/A-2b with every character mapped to Unicode, so text extracts reliably.</summary>
    PdfA2U,

    /// <summary>PDF/A-3b (ISO 19005-3): PDF/A-2b that may embed files of any type, as Factur-X and ZUGFeRD invoices do.</summary>
    PdfA3B,

    /// <summary>PDF/A-3u: PDF/A-3b with Unicode text.</summary>
    PdfA3U,

    /// <summary>
    /// PDF/A-1b (ISO 19005-1), based on PDF 1.4: written with a classic cross-reference table and
    /// no object streams. Transparency, JPEG 2000, optional content and embedded files are not
    /// allowed, and subset CID fonts get the CIDSet stream the part requires.
    /// </summary>
    PdfA1B,

    /// <summary>
    /// PDF/A-4 (ISO 19005-4), based on PDF 2.0. The document information is kept in the XMP
    /// metadata; the information dictionary in the file holds only the modification date.
    /// Embedded files must themselves be PDF/A-1, PDF/A-2 or PDF/A-4 files.
    /// </summary>
    PdfA4,

    /// <summary>PDF/A-4e: PDF/A-4 for engineering documents, which also allows 3D and RichMedia annotations and their actions.</summary>
    PdfA4E,

    /// <summary>PDF/A-4f: PDF/A-4 with at least one embedded file of any type, each declared as an associated file.</summary>
    PdfA4F,
}

/// <summary>
/// Prepares a document for PDF/A output (ISO 19005, parts 1 to 4): checks what cannot be
/// repaired, repairs what can (annotation flags, image interpolation, form appearance flags, LZW
/// compression, CIDSet streams for PDF/A-1), then adds an sRGB output intent and XMP metadata
/// that matches the document information, and declares embedded files as associated files.
/// </summary>
internal static class PdfArchive
{
    private const string Transparency = "Transparency (opacity, soft masks, images with an alpha channel, blend modes, transparency groups) is not allowed in PDF/A-1; use PDF/A-2 or later.";

    private static readonly Lazy<byte[]> Srgb = new(SrgbProfile.Create);

    /// <summary>Annotation types PDF/A-1 allows: those of PDF 1.4 except file attachments, sounds and movies (ISO 19005-1, 6.5.2).</summary>
    private static readonly HashSet<string> PdfA1Annotations =
    [
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Highlight", "Underline", "Squiggly", "StrikeOut",
        "Stamp", "Ink", "Popup", "Widget", "PrinterMark", "TrapNet",
    ];

    /// <summary>Action types PDF/A-1 allows (ISO 19005-1, 6.6.1): those of PDF 1.4 that neither run code nor play media.</summary>
    private static readonly HashSet<string> PdfA1Actions = ["GoTo", "GoToR", "Thread", "URI", "Named", "SubmitForm"];

    /// <summary>Action types defined by ISO 32000-1 and 32000-2; any other type is refused.</summary>
    private static readonly HashSet<string> KnownActions =
    [
        "GoTo", "GoToR", "GoToE", "GoToDp", "Launch", "Thread", "URI", "Sound", "Movie", "Hide", "Named", "SubmitForm",
        "ResetForm", "ImportData", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "JavaScript", "RichMediaExecute",
    ];

    /// <summary>Actions no part allows: they run code, play media, change what is shown or reach outside the file.</summary>
    private static readonly HashSet<string> ForbiddenActions =
        ["Launch", "Sound", "Movie", "ResetForm", "ImportData", "Hide", "SetOCGState", "Rendition", "Trans", "JavaScript"];

    /// <summary>The ISO 19005 part of a conformance level, or 0 for none.</summary>
    public static int Part(PdfConformance conformance) => conformance switch
    {
        PdfConformance.PdfA1B => 1,
        PdfConformance.PdfA2B or PdfConformance.PdfA2U => 2,
        PdfConformance.PdfA3B or PdfConformance.PdfA3U => 3,
        PdfConformance.PdfA4 or PdfConformance.PdfA4E or PdfConformance.PdfA4F => 4,
        _ => 0,
    };

    /// <summary>The header version a part fixes: PDF 1.4 for PDF/A-1, PDF 2.0 for PDF/A-4. Null for the others, which keep the document's.</summary>
    public static string? HeaderVersion(int part) => part switch
    {
        1 => "1.4",
        4 => "2.0",
        _ => null,
    };

    /// <summary>The pdfaid:conformance letter; null for plain PDF/A-4, which has none.</summary>
    private static string? Level(PdfConformance conformance) => conformance switch
    {
        PdfConformance.PdfA2U or PdfConformance.PdfA3U => "U",
        PdfConformance.PdfA4 => null,
        PdfConformance.PdfA4E => "E",
        PdfConformance.PdfA4F => "F",
        _ => "B",
    };

    private static string Name(PdfConformance conformance) =>
        "PDF/A-" + Part(conformance).ToString(CultureInfo.InvariantCulture) + (Level(conformance)?.ToLowerInvariant() ?? "");

    public static void Prepare(PdfDocument doc, PdfConformance conformance)
    {
        int part = Part(conformance);
        string? level = Level(conformance);
        bool unicode = level == "U";
        bool engineering = conformance == PdfConformance.PdfA4E;
        var problems = new List<string>();
        if (doc.Security.Target is not null) problems.Add("PDF/A files cannot be encrypted.");
        if (part is 2 or 3 && string.CompareOrdinal(doc.Version, "1.7") > 0) problems.Add($"PDF/A-{part} is based on PDF 1.7; this document is PDF {doc.Version}.");
        if (doc.UsesDeviceCmyk) problems.Add("DeviceCMYK colors or images are used; PDF/A with an sRGB output intent needs RGB, gray, or ICC based colors.");
        if (doc.Invoice is not null && part != 3) problems.Add("A Factur-X or ZUGFeRD invoice must be saved as PDF/A-3.");
        Resources resources = Collect(doc);
        CheckFonts(doc, resources.Fonts, problems, unicode, part);
        List<(PdfDictionary Descriptor, PdfStream Set)> cidSets = part == 1 ? CidSets(doc, resources.Fonts, problems) : [];
        CheckGraphicsStates(doc, resources.GraphicsStates, part, problems);
        CheckObjects(doc, part, problems);
        CheckScripts(doc, problems);
        CheckActions(doc, part, engineering, problems);
        CheckForm(doc, problems);
        CheckAnnotations(doc, part, engineering, problems);
        if (part == 1 && doc.Catalog["OCProperties"] is not null) problems.Add("Optional content (layers) is not allowed in PDF/A-1.");
        PrepareAttachments(doc, conformance, problems);
        InvoiceMetadata? invoice = part == 3 ? PdfInvoice.Check(doc, problems) : null;
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The document cannot be saved as " + Name(conformance) + ":\n- " + string.Join("\n- ", problems.Distinct()));
        }

        foreach ((PdfDictionary descriptor, PdfStream set) in cidSets) descriptor.Set("CIDSet", doc.AddObject(set));

        // The header carries the version the part fixes; a catalog entry must not claim another.
        if (part is 1 or 4) doc.Catalog.Remove("Version");
        AddOutputIntent(doc);
        WriteMetadata(doc, part, level, invoice);
    }

    private static void CheckFonts(PdfDocument doc, List<PdfDictionary> fonts, List<string> problems, bool unicode, int part)
    {
        foreach (PdfDictionary font in fonts)
        {
            string name = font.GetName("BaseFont") ?? "(unnamed font)";
            string subtype = font.GetName("Subtype") ?? "";
            if (subtype == "Type3") continue;
            PdfDictionary? descriptor = doc.Resolve(font["FontDescriptor"]) as PdfDictionary;
            PdfDictionary? cid = null;
            if (subtype == "Type0" && doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } d && doc.Resolve(d[0]) is PdfDictionary descendant)
            {
                cid = descendant;
                descriptor = doc.Resolve(cid["FontDescriptor"]) as PdfDictionary;
            }

            bool embedded = descriptor is not null && (descriptor["FontFile"] ?? descriptor["FontFile2"] ?? descriptor["FontFile3"]) is not null;
            if (!embedded) problems.Add($"Font {name} is not embedded. PDF/A embeds every font: draw with a PdfOpenTypeFont instead of the standard fonts.");
            if (unicode && subtype == "Type0" && font["ToUnicode"] is null) problems.Add($"Font {name} has no ToUnicode map.");
            if (part != 1 || descriptor is null) continue;

            // PDF 1.4 knows no OpenType font programs, and its Type 1 subsets list their glyphs (ISO 19005-1, 6.3.4 and 6.3.5).
            if (doc.Resolve(descriptor["FontFile3"]) is PdfStream program && program.Dictionary.GetName("Subtype") == "OpenType")
            {
                problems.Add($"Font {name} embeds an OpenType program, which PDF/A-1 (PDF 1.4) cannot hold.");
            }

            if (subtype is "Type1" or "MMType1" && embedded && IsSubset(name) && descriptor["CharSet"] is null)
            {
                problems.Add($"Font {name} is a Type 1 subset without the CharSet PDF/A-1 requires.");
            }

            // Identity is what an absent map means; PDF/A-1 wants it written (6.3.3.2).
            if (cid?.GetName("Subtype") == "CIDFontType2" && cid["CIDToGIDMap"] is null) cid.Set("CIDToGIDMap", new PdfName("Identity"));
        }
    }

    private static bool IsSubset(string name) =>
        name.Length > 7 && name[6] == '+' && name.Take(6).All(c => c is >= 'A' and <= 'Z');

    /// <summary>
    /// The CIDSet of every subset CID font (ISO 19005-1, 6.3.5): one bit per CID the embedded
    /// program holds, read from the program itself. TrueType subsets keep their glyph ids, so the
    /// program holds every glyph up to the highest used one, the unused ones empty.
    /// </summary>
    private static List<(PdfDictionary Descriptor, PdfStream Set)> CidSets(PdfDocument doc, List<PdfDictionary> fonts, List<string> problems)
    {
        var sets = new List<(PdfDictionary, PdfStream)>();
        foreach (PdfDictionary font in fonts)
        {
            if (font.GetName("Subtype") != "Type0" || doc.Resolve(font["DescendantFonts"]) is not PdfArray { Count: > 0 } d || doc.Resolve(d[0]) is not PdfDictionary cid) continue;
            string name = cid.GetName("BaseFont") ?? font.GetName("BaseFont") ?? "";
            if (!IsSubset(name) || doc.Resolve(cid["FontDescriptor"]) is not PdfDictionary descriptor) continue;
            byte[]? set = null;
            try
            {
                if (doc.Resolve(descriptor["FontFile2"]) is PdfStream trueType)
                {
                    byte[]? map = doc.Resolve(cid["CIDToGIDMap"]) is PdfStream m ? doc.GetStreamData(m) : null;
                    set = TrueTypeCids(doc.GetStreamData(trueType), map);
                }
                else if (doc.Resolve(descriptor["FontFile3"]) is PdfStream compact && compact.Dictionary.GetName("Subtype") == "CIDFontType0C")
                {
                    set = Bits(new CffFont(doc.GetStreamData(compact)).GlyphToCid);
                }
                else
                {
                    continue;
                }
            }
            catch (Exception e) when (e is PdfFormatException or FontFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                set = null;
            }

            if (set is null) problems.Add($"Font {name}: its embedded program cannot be read to list its glyphs, which PDF/A-1 requires (CIDSet).");
            else sets.Add((descriptor, PdfStream.FromData(set)));
        }

        return sets;
    }

    /// <summary>The CIDs of a TrueType program: CID = glyph id, or through a CIDToGIDMap stream, for every glyph the maxp table counts.</summary>
    private static byte[]? TrueTypeCids(byte[] program, byte[]? map)
    {
        if (GlyphCount(program) is not int glyphs) return null;
        var cids = new List<int>();
        if (map is null)
        {
            for (int g = 0; g < glyphs; g++) cids.Add(g);
        }
        else
        {
            for (int c = 0; c + 1 < map.Length && c / 2 <= ushort.MaxValue; c += 2)
            {
                int gid = map[c] << 8 | map[c + 1];
                if (gid < glyphs && (gid != 0 || c == 0)) cids.Add(c / 2);
            }
        }

        return Bits(cids);
    }

    /// <summary>numGlyphs from the maxp table of an sfnt program, or null when it cannot be found.</summary>
    private static int? GlyphCount(byte[] sfnt)
    {
        if (sfnt.Length < 12) return null;
        int tables = sfnt[4] << 8 | sfnt[5];
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            if (record + 16 > sfnt.Length) return null;
            if (sfnt[record] != 'm' || sfnt[record + 1] != 'a' || sfnt[record + 2] != 'x' || sfnt[record + 3] != 'p') continue;
            long offset = (long)sfnt[record + 8] << 24 | (long)sfnt[record + 9] << 16 | (long)sfnt[record + 10] << 8 | sfnt[record + 11];
            return offset + 6 <= sfnt.Length ? sfnt[offset + 4] << 8 | sfnt[offset + 5] : null;
        }

        return null;
    }

    /// <summary>A CIDSet bit string: the high order bit of the first byte is CID 0.</summary>
    private static byte[] Bits(IEnumerable<int> cids)
    {
        int[] all = [.. cids.Where(c => c is >= 0 and <= ushort.MaxValue)];
        var bits = new byte[(all.Length == 0 ? 0 : all.Max()) / 8 + 1];
        foreach (int c in all) bits[c >> 3] |= (byte)(0x80 >> (c & 7));
        return bits;
    }

    /// <summary>What the resource dictionaries of pages, forms, patterns, soft masks, Type 3 fonts and annotation appearances use.</summary>
    private sealed class Resources
    {
        public List<PdfDictionary> Fonts { get; } = [];

        public List<PdfDictionary> GraphicsStates { get; } = [];
    }

    private static Resources Collect(PdfDocument doc)
    {
        var found = new Resources();
        var seen = new HashSet<PdfDictionary>();
        var pending = new Stack<PdfDictionary>();
        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Resources"]) is PdfDictionary r) pending.Push(r);
            if (doc.Resolve(page.Dictionary["Annots"]) is PdfArray annots)
            {
                foreach (PdfObject a in annots)
                {
                    if (doc.Resolve(a) is PdfDictionary annot && doc.Resolve(annot["AP"]) is PdfDictionary ap) PushAppearances(ap);
                }
            }
        }

        int budget = 100_000;
        while (pending.Count > 0 && budget-- > 0)
        {
            PdfDictionary resources = pending.Pop();
            if (!seen.Add(resources)) continue;
            if (doc.Resolve(resources["Font"]) is PdfDictionary fontDict)
            {
                foreach (KeyValuePair<string, PdfObject> f in fontDict)
                {
                    if (doc.Resolve(f.Value) is PdfDictionary font && seen.Add(font))
                    {
                        found.Fonts.Add(font);
                        if (doc.Resolve(font["Resources"]) is PdfDictionary t3) pending.Push(t3);
                    }
                }
            }

            if (doc.Resolve(resources["ExtGState"]) is PdfDictionary states)
            {
                foreach (KeyValuePair<string, PdfObject> s in states)
                {
                    if (doc.Resolve(s.Value) is not PdfDictionary state || !seen.Add(state)) continue;
                    found.GraphicsStates.Add(state);
                    if (doc.Resolve(state["SMask"]) is PdfDictionary mask && doc.Resolve(mask["G"]) is PdfStream group && doc.Resolve(group.Dictionary["Resources"]) is PdfDictionary maskResources)
                    {
                        pending.Push(maskResources);
                    }
                }
            }

            foreach (string category in new[] { "XObject", "Pattern" })
            {
                if (doc.Resolve(resources[category]) is not PdfDictionary items) continue;
                foreach (KeyValuePair<string, PdfObject> x in items)
                {
                    PdfDictionary? inner = doc.Resolve(x.Value) switch
                    {
                        PdfStream s => s.Dictionary,
                        PdfDictionary d => d,
                        _ => null,
                    };
                    if (inner is not null && doc.Resolve(inner["Resources"]) is PdfDictionary nested) pending.Push(nested);
                }
            }
        }

        return found;

        void PushAppearances(PdfDictionary ap)
        {
            foreach (KeyValuePair<string, PdfObject> state in ap)
            {
                switch (doc.Resolve(state.Value))
                {
                    case PdfStream s when doc.Resolve(s.Dictionary["Resources"]) is PdfDictionary r:
                        pending.Push(r);
                        break;
                    case PdfDictionary states:
                        foreach (KeyValuePair<string, PdfObject> sub in states)
                        {
                            if (doc.Resolve(sub.Value) is PdfStream ss && doc.Resolve(ss.Dictionary["Resources"]) is PdfDictionary sr) pending.Push(sr);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Transfer functions are refused by every part; PDF/A-1 also refuses soft masks, opacity and blend modes other than Normal.</summary>
    private static void CheckGraphicsStates(PdfDocument doc, List<PdfDictionary> states, int part, List<string> problems)
    {
        foreach (PdfDictionary state in states)
        {
            if (state["TR"] is not null || (state["TR2"] is not null && doc.Resolve(state["TR2"]) is not PdfName { Value: "Default" }))
            {
                problems.Add("A graphics state sets a transfer function, which PDF/A forbids.");
            }

            if (part != 1) continue;
            bool masked = state["SMask"] is not null && doc.Resolve(state["SMask"]) is not PdfName { Value: "None" };
            bool faded = Opacity(state["CA"]) < 1 || Opacity(state["ca"]) < 1;
            bool blended = doc.Resolve(state["BM"]) switch
            {
                null => false,
                PdfName n => n.Value is not ("Normal" or "Compatible"),
                PdfArray modes => modes.Any(m => doc.Resolve(m) is not PdfName { Value: "Normal" or "Compatible" }),
                _ => true,
            };
            if (masked || faded || blended) problems.Add(Transparency);
        }

        double Opacity(PdfObject? value) => doc.Resolve(value) is PdfNumber n ? n.ToDouble() : 1;
    }

    /// <summary>
    /// Every object the saved file will hold, reached from the catalog and the pages: streams
    /// that keep their data in other files, LZW (re-encoded with Flate where it can be), image and
    /// form XObject entries PDF/A forbids, and for PDF/A-1 JPEG 2000, transparency and ICC
    /// profiles newer than version 2. Image interpolation is switched off.
    /// </summary>
    private static void CheckObjects(PdfDocument doc, int part, List<string> problems)
    {
        // Pages that were removed and the old page tree are not written; references to them are skipped.
        var seen = new HashSet<int>();
        var pending = new Stack<(PdfObject Value, int Depth)>();
        pending.Push((doc.Catalog, 0));
        foreach (PdfPage page in doc.Pages)
        {
            seen.Add(page.Number);
            pending.Push((page.Dictionary, 0));
            if (part == 1 && doc.Resolve(page.Dictionary["Group"]) is PdfDictionary group && group.GetName("S") == "Transparency") problems.Add(Transparency);
        }

        int budget = 10_000_000;
        while (pending.Count > 0 && budget-- > 0)
        {
            (PdfObject value, int depth) = pending.Pop();
            if (depth > PdfParser.MaxNesting) continue;
            switch (value)
            {
                case PdfReference r:
                    if (doc.IsPageTreeNode(r.Number) || doc.IsPage(r.Number) || !seen.Add(r.Number)) break;
                    PdfObject? target = doc.Resolve(r);
                    if (target is PdfStream stream) CheckStream(doc, stream, part, problems);
                    if (target is not null) pending.Push((target, depth + 1));
                    break;
                case PdfStream s:
                    pending.Push((s.Dictionary, depth + 1));
                    break;
                case PdfDictionary d:
                    for (int i = 0; i < d.Count; i++)
                    {
                        // Parents are reached from their own roots; a page's parent is the old page tree.
                        (string key, PdfObject item) = d.EntryAt(i);
                        if (key != "Parent") pending.Push((item, depth + 1));
                    }

                    break;
                case PdfArray a:
                    if (part == 1 && a.Count == 2 && a[0] is PdfName { Value: "ICCBased" } && doc.Resolve(a[1]) is PdfStream profile) CheckProfile(doc, profile, problems);
                    foreach (PdfObject item in a.Items) pending.Push((item, depth + 1));
                    break;
            }
        }
    }

    private static void CheckStream(PdfDocument doc, PdfStream stream, int part, List<string> problems)
    {
        PdfDictionary d = stream.Dictionary;
        if (d["F"] is not null || d["FFilter"] is not null || d["FDecodeParms"] is not null) problems.Add("A stream keeps its data in an external file, which PDF/A forbids.");

        List<string> filters = doc.Resolve(d["Filter"]) switch
        {
            PdfName n => [n.Value],
            PdfArray a => [.. a.Select(f => doc.Resolve(f) is PdfName n ? n.Value : "")],
            _ => [],
        };
        if (filters.Contains("LZWDecode"))
        {
            // LZW is forbidden; the general filters decode losslessly and Flate replaces them.
            if (filters.All(f => f is "LZWDecode" or "FlateDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "RunLengthDecode"))
            {
                try
                {
                    byte[] plain = doc.GetStreamData(stream);
                    stream.Data = Filters.StreamCodec.Deflate(plain, System.IO.Compression.CompressionLevel.Optimal);
                    d.Set("Filter", new PdfName("FlateDecode"));
                    d.Remove("DecodeParms");
                    d.Remove("DL");
                }
                catch (PdfFormatException)
                {
                    problems.Add("An LZW compressed stream cannot be decoded, so it cannot be re-encoded; PDF/A forbids LZW.");
                }
            }
            else
            {
                problems.Add("An image is LZW compressed ahead of an image codec; PDF/A forbids LZW.");
            }
        }

        if (part == 1 && filters.Contains("JPXDecode")) problems.Add("JPEG 2000 images are not allowed in PDF/A-1.");
        switch (d.GetName("Subtype"))
        {
            case "Image":
                if (doc.Resolve(d["Interpolate"]) is PdfBoolean { Value: true }) d.Set("Interpolate", PdfBoolean.False);
                if (d["Alternates"] is not null) problems.Add("Images with alternate versions are not allowed.");
                if (d["OPI"] is not null) problems.Add("OPI proxy images are not allowed.");
                if (part == 1 && d["SMask"] is not null) problems.Add(Transparency);
                break;
            case "Form":
                if (d["OPI"] is not null) problems.Add("OPI proxy images are not allowed.");
                if (d["Ref"] is not null) problems.Add("Reference XObjects, which show a page of another file, are not allowed.");
                if (d.GetName("Subtype2") == "PS" || d["PS"] is not null) problems.Add("PostScript XObjects are not allowed.");
                if (part == 1 && doc.Resolve(d["Group"]) is PdfDictionary group && group.GetName("S") == "Transparency") problems.Add(Transparency);
                break;
            case "PS":
                if (d.GetName("Type") is null or "XObject") problems.Add("PostScript XObjects are not allowed.");
                break;
        }
    }

    /// <summary>PDF 1.4 reads ICC profiles up to version 2 (ISO 19005-1, 6.2.3.3).</summary>
    private static void CheckProfile(PdfDocument doc, PdfStream profile, List<string> problems)
    {
        try
        {
            byte[] data = doc.GetStreamData(profile);
            if (data.Length < 12) problems.Add("An ICC profile is damaged.");
            else if (data[8] > 2) problems.Add($"An ICC profile of version {data[8]} is used; PDF/A-1 accepts version 2 profiles only.");
        }
        catch (PdfFormatException)
        {
            problems.Add("An ICC profile cannot be read.");
        }
    }

    private static void CheckScripts(PdfDocument doc, List<string> problems)
    {
        if (doc.Resolve(doc.Catalog["Names"]) is PdfDictionary names && names["JavaScript"] is not null) problems.Add("Document level JavaScript is not allowed.");
        if (doc.Catalog["AA"] is not null) problems.Add("Document additional actions are not allowed.");
    }

    /// <summary>
    /// The open action, link and widget actions, bookmark actions and their Next chains, and
    /// additional actions on pages, widgets and fields (ISO 19005-1, 6.6; ISO 19005-2 and 4, 6.5).
    /// </summary>
    private static void CheckActions(PdfDocument doc, int part, bool engineering, List<string> problems)
    {
        var seen = new HashSet<PdfDictionary>();
        Check(doc.Catalog["OpenAction"]);
        foreach (PdfPage page in doc.Pages)
        {
            if (page.Dictionary["AA"] is not null) problems.Add("Page additional actions are not allowed.");
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is not PdfDictionary annot) continue;
                Check(annot["A"]);
                if (annot["AA"] is not null && (part == 1 || annot.GetName("Subtype") == "Widget")) problems.Add("Annotations and form fields cannot carry additional actions in PDF/A.");
            }
        }

        // Bookmarks: siblings through Next, children through First.
        var items = new Stack<PdfObject?>();
        var visited = new HashSet<PdfDictionary>();
        if (doc.Resolve(doc.Catalog["Outlines"]) is PdfDictionary outlines) items.Push(outlines["First"]);
        while (items.Count > 0 && visited.Count < 100_000)
        {
            if (doc.Resolve(items.Pop()) is not PdfDictionary item || !visited.Add(item)) continue;
            Check(item["A"]);
            items.Push(item["Next"]);
            items.Push(item["First"]);
        }

        // Fields that are not widgets themselves: through the form's Kids.
        var fields = new Stack<PdfObject>();
        visited.Clear();
        if (doc.Resolve(doc.Catalog["AcroForm"]) is PdfDictionary form && doc.Resolve(form["Fields"]) is PdfArray roots)
        {
            foreach (PdfObject root in roots) fields.Push(root);
        }

        while (fields.Count > 0 && visited.Count < 100_000)
        {
            if (doc.Resolve(fields.Pop()) is not PdfDictionary field || !visited.Add(field)) continue;
            if (field["AA"] is not null) problems.Add("Annotations and form fields cannot carry additional actions in PDF/A.");
            Check(field["A"]);
            if (doc.Resolve(field["Kids"]) is PdfArray kids)
            {
                foreach (PdfObject kid in kids) fields.Push(kid);
            }
        }

        void Check(PdfObject? first)
        {
            var pending = new Stack<PdfObject?>();
            pending.Push(first);
            int budget = 10_000;
            while (pending.Count > 0 && budget-- > 0)
            {
                // An open action may also be a plain destination array.
                if (doc.Resolve(pending.Pop()) is not PdfDictionary action || !seen.Add(action)) continue;
                if (ActionProblem(action, part, engineering) is string problem) problems.Add(problem);
                switch (doc.Resolve(action["Next"]))
                {
                    case PdfDictionary next:
                        pending.Push(next);
                        break;
                    case PdfArray chain:
                        foreach (PdfObject next in chain) pending.Push(next);
                        break;
                }
            }
        }
    }

    private static string? ActionProblem(PdfDictionary action, int part, bool engineering)
    {
        string type = action.GetName("S") ?? "";
        if (type == "Named")
        {
            string named = action.GetName("N") ?? "";
            return named is "NextPage" or "PrevPage" or "FirstPage" or "LastPage"
                ? null
                : $"The named action {named} is not allowed; PDF/A allows only NextPage, PrevPage, FirstPage and LastPage.";
        }

        bool allowed = part == 1
            ? PdfA1Actions.Contains(type)
            : KnownActions.Contains(type) && !ForbiddenActions.Contains(type)
                && (part == 4 || type is not ("GoToDp" or "GoTo3DView" or "RichMediaExecute"))
                && (part != 4 || engineering || type is not ("GoTo3DView" or "RichMediaExecute"));
        if (allowed) return null;
        if (type.Length == 0) return "An action without a type is not allowed.";
        return part == 4 && type is "GoTo3DView" or "RichMediaExecute" ? $"{type} actions need PDF/A-4e." : $"{type} actions are not allowed in PDF/A-{part}.";
    }

    /// <summary>
    /// XFA is refused. NeedAppearances is dropped: every widget must carry its appearance anyway,
    /// so viewers have nothing to regenerate.
    /// </summary>
    private static void CheckForm(PdfDocument doc, List<string> problems)
    {
        if (doc.Catalog["NeedsRendering"] is not null && doc.Resolve(doc.Catalog["NeedsRendering"]) is not PdfBoolean { Value: false }) problems.Add("Dynamic XFA forms are not allowed.");
        if (doc.Resolve(doc.Catalog["AcroForm"]) is not PdfDictionary form) return;
        if (form["XFA"] is not null) problems.Add("XFA forms are not allowed; remove the XFA entry to keep the AcroForm fields.");
        if (form["NeedAppearances"] is not null) form.Remove("NeedAppearances");
    }

    /// <summary>Annotations must print, must not hide, and must carry an appearance (links and popups excepted).</summary>
    private static void CheckAnnotations(PdfDocument doc, int part, bool engineering, List<string> problems)
    {
        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is not PdfDictionary annot) continue;
                string subtype = annot.GetName("Subtype") ?? "";
                if (part == 1 && !PdfA1Annotations.Contains(subtype))
                {
                    problems.Add($"{subtype} annotations are not allowed in PDF/A-1.");
                    continue;
                }

                if (subtype is "Movie" or "Sound" or "Screen" || (subtype is "3D" or "RichMedia" && !engineering))
                {
                    problems.Add(part == 4 && subtype is "3D" or "RichMedia" ? $"{subtype} annotations need PDF/A-4e." : $"{subtype} annotations are not allowed.");
                    continue;
                }

                if (subtype == "Popup") continue;
                long flags = doc.Resolve(annot["F"]) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
                annot.Set("F", PdfNumber.FromInteger((flags | 4) & ~(1L | 2 | 32 | 256)));
                PdfRectangle r = PdfRectangle.From(annot["Rect"], doc.Resolve) ?? default;
                if (subtype != "Link" && r.Width != 0 && r.Height != 0 && annot["AP"] is null) problems.Add($"A {subtype} annotation on page {page.Index + 1} has no appearance.");
                if (part == 1 && doc.Resolve(annot["CA"]) is PdfNumber opacity && opacity.ToDouble() < 1) problems.Add(Transparency);
            }
        }
    }

    /// <summary>
    /// PDF/A-1 and PDF/A-2 forbid embedded files; PDF/A-3 and PDF/A-4f allow any, each declared as
    /// an associated file with its relationship, MIME type and modification date. PDF/A-4 and 4e
    /// accept PDF/A-1, PDF/A-2 and PDF/A-4 files only, declared the same way, and PDF/A-4f needs at
    /// least one file. Files in the attachment list and files of file attachment annotations are
    /// covered alike.
    /// </summary>
    private static void PrepareAttachments(PdfDocument doc, PdfConformance conformance, List<string> problems)
    {
        int part = Part(conformance);
        var specs = new List<(PdfDictionary Spec, PdfObject? Reference)>();
        // Attachment entries keep their references, so the associated file is the same object, not a copy.
        foreach ((string _, PdfObject entry) in doc.Attachments.Entries())
        {
            if (doc.Resolve(entry) is PdfDictionary spec) specs.Add((spec, entry as PdfReference));
        }

        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is PdfDictionary annot && annot.GetName("Subtype") == "FileAttachment" && doc.Resolve(annot["FS"]) is PdfDictionary spec && spec["EF"] is not null)
                {
                    specs.Add((spec, annot["FS"] as PdfReference));
                }
            }
        }

        if (specs.Count == 0)
        {
            if (conformance == PdfConformance.PdfA4F) problems.Add("PDF/A-4f needs at least one embedded file; save as PDF/A-4 when there is none.");
            return;
        }

        if (part is 1 or 2)
        {
            problems.Add($"PDF/A-{part} cannot embed files; use PDF/A-3 for attachments such as invoice XML.");
            return;
        }

        bool anyType = part == 3 || conformance == PdfConformance.PdfA4F;
        var af = doc.Resolve(doc.Catalog["AF"]) as PdfArray ?? new PdfArray();
        foreach ((PdfDictionary spec, PdfObject? reference) in specs)
        {
            if (spec["AFRelationship"] is null) spec.Set("AFRelationship", new PdfName("Unspecified"));
            if (doc.Resolve(spec["EF"]) is PdfDictionary ef)
            {
                foreach (KeyValuePair<string, PdfObject> e in ef)
                {
                    if (doc.Resolve(e.Value) is not PdfStream file) continue;
                    if (!anyType && !IsArchivalPdf(doc, file))
                    {
                        string name = doc.Resolve(spec["UF"] ?? spec["F"]) is PdfString s ? s.ToText() : "an attachment";
                        problems.Add($"{Name(conformance)} embeds only PDF/A-1, PDF/A-2 and PDF/A-4 files, and {name} is not one; save as PDF/A-4f to embed files of any type.");
                    }

                    if (file.Dictionary["Subtype"] is null) file.Dictionary.Set("Subtype", new PdfName("application/octet-stream"));
                    var parameters = doc.Resolve(file.Dictionary["Params"]) as PdfDictionary ?? new PdfDictionary();
                    if (parameters["ModDate"] is null) parameters.Set("ModDate", new PdfString(Encoding.ASCII.GetBytes(Documents.PdfText.FormatDate(DateTimeOffset.UtcNow)).AsSpan()));
                    file.Dictionary.Set("Params", parameters);
                }
            }

            if (!af.Any(x => doc.Resolve(x) == spec)) af.Add(reference ?? doc.AddObject(spec));
        }

        doc.Catalog.Set("AF", af);
    }

    /// <summary>Whether an embedded file is a PDF whose XMP declares PDF/A-1, PDF/A-2 or PDF/A-4.</summary>
    private static bool IsArchivalPdf(PdfDocument doc, PdfStream file)
    {
        try
        {
            byte[] data = doc.GetStreamData(file);
            PdfFile pdf = PdfFile.Open(data, doc.DecodeLimit);
            return DeclaredPart(pdf, doc.DecodeLimit) is 1 or 2 or 4;
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    /// <summary>The pdfaid:part a file's XMP metadata declares, as an element or an attribute; null when it declares none.</summary>
    internal static int? DeclaredPart(PdfFile file, long decodeLimit)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (file.Resolve(catalog?["Metadata"]) is not PdfStream metadata) return null;
        string xmp;
        try
        {
            using Filters.DecodedStream decoded = Filters.StreamCodec.Decode(metadata.Data, Filters.StreamCodec.GetFilters(metadata.Dictionary, file.Resolve),
                Filters.DecodeDepth.Full, decodeLimit, file.Resolve);
            xmp = Compat.Latin1.GetString(decoded.Data.Span);
        }
        catch (Filters.StreamDecodeException)
        {
            return null;
        }

        for (int at = xmp.IndexOf("pdfaid:part", StringComparison.Ordinal); at >= 0; at = xmp.IndexOf("pdfaid:part", at + 1, StringComparison.Ordinal))
        {
            // <pdfaid:part>2</pdfaid:part> or pdfaid:part="2"; a closing tag is skipped.
            int i = at + "pdfaid:part".Length;
            while (i < xmp.Length && xmp[i] is ' ' or '\t' or '\r' or '\n' or '=' or '>' or '"' or '\'') i++;
            if (i < xmp.Length && xmp[i] is >= '1' and <= '9' && (i + 1 == xmp.Length || xmp[i + 1] is < '0' or > '9')) return xmp[i] - '0';
        }

        return null;
    }

    private static void AddOutputIntent(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog["OutputIntents"]) is PdfArray existing && existing.Any(i => doc.Resolve(i) is PdfDictionary d && d.GetName("S") == "GTS_PDFA1"))
        {
            return;
        }

        PdfStream profile = PdfStream.FromData(Srgb.Value);
        profile.Dictionary.Set("N", PdfNumber.FromInteger(3));
        var intent = new PdfDictionary
        {
            ["Type"] = new PdfName("OutputIntent"),
            ["S"] = new PdfName("GTS_PDFA1"),
            ["OutputConditionIdentifier"] = PdfString.FromText("sRGB IEC61966-2.1"),
            ["RegistryName"] = PdfString.FromText("http://www.color.org"),
            ["Info"] = PdfString.FromText("sRGB IEC61966-2.1"),
            ["DestOutputProfile"] = doc.AddObject(profile),
        };
        doc.Catalog.Set("OutputIntents", PdfArray.Of(doc.AddObject(intent)));
    }

    /// <summary>
    /// XMP with the PDF/A identification and the same values as the information dictionary.
    /// PDF/A-4 adds the revision year and names a conformance level only for 4e and 4f.
    /// </summary>
    private static void WriteMetadata(PdfDocument doc, int part, string? conformance, InvoiceMetadata? invoice)
    {
        PdfDocumentInfo info = doc.Info;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        now = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        info.CreationDate ??= now;
        info.ModificationDate = now;
        info.Producer ??= "PdfNetStack";
        DateTimeOffset created = info.CreationDate.Value;

        byte[] id = doc.EnsureId();
        string uuid = new Guid(id.Length >= 16 ? id.AsSpan(0, 16).ToArray() : Guid.NewGuid().ToByteArray()).ToString();
        var x = new StringBuilder();
        x.Append("<?xpacket begin=\"").Append((char)0xFEFF).Append("\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        x.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        x.Append("<rdf:Description rdf:about=\"\"");
        x.Append(" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        x.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\">\n");
        x.Append("<pdfaid:part>").Append(part).Append("</pdfaid:part>\n");
        if (part == 4) x.Append("<pdfaid:rev>2020</pdfaid:rev>\n");
        if (conformance is not null) x.Append("<pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n");
        x.Append("<dc:format>application/pdf</dc:format>\n");
        if (info.Title is string title) x.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (info.Author is string author) x.Append("<dc:creator><rdf:Seq><rdf:li>").Append(Escape(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (info.Subject is string subject) x.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        if (info.Keywords is string keywords) x.Append("<pdf:Keywords>").Append(Escape(keywords)).Append("</pdf:Keywords>\n");
        x.Append("<pdf:Producer>").Append(Escape(info.Producer!)).Append("</pdf:Producer>\n");
        if (info.Creator is string tool) x.Append("<xmp:CreatorTool>").Append(Escape(tool)).Append("</xmp:CreatorTool>\n");
        x.Append("<xmp:CreateDate>").Append(Iso(created)).Append("</xmp:CreateDate>\n");
        x.Append("<xmp:ModifyDate>").Append(Iso(now)).Append("</xmp:ModifyDate>\n");
        x.Append("<xmp:MetadataDate>").Append(Iso(now)).Append("</xmp:MetadataDate>\n");
        x.Append("<xmpMM:DocumentID>uuid:").Append(uuid).Append("</xmpMM:DocumentID>\n");
        x.Append("<xmpMM:InstanceID>uuid:").Append(Guid.NewGuid()).Append("</xmpMM:InstanceID>\n");
        x.Append("</rdf:Description>\n");
        if (invoice is not null) AppendInvoice(x, invoice);
        x.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        x.Append(' ', 2000).Append("\n<?xpacket end=\"w\"?>");

        // Metadata stays uncompressed so archival tools can read it without decoding.
        var stream = PdfStream.FromData(Encoding.UTF8.GetBytes(x.ToString()), compress: false);
        stream.Dictionary.Set("Type", new PdfName("Metadata"));
        stream.Dictionary.Set("Subtype", new PdfName("XML"));
        doc.Catalog.Set("Metadata", doc.AddObject(stream));
    }

    /// <summary>
    /// The Factur-X properties and, as PDF/A requires for any schema outside its predefined list,
    /// the extension schema that declares them (ISO 19005-3, 6.6.2.3; Factur-X 1.0, 6.2.2).
    /// </summary>
    private static void AppendInvoice(StringBuilder x, InvoiceMetadata invoice)
    {
        x.Append("<rdf:Description rdf:about=\"\" xmlns:fx=\"").Append(PdfInvoice.Namespace).Append("\">\n");
        x.Append("<fx:DocumentType>INVOICE</fx:DocumentType>\n");
        x.Append("<fx:DocumentFileName>").Append(Escape(invoice.FileName)).Append("</fx:DocumentFileName>\n");
        x.Append("<fx:Version>").Append(Escape(invoice.Version)).Append("</fx:Version>\n");
        x.Append("<fx:ConformanceLevel>").Append(Escape(invoice.ConformanceLevel)).Append("</fx:ConformanceLevel>\n");
        x.Append("</rdf:Description>\n");
        x.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"");
        x.Append(" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n");
        x.Append("<pdfaExtension:schemas>\n<rdf:Bag>\n<rdf:li rdf:parseType=\"Resource\">\n");
        x.Append("<pdfaSchema:schema>Factur-X PDFA Extension Schema</pdfaSchema:schema>\n");
        x.Append("<pdfaSchema:namespaceURI>").Append(PdfInvoice.Namespace).Append("</pdfaSchema:namespaceURI>\n");
        x.Append("<pdfaSchema:prefix>fx</pdfaSchema:prefix>\n");
        x.Append("<pdfaSchema:property>\n<rdf:Seq>\n");
        Property("DocumentFileName", "The name of the embedded XML document");
        Property("DocumentType", "The type of the hybrid document in capital letters, such as INVOICE or ORDER");
        Property("Version", "The version of the standard applying to the embedded XML document");
        Property("ConformanceLevel", "The conformance level of the embedded XML document");
        x.Append("</rdf:Seq>\n</pdfaSchema:property>\n</rdf:li>\n</rdf:Bag>\n</pdfaExtension:schemas>\n</rdf:Description>\n");

        void Property(string name, string description)
        {
            x.Append("<rdf:li rdf:parseType=\"Resource\">\n");
            x.Append("<pdfaProperty:name>").Append(name).Append("</pdfaProperty:name>\n");
            x.Append("<pdfaProperty:valueType>Text</pdfaProperty:valueType>\n");
            x.Append("<pdfaProperty:category>external</pdfaProperty:category>\n");
            x.Append("<pdfaProperty:description>").Append(description).Append("</pdfaProperty:description>\n");
            x.Append("</rdf:li>\n");
        }
    }

    private static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
        + (d.Offset == TimeSpan.Zero ? "Z" : (d.Offset < TimeSpan.Zero ? "-" : "+") + d.Offset.ToString("hh\\:mm", CultureInfo.InvariantCulture));

    private static string Escape(string s) => SecurityElement.Escape(s) ?? "";
}

/// <summary>
/// An sRGB display profile (ICC v2, matrix and tone curves) computed from the sRGB definition
/// (IEC 61966-2-1): D50 adapted primaries and the sRGB transfer curve. Written here so the
/// library ships no third party profile.
/// </summary>
internal static class SrgbProfile
{
    public static byte[] Create()
    {
        var curve = new byte[12 + 2 * 1024];
        WriteTag(curve, 0, "curv");
        Be32(curve, 8, 1024);
        for (int i = 0; i < 1024; i++)
        {
            double v = i / 1023.0;
            double linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            int value = (int)Math.Round(linear * 65535);
            curve[12 + 2 * i] = (byte)(value >> 8);
            curve[13 + 2 * i] = (byte)value;
        }

        var tags = new List<(string Tag, byte[] Data)>
        {
            ("desc", Description("sRGB IEC61966-2.1 (PdfNetStack)")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", curve),
            ("gTRC", curve),
            ("bTRC", curve),
        };

        // Header, tag table, then tag data; the three curves share one block.
        int offset = 128 + 4 + 12 * tags.Count;
        var placed = new Dictionary<byte[], int>();
        var body = new List<byte>();
        var table = new List<(string Tag, int Offset, int Size)>();
        foreach ((string tag, byte[] data) in tags)
        {
            if (!placed.TryGetValue(data, out int at))
            {
                while ((offset + body.Count) % 4 != 0) body.Add(0);
                at = offset + body.Count;
                placed[data] = at;
                body.AddRange(data);
            }

            table.Add((tag, at, data.Length));
        }

        while (body.Count % 4 != 0) body.Add(0);
        var profile = new byte[offset + body.Count];
        Be32(profile, 0, profile.Length);
        Be32(profile, 8, 0x02100000);
        WriteTag(profile, 12, "mntr");
        WriteTag(profile, 16, "RGB ");
        WriteTag(profile, 20, "XYZ ");
        int[] date = [2026, 1, 1, 0, 0, 0];
        for (int i = 0; i < 6; i++)
        {
            profile[24 + 2 * i] = (byte)(date[i] >> 8);
            profile[25 + 2 * i] = (byte)date[i];
        }

        WriteTag(profile, 36, "acsp");
        Be32(profile, 68, Fixed(0.9642));
        Be32(profile, 72, Fixed(1.0));
        Be32(profile, 76, Fixed(0.8249));
        Be32(profile, 128, tags.Count);
        for (int i = 0; i < table.Count; i++)
        {
            WriteTag(profile, 132 + 12 * i, table[i].Tag);
            Be32(profile, 136 + 12 * i, table[i].Offset);
            Be32(profile, 140 + 12 * i, table[i].Size);
        }

        body.CopyTo(profile, offset);
        return profile;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        WriteTag(b, 0, "XYZ ");
        Be32(b, 8, Fixed(x));
        Be32(b, 12, Fixed(y));
        Be32(b, 16, Fixed(z));
        return b;
    }

    private static byte[] Text(string text)
    {
        var b = new byte[8 + text.Length + 1];
        WriteTag(b, 0, "text");
        Encoding.ASCII.GetBytes(text).CopyTo(b, 8);
        return b;
    }

    /// <summary>textDescriptionType (ICC v2): ASCII, then empty Unicode and ScriptCode parts.</summary>
    private static byte[] Description(string text)
    {
        var b = new byte[8 + 4 + text.Length + 1 + 4 + 4 + 2 + 1 + 67];
        WriteTag(b, 0, "desc");
        Be32(b, 8, text.Length + 1);
        Encoding.ASCII.GetBytes(text).CopyTo(b, 12);
        return b;
    }

    private static int Fixed(double v) => (int)Math.Round(v * 65536);

    private static void WriteTag(byte[] b, int at, string tag)
    {
        for (int i = 0; i < 4; i++) b[at + i] = (byte)tag[i];
    }

    private static void Be32(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
