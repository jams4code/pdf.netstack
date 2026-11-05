using System.Globalization;
using PdfNetStack.Drawing;
using PdfNetStack.Hyphenation;

namespace PdfNetStack.Tests;

/// <summary>Liang's algorithm and the pattern sets, against what the sources publish.</summary>
[TestClass]
public sealed class HyphenationTests
{
    private const char SoftHyphen = (char)0xAD;

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hyphenation");

    private static PdfHyphenator For(string language) =>
        PdfHyphenationPatterns.ForLanguage(language) ?? throw new AssertFailedException("No patterns for " + language);

    private static string Hyphenate(PdfHyphenator hyphenator, string word)
    {
        string result = word;
        foreach (int b in hyphenator.FindBreaks(word).Reverse()) result = result.Insert(b, "-");
        return result;
    }

    private static string Hyphenate(string language, string word) => Hyphenate(For(language), word);

    /// <summary>Every set, against a direct reading of Liang's definition in tools/make_hyphenation_data.py.</summary>
    [TestMethod]
    public void AgreesWithAnIndependentImplementationInEveryLanguage()
    {
        var seen = new HashSet<string>();
        int lines = 0;
        foreach (string line in File.ReadLines(Path.Combine(Fixtures, "reference.tsv")))
        {
            if (line[0] == '#') continue;
            string[] f = line.Split('\t');
            seen.Add(f[0]);
            lines++;
            Assert.AreEqual(f[2], Hyphenate(For(f[0]) with { MinWordLength = 1 }, f[1]), f[0]);
        }

        Assert.IsGreaterThan(1000, lines);
        CollectionAssert.AreEquivalent(PdfHyphenationPatterns.Languages.ToArray(), seen.ToArray());
    }

    /// <summary>The word list the German patterns are generated from, as the list hyphenates each word.</summary>
    [TestMethod]
    public void GermanFollowsItsWordList()
    {
        int lines = 0;
        foreach (string line in File.ReadLines(Path.Combine(Fixtures, "de-wortliste.tsv")))
        {
            if (line[0] == '#') continue;
            string[] f = line.Split('\t');
            lines++;
            Assert.AreEqual(f[1], Hyphenate(For("de") with { MinWordLength = 1 }, f[0]));
        }

        Assert.IsGreaterThan(200, lines);
    }

    [TestMethod]
    [DataRow("en-us", "hyphenation", "hy-phen-ation", DisplayName = "TeXbook, appendix H")]
    [DataRow("en-us", "associate", "as-so-ciate", DisplayName = "en-us exception")]
    [DataRow("en-us", "table", "ta-ble", DisplayName = "en-us exception, short")]
    [DataRow("en-us", "reciprocity", "reci-procity", DisplayName = "en-us exception, long")]
    [DataRow("en-us", "present", "present", DisplayName = "en-us exception without breaks")]
    [DataRow("en-us", "project", "project", DisplayName = "en-us exception without breaks 2")]
    [DataRow("en-us", "democrat", "de-mo-c-rat", DisplayName = "en-us, known_bugs in the file's header")]
    [DataRow("en-gb", "hyphenation", "hy-phen-a-tion", DisplayName = "en-gb patterns differ from en-us")]
    [DataRow("nl", "aandachtstrekker", "aan-dachts-trek-ker", DisplayName = "nl exception")]
    [DataRow("nl", "achtendertig", "acht-en-der-tig", DisplayName = "nl exception 2")]
    [DataRow("fr", "l'information", "l'in-for-ma-tion", DisplayName = "fr, tests/features/hyphenation.feature")]
    [DataRow("fr", "l\u2019information", "l\u2019in-for-ma-tion", DisplayName = "fr with a typographic apostrophe")]
    [DataRow("it", "dell'amicizia", "del-l'a-mi-ci-zia", DisplayName = "it, tests/features/hyphenation.feature")]
    [DataRow("it", "dell\u2019amicizia", "del-l\u2019a-mi-ci-zia", DisplayName = "it with a typographic apostrophe")]
    [DataRow("pt", "hardware", "hard-ware", DisplayName = "pt exception")]
    [DataRow("pt", "software", "soft-ware", DisplayName = "pt exception 2")]
    [DataRow("pl", "bynajmniej", "by-naj-mniej", DisplayName = "pl exception")]
    [DataRow("nb", "attende", "at-ten-de", DisplayName = "nb exception")]
    [DataRow("nn", "attende", "att-en-de", DisplayName = "nn has its own exceptions over the same patterns")]
    [DataRow("es", "desarrollo", "de-sa-rro-llo", DisplayName = "es never splits rr or ll")]
    [DataRow("de", "Rechtschreibung", "Recht-schrei-bung", DisplayName = "de, capitalized noun")]
    public void FollowsWhatTheSourcesPublish(string language, string word, string expected) =>
        Assert.AreEqual(expected, Hyphenate(language, word));

    /// <summary>The header of hyph-nl.tex lists the breaks its patterns must not make.</summary>
    [TestMethod]
    [DataRow("adrenaline", "a-d")]
    [DataRow("studio", "i-o")]
    [DataRow("mensapen", "a-p")]
    [DataRow("videoachtig", "e-o")]
    [DataRow("menuutje", "u-t")]
    public void DutchAvoidsTheBreaksItsHeaderRules(string word, string wrong) =>
        Assert.DoesNotContain(wrong, Hyphenate(For("nl") with { MinWordLength = 1 }, word));

    [TestMethod]
    public void DutchReportsItsKnownBug() =>
        Assert.AreEqual("xi-ii", Hyphenate(For("nl") with { MinWordLength = 1 }, "xiii"), "known_bugs in the header of hyph-nl.tex");

    [TestMethod]
    public void MinimumsComeFromTheSourceAndCanChange()
    {
        Assert.AreEqual(2, For("en-us").MinLeft);
        Assert.AreEqual(3, For("en-us").MinRight);
        Assert.AreEqual(2, For("fr").MinRight);
        Assert.AreEqual(1, For("el").MinLeft);
        Assert.AreEqual(5, For("fr").MinWordLength);

        Assert.AreEqual("hy-phen-ation", Hyphenate(For("en-us"), "hyphenation"));
        Assert.AreEqual("hyphen-ation", Hyphenate(For("en-us") with { MinLeft = 3 }, "hyphenation"));
        Assert.AreEqual("hy-phenation", Hyphenate(For("en-us") with { MinRight = 6 }, "hyphenation"));
        Assert.AreEqual("hyphenation", Hyphenate(For("en-us") with { MinWordLength = 12 }, "hyphenation"));
        Assert.AreEqual("table", Hyphenate(For("en-us") with { MinWordLength = 6 }, "table"));
        Assert.AreEqual(2, For("en-us").MinLeft, "with makes a copy");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => For("en-us") with { MinLeft = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => For("en-us") with { MinRight = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => For("en-us") with { MinWordLength = 0 });
    }

    [TestMethod]
    public void ExceptionsWinOverPatterns()
    {
        PdfHyphenator h = For("en-us").WithExceptions(["hyph-enation", "Algorithm", "ma-\u00F1ana"]);
        Assert.AreEqual("hyph-enation", Hyphenate(h, "hyphenation"));
        Assert.AreEqual("Hyph-enation", Hyphenate(h, "Hyphenation"), "the case of the text does not matter");
        Assert.AreEqual("algorithm", Hyphenate(h, "algorithm"), "without a hyphen it never breaks");
        Assert.AreEqual("ma-\u00F1ana", Hyphenate(h, "ma\u00F1ana"), "its letter joins the alphabet");
        Assert.AreEqual("hy-phen-ation", Hyphenate(For("en-us"), "hyphenation"), "the shared set stays as it was");
        Assert.AreEqual("hyphenat-ion", Hyphenate(h.WithExceptions(["hyphenat-ion"]), "hyphenation"), "the later exception wins");

        // The minimums still apply to them, as in TeX.
        Assert.AreEqual("xyzab", Hyphenate(PdfHyphenator.Create(["a1b"], ["x-yzab"]), "xyzab"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("-ab")]
    [DataRow("ab-")]
    [DataRow("a--b")]
    [DataRow("a1b")]
    [DataRow("a.b")]
    [DataRow("a b")]
    public void RejectsMalformedExceptions(string word) =>
        Assert.ThrowsExactly<ArgumentException>(() => PdfHyphenator.Create(["a1b"], [word]));

    [TestMethod]
    [DataRow("")]
    [DataRow("123")]
    [DataRow("a12b")]
    [DataRow("a.b")]
    [DataRow(".")]
    public void RejectsMalformedPatterns(string pattern) =>
        Assert.ThrowsExactly<ArgumentException>(() => PdfHyphenator.Create([pattern]));

    [TestMethod]
    public void AppliesPatternsAsLiangDefinesThem()
    {
        // Larger digits win, odd allows and even forbids; a dot matches only at an edge.
        PdfHyphenator h = PdfHyphenator.Create(["a1b", "a2b3c", ".x1y", "z1z.", "1m", "n1"]) with { MinLeft = 1, MinRight = 1, MinWordLength = 1 };
        Assert.AreEqual("a-bz", Hyphenate(h, "abz"));
        Assert.AreEqual("ab-c", Hyphenate(h, "abc"));
        Assert.AreEqual("x-ya", Hyphenate(h, "xya"));
        Assert.AreEqual("axya", Hyphenate(h, "axya"));
        Assert.AreEqual("az-z", Hyphenate(h, "azz"));
        Assert.AreEqual("azza", Hyphenate(h, "azza"));
        Assert.AreEqual("a-m", Hyphenate(h, "am"));
        Assert.AreEqual("n-a", Hyphenate(h, "na"));
    }

    [TestMethod]
    [DataRow("See https://example.com/information/hyphenation here")]
    [DataRow("Mail information@example.com now")]
    [DataRow("www.information.com")]
    [DataRow("information.hyphenation")]
    [DataRow("C:\\information\\hyphenation")]
    [DataRow("/usr/information/hyphenation")]
    [DataRow("information_hyphenation")]
    [DataRow("?information=hyphenation&international=representation")]
    public void LeavesAddressesAlone(string text)
    {
        foreach (string token in text.Split(' '))
        {
            if (token.Length > 5 && (token.Contains('.') || token.Contains('/') || token.Contains('@') || token.Contains('\\') || token.Contains('_') || token.Contains('=')))
            {
                Assert.AreEqual(token, Hyphenate("en-us", token));
            }
        }
    }

    [TestMethod]
    public void LeavesWordsWithDigitsCapitalsAndForeignLettersAlone()
    {
        Assert.AreEqual("information2", Hyphenate("en-us", "information2"));
        Assert.AreEqual("2information", Hyphenate("en-us", "2information"));
        Assert.AreEqual("1234567890", Hyphenate("en-us", "1234567890"));
        Assert.AreEqual("INFORMATION", Hyphenate("en-us", "INFORMATION"));
        Assert.AreEqual("IN-FOR-MA-TION", Hyphenate(For("en-us") with { HyphenateAllCaps = true }, "INFORMATION"));
        Assert.AreEqual("In-for-ma-tion", Hyphenate("en-us", "Information"));
        Assert.AreEqual("InformationSystem", Hyphenate("en-us", "InformationSystem"));
        Assert.AreEqual("McInformation", Hyphenate("en-us", "McInformation"));
        Assert.AreEqual("informati\u00F6n", Hyphenate("en-us", "informati\u00F6n"), "a letter outside the alphabet");
        Assert.AreEqual("informa\u05D0tion", Hyphenate("en-us", "informa\u05D0tion"));
        Assert.AreEqual("couldn't", Hyphenate("en-us", "couldn't"), "TeX leaves a word with an inner mark alone unless its alphabet has it");
        Assert.AreEqual(new string('a', 65), Hyphenate(PdfHyphenator.Create(["a1a"]), new string('a', 65)), "over 64 letters, as in TeX");
        Assert.AreNotEqual(new string('a', 64), Hyphenate(PdfHyphenator.Create(["a1a"]), new string('a', 64)));
    }

    [TestMethod]
    public void HonorsTheAuthorsMarks()
    {
        // CSS Text 3, 5.4: a word with a soft hyphen breaks only there.
        string marked = "in" + SoftHyphen + "formation";
        Assert.AreEqual(marked, Hyphenate("en-us", marked));
        Assert.AreEqual("in\u2060formation", Hyphenate("en-us", "in\u2060formation"), "a word joiner");
        Assert.AreEqual("in\u200Cformation", Hyphenate("en-us", "in\u200Cformation"), "a zero width non-joiner");

        // Each part of a hyphenated compound is a word.
        Assert.AreEqual("in-for-ma-tion-hy-phen-ation", Hyphenate("en-us", "information-hyphenation"));

        // Punctuation around a word is not part of it.
        Assert.AreEqual("(in-for-ma-tion),", Hyphenate("en-us", "(information),"));
        Assert.AreEqual("'in-for-ma-tion'", Hyphenate("en-us", "'information'"));
        Assert.AreEqual("\u00ABin-for-ma-tion\u00BB", Hyphenate("fr", "\u00ABinformation\u00BB"));
        Assert.AreEqual("'in-for-ma-tion'", Hyphenate("fr", "'information'"), "quotes, though French patterns have the character");
    }

    [TestMethod]
    public void ReadsDecomposedTextLikeComposedText()
    {
        Assert.AreEqual("d\u00E9-ve-lop-pe-ment", Hyphenate("fr", "d\u00E9veloppement"));
        Assert.AreEqual("de\u0301-ve-lop-pe-ment", Hyphenate("fr", "de\u0301veloppement"));
        Assert.AreEqual("D\u00C9-VE-LOP-PE-MENT", Hyphenate(For("fr") with { HyphenateAllCaps = true }, "D\u00C9VELOPPEMENT"));
        Assert.AreEqual("informatio\u0308n", Hyphenate("en-us", "informatio\u0308n"), "a mark that makes a letter outside the alphabet");
    }

    [TestMethod]
    public void CaseFoldingIgnoresTheCulture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        string[] words = ["INFORMATION", "Information", "information", "Ideal", "Illustration"];
        PdfHyphenator h = For("en-us") with { HyphenateAllCaps = true };
        string[] invariant, turkish;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            invariant = [.. words.Select(w => Hyphenate(h, w))];
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            }
            catch (CultureNotFoundException)
            {
                // Invariant globalization has no other cultures: the CI run under it covers that case.
            }

            turkish = [.. words.Select(w => Hyphenate(h, w))];
            Assert.IsNotNull(PdfHyphenationPatterns.ForLanguage("IT"), "tag lookup under tr-TR: I is not the capital of i there");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        CollectionAssert.AreEqual(invariant, turkish);
        Assert.AreEqual("IN-FOR-MA-TION", invariant[0]);
    }

    [TestMethod]
    public void FindsSetsByLanguageTag()
    {
        foreach (string main in new[] { "en-us", "en-gb", "de", "fr", "nl", "es", "it", "pt" }) CollectionAssert.Contains(PdfHyphenationPatterns.Languages.ToArray(), main);
        Assert.AreSame(For("en-us"), For("en"));
        Assert.AreSame(For("en-us"), For("EN-US"));
        Assert.AreSame(For("en-us"), For("en_US"));
        Assert.AreSame(For("en-us"), For("en-CA"));
        Assert.AreSame(For("en-gb"), For("en-GB"));
        Assert.AreSame(For("en-gb"), For("en-AU"));
        Assert.AreSame(For("de"), For("de-DE"));
        Assert.AreSame(For("de"), For("de-AT"));
        Assert.AreSame(For("de"), For("de_CH"));
        Assert.AreSame(For("de"), For("de-1996"));
        Assert.AreSame(For("pt"), For("pt-BR"));
        Assert.AreSame(For("pt"), For("pt-PT"));
        Assert.AreSame(For("fr"), For("fr-CA"));
        Assert.AreSame(For("nb"), For("no"));
        Assert.AreSame(For("nb"), For("nb-NO"));
        Assert.AreSame(For("el-monoton"), For("el"));
        Assert.AreSame(For("el-monoton"), For("el-GR"));

        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("de-1901"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("de-CH-1901"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("ru"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("sv-SE"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage(""));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("-"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage("\u0130"));
        Assert.IsNull(PdfHyphenationPatterns.ForLanguage(new string('e', 100000)));
        Assert.ThrowsExactly<ArgumentNullException>(() => PdfHyphenationPatterns.ForLanguage(null!));
    }

    [TestMethod]
    public void InsertsSoftHyphens()
    {
        Assert.AreEqual($"Hy{SoftHyphen}phen{SoftHyphen}ation of (in{SoftHyphen}for{SoftHyphen}ma{SoftHyphen}tion), 2026 and http://example.com/information.",
            For("en-us").InsertSoftHyphens("Hyphenation of (information), 2026 and http://example.com/information."));
        Assert.AreEqual("", For("en-us").InsertSoftHyphens(""));
        Assert.AreEqual("a b", For("en-us").InsertSoftHyphens("a b"));
    }

    [TestMethod]
    public void HostileTextStaysBoundedAndNeverThrows()
    {
        PdfHyphenator h = For("en-us");

        // A megabyte of one word, of short words and of characters that are not letters: all return, with no work over a word's 64 letters.
        Assert.IsEmpty(h.FindBreaks(new string('a', 1 << 20)));
        Assert.IsNotEmpty(h.FindBreaks(string.Concat(Enumerable.Repeat("information ", 1 << 16))));
        Assert.IsEmpty(h.FindBreaks(new string('\uD83D', 1 << 16)));
        Assert.IsEmpty(h.FindBreaks(new string('\u0301', 1 << 16)));
        Assert.IsEmpty(h.FindBreaks(new string('\'', 1 << 16)));

        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var chars = new char[random.Next(200)];
            for (int k = 0; k < chars.Length; k++) chars[k] = random.Next(4) == 0 ? (char)random.Next(0x10000) : (char)random.Next(0x20, 0x180);
            string text = new(chars);
            foreach (int b in h.FindBreaks(text)) Assert.IsTrue(b > 0 && b < text.Length);
            foreach (int b in For("fr").FindBreaks(text)) Assert.IsTrue(b > 0 && b < text.Length);
            _ = For("de").InsertSoftHyphens(text);
        }
    }

    [TestMethod]
    public void SharedAcrossThreads()
    {
        var results = new string[64];
        Parallel.For(0, results.Length, i => results[i] = Hyphenate(PdfHyphenationPatterns.ForLanguage("es")!, "otorrinolaringólogo"));
        CollectionAssert.AreEqual(Enumerable.Repeat("oto-rri-no-la-rin-gó-lo-go", 64).ToArray(), results);
    }
}
