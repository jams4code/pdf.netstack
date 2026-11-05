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
    /// <summary>Also run the exhaustive Flate encoder on object streams.</summary>
    public bool Exhaustive { get; init; }

    /// <summary>Encrypts every in-file object except the encryption dictionary and the xref stream.</summary>
    public Security.StandardSecurity? Encryption { get; init; }

    /// <summary>Output number of the encryption dictionary, which stays unencrypted and outside object streams.</summary>
    public int EncryptionDictionaryNumber { get; init; } = -1;
}

/// <summary>
/// Serializes a renumbered object set as a fresh, single revision PDF. Non-stream objects are
/// packed into compressed object streams and the cross-reference table is written as a
/// predictor-encoded, compressed xref stream. A classic table writer covers PDF/A-1, which
/// predates object streams. The same bytes come out whole, for a file built in memory, or as a
/// sequence of chunks, for a file written to a stream as it is made.
/// </summary>
internal static class PdfDocumentWriter
{
    private static readonly byte[] BinaryMarker = [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];

    /// <summary>
    /// Progressive output hands its buffer on once it holds this much, and passes stream data of
    /// at least this size on directly instead of copying it.
    /// </summary>
    internal const int ChunkSize = 64 * 1024;

    /// <param name="objects">Object i is written with number i + 1.</param>
    /// <param name="trailer">Trailer entries to keep, typically Root, Info and ID.</param>
    /// <param name="mapReference">Maps any reference found in the objects to an output number, or -1.</param>
    public static ReadOnlyMemory<byte> Write(
        IReadOnlyList<PdfObject> objects,
        PdfDictionary trailer,
        Func<PdfReference, int> mapReference,
        WriterSettings settings)
    {
        long estimate = 1024;
        foreach (PdfObject o in objects)
        {
            if (o is PdfStream s) estimate += s.EncodedLength + 64;
            else estimate += 48;
        }

        var output = new Output(new ByteSink((int)Math.Min(estimate, Compat.MaxArrayLength)), progressive: false);
        foreach (ReadOnlyMemory<byte> unused in Produce(output, objects, trailer, mapReference, settings, null, CancellationToken.None))
        {
            // In memory nothing is handed out: the whole file stays in the buffer.
        }

        return output.Sink.WrittenMemory;
    }

    /// <summary>
    /// The file <see cref="Write"/> builds, byte for byte, as a sequence of chunks to write out in
    /// order. A chunk is valid only until the next one is requested. Besides the chunk being
    /// handed out, the writer holds one object, or one batch of objects for an object stream,
    /// and the cross-reference data, a few bytes per object.
    /// </summary>
    /// <param name="rewrite">
    /// Given an output number and the stream about to be written under it, returns the stream to
    /// write instead. Streams are asked for in increasing number order, each once.
    /// </param>
    /// <param name="ct">Observed before each object.</param>
    public static IEnumerable<ReadOnlyMemory<byte>> WriteChunks(
        IReadOnlyList<PdfObject> objects,
        PdfDictionary trailer,
        Func<PdfReference, int> mapReference,
        WriterSettings settings,
        Func<int, PdfStream, PdfStream>? rewrite = null,
        CancellationToken ct = default) =>
        Produce(new Output(new ByteSink(ChunkSize + 1024), progressive: true), objects, trailer, mapReference, settings, rewrite, ct);

    /// <summary>Where the writer puts bytes, and how many it has handed out.</summary>
    private sealed class Output(ByteSink sink, bool progressive)
    {
        private long _handedOut;

        public ByteSink Sink { get; } = sink;

        /// <summary>Chunks go out as the file is made; otherwise the whole file stays in <see cref="Sink"/>.</summary>
        public bool Progressive { get; } = progressive;

        /// <summary>Offset in the file of the next byte written.</summary>
        public long Position => _handedOut + Sink.Length;

        /// <summary>The buffer is due to be handed out.</summary>
        public bool Full => Progressive && Sink.Length >= ChunkSize;

        /// <summary>Data this large goes out on its own rather than through the buffer.</summary>
        public bool PassesOn(int length) => Progressive && length >= ChunkSize;

        /// <summary>Records that the buffer's content went out, and empties it.</summary>
        public void Release()
        {
            _handedOut += Sink.Length;
            Sink.Clear();
        }

        /// <summary>Records data that went out directly.</summary>
        public void Passed(int length) => _handedOut += length;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Produce(
        Output output,
        IReadOnlyList<PdfObject> objects,
        PdfDictionary trailer,
        Func<PdfReference, int> mapReference,
        WriterSettings settings,
        Func<int, PdfStream, PdfStream>? rewrite,
        CancellationToken ct)
    {
        var serializer = new PdfSerializer(mapReference);
        output.Sink.WriteAscii("%PDF-" + settings.Version + "\n");
        output.Sink.Write(BinaryMarker);
        IEnumerable<ReadOnlyMemory<byte>> body = settings.UseObjectStreams
            ? WriteCompact(output, objects, trailer, serializer, settings, rewrite, ct)
            : WriteClassic(output, objects, trailer, serializer, settings, rewrite, ct);
        foreach (ReadOnlyMemory<byte> chunk in body) yield return chunk;
        if (output.Progressive && output.Sink.Length > 0) yield return output.Sink.WrittenMemory;
    }

    /// <summary>Writes one indirect object, handing out the buffer and large stream data as they become due.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> WriteObject(Output output, PdfSerializer serializer, int number, PdfObject obj, WriterSettings settings, Func<int, PdfStream, PdfStream>? rewrite)
    {
        if (obj is PdfStream stream)
        {
            if (rewrite is not null) stream = rewrite(number, stream);
            ReadOnlyMemory<byte> data = BeginStream(output.Sink, serializer, number, stream, settings);
            foreach (ReadOnlyMemory<byte> chunk in Payload(output, data)) yield return chunk;
            output.Sink.Write("\nendstream\nendobj\n"u8);
        }
        else
        {
            WriteIndirect(output.Sink, serializer, number, obj, settings);
        }

        if (output.Full)
        {
            yield return output.Sink.WrittenMemory;
            output.Release();
        }
    }

    /// <summary>Stream data: copied into the buffer, or, when large and the output is progressive, handed out as it is.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> Payload(Output output, ReadOnlyMemory<byte> data)
    {
        if (!output.PassesOn(data.Length))
        {
            output.Sink.Write(data.Span);
            yield break;
        }

        if (output.Sink.Length > 0)
        {
            yield return output.Sink.WrittenMemory;
            output.Release();
        }

        yield return data;
        output.Passed(data.Length);
    }

    private static IEnumerable<ReadOnlyMemory<byte>> WriteClassic(Output output, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, PdfSerializer serializer, WriterSettings settings, Func<int, PdfStream, PdfStream>? rewrite, CancellationToken ct)
    {
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            offsets[i + 1] = output.Position;
            foreach (ReadOnlyMemory<byte> chunk in WriteObject(output, serializer, i + 1, objects[i], settings, rewrite)) yield return chunk;
        }

        ByteSink sink = output.Sink;
        long xref = output.Position;
        sink.WriteAscii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
        {
            sink.WriteAscii($"{offsets[i]:D10} 00000 n \n");
            if (output.Full)
            {
                yield return sink.WrittenMemory;
                output.Release();
            }
        }

        var t = new PdfDictionary();
        t.Set("Size", PdfNumber.FromInteger(objects.Count + 1));
        CopyTrailerEntries(trailer, t);
        sink.Write("trailer\n"u8);
        serializer.WriteDictionary(sink, t, skipKey: null);
        sink.WriteAscii($"\nstartxref\n{xref}\n%%EOF\n");
    }

    private static IEnumerable<ReadOnlyMemory<byte>> WriteCompact(Output output, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, PdfSerializer serializer, WriterSettings settings, Func<int, PdfStream, PdfStream>? rewrite, CancellationToken ct)
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
            ct.ThrowIfCancellationRequested();
            types[number] = 1;
            field2[number] = output.Position;
            foreach (ReadOnlyMemory<byte> chunk in WriteObject(output, serializer, number, objects[i], settings, rewrite)) yield return chunk;
        }

        ByteSink sink = output.Sink;
        var header = new ByteSink(1024);
        var body = new ByteSink(16 * 1024);
        for (int s = 0; s < streamCount; s++)
        {
            ct.ThrowIfCancellationRequested();
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
            byte[] compressed = StreamCodec.Deflate(header.WrittenSpan, settings.CompressionLevel, settings.Exhaustive, ct);

            // Objects inside an object stream are protected by encrypting the stream as a whole.
            if (settings.Encryption is { } encryption) compressed = encryption.Encrypt(streamNumber, 0, compressed, isString: false);

            types[streamNumber] = 1;
            field2[streamNumber] = output.Position;
            sink.WriteInteger(streamNumber);
            sink.Write(" 0 obj\n<</Type/ObjStm/N "u8);
            sink.WriteInteger(last - first);
            sink.Write("/First "u8);
            sink.WriteInteger(firstOffset);
            sink.Write("/Filter/FlateDecode/Length "u8);
            sink.WriteInteger(compressed.Length);
            sink.Write(">>stream\n"u8);
            foreach (ReadOnlyMemory<byte> chunk in Payload(output, compressed)) yield return chunk;
            sink.Write("\nendstream\nendobj\n"u8);
            if (output.Full)
            {
                yield return sink.WrittenMemory;
                output.Release();
            }
        }

        long xrefOffset = output.Position;
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

    /// <summary>Writes one object as "n 0 obj ... endobj", a stream with its /Length set directly.</summary>
    internal static void WriteIndirect(ByteSink sink, PdfSerializer serializer, int number, PdfObject obj, WriterSettings settings)
    {
        if (obj is PdfStream stream)
        {
            sink.Write(BeginStream(sink, serializer, number, stream, settings).Span);
            sink.Write("\nendstream\nendobj\n"u8);
            return;
        }

        serializer.StringTransform = StringTransform(number, settings);
        try
        {
            sink.WriteInteger(number);
            sink.Write(" 0 obj\n"u8);
            serializer.Write(sink, obj);
            sink.Write("\nendobj\n"u8);
        }
        finally
        {
            serializer.StringTransform = null;
        }
    }

    /// <summary>
    /// Writes a stream object up to and including the "stream" keyword and returns the data that
    /// follows it, encrypted when the settings say so. The caller writes the data and "endstream".
    /// </summary>
    private static ReadOnlyMemory<byte> BeginStream(ByteSink sink, PdfSerializer serializer, int number, PdfStream stream, WriterSettings settings)
    {
        Security.StandardSecurity? encryption = number == settings.EncryptionDictionaryNumber ? null : settings.Encryption;
        ReadOnlyMemory<byte> data = stream.Data;
        bool clearMetadata = encryption is { EncryptMetadata: false } && stream.Dictionary.GetName("Type") == "Metadata";
        if (encryption is not null && !clearMetadata) data = encryption.Encrypt(number, 0, data.Span, isString: false);

        serializer.StringTransform = StringTransform(number, settings);
        try
        {
            sink.WriteInteger(number);
            sink.Write(" 0 obj\n"u8);
            WriteStreamDictionary(sink, serializer, stream.Dictionary, data.Length);
            sink.Write("stream\n"u8);
        }
        finally
        {
            serializer.StringTransform = null;
        }

        return data;
    }

    private static Func<byte[], byte[]>? StringTransform(int number, WriterSettings settings)
    {
        Security.StandardSecurity? encryption = number == settings.EncryptionDictionaryNumber ? null : settings.Encryption;
        return encryption is null ? null : value => encryption.Encrypt(number, 0, value, isString: true);
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
