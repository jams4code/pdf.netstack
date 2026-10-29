namespace PdfNetStack.Imaging;

/// <summary>
/// What embedding a JPEG needs from its header, read without decoding the image: frame size and
/// components, the Adobe marker (inverted CMYK), JFIF density, the EXIF orientation and an
/// embedded ICC profile.
/// </summary>
internal sealed class JpegHeader
{
    private JpegHeader()
    {
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Components { get; private set; }

    public bool Progressive { get; private set; }

    public bool Adobe { get; private set; }

    /// <summary>Pixels per inch from JFIF, or 0 when unknown.</summary>
    public double DpiX { get; private set; }

    public double DpiY { get; private set; }

    /// <summary>EXIF orientation, 1 (upright) to 8.</summary>
    public int Orientation { get; private set; } = 1;

    public byte[]? IccProfile { get; private set; }

    /// <exception cref="FormatException">Not a JPEG PDF viewers can show.</exception>
    public static JpegHeader Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new FormatException("Not a JPEG file.");
        var h = new JpegHeader();
        var icc = new SortedDictionary<int, byte[]>();
        int at = 2;
        while (at + 4 <= d.Length)
        {
            if (d[at] != 0xFF)
            {
                at++;
                continue;
            }

            byte marker = d[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }

            int length = d[at + 2] << 8 | d[at + 3];
            if (length < 2 || at + 2 + length > d.Length) throw new FormatException("JPEG segment is truncated.");
            ReadOnlySpan<byte> payload = d.Slice(at + 4, length - 2);
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (payload.Length < 6) throw new FormatException("JPEG frame header is truncated.");
                    if (payload[0] != 8) throw new FormatException("Only 8 bit JPEG images can be embedded in PDF.");
                    h.Height = payload[1] << 8 | payload[2];
                    h.Width = payload[3] << 8 | payload[4];
                    h.Components = payload[5];
                    h.Progressive = marker == 0xC2;
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    throw new FormatException("Lossless, hierarchical and arithmetic coded JPEGs cannot be shown by PDF viewers.");
                case 0xE0:
                    ReadJfif(h, payload);
                    break;
                case 0xE1:
                    ReadExif(h, payload);
                    break;
                case 0xE2:
                    if (payload.Length > 14 && payload.Slice(0, 12).SequenceEqual("ICC_PROFILE\0"u8)) icc[payload[12]] = payload.Slice(14).ToArray();
                    break;
                case 0xEE:
                    if (payload.Length >= 5 && payload.Slice(0, 5).SequenceEqual("Adobe"u8)) h.Adobe = true;
                    break;
                case 0xDA:
                    at = d.Length; // image data follows; the header is complete
                    continue;
            }

            at += 2 + length;
        }

        if (h.Width == 0 || h.Height == 0) throw new FormatException("JPEG has no frame header.");
        if (h.Components is not (1 or 3 or 4)) throw new FormatException("JPEG must have 1, 3 or 4 components.");
        if (icc.Count > 0) h.IccProfile = [.. icc.Values.SelectMany(b => b)];
        return h;
    }

    private static void ReadJfif(JpegHeader h, ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 || !p.Slice(0, 5).SequenceEqual("JFIF\0"u8)) return;
        int units = p[7];
        int x = p[8] << 8 | p[9], y = p[10] << 8 | p[11];
        if (x == 0 || y == 0) return;
        double scale = units switch { 1 => 1, 2 => 2.54, _ => 0 };
        h.DpiX = x * scale;
        h.DpiY = y * scale;
    }

    /// <summary>The orientation tag (0x0112) of the first EXIF image directory.</summary>
    private static void ReadExif(JpegHeader h, ReadOnlySpan<byte> p)
    {
        if (p.Length < 14 || !p.Slice(0, 6).SequenceEqual("Exif\0\0"u8)) return;
        ReadOnlySpan<byte> tiff = p.Slice(6);
        bool little = tiff[0] == 'I';
        long ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd + 2 > tiff.Length) return;
        int count = U16(tiff, (int)ifd, little);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + 12 * i;
            if (entry + 12 > tiff.Length) return;
            if (U16(tiff, entry, little) != 0x0112) continue;
            int value = U16(tiff, entry + 8, little);
            if (value is >= 1 and <= 8) h.Orientation = value;
            return;
        }
    }

    private static int U16(ReadOnlySpan<byte> d, int at, bool little) =>
        at + 2 > d.Length ? -1 : little ? d[at] | d[at + 1] << 8 : d[at] << 8 | d[at + 1];

    private static long U32(ReadOnlySpan<byte> d, int at, bool little) => at + 4 > d.Length ? -1 : little
        ? (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24)
        : (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
}
