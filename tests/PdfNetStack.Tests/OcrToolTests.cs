#if NET
using PdfNetStack.Cli;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Ocr;
using PdfNetStack.Ocr.Processing;

namespace PdfNetStack.Tests;

/// <summary>The command line OCR tool, pdfnetstack-ocr, and the pdfnetstack ocr command that runs it.</summary>
[TestClass]
public sealed class OcrToolTests
{
    private static string Scratch()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pdfnetstack-ocr-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void FindsTheOcrToolNextToItselfOrOnThePath()
    {
        string here = Scratch(), there = Scratch(), elsewhere = Scratch();
        try
        {
            string unix = Path.Combine(there, OcrTool.Name), windows = Path.Combine(there, OcrTool.Name + ".exe");
            File.WriteAllText(unix, "");
            File.WriteAllText(windows, "");
            string path = string.Join(Path.PathSeparator, elsewhere, "", $"\"{there}\"", "  ");
            Assert.AreEqual(unix, OcrTool.Find(path, here, windows: false));
            Assert.AreEqual(windows, OcrTool.Find(path, here, windows: true));
            Assert.IsNull(OcrTool.Find(elsewhere, here, windows: false));
            Assert.IsNull(OcrTool.Find(null, here, windows: false));

            // Next to the running tool comes first, as when both are unpacked into one folder.
            string beside = Path.Combine(here, OcrTool.Name);
            File.WriteAllText(beside, "");
            Assert.AreEqual(beside, OcrTool.Find(path, here, windows: false));
        }
        finally
        {
            foreach (string folder in new[] { here, there, elsewhere }) Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void RefusesBadArgumentsBeforeWritingAnything()
    {
        string folder = Scratch();
        try
        {
            string input = Path.Combine(folder, "in.pdf"), output = Path.Combine(folder, "out.pdf");
            PdfDocument doc = PdfDocument.Create();
            doc.Pages.Add(PdfRectangle.A4);
            doc.Save(input);

            Assert.AreEqual(2, OcrCli.Run([]));
            Assert.AreEqual(0, OcrCli.Run(["--help"]));
            Assert.AreEqual(2, OcrCli.Run([input]), "no output");
            Assert.AreEqual(2, OcrCli.Run([input, output, "--languages", "latin,klingon"]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--languages", ","]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--min-confidence", "1.5"]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--threads", "-1"]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--colour"]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--models"]), "an option without its value");
            Assert.AreEqual(2, OcrCli.Run([Path.Combine(folder, "missing.pdf"), output]));
            Assert.AreEqual(2, OcrCli.Run([input, output, "--models", folder]), "no models in the folder");
            Assert.IsFalse(File.Exists(output));

            // Not a PDF.
            File.WriteAllText(input, "not a pdf");
            Assert.AreEqual(2, OcrCli.Run([input, output]));
            Assert.IsFalse(File.Exists(output));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void MakesAScanSearchableWithTheModels()
    {
        string? models = Environment.GetEnvironmentVariable("PDFNETSTACK_OCR_MODELS");
        if (string.IsNullOrEmpty(models) || !Directory.Exists(models)) models = Path.Combine(AppContext.BaseDirectory, OnnxOcrEngine.PackagedModelFolder);
        if (!File.Exists(Path.Combine(models, OnnxOcrEngine.DetectionModel)))
        {
            Assert.Inconclusive("Set PDFNETSTACK_OCR_MODELS to a folder with the PP-OCRv5 models, or fetch them into src/PdfNetStack.Ocr.Models/models (tools/fetch_ocr_models.py).");
        }

        string folder = Scratch();
        try
        {
            // The fixture page scanned upside down, and a page that already has text.
            PdfImage page = PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "page.png"));
            PdfDocument decoded = PdfDocument.Create();
            using (PdfGraphics g = decoded.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics()) g.DrawImage(page, 0, 0, 420, 595);
            OcrBitmap upright = OcrBitmap.FromPdfBitmap(PdfDocument.Open(decoded.ToArray()).Pages[0].GetImages()[0].Decode()!);
            (byte[] turned, int w, int h) = ImageOps.Rotate(upright.Pixels, upright.Width, upright.Height, 2);

            PdfDocument doc = PdfDocument.Create();
            using (PdfGraphics g = doc.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics()) g.DrawImage(PdfImage.Load(Imaging.PngWriter.Write(w, h, 1, turned)), 0, 0, 420, 595);
            using (PdfGraphics g = doc.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics()) g.DrawString("Born digital", PdfStandardFont.Helvetica, 12, PdfColor.Black, 50, 50);
            string input = Path.Combine(folder, "scan.pdf"), output = Path.Combine(folder, "searchable.pdf"), text = Path.Combine(folder, "scan.txt");
            doc.Save(input);

            Assert.AreEqual(0, OcrCli.Run([input, output, "--languages", "latin,arabic", "--models", models, "--text", text, "--threads", "1"]));
            string written = File.ReadAllText(text);
            StringAssert.StartsWith(written, "Invoice 2026-114\n", "read the right way up");
            StringAssert.Contains(written, "The quick brown fox jumps over the lazy dog.");
            StringAssert.Contains(written, "مرحبا بالعالم، هذا نص عربي");

            PdfDocument searchable = PdfDocument.Open(output);
            StringAssert.Contains(searchable.Pages[0].ExtractText(), "Total amount due: 1,250.00 EUR by 30 November");
            Assert.AreEqual("Born digital", searchable.Pages[1].ExtractText().Trim(), "the page with text is left as it was");

            // A second pass finds text on every page and reads nothing.
            Assert.AreEqual(0, OcrCli.Run([output, Path.Combine(folder, "again.pdf"), "--models", models, "--json"]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
#endif
