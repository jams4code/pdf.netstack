using System.Diagnostics;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Ocr;
using PdfNetStack.Ocr.Tesseract;
using PdfNetStack.Ocr.Tesseract.Processing;

namespace PdfNetStack.Tests;

/// <summary>
/// The Tesseract engine without Tesseract: command lines, output parsing and process handling, the
/// last through a stand-in executable that does as each test asks. See <see cref="TesseractRealTests"/>
/// for the real one.
/// </summary>
[TestClass]
public sealed class TesseractOcrTests
{
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

    private static readonly OcrBitmap Small = new(4, 2, new byte[8]);

    private static string Fake() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "PdfNetStack.FakeTesseract", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0",
        Environment.OSVersion.Platform == PlatformID.Win32NT ? "fake-tesseract.exe" : "fake-tesseract"));

    /// <summary>A data folder for one test, with the languages named and the stand-in's settings.</summary>
    private sealed class Folder : IDisposable
    {
        public Folder(string settings = "", string name = "data", params string[] languages)
        {
            Parent = Path.Combine(Path.GetTempPath(), "pdfnetstack-tess-" + Guid.NewGuid().ToString("N"));
            Data = Path.Combine(Parent, name);
            Directory.CreateDirectory(Data);
            foreach (string language in languages.Length > 0 ? languages : ["eng"]) File.WriteAllText(Path.Combine(Data, language + ".traineddata"), "");
            File.WriteAllText(Path.Combine(Data, "fake.txt"), settings);
        }

        public string Parent { get; }

        public string Data { get; }

        public TesseractOcrEngine Engine(string[]? languages = null, TimeSpan? timeout = null, long maxOutput = 64L << 20, int processes = 8, int threads = 1) =>
            TesseractOcrEngine.Load(new TesseractOcrEngineOptions
            {
                ExecutablePath = Fake(),
                TessdataPath = Data,
                Languages = languages ?? ["eng"],
                Timeout = timeout ?? TimeSpan.FromMinutes(2),
                MaxOutputBytes = maxOutput,
                MaxConcurrentProcesses = processes,
                ThreadsPerProcess = threads,
            });

        public string Tsv(string content)
        {
            string path = Path.Combine(Parent, "out.tsv");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            return path;
        }

        public void Settings(string settings) => File.WriteAllText(Path.Combine(Data, "fake.txt"), settings);

        public string[] Recorded(string prefix) => Directory.GetFiles(Data, prefix + "-*");

        public void Dispose()
        {
            // A killed process can hold its files for a moment on Windows; what is left is in the temp folder.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(Parent, true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    [TestMethod]
    public void BuildsTheCommandLine()
    {
        CollectionAssert.AreEqual(
            new[] { "stdin", "stdout", "--tessdata-dir", "/data", "-l", "eng+ara", "--psm", "6", "--dpi", "300", "-c", "tessedit_create_tsv=1" },
            TesseractArguments.Recognize("eng+ara", "/data", 6, 300).ToArray());
        CollectionAssert.AreEqual(
            new[] { "stdin", "stdout", "-l", "eng", "--psm", "3", "-c", "tessedit_create_tsv=1" },
            TesseractArguments.Recognize("eng", null, 3, null).ToArray());
        CollectionAssert.AreEqual(new[] { "--tessdata-dir", "/data", "--list-langs" }, TesseractArguments.ListLanguages("/data").ToArray());
    }

    [TestMethod]
    [DataRow("eng")]
    [DataRow("chi_sim")]
    [DataRow("eng+ara+fra")]
    [DataRow("script/Latin")]
    [DataRow("chi_sim_vert+script/Arabic")]
    [DataRow("sr-Latn")]
    public void TakesLanguageNames(string language) => Assert.IsTrue(TesseractArguments.IsValidLanguage(language));

    [TestMethod]
    [DataRow("")]
    [DataRow("+")]
    [DataRow("eng+")]
    [DataRow("-c")]
    [DataRow("--psm")]
    [DataRow("../eng")]
    [DataRow("..")]
    [DataRow("a/b")]
    [DataRow("script/../../eng")]
    [DataRow("/etc/passwd")]
    [DataRow("C:\\data\\eng")]
    [DataRow("eng ara")]
    [DataRow("eng\tara")]
    [DataRow("eng\nara")]
    [DataRow("eng;rm -rf x")]
    [DataRow("$(id)")]
    [DataRow("`id`")]
    [DataRow("eng\"")]
    [DataRow("~eng")]
    [DataRow("eng\0")]
    [DataRow("éng")]
    public void RefusesAnythingElseAsALanguage(string language) => Assert.IsFalse(TesseractArguments.IsValidLanguage(language), language);

    [TestMethod]
    public void RefusesEndlessLanguages()
    {
        Assert.IsFalse(TesseractArguments.IsValidLanguage(new string('a', 65)));
        Assert.IsFalse(TesseractArguments.IsValidLanguage(string.Join("+", Enumerable.Repeat("eng", 17))));
    }

    [TestMethod]
    [DataRow("plain", "plain")]
    [DataRow("", "\"\"")]
    [DataRow("a b", "\"a b\"")]
    [DataRow("a\\b", "a\\b")]
    [DataRow("a b\\", "\"a b\\\\\"")]
    [DataRow("a\\\\b c", "\"a\\\\b c\"")]
    [DataRow("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [DataRow("back\\\"slash", "\"back\\\\\\\"slash\"")]
    [DataRow("tab\there", "\"tab\there\"")]
    public void QuotesForACommandLine(string argument, string expected) => Assert.AreEqual(expected, TesseractArguments.Quote(argument));

    [TestMethod]
    public void ArgumentsReachTheProcessIntact()
    {
        // The folder's name goes through whichever way this framework starts a process.
        bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        using var folder = new Folder(name: windows ? "tess data's é & $x;" : "tess data's \"quoted\" é & $x;");
        using TesseractOcrEngine engine = folder.Engine();
        engine.Recognize(Small, new OcrRequest { Language = "eng" }, default);

        string[] arguments = File.ReadAllLines(folder.Recorded("args").Single());
        CollectionAssert.AreEqual(new[] { "stdin", "stdout", "--tessdata-dir", folder.Data, "-l", "eng", "--psm", "3", "-c", "tessedit_create_tsv=1" }, arguments);
    }

    [TestMethod]
    public void SetsTheChildsEnvironmentAndFolder()
    {
        using var folder = new Folder();
        using TesseractOcrEngine engine = folder.Engine(threads: 3);
        engine.Recognize(Small, new OcrRequest { Language = "eng" }, default);

        string[] variables = File.ReadAllLines(folder.Recorded("env").Single());
        CollectionAssert.Contains(variables, "OMP_THREAD_LIMIT=3");
        CollectionAssert.Contains(variables, "LC_ALL=C");
        Assert.AreEqual(
            Path.GetDirectoryName(Fake()),
            Path.GetFullPath(variables.Single(v => v.StartsWith("CWD=", StringComparison.Ordinal)).Substring(4)).TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    [TestMethod]
    public void SendsTheImageAsABitmapFile()
    {
        // An odd width, so rows are padded, with every pixel different and known.
        const int Width = 5, Height = 3;
        byte[] pixels = [.. Enumerable.Range(0, Width * Height).Select(i => (byte)(i * 17))];
        using var folder = new Folder();
        using TesseractOcrEngine engine = folder.Engine();
        engine.Recognize(new OcrBitmap(Width, Height, pixels), new OcrRequest { Language = "eng" }, default);

        byte[] file = File.ReadAllBytes(folder.Recorded("stdin").Single());
        Assert.AreEqual((byte)'B', file[0]);
        Assert.AreEqual((byte)'M', file[1]);
        Assert.HasCount(BitConverter.ToInt32(file, 2), file);
        Assert.AreEqual(8, BitConverter.ToInt16(file, 28));

        // Read back by the core's own decoder: the palette and the bottom up, padded rows undo to the pixels.
        Imaging.DecodedImage decoded = Imaging.BmpDecoder.Read(file);
        Assert.AreEqual(Width, decoded.Width);
        Assert.AreEqual(Height, decoded.Height);
        byte[] gray = decoded.Colors == 1 ? decoded.Samples : [.. Enumerable.Range(0, Width * Height).Select(i => decoded.Samples[decoded.Colors * i])];
        CollectionAssert.AreEqual(pixels, gray);
    }

    [TestMethod]
    public void ParsesWordsAndLines()
    {
        string tsv = Header
            + "1\t1\t0\t0\t0\t0\t0\t0\t1000\t800\t-1\t\n"
            + "2\t1\t1\t0\t0\t0\t10\t20\t500\t200\t-1\t\n"
            + "3\t1\t1\t1\t0\t0\t10\t20\t500\t200\t-1\t\n"
            + "4\t1\t1\t1\t1\t0\t10\t20\t500\t40\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t10\t20\t100\t40\t90.5\tHello\n"
            + "5\t1\t1\t1\t1\t2\t120\t22\t60\t30\t50\tyou\n"
            + "4\t1\t1\t1\t2\t0\t10\t80\t200\t40\t-1\t\n"
            + "5\t1\t1\t1\t2\t1\t10\t80\t200\t40\t100\tsecond\n";

        List<OcrLine> lines = TesseractOutput.Lines(Encoding.UTF8.GetBytes(tsv), 1000, 800);

        Assert.HasCount(2, lines);
        OcrLine first = lines[0];
        Assert.AreEqual("Hello you", first.Text);
        Assert.IsFalse(first.RightToLeft);
        Assert.HasCount(2, first.Words);
        Assert.AreEqual("Hello", first.Words[0].Text);
        Assert.AreEqual(0.905, first.Words[0].Confidence, 1e-9);
        CollectionAssert.AreEqual(new[] { new PdfPoint(10, 20), new PdfPoint(110, 20), new PdfPoint(110, 60), new PdfPoint(10, 60) }, first.Words[0].Quad);

        // The line is the words' union; its confidence weighs them by length, as "mean character confidence".
        CollectionAssert.AreEqual(new[] { new PdfPoint(10, 20), new PdfPoint(180, 20), new PdfPoint(180, 60), new PdfPoint(10, 60) }, first.Quad);
        Assert.AreEqual(((0.905 * 5) + (0.5 * 3)) / 8, first.Confidence, 1e-9);
        Assert.AreEqual("second", lines[1].Text);
        Assert.AreEqual(1, lines[1].Confidence, 1e-9);
    }

    [TestMethod]
    public void MarksRightToLeftLines()
    {
        string tsv = Header
            + "5\t1\t1\t1\t1\t1\t200\t0\t100\t40\t90\tمرحبا\n"
            + "5\t1\t1\t1\t1\t2\t100\t0\t90\t40\t90\tبالعالم\n"
            + "5\t1\t1\t1\t2\t1\t100\t50\t90\t40\t90\tHello\n"
            + "5\t1\t1\t1\t3\t1\t100\t90\t90\t40\t90\t12345\n";

        List<OcrLine> lines = TesseractOutput.Lines(Encoding.UTF8.GetBytes(tsv), 1000, 800);

        Assert.IsTrue(lines[0].RightToLeft);
        Assert.AreEqual("مرحبا بالعالم", lines[0].Text, "kept in the order Tesseract reads them, which is logical");
        Assert.IsFalse(lines[1].RightToLeft);
        Assert.IsFalse(lines[2].RightToLeft);
    }

    [TestMethod]
    public void AcceptsWindowsLineEndingsAndAMissingLastNewline()
    {
        string tsv = Header.Replace("\n", "\r\n") + "5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\tfirst\r\n5\t1\t1\t1\t2\t1\t0\t20\t10\t10\t90\tlast";
        List<OcrLine> lines = TesseractOutput.Lines(Encoding.UTF8.GetBytes(tsv), 100, 100);
        CollectionAssert.AreEqual(new[] { "first", "last" }, lines.Select(l => l.Text).ToArray());
    }

    [TestMethod]
    public void HeaderOnlyAndNoOutputAreNoText()
    {
        Assert.IsEmpty(TesseractOutput.Lines(Encoding.UTF8.GetBytes(Header), 100, 100));
        Assert.IsEmpty(TesseractOutput.Lines([], 100, 100));
    }

    [TestMethod]
    public void OutputThatIsNotTsvIsAnError()
    {
        Assert.Throws<TesseractOcrException>(() => TesseractOutput.Lines(Encoding.UTF8.GetBytes("Usage: tesseract imagename outputbase\n"), 100, 100));
        Assert.Throws<TesseractOcrException>(() => TesseractOutput.Lines([0xFF, 0xFE, 0x00, 0x01], 100, 100));
    }

    [TestMethod]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90", "too few columns")]
    [DataRow("", "empty row")]
    [DataRow("\t\t\t\t\t\t\t\t\t\t\t", "all empty")]
    [DataRow("x\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\tword", "level is not a number")]
    [DataRow("5\t1\t1\t1\t1\t1\tx\t0\t10\t10\t90\tword", "left is not a number")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t10\t10\tx\tword", "confidence is not a number")]
    [DataRow("5\t1\t1\t1\t1\t1\t99999999999999\t0\t10\t10\t90\tword", "left overflows")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t99999999999999\t10\t90\tword", "width overflows")]
    [DataRow("5\t1\t1\t1\t1\t1\t1e3\t0\t10\t10\t90\tword", "not an integer")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t0\t10\t90\tword", "no width")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t-5\t10\t90\tword", "negative width")]
    [DataRow("5\t1\t1\t1\t1\t1\t500\t0\t10\t10\t90\tword", "right of the image")]
    [DataRow("5\t1\t1\t1\t1\t1\t-50\t0\t10\t10\t90\tword", "left of the image")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t500\t10\t10\t90\tword", "below the image")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\t", "no text")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\t   ", "only spaces")]
    [DataRow("5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\t\u0001\u0002\u007f", "only control characters")]
    [DataRow("4\t1\t1\t1\t1\t0\t0\t0\t10\t10\t-1\tword", "not a word row")]
    public void DropsRowsItCannotUse(string row, string why)
    {
        List<OcrLine> lines = TesseractOutput.Lines(Encoding.UTF8.GetBytes(Header + row + "\n"), 100, 100);
        Assert.IsEmpty(lines, why);
    }

    [TestMethod]
    public void KeepsGoodRowsAroundBadOnes()
    {
        string tsv = Header + "5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\tgood\ngarbage row\n5\t1\t1\t1\t1\t2\t20\t0\t10\t10\t90\tbetter\n";
        Assert.AreEqual("good better", TesseractOutput.Lines(Encoding.UTF8.GetBytes(tsv), 100, 100).Single().Text);
    }

    [TestMethod]
    public void HoldsBoxesAndConfidenceToTheirRanges()
    {
        string tsv = Header
            + "5\t1\t1\t1\t1\t1\t-10\t-10\t2147483647\t2147483647\t250\thuge\n"
            + "5\t1\t1\t1\t2\t1\t10\t10\t5\t5\t-1\tnone\n"
            + "5\t1\t1\t1\t3\t1\t10\t10\t5\t5\tNaN\tnan\n"
            + "5\t1\t1\t1\t4\t1\t10\t10\t5\t5\tInfinity\tinf\n"
            + "5\t1\t1\t1\t5\t1\t10\t10\t5\t5\t-Infinity\tminf\n";

        List<OcrLine> lines = TesseractOutput.Lines(Encoding.UTF8.GetBytes(tsv), 100, 50);

        CollectionAssert.AreEqual(new[] { new PdfPoint(0, 0), new PdfPoint(100, 0), new PdfPoint(100, 50), new PdfPoint(0, 50) }, lines[0].Quad);
        Assert.AreEqual(1, lines[0].Confidence, "over 100");
        Assert.AreEqual(0, lines[1].Confidence, "no confidence");
        Assert.AreEqual(0, lines[2].Confidence, "NaN");
        Assert.AreEqual(1, lines[3].Confidence, "infinity");
        Assert.AreEqual(0, lines[4].Confidence, "minus infinity");
        foreach (OcrLine line in lines) Assert.IsTrue(line.Confidence is >= 0 and <= 1);
    }

    [TestMethod]
    public void HostileTextStaysInert()
    {
        // TSV has no escaping: a tab in the text must not shift columns, and the bytes need not be text at all.
        byte[] invalid = [(byte)'a', 0xC3, 0x28, 0xFF, (byte)'b'];
        byte[] start = Encoding.UTF8.GetBytes(Header + "5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90\tone\ttwo\tthree\n5\t1\t1\t1\t2\t1\t0\t20\t10\t10\t90\t");
        byte[] end = Encoding.UTF8.GetBytes("\n5\t1\t1\t1\t3\t1\t0\t40\t10\t10\t90\t<script>'; DROP TABLE\u0000\u001B[31m $(id) \\ %s {0}\n");
        byte[] all = [.. start, .. invalid, .. end];

        List<OcrLine> lines = TesseractOutput.Lines(all, 100, 100);

        Assert.HasCount(3, lines);
        Assert.AreEqual("onetwothree", lines[0].Text, "control characters are dropped");
        StringAssert.StartsWith(lines[1].Text, "a");
        StringAssert.EndsWith(lines[1].Text, "b");
        StringAssert.Contains(lines[1].Text, "\uFFFD");
        Assert.AreEqual("<script>'; DROP TABLE[31m $(id) \\ %s {0}", lines[2].Text);
    }

    [TestMethod]
    public void RandomBytesNeverThrowAnythingElse()
    {
        var random = new Random(20261105);
        for (int i = 0; i < 300; i++)
        {
            var bytes = new byte[random.Next(0, 2000)];
            random.NextBytes(bytes);
            byte[] tsv = i % 2 == 0 ? bytes : [.. Encoding.UTF8.GetBytes(Header), .. bytes];
            try
            {
                foreach (OcrLine line in TesseractOutput.Lines(tsv, 100, 100))
                {
                    Assert.IsTrue(line.Confidence is >= 0 and <= 1);
                    Assert.IsTrue(line.Quad.All(p => p.X is >= 0 and <= 100 && p.Y is >= 0 and <= 100));
                }
            }
            catch (TesseractOcrException)
            {
                // The documented answer to output that is not TSV.
            }
        }
    }

    [TestMethod]
    public void MutatedRowsNeverThrow()
    {
        const string Good = "5\t1\t1\t1\t1\t1\t10\t10\t10\t10\t90\tword\n";
        var random = new Random(7);
        string[] values = ["", "-1", "2147483648", "-2147483649", "99999999999999999999", "NaN", "1e308", "\t", "\n", "\r", " ", "é", "\uFFFD"];
        for (int i = 0; i < 1000; i++)
        {
            string[] fields = Good.TrimEnd('\n').Split('\t');
            for (int k = random.Next(1, 4); k > 0; k--) fields[random.Next(fields.Length)] = values[random.Next(values.Length)];
            foreach (OcrLine line in TesseractOutput.Lines(Encoding.UTF8.GetBytes(Header + string.Join("\t", fields) + "\n"), 100, 100))
            {
                Assert.IsTrue(line.Quad.All(p => p.X is >= 0 and <= 100 && p.Y is >= 0 and <= 100));
            }
        }
    }

    [TestMethod]
    public void ReadsTheVersion()
    {
        Assert.AreEqual("5.5.0", TesseractOutput.Version("tesseract 5.5.0\n leptonica-1.85.0\n", "x"));
        Assert.AreEqual("5.3.0.20221214", TesseractOutput.Version("tesseract v5.3.0.20221214\n", "x"));
        Assert.AreEqual("4.1.1", TesseractOutput.Version("\nWarning\ntesseract 4.1.1\n", "x"));
    }

    [TestMethod]
    [DataRow("tesseract 3.05.02\n")]
    [DataRow("something else 5.5.0\n")]
    [DataRow("tesseract five\n")]
    [DataRow("tesseract 5\n")]
    [DataRow("")]
    public void RefusesAnythingButARecentTesseract(string answer)
    {
        var e = Assert.Throws<TesseractOcrException>(() => TesseractOutput.Version(answer, "/bin/x"));
        StringAssert.Contains(e.Message, "/bin/x");
    }

    [TestMethod]
    public void ReadsTheLanguageList()
    {
        (string folder, HashSet<string> languages) = TesseractOutput.Languages("Warning\nList of available languages in \"/usr/share/tessdata/\" (3):\neng\nscript/Latin\nara\r\n../bad\n");
        Assert.AreEqual("/usr/share/tessdata/", folder);
        CollectionAssert.AreEquivalent(new[] { "eng", "script/Latin", "ara" }, languages.ToArray());

        (string none, HashSet<string> older) = TesseractOutput.Languages("List of available languages (1):\neng\n");
        Assert.AreEqual("", none);
        CollectionAssert.AreEqual(new[] { "eng" }, older.ToArray());
        Assert.IsEmpty(TesseractOutput.Languages("Error opening data file\n").Languages);
    }

    [TestMethod]
    public void FindsTheExecutableOnThePathOnly()
    {
        using var folder = new Folder();
        string file = Path.Combine(folder.Data, "tesseract");
        File.WriteAllText(file, "");
        File.WriteAllText(file + ".exe", "");

        string separator = Path.PathSeparator.ToString();
        Assert.AreEqual(file, TesseractArguments.FindOnPath(string.Join(separator, "\"" + folder.Parent + "\"", folder.Data), windows: false));
        Assert.AreEqual(file + ".exe", TesseractArguments.FindOnPath(folder.Data, windows: true));
        Assert.IsNull(TesseractArguments.FindOnPath(folder.Parent, windows: false));
        Assert.IsNull(TesseractArguments.FindOnPath(null, windows: false));
        Assert.IsNull(TesseractArguments.FindOnPath("", windows: false));

        // Relative entries are the current folder in disguise: skipped, even with a tesseract there.
        string relative = Path.Combine("..", new DirectoryInfo(folder.Data).Name);
        string before = Environment.CurrentDirectory;
        Assert.IsNull(TesseractArguments.FindOnPath(string.Join(separator, ".", "", relative, "  "), windows: false), before);
    }

    [TestMethod]
    public void MissingExecutableSaysHowToGetIt()
    {
        string nowhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tesseract");
        var e = Assert.Throws<FileNotFoundException>(() => TesseractOcrEngine.Load(new TesseractOcrEngineOptions { ExecutablePath = nowhere }));
        StringAssert.Contains(e.Message, nowhere);
        StringAssert.Contains(e.Message, "apt install tesseract-ocr");

        var path = Assert.Throws<FileNotFoundException>(() => TesseractArguments.ResolveExecutable(null, null, windows: false));
        StringAssert.Contains(path.Message, "PATH");
        StringAssert.Contains(path.Message, "ExecutablePath");
    }

    [TestMethod]
    [DataRow("tesseract.bat")]
    [DataRow("C:\\tools\\tesseract.CMD")]
    public void RefusesBatchFiles(string file) =>
        Assert.Throws<ArgumentException>(() => TesseractArguments.ResolveExecutable(file, null, windows: true));

    [TestMethod]
    public void AFileThatCannotRunIsAClearError()
    {
        using var folder = new Folder();
        string text = Path.Combine(folder.Data, "not-a-program.exe");
        File.WriteAllText(text, "just text");
        var e = Assert.Throws<TesseractOcrException>(() => TesseractOcrEngine.Load(new TesseractOcrEngineOptions { ExecutablePath = text, TessdataPath = folder.Data }));
        StringAssert.Contains(e.Message, "did not start");
    }

    [TestMethod]
    public void MissingDataFolderIsAClearError()
    {
        string nowhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<DirectoryNotFoundException>(() => TesseractOcrEngine.Load(new TesseractOcrEngineOptions { ExecutablePath = Fake(), TessdataPath = nowhere }));
        StringAssert.Contains(e.Message, nowhere);
        StringAssert.Contains(e.Message, "tessdata_fast");
    }

    [TestMethod]
    public void MissingLanguageSaysWhereToGetIt()
    {
        using var folder = new Folder();
        var e = Assert.Throws<FileNotFoundException>(() => folder.Engine(["ara"]));
        StringAssert.Contains(e.Message, "'ara'");
        StringAssert.Contains(e.Message, folder.Data);
        StringAssert.Contains(e.Message, "tessdata_fast");
        StringAssert.Contains(e.Message, "apt install");
        Assert.AreEqual("ara.traineddata", e.FileName);

        var part = Assert.Throws<FileNotFoundException>(() => folder.Engine(["eng+ara"]));
        StringAssert.Contains(part.Message, "'ara'");
    }

    [TestMethod]
    public void MapsTheScriptsToLanguages()
    {
        using var folder = new Folder(languages: ["eng", "ara"]);
        using TesseractOcrEngine engine = folder.Engine(["latin", "arabic", "LATIN"]);
        Assert.AreEqual("5.9.1", engine.Version);
        CollectionAssert.AreEquivalent(new[] { "eng", "ara" }, engine.InstalledLanguages.ToArray());

        engine.Recognize(Small, new OcrRequest { Language = "arabic" }, default);
        CollectionAssert.Contains(File.ReadAllLines(folder.Recorded("args").Single()), "ara");
    }

    [TestMethod]
    public void TakesAMapOfItsOwn()
    {
        using var folder = new Folder(languages: ["eng", "fra"]);
        using TesseractOcrEngine engine = TesseractOcrEngine.Load(new TesseractOcrEngineOptions
        {
            ExecutablePath = Fake(),
            TessdataPath = folder.Data,
            LanguageMap = new Dictionary<string, string> { ["latin"] = "eng+fra" },
        });
        engine.Recognize(Small, new OcrRequest(), default);
        CollectionAssert.Contains(File.ReadAllLines(folder.Recorded("args").Single()), "eng+fra");
    }

    [TestMethod]
    public void RequestsAreChecked()
    {
        using var folder = new Folder();
        using TesseractOcrEngine engine = folder.Engine();
        var missing = Assert.Throws<ArgumentException>(() => engine.Recognize(Small, new OcrRequest { Language = "deu" }, default));
        StringAssert.Contains(missing.Message, "'deu'");
        foreach (string hostile in new[] { "../../etc/passwd", "-c", "eng --tessdata-dir /tmp", "eng+../x" })
        {
            Assert.Throws<ArgumentException>(() => engine.Recognize(Small, new OcrRequest { Language = hostile }, default), hostile);
        }

        Assert.IsEmpty(folder.Recorded("args"), "no process started for a request refused");
    }

    [TestMethod]
    [DataRow(0, null, 0, 1, 60, "page segmentation mode 0")]
    [DataRow(2, null, 0, 1, 60, "page segmentation mode 2")]
    [DataRow(14, null, 0, 1, 60, "page segmentation mode 14")]
    [DataRow(3, 0, 0, 1, 60, "no resolution")]
    [DataRow(3, null, -1, 1, 60, "negative threads")]
    [DataRow(3, null, 0, 0, 60, "no processes")]
    [DataRow(3, null, 0, 1, 0, "no time")]
    public void SettingsAreChecked(int mode, int? dpi, int threads, int processes, int seconds, string why) =>
        Assert.Throws<ArgumentException>(
            () => TesseractOcrEngine.Load(new TesseractOcrEngineOptions
            {
                ExecutablePath = Fake(),
                PageSegmentationMode = mode,
                Dpi = dpi,
                ThreadsPerProcess = threads,
                MaxConcurrentProcesses = processes,
                Timeout = TimeSpan.FromSeconds(seconds),
            }),
            why);

    [TestMethod]
    public void RefusesAnImageTesseractWouldNot()
    {
        using var folder = new Folder();
        using TesseractOcrEngine engine = folder.Engine();
        Assert.Throws<ArgumentException>(() => engine.Recognize(new OcrBitmap(1_000_001, 1, new byte[1_000_001]), new OcrRequest { Language = "eng" }, default));
        Assert.IsEmpty(folder.Recorded("args"));
    }

    [TestMethod]
    public void ReadsTheOutputOfAProcess()
    {
        using var folder = new Folder();
        folder.Settings("tsv=" + folder.Tsv(Header + "5\t1\t1\t1\t1\t1\t1\t0\t2\t2\t80\tword\n"));
        using TesseractOcrEngine engine = folder.Engine();

        OcrImageResult result = engine.Recognize(Small, new OcrRequest { Language = "eng" }, default);

        Assert.AreEqual("word", result.Text);
        Assert.AreEqual(0.8, result.Lines[0].Confidence, 1e-9);
    }

    [TestMethod]
    public void AFailureCarriesTheExitCodeAndTheEndOfTheError()
    {
        using var folder = new Folder("exit=3\nstderr=Error opening data file eng.traineddata");
        using TesseractOcrEngine engine = folder.Engine();
        var e = Assert.Throws<TesseractOcrException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
        StringAssert.Contains(e.Message, "exit code 3");
        StringAssert.Contains(e.Message, "Error opening data file eng.traineddata");
    }

    [TestMethod]
    public void AnEndlessErrorStreamCostsNoMemory()
    {
        using var folder = new Folder("exit=1\nstderr=" + new string('e', 200_000) + "THE END");
        using TesseractOcrEngine engine = folder.Engine();
        var e = Assert.Throws<TesseractOcrException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
        Assert.IsLessThan(5000, e.Message.Length);
        StringAssert.EndsWith(e.Message, "THE END");
    }

    [TestMethod]
    public void AProcessThatStopsReadingDoesNotHang()
    {
        // Far larger than a pipe holds, to a process that never reads: the exit code decides.
        using var folder = new Folder("no-stdin=1\nexit=2\nstderr=could not read");
        using TesseractOcrEngine engine = folder.Engine();
        var large = new OcrBitmap(4000, 4000, new byte[16_000_000]);
        var e = Assert.Throws<TesseractOcrException>(() => engine.Recognize(large, new OcrRequest { Language = "eng" }, default));
        StringAssert.Contains(e.Message, "exit code 2");
    }

    [TestMethod]
    public void TooMuchOutputKillsTheProcess()
    {
        using var folder = new Folder("flood=1");
        using TesseractOcrEngine engine = folder.Engine(maxOutput: 1 << 20);
        var e = Assert.Throws<TesseractOcrException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
        StringAssert.Contains(e.Message, "wrote more than");
        AssertGone(folder);
    }

    [TestMethod]
    public void CutsOffOutputThatEndsJustOverTheLimit()
    {
        // The process ends by itself, having written more than allowed: never a silently cut result.
        using var folder = new Folder();
        folder.Settings("tsv=" + folder.Tsv(Header + new string('x', 1 << 16)));
        using TesseractOcrEngine engine = folder.Engine(maxOutput: 1 << 10);
        Assert.Throws<TesseractOcrException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
    }

    [TestMethod]
    public void ATimeoutKillsTheProcess()
    {
        using var folder = new Folder("sleep=120000");
        using TesseractOcrEngine engine = folder.Engine(timeout: TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
        Assert.IsLessThan(60, clock.Elapsed.TotalSeconds);
        AssertGone(folder);
    }

    [TestMethod]
    public void CancellingKillsTheProcess()
    {
        using var folder = new Folder("sleep=120000");
        using TesseractOcrEngine engine = folder.Engine();
        using var cancel = new CancellationTokenSource();
        Task canceller = Task.Run(async () =>
        {
            // Once the process is up, so that it really is cancelled in flight.
            while (folder.Recorded("pid").Length == 0) await Task.Delay(20).ConfigureAwait(false);
            cancel.Cancel();
        });

        var clock = Stopwatch.StartNew();
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, cancel.Token));
        Assert.IsLessThan(60, clock.Elapsed.TotalSeconds);
        canceller.GetAwaiter().GetResult();
        AssertGone(folder);
    }

    [TestMethod]
    public void AlreadyCancelledStartsNothing()
    {
        using var folder = new Folder();
        using TesseractOcrEngine engine = folder.Engine();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, cancel.Token));
        Assert.IsEmpty(folder.Recorded("args"));
    }

    [TestMethod]
    public void CancellingWhileWaitingForATurn()
    {
        using var folder = new Folder("sleep=120000");
        using TesseractOcrEngine engine = folder.Engine(processes: 1);
        using var cancel = new CancellationTokenSource();
        Task first = Task.Run(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, cancel.Token));
        while (folder.Recorded("pid").Length == 0) Thread.Sleep(20);

        using var cancelSecond = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, cancelSecond.Token));
        Assert.HasCount(1, folder.Recorded("pid"), "the second call never started a process");

        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => first.GetAwaiter().GetResult());
    }

    [TestMethod]
    public void IsSafeFromSeveralThreadsAndHoldsItsLimit()
    {
        const int Calls = 12, Limit = 2;
        using var folder = new Folder("sleep=150");
        folder.Settings("sleep=150\ntsv=" + folder.Tsv(Header + "5\t1\t1\t1\t1\t1\t1\t0\t2\t2\t80\tword\n"));
        using TesseractOcrEngine engine = folder.Engine(processes: Limit);

        string[] texts = new string[Calls];
        Parallel.For(0, Calls, new ParallelOptions { MaxDegreeOfParallelism = Calls }, i => texts[i] = engine.Recognize(Small, new OcrRequest { Language = "eng" }, default).Text);

        Assert.IsTrue(texts.All(t => t == "word"));
        (long Begin, long End)[] runs = [.. folder.Recorded("times").Select(f => File.ReadAllLines(f)).Select(l => (long.Parse(l[0], System.Globalization.CultureInfo.InvariantCulture), long.Parse(l[1], System.Globalization.CultureInfo.InvariantCulture)))];
        Assert.HasCount(Calls, runs);
        foreach ((long begin, long _) in runs)
        {
            int running = runs.Count(r => r.Begin <= begin && begin < r.End);
            Assert.IsLessThanOrEqualTo(Limit, running);
        }
    }

    [TestMethod]
    public void AfterDisposeItRefuses()
    {
        using var folder = new Folder();
        TesseractOcrEngine engine = folder.Engine();
        engine.Dispose();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.Recognize(Small, new OcrRequest { Language = "eng" }, default));
    }

    [TestMethod]
    public void MakesAScanSearchableThroughThePipeline()
    {
        using var folder = new Folder();
        folder.Settings("tsv=" + folder.Tsv(Header + "5\t1\t1\t1\t1\t1\t50\t60\t100\t20\t88\tSearchable\n5\t1\t1\t1\t1\t2\t160\t60\t30\t20\t92\ttext\n"));
        using TesseractOcrEngine engine = folder.Engine();
        PdfDocument doc = TesseractRealTests.Scan(new OcrBitmap(400, 300, Enumerable.Repeat((byte)255, 120000).ToArray()), 400, 300);

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng"], MaxDegreeOfParallelism = 1 }).MakeSearchable(doc);

        Assert.AreEqual(PdfOcrPageStatus.Done, report.Pages[0].Status);
        Assert.AreEqual("Searchable text", report.Text);
        StringAssert.Contains(PdfDocument.Open(doc.ToArray()).Pages[0].ExtractText(), "Searchable");
    }

    [TestMethod]
    public void PagesScannedSidewaysAreNotDetected()
    {
        // Tesseract's boxes are axis aligned, so PdfOcr sees every line running left to right and
        // never looks for another orientation: one read, as the documentation says.
        using var folder = new Folder();
        folder.Settings("tsv=" + folder.Tsv(Header + "5\t1\t1\t1\t1\t1\t50\t60\t100\t20\t88\tword\n"));
        using TesseractOcrEngine engine = folder.Engine();
        PdfDocument doc = TesseractRealTests.Scan(new OcrBitmap(400, 300, Enumerable.Repeat((byte)255, 120000).ToArray()), 400, 300);

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng"], DetectOrientation = true }).Recognize(doc);

        Assert.AreEqual(0, report.Pages[0].Orientation);
        Assert.HasCount(1, folder.Recorded("args"));
    }

    [TestMethod]
    public void AFailedImageIsAFailedPageAndCancellingEndsTheRun()
    {
        using var folder = new Folder("exit=1\nstderr=broken");
        using TesseractOcrEngine engine = folder.Engine();
        PdfDocument doc = TesseractRealTests.Scan(new OcrBitmap(40, 30, new byte[1200]), 40, 30);

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng"] }).Recognize(doc);
        Assert.AreEqual(PdfOcrPageStatus.Failed, report.Pages[0].Status);
        StringAssert.Contains(report.Pages[0].Reason!, "broken");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng"] }).Recognize(doc, cancel.Token));
    }

    /// <summary>Every process the stand-in recorded has ended, allowing for a busy machine.</summary>
    private static void AssertGone(Folder folder)
    {
        foreach (string file in folder.Recorded("pid"))
        {
            int pid = int.Parse(File.ReadAllText(file), System.Globalization.CultureInfo.InvariantCulture);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 30)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (process.HasExited) break;
                }
                catch (ArgumentException)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            Assert.IsLessThan(30, clock.Elapsed.TotalSeconds, $"process {pid} is still running");
        }
    }
}

/// <summary>
/// The engine against a real Tesseract: the executable <c>PDFNETSTACK_TESSERACT</c> names, else the
/// one on the PATH, with the data in <c>PDFNETSTACK_TESSDATA</c> or wherever it looks by itself. These
/// are inconclusive without it, unless <c>PDFNETSTACK_REQUIRE_TESSERACT</c> is set, as in CI, where
/// a missing Tesseract must fail rather than pass quietly. The numbers go to the test log.
/// </summary>
[TestClass]
public sealed class TesseractRealTests
{
    private const string Category = "Tesseract";

    private const string Arabic = "مرحبا بالعالم، هذا نص عربي";

    private static readonly string[] Expected =
    [
        "Invoice 2026-114",
        "Total amount due: 1,250.00 EUR by 30 November",
        "The quick brown fox jumps over the lazy dog.",
    ];

    public TestContext TestContext { get; set; } = null!;

    private static TesseractOcrEngineOptions Options(string[] languages, string? tessdata = null, int processes = 4)
    {
        string? executable = Environment.GetEnvironmentVariable("PDFNETSTACK_TESSERACT");
        string? data = tessdata ?? Environment.GetEnvironmentVariable("PDFNETSTACK_TESSDATA");
        return new TesseractOcrEngineOptions
        {
            ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable,
            TessdataPath = string.IsNullOrEmpty(data) ? null : data,
            Languages = languages,
            MaxConcurrentProcesses = processes,
        };
    }

    private static TesseractOcrEngine Engine(params string[] languages)
    {
        try
        {
            return TesseractOcrEngine.Load(Options(languages));
        }
        catch (FileNotFoundException e) when (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFNETSTACK_REQUIRE_TESSERACT")))
        {
            Assert.Inconclusive(e.Message);
            throw;
        }
    }

    /// <summary>A page whose scan is the image, at one point per pixel.</summary>
    internal static PdfDocument Scan(OcrBitmap bitmap, double width, double height)
    {
        PdfDocument document = PdfDocument.Create();
        PdfImage image = PdfImage.Load(Imaging.PngWriter.Write(bitmap.Width, bitmap.Height, 1, bitmap.Pixels));
        using (PdfGraphics g = document.Pages.Add(PdfRectangle.FromSize(width, height)).CreateGraphics()) g.DrawImage(image, 0, 0, width, height);
        return PdfDocument.Open(document.ToArray(new PdfSaveOptions { Optimize = false }));
    }

    /// <summary>The fixture page, rendered from known text at 200 dpi by tools/make_ocr_fixtures.py.</summary>
    private static OcrBitmap FixturePage()
    {
        PdfImage image = PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "page.png"));
        PdfDocument document = PdfDocument.Create();
        using (PdfGraphics g = document.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics()) g.DrawImage(image, 0, 0, 420, 595);
        return OcrBitmap.FromPdfBitmap(PdfDocument.Open(document.ToArray()).Pages[0].GetImages()[0].Decode()!);
    }

    /// <summary>Edits needed to get from one text to another, per character of the expected one.</summary>
    private static double ErrorRate(string expected, string actual)
    {
        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
        for (int j = 0; j <= actual.Length; j++) previous[j] = j;
        for (int i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= actual.Length; j++)
            {
                int change = previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1);
                current[j] = Math.Min(change, Math.Min(previous[j], current[j - 1]) + 1);
            }

            (previous, current) = (current, previous);
        }

        return (double)previous[actual.Length] / expected.Length;
    }

    /// <summary>The best any line of the result does against one expected line.</summary>
    private static double BestLine(OcrImageResult result, string expected) => result.Lines.Count == 0 ? 1 : result.Lines.Min(l => ErrorRate(expected, l.Text));

    [TestMethod]
    [TestCategory(Category)]
    public void ReadsLatinWithWordBoxes()
    {
        using TesseractOcrEngine engine = Engine("eng");
        OcrBitmap page = FixturePage();
        var clock = Stopwatch.StartNew();
        OcrImageResult result = engine.Recognize(page, new OcrRequest { Language = "eng" }, default);
        TestContext.WriteLine($"Tesseract {engine.Version}, eng, {page.Width} by {page.Height}: {clock.ElapsedMilliseconds} ms\n{result.Text}");

        foreach (string line in Expected) Assert.IsLessThan(0.1, BestLine(result, line), $"'{line}' in:\n{result.Text}");
        foreach (OcrWord word in result.Lines.SelectMany(l => l.Words))
        {
            Assert.IsTrue(word.Confidence is > 0 and <= 1, word.Text);
            Assert.IsTrue(word.Quad[0].X >= 0 && word.Quad[2].X <= page.Width && word.Quad[0].Y >= 0 && word.Quad[2].Y <= page.Height, word.Text);
            Assert.IsTrue(word.Quad[2].X > word.Quad[0].X && word.Quad[2].Y > word.Quad[0].Y, word.Text);
        }

        // Lines come top to bottom, and each line's words left to right.
        double[] tops = [.. result.Lines.Select(l => l.Quad[0].Y)];
        CollectionAssert.AreEqual(tops.OrderBy(t => t).ToArray(), tops);
        foreach (OcrLine line in result.Lines.Where(l => !l.RightToLeft))
        {
            double[] lefts = [.. line.Words.Select(w => w.Quad[0].X)];
            CollectionAssert.AreEqual(lefts.OrderBy(x => x).ToArray(), lefts, line.Text);
        }
    }

    [TestMethod]
    [TestCategory(Category)]
    public void ReadsArabicInLogicalOrder()
    {
        using TesseractOcrEngine engine = Engine("ara");
        OcrImageResult result = engine.Recognize(FixturePage(), new OcrRequest { Language = "ara" }, default);
        TestContext.WriteLine($"Tesseract {engine.Version}, ara:\n{result.Text}");

        OcrLine best = result.Lines.OrderBy(l => ErrorRate(Arabic, l.Text)).First();
        TestContext.WriteLine($"Best line, error rate {ErrorRate(Arabic, best.Text):F3}: {best.Text}");
        Assert.IsTrue(best.RightToLeft, best.Text);
        Assert.IsLessThan(0.5, ErrorRate(Arabic, best.Text), best.Text);
    }

    [TestMethod]
    [TestCategory(Category)]
    public void MakesAScanSearchableWithCombinedLanguages()
    {
        // Tesseract lays out each language on its own, so a page in two scripts is one request for both.
        using TesseractOcrEngine engine = Engine("eng+ara");
        OcrBitmap page = FixturePage();
        PdfDocument document = Scan(page, 420, 595);

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng+ara"] }).MakeSearchable(document);

        Assert.AreEqual(PdfOcrPageStatus.Done, report.Pages[0].Status, report.Pages[0].Reason);
        string text = PdfDocument.Open(document.ToArray()).Pages[0].ExtractText();
        TestContext.WriteLine(text);
        StringAssert.Contains(text, "Invoice");
        StringAssert.Contains(text, "quick");

        // The heading sits at the top of the page, in page space.
        OcrLine heading = report.Pages[0].Lines.OrderBy(l => ErrorRate(Expected[0], l.Text)).First();
        Assert.IsGreaterThan(500, heading.Quad[0].Y);
    }

    [TestMethod]
    [TestCategory(Category)]
    public void DataFolderWithSpacesWorks()
    {
        using TesseractOcrEngine probe = Engine("eng");
        string? source = Environment.GetEnvironmentVariable("PDFNETSTACK_TESSDATA");
        if (string.IsNullOrEmpty(source)) source = probe.DataFolder;

        string parent = Path.Combine(Path.GetTempPath(), "pdfnetstack tess " + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(parent, "tess data");
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(source!, "eng.traineddata"), Path.Combine(folder, "eng.traineddata"));
            using TesseractOcrEngine engine = TesseractOcrEngine.Load(Options(["eng"], folder));
            OcrImageResult result = engine.Recognize(FixturePage(), new OcrRequest { Language = "eng" }, default);
            StringAssert.Contains(result.Text, "Invoice");
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [TestCategory(Category)]
    public void MissingLanguageDataIsAClearError()
    {
        using TesseractOcrEngine engine = Engine("eng");
        var e = Assert.Throws<FileNotFoundException>(() => TesseractOcrEngine.Load(Options(["zzz"])));
        StringAssert.Contains(e.Message, "'zzz'");
    }

    [TestMethod]
    [TestCategory(Category)]
    public void CancellingStopsARealRun()
    {
        using TesseractOcrEngine engine = Engine("eng");

        // The page repeated in a grid, so one read takes long enough to be cancelled in flight.
        OcrBitmap page = FixturePage();
        const int Across = 3, Down = 3;
        var pixels = new byte[page.Pixels.Length * Across * Down];
        for (int y = 0; y < page.Height * Down; y++)
        {
            for (int x = 0; x < Across; x++) Array.Copy(page.Pixels, (y % page.Height) * page.Width, pixels, ((long)y * page.Width * Across) + (x * page.Width), page.Width);
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.Recognize(new OcrBitmap(page.Width * Across, page.Height * Down, pixels), new OcrRequest { Language = "eng" }, cancel.Token));
        TestContext.WriteLine($"Cancelled after {clock.ElapsedMilliseconds} ms");
        Assert.IsLessThan(30, clock.Elapsed.TotalSeconds);
    }

    [TestMethod]
    [TestCategory(Category)]
    public void IsSafeFromSeveralThreads()
    {
        using TesseractOcrEngine engine = Engine("eng");
        OcrBitmap page = FixturePage();
        string[] texts = new string[6];
        var clock = Stopwatch.StartNew();
        Parallel.For(0, texts.Length, new ParallelOptions { MaxDegreeOfParallelism = texts.Length }, i => texts[i] = engine.Recognize(page, new OcrRequest { Language = "eng" }, default).Text);
        TestContext.WriteLine($"{texts.Length} reads on 4 processes: {clock.ElapsedMilliseconds} ms");
        Assert.IsTrue(texts.All(t => t == texts[0] && t.Contains("Invoice")));
    }

    [TestMethod]
    [TestCategory(Category)]
    public void ReportsWhatHappensToPagesScannedSideways()
    {
        // Tesseract's TSV says nothing about which way text runs, so a turned page is not detected.
        // Recorded rather than asserted, as what it reads from a turned page differs by version.
        using TesseractOcrEngine engine = Engine("eng");
        OcrBitmap page = FixturePage();
        var turned = new byte[page.Pixels.Length];
        for (int y = 0; y < page.Height; y++)
        {
            for (int x = 0; x < page.Width; x++) turned[(x * page.Height) + (page.Height - 1 - y)] = page.Pixels[(y * page.Width) + x];
        }

        PdfDocument document = Scan(new OcrBitmap(page.Height, page.Width, turned), 595, 420);
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["eng"] }).Recognize(document);

        Assert.AreEqual(PdfOcrPageStatus.Done, report.Pages[0].Status, report.Pages[0].Reason);
        TestContext.WriteLine($"Orientation found: {report.Pages[0].Orientation}\n{report.Text}");
    }

    [TestMethod]
    [TestCategory(Category)]
    public void MeasuresAgainstTheBundledEngine()
    {
        // For the numbers in the guide: both engines on the same page, on the same machine.
        string? models = Environment.GetEnvironmentVariable("PDFNETSTACK_OCR_MODELS");
        if (string.IsNullOrEmpty(models) || !Directory.Exists(models)) models = Path.Combine(AppContext.BaseDirectory, OnnxOcrEngine.PackagedModelFolder);
        if (!File.Exists(Path.Combine(models, OnnxOcrEngine.DetectionModel))) Assert.Inconclusive("The PP-OCRv5 models are not here.");

        using TesseractOcrEngine tesseract = Engine("eng");
        using OnnxOcrEngine onnx = OnnxOcrEngine.Load(models);
        OcrBitmap page = FixturePage();
        string truth = string.Join("\n", Expected);

        foreach ((string name, IOcrEngine engine, string language) in new (string, IOcrEngine, string)[] { ($"Tesseract {tesseract.Version} eng", tesseract, "eng"), ("PP-OCRv5 latin", onnx, "latin") })
        {
            double first = 0, last = 0;
            OcrImageResult result = null!;
            for (int run = 0; run < 3; run++)
            {
                var clock = Stopwatch.StartNew();
                result = engine.Recognize(page, new OcrRequest { Language = language }, default);
                if (run == 0) first = clock.Elapsed.TotalMilliseconds;
                last = clock.Elapsed.TotalMilliseconds;
            }

            double error = Expected.Average(line => BestLine(result, line));
            TestContext.WriteLine($"{name}: first read {first:F0} ms, third {last:F0} ms, mean line error rate {error:F3} over {truth.Length} characters");
        }
    }
}
