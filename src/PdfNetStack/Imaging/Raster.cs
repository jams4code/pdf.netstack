namespace PdfNetStack.Imaging;

/// <summary>Decoded 8 bit samples, interleaved, rows top to bottom with no padding.</summary>
internal sealed class Raster
{
    public Raster(int width, int height, int channels, byte[] samples)
    {
        if (samples.Length != (long)width * height * channels) throw new ArgumentException("Sample count does not match the size.", nameof(samples));
        Width = width;
        Height = height;
        Channels = channels;
        Samples = samples;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Samples per pixel: 1 gray, 3 RGB, 4 CMYK.</summary>
    public int Channels { get; }

    public byte[] Samples { get; }
}
