using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Signing;

/// <summary>The hash a signature uses.</summary>
public enum PdfDigestAlgorithm
{
    /// <summary>SHA-256, the usual choice.</summary>
    Sha256,

    /// <summary>SHA-384.</summary>
    Sha384,

    /// <summary>SHA-512.</summary>
    Sha512,
}

/// <summary>A visible signature: a box on a page with the signer's name and the date.</summary>
/// <param name="PageIndex">Zero based page index.</param>
/// <param name="X">Left edge, points from the page's left.</param>
/// <param name="Y">Top edge, points from the page's top.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public sealed record PdfSignatureAppearance(int PageIndex, double X, double Y, double Width, double Height)
{
    /// <summary>The text shown; null for "Digitally signed by", the signer's name, the date and the reason.</summary>
    public string? Text { get; init; }

    /// <summary>The font of the text; null for Helvetica. Use an embedded font for PDF/A documents.</summary>
    public PdfFont? Font { get; init; }
}

/// <summary>How to sign.</summary>
public sealed record PdfSignatureOptions
{
    /// <summary>Why the document is signed.</summary>
    public string? Reason { get; init; }

    /// <summary>Where it is signed.</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The signature field's name; null picks Signature1, Signature2 and so on.</summary>
    public string? FieldName { get; init; }

    /// <summary>The hash. Default SHA-256.</summary>
    public PdfDigestAlgorithm Digest { get; init; } = PdfDigestAlgorithm.Sha256;

    /// <summary>Intermediate certificates to embed, so validators can build the chain to a trusted root.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    /// <summary>A visible signature, or null for an invisible one.</summary>
    public PdfSignatureAppearance? Appearance { get; init; }

    /// <summary>The signing time recorded in the signature dictionary; null for now.</summary>
    public DateTimeOffset? SigningTime { get; init; }
}

/// <summary>What <see cref="PdfSigner.Verify"/> found about one signature.</summary>
public sealed class PdfSignatureInfo
{
    internal PdfSignatureInfo()
    {
    }

    /// <summary>The signature field's fully qualified name.</summary>
    public string FieldName { get; internal set; } = "";

    /// <summary>The signer's common name, from the certificate.</summary>
    public string? SignerName { get; internal set; }

    /// <summary>The signing certificate, for the caller's trust decision.</summary>
    public X509Certificate2? Certificate { get; internal set; }

    /// <summary>The time the signer's software recorded (/M), not a trusted timestamp.</summary>
    public DateTimeOffset? SigningTime { get; internal set; }

    /// <summary>The reason given.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The location given.</summary>
    public string? Location { get; internal set; }

    /// <summary>The signature format, such as ETSI.CAdES.detached or adbe.pkcs7.detached.</summary>
    public string? SubFilter { get; internal set; }

    /// <summary>True when the signed bytes still hash to the value the signature covers.</summary>
    public bool DigestMatches { get; internal set; }

    /// <summary>True when the signature verifies with the certificate's public key.</summary>
    public bool SignatureValid { get; internal set; }

    /// <summary>True when the signature covers the whole file: nothing was appended after it.</summary>
    public bool CoversWholeDocument { get; internal set; }

    /// <summary>Why verification failed, or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The signed content is intact and the signature is cryptographically valid. Trust in the certificate is a separate decision.</summary>
    public bool IsIntact => DigestMatches && SignatureValid;
}

/// <summary>
/// PAdES baseline signatures (ETSI EN 319 142-1, B-B level): the document is signed in an
/// incremental update, so earlier signatures stay valid, with a CAdES detached CMS signature.
/// </summary>
/// <remarks>
/// The private key may be RSA or ECDSA, from a file or any key store X509Certificate2 can reach
/// (Windows certificate store, smart cards through their providers). Encrypted documents are not
/// supported.
/// </remarks>
public static class PdfSigner
{
    private const int ByteRangeDigits = 10;

    /// <summary>Signs <paramref name="pdf"/> and returns the signed file.</summary>
    /// <exception cref="ArgumentException">The certificate has no usable private key.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    public static byte[] Sign(byte[] pdf, X509Certificate2 certificate, PdfSignatureOptions? options = null)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(certificate, nameof(certificate));
        options ??= new PdfSignatureOptions();
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(certificate));

        PdfFile file = PdfFile.Open(pdf, 256L << 20);
        if (file.Trailer["Encrypt"] is not null and not PdfNull) throw new NotSupportedException("Signing encrypted documents is not supported.");
        PdfDocument doc = PdfDocument.Open(pdf);
        int size = file.Trailer["Size"] is PdfNumber { IsInteger: true } s ? (int)Math.Min(s.IntegerValue, int.MaxValue) : doc.NextNumber;
        int firstNew = Math.Max(size, doc.NextNumber);
        doc.NextNumber = firstNew;
        var changed = new SortedSet<int>();
        DateTimeOffset when = options.SigningTime ?? DateTimeOffset.UtcNow;

        // The signature value is written by hand; reserve its number.
        int signatureNumber = doc.NextNumber++;
        PdfPage page = doc.Pages[Math.Max(0, Math.Min(options.Appearance?.PageIndex ?? 0, doc.Pages.Count - 1))];
        string fieldName = options.FieldName ?? UniqueFieldName(doc);
        var widget = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Widget"),
            ["FT"] = new PdfName("Sig"),
            ["T"] = PdfString.FromText(fieldName),
            ["V"] = new PdfReference(signatureNumber, 0),
            ["F"] = PdfNumber.FromInteger(132), // print, locked
            ["P"] = new PdfReference(page.Number, 0),
            ["Rect"] = PdfArray.Of(0, 0, 0, 0),
        };
        if (options.Appearance is PdfSignatureAppearance look) Appear(doc, page, widget, look, certificate, options, when);
        PdfReference widgetRef = doc.AddObject(widget);

        // The page lists the widget; the form lists the field and says signatures exist.
        if (page.Dictionary["Annots"] is PdfReference annotsRef && doc.Resolve(annotsRef) is PdfArray annots)
        {
            annots.Add(widgetRef);
            changed.Add(annotsRef.Number);
        }
        else
        {
            var list = doc.Resolve(page.Dictionary["Annots"]) as PdfArray ?? new PdfArray();
            var copy = new PdfArray();
            foreach (PdfObject o in list) copy.Add(o);
            copy.Add(widgetRef);
            page.Dictionary.Set("Annots", copy);
            changed.Add(page.Number);
        }

        PdfDictionary form;
        if (doc.Catalog["AcroForm"] is PdfReference formRef && doc.Resolve(formRef) is PdfDictionary existingForm)
        {
            form = existingForm;
            changed.Add(formRef.Number);
        }
        else
        {
            form = doc.Resolve(doc.Catalog["AcroForm"]) as PdfDictionary ?? new PdfDictionary();
            doc.Catalog.Set("AcroForm", form);
            changed.Add(doc.CatalogNumber);
        }

        if (form["Fields"] is PdfReference fieldsRef && doc.Resolve(fieldsRef) is PdfArray fieldsArray)
        {
            fieldsArray.Add(widgetRef);
            changed.Add(fieldsRef.Number);
        }
        else
        {
            var fields = new PdfArray();
            if (doc.Resolve(form["Fields"]) is PdfArray old)
            {
                foreach (PdfObject o in old) fields.Add(o);
            }

            fields.Add(widgetRef);
            form.Set("Fields", fields);
        }

        form.Set("SigFlags", PdfNumber.FromInteger(3));
        doc.FinishFonts();

        // Everything new is written, plus the existing objects that changed.
        var objects = new SortedDictionary<int, PdfObject>();
        foreach (int n in changed) objects[n] = doc.Objects[n];
        foreach (KeyValuePair<int, PdfObject> o in doc.Objects)
        {
            if (o.Key >= firstNew) objects[o.Key] = o.Value;
        }

        byte[][] chain = [.. options.Chain.Select(c => c.RawData)];
        int reserve = 8192 + certificate.RawData.Length + chain.Sum(c => c.Length);
        (byte[] output, int byteRangeAt, int contentsStart, int contentsEnd) = WriteUpdate(pdf, file, objects, signatureNumber, reserve, options, certificate, when);

        // The byte range covers everything but the Contents hex string, delimiters included.
        long[] range = [0, contentsStart, contentsEnd, output.Length - contentsEnd];
        byte[] digits = Encoding.ASCII.GetBytes(string.Join(" ", range.Select(r => r.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture))));
        digits.CopyTo(output, byteRangeAt);

        HashAlgorithmName hash = HashName(options.Digest);
        byte[] digest = Hash(hash, output, contentsStart, contentsEnd);
        byte[] cms = Cms.Sign(digest, hash, certificate, chain);
        if (2 * cms.Length > contentsEnd - contentsStart - 2) throw new InvalidOperationException("The signature is larger than the space reserved for it.");
        byte[] hex = Encoding.ASCII.GetBytes(Compat.ToHex(cms));
        hex.CopyTo(output, contentsStart + 1);
        return output;
    }

    /// <summary>Checks every signature of a file.</summary>
    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] pdf)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        PdfDocument doc = PdfDocument.Open(pdf);
        var results = new List<PdfSignatureInfo>();
        foreach (PdfSignatureField field in doc.Form.Fields.OfType<PdfSignatureField>())
        {
            if (doc.Resolve(field.Dictionary["V"]) is not PdfDictionary sig) continue;
            var info = new PdfSignatureInfo
            {
                FieldName = field.Name,
                SubFilter = sig.GetName("SubFilter"),
                Reason = doc.Resolve(sig["Reason"]) is PdfString r ? r.ToText() : null,
                Location = doc.Resolve(sig["Location"]) is PdfString l ? l.ToText() : null,
                SigningTime = doc.Resolve(sig["M"]) is PdfString m ? Documents.PdfText.ParseDate(m.ToText()) : null,
            };
            results.Add(info);
            try
            {
                long[] range = doc.Resolve(sig["ByteRange"]) is PdfArray { Count: 4 } a ? [.. a.Select(v => ((PdfNumber)doc.Resolve(v)!).IntegerValue)] : [];
                if (range.Length != 4 || range[0] != 0 || range[1] < 0 || range[2] < range[1] || range[3] < 0 || range[2] + range[3] > pdf.Length)
                {
                    info.Problem = "The byte range is invalid.";
                    continue;
                }

                info.CoversWholeDocument = range[2] + range[3] == pdf.Length;
                byte[] cms = ((PdfString)doc.Resolve(sig["Contents"])!).Value;
                Cms.Verify(cms, pdf, (int)range[1], (int)range[2], (int)(range[2] + range[3]), info);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or CryptographicException or NullReferenceException)
            {
                info.Problem = "The signature cannot be read: " + e.Message;
            }
        }

        return results;
    }

    private static string UniqueFieldName(PdfDocument doc)
    {
        var names = new HashSet<string>(doc.Form.Fields.Select(f => f.Name));
        for (int i = 1; ; i++)
        {
            if (!names.Contains("Signature" + i)) return "Signature" + i;
        }
    }

    private static void Appear(PdfDocument doc, PdfPage page, PdfDictionary widget, PdfSignatureAppearance look, X509Certificate2 cert, PdfSignatureOptions options, DateTimeOffset when)
    {
        PdfRectangle box = page.MediaBox;
        widget.Set("Rect", PdfArray.Of(box.Left + look.X, box.Top - look.Y - look.Height, box.Left + look.X + look.Width, box.Top - look.Y));
        string text = look.Text ?? "Digitally signed by " + (cert.GetNameInfo(X509NameType.SimpleName, false) ?? "") + "\nDate: "
            + when.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + (options.Reason is string reason ? "\nReason: " + reason : "");
        PdfForm appearance = PdfForm.Create(doc, look.Width, look.Height);
        using (PdfGraphics g = appearance.CreateGraphics())
        {
            g.DrawRectangle(new PdfPen(PdfColor.Gray(0.6), 0.75), PdfColor.Gray(0.97), 0.5, 0.5, look.Width - 1, look.Height - 1);
            double size = Math.Max(5, Math.Min(10, look.Height / (text.Split('\n').Length * 1.4)));
            g.IntersectClip(3, 3, look.Width - 6, look.Height - 6);
            g.DrawText(text, new PdfTextStyle(look.Font ?? PdfStandardFont.Helvetica, size) { Brush = PdfColor.Gray(0.15) }, 5, 4, Math.Max(1, look.Width - 10));
        }

        widget.Set("AP", new PdfDictionary { ["N"] = appearance.Reference });
    }

    /// <summary>Appends the objects, the signature value with placeholders, and a cross-reference section matching the file's style.</summary>
    private static (byte[] Output, int ByteRangeAt, int ContentsStart, int ContentsEnd) WriteUpdate(
        byte[] original, PdfFile file, SortedDictionary<int, PdfObject> objects, int signatureNumber, int reserve,
        PdfSignatureOptions options, X509Certificate2 certificate, DateTimeOffset when)
    {
        var sink = new ByteSink(original.Length + reserve * 2 + 16384);
        sink.Write(original);
        if (sink.LastByte is not ((byte)'\n' or (byte)'\r')) sink.Write((byte)'\n');
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();

        foreach ((int number, PdfObject obj) in objects)
        {
            int generation = file.Exists(number) ? file.GenerationOf(number) : 0;
            offsets[number] = (sink.Length, generation);
            sink.WriteAscii($"{number} {generation} obj\n");
            if (obj is PdfStream stream)
            {
                PdfDictionary d = PdfDocument.Clone(stream.Dictionary);
                d.Set("Length", PdfNumber.FromInteger(stream.Data.Length));
                serializer.WriteDictionary(sink, d, skipKey: null);
                sink.Write("\nstream\n"u8);
                sink.Write(stream.Data.Span);
                sink.Write("\nendstream"u8);
            }
            else
            {
                serializer.Write(sink, obj);
            }

            sink.Write("\nendobj\n"u8);
        }

        offsets[signatureNumber] = (sink.Length, 0);
        sink.WriteAscii($"{signatureNumber} 0 obj\n<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/ETSI.CAdES.detached/ByteRange[");
        int byteRangeAt = sink.Length;
        sink.WriteAscii(string.Join(" ", Enumerable.Repeat(new string('0', ByteRangeDigits), 4)));
        sink.WriteAscii("]/Contents");
        int contentsStart = sink.Length;
        sink.Write((byte)'<');
        for (int i = 0; i < 2 * reserve; i++) sink.Write((byte)'0');
        sink.Write((byte)'>');
        int contentsEnd = sink.Length;
        sink.WriteAscii("/M");
        PdfSerializer.WriteString(sink, Encoding.ASCII.GetBytes(Documents.PdfText.FormatDate(when)));
        WriteText(sink, "Name", certificate.GetNameInfo(X509NameType.SimpleName, false));
        WriteText(sink, "Reason", options.Reason);
        WriteText(sink, "Location", options.Location);
        WriteText(sink, "ContactInfo", options.ContactInfo);
        sink.Write(">>\nendobj\n"u8);

        int maxNumber = offsets.Keys.Max();
        PdfDictionary trailer = new()
        {
            ["Size"] = PdfNumber.FromInteger(Math.Max(maxNumber + (file.UsesXrefStreams ? 2 : 1), file.Trailer["Size"] is PdfNumber { IsInteger: true } sz ? sz.IntegerValue : 0)),
            ["Root"] = file.Trailer["Root"]!,
            ["Prev"] = PdfNumber.FromInteger(LastStartXref(original)),
        };
        if (file.Trailer["Info"] is PdfObject info) trailer.Set("Info", info);
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);

        long xref = sink.Length;
        if (file.UsesXrefStreams)
        {
            int xrefNumber = maxNumber + 1;
            offsets[xrefNumber] = (xref, 0);
            var rows = new List<byte>();
            var index = new PdfArray();
            foreach ((int start, int count) in Runs(offsets.Keys))
            {
                index.Add(PdfNumber.FromInteger(start));
                index.Add(PdfNumber.FromInteger(count));
                for (int n = start; n < start + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    rows.Add(1);
                    for (int k = 3; k >= 0; k--) rows.Add((byte)(offset >> (8 * k)));
                    rows.Add((byte)(generation >> 8));
                    rows.Add((byte)generation);
                }
            }

            trailer.Set("Type", new PdfName("XRef"));
            trailer.Set("Size", PdfNumber.FromInteger(Math.Max(xrefNumber + 1, ((PdfNumber)trailer["Size"]!).IntegerValue)));
            trailer.Set("Index", index);
            trailer.Set("W", PdfArray.Of(1, 4, 2));
            trailer.Set("Length", PdfNumber.FromInteger(rows.Count));
            sink.WriteAscii($"{xrefNumber} 0 obj\n");
            serializer.WriteDictionary(sink, trailer, skipKey: null);
            sink.Write("\nstream\n"u8);
            sink.Write([.. rows]);
            sink.Write("\nendstream\nendobj\n"u8);
        }
        else
        {
            sink.Write("xref\n"u8);
            foreach ((int start, int count) in Runs(offsets.Keys))
            {
                sink.WriteAscii($"{start} {count}\n");
                for (int n = start; n < start + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    sink.WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " " + generation.ToString("D5", CultureInfo.InvariantCulture) + " n\r\n");
                }
            }

            sink.Write("trailer\n"u8);
            serializer.WriteDictionary(sink, trailer, skipKey: null);
            sink.Write((byte)'\n');
        }

        sink.WriteAscii($"startxref\n{xref}\n%%EOF\n");
        return (sink.ToArray(), byteRangeAt, contentsStart, contentsEnd);
    }

    private static void WriteText(ByteSink sink, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        PdfSerializer.WriteName(sink, key);
        PdfSerializer.WriteString(sink, PdfString.FromText(value!).Value);
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> numbers)
    {
        int start = -1, previous = -2;
        foreach (int n in numbers)
        {
            if (n != previous + 1)
            {
                if (start >= 0) yield return (start, previous - start + 1);
                start = n;
            }

            previous = n;
        }

        if (start >= 0) yield return (start, previous - start + 1);
    }

    private static long LastStartXref(byte[] pdf)
    {
        int at = pdf.AsSpan().LastIndexOf("startxref"u8);
        if (at < 0) throw new PdfFormatException("startxref not found.");
        int i = at + 9;
        while (i < pdf.Length && pdf[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') i++;
        long value = 0;
        while (i < pdf.Length && pdf[i] is >= (byte)'0' and <= (byte)'9') value = value * 10 + (pdf[i++] - '0');
        return value;
    }

    internal static HashAlgorithmName HashName(PdfDigestAlgorithm digest) => digest switch
    {
        PdfDigestAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        PdfDigestAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    /// <summary>The hash of the file outside the Contents hex string.</summary>
    internal static byte[] Hash(HashAlgorithmName name, byte[] data, int gapStart, int gapEnd, int? end = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data, 0, gapStart);
        hash.AppendData(data, gapEnd, (end ?? data.Length) - gapEnd);
        return hash.GetHashAndReset();
    }
}
