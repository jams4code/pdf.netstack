using System.Runtime.InteropServices;

namespace PdfNetStack.Parsing;

/// <summary>
/// A private, seekable copy of a stream that cannot seek, or that must not be read after opening:
/// in memory up to a threshold, past it in a temporary file of its own. Disposing the copy
/// deletes the file. The file is opened with <see cref="FileOptions.DeleteOnClose"/>, so Windows
/// removes it even when the process ends without disposing; elsewhere its name is removed as soon
/// as it is open, which leaves the data readable through the handle and nothing on disk once the
/// handle closes, however the process ends.
/// </summary>
internal sealed class Spool : IDisposable
{
    private const int ChunkSize = 81920;

    private Spool(ReadOnlyMemory<byte> memory) => Memory = memory;

    private Spool(FileStream file, string path)
    {
        File = file;
        Path = path;
    }

    /// <summary>The copy, when it fit in memory.</summary>
    public ReadOnlyMemory<byte> Memory { get; }

    /// <summary>The copy, when it went to a temporary file, positioned at its start.</summary>
    public FileStream? File { get; }

    /// <summary>Where the temporary file was created.</summary>
    public string? Path { get; }

    /// <summary>Copies <paramref name="input"/> from its position to its end.</summary>
    /// <param name="threshold">Bytes kept in memory at most; a longer stream goes to a file.</param>
    /// <param name="directory">Folder for the file; null for the system's temporary folder.</param>
    public static Spool Copy(Stream input, long threshold, string? directory)
    {
        byte[] chunk = new byte[ChunkSize];
        var memory = new MemoryStream();
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (memory.Length + read > threshold)
            {
                (FileStream file, string path) = Create(directory);
                try
                {
                    file.Write(memory.GetBuffer(), 0, (int)memory.Length);
                    memory = new MemoryStream();
                    do
                    {
                        file.Write(chunk, 0, read);
                    }
                    while ((read = input.Read(chunk, 0, chunk.Length)) > 0);

                    file.Flush();
                    file.Position = 0;
                    return new Spool(file, path);
                }
                catch
                {
                    Delete(file, path);
                    throw;
                }
            }

            memory.Write(chunk, 0, read);
        }

        return new Spool(memory.GetBuffer().AsMemory(0, (int)memory.Length));
    }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
    /// <summary>Copies <paramref name="input"/> from its position to its end, reading it asynchronously only.</summary>
    /// <param name="threshold">Bytes kept in memory at most; a longer stream goes to a file.</param>
    /// <param name="directory">Folder for the file; null for the system's temporary folder.</param>
    public static async Task<Spool> CopyAsync(Stream input, long threshold, string? directory, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[ChunkSize];
        var memory = new MemoryStream();
        int read;
        while ((read = await input.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > threshold)
            {
                (FileStream file, string path) = Create(directory);
                try
                {
                    await file.WriteAsync(memory.GetBuffer(), 0, (int)memory.Length, cancellationToken).ConfigureAwait(false);
                    memory = new MemoryStream();
                    do
                    {
                        await file.WriteAsync(chunk, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                    while ((read = await input.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0);

                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    file.Position = 0;
                    return new Spool(file, path);
                }
                catch
                {
                    Delete(file, path);
                    throw;
                }
            }

            memory.Write(chunk, 0, read);
        }

        return new Spool(memory.GetBuffer().AsMemory(0, (int)memory.Length));
    }
#pragma warning restore CA1835

    public void Dispose()
    {
        if (File is not null) Delete(File, Path!);
    }

    private static (FileStream File, string Path) Create(string? directory)
    {
        string folder = string.IsNullOrEmpty(directory) ? System.IO.Path.GetTempPath() : directory!;
        string path = System.IO.Path.Combine(folder, "pdfnetstack-" + Guid.NewGuid().ToString("N") + ".tmp");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) TryDelete(path);
        return (file, path);
    }

    private static void Delete(FileStream file, string path)
    {
        file.Dispose();
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Already gone, or the system removes it when the last handle closes.
        }
    }
}
