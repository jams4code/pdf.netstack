#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

#region list
PdfDocument doc = PdfDocument.Open("manual.pdf");
IReadOnlyList<PdfPageImage> images = doc.Pages[0].GetImages();
foreach (PdfPageImage image in images)
{
    Console.WriteLine(
        $"{image.Width}x{image.Height}, {image.Encoding ?? "raw samples"}, " +
        $"{image.HorizontalResolution:0} dpi, at {image.Bounds.Left:0},{image.Bounds.Bottom:0}");
}
#endregion

#region save
int n = 0;
foreach (PdfPageImage image in images)
{
    n++;
    if (image.Encoding == "DCTDecode")
    {
        File.WriteAllBytes($"image-{n}.jpg", image.GetEncodedData());   // the JPEG as stored
    }
    else if (image.Decode() is PdfBitmap bitmap)
    {
        bitmap.SavePng($"image-{n}.png");                                // 8 bit gray or RGB pixels
        Console.WriteLine($"image-{n}.png: {bitmap.Format}, {bitmap.Width}x{bitmap.Height}");
    }
}
#endregion
