using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Helpers shared by the BMP, TIFF and WebP tests: references, RGBA conversion and damaged input.</summary>
internal static class DecodedImages
{
    /// <summary>An RGBA PNG written by a fixture script: width, height and 4 bytes per pixel.</summary>
    public static (int Width, int Height, byte[] Rgba) Reference(string path)
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(path));
        Assert.AreEqual(6, png.ColorType, path);
        Assert.AreEqual(8, png.BitDepth, path);
        return (png.Width, png.Height, png.Decode());
    }

    /// <summary>A decoded image as 8 bit RGBA: palettes looked up, gray spread, 16 bit samples cut to their high byte.</summary>
    public static byte[] Rgba(DecodedImage image)
    {
        int w = image.Width, h = image.Height, bits = image.BitsPerComponent, colors = image.Colors;
        long stride = DecodedImage.RowBytes(w, colors, bits);
        var rgba = new byte[(long)w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long o = ((long)y * w + x) * 4;
                if (image.Palette is byte[] palette)
                {
                    int index = Sample(image.Samples, y * stride, x, bits);
                    (rgba[o], rgba[o + 1], rgba[o + 2]) = (palette[3 * index], palette[3 * index + 1], palette[3 * index + 2]);
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int v = To8(Sample(image.Samples, y * stride, x * colors + (colors == 1 ? 0 : c), bits), bits);
                        rgba[o + c] = (byte)(image.InvertedGray ? 255 - v : v);
                    }
                }

                rgba[o + 3] = image.Alpha is byte[] a ? (byte)To8(Sample(a, y * (((long)w * image.AlphaBits + 7) / 8), x, image.AlphaBits), image.AlphaBits) : (byte)255;
            }
        }

        return rgba;
    }

    /// <summary>
    /// Asserts two RGBA images agree within a tolerance per channel. Where the reference is fully
    /// transparent only the alpha is compared, since color under it is not defined.
    /// </summary>
    public static void AssertSimilar(byte[] expected, byte[] actual, int tolerance, string name)
    {
        Assert.HasCount(expected.Length, actual, name);
        for (int i = 0; i < expected.Length; i += 4)
        {
            Assert.AreEqual(expected[i + 3], actual[i + 3], $"{name}: alpha of pixel {i / 4}");
            if (expected[i + 3] == 0) continue;
            for (int c = 0; c < 3; c++)
            {
                if (Math.Abs(expected[i + c] - actual[i + c]) > tolerance) Assert.Fail($"{name}: pixel {i / 4} channel {c} is {actual[i + c]}, expected {expected[i + c]}");
            }
        }
    }

    /// <summary>
    /// The image drawn on a page, saved without the optimizer (which may code it anew) and read
    /// back: the XObject as embedded and its pixels as the reader decodes them.
    /// </summary>
    public static (PdfStream XObject, PdfBitmap Pixels, PdfDocument Document) Embedded(PdfImage image)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(image, 0, 0);
        PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Optimize = false }));
        PdfPageImage placed = back.Pages[0].GetImages().Single();
        return ((PdfStream)placed.Stream!, placed.Decode()!, back);
    }

    /// <summary>
    /// Feeds truncated and corrupted copies of good data to a decoder: each must either decode or
    /// fail with <see cref="FormatException"/>, nothing else.
    /// </summary>
    public static void Survives(byte[] good, Action<byte[]> decode, int rounds = 400, int seed = 7)
    {
        var random = new Random(seed);
        for (int round = 0; round < rounds; round++)
        {
            byte[] copy;
            if (round % 3 == 0)
            {
                copy = good.AsSpan(0, random.Next(0, good.Length)).ToArray();
            }
            else
            {
                copy = (byte[])good.Clone();
                int changes = 1 + random.Next(round % 3 == 1 ? 2 : 12);
                for (int k = 0; k < changes; k++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
            }

            try
            {
                decode(copy);
            }
            catch (FormatException)
            {
            }
        }
    }

    private static int Sample(byte[] data, long row, long index, int bits)
    {
        if (bits == 16) return data[row + 2 * index] << 8 | data[row + 2 * index + 1];
        if (bits == 8) return data[row + index];
        long bit = index * bits;
        return (data[row + bit / 8] >> (int)(8 - bits - bit % 8)) & ((1 << bits) - 1);
    }

    private static int To8(int v, int bits) => bits switch { 16 => v >> 8, 8 => v, _ => v * 255 / ((1 << bits) - 1) };
}
