using System.Globalization;
using PdfNetStack.Objects;

namespace PdfNetStack.Writing;

/// <summary>
/// Writes an incremental update (ISO 32000-1, 7.5.6): changed and new objects under their own
/// numbers and generations, then a cross-reference section for just those objects whose trailer
/// points back to the previous section with /Prev. A file whose last section is a
/// cross-reference stream gets a stream again, so readers that require one keep working.
/// </summary>
internal static class IncrementalWriter
{
    /// <param name="objects">The objects to write, by number, with their generations.</param>
    /// <param name="trailer">Root, Info and ID for the new trailer.</param>
    /// <param name="start">Offset in the file at which the update begins.</param>
    /// <param name="offsetBase">Subtracted from every offset written, for files that count from a shifted header.</param>
    /// <param name="previousSize">/Size of the file being updated.</param>
    /// <param name="previousXref">The file's last startxref value, for /Prev.</param>
    /// <param name="xrefStream">Write the cross-reference section as a stream.</param>
    /// <param name="freeNumber">An unused object number for the cross-reference stream.</param>
    public static byte[] Write(
        IReadOnlyDictionary<int, (PdfObject Object, int Generation)> objects,
        PdfDictionary trailer,
        long start,
        long offsetBase,
        long previousSize,
        long previousXref,
        bool xrefStream,
        int freeNumber)
    {
        var sink = new ByteSink(4096);
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach ((int number, (PdfObject obj, int generation)) in objects.OrderBy(o => o.Key))
        {
            offsets[number] = (start + sink.Length - offsetBase, generation);
            sink.WriteInteger(number);
            sink.Write((byte)' ');
            sink.WriteInteger(generation);
            sink.Write(" obj\n"u8);
            if (obj is PdfStream stream)
            {
                ReadOnlyMemory<byte> data = stream.Data;
                var dict = new PdfDictionary(stream.Dictionary.Count + 1);
                dict.Set("Length", PdfNumber.FromInteger(data.Length));
                for (int i = 0; i < stream.Dictionary.Count; i++)
                {
                    (string key, PdfObject value) = stream.Dictionary.EntryAt(i);
                    if (key != "Length") dict.Set(key, value);
                }

                serializer.WriteDictionary(sink, dict, skipKey: null);
                sink.Write("stream\n"u8);
                sink.Write(data.Span);
                sink.Write("\nendstream\nendobj\n"u8);
            }
            else
            {
                serializer.Write(sink, obj);
                sink.Write("\nendobj\n"u8);
            }
        }

        int highest = offsets.Count == 0 ? 0 : offsets.Keys.Max();
        var t = new PdfDictionary();
        foreach (string key in new[] { "Root", "Info", "ID" })
        {
            if (trailer[key] is { } value) t.Set(key, value);
        }

        t.Set("Prev", PdfNumber.FromInteger(previousXref));
        long xref = start + sink.Length - offsetBase;
        if (xrefStream)
        {
            offsets[freeNumber] = (xref, 0);
            t.Set("Size", PdfNumber.FromInteger(Math.Max(previousSize, Math.Max(highest, freeNumber) + 1)));
            var rows = new ByteSink(offsets.Count * 11);
            var index = new PdfArray();
            foreach ((int first, int count) in Runs(offsets.Keys))
            {
                index.Add(PdfNumber.FromInteger(first));
                index.Add(PdfNumber.FromInteger(count));
                for (int n = first; n < first + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    rows.Write(1);
                    for (int k = 7; k >= 0; k--) rows.Write((byte)(offset >> (8 * k)));
                    rows.Write((byte)(generation >> 8));
                    rows.Write((byte)generation);
                }
            }

            t.Set("Type", new PdfName("XRef"));
            t.Set("Index", index);
            t.Set("W", PdfArray.Of(1, 8, 2));
            t.Set("Length", PdfNumber.FromInteger(rows.Length));
            sink.WriteInteger(freeNumber);
            sink.Write(" 0 obj\n"u8);
            serializer.WriteDictionary(sink, t, skipKey: null);
            sink.Write("stream\n"u8);
            sink.Write(rows.WrittenSpan);
            sink.Write("\nendstream\nendobj\n"u8);
        }
        else
        {
            t.Set("Size", PdfNumber.FromInteger(Math.Max(previousSize, highest + 1)));
            sink.Write("xref\n"u8);
            foreach ((int first, int count) in Runs(offsets.Keys))
            {
                sink.WriteAscii(first.ToString(CultureInfo.InvariantCulture) + " " + count.ToString(CultureInfo.InvariantCulture) + "\n");
                for (int n = first; n < first + count; n++)
                {
                    (long offset, int generation) = offsets[n];
                    sink.WriteAscii(offset.ToString("D10", CultureInfo.InvariantCulture) + " " + generation.ToString("D5", CultureInfo.InvariantCulture) + " n \n");
                }
            }

            sink.Write("trailer\n"u8);
            serializer.WriteDictionary(sink, t, skipKey: null);
            sink.Write((byte)'\n');
        }

        sink.WriteAscii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return sink.ToArray();
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
}
