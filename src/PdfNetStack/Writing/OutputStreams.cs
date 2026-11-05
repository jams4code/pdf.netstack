using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfNetStack.Writing;

/// <summary>Asynchronous writes of memory to a caller's stream, on every target.</summary>
internal static class OutputStreams
{
    /// <summary>How much a copy between streams moves at a time.</summary>
    public const int CopyBufferSize = 81920;

    public static Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        return stream.WriteAsync(data, ct).AsTask();
#else
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
        {
            return stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, ct);
        }

        return WriteCopyAsync(stream, data, ct);
#endif
    }

#if !NET8_0_OR_GREATER
    private static async Task WriteCopyAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(buffer);
            await stream.WriteAsync(buffer, 0, data.Length, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
#endif

    /// <summary>Copies <paramref name="source"/> from its current position to its end, reading and writing asynchronously.</summary>
    public static async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
            while ((read = await source.ReadAsync(buffer, 0, CopyBufferSize, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            }
#pragma warning restore CA1835
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies <paramref name="source"/> from its current position to its end.</summary>
    public static void Copy(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, CopyBufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
