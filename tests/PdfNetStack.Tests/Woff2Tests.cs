using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Html;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Tests;

/// <summary>
/// WOFF2 fonts against the synthetic fixtures of tools/make_font_fixtures.py: each WOFF2 file must
/// rebuild into a font equivalent to the TrueType file it was made from.
/// </summary>
[TestClass]
public sealed class Woff2Tests
{
    private const int MaxSize = 128 << 20;
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };
    private static readonly string[] Woff2Fixtures = ["Test-Regular.woff2", "Test-Outlines.woff2", "Test-Outlines-Untransformed.woff2", "Test.woff2"];

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static byte[] Fixture(string name) => File.ReadAllBytes(FixturePath(name));

    private static OpenTypeFile Parse(string name, int index = 0) => OpenTypeFile.Parse(Fixture(name), index);

    private static PdfDocument Draw(PdfFont font, string text)
    {
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString(text, font, 12, PdfColor.Black, 72, 100);
        return doc;
    }

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(Plain);
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    /// <summary>The fonts of the first page: base font name and decoded font program.</summary>
    private static List<(string Name, byte[]? Program)> PageFonts(PdfDocument doc)
    {
        var result = new List<(string, byte[]?)>();
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        if (doc.Resolve(resources["Font"]) is not PdfDictionary fonts) return result;
        foreach (KeyValuePair<string, PdfObject> entry in fonts)
        {
            var font = (PdfDictionary)doc.Resolve(entry.Value)!;
            string name = ((PdfName)font["BaseFont"]!).Value;
            byte[]? program = null;
            if (doc.Resolve(font["DescendantFonts"]) is PdfArray descendants)
            {
                var cid = (PdfDictionary)doc.Resolve(descendants[0])!;
                var descriptor = (PdfDictionary)doc.Resolve(cid["FontDescriptor"])!;
                if (doc.Resolve(descriptor["FontFile2"]) is PdfStream stream) program = doc.GetStreamData(stream);
            }

            result.Add((name, program));
        }

        return result;
    }

    /// <summary>
    /// A glyph as text, independent of how its glyf record encodes it: the bounding box, then for a
    /// simple glyph each point (absolute, on or off curve), the overlap flag and the instructions,
    /// and for a composite glyph its component records and instructions.
    /// </summary>
    private static string Outline(OpenTypeFile font, int glyph)
    {
        (int start, int end) = font.GlyphRange(glyph);
        ReadOnlySpan<byte> g = font.Table("glyf").Slice(start, end - start);
        if (g.Length == 0) return "empty";
        int contours = I16(g, 0);
        var text = new StringBuilder($"{contours} box {I16(g, 2)} {I16(g, 4)} {I16(g, 6)} {I16(g, 8)};");
        int p = 10;
        if (contours < 0)
        {
            int flags;
            bool instructions = false;
            do
            {
                flags = U16(g, p);
                int size = 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
                text.Append(" component ").Append(Compat.ToHex(g.Slice(p, size).ToArray()));
                instructions |= (flags & 0x100) != 0;
                p += size;
            }
            while ((flags & 0x20) != 0);
            if (instructions) text.Append(" code ").Append(Compat.ToHex(g.Slice(p + 2, U16(g, p)).ToArray()));
            return text.ToString();
        }

        var ends = new int[contours];
        for (int c = 0; c < contours; c++) ends[c] = U16(g, p + 2 * c);
        p += 2 * contours;
        int codeLength = U16(g, p);
        string code = Compat.ToHex(g.Slice(p + 2, codeLength).ToArray());
        p += 2 + codeLength;
        int points = ends[contours - 1] + 1;
        var pointFlags = new byte[points];
        for (int i = 0; i < points;)
        {
            byte flag = g[p++];
            pointFlags[i++] = flag;
            if ((flag & 8) == 0) continue;
            for (int repeat = g[p++]; repeat > 0; repeat--) pointFlags[i++] = flag;
        }

        var xs = new int[points];
        var ys = new int[points];
        foreach ((int[] values, int shortFlag, int sameFlag) in new[] { (xs, 2, 0x10), (ys, 4, 0x20) })
        {
            int value = 0;
            for (int i = 0; i < points; i++)
            {
                int flag = pointFlags[i];
                if ((flag & shortFlag) != 0) value += (flag & sameFlag) != 0 ? g[p++] : -g[p++];
                else if ((flag & sameFlag) == 0) value += I16(g, (p += 2) - 2);
                values[i] = (short)value;
            }
        }

        text.Append($" ends {string.Join(",", ends)} overlap {(pointFlags[0] & 0x40) != 0} code {code};");
        for (int i = 0; i < points; i++) text.Append($" {((pointFlags[i] & 1) != 0 ? "on" : "off")} {xs[i]} {ys[i]}");
        return text.ToString();
    }

    /// <summary>The fonts match table by table: glyf glyph by glyph, head apart from the fields WOFF2 changes, the rest byte for byte.</summary>
    private static void AssertEquivalent(OpenTypeFile expected, OpenTypeFile actual, string label)
    {
        Assert.AreEqual(expected.GlyphCount, actual.GlyphCount, label);
        Assert.AreEqual(expected.LongLoca, actual.LongLoca, label);
        string[] tags = ["OS/2", "cmap", "hhea", "hmtx", "maxp", "name", "post", "GPOS"];
        foreach (string tag in tags) CollectionAssert.AreEqual(expected.Table(tag).ToArray(), actual.Table(tag).ToArray(), $"{label} {tag}");

        // head: checkSumAdjustment is recomputed, and encoders set flags bit 11 (font was transformed).
        byte[] head1 = expected.Table("head").ToArray(), head2 = actual.Table("head").ToArray();
        foreach (byte[] head in new[] { head1, head2 })
        {
            Array.Clear(head, 8, 4);
            head[16] &= 0xF7;
        }

        CollectionAssert.AreEqual(head1, head2, $"{label} head");
        for (int g = 0; g < expected.GlyphCount; g++)
        {
            Assert.AreEqual(Outline(expected, g), Outline(actual, g), $"{label} glyph {g}");
            Assert.AreEqual(expected.Advance(g), actual.Advance(g), $"{label} advance {g}");
        }

        foreach (int c in Enumerable.Range(0, 0x2000).Append(0x1F600))
        {
            Assert.AreEqual(expected.TryGetGlyph(c, out int a) ? a : -1, actual.TryGetGlyph(c, out int b) ? b : -1, $"{label} U+{c:X4}");
        }
    }

    /// <summary>Checks the table checksums and checkSumAdjustment of the font at <paramref name="offset"/>.</summary>
    private static void AssertChecksums(byte[] sfnt, int offset)
    {
        int count = U16(sfnt, offset + 4);
        uint total = Checksum(sfnt.AsSpan(offset, 12 + 16 * count));
        for (int i = 0; i < count; i++)
        {
            int record = offset + 12 + 16 * i;
            string tag = Tag(sfnt, record);
            byte[] table = sfnt.AsSpan(Offset32(sfnt, record + 8), Offset32(sfnt, record + 12)).ToArray();
            uint adjustment = 0;
            if (tag == "head")
            {
                adjustment = U32(table, 8);
                Array.Clear(table, 8, 4);
            }

            Assert.AreEqual(Checksum(table), U32(sfnt, record + 4), tag);
            total += Checksum(table) + adjustment;
        }

        Assert.AreEqual(0xB1B0AFBAu, total);
    }

    [TestMethod]
    public void RegularLoadsLikeTheTtf()
    {
        PdfOpenTypeFont ttf = PdfOpenTypeFont.Load(Fixture("Test-Regular.ttf")), woff2 = PdfOpenTypeFont.Load(Fixture("Test-Regular.woff2"));
        Assert.AreEqual(ttf.Name, woff2.Name);
        Assert.AreEqual(ttf.FamilyName, woff2.FamilyName);
        Assert.AreEqual(ttf.GlyphCount, woff2.GlyphCount);
        Assert.AreEqual(ttf.Ascent, woff2.Ascent);
        Assert.AreEqual(ttf.CapHeight, woff2.CapHeight);
        Assert.AreEqual(ttf.MeasureString("AV To", 12), woff2.MeasureString("AV To", 12));
        Assert.AreEqual(1, PdfOpenTypeFont.CountFonts(Fixture("Test-Regular.woff2")));
        AssertEquivalent(Parse("Test-Regular.ttf"), Parse("Test-Regular.woff2"), "Test-Regular.woff2");
    }

    [TestMethod]
    public void TransformedTablesRebuildLikeTheTtf()
    {
        // glyf with every triplet size, long contours, instructions, an overlap flag, a stored
        // bounding box and scaled composites; hmtx with omitted and stored side bearings.
        OpenTypeFile ttf = Parse("Test-Outlines.ttf");
        Assert.AreEqual(14, ttf.GlyphCount);
        Assert.AreEqual(12, ttf.NumberOfHMetrics);
        AssertEquivalent(ttf, Parse("Test-Outlines.woff2"), "Test-Outlines.woff2");
        AssertEquivalent(ttf, Parse("Test-Outlines-Untransformed.woff2"), "Test-Outlines-Untransformed.woff2");
        StringAssert.Contains(Outline(ttf, 5), "overlap True");
        StringAssert.Contains(Outline(ttf, 6), "box 50 -50 450 750");
    }

    [TestMethod]
    public void ChecksumsAreRecomputed()
    {
        foreach (string name in new[] { "Test-Regular.woff2", "Test-Outlines.woff2", "Test-Outlines-Untransformed.woff2" })
        {
            AssertChecksums(Woff2.Decode(Fixture(name), MaxSize), 0);
        }

        byte[] collection = Woff2.Decode(Fixture("Test.woff2"), MaxSize);
        Assert.AreEqual("ttcf", Tag(collection, 0));
        for (int i = 0; i < 2; i++) AssertChecksums(collection, Offset32(collection, 12 + 4 * i));
    }

    [TestMethod]
    public void CollectionLoadsEveryFont()
    {
        byte[] woff2 = Fixture("Test.woff2");
        Assert.AreEqual(2, PdfOpenTypeFont.CountFonts(woff2));
        for (int i = 0; i < 2; i++)
        {
            AssertEquivalent(Parse("Test.ttc", i), Parse("Test.woff2", i), $"Test.woff2 font {i}");
            PdfOpenTypeFont font = PdfOpenTypeFont.Load(woff2, i);
            Assert.AreEqual(i == 0 ? 400 : 700, font.Weight);
            Reopen(Draw(font, "AV"));
        }

        // The two fonts share their glyf and loca tables, as in the TTC.
        byte[] collection = Woff2.Decode(woff2, MaxSize);
        OpenTypeFile regular = OpenTypeFile.Parse(collection, 0), bold = OpenTypeFile.Parse(collection, 1);
        Assert.AreEqual(regular.TableOffset("glyf"), bold.TableOffset("glyf"));
        Assert.AreNotEqual(regular.TableOffset("head"), bold.TableOffset("head"));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOpenTypeFont.Load(woff2, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOpenTypeFont.Load(Fixture("Test-Regular.woff2"), 1));
    }

    [TestMethod]
    public void ManagedAndBuiltInBrotliAgree()
    {
        foreach (string name in Woff2Fixtures)
        {
            byte[] data = Fixture(name);
            CollectionAssert.AreEqual(Woff2.Decode(data, MaxSize), Woff2.Decode(data, MaxSize, managedBrotli: true, BrotliTests.Dictionary), name);
        }
    }

    [TestMethod]
    public void EmbedsASubsetThatReopens()
    {
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(Fixture("Test-Outlines.woff2"));
        Assert.IsTrue(font.CanRender("OZIVBÓŒ012"));
        PdfDocument doc = Reopen(Draw(font, "OZIVBÓŒ012"));
        (string name, byte[]? program) = PageFonts(doc).Single();
        StringAssert.EndsWith(name, "+PdfNetStackTestOutlines-Regular");
        OpenTypeFile subset = OpenTypeFile.ParseSubset(program!);
        Assert.IsLessThanOrEqualTo(font.GlyphCount, subset.GlyphCount);
        OpenTypeFile full = Parse("Test-Outlines.ttf");
        foreach (char c in "OZIVBÓŒ012")
        {
            Assert.IsTrue(full.TryGetGlyph(c, out int glyph));
            Assert.AreEqual(Outline(full, glyph), Outline(subset, glyph), c.ToString());
        }
    }

    [TestMethod]
    public void FontCollectionReadsWoff2Files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfnetstack-woff2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(FixturePath("Test.woff2"), Path.Combine(dir, "Test.woff2"));
            File.Copy(FixturePath("Test-Outlines.woff2"), Path.Combine(dir, "Test-Outlines.woff2"));
            var fonts = new PdfFontCollection();
            Assert.AreEqual(3, fonts.AddDirectory(dir));
            Assert.AreEqual(700, fonts.Find("PdfNetStack Test", 700)!.Weight);
            Assert.AreEqual(400, fonts.Find("PdfNetStack Test", 400)!.Weight);
            Assert.IsNotNull(fonts.Find("PdfNetStack Test Outlines"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void HtmlLoadsWoff2FontFaces()
    {
        // A data: URI after a source that does not load, and a file next to the document.
        string uri = "data:font/woff2;base64," + Convert.ToBase64String(Fixture("Test-Regular.woff2"));
        string html = $$"""
            <style>
            @font-face { font-family: Web; src: url(missing.woff2) format("woff2"), url({{uri}}) format("woff2"); }
            @font-face { font-family: Local; src: url(Test-Outlines.woff2) format("woff2"); }
            p { font-family: Web }
            h1 { font-family: Local }
            </style>
            <h1>OZ</h1><p>AV To</p>
            """;
        var options = new HtmlConverterOptions { UseSystemFonts = false, BaseUri = new Uri(FixturePath("index.html")) };
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, options));
        List<string> names = PageFonts(doc).Select(f => f.Name).ToList();
        Assert.IsTrue(names.Any(n => n.EndsWith("+PdfNetStackTest-Regular", StringComparison.Ordinal)), string.Join(", ", names));
        Assert.IsTrue(names.Any(n => n.EndsWith("+PdfNetStackTestOutlines-Regular", StringComparison.Ordinal)), string.Join(", ", names));
    }

    [TestMethod]
    public void TruncatedFilesAreRejected()
    {
        foreach (string name in Woff2Fixtures)
        {
            byte[] data = Fixture(name);
            (int offset, int length, _) = Woff2.Layout(data);
            for (int cut = 0; cut < offset + length; cut++)
            {
                byte[] truncated = data.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load(truncated), $"{name} cut to {cut}");
            }
        }
    }

    /// <summary>Loads, measures and draws a font, or expects a font error, and nothing else.</summary>
    private static void LoadsOrFailsCleanly(byte[] data, string label)
    {
        try
        {
            int count = PdfOpenTypeFont.CountFonts(data);
            for (int i = 0; i < count; i++)
            {
                PdfOpenTypeFont font = PdfOpenTypeFont.Load(data, i);
                string text = font.CanRender("AVTo OZIB") ? "AVTo OZIB" : "";
                font.MeasureString(text, 12);
                if (text.Length > 0 && font.AllowsEmbedding) Draw(font, text).ToArray(Plain);
            }
        }
        catch (FontFormatException)
        {
        }
        catch (Exception e)
        {
            Assert.Fail($"{label}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    [TestMethod]
    public void CorruptedFilesFailCleanly()
    {
        var random = new Random(20261029);
        foreach (string name in Woff2Fixtures)
        {
            byte[] original = Fixture(name);
            (int offset, _, _) = Woff2.Layout(original);
            for (int round = 0; round < 1000; round++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = 1 + random.Next(4);

                // Half the rounds damage the header and directories, which the decoder reads before Brotli.
                for (int k = 0; k < flips; k++) data[round % 2 == 0 ? random.Next(offset) : random.Next(data.Length)] = (byte)random.Next(256);
                LoadsOrFailsCleanly(data, $"{name} round {round}");
            }
        }
    }

    /// <summary>The table data block of a WOFF2 file, decompressed.</summary>
    private static byte[] Block(byte[] woff2)
    {
        (int offset, int length, int expanded) = Woff2.Layout(woff2);
        return new ManagedBrotli(woff2.AsSpan(offset, length).ToArray(), expanded, BrotliTests.Dictionary).Decompress();
    }

    /// <summary>A WOFF2 file with its table data block replaced, stored in uncompressed Brotli meta-blocks.</summary>
    private static byte[] WithBlock(byte[] woff2, byte[] block)
    {
        (int offset, _, _) = Woff2.Layout(woff2);
        byte[] compressed = BrotliTests.Stored(block);
        byte[] result = [.. woff2.AsSpan(0, offset).ToArray(), .. compressed];
        var w = new FontWriter();
        w.U32((uint)result.Length);
        w.ToArray().CopyTo(result, 8);
        w = new FontWriter();
        w.U32((uint)compressed.Length);
        w.ToArray().CopyTo(result, 20);
        return result;
    }

    /// <summary>Offset and length of each table in the data block, from the table directory (section 4.1).</summary>
    private static Dictionary<string, (int Offset, int Length)> Tables(byte[] woff2)
    {
        string[] known = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca"];
        var tables = new Dictionary<string, (int, int)>();
        int position = 48, offset = 0;
        int Base128()
        {
            int value = 0;
            while (true)
            {
                int b = woff2[position++];
                value = value << 7 | (b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }
        }

        for (int i = 0; i < U16(woff2, 12); i++)
        {
            int flags = woff2[position++];
            string tag = (flags & 0x3F) == 0x3F ? Tag(woff2, (position += 4) - 4) : (flags & 0x3F) < known.Length ? known[flags & 0x3F] : "other" + i;
            int version = flags >> 6;
            int length = Base128();
            if (tag is "glyf" or "loca" ? version != 3 : version != 0) length = Base128();
            tables[tag] = (offset, length);
            offset += length;
        }

        return tables;
    }

    [TestMethod]
    public void StoredBlocksDecodeLikeCompressedOnes()
    {
        foreach (string name in Woff2Fixtures)
        {
            byte[] data = Fixture(name);
            CollectionAssert.AreEqual(Woff2.Decode(data, MaxSize), Woff2.Decode(WithBlock(data, Block(data)), MaxSize), name);
        }
    }

    [TestMethod]
    public void CorruptedTableDataFailsCleanly()
    {
        // The Brotli layer is bypassed: damage goes straight into the transformed glyf, loca and
        // hmtx data, and the glyph reconstruction must catch it.
        var random = new Random(1029);
        foreach (string name in Woff2Fixtures)
        {
            byte[] original = Fixture(name);
            byte[] block = Block(original);
            (int glyf, int glyfLength) = Tables(original)["glyf"];
            for (int round = 0; round < 1500; round++)
            {
                byte[] damaged = (byte[])block.Clone();
                int flips = 1 + random.Next(6);
                for (int k = 0; k < flips; k++)
                {
                    int at = round % 3 == 0 ? random.Next(damaged.Length) : glyf + random.Next(round % 3 == 1 ? Math.Min(glyfLength, 40) : glyfLength);
                    damaged[at] = (byte)random.Next(256);
                }

                LoadsOrFailsCleanly(WithBlock(original, damaged), $"{name} round {round}");
            }
        }
    }

    private static void AssertRejected(byte[] data, string message)
    {
        StringAssert.Contains(Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load(data)).Message, message);
    }

    [TestMethod]
    public void InvalidTransformedDataIsRejected()
    {
        byte[] woff2 = Fixture("Test-Outlines.woff2");
        Dictionary<string, (int Offset, int Length)> tables = Tables(woff2);
        int glyf = tables["glyf"].Offset;
        byte[] Damage(Action<byte[]> change)
        {
            byte[] block = Block(woff2);
            change(block);
            return WithBlock(woff2, block);
        }

        // The bbox bitmap follows the nContour, nPoints, flag, glyph and composite streams.
        int Bitmap(byte[] block)
        {
            int at = glyf + 36;
            for (int i = 0; i < 5; i++) at += Offset32(block, glyf + 8 + 4 * i);
            return at;
        }

        AssertRejected(Damage(b => b[glyf + 7] = 1), "index format");
        AssertRejected(Damage(b => b[glyf + 5]++), "glyph count");
        AssertRejected(Damage(b => b[Bitmap(b) + 1] &= 0x7F), "composite glyph has no bounding box"); // glyph 8, Oacute
        AssertRejected(Damage(b => b[Bitmap(b)] |= 0x40), "empty glyph has a bounding box"); // glyph 1, space
        AssertRejected(Damage(b => b[glyf + 8 + 4 * 6 + 2] += 0x10), "truncated"); // the instruction stream overruns the table
        AssertRejected(Damage(b => b[tables["hmtx"].Offset] |= 4), "hmtx transform flags");
        AssertRejected(Damage(b => b[tables["hmtx"].Offset] = 0), "hmtx transform flags");
    }

    [TestMethod]
    public void InvalidDirectoriesAreRejected()
    {
        byte[] woff2 = Fixture("Test-Outlines.woff2");

        // Directory entries start at 48: OS/2, cmap, then glyf (flag 10, transform version 0).
        int glyfEntry = 48 + 2 + 2;
        Assert.AreEqual(10, woff2[glyfEntry]);
        byte[] copy = (byte[])woff2.Clone();
        copy[glyfEntry] = 10 | 1 << 6;
        AssertRejected(copy, "unknown transform");

        copy = (byte[])woff2.Clone();
        copy[48] = 0x80 | 6;
        AssertRejected(copy, "WOFF2");

        copy = (byte[])woff2.Clone();
        copy[49] = 0x80;
        AssertRejected(copy, "leading zero");

        // A header that claims more table data than the limit.
        var w = new FontWriter();
        w.Bytes("wOF2"u8);
        w.U32(0x00010000);
        w.U32(60);
        w.U16(1);
        w.U16(0);
        for (int i = 0; i < 8; i++) w.U32(0);
        w.U8(5);
        foreach (byte b in new byte[] { 0x8F, 0xFF, 0xFF, 0xFF, 0x7F }) w.U8(b);
        AssertRejected(w.ToArray(), "too large");
    }
}
