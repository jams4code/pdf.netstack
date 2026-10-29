using System.Runtime.CompilerServices;
using System.Text;
using PdfNetStack.Drawing.Svg;

namespace PdfNetStack.Drawing;

/// <summary>
/// An SVG drawing, placed as vector graphics: it stays sharp at any zoom and its text stays text.
/// Each drawing is written once per document and reused wherever it is placed. Supported: shapes,
/// paths, groups, use and symbol, nested viewports, solid colors and linear and radial gradients,
/// clip paths, opacity, text in the standard fonts, embedded JPEG and PNG images, and style
/// sheets with type, class and id selectors. Not supported: filters, masks, patterns, external
/// references and animation.
/// </summary>
/// <example>
/// <code>
/// PdfSvg logo = PdfSvg.Load("logo.svg");
/// using PdfGraphics g = page.CreateGraphics();
/// g.DrawSvg(logo, 40, 40, 120, 40);
/// </code>
/// </example>
public sealed class PdfSvg
{
    private const double PointsPerPixel = 0.75;
    private const int MaxBytes = 32 << 20;

    private readonly SvgElement _root;
    private readonly ConditionalWeakTable<PdfDocument, PdfForm> _forms = new();

    private PdfSvg(SvgElement root)
    {
        _root = root;

        // Size from width and height, else the viewBox, else the default 300 by 150 (SVG 2, 8.2).
        string[] viewBox = (root["viewbox"] ?? "").Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        double vbWidth = viewBox.Length == 4 ? SvgRenderer.Length(viewBox[2], 0) : 0;
        double vbHeight = viewBox.Length == 4 ? SvgRenderer.Length(viewBox[3], 0) : 0;
        double width = Absolute(root["width"]), height = Absolute(root["height"]);
        if (width <= 0 && height > 0 && vbWidth > 0 && vbHeight > 0) width = height * vbWidth / vbHeight;
        if (height <= 0 && width > 0 && vbWidth > 0 && vbHeight > 0) height = width * vbHeight / vbWidth;
        PixelWidth = width > 0 ? width : vbWidth > 0 ? vbWidth : 300;
        PixelHeight = height > 0 ? height : vbHeight > 0 ? vbHeight : 150;
    }

    /// <summary>Width in points at the drawing's own size, CSS pixels at 96 per inch.</summary>
    public double Width => PixelWidth * PointsPerPixel;

    /// <summary>Height in points at the drawing's own size.</summary>
    public double Height => PixelHeight * PointsPerPixel;

    /// <summary>The size in CSS pixels, for layout engines that work in them.</summary>
    internal double PixelWidth { get; }

    internal double PixelHeight { get; }

    // A width in percent says nothing about the drawing's own size.
    private static double Absolute(string? length) =>
        length is null || Compat.EndsWith(length.TrimEnd(), '%') ? 0 : SvgRenderer.Length(length, 0);

    /// <summary>Parses SVG markup.</summary>
    /// <exception cref="FormatException">The text has no svg root element.</exception>
    public static PdfSvg Parse(string svg)
    {
        Compat.ThrowIfNull(svg, nameof(svg));
        return new PdfSvg(SvgParser.Parse(svg));
    }

    /// <summary>Loads an SVG file, UTF-8 unless it starts with a byte order mark.</summary>
    /// <exception cref="FormatException">The file is not SVG.</exception>
    public static PdfSvg Load(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Loads SVG from a stream, up to 32 MiB.</summary>
    /// <exception cref="FormatException">The data is not SVG.</exception>
    public static PdfSvg Load(Stream stream)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new FormatException("The SVG data is larger than 32 MiB.");
            buffer.Write(chunk, 0, read);
        }

        return Load(buffer.ToArray());
    }

    /// <summary>Loads SVG from bytes, UTF-8 unless they start with a byte order mark.</summary>
    /// <exception cref="FormatException">The data is not SVG.</exception>
    public static PdfSvg Load(byte[] data)
    {
        Compat.ThrowIfNull(data, nameof(data));
        if (data.Length > MaxBytes) throw new FormatException("The SVG data is larger than 32 MiB.");
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Whether bytes look like SVG rather than a raster image: markup that names an svg element.</summary>
    internal static bool Sniff(byte[] data)
    {
        int length = Math.Min(data.Length, 4096);
        string head = Encoding.UTF8.GetString(data, 0, length).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return Compat.StartsWith(head, '<') && Compat.Contains(head, "<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The drawing as a form of <paramref name="document"/>, rendered on first use.</summary>
    internal PdfForm FormFor(PdfDocument document)
    {
        if (_forms.TryGetValue(document, out PdfForm? cached)) return cached;
        PdfForm form = PdfForm.Create(document, Width, Height);
        using (PdfGraphics g = form.CreateGraphics())
        {
            g.ScaleTransform(PointsPerPixel, PointsPerPixel);
            SvgRenderer.Render(g, _root, PixelWidth, PixelHeight);
        }

        _forms.Add(document, form);
        return form;
    }
}
