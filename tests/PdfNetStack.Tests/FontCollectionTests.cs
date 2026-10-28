using PdfNetStack.Fonts;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class FontCollectionTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    [TestMethod]
    public void FindsByFamilyAndWeight()
    {
        var fonts = new PdfFontCollection();
        Assert.AreEqual(2, fonts.AddFile(FixturePath("Test.ttc")));
        CollectionAssert.AreEqual(new[] { "PdfNetStack Test" }, fonts.Families.ToArray());

        Assert.AreEqual("PdfNetStackTest-Regular", fonts.Find("PdfNetStack Test")!.Name);
        Assert.AreEqual("PdfNetStackTest-Bold", fonts.Find("pdfnetstack test", 700)!.Name);

        // CSS order: 450 looks heavier up to 500, then lighter; 550 looks heavier first; 300 lighter first, then heavier.
        Assert.AreEqual(400, fonts.Find("PdfNetStack Test", 450)!.Weight);
        Assert.AreEqual(700, fonts.Find("PdfNetStack Test", 550)!.Weight);
        Assert.AreEqual(700, fonts.Find("PdfNetStack Test", 900)!.Weight);
        Assert.AreEqual(400, fonts.Find("PdfNetStack Test", 300)!.Weight);

        // A missing italic falls back to the upright face.
        Assert.AreEqual(700, fonts.Find("PdfNetStack Test", 700, italic: true)!.Weight);
        Assert.IsNull(fonts.Find("Nope"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fonts.Find("PdfNetStack Test", 0));
    }

    [TestMethod]
    public void LoadsEachFontOnce()
    {
        var fonts = new PdfFontCollection();
        fonts.AddFile(FixturePath("Test-Regular.otf"));
        Assert.AreSame(fonts.Find("PdfNetStack Test"), fonts.Find("PdfNetStack Test"));

        PdfOpenTypeFont loaded = PdfOpenTypeFont.Load(FixturePath("Test-Regular.ttf"));
        var other = new PdfFontCollection();
        other.Add(loaded);
        Assert.AreSame(loaded, other.Find("PdfNetStack Test"));
    }

    [TestMethod]
    public void WeightRankFollowsCss()
    {
        // CSS Fonts 4, 5.2 step 4: candidates in order of preference for each requested weight.
        AssertOrder(400, [400, 500, 300, 100, 600, 900]);
        AssertOrder(500, [500, 400, 300, 600, 900]);
        AssertOrder(600, [600, 700, 900, 500, 400]);
        AssertOrder(300, [300, 200, 100, 400, 500]);

        static void AssertOrder(int wanted, int[] expected)
        {
            int[] shuffled = [.. Enumerable.Reverse(expected)];
            int[] ordered = [.. shuffled.OrderBy(w => PdfFontCollection.WeightRank(wanted, w))];
            CollectionAssert.AreEqual(expected, ordered, $"weight {wanted}");
        }
    }

    [TestMethod]
    public void AddDirectoryScansFontFilesAndSkipsTheRest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfnetstack-fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        try
        {
            File.Copy(FixturePath("Test.ttc"), Path.Combine(dir, "Test.ttc"));
            File.Copy(FixturePath("Test-Regular.woff"), Path.Combine(dir, "nested", "Test-Regular.woff"));
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a font");
            File.WriteAllBytes(Path.Combine(dir, "broken.ttf"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);

            var flat = new PdfFontCollection();
            Assert.AreEqual(2, flat.AddDirectory(dir, recursive: false));
            var deep = new PdfFontCollection();
            Assert.AreEqual(3, deep.AddDirectory(dir));
            Assert.AreEqual(0, new PdfFontCollection().AddDirectory(Path.Combine(dir, "missing")));
            Assert.ThrowsExactly<FontFormatException>(() => new PdfFontCollection().AddFile(Path.Combine(dir, "broken.ttf")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void SystemFontsScanWithoutErrors()
    {
        // Contents depend on the machine; scanning and a lookup must simply work.
        IReadOnlyList<string> families = PdfFontCollection.System.Families;
        if (families.Count > 0) Assert.IsTrue(families.Any(f => PdfFontCollection.System.Find(f) is not null));
    }
}
