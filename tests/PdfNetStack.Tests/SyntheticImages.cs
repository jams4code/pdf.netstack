using PdfNetStack.Imaging;

namespace PdfNetStack.Tests;

/// <summary>Deterministic synthetic pictures for the lossy image tests: photographs, screenshots and line art.</summary>
internal static class SyntheticImages
{
    /// <summary>A photograph-like test card: smooth color fields, soft edges and sensor noise.</summary>
    public static Raster Photo(int width, int height, int channels, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width, v = (double)y / height;
                double r = 128 + (90 * Math.Sin((u * 7) + (v * 3))) + (30 * Math.Cos(v * 11));
                double g = 40 + (170 * u * v) + (25 * Math.Sin(x / 5.0));
                double b = 200 - (150 * v) + (20 * Math.Cos((u - v) * 9));
                double[] channel = [r, g, b, (r + g) / 3];
                for (int c = 0; c < channels; c++)
                {
                    double value = (channels == 1 ? (0.3 * r) + (0.59 * g) + (0.11 * b) : channel[c]) + ((random.NextDouble() - 0.5) * 14);
                    samples[(((y * width) + x) * channels) + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
                }
            }
        }

        return new Raster(width, height, channels, samples);
    }

    /// <summary>A screenshot-like image: a white page with dark text strokes and a flat toolbar.</summary>
    public static Raster Screenshot(int width, int height, int channels)
    {
        var samples = new byte[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = 255;
                if (y < height / 10) value = 230;
                else if ((y / 6) % 3 == 0 && (x / 4) % 5 != 0 && x > width / 12 && x < width * 11 / 12) value = 30;
                for (int c = 0; c < channels; c++) samples[(((y * width) + x) * channels) + c] = (byte)(c == 2 && y < height / 10 ? 250 : value);
            }
        }

        return new Raster(width, height, channels, samples);
    }

    /// <summary>Black and white only: a checkerboard of large cells.</summary>
    public static Raster Bilevel(int width, int height)
    {
        var samples = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) samples[(y * width) + x] = (byte)(((x / 16) + (y / 16)) % 2 == 0 ? 0 : 255);
        }

        return new Raster(width, height, 1, samples);
    }

    /// <summary>The same picture flipped upside down.</summary>
    public static Raster Flipped(Raster raster)
    {
        int stride = raster.Width * raster.Channels;
        var samples = new byte[raster.Samples.Length];
        for (int y = 0; y < raster.Height; y++) Array.Copy(raster.Samples, y * stride, samples, (raster.Height - 1 - y) * stride, stride);
        return new Raster(raster.Width, raster.Height, raster.Channels, samples);
    }

    /// <summary>Encodes with the library's own baseline encoder.</summary>
    public static byte[] Jpeg(Raster raster, int quality, bool subsample = true)
    {
        using JpegImage image = JpegEncoder.Encode(raster, quality, subsample);
        return JpegWriter.TryWrite(image, raster.Samples.Length) ?? throw new InvalidOperationException("Encoding failed.");
    }

    public static string ColorSpace(int channels) => channels switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" };
}
