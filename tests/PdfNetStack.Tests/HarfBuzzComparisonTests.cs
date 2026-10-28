using System.Text.Json;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;

namespace PdfNetStack.Tests;

/// <summary>
/// The shaper against HarfBuzz, glyph by glyph: ids, clusters, advances and offsets in font
/// units. The synthetic Arabic fixture always runs, with HarfBuzz's output recorded by
/// tools/make_font_fixtures.py. Local fonts run when PDFNETSTACK_HB_REFERENCE names a file written
/// by tools/harfbuzz_reference.py; they are not ours to commit.
/// </summary>
[TestClass]
public sealed class HarfBuzzComparisonTests
{
    [TestMethod]
    public void MatchesHarfBuzzOnTheArabicFixture()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts");
        Compare(File.ReadLines(Path.Combine(dir, "Test-Arabic.shaping.jsonl")), name => Path.Combine(dir, name));
    }

    [TestMethod]
    public void MatchesHarfBuzzOnLocalFonts()
    {
        string? path = Environment.GetEnvironmentVariable("PDFNETSTACK_HB_REFERENCE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("Set PDFNETSTACK_HB_REFERENCE to compare with HarfBuzz.");
        Compare(File.ReadLines(path!), name => name);
    }

    private static void Compare(IEnumerable<string> lines, Func<string, string> fontPath)
    {
        var fonts = new Dictionary<string, PdfOpenTypeFont>();
        var failures = new List<string>();
        int cases = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;
            string font = fontPath(root.GetProperty("font").GetString()!);
            string text = root.GetProperty("text").GetString()!;
            bool rtl = root.GetProperty("direction").GetString() == "rtl";
            string script = root.GetProperty("script").GetString()!;
            if (!fonts.TryGetValue(font, out PdfOpenTypeFont? loaded)) fonts[font] = loaded = PdfOpenTypeFont.Load(font);

            // HarfBuzz clusters are code point indices; ours are UTF-16 indices.
            var cpIndex = new Dictionary<int, int>();
            for (int i = 0, k = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1, k++) cpIndex[i] = k;

            // Both sides in logical order: the reference scripts reverse HarfBuzz's right to left output.
            List<GlyphInfo> ours = new Shaper(loaded.Tables).Shape(text, 0, text.Length, rtl, script);
            string actual = string.Join(" ", ours.Select(g => $"{g.Glyph}@{cpIndex[g.Cluster]}+{g.XAdvance}<{g.XOffset},{g.YOffset}>"));
            string expected = string.Join(" ", root.GetProperty("glyphs").EnumerateArray()
                .Select(g => $"{g[0].GetInt32()}@{g[1].GetInt32()}+{g[2].GetInt32()}<{g[3].GetInt32()},{g[4].GetInt32()}>"));
            cases++;
            if (actual != expected) failures.Add($"{Path.GetFileName(font)} \"{text}\"\n  harfbuzz {expected}\n  ours     {actual}");
        }

        Assert.IsGreaterThan(0, cases);
        Assert.IsEmpty(failures, $"{failures.Count} of {cases} differ:\n" + string.Join("\n", failures.Take(12)));
    }
}
