using System.Globalization;

namespace PdfNetStack.Drawing;

/// <summary>Color model of a <see cref="PdfColor"/>, written as the matching PDF device color space.</summary>
public enum PdfColorSpace
{
    /// <summary>DeviceGray: one component, 0 black to 1 white.</summary>
    Gray,

    /// <summary>DeviceRGB: red, green, blue, each 0 to 1.</summary>
    Rgb,

    /// <summary>DeviceCMYK: cyan, magenta, yellow, black, each 0 to 1. For print workflows.</summary>
    Cmyk,
}

/// <summary>
/// A device color with an opacity. Components are 0 to 1. Opacity below 1 is written as a
/// constant alpha graphics state.
/// </summary>
public readonly struct PdfColor : IEquatable<PdfColor>
{
    private readonly double _c0, _c1, _c2, _c3;

    private PdfColor(PdfColorSpace space, double c0, double c1, double c2, double c3, double alpha)
    {
        Space = space;
        _c0 = Unit(c0, "component");
        _c1 = Unit(c1, "component");
        _c2 = Unit(c2, "component");
        _c3 = Unit(c3, "component");
        Alpha = Unit(alpha, nameof(alpha));
    }

    /// <summary>The color model.</summary>
    public PdfColorSpace Space { get; }

    /// <summary>Opacity, 0 transparent to 1 opaque.</summary>
    public double Alpha { get; }

    /// <summary>Opaque black.</summary>
    public static PdfColor Black => Gray(0);

    /// <summary>Opaque white.</summary>
    public static PdfColor White => Gray(1);

    /// <summary>Fully transparent.</summary>
    public static PdfColor Transparent => Gray(0, 0);

    /// <summary>A gray level, 0 black to 1 white.</summary>
    public static PdfColor Gray(double level, double alpha = 1) => new(PdfColorSpace.Gray, level, 0, 0, 0, alpha);

    /// <summary>An RGB color with components 0 to 1.</summary>
    public static PdfColor Rgb(double red, double green, double blue, double alpha = 1) => new(PdfColorSpace.Rgb, red, green, blue, 0, alpha);

    /// <summary>An RGB color with components 0 to 255, as in CSS and most image tools.</summary>
    public static PdfColor FromRgb(byte red, byte green, byte blue, double alpha = 1) => Rgb(red / 255.0, green / 255.0, blue / 255.0, alpha);

    /// <summary>A CMYK color with components 0 to 1.</summary>
    public static PdfColor Cmyk(double cyan, double magenta, double yellow, double black, double alpha = 1) =>
        new(PdfColorSpace.Cmyk, cyan, magenta, yellow, black, alpha);

    /// <summary>Parses #RGB, #RGBA, #RRGGBB or #RRGGBBAA, with or without the hash.</summary>
    /// <exception cref="FormatException">The text is not a hex color.</exception>
    public static PdfColor FromHex(string hex)
    {
        Compat.ThrowIfNull(hex, nameof(hex));
        string h = hex.Length > 0 && hex[0] == '#' ? hex.Substring(1) : hex;
        if (h.Length is 3 or 4)
        {
            var expanded = new char[h.Length * 2];
            for (int i = 0; i < h.Length; i++) expanded[2 * i] = expanded[2 * i + 1] = h[i];
            h = new string(expanded);
        }

        if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
        {
            throw new FormatException("Expected a hex color such as #1E3A8A.");
        }

        if (h.Length == 6) v = (v << 8) | 0xFF;
        return FromRgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (v & 0xFF) / 255.0);
    }

    /// <summary>The same color with another opacity.</summary>
    public PdfColor WithAlpha(double alpha) => new(Space, _c0, _c1, _c2, _c3, alpha);

    /// <summary>The components in the order the color space defines: 1, 3 or 4 values.</summary>
    public double[] Components() => Space switch
    {
        PdfColorSpace.Gray => [_c0],
        PdfColorSpace.Rgb => [_c0, _c1, _c2],
        _ => [_c0, _c1, _c2, _c3],
    };

    /// <summary>The color converted to RGB (CMYK by the naive formula, without a color profile).</summary>
    public PdfColor ToRgb() => Space switch
    {
        PdfColorSpace.Gray => Rgb(_c0, _c0, _c0, Alpha),
        PdfColorSpace.Rgb => this,
        _ => Rgb((1 - _c0) * (1 - _c3), (1 - _c1) * (1 - _c3), (1 - _c2) * (1 - _c3), Alpha),
    };

    /// <inheritdoc/>
    public bool Equals(PdfColor other) =>
        Space == other.Space && _c0 == other._c0 && _c1 == other._c1 && _c2 == other._c2 && _c3 == other._c3 && Alpha == other.Alpha;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfColor c && Equals(c);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        ((int)Space * 397) ^ _c0.GetHashCode() ^ (_c1.GetHashCode() * 31) ^ (_c2.GetHashCode() * 17) ^ (_c3.GetHashCode() * 7) ^ Alpha.GetHashCode();

    /// <summary>Equality.</summary>
    public static bool operator ==(PdfColor left, PdfColor right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(PdfColor left, PdfColor right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        Space + "(" + string.Join(", ", Components().Select(c => c.ToString("0.###", CultureInfo.InvariantCulture))) + ")" +
        (Alpha < 1 ? " alpha " + Alpha.ToString("0.###", CultureInfo.InvariantCulture) : "");

    private static double Unit(double value, string name)
    {
        if (double.IsNaN(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(name, "Color values range from 0 to 1.");
        return value;
    }
}
