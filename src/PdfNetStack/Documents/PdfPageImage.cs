using PdfNetStack.Drawing;
using PdfNetStack.Content;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>
/// An image drawn on a page: an image XObject or an inline image, with where it lands. The same
/// XObject drawn twice appears twice, once per placement.
/// </summary>
/// <example>
/// <code>
/// foreach (PdfPageImage image in doc.Pages[0].GetImages())
/// {
///     if (image.Encoding == "DCTDecode") File.WriteAllBytes("photo.jpg", image.GetEncodedData());
///     else image.Decode()?.SavePng("image.png");
/// }
/// </code>
/// </example>
public sealed class PdfPageImage
{
    private readonly PdfDocument _document;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly PdfDictionary? _resources;

    internal PdfPageImage(PdfDocument document, PdfDictionary dictionary, PdfStream? stream, ReadOnlyMemory<byte> data, PdfDictionary? resources, PdfMatrix transform)
    {
        _document = document;
        _data = data;
        _resources = resources;
        Dictionary = dictionary;
        Stream = stream;
        Transform = transform;
        Width = Int(dictionary["Width"]);
        Height = Int(dictionary["Height"]);
        IsMask = document.Resolve(dictionary["ImageMask"]) is PdfBoolean { Value: true };
        BitsPerComponent = IsMask ? 1 : Int(dictionary["BitsPerComponent"]);
        Encoding = ImageSamples.Encoding(document, dictionary);

        // The image is the unit square in its own space (ISO 32000-1, 8.9.4).
        PdfPoint[] corners = [transform.Transform(new(0, 0)), transform.Transform(new(1, 0)), transform.Transform(new(0, 1)), transform.Transform(new(1, 1))];
        Bounds = new PdfRectangle(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    /// <summary>The image XObject, or null for an inline image.</summary>
    public PdfStream? Stream { get; }

    /// <summary>True for an inline image (BI ... EI in the content).</summary>
    public bool IsInline => Stream is null;

    /// <summary>The image dictionary; an inline image's abbreviations are expanded.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Width in samples.</summary>
    public int Width { get; }

    /// <summary>Height in samples.</summary>
    public int Height { get; }

    /// <summary>Bits per sample: 1, 2, 4, 8 or 16 (1 for a mask, 8 for a JPEG).</summary>
    public int BitsPerComponent { get; }

    /// <summary>True for a stencil mask: its samples say where the fill color paints.</summary>
    public bool IsMask { get; }

    /// <summary>
    /// The image codec of the data: DCTDecode, JPXDecode, JBIG2Decode or CCITTFaxDecode, or null
    /// when the data is plain samples once general compression is undone.
    /// </summary>
    public string? Encoding { get; }

    /// <summary>Maps the image's unit square to the page's default user space, in points.</summary>
    public PdfMatrix Transform { get; }

    /// <summary>The smallest rectangle around the image on the page, in default user space (before /Rotate).</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Resolution across the image in dots per inch, as drawn.</summary>
    public double HorizontalResolution => Width / Math.Max(1e-9, Math.Sqrt((Transform.A * Transform.A) + (Transform.B * Transform.B)) / 72);

    /// <summary>Resolution down the image in dots per inch, as drawn.</summary>
    public double VerticalResolution => Height / Math.Max(1e-9, Math.Sqrt((Transform.C * Transform.C) + (Transform.D * Transform.D)) / 72);

    /// <summary>
    /// The data with general compression undone: a JPEG file for DCTDecode, a JPEG 2000 codestream
    /// for JPXDecode, the raw samples when <see cref="Encoding"/> is null.
    /// </summary>
    /// <exception cref="PdfFormatException">The data is damaged or exceeds the document's decode limit.</exception>
    public byte[] GetEncodedData() => ImageSamples.EncodedData(_document, Dictionary, _data);

    /// <summary>
    /// Decodes the image to 8 bit gray or RGB pixels: color spaces converted, the Decode array
    /// applied, masks black where they paint. Soft masks and transparency are not applied.
    /// </summary>
    /// <returns>The pixels, or null for an encoding not decoded yet (JPXDecode, JBIG2Decode, CCITTFaxDecode) or an unsupported color space.</returns>
    /// <exception cref="PdfFormatException">The data is damaged or too large.</exception>
    public PdfBitmap? Decode() => ImageSamples.Decode(_document, Dictionary, _data, _resources) is Raster r ? new PdfBitmap(r) : null;

    private int Int(PdfObject? value) => _document.Resolve(value) is PdfNumber n && n.ToDouble() is >= 0 and <= int.MaxValue and var v ? (int)v : 0;

    /// <summary>Finds the images a page draws, in drawing order.</summary>
    internal sealed class Finder(PdfDocument document) : ContentInterpreter(document)
    {
        public List<PdfPageImage> Images { get; } = [];

        protected override void OnImage(PdfStream image, PdfDictionary? resources) =>
            Images.Add(new PdfPageImage(Document, image.Dictionary, image, image.Data, resources, Ctm));

        protected override void OnInlineImage(PdfDictionary image, ReadOnlyMemory<byte> data, PdfDictionary? resources) =>
            Images.Add(new PdfPageImage(Document, image, null, data, resources, Ctm));
    }
}

/// <summary>The pixel layout of a <see cref="PdfBitmap"/>.</summary>
public enum PdfBitmapFormat
{
    /// <summary>One byte per pixel, 0 black to 255 white.</summary>
    Gray8,

    /// <summary>Three bytes per pixel: red, green, blue.</summary>
    Rgb24,
}

/// <summary>Decoded pixels: rows top to bottom, no padding between them.</summary>
public sealed class PdfBitmap
{
    private readonly byte[] _pixels;

    internal PdfBitmap(Raster raster)
    {
        Width = raster.Width;
        Height = raster.Height;
        Format = raster.Channels == 1 ? PdfBitmapFormat.Gray8 : PdfBitmapFormat.Rgb24;
        _pixels = raster.Samples;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per pixel and their meaning.</summary>
    public PdfBitmapFormat Format { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * (Format == PdfBitmapFormat.Gray8 ? 1 : 3);

    /// <summary>The pixels, <see cref="Stride"/> bytes per row.</summary>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>Encodes the bitmap as a PNG file.</summary>
    public byte[] ToPng() => PngWriter.Write(Width, Height, Format == PdfBitmapFormat.Gray8 ? 1 : 3, _pixels);

    /// <summary>Writes the bitmap as a PNG file.</summary>
    /// <param name="path">The file to create or overwrite.</param>
    public void SavePng(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        File.WriteAllBytes(path, ToPng());
    }

    /// <summary>Writes the bitmap as a PNG to a stream.</summary>
    /// <param name="output">Where the PNG is written.</param>
    public void SavePng(Stream output)
    {
        Compat.ThrowIfNull(output, nameof(output));
        byte[] png = ToPng();
        output.Write(png, 0, png.Length);
    }
}
