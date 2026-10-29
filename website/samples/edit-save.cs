#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("manual.pdf");

#region save
doc.Save("manual-fast.pdf", new PdfSaveOptions
{
    Optimize = false,                          // skip the optimizer pass, save sooner
    Compression = CompressionEffort.Fast,      // zlib level 1 for the streams the writer compresses
});

byte[] bytes = doc.ToArray();                  // in memory, with the default options
using (var stream = new MemoryStream())
{
    doc.Save(stream);                          // or to any writable stream
}
#endregion

File.WriteAllBytes("manual-copy.pdf", bytes);
Console.WriteLine($"fast: {new FileInfo("manual-fast.pdf").Length:N0} bytes, default: {bytes.Length:N0} bytes");
