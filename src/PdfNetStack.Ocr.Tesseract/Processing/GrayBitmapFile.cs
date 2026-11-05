namespace PdfNetStack.Ocr.Tesseract.Processing;

/// <summary>
/// Writes an 8 bit gray image as a BMP file with a gray palette. Leptonica reads BMP from memory on
/// every platform and always has the code for it; its PNM reader goes through a temporary file on
/// Windows, which would leave a scan's pixels on disk, and PNG support depends on how it was built.
/// </summary>
internal static class GrayBitmapFile
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteSize = 256 * 4;

    /// <summary>Writes the rows directly from the image, so memory stays at one header.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="image">The image; its size must fit a BMP file, as the engine checks.</param>
    /// <param name="cancellationToken">Stops between rows.</param>
    public static void Write(Stream stream, OcrBitmap image, CancellationToken cancellationToken)
    {
        int stride = (image.Width + 3) & ~3;
        long imageSize = (long)stride * image.Height;
        byte[] header = Header(image.Width, image.Height, imageSize);
        stream.Write(header, 0, header.Length);

        // Rows go bottom to top, padded to four bytes.
        byte[] padding = new byte[stride - image.Width];
        for (int y = image.Height - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(image.Pixels, y * image.Width, image.Width);
            stream.Write(padding, 0, padding.Length);
        }
    }

    private static byte[] Header(int width, int height, long imageSize)
    {
        int offset = FileHeaderSize + InfoHeaderSize + PaletteSize;
        byte[] header = new byte[offset];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        PutInt(header, 2, checked((int)(offset + imageSize)));
        PutInt(header, 10, offset);
        PutInt(header, 14, InfoHeaderSize);
        PutInt(header, 18, width);
        PutInt(header, 22, height);
        header[26] = 1; // planes
        header[28] = 8; // bits per pixel
        PutInt(header, 34, checked((int)imageSize));
        PutInt(header, 46, 256); // colors used
        for (int i = 0; i < 256; i++)
        {
            int entry = FileHeaderSize + InfoHeaderSize + (4 * i);
            header[entry] = header[entry + 1] = header[entry + 2] = (byte)i;
        }

        return header;
    }

    private static void PutInt(byte[] target, int at, int value)
    {
        target[at] = (byte)value;
        target[at + 1] = (byte)(value >> 8);
        target[at + 2] = (byte)(value >> 16);
        target[at + 3] = (byte)(value >> 24);
    }
}
