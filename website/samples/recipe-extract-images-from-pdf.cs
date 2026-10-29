#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("manual.pdf");
for (int p = 0; p < doc.Pages.Count; p++)
{
    IReadOnlyList<PdfPageImage> images = doc.Pages[p].GetImages();
    for (int i = 0; i < images.Count; i++)
    {
        PdfPageImage image = images[i];
        string name = $"page{p + 1}-image{i + 1}";
        if (image.Encoding == "DCTDecode")
        {
            File.WriteAllBytes(name + ".jpg", image.GetEncodedData());   // the JPEG as stored
        }
        else if (image.Decode() is PdfBitmap bitmap)
        {
            bitmap.SavePng(name + ".png");                                // 8 bit gray or RGB pixels
        }
        Console.WriteLine($"{name}: {image.Width}x{image.Height}, {image.Encoding ?? "raw"}, {image.HorizontalResolution:0} dpi");
    }
}
