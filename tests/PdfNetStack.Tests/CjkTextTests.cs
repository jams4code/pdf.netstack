using PdfNetStack.Text.Extraction;

namespace PdfNetStack.Tests;

/// <summary>
/// Text extraction of composite fonts through Adobe's predefined CJK CMaps and the CID to Unicode
/// tables of their character collections. The files are written by hand; the CIDs are those of
/// Adobe's published CMaps (Adobe-Japan1: 843 is あ, 7887 the vertical 、, 264 the half width A).
/// </summary>
[TestClass]
public sealed class CjkTextTests
{
    private const double Tolerance = 1e-6;

    /// <summary>A one page file drawing <paramref name="content"/> with font F1 (object 10); further objects start at 11.</summary>
    private static PdfPage Page(string content, string font, params (int Number, string Body)[] objects)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 10 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", Compat.Latin1.GetBytes(content));
        b.Set(10, font);
        foreach ((int number, string body) in objects)
        {
            if (body.StartsWith("stream:"))
            {
                int bar = body.IndexOf('|');
                b.SetStream(number, body[7..bar], Compat.Latin1.GetBytes(body[(bar + 1)..]));
            }
            else
            {
                b.Set(number, body);
            }
        }

        return PdfDocument.Open(b.Build()).Pages[0];
    }

    /// <summary>A Type 0 font with no ToUnicode map.</summary>
    private static string Type0(string encoding, string cidFont) =>
        $"<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding {encoding}/DescendantFonts[{cidFont}]>>";

    private static string CidFont(string ordering, string extra = "") =>
        $"<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement 0>>{extra}>>";

    private static string Program(string body) =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" + body + "\nendcmap CMapName currentdict /CMap defineresource pop end end";

    private static double[] Starts(PdfPage page) => [.. page.GetLetters().Select(l => l.BaselineStart.X)];

    [TestMethod]
    public void ShiftJisCodesSplitAndReadThroughTheCMap()
    {
        // One byte A (CID 264), two byte あ (843), and the yen sign that 0x5C stands for (291).
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td <41 82A0 5C> Tj ET", Type0("/90ms-RKSJ-H", CidFont("Japan1", "/W[264 [400] 291 [500] 843 [900]]")));
        Assert.AreEqual("Aあ¥", page.ExtractText());

        // Widths come from the CIDs the CMap gives, not from the codes.
        CollectionAssert.AreEqual(new[] { 72, 76, 85.0 }, Starts(page));
    }

    [TestMethod]
    [DataRow("/UniJIS-UTF16-H", "<3042 D842DF9F 0041>")]
    [DataRow("/UniJIS-UTF8-H", "<E38182 F0A0AE9F 41>")]
    [DataRow("/UniJIS-UTF32-H", "<00003042 00020B9F 00000041>")]
    [DataRow("/UniJIS2004-UTF16-H", "<3042 D842DF9F 0041>")]
    public void UnicodeCMapsGiveCidsForWidths(string encoding, string codes)
    {
        PdfPage page = Page($"BT /F1 10 Tf 72 700 Td {codes} Tj ET", Type0(encoding, CidFont("Japan1", "/W[34 [300] 843 [900] 13803 [800]]")));
        Assert.AreEqual("あ\U00020B9FA", page.ExtractText());
        CollectionAssert.AreEqual(new[] { 72, 81, 89.0 }, Starts(page));
    }

    [TestMethod]
    public void UcsTwoCMapsKeepTheirOwnMappings()
    {
        // UniJIS-UCS2-H maps U+007C to CID 93; its UTF16 form maps it to CID 99.
        string cidFont = CidFont("Japan1", "/W[93 [100] 99 [200]]");
        PdfPage ucs2 = Page("BT /F1 10 Tf 72 700 Td <007C 0041> Tj ET", Type0("/UniJIS-UCS2-H", cidFont));
        PdfPage utf16 = Page("BT /F1 10 Tf 72 700 Td <007C 0041> Tj ET", Type0("/UniJIS-UTF16-H", cidFont));
        Assert.AreEqual(73, Starts(ucs2)[1], Tolerance);
        Assert.AreEqual(74, Starts(utf16)[1], Tolerance);
        Assert.AreEqual("|A", ucs2.ExtractText(), "the text of a Unicode CMap is its code");
    }

    [TestMethod]
    public void VerticalCMapsWriteDownWithTheirOwnGlyphs()
    {
        // In 90ms-RKSJ-V the ideographic comma is the vertical form, CID 7887; あ is the same as in H.
        string font = Type0("/90ms-RKSJ-V", CidFont("Japan1", "/W2[7887 [-600 500 880]]"));
        PdfPage page = Page("BT /F1 10 Tf 300 700 Td <8141 82A0 82A0> Tj ET", font);
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.AreEqual("、ああ", page.ExtractText(), "the vertical comma reads as the comma");
        Assert.AreEqual(-90, l[0].Angle, 1e-9);
        Assert.AreEqual(694, l[1].BaselineStart.Y, Tolerance, "W2 gives CID 7887 an advance of -600");
        Assert.AreEqual(684, l[2].BaselineStart.Y, Tolerance);
        Assert.AreEqual(1, CMap.Predefined("UniJIS-UTF16-V")!.WMode);
        Assert.AreEqual(0, CMap.Predefined("UniJIS-UTF16-H")!.WMode);
    }

    [TestMethod]
    public void VerticalColumnsReadRightToLeftAndTopToBottom()
    {
        // Three columns of four characters, drawn left column first; each column is read from the top.
        PdfPage page = Page(
            "BT /F1 20 Tf 100 700 Td <0001 0002 0003 0004> Tj ET BT /F1 20 Tf 130 700 Td <0005 0006 0007 0008> Tj ET " +
            "BT /F1 20 Tf 160 700 Td <0009 000A 000B 000C> Tj ET",
            "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding/Identity-V/DescendantFonts[<</Subtype/CIDFontType0/BaseFont/Test>>]/ToUnicode 11 0 R>>",
            (11, "stream:|" + Program("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0001> <000C> <4E00> endbfrange")));
        Assert.AreEqual("丈三上下\n丄丅丆万\n一丁丂七", page.ExtractText());
    }

    [TestMethod]
    [DataRow("/GBK-EUC-H", "GB1", "<C4E3 BAC3>", "你好")]
    [DataRow("/GBK-EUC-V", "GB1", "<C4E3 BAC3>", "你好")]
    [DataRow("/GB-EUC-H", "GB1", "<C4E3 BAC3>", "你好")]
    [DataRow("/B5pc-H", "CNS1", "<A4A4 A4E5>", "中文")]
    [DataRow("/ETenms-B5-H", "CNS1", "<A4A4 A4E5>", "中文")]
    [DataRow("/KSCms-UHC-H", "Korea1", "<C7D1 B1DB>", "한글")]
    [DataRow("/KSC-EUC-H", "Korea1", "<C7D1 B1DB>", "한글")]
    [DataRow("/EUC-H", "Japan1", "<A4A2>", "あ")]
    [DataRow("/H", "Japan1", "<2422>", "あ")]
    [DataRow("/Identity-H", "KR", "<0B13>", "한")]
    public void EachCollectionReadsWithoutToUnicode(string encoding, string ordering, string codes, string text)
    {
        // The CIDFont states no collection here: the predefined CMap names it (except for Identity-H).
        string cidFont = encoding == "/Identity-H" ? CidFont(ordering) : "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test>>";
        Assert.AreEqual(text, Page($"BT /F1 10 Tf 72 700 Td {codes} Tj ET", Type0(encoding, cidFont)).ExtractText());
    }

    [TestMethod]
    public void IdentityCidsOfACollectionTakeItsText()
    {
        // あ, a space, the vertical comma, a slashed zero (0 and a variation selector), an unmapped CID.
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td <034B 0001 1ECF 00E6 FFFF> Tj ET", Type0("/Identity-H", CidFont("Japan1")));
        CollectionAssert.AreEqual(new[] { "あ", " ", "、", "0︀", "�" }, page.GetLetters().Select(l => l.Text).ToArray());

        // Another registry's collection is not Adobe's, whatever its name.
        string other = "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/CIDSystemInfo<</Registry(Other)/Ordering(Japan1)/Supplement 0>>>>";
        Assert.AreEqual("�", Page("BT /F1 10 Tf <034B> Tj ET", Type0("/Identity-H", other)).ExtractText());
    }

    [TestMethod]
    public void ToUnicodeWinsAndTheCollectionFillsItsGaps()
    {
        string font = "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding/Identity-H/DescendantFonts[" + CidFont("Japan1") + "]/ToUnicode 11 0 R>>";
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <034B 034C> Tj ET",
            font,
            (11, "stream:|" + Program("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar <034B> <0058> endbfchar")));
        Assert.AreEqual("Xぃ", page.ExtractText());
    }

    [TestMethod]
    public void EmbeddedCMapsInheritAPredefinedCollection()
    {
        // The usecmap operator and the /UseCMap entry: code 82A0 overridden to CID 844 (ぃ), A from the parent.
        foreach (string use in new[] { "/90ms-RKSJ-H usecmap", "" })
        {
            string dict = use.Length == 0 ? "/Type/CMap/UseCMap/90ms-RKSJ-H" : "/Type/CMap";
            PdfPage page = Page(
                "BT /F1 10 Tf 72 700 Td <41 82A0> Tj ET",
                Type0("11 0 R", "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/W[264 [400] 844 [700]]>>"),
                (11, $"stream:{dict}|" + Program(use + " 1 begincidchar <82A0> 844 endcidchar")));
            Assert.AreEqual("Aぃ", page.ExtractText());
            CollectionAssert.AreEqual(new[] { 72, 76.0 }, Starts(page));
        }
    }

    [TestMethod]
    public void EmbeddedCMapWritingModeWinsOverItsParent()
    {
        CMap cmap = CMap.Parse(Compat.Latin1.GetBytes(Program("/WMode 1 def /UniJIS-UTF16-H usecmap")), (name, _) => CMap.Predefined(name));
        Assert.AreEqual(1, cmap.WMode);
        Assert.AreEqual("Japan1", cmap.Ordering);
        Assert.AreEqual(843, cmap.Cid(0x3042));
    }

    /// <summary>The predefined CMaps of ISO 32000-1, Table 118, other than Identity-H and Identity-V.</summary>
    private static readonly (string Ordering, string[] Names)[] Table118 =
    [
        ("GB1", ["GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V",
            "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V"]),
        ("CNS1", ["B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V",
            "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H", "UniCNS-UTF16-V"]),
        ("Japan1", ["83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V",
            "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V", "UniJIS-UTF16-H", "UniJIS-UTF16-V"]),
        ("Korea1", ["KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V", "KSCpc-EUC-H",
            "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V"]),
    ];

    [TestMethod]
    public void EveryStandardCMapLoads()
    {
        foreach ((string ordering, string[] names) in Table118)
        {
            foreach (string name in names)
            {
                CMap? cmap = CMap.Predefined(name);
                Assert.IsNotNull(cmap, name);
                Assert.IsTrue(cmap.MapsCids && cmap.HasCodespace, name);
                Assert.AreEqual(ordering, cmap.Ordering, name);
                Assert.AreEqual(name is "V" || name.EndsWith("-V") ? 1 : 0, cmap.WMode, name);
                Assert.AreSame(cmap, CMap.Predefined(name), "loaded once and shared");
            }
        }

        Assert.IsNull(CMap.Predefined("No-Such-H"));
        Assert.IsNull(CMap.Predefined("Adobe-Japan1-UCS2"), "a CID to Unicode CMap is no encoding");
        CMap unknownUnicode = CMap.Predefined("UniHojo-UTF16-H")!;
        Assert.IsFalse(unknownUnicode.MapsCids, "a Unicode CMap without data still splits codes, with no CIDs");
        Assert.IsNull(PredefinedCMaps.ForOrdering("Identity"));
    }

    [TestMethod]
    public void LoadingIsSafeFromManyThreads()
    {
        string[] names = [.. Table118.SelectMany(t => t.Names)];
        var results = new int?[names.Length * 4];
        Parallel.For(0, results.Length, i =>
        {
            string name = names[i % names.Length];
            CMap cmap = CMap.Predefined(name)!;
            results[i] = cmap.Cid(0x41);
            _ = PredefinedCMaps.ForOrdering(cmap.Ordering)!.Text(843);
        });
        for (int i = 0; i < results.Length; i++) Assert.AreEqual(results[i % names.Length], results[i]);
        Assert.AreEqual("あ", PredefinedCMaps.ForOrdering("Japan1")!.Text(843));
    }

    [TestMethod]
    public void DamagedCodesStillAdvance()
    {
        string font = Type0("/90ms-RKSJ-H", CidFont("Japan1"));

        // A lead byte with nothing after it, a code outside every range (0xFD), a control code in a notdef range.
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td <82> Tj <FD 41> Tj <01> Tj ET", font);
        Assert.HasCount(4, page.GetLetters());
        Assert.AreEqual("A", page.GetLetters()[2].Text);

        // Byte sequences that are no UTF-8 or UTF-16 character map to no CID and no text.
        PdfPage utf8 = Page("BT /F1 10 Tf 72 700 Td <E341 C0 F0808080 FF> Tj ET", Type0("/UniJIS-UTF8-H", CidFont("Japan1")));
        _ = utf8.ExtractText();
        PdfPage utf16 = Page("BT /F1 10 Tf 72 700 Td <D800 DBFFDFFF DC00> Tj ET", Type0("/UniJIS-UTF16-H", CidFont("Japan1")));
        _ = utf16.ExtractText();
        CMap cmap = CMap.Predefined("UniJIS-UTF8-H")!;
        Assert.IsNull(cmap.Cid(0xE341));
        Assert.IsNull(cmap.Cid(0xFFFFFF));
        Assert.AreEqual(843, cmap.Cid(0xE38182));
    }
}
