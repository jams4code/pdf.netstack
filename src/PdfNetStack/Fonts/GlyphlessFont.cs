using System.Runtime.CompilerServices;
using System.Text;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;

namespace PdfNetStack.Fonts;

/// <summary>
/// A font for invisible text layers, as OCR tools write them: every character code draws one
/// empty glyph half an em wide, so any script fits without shaping, and the codes are UTF-16 code
/// units, so text copies back as written. Type0 with Identity-H (ISO 32000-1, 9.7), embedded as
/// a TrueType program of two empty glyphs; one copy per document.
/// </summary>
internal static class GlyphlessFont
{
    public const string Name = "GlyphLessFont";

    /// <summary>The advance of every glyph, in thousandths of the font size.</summary>
    public const int Advance = 500;

    private static readonly ConditionalWeakTable<PdfDocument, PdfReference> References = new();
    private static readonly Lazy<byte[]> Program = new(BuildProgram);

    /// <summary>The font in <paramref name="document"/>, added on first use.</summary>
    public static PdfReference For(PdfDocument document) => References.GetValue(document, Create);

    /// <summary>The TrueType program, for the tests.</summary>
    internal static byte[] FontProgram => Program.Value;

    /// <summary>A code for each UTF-16 code unit; outside the BMP, where a lone surrogate maps to nothing, U+FFFD.</summary>
    public static byte[] Encode(string text, out bool lossy)
    {
        var codes = new List<byte>(text.Length * 2);
        lossy = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c)) continue;
            if (char.IsSurrogate(c))
            {
                lossy = true;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                c = '�';
            }

            codes.Add((byte)(c >> 8));
            codes.Add((byte)c);
        }

        return [.. codes];
    }

    private static PdfReference Create(PdfDocument doc)
    {
        var program = new PdfDictionary { ["Length1"] = PdfNumber.FromInteger(Program.Value.Length) };
        var descriptor = new PdfDictionary
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName(Name),
            ["Flags"] = PdfNumber.FromInteger(5), // fixed pitch, symbolic
            ["FontBBox"] = PdfArray.Of(0, 0, Advance, 1000),
            ["ItalicAngle"] = PdfNumber.FromInteger(0),
            ["Ascent"] = PdfNumber.FromInteger(1000),
            ["Descent"] = PdfNumber.FromInteger(0),
            ["CapHeight"] = PdfNumber.FromInteger(1000),
            ["StemV"] = PdfNumber.FromInteger(80),
            ["FontFile2"] = doc.AddObject(PdfStream.FromData(Program.Value, program)),
        };

        // Every CID draws glyph 1 (ISO 32000-1, 9.7.4.2): two bytes per CID, big endian.
        var map = new byte[2 * 65536];
        for (int cid = 0; cid < 65536; cid++) map[(2 * cid) + 1] = 1;

        var cidFont = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("CIDFontType2"),
            ["BaseFont"] = new PdfName(Name),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = PdfString.FromText("Adobe"),
                ["Ordering"] = PdfString.FromText("Identity"),
                ["Supplement"] = PdfNumber.FromInteger(0),
            },
            ["FontDescriptor"] = doc.AddObject(descriptor),
            ["DW"] = PdfNumber.FromInteger(Advance),
            ["CIDToGIDMap"] = doc.AddObject(PdfStream.FromData(map)),
        };

        var font = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
            ["BaseFont"] = new PdfName(Name),
            ["Encoding"] = new PdfName("Identity-H"),
            ["DescendantFonts"] = PdfArray.Of(doc.AddObject(cidFont)),
            ["ToUnicode"] = doc.AddObject(PdfStream.FromData(Encoding.ASCII.GetBytes(ToUnicode()))),
        };
        return doc.AddObject(font);
    }

    /// <summary>
    /// Each code to the same UTF-16 code unit (ISO 32000-1, 9.10.3). A range may only vary its
    /// last byte, so there is one per high byte; surrogates are left out, never valid alone.
    /// </summary>
    private static string ToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        List<int> blocks = [.. Enumerable.Range(0, 256).Where(high => high is < 0xD8 or > 0xDF)];

        // At most 100 entries per section (PDF Reference, 5.9.2, an implementation limit).
        for (int start = 0; start < blocks.Count; start += 100)
        {
            int count = Math.Min(100, blocks.Count - start);
            sb.Append(count).Append(" beginbfrange\n");
            for (int i = start; i < start + count; i++) sb.Append($"<{blocks[i]:X2}00> <{blocks[i]:X2}FF> <{blocks[i]:X2}00>\n");
            sb.Append("endbfrange\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>A TrueType program with .notdef and one other glyph, both empty and 500 units wide.</summary>
    private static byte[] BuildProgram()
    {
        var head = new FontWriter(54);
        head.U32(0x00010000);       // version
        head.U32(0x00010000);       // fontRevision
        head.U32(0);                // checkSumAdjustment, set by the builder
        head.U32(0x5F0F3CF5);       // magicNumber
        head.U16(0b1011);           // flags: baseline at 0, lsb at 0, integer scaling
        head.U16(1000);             // unitsPerEm
        for (int i = 0; i < 4; i++) head.U32(0); // created, modified
        head.U16(0);                // xMin
        head.U16(0);                // yMin
        head.U16(Advance);          // xMax
        head.U16(1000);             // yMax
        head.U16(0);                // macStyle
        head.U16(3);                // lowestRecPPEM
        head.U16(2);                // fontDirectionHint
        head.U16(0);                // indexToLocFormat: short
        head.U16(0);                // glyphDataFormat

        var hhea = new FontWriter(36);
        hhea.U32(0x00010000);
        hhea.U16(1000);             // ascender
        hhea.U16(0);                // descender
        hhea.U16(0);                // lineGap
        hhea.U16(Advance);          // advanceWidthMax
        hhea.U16(0);                // minLeftSideBearing
        hhea.U16(0);                // minRightSideBearing
        hhea.U16(0);                // xMaxExtent
        hhea.U16(1);                // caretSlopeRise
        hhea.U16(0);                // caretSlopeRun
        for (int i = 0; i < 5; i++) hhea.U16(0); // caretOffset, reserved
        hhea.U16(0);                // metricDataFormat
        hhea.U16(2);                // numberOfHMetrics

        var maxp = new FontWriter(32);
        maxp.U32(0x00010000);
        maxp.U16(2);                // numGlyphs
        for (int i = 0; i < 4; i++) maxp.U16(0); // points and contours, simple and composite
        maxp.U16(2);                // maxZones
        for (int i = 0; i < 8; i++) maxp.U16(0);

        var hmtx = new FontWriter(8);
        for (int i = 0; i < 2; i++)
        {
            hmtx.U16(Advance);
            hmtx.U16(0);
        }

        // Both glyphs are empty: every loca offset is 0. glyf keeps four bytes so it is never empty.
        var loca = new FontWriter(6);
        for (int i = 0; i < 3; i++) loca.U16(0);

        // A format 4 cmap with only the closing segment: no character maps, the PDF supplies CIDs.
        var cmap = new FontWriter(36);
        cmap.U16(0);                // version
        cmap.U16(1);                // numTables
        cmap.U16(3);                // platform: Windows
        cmap.U16(1);                // encoding: Unicode BMP
        cmap.U32(12);               // offset
        cmap.U16(4);                // format
        cmap.U16(24);               // length
        cmap.U16(0);                // language
        cmap.U16(2);                // segCountX2
        cmap.U16(2);                // searchRange
        cmap.U16(0);                // entrySelector
        cmap.U16(0);                // rangeShift
        cmap.U16(0xFFFF);           // endCode
        cmap.U16(0);                // reservedPad
        cmap.U16(0xFFFF);           // startCode
        cmap.U16(1);                // idDelta
        cmap.U16(0);                // idRangeOffset

        var post = new FontWriter(32);
        post.U32(0x00030000);       // format 3: no glyph names
        post.U32(0);                // italicAngle
        post.U16(-100);             // underlinePosition
        post.U16(50);               // underlineThickness
        post.U32(1);                // isFixedPitch
        for (int i = 0; i < 4; i++) post.U32(0);

        var os2 = new FontWriter(96);
        os2.U16(4);                 // version
        os2.U16(Advance);           // xAvgCharWidth
        os2.U16(400);               // usWeightClass
        os2.U16(5);                 // usWidthClass
        os2.U16(0);                 // fsType: installable, no embedding restrictions
        for (int i = 0; i < 10; i++) os2.U16(0); // sub and superscript metrics, strikeout
        os2.U16(0);                 // sFamilyClass
        for (int i = 0; i < 10; i++) os2.U8(0);  // panose
        for (int i = 0; i < 4; i++) os2.U32(0);  // ulUnicodeRange
        foreach (char c in "NONE") os2.U8(c);    // achVendID
        os2.U16(0x40);              // fsSelection: regular
        os2.U16(0);                 // usFirstCharIndex
        os2.U16(0xFFFF);            // usLastCharIndex
        os2.U16(1000);              // sTypoAscender
        os2.U16(0);                 // sTypoDescender
        os2.U16(0);                 // sTypoLineGap
        os2.U16(1000);              // usWinAscent
        os2.U16(0);                 // usWinDescent
        os2.U32(0);                 // ulCodePageRange1
        os2.U32(0);                 // ulCodePageRange2
        os2.U16(500);               // sxHeight
        os2.U16(1000);              // sCapHeight
        os2.U16(0);                 // usDefaultChar
        os2.U16(32);                // usBreakChar
        os2.U16(0);                 // usMaxContext

        return SfntBuilder.Build(0x00010000,
        [
            ("head", 0, head.ToArray()),
            ("hhea", 0, hhea.ToArray()),
            ("maxp", 0, maxp.ToArray()),
            ("hmtx", 0, hmtx.ToArray()),
            ("loca", 0, loca.ToArray()),
            ("glyf", 0, new byte[4]),
            ("cmap", 0, cmap.ToArray()),
            ("post", 0, post.ToArray()),
            ("OS/2", 0, os2.ToArray()),
            ("name", 0, NameTable()),
        ]);
    }

    /// <summary>Family, style, full and PostScript names (nameIDs 1, 2, 4, 6), Windows Unicode.</summary>
    private static byte[] NameTable()
    {
        (int Id, string Value)[] names = [(1, Name), (2, "Regular"), (4, Name), (6, Name)];
        var w = new FontWriter(128);
        w.U16(0);                   // format
        w.U16(names.Length);
        w.U16(6 + (12 * names.Length)); // storage offset
        int offset = 0;
        foreach ((int id, string value) in names)
        {
            w.U16(3);
            w.U16(1);
            w.U16(0x409);
            w.U16(id);
            w.U16(value.Length * 2);
            w.U16(offset);
            offset += value.Length * 2;
        }

        foreach ((_, string value) in names) w.Bytes(Encoding.BigEndianUnicode.GetBytes(value));
        return w.ToArray();
    }
}
