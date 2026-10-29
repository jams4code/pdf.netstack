using PdfNetStack.Drawing;
using System.Globalization;

namespace PdfNetStack.Html.Css;

/// <summary>An sRGB color with components from 0 to 1, or the currentcolor keyword.</summary>
internal readonly record struct CssColor(double R, double G, double B, double A, bool IsCurrentColor = false)
{
    public static readonly CssColor Black = new(0, 0, 0, 1);
    public static readonly CssColor Transparent = new(0, 0, 0, 0);
    public static readonly CssColor CurrentColor = new(0, 0, 0, 1, true);

    internal static readonly Dictionary<string, CssColor> Named = BuildNamed();

    public override string ToString() => IsCurrentColor
        ? "currentcolor"
        : string.Format(CultureInfo.InvariantCulture, "rgba({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", R * 255, G * 255, B * 255, A);

    /// <summary>Parses one component value as a color.</summary>
    public static CssColor? Parse(CssValue value)
    {
        if (value.IsToken)
        {
            CssToken t = value.Token;
            if (t.Type == CssTokenType.Ident)
            {
                string name = t.Value.ToLowerInvariant();
                if (name == "transparent") return Transparent;
                if (name == "currentcolor") return CurrentColor;
                return Named.TryGetValue(name, out CssColor named) ? named : null;
            }

            return t.Type == CssTokenType.Hash ? Hex(t.Value) : null;
        }

        return value.Function switch
        {
            "rgb" or "rgba" => Rgb(Arguments(value.Children)),
            "hsl" or "hsla" => Hsl(Arguments(value.Children)),
            "hwb" => Hwb(Arguments(value.Children)),
            "lab" => Lab(Arguments(value.Children), oklab: false, polar: false),
            "lch" => Lab(Arguments(value.Children), oklab: false, polar: true),
            "oklab" => Lab(Arguments(value.Children), oklab: true, polar: false),
            "oklch" => Lab(Arguments(value.Children), oklab: true, polar: true),
            _ => null,
        };
    }

    public static CssColor? Parse(string text)
    {
        List<CssValue> values = CssParser.ComponentValues(CssTokenizer.Tokenize(text)).Where(v => !v.IsWhitespace).ToList();
        return values.Count == 1 ? Parse(values[0]) : null;
    }

    private static Dictionary<string, CssColor> BuildNamed()
    {
        var table = new Dictionary<string, CssColor>(NamedColors.Rgb.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, (byte R, byte G, byte B)> c in NamedColors.Rgb) table[c.Key] = new CssColor(c.Value.R / 255.0, c.Value.G / 255.0, c.Value.B / 255.0, 1);
        return table;
    }

    private static CssColor? Hex(string hex)
    {
        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(Uri.IsHexDigit)) return null;
        bool shortForm = hex.Length <= 4;
        int Digit(int i) => shortForm
            ? Convert.ToInt32(new string(hex[i], 2), 16)
            : Convert.ToInt32(hex.Substring(i * 2, 2), 16);
        int count = shortForm ? hex.Length : hex.Length / 2;
        return new CssColor(Digit(0) / 255.0, Digit(1) / 255.0, Digit(2) / 255.0, count == 4 ? Digit(3) / 255.0 : 1);
    }

    /// <summary>The components and the alpha, from either legacy comma syntax or modern space syntax with a slash.</summary>
    private static (List<CssToken> Components, CssToken? Alpha)? Arguments(List<CssValue> children)
    {
        List<CssValue> items = children.Where(c => !c.IsWhitespace).ToList();
        if (items.Any(i => !i.IsToken)) return null; // calc() and relative colors are not supported
        var components = new List<CssToken>();
        CssToken? alpha = null;
        bool commas = items.Any(i => i.Token.Type == CssTokenType.Comma);
        for (int k = 0; k < items.Count; k++)
        {
            CssToken t = items[k].Token;
            if (commas)
            {
                if (k % 2 == 1)
                {
                    if (t.Type != CssTokenType.Comma) return null;
                    continue;
                }
            }
            else if (t is { Type: CssTokenType.Delim, Value: "/" })
            {
                if (k != items.Count - 2) return null;
                alpha = items[k + 1].Token;
                break;
            }

            if (t.Type == CssTokenType.Comma) return null;
            components.Add(t);
        }

        if (commas && components.Count == 4)
        {
            alpha = components[3];
            components.RemoveAt(3);
        }

        return components.Count == 3 ? (components, alpha) : null;
    }

    /// <summary>A number or percentage component; <paramref name="percentScale"/> is the value of 100%.</summary>
    private static double? Number(CssToken t, double percentScale) => t.Type switch
    {
        CssTokenType.Number => t.Number,
        CssTokenType.Percentage => t.Number / 100 * percentScale,
        CssTokenType.Ident when t.Value.Equals("none", StringComparison.OrdinalIgnoreCase) => 0,
        _ => null,
    };

    /// <summary>A hue in degrees: a number or an angle.</summary>
    private static double? Hue(CssToken t) => t.Type switch
    {
        CssTokenType.Number => t.Number,
        CssTokenType.Dimension => t.Unit switch
        {
            "deg" => t.Number,
            "rad" => t.Number * 180 / Math.PI,
            "grad" => t.Number * 0.9,
            "turn" => t.Number * 360,
            _ => null,
        },
        CssTokenType.Ident when t.Value.Equals("none", StringComparison.OrdinalIgnoreCase) => 0,
        _ => null,
    };

    private static double? Alpha(CssToken? t) => t is CssToken a ? Number(a, 1) is double v ? Clamp(v) : null : 1;

    private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static CssColor? Rgb((List<CssToken> C, CssToken? A)? args)
    {
        if (args is not var (c, a)) return null;
        double? r = Number(c[0], 255), g = Number(c[1], 255), b = Number(c[2], 255), alpha = Alpha(a);
        return r is null || g is null || b is null || alpha is null ? null : new CssColor(Clamp(r.Value / 255), Clamp(g.Value / 255), Clamp(b.Value / 255), alpha.Value);
    }

    private static CssColor? Hsl((List<CssToken> C, CssToken? A)? args)
    {
        if (args is not var (c, a)) return null;
        double? h = Hue(c[0]), s = Number(c[1], 100), l = Number(c[2], 100), alpha = Alpha(a);
        if (h is null || s is null || l is null || alpha is null) return null;
        (double r, double g, double b) = HslToRgb(h.Value, Clamp(s.Value / 100), Clamp(l.Value / 100));
        return new CssColor(r, g, b, alpha.Value);
    }

    private static (double R, double G, double B) HslToRgb(double hue, double s, double l)
    {
        hue = ((hue % 360) + 360) % 360;
        double F(int n)
        {
            double k = (n + (hue / 30)) % 12;
            return l - (s * Math.Min(l, 1 - l) * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
        }

        return (F(0), F(8), F(4));
    }

    private static CssColor? Hwb((List<CssToken> C, CssToken? A)? args)
    {
        if (args is not var (c, a)) return null;
        double? h = Hue(c[0]), w = Number(c[1], 100), bl = Number(c[2], 100), alpha = Alpha(a);
        if (h is null || w is null || bl is null || alpha is null) return null;
        double white = Clamp(w.Value / 100), black = Clamp(bl.Value / 100);
        if (white + black >= 1)
        {
            double gray = white / (white + black);
            return new CssColor(gray, gray, gray, alpha.Value);
        }

        (double r, double g, double b) = HslToRgb(h.Value, 1, 0.5);
        double scale = 1 - white - black;
        return new CssColor((r * scale) + white, (g * scale) + white, (b * scale) + white, alpha.Value);
    }

    // CSS Color Level 4, 9 and 17: Lab and LCH in D50 through XYZ, Oklab through LMS, to sRGB.
    private static CssColor? Lab((List<CssToken> C, CssToken? A)? args, bool oklab, bool polar)
    {
        if (args is not var (c, a)) return null;
        double lightScale = oklab ? 1 : 100;
        double chromaScale = oklab ? 0.4 : polar ? 150 : 125;
        double? l = Number(c[0], lightScale), x = Number(c[1], chromaScale);
        double? y = polar ? Hue(c[2]) : Number(c[2], chromaScale);
        double? alpha = Alpha(a);
        if (l is null || x is null || y is null || alpha is null) return null;

        double light = Math.Max(0, l.Value), ca = x.Value, cb = y.Value;
        if (polar)
        {
            double chroma = Math.Max(0, x.Value), radians = y.Value * Math.PI / 180;
            ca = chroma * Math.Cos(radians);
            cb = chroma * Math.Sin(radians);
        }

        (double r, double g, double b) = oklab ? OklabToLinear(light, ca, cb) : LabToLinear(light, ca, cb);
        return new CssColor(Clamp(Gamma(r)), Clamp(Gamma(g)), Clamp(Gamma(b)), alpha.Value);
    }

    private static (double, double, double) OklabToLinear(double l, double a, double b)
    {
        double lp = l + (0.3963377774 * a) + (0.2158037573 * b);
        double mp = l - (0.1055613458 * a) - (0.0638541728 * b);
        double sp = l - (0.0894841775 * a) - (1.2914855480 * b);
        double lc = lp * lp * lp, mc = mp * mp * mp, sc = sp * sp * sp;
        return (
            (4.0767416621 * lc) - (3.3077115913 * mc) + (0.2309699292 * sc),
            (-1.2684380046 * lc) + (2.6097574011 * mc) - (0.3413193965 * sc),
            (-0.0041960863 * lc) - (0.7034186147 * mc) + (1.7076147010 * sc));
    }

    private static (double, double, double) LabToLinear(double l, double a, double b)
    {
        const double Kappa = 24389.0 / 27, Epsilon = 216.0 / 24389;
        double f1 = (l + 16) / 116, f0 = (a / 500) + f1, f2 = f1 - (b / 200);
        double x = (f0 * f0 * f0 > Epsilon ? f0 * f0 * f0 : ((116 * f0) - 16) / Kappa) * (0.3457 / 0.3585);
        double y = l > Kappa * Epsilon ? f1 * f1 * f1 : l / Kappa;
        double z = (f2 * f2 * f2 > Epsilon ? f2 * f2 * f2 : ((116 * f2) - 16) / Kappa) * ((1.0 - 0.3457 - 0.3585) / 0.3585);

        // Bradford adaptation from D50 to D65.
        double x65 = (0.9554734527042182 * x) - (0.023098536874261423 * y) + (0.0632593086610217 * z);
        double y65 = (-0.028369706963208136 * x) + (1.0099954580058226 * y) + (0.021041398966943008 * z);
        double z65 = (0.012314001688319899 * x) - (0.020507696433477912 * y) + (1.3303659366080753 * z);
        return (
            (3.2409699419045226 * x65) - (1.537383177570094 * y65) - (0.4986107602930034 * z65),
            (-0.9692436362808796 * x65) + (1.8759675015077202 * y65) + (0.04155505740717559 * z65),
            (0.05563007969699366 * x65) - (0.20397695888897652 * y65) + (1.0569715142428786 * z65));
    }

    private static double Gamma(double c)
    {
        double sign = c < 0 ? -1 : 1, abs = Math.Abs(c);
        return sign * (abs <= 0.0031308 ? 12.92 * abs : (1.055 * Math.Pow(abs, 1 / 2.4)) - 0.055);
    }
}
