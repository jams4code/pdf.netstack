using PdfNetStack.Drawing;

namespace PdfNetStack.Html.Layout;

/// <summary>An image the document uses: a JPEG or PNG raster, or an SVG drawing kept as vectors.</summary>
internal sealed class HtmlImage
{
    public HtmlImage(PdfImage raster) => Raster = raster;

    public HtmlImage(PdfSvg vector) => Vector = vector;

    public PdfImage? Raster { get; }

    public PdfSvg? Vector { get; }

    /// <summary>The intrinsic width in CSS px: raster pixels at their recorded resolution (else 96 per inch), or the drawing's own size.</summary>
    public double Width => Raster is PdfImage r
        ? r.PixelWidth * 96 / (r.HorizontalResolution > 0 ? r.HorizontalResolution : 96)
        : Vector!.PixelWidth;

    public double Height => Raster is PdfImage r
        ? r.PixelHeight * 96 / (r.VerticalResolution > 0 ? r.VerticalResolution : 96)
        : Vector!.PixelHeight;

    public void Draw(PdfGraphics g, double x, double y, double width, double height)
    {
        if (Raster is PdfImage raster) g.DrawImage(raster, x, y, width, height);
        else g.DrawSvg(Vector!, x, y, width, height);
    }
}
