using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Tests;

/// <summary>
/// Instances of variable fonts against fontTools' instancer: outlines point by point, advances
/// and font metrics, for TrueType (gvar, HVAR, MVAR, cvar) and CFF2 outlines. The fixtures and
/// references come from tools/make_variable_font_fixtures.py.
/// </summary>
[TestClass]
public sealed class VariableFontTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static PdfOpenTypeFont Load(string name) => PdfOpenTypeFont.Load(FixturePath(name));

    private static PdfOpenTypeFont At(string name, double weight) =>
        Load(name).GetInstance(new Dictionary<string, double> { ["wght"] = weight });

    [TestMethod]
    [DataRow("Test-Variable.ttf")]
    [DataRow("Test-Variable.otf")]
    public void ListsAxesAndNamedInstances(string name)
    {
        PdfOpenTypeFont font = Load(name);
        Assert.IsTrue(font.IsVariable);
        PdfFontAxis axis = font.VariationAxes.Single();
        Assert.AreEqual("wght", axis.Tag);
        Assert.AreEqual("Weight", axis.Name);
        Assert.AreEqual(100, axis.Minimum);
        Assert.AreEqual(400, axis.Default);
        Assert.AreEqual(900, axis.Maximum);
        CollectionAssert.AreEqual(new[] { "Thin", "Regular", "SemiBold", "Bold", "Black" }, font.NamedInstances.ToArray());
    }

    [TestMethod]
    public void StaticFontsHaveNoAxes()
    {
        PdfOpenTypeFont font = Load("Test-Regular.ttf");
        Assert.IsFalse(font.IsVariable);
        Assert.IsEmpty(font.VariationAxes);
        Assert.IsEmpty(font.NamedInstances);
        Assert.ThrowsExactly<InvalidOperationException>(() => font.GetInstance("Bold"));
    }

    [TestMethod]
    public void NamedInstancesCarryTheirNamesAndWeight()
    {
        PdfOpenTypeFont font = Load("Test-Variable.otf");
        PdfOpenTypeFont bold = font.GetInstance("bold");
        Assert.AreEqual("Bold", bold.StyleName);
        Assert.AreEqual("PdfNetStackTestVariable-Bold", bold.Name);
        Assert.AreEqual(700, bold.Weight);
        Assert.IsTrue(bold.IsBold);
        Assert.AreSame(bold, font.GetInstance("Bold"), "instances are made once");
        Assert.AreSame(bold, font.GetInstance(new Dictionary<string, double> { ["wght"] = 700 }));

        PdfOpenTypeFont custom = At("Test-Variable.ttf", 550);
        Assert.AreEqual("PdfNetStackTestVariable_550wght", custom.Name);
        Assert.AreEqual(550, custom.Weight);
        Assert.AreEqual(900, At("Test-Variable.ttf", 5000).Weight, "values are clamped to the axis");
        Assert.ThrowsExactly<ArgumentException>(() => font.GetInstance("Hairline"));
        Assert.ThrowsExactly<ArgumentException>(() => font.GetInstance(new Dictionary<string, double> { ["wdth"] = 75 }));
    }

    [TestMethod]
    public void InstancesMatchFontToolsOutlinesAdvancesAndMetrics() =>
        CompareInstances(File.ReadAllText(FixturePath("Test-Variable.instances.json")), FixturePath, 100);

    /// <summary>Local variable fonts, when PDFNETSTACK_VF_REFERENCE names a file written by tools/variable_font_reference.py.</summary>
    [TestMethod]
    public void InstancesMatchFontToolsOnLocalFonts()
    {
        string? path = Environment.GetEnvironmentVariable("PDFNETSTACK_VF_REFERENCE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("Set PDFNETSTACK_VF_REFERENCE to compare with fontTools.");
        CompareInstances(File.ReadAllText(path!), name => name, 1);
    }

    private static void CompareInstances(string references, Func<string, string> fontPath, int minimumGlyphs)
    {
        using JsonDocument json = JsonDocument.Parse(references);
        var failures = new List<string>();
        var fonts = new Dictionary<string, PdfOpenTypeFont>();
        int glyphs = 0;
        foreach (JsonElement reference in json.RootElement.EnumerateArray())
        {
            string name = reference.GetProperty("font").GetString()!;
            if (!fonts.TryGetValue(name, out PdfOpenTypeFont? font))
            {
                string[] parts = name.Split('#');
                fonts[name] = font = PdfOpenTypeFont.Load(fontPath(parts[0]), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
            }

            var location = reference.GetProperty("location").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetDouble());
            OpenTypeFile file = font.GetInstance(location).File;
            string label = $"{Path.GetFileName(name)} {string.Join(",", location.Select(a => $"{a.Key}={a.Value}"))}";

            JsonElement m = reference.GetProperty("metrics");
            void Metric(string key, int actual)
            {
                if (m.GetProperty(key).GetInt32() != actual) failures.Add($"{label} {key}: expected {m.GetProperty(key).GetInt32()}, got {actual}");
            }

            Metric("typoAscender", file.TypoAscender);
            Metric("hheaAscender", file.HheaAscender);
            Metric("xHeight", file.XHeight);
            Metric("capHeight", file.CapHeight);
            Metric("strikeoutSize", file.StrikeoutSize);
            Metric("strikeoutPosition", file.StrikeoutPosition);
            Metric("underlinePosition", file.UnderlinePosition);
            Metric("underlineThickness", file.UnderlineThickness);
            Metric("weightClass", file.WeightClass);

            CffFont? cff = file.IsCff ? new CffFont(file.Table("CFF ")) : null;
            foreach (JsonProperty glyph in reference.GetProperty("glyphs").EnumerateObject())
            {
                int g = int.Parse(glyph.Name, CultureInfo.InvariantCulture);
                glyphs++;
                int advance = glyph.Value.GetProperty("advance").GetInt32();
                if (file.Advance(g) != advance) failures.Add($"{label} glyph {g} advance: expected {advance}, got {file.Advance(g)}");
                if (cff is not null)
                {
                    string expected = glyph.Value.GetProperty("path").GetString()!;
                    string actual = CharStringPath(cff.CharString(g));
                    if (!SamePath(expected, actual)) failures.Add($"{label} glyph {g}:\n  fontTools {expected}\n  ours      {actual}");
                }
                else
                {
                    string expected = string.Join(" ", glyph.Value.GetProperty("points").EnumerateArray().Select(p => $"{p[0]},{p[1]},{p[2]}"))
                        + " ends " + string.Join(",", glyph.Value.GetProperty("ends").EnumerateArray().Select(e => e.GetInt32()));
                    string actual = GlyfPoints(file, g);
                    if (expected != actual) failures.Add($"{label} glyph {g}:\n  fontTools {expected}\n  ours      {actual}");
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(minimumGlyphs, glyphs);
        Assert.IsEmpty(failures, $"{failures.Count} differ:\n" + string.Join("\n", failures.Take(30)));
    }

    [TestMethod]
    public void ControlValuesFollowTheInstance()
    {
        // cvt values 2 and 3 are the stem and x height of each master: 20 + w / 6 and 480 + w / 25.
        OpenTypeFile thin = At("Test-Variable.ttf", 100).File, black = At("Test-Variable.ttf", 900).File;
        Assert.AreEqual(20 + 100 / 6, I16(thin.Table("cvt "), 4));
        Assert.AreEqual(480 + 100 / 25, I16(thin.Table("cvt "), 6));
        Assert.AreEqual(20 + 900 / 6, I16(black.Table("cvt "), 4));
        Assert.AreEqual(480 + 900 / 25, I16(black.Table("cvt "), 6));
    }

    [TestMethod]
    [DataRow("Test-Variable.ttf")]
    [DataRow("Test-Variable.otf")]
    public void InstancesEmbedAsStaticFonts(string name)
    {
        PdfOpenTypeFont bold = Load(name).GetInstance("Bold");
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("HOT $Z", bold, 12, PdfColor.Black, 72, 100);
        PdfDocument reopened = PdfDocument.Open(doc.ToArray(Plain));
        var resources = (PdfDictionary)reopened.Resolve(reopened.Pages[0].Dictionary["Resources"])!;
        var type0 = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(resources["Font"])!)["F1"])!;
        StringAssert.EndsWith(((PdfName)type0["BaseFont"]!).Value, "+PdfNetStackTestVariable-Bold");
        var cid = (PdfDictionary)reopened.Resolve(((PdfArray)reopened.Resolve(type0["DescendantFonts"])!)[0])!;
        var descriptor = (PdfDictionary)reopened.Resolve(cid["FontDescriptor"])!;
        var program = (PdfStream)reopened.Resolve(descriptor["FontFile2"] ?? descriptor["FontFile3"])!;
        byte[] data = reopened.GetStreamData(program);

        if (name.EndsWith(".otf", StringComparison.Ordinal))
        {
            Assert.AreEqual("CIDFontType0C", ((PdfName)program.Dictionary["Subtype"]!).Value);
            var cff = new CffFont(data);
            Assert.IsTrue(cff.IsCidKeyed);
            Assert.AreEqual(bold.GlyphCount, cff.GlyphCount);
            Assert.IsTrue(bold.File.TryGetGlyph('Z', out int z));
            Assert.AreEqual(CharStringPath(bold.Cff.CharString(z)), CharStringPath(cff.CharString(z)));
        }
        else
        {
            OpenTypeFile subset = OpenTypeFile.ParseSubset(data);
            Assert.IsTrue(bold.File.TryGetGlyph('O', out int o));
            Assert.AreEqual(GlyfPoints(bold.File, o), GlyfPoints(subset, o));
        }
    }

    [TestMethod]
    public void HeavyDollarSubstitutesFromSemiBold()
    {
        foreach (string name in new[] { "Test-Variable.ttf", "Test-Variable.otf" })
        {
            PdfOpenTypeFont font = Load(name);
            Assert.IsTrue(font.File.TryGetGlyph('$', out int dollar));
            Assert.AreEqual(dollar, Shape(font, "$"), name);
            Assert.AreEqual(dollar, Shape(font.GetInstance("Regular"), "$"), name);
            Assert.AreEqual(dollar + 1, Shape(font.GetInstance("SemiBold"), "$"), name);
            Assert.AreEqual(dollar + 1, Shape(font.GetInstance("Black"), "$"), name);
        }

        static int Shape(PdfOpenTypeFont font, string text) => new Shaper(font.Tables).Shape(text, 0, text.Length, false, "latn").Single().Glyph;
    }

    [TestMethod]
    public void DamagedVariationDataFailsCleanly()
    {
        var random = new Random(7);
        foreach (string name in new[] { "Test-Variable.ttf", "Test-Variable.otf" })
        {
            byte[] original = File.ReadAllBytes(FixturePath(name));
            OpenTypeFile parsed = OpenTypeFile.Parse(original, 0);
            string[] tags = [.. parsed.TableTags.Where(t => t is "fvar" or "avar" or "gvar" or "cvar" or "HVAR" or "MVAR" or "CFF2" or "GDEF" or "glyf")];
            for (int round = 0; round < 400; round++)
            {
                byte[] data = (byte[])original.Clone();
                string tag = tags[round % tags.Length];
                int start = parsed.TableOffset(tag), length = parsed.Table(tag).Length;
                for (int k = 0; k < 1 + round % 4; k++) data[start + random.Next(length)] = (byte)random.Next(256);
                try
                {
                    PdfOpenTypeFont font = PdfOpenTypeFont.Load(data);
                    if (!font.IsVariable) continue;
                    foreach (string instance in font.NamedInstances) _ = font.GetInstance(instance).Cff;
                    _ = new Shaper(font.GetInstance(new Dictionary<string, double> { ["wght"] = 650 }).Tables).Shape("To HOT $Z", 0, 9, false, "latn");
                }
                catch (FontFormatException)
                {
                    // Refused: the only acceptable failure.
                }
                catch (ArgumentException e) when (e is not ArgumentNullException)
                {
                    // A damaged fvar can rename its instances or axes; asking for them by name may fail.
                }
            }
        }
    }

    [TestMethod]
    public void Cff2ConversionHasAFontWideBudget()
    {
        // The budget counts steps and subroutine bytes across every glyph, not per glyph.
        var file = Fonts.OpenType.OpenTypeFile.Parse(File.ReadAllBytes(FixturePath("Test-Variable.otf")), 0);
        byte[] cff2 = file.Table("CFF2").ToArray();
        float[] origin = new float[Fonts.OpenType.FontData.U16(file.Table("fvar"), 8)];
        int glyphs = file.GlyphCount;
        Assert.IsNotEmpty(Fonts.OpenType.Cff2Converter.ToCff(cff2, origin, "Test", glyphs));
        Assert.ThrowsExactly<FontFormatException>(() => Fonts.OpenType.Cff2Converter.ToCff(cff2, origin, "Test", glyphs, maxSteps: 64));
    }

    [TestMethod]
    public void TruncatedVariableFontsFailCleanly()
    {
        foreach (string name in new[] { "Test-Variable.ttf", "Test-Variable.otf" })
        {
            byte[] original = File.ReadAllBytes(FixturePath(name));
            for (int length = 0; length < original.Length; length += 97)
            {
                try
                {
                    PdfOpenTypeFont font = PdfOpenTypeFont.Load(original.AsSpan(0, length).ToArray());
                    if (font.IsVariable) _ = font.GetInstance("Bold");
                }
                catch (FontFormatException)
                {
                }
            }
        }
    }

    /// <summary>The TrueType points of a glyph, components placed: "x,y,on" each, then the contour ends.</summary>
    private static string GlyfPoints(OpenTypeFile font, int glyph)
    {
        var points = new List<(int X, int Y, int On)>();
        var ends = new List<int>();
        Collect(glyph, 0, 0, 0);
        return string.Join(" ", points.Select(p => $"{p.X},{p.Y},{p.On}")) + " ends " + string.Join(",", ends);

        void Collect(int g, int dx, int dy, int depth)
        {
            (int start, int end) = font.GlyphRange(g);
            ReadOnlySpan<byte> d = font.Table("glyf").Slice(start, end - start);
            if (d.Length == 0) return;
            int contours = I16(d, 0);
            if (contours < 0)
            {
                int at = 10, flags;
                do
                {
                    flags = U16(d, at);
                    int child = U16(d, at + 2);
                    bool words = (flags & 1) != 0;
                    int x = words ? I16(d, at + 4) : (sbyte)d[at + 4];
                    int y = words ? I16(d, at + 6) : (sbyte)d[at + 5];
                    at += words ? 8 : 6;
                    at += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                    Collect(child, dx + x, dy + y, depth + 1);
                }
                while ((flags & 0x20) != 0);
                return;
            }

            int p = 10;
            int first = points.Count;
            var contourEnds = new int[contours];
            for (int c = 0; c < contours; c++) contourEnds[c] = U16(d, p + 2 * c);
            p += 2 * contours;
            p += 2 + U16(d, p);
            int count = contours == 0 ? 0 : contourEnds[^1] + 1;
            var flagsOf = new byte[count];
            for (int i = 0; i < count;)
            {
                byte flag = d[p++];
                flagsOf[i++] = flag;
                if ((flag & 8) == 0) continue;
                for (int repeat = d[p++]; repeat > 0; repeat--) flagsOf[i++] = flag;
            }

            var xs = new int[count];
            var ys = new int[count];
            foreach ((int[] values, int shortFlag, int sameFlag) in new[] { (xs, 2, 0x10), (ys, 4, 0x20) })
            {
                int value = 0;
                for (int i = 0; i < count; i++)
                {
                    int flag = flagsOf[i];
                    if ((flag & shortFlag) != 0) value += (flag & sameFlag) != 0 ? d[p++] : -d[p++];
                    else if ((flag & sameFlag) == 0) value += I16(d, (p += 2) - 2);
                    values[i] = value;
                }
            }

            for (int i = 0; i < count; i++) points.Add((xs[i] + dx, ys[i] + dy, flagsOf[i] & 1));
            foreach (int e in contourEnds) ends.Add(first + e);
        }
    }

    /// <summary>
    /// A Type 2 charstring (no subroutines) drawn as absolute moves, lines and curves the way
    /// fontTools records them: "M x y", "L x y", "C x1 y1 x2 y2 x3 y3" and "Z" after each contour.
    /// </summary>
    private static string CharStringPath(byte[] code)
    {
        var path = new List<string>();
        var stack = new List<double>();
        double x = 0, y = 0;
        int stems = 0;
        bool open = false;

        void Move(double dx, double dy)
        {
            if (open) path.Add("Z");
            x += dx;
            y += dy;
            path.Add($"M {N(x)} {N(y)}");
            open = true;
        }

        void Line(double dx, double dy)
        {
            x += dx;
            y += dy;
            path.Add($"L {N(x)} {N(y)}");
        }

        void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = x + dx1, y1 = y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
            x = x2 + dx3;
            y = y2 + dy3;
            path.Add($"C {N(x1)} {N(y1)} {N(x2)} {N(y2)} {N(x)} {N(y)}");
        }

        for (int i = 0; i < code.Length;)
        {
            int b = code[i];
            if (b >= 32 || b == 28 || b == 255)
            {
                if (b == 28) stack.Add(I16(code, i + 1));
                else if (b == 255) stack.Add((int)U32(code, i + 1) / 65536.0);
                else if (b <= 246) stack.Add(b - 139);
                else if (b <= 250) stack.Add((b - 247) * 256 + code[i + 1] + 108);
                else stack.Add(-(b - 251) * 256 - code[i + 1] - 108);
                i += b == 28 ? 3 : b == 255 ? 5 : b <= 246 ? 1 : 2;
                continue;
            }

            i++;
            Assert.IsLessThanOrEqualTo(48, stack.Count, "CFF allows 48 operands");
            switch (b)
            {
                case 1 or 3 or 18 or 23:
                    stems += stack.Count / 2;
                    break;
                case 19 or 20:
                    stems += stack.Count / 2;
                    i += (stems + 7) / 8;
                    break;
                case 21:
                    Move(stack[0], stack[1]);
                    break;
                case 22:
                    Move(stack[0], 0);
                    break;
                case 4:
                    Move(0, stack[0]);
                    break;
                case 5:
                    for (int k = 0; k + 1 < stack.Count; k += 2) Line(stack[k], stack[k + 1]);
                    break;
                case 6 or 7:
                    for (int k = 0; k < stack.Count; k++)
                    {
                        if ((k % 2 == 0) == (b == 6)) Line(stack[k], 0);
                        else Line(0, stack[k]);
                    }

                    break;
                case 8:
                    for (int k = 0; k + 5 < stack.Count; k += 6) Curve(stack[k], stack[k + 1], stack[k + 2], stack[k + 3], stack[k + 4], stack[k + 5]);
                    break;
                case 24:
                {
                    int k = 0;
                    for (; k + 7 < stack.Count; k += 6) Curve(stack[k], stack[k + 1], stack[k + 2], stack[k + 3], stack[k + 4], stack[k + 5]);
                    Line(stack[k], stack[k + 1]);
                    break;
                }

                case 25:
                {
                    int k = 0;
                    for (; k + 6 < stack.Count; k += 2) Line(stack[k], stack[k + 1]);
                    Curve(stack[k], stack[k + 1], stack[k + 2], stack[k + 3], stack[k + 4], stack[k + 5]);
                    break;
                }

                case 26 or 27:
                {
                    int k = 0;
                    double extra = 0;
                    if (stack.Count % 4 == 1) extra = stack[k++];
                    for (; k + 3 < stack.Count; k += 4)
                    {
                        if (b == 26) Curve(extra, stack[k], stack[k + 1], stack[k + 2], 0, stack[k + 3]);
                        else Curve(stack[k], extra, stack[k + 1], stack[k + 2], stack[k + 3], 0);
                        extra = 0;
                    }

                    break;
                }

                case 30 or 31:
                {
                    bool horizontal = b == 31;
                    for (int k = 0; k + 3 < stack.Count; k += 4)
                    {
                        double last = k + 5 == stack.Count ? stack[k + 4] : 0;
                        if (horizontal) Curve(stack[k], 0, stack[k + 1], stack[k + 2], last, stack[k + 3]);
                        else Curve(0, stack[k], stack[k + 1], stack[k + 2], stack[k + 3], last);
                        horizontal = !horizontal;
                    }

                    break;
                }

                case 14:
                    if (open) path.Add("Z");
                    return string.Join(" ", path);
                default:
                    Assert.Fail($"Unexpected charstring operator {b}.");
                    break;
            }

            stack.Clear();
        }

        Assert.Fail("Charstring has no endchar.");
        return "";

        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Paths match when their commands match and every coordinate is within 0.02.</summary>
    private static bool SamePath(string expected, string actual)
    {
        string[] a = expected.Split(' '), b = actual.Split(' ');
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            bool numberA = double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double va);
            bool numberB = double.TryParse(b[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double vb);
            if (numberA != numberB || (numberA ? Math.Abs(va - vb) > 0.02 : a[i] != b[i])) return false;
        }

        return true;
    }
}
