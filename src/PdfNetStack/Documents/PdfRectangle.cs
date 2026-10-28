using System.Globalization;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>A rectangle in PDF user space units (1/72 inch), normalized so that Left &lt;= Right and Bottom &lt;= Top.</summary>
/// <param name="Left">Lower-left x.</param>
/// <param name="Bottom">Lower-left y.</param>
/// <param name="Right">Upper-right x.</param>
/// <param name="Top">Upper-right y.</param>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>ISO A4, 595 x 842 points.</summary>
    public static PdfRectangle A4 => new(0, 0, 595, 842);

    /// <summary>US Letter, 612 x 792 points.</summary>
    public static PdfRectangle Letter => new(0, 0, 612, 792);

    /// <summary>Horizontal size.</summary>
    public double Width => Right - Left;

    /// <summary>Vertical size.</summary>
    public double Height => Top - Bottom;

    /// <summary>Creates a rectangle at the origin with the given size.</summary>
    public static PdfRectangle FromSize(double width, double height) => new(0, 0, width, height);

    internal static PdfRectangle? From(PdfObject? obj, Func<PdfObject?, PdfObject?> resolve)
    {
        if (resolve(obj) is not PdfArray a || a.Count != 4) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (resolve(a[i]) is not PdfNumber n) return null;
            v[i] = n.ToDouble();
        }

        return new PdfRectangle(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    internal PdfArray ToArray() => new([Number(Left), Number(Bottom), Number(Right), Number(Top)]);

    internal static PdfNumber Number(double value) =>
        PdfNumber.Parse(System.Text.Encoding.ASCII.GetBytes(Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture)));
}
