namespace PdfNetStack.Drawing;

/// <summary>A point in canvas coordinates.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>
/// A 2D affine transform in PDF order [a b c d e f]: x' = a x + c y + e, y' = b x + d y + f.
/// </summary>
/// <param name="A">Horizontal scale component.</param>
/// <param name="B">Vertical shear component.</param>
/// <param name="C">Horizontal shear component.</param>
/// <param name="D">Vertical scale component.</param>
/// <param name="E">Horizontal translation.</param>
/// <param name="F">Vertical translation.</param>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity transform.</summary>
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>A translation.</summary>
    public static PdfMatrix CreateTranslation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    /// <summary>A scale about the origin.</summary>
    public static PdfMatrix CreateScale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>
    /// A rotation about the origin. On the canvas, whose y axis points down, positive angles turn
    /// clockwise, as in CSS.
    /// </summary>
    public static PdfMatrix CreateRotation(double degrees)
    {
        double r = degrees * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r);

        // Snap multiples of 90 degrees so that rotated lines stay exactly axis aligned.
        if (Math.Abs(cos) < 1e-12) cos = 0;
        if (Math.Abs(sin) < 1e-12) sin = 0;
        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>A skew by the given angles, in degrees.</summary>
    public static PdfMatrix CreateSkew(double degreesX, double degreesY) =>
        new(1, Math.Tan(degreesY * Math.PI / 180), Math.Tan(degreesX * Math.PI / 180), 1, 0, 0);

    /// <summary>True when the transform can be inverted.</summary>
    public bool IsInvertible => Math.Abs(A * D - B * C) > 1e-12;

    /// <summary>
    /// The transform that applies <paramref name="first"/>, then <paramref name="second"/>.
    /// </summary>
    public static PdfMatrix Multiply(PdfMatrix first, PdfMatrix second) => new(
        first.A * second.A + first.B * second.C,
        first.A * second.B + first.B * second.D,
        first.C * second.A + first.D * second.C,
        first.C * second.B + first.D * second.D,
        first.E * second.A + first.F * second.C + second.E,
        first.E * second.B + first.F * second.D + second.F);

    /// <summary>The inverse transform.</summary>
    /// <exception cref="InvalidOperationException">The transform is singular.</exception>
    public PdfMatrix Invert()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) <= 1e-12) throw new InvalidOperationException("The matrix cannot be inverted.");
        return new PdfMatrix(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    /// <summary>Applies the transform to a point.</summary>
    public PdfPoint Transform(PdfPoint point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);
}
