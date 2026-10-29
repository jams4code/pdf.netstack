using PdfNetStack.Drawing;
using PdfNetStack.Html;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class GifTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gif", name);

    private static PdfPageImage Placed(PdfImage image)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(image, 0, 0);
        return PdfDocument.Open(doc.ToArray()).Pages[0].GetImages().Single();
    }

    [TestMethod]
    [DataRow("photo", false)]
    [DataRow("interlaced", false)]
    [DataRow("transparent", true)]
    [DataRow("two-color", false)]
    public void MatchesPillow(string name, bool transparent)
    {
        var image = PdfImage.Load(Fixture(name + ".gif"));
        PngImage reference = PngImage.Read(File.ReadAllBytes(Fixture(name + ".png")));
        Assert.AreEqual(PdfImageFormat.Gif, image.Format);
        Assert.AreEqual(reference.Width, image.PixelWidth);
        Assert.AreEqual(reference.Height, image.PixelHeight);
        Assert.AreEqual(transparent, image.HasTransparency);

        PdfPageImage placed = Placed(image);
        Assert.AreEqual(transparent, placed.Dictionary["SMask"] is not null);
        byte[] actual = placed.Decode()!.Pixels.ToArray();
        byte[] rgba = reference.Decode();
        int opaque = 0;
        for (int i = 0; i < reference.Width * reference.Height; i++)
        {
            if (rgba[(4 * i) + 3] == 0) continue;
            opaque++;
            for (int c = 0; c < 3; c++) Assert.AreEqual(rgba[(4 * i) + c], actual[(3 * i) + c], $"{name} pixel {i}");
        }

        Assert.IsGreaterThan(reference.Width * reference.Height / 2, opaque);
    }

    [TestMethod]
    public void LoadsInHtml()
    {
        string data = Convert.ToBase64String(File.ReadAllBytes(Fixture("photo.gif")));
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf($"<p><img src=\"data:image/gif;base64,{data}\" width=97 height=61></p>", new HtmlConverterOptions { UseSystemFonts = false }));
        PdfPageImage image = doc.Pages[0].GetImages().Single();
        Assert.AreEqual(97, image.Width);
        Assert.AreEqual(61, image.Height);
    }

    [TestMethod]
    public void RejectsOrSurvivesDamagedData()
    {
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("GIF89a"u8.ToArray()));
        Assert.ThrowsExactly<FormatException>(() => GifDecoder.Read("GIF87a\x05\0\x05\0\0\0\0\x3B"u8.ToArray()));

        byte[] good = File.ReadAllBytes(Fixture("photo.gif"));
        var random = new Random(9);
        for (int round = 0; round < 500; round++)
        {
            byte[] copy = round % 2 == 0 ? good.AsSpan(0, random.Next(6, good.Length)).ToArray() : (byte[])good.Clone();
            if (round % 2 == 1)
            {
                for (int k = 0; k < 4; k++) copy[random.Next(6, copy.Length)] = (byte)random.Next(256);
            }

            try
            {
                GifImage gif = GifDecoder.Read(copy);
                Assert.HasCount(gif.Width * gif.Height, gif.Indexes);
            }
            catch (FormatException)
            {
            }
        }
    }
}
