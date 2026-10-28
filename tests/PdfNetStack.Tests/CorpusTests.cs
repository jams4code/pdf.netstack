#if NET8_0_OR_GREATER
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Security;
using PdfNetStack.Writing;

namespace PdfNetStack.Tests;

/// <summary>
/// Opt-in checks against real documents, which never live in this repository. Set
/// PDFNETSTACK_CORPUS to a folder of PDFs and PDFNETSTACK_CORPUS_OUT to an empty folder, then
/// compare the output with tools/render_compare.py.
/// </summary>
[TestClass]
public sealed class CorpusTests
{
    private static (string In, string Out)? Folders()
    {
        string? input = Environment.GetEnvironmentVariable("PDFNETSTACK_CORPUS");
        string? output = Environment.GetEnvironmentVariable("PDFNETSTACK_CORPUS_OUT");
        return string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) ? null : (input, output);
    }

    /// <summary>
    /// Decrypts every encrypted PDF that opens with the empty user password and writes it in the
    /// clear under decrypted/, and writes RC4 encrypted copies of plain files under rc4/ with user
    /// password "u", for an independent reader to check.
    /// </summary>
    [TestMethod]
    public void DecryptAndLegacyEncryptCorpus()
    {
        (string In, string Out)? configured = Folders();
        if (configured is null)
        {
            Assert.Inconclusive("Set PDFNETSTACK_CORPUS and PDFNETSTACK_CORPUS_OUT to run.");
            return;
        }

        (string In, string Out) folders = configured.Value;
        int decrypted = 0, encrypted = 0;
        foreach (string path in Directory.EnumerateFiles(folders.In, "*.pdf", SearchOption.AllDirectories))
        {
            byte[] data = File.ReadAllBytes(path);
            PdfFile file;
            try { file = PdfFile.Open(data, 256L << 20); }
            catch (PdfFormatException) { continue; }
            string relative = Path.GetRelativePath(folders.In, path);

            if (file.Trailer["Encrypt"] is not null)
            {
                try { file.Unlock(null); }
                catch (PdfPasswordException) { continue; }
                Write(file, Path.Combine(folders.Out, "decrypted", relative), encryption: null);
                decrypted++;
            }
            else if (encrypted < 40 && data.Length < 4 << 20)
            {
                byte[] id0 = file.Resolve(file.Trailer["ID"]) is PdfArray { Count: > 0 } id && file.Resolve(id[0]) is PdfString s ? s.Value : new byte[16];
                if (file.Resolve(file.Trailer["ID"]) is null) file.Trailer.Set("ID", new PdfArray([new PdfString(id0), new PdfString(id0)]));
                Write(file, Path.Combine(folders.Out, "rc4", relative), StandardSecurity.CreateLegacyRc4("u", "o", unchecked((int)0xFFFFFFFC), id0, 16));
                encrypted++;
            }
        }

        Console.WriteLine($"decrypted {decrypted}, rc4 encrypted {encrypted}");
    }

    private static void Write(PdfFile file, string path, StandardSecurity? encryption)
    {
        ObjectGraph graph = ObjectGraph.Collect(file, false, false, CancellationToken.None);
        var objects = graph.Order.Select(file.GetObject).ToList();
        var index = new Dictionary<int, int>();
        for (int i = 0; i < graph.Order.Count; i++) index[graph.Order[i]] = i + 1;
        var trailer = new PdfDictionary();
        trailer.Set("Root", file.Trailer["Root"]!);
        if (file.Trailer["Info"] is { } info) trailer.Set("Info", info);
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);
        int encryptNumber = -1;
        if (encryption is not null)
        {
            objects.Add(encryption.Dictionary);
            encryptNumber = objects.Count;
            trailer.Set("Encrypt", new PdfReference(int.MaxValue, 0));
        }

        int Map(PdfReference r) => r.Number == int.MaxValue ? encryptNumber : index.TryGetValue(r.Number, out int n) ? n : -1;
        var settings = new WriterSettings(false, 200, "1.7", System.IO.Compression.CompressionLevel.Optimal)
        {
            Encryption = encryption,
            EncryptionDictionaryNumber = encryptNumber,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PdfDocumentWriter.Write(objects, trailer, Map, settings).ToArray());
    }
}
#endif
