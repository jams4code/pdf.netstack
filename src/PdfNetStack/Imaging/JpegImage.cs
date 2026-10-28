using System.Buffers;

namespace PdfNetStack.Imaging;

/// <summary>Thrown for JPEG data that is malformed or uses a coding process this codec does not support.</summary>
internal sealed class JpegFormatException : Exception
{
    public JpegFormatException(string message) : base(message) { }

    public JpegFormatException(string message, Exception inner) : base(message, inner) { }

    public JpegFormatException() { }
}

/// <summary>A marker segment carried over verbatim (JFIF and Adobe application data).</summary>
/// <param name="Marker">The second marker byte, for example 0xE0 for APP0.</param>
/// <param name="Payload">The segment content after the length field.</param>
internal sealed record JpegSegment(byte Marker, byte[] Payload);

/// <summary>
/// One color component, held as quantized DCT coefficients in zigzag order, 64 per block.
/// Blocks are allocated to whole MCUs, so interleaved scans can address padding blocks.
/// </summary>
internal sealed class JpegComponent
{
    private short[]? _coefficients;
    private readonly int _length;

    public JpegComponent(int id, int horizontal, int vertical, int tableIndex, int blocksPerLine, int blocksPerColumn, int allocatedPerLine, int allocatedPerColumn)
    {
        Id = id;
        Horizontal = horizontal;
        Vertical = vertical;
        TableIndex = tableIndex;
        BlocksPerLine = blocksPerLine;
        BlocksPerColumn = blocksPerColumn;
        AllocatedPerLine = allocatedPerLine;
        AllocatedPerColumn = allocatedPerColumn;
        _length = checked(allocatedPerLine * allocatedPerColumn * 64);
        _coefficients = ArrayPool<short>.Shared.Rent(_length);
        Array.Clear(_coefficients, 0, _length);
    }

    public int Id { get; }

    public int Horizontal { get; }

    public int Vertical { get; }

    /// <summary>Quantization table index named by the frame header.</summary>
    public int TableIndex { get; }

    /// <summary>Blocks that cover the image, per row.</summary>
    public int BlocksPerLine { get; }

    /// <summary>Blocks that cover the image, per column.</summary>
    public int BlocksPerColumn { get; }

    public int AllocatedPerLine { get; }

    public int AllocatedPerColumn { get; }

    /// <summary>
    /// The table in force when the component's first scan began. A decoder latches it there, so a
    /// later redefinition of the same table index does not apply to this component.
    /// </summary>
    public ushort[] Quantization { get; set; } = [];

    /// <summary>Whether a scan has carried this component yet.</summary>
    public bool Started { get; set; }

    public Span<short> Coefficients => (_coefficients ?? throw new ObjectDisposedException(nameof(JpegComponent))).AsSpan(0, _length);

    public Span<short> Block(int row, int column) => Coefficients.Slice((row * AllocatedPerLine + column) * 64, 64);

    public void Release()
    {
        if (_coefficients is null) return;
        ArrayPool<short>.Shared.Return(_coefficients);
        _coefficients = null;
    }
}

/// <summary>How the source frame was coded. The writer always produces sequential output.</summary>
internal enum JpegCoding
{
    Baseline,
    ExtendedSequential,
    Progressive,
}

/// <summary>
/// A decoded JPEG frame at the coefficient level: everything needed to write the same image
/// again, and nothing that needs an inverse DCT. Owns pooled buffers; dispose it.
/// </summary>
internal sealed class JpegImage : IDisposable
{
    public JpegImage(JpegCoding coding, int width, int height, JpegComponent[] components)
    {
        Coding = coding;
        Width = width;
        Height = height;
        Components = components;
        foreach (JpegComponent c in components)
        {
            MaxHorizontal = Math.Max(MaxHorizontal, c.Horizontal);
            MaxVertical = Math.Max(MaxVertical, c.Vertical);
        }

        McusPerLine = (width + 8 * MaxHorizontal - 1) / (8 * MaxHorizontal);
        McusPerColumn = (height + 8 * MaxVertical - 1) / (8 * MaxVertical);
    }

    public JpegCoding Coding { get; }

    public int Width { get; }

    public int Height { get; }

    public JpegComponent[] Components { get; }

    public int MaxHorizontal { get; }

    public int MaxVertical { get; }

    public int McusPerLine { get; }

    public int McusPerColumn { get; }

    /// <summary>JFIF APP0 and Adobe APP14 segments, in file order. They decide how decoders convert color.</summary>
    public List<JpegSegment> Segments { get; } = [];

    /// <summary>The Adobe APP14 color transform (0 none, 1 YCbCr, 2 YCCK), or null without that segment.</summary>
    public int? AdobeTransform { get; set; }

    public bool HasJfif { get; set; }

    /// <summary>
    /// Whether each component holds color difference (Cb or Cr) data rather than luminance or an
    /// ink. Follows libjpeg's color space inference, with the PDF /ColorTransform taking precedence.
    /// </summary>
    public bool[] ChromaComponents(int? pdfColorTransform)
    {
        int n = Components.Length;
        // An Adobe marker in the data overrides the DCTDecode /ColorTransform entry (ISO 32000-1, 7.4.8).
        bool transformed = AdobeTransform is { } adobe
            ? adobe != 0
            : pdfColorTransform is { } pdf
                ? pdf != 0
                : n == 3 && !(Components[0].Id == 'R' && Components[1].Id == 'G' && Components[2].Id == 'B');
        var chroma = new bool[n];
        if (transformed && n is 3 or 4)
        {
            chroma[1] = true;
            chroma[2] = true;
        }

        return chroma;
    }

    public void Dispose()
    {
        foreach (JpegComponent c in Components) c.Release();
    }
}
