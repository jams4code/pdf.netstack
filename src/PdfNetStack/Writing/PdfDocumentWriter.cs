using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Objects;

namespace PdfNetStack.Writing;

internal sealed record WriterSettings(
    bool UseObjectStreams,
    int ObjectsPerStream,
    string Version,
    CompressionLevel CompressionLevel)
{
    /// <summary>Encrypts every in-file object except the encryption dictionary and the xref stream.</summary>
    public Security.StandardSecurity? Encryption { get; init; }

    /// <summary>Output number of the encryption dictionary, which stays unencrypted and outside object streams.</summary>
    public int EncryptionDictionaryNumber { get; init; } = -1;
}

/// <summary>
/// Serializes a renumbered object set as a fresh, single revision PDF. Non-stream objects are
/// packed into compressed object streams and the cross-reference table is written as a
/// predictor-encoded, compressed xref stream. A classic table writer covers PDF/A-1, which
/// predates object streams.
/// </summary>
internal static class PdfDocumentWriter
{
    private static readonly byte[] BinaryMarker = [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];

    /// <param name="objects">Object i is written with number i + 1.</param>
    /// <param name="trailer">Trailer entries to keep, typically Root, Info and ID.</param>
    /// <param name="mapReference">Maps any reference found in the objects to an output number, or -1.</param>
    public static ReadOnlyMemory<byte> Write(
        IReadOnlyList<PdfObject> objects,
        PdfDictionary trailer,
        Func<PdfReference, int> mapReference,
        WriterSettings settings)
    {
        var serializer = new PdfSerializer(mapReference);
        long estimate = 1024;
        foreach (PdfObject o in objects)
        {
            if (o is PdfStream s) estimate += s.Data.Length + 64;
            else estimate += 48;
        }

        var sink = new ByteSink((int)Math.Min(estimate, Compat.MaxArrayLength));
        sink.WriteAscii("%PDF-" + settings.Version + "\n");
        sink.Write(BinaryMarker);

        return settings.UseObjectStreams
            ? WriteCompact(sink, objects, trailer, serializer, settings)
            : WriteClassic(sink, objects, trailer, serializer, settings);
    }

    private static ReadOnlyMemory<byte> WriteClassic(ByteSink sink, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, PdfSerializer serializer, WriterSettings settings)
    {
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = sink.Length;
            WriteIndirect(sink, serializer, i + 1, objects[i], settings);
        }

        long xref = sink.Length;
        sink.WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++) sink.WriteAscii($"{offsets[i]:D10} 00000 n \n");

        var t = new PdfDictionary();
        t.Set("Size", PdfNumber.FromInteger(objects.Count + 1));
        CopyTrailerEntries(trailer, t);
        sink.Write("trailer\n"u8);
        serializer.WriteDictionary(sink, t, skipKey: null);
        sink.WriteAscii($"\nstartxref\n{xref}\n%%EOF\n");
        return sink.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> WriteCompact(ByteSink sink, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, PdfSerializer serializer, WriterSettings settings)
    {
        int count = objects.Count;
        var packed = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (objects[i] is not PdfStream && i + 1 != settings.EncryptionDictionaryNumber) packed.Add(i + 1);
        }

        int streamCount = (packed.Count + settings.ObjectsPerStream - 1) / settings.ObjectsPerStream;
        int xrefNumber = count + streamCount + 1;
        int size = xrefNumber + 1;

        // Type, field 2, field 3 for every object number 0..size-1.
        var types = new byte[size];
        var field2 = new long[size];
        var field3 = new int[size];
        types[0] = 0;
        field3[0] = 65535;

        for (int i = 0; i < count; i++)
        {
            int number = i + 1;
            if (objects[i] is not PdfStream && number != settings.EncryptionDictionaryNumber) continue;
            types[number] = 1;
            field2[number] = sink.Length;
            WriteIndirect(sink, serializer, number, objects[i], settings);
        }

        var header = new ByteSink(1024);
        var body = new ByteSink(16 * 1024);
        for (int s = 0; s < streamCount; s++)
        {
            int streamNumber = count + 1 + s;
            header.Clear();
            body.Clear();
            int first = s * settings.ObjectsPerStream;
            int last = Math.Min(packed.Count, first + settings.ObjectsPerStream);
            for (int k = first; k < last; k++)
            {
                int number = packed[k];
                if (k > first) header.Write((byte)' ');
                header.WriteInteger(number);
                header.Write((byte)' ');
                header.WriteInteger(body.Length);
                serializer.Write(body, objects[number - 1]);
                types[number] = 2;
                field2[number] = streamNumber;
                field3[number] = k - first;
            }

            header.Write((byte)'\n');
            int firstOffset = header.Length;
            header.Write(body.WrittenSpan);
            byte[] compressed = StreamCodec.Deflate(header.WrittenSpan, settings.CompressionLevel);

            // Objects inside an object stream are protected by encrypting the stream as a whole.
            if (settings.Encryption is { } encryption) compressed = encryption.Encrypt(streamNumber, 0, compressed, isString: false);

            types[streamNumber] = 1;
            field2[streamNumber] = sink.Length;
            sink.WriteInteger(streamNumber);
            sink.Write(" 0 obj\n<</Type/ObjStm/N "u8);
            sink.WriteInteger(last - first);
            sink.Write("/First "u8);
            sink.WriteInteger(firstOffset);
            sink.Write("/Filter/FlateDecode/Length "u8);
            sink.WriteInteger(compressed.Length);
            sink.Write(">>stream\n"u8);
            sink.Write(compressed);
            sink.Write("\nendstream\nendobj\n"u8);
        }

        long xrefOffset = sink.Length;
        types[xrefNumber] = 1;
        field2[xrefNumber] = xrefOffset;

        int w1 = BytesNeeded(Math.Max(xrefOffset, count + streamCount));
        int w2 = 2;
        int row = 1 + w1 + w2;
        var table = new byte[size * row];
        for (int n = 0; n < size; n++)
        {
            int o = n * row;
            table[o] = types[n];
            for (int b = 0; b < w1; b++) table[o + 1 + b] = (byte)(field2[n] >> (8 * (w1 - 1 - b)));
            table[o + 1 + w1] = (byte)(field3[n] >> 8);
            table[o + 2 + w1] = (byte)field3[n];
        }

        var predictor = new PredictorParams(12, 1, 8, row);
        byte[] xrefData;
        using (BoundedBuffer predicted = Predictors.EncodePng(table, predictor))
        {
            xrefData = StreamCodec.Deflate(predicted.Span, settings.CompressionLevel);
        }

        var x = new PdfDictionary();
        x.Set("Type", new PdfName("XRef"));
        x.Set("Size", PdfNumber.FromInteger(size));
        x.Set("W", new PdfArray([PdfNumber.FromInteger(1), PdfNumber.FromInteger(w1), PdfNumber.FromInteger(w2)]));
        CopyTrailerEntries(trailer, x);
        x.Set("Filter", new PdfName("FlateDecode"));
        x.Set("DecodeParms", StreamCodec.PredictorDictionary(predictor));

        sink.WriteInteger(xrefNumber);
        sink.Write(" 0 obj\n"u8);
        WriteStreamDictionary(sink, serializer, x, xrefData.Length);
        sink.Write("stream\n"u8);
        sink.Write(xrefData);
        sink.Write("\nendstream\nendobj\n"u8);
        sink.WriteAscii($"startxref\n{xrefOffset}\n%%EOF\n");
        return sink.WrittenMemory;
    }

    private static void CopyTrailerEntries(PdfDictionary source, PdfDictionary target)
    {
        foreach (string key in new[] { "Root", "Info", "ID", "Encrypt" })
        {
            if (source[key] is { } value) target.Set(key, value);
        }
    }

    private static int BytesNeeded(long value)
    {
        int n = 1;
        while (n < 8 && value >= 1L << (8 * n)) n++;
        return n;
    }

    private static void WriteIndirect(ByteSink sink, PdfSerializer serializer, int number, PdfObject obj, WriterSettings settings)
    {
        Security.StandardSecurity? encryption = number == settings.EncryptionDictionaryNumber ? null : settings.Encryption;
        serializer.StringTransform = encryption is null ? null : value => encryption.Encrypt(number, 0, value, isString: true);
        try
        {
            sink.WriteInteger(number);
            sink.Write(" 0 obj\n"u8);
            if (obj is PdfStream stream)
            {
                ReadOnlySpan<byte> data = stream.Data.Span;
                bool clearMetadata = encryption is { EncryptMetadata: false } && stream.Dictionary.GetName("Type") == "Metadata";
                byte[]? encrypted = encryption is null || clearMetadata ? null : encryption.Encrypt(number, 0, data, isString: false);
                if (encrypted is not null) data = encrypted;
                WriteStreamDictionary(sink, serializer, stream.Dictionary, data.Length);
                sink.Write("stream\n"u8);
                sink.Write(data);
                sink.Write("\nendstream\nendobj\n"u8);
            }
            else
            {
                serializer.Write(sink, obj);
                sink.Write("\nendobj\n"u8);
            }
        }
        finally
        {
            serializer.StringTransform = null;
        }
    }

    private static void WriteStreamDictionary(ByteSink sink, PdfSerializer serializer, PdfDictionary dict, int length)
    {
        var copy = new PdfDictionary(dict.Count + 1);
        copy.Set("Length", PdfNumber.FromInteger(length));
        for (int i = 0; i < dict.Count; i++)
        {
            (string key, PdfObject value) = dict.EntryAt(i);
            if (key != "Length") copy.Set(key, value);
        }

        serializer.WriteDictionary(sink, copy, skipKey: null);
    }
}
