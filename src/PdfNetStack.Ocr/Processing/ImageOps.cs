using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr.Processing;

/// <summary>Resampling for 8 bit gray images: what the models' pre processing needs, nothing more.</summary>
internal static class ImageOps
{
    /// <summary>Bilinear resize, sampling at pixel centers as OpenCV does.</summary>
    public static byte[] Resize(byte[] source, int width, int height, int newWidth, int newHeight)
    {
        var output = new byte[(long)newWidth * newHeight];
        double sx = (double)width / newWidth, sy = (double)height / newHeight;
        for (int y = 0; y < newHeight; y++)
        {
            double fy = Math.Max(0, ((y + 0.5) * sy) - 0.5);
            int y0 = Math.Min((int)fy, height - 1), y1 = Math.Min(y0 + 1, height - 1);
            double wy = fy - y0;
            for (int x = 0; x < newWidth; x++)
            {
                double fx = Math.Max(0, ((x + 0.5) * sx) - 0.5);
                int x0 = Math.Min((int)fx, width - 1), x1 = Math.Min(x0 + 1, width - 1);
                double wx = fx - x0;
                double top = (source[(y0 * width) + x0] * (1 - wx)) + (source[(y0 * width) + x1] * wx);
                double bottom = (source[(y1 * width) + x0] * (1 - wx)) + (source[(y1 * width) + x1] * wx);
                output[((long)y * newWidth) + x] = (byte)Math.Round((top * (1 - wy)) + (bottom * wy));
            }
        }

        return output;
    }

    /// <summary>Shrinks by averaging every source pixel a target pixel covers: no aliasing on fine scans.</summary>
    public static byte[] Shrink(byte[] source, int width, int height, int newWidth, int newHeight)
    {
        var sums = new double[(long)newWidth * newHeight];
        var weights = new double[sums.Length];
        double sx = (double)newWidth / width, sy = (double)newHeight / height;
        for (int y = 0; y < height; y++)
        {
            int ty = Math.Min(newHeight - 1, (int)(y * sy));
            for (int x = 0; x < width; x++)
            {
                long t = ((long)ty * newWidth) + Math.Min(newWidth - 1, (int)(x * sx));
                sums[t] += source[((long)y * width) + x];
                weights[t]++;
            }
        }

        var output = new byte[sums.Length];
        for (int i = 0; i < output.Length; i++) output[i] = weights[i] > 0 ? (byte)Math.Round(sums[i] / weights[i]) : (byte)255;
        return output;
    }

    /// <summary>Rotates clockwise by a multiple of 90 degrees.</summary>
    public static (byte[] Pixels, int Width, int Height) Rotate(byte[] source, int width, int height, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return (source, width, height);
        (int w, int h) = quarterTurns == 2 ? (width, height) : (height, width);
        var output = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int nx, int ny) = quarterTurns switch
                {
                    1 => (height - 1 - y, x),
                    2 => (width - 1 - x, height - 1 - y),
                    _ => (y, width - 1 - x),
                };
                output[((long)ny * w) + nx] = source[((long)y * width) + x];
            }
        }

        return (output, w, h);
    }

    /// <summary>
    /// Cuts a rotated rectangle out upright: (u, v) of the output maps to top left plus u along the
    /// top edge and v down the left edge. Detection boxes are rectangles, so the map is affine.
    /// Outside the image reads white.
    /// </summary>
    public static byte[] Crop(byte[] source, int width, int height, PdfPoint[] quad, int cropWidth, int cropHeight)
    {
        var output = new byte[(long)cropWidth * cropHeight];
        for (int v = 0; v < cropHeight; v++)
        {
            for (int u = 0; u < cropWidth; u++)
            {
                PdfPoint p = Geometry.Along(quad, (u + 0.5) / cropWidth, (v + 0.5) / cropHeight);
                output[((long)v * cropWidth) + u] = Sample(source, width, height, p.X - 0.5, p.Y - 0.5);
            }
        }

        return output;
    }

    private static byte Sample(byte[] source, int width, int height, double fx, double fy)
    {
        if (fx < -1 || fy < -1 || fx > width || fy > height) return 255;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double wx = fx - x0, wy = fy - y0;
        double At(int x, int y) => x < 0 || y < 0 || x >= width || y >= height ? 255 : source[((long)y * width) + x];
        double top = (At(x0, y0) * (1 - wx)) + (At(x0 + 1, y0) * wx);
        double bottom = (At(x0, y0 + 1) * (1 - wx)) + (At(x0 + 1, y0 + 1) * wx);
        return (byte)Math.Round((top * (1 - wy)) + (bottom * wy));
    }
}
