using System.Globalization;
using System.Security;
using System.Text;
using PdfNetStack.Objects;

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
}

/// <summary>
/// Prepares a document for PDF/A-2 or PDF/A-3 output: checks what cannot be repaired, then adds
/// an sRGB output intent and XMP metadata that matches the document information, and fixes
/// annotation flags and embedded file entries.
/// </summary>
internal static class PdfArchive
{
    private static readonly Lazy<byte[]> Srgb = new(SrgbProfile.Create);

    public static void Prepare(PdfDocument doc, PdfConformance conformance)
    {
        int part = conformance is PdfConformance.PdfA2B or PdfConformance.PdfA2U ? 2 : 3;
        bool unicode = conformance is PdfConformance.PdfA2U or PdfConformance.PdfA3U;
        var problems = new List<string>();
        if (doc.Security.Target is not null) problems.Add("PDF/A files cannot be encrypted.");
        if (string.CompareOrdinal(doc.Version, "1.7") > 0) problems.Add($"PDF/A-{part} is based on PDF 1.7; this document is PDF {doc.Version}.");
        if (doc.UsesDeviceCmyk) problems.Add("DeviceCMYK colors or images are used; PDF/A with an sRGB output intent needs RGB, gray, or ICC based colors.");
        CheckFonts(doc, problems, unicode);
        CheckScripts(doc, problems);
        CheckAnnotations(doc, problems);
        PrepareAttachments(doc, part, problems);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The document cannot be saved as PDF/A-" + part + (unicode ? "u" : "b") + ":\n- " + string.Join("\n- ", problems.Distinct()));
        }

        AddOutputIntent(doc);
        WriteMetadata(doc, part, unicode ? "U" : "B");
    }

    private static void CheckFonts(PdfDocument doc, List<string> problems, bool unicode)
    {
        foreach (PdfDictionary font in Fonts(doc))
        {
            string name = font.GetName("BaseFont") ?? "(unnamed font)";
            string subtype = font.GetName("Subtype") ?? "";
            if (subtype == "Type3") continue;
            PdfDictionary? descriptor = doc.Resolve(font["FontDescriptor"]) as PdfDictionary;
            if (subtype == "Type0" && doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } d && doc.Resolve(d[0]) is PdfDictionary cid)
            {
                descriptor = doc.Resolve(cid["FontDescriptor"]) as PdfDictionary;
            }

            bool embedded = descriptor is not null && (descriptor["FontFile"] ?? descriptor["FontFile2"] ?? descriptor["FontFile3"]) is not null;
            if (!embedded) problems.Add($"Font {name} is not embedded. PDF/A embeds every font: draw with a PdfOpenTypeFont instead of the standard fonts.");
            if (unicode && subtype == "Type0" && font["ToUnicode"] is null) problems.Add($"Font {name} has no ToUnicode map.");
        }
    }

    /// <summary>Every font dictionary used by pages, forms, patterns and annotation appearances.</summary>
    private static IEnumerable<PdfDictionary> Fonts(PdfDocument doc)
    {
        var seen = new HashSet<PdfDictionary>();
        var fonts = new List<PdfDictionary>();
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
                        fonts.Add(font);
                        if (doc.Resolve(font["Resources"]) is PdfDictionary t3) pending.Push(t3);
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

        return fonts;

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

    private static void CheckScripts(PdfDocument doc, List<string> problems)
    {
        if (doc.Resolve(doc.Catalog["Names"]) is PdfDictionary names && names["JavaScript"] is not null) problems.Add("Document level JavaScript is not allowed.");
        if (doc.Catalog["AA"] is not null) problems.Add("Document additional actions are not allowed.");
        if (doc.Resolve(doc.Catalog["OpenAction"]) is PdfDictionary open && open.GetName("S") is "JavaScript" or "Launch") problems.Add("The open action runs a script or launches a program.");
    }

    /// <summary>Annotations must print, must not hide, and must carry an appearance (links and popups excepted).</summary>
    private static void CheckAnnotations(PdfDocument doc, List<string> problems)
    {
        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is not PdfDictionary annot) continue;
                string subtype = annot.GetName("Subtype") ?? "";
                if (subtype is "Movie" or "Sound" or "Screen" or "3D" or "RichMedia")
                {
                    problems.Add($"{subtype} annotations are not allowed.");
                    continue;
                }

                if (subtype == "Popup") continue;
                long flags = doc.Resolve(annot["F"]) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
                annot.Set("F", PdfNumber.FromInteger((flags | 4) & ~(1L | 2 | 32 | 256)));
                PdfRectangle r = PdfRectangle.From(annot["Rect"], doc.Resolve) ?? default;
                if (subtype != "Link" && r.Width != 0 && r.Height != 0 && annot["AP"] is null) problems.Add($"A {subtype} annotation on page {page.Index + 1} has no appearance.");
            }
        }
    }

    /// <summary>
    /// PDF/A-2 forbids embedded files other than PDF/A; PDF/A-3 allows any, each declared as an
    /// associated file with its relationship, MIME type and modification date.
    /// </summary>
    private static void PrepareAttachments(PdfDocument doc, int part, List<string> problems)
    {
        IReadOnlyList<PdfAttachment> attachments = doc.Attachments;
        if (attachments.Count == 0) return;
        if (part == 2)
        {
            problems.Add("PDF/A-2 cannot embed files; use PDF/A-3 for attachments such as invoice XML.");
            return;
        }

        var af = doc.Resolve(doc.Catalog["AF"]) as PdfArray ?? new PdfArray();
        foreach (PdfAttachment attachment in attachments)
        {
            PdfDictionary spec = attachment.Dictionary;
            if (spec["AFRelationship"] is null) spec.Set("AFRelationship", new PdfName("Unspecified"));
            if (doc.Resolve(spec["EF"]) is PdfDictionary ef)
            {
                foreach (KeyValuePair<string, PdfObject> e in ef)
                {
                    if (doc.Resolve(e.Value) is not PdfStream file) continue;
                    if (file.Dictionary["Subtype"] is null) file.Dictionary.Set("Subtype", new PdfName("application/octet-stream"));
                    var parameters = doc.Resolve(file.Dictionary["Params"]) as PdfDictionary ?? new PdfDictionary();
                    if (parameters["ModDate"] is null) parameters.Set("ModDate", new PdfString(Encoding.ASCII.GetBytes(Documents.PdfText.FormatDate(DateTimeOffset.UtcNow)).AsSpan()));
                    file.Dictionary.Set("Params", parameters);
                }
            }

            if (!af.Any(x => doc.Resolve(x) == spec)) af.Add(doc.AddObject(spec));
        }

        doc.Catalog.Set("AF", af);
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

    /// <summary>XMP with the PDF/A identification and the same values as the information dictionary.</summary>
    private static void WriteMetadata(PdfDocument doc, int part, string conformance)
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
        x.Append("<pdfaid:part>").Append(part).Append("</pdfaid:part>\n<pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n");
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
        x.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n");
        x.Append(' ', 2000).Append("\n<?xpacket end=\"w\"?>");

        // Metadata stays uncompressed so archival tools can read it without decoding.
        var stream = PdfStream.FromData(Encoding.UTF8.GetBytes(x.ToString()), compress: false);
        stream.Dictionary.Set("Type", new PdfName("Metadata"));
        stream.Dictionary.Set("Subtype", new PdfName("XML"));
        doc.Catalog.Set("Metadata", doc.AddObject(stream));
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
