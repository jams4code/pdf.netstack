using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PdfNetStack.Ocr.Tesseract.Processing;

/// <summary>
/// Runs a process to its end with every stream pumped at once, so it cannot block on a full pipe,
/// and with memory and time bounded: output over the limit, a deadline or cancellation kills it.
/// </summary>
internal static class ChildProcess
{
    private const int BufferSize = 64 * 1024;
    private const int ErrorTail = 4096;

    // How long to wait for the pipes once the process is gone: something it started can hold them open.
    private const int SettleMilliseconds = 5000;

    /// <summary>What a finished process left.</summary>
    /// <param name="ExitCode">Its exit code.</param>
    /// <param name="Output">Everything from standard output.</param>
    /// <param name="ErrorOutput">The end of standard error.</param>
    public sealed record Result(int ExitCode, byte[] Output, byte[] ErrorOutput)
    {
        /// <summary>The end of standard error for a message: one line, without control characters.</summary>
        public string Error
        {
            get
            {
                var text = new StringBuilder(ErrorOutput.Length);
                foreach (char c in Encoding.UTF8.GetString(ErrorOutput)) text.Append(char.IsControl(c) ? ' ' : c);
                return text.ToString().Trim();
            }
        }
    }

    /// <summary>Runs a process.</summary>
    /// <param name="start">The process; every stream must be redirected.</param>
    /// <param name="input">Writes standard input, or null to close it at once. The process may stop reading: that is not an error here, its exit code tells.</param>
    /// <param name="maxOutput">The limit on standard output.</param>
    /// <param name="timeout">The limit on time.</param>
    /// <param name="cancellationToken">Kills the process.</param>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    /// <exception cref="TimeoutException">Too slow.</exception>
    /// <exception cref="TesseractOcrException">It did not start, wrote too much, or held its output open after ending.</exception>
    public static Result Run(ProcessStartInfo start, Action<Stream>? input, long maxOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // A task rather than an event object: a late signal after this method returned is harmless.
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => ended.TrySetResult(true);
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new TesseractOcrException($"{start.FileName} did not start: {e.Message}", e);
        }

        var output = new MemoryStream();
        var error = new TailBuffer(ErrorTail);
        bool overflow = false;
        Stream inputStream = process.StandardInput.BaseStream, outputStream = process.StandardOutput.BaseStream, errorStream = process.StandardError.BaseStream;
        Task[] pumps =
        [
            Pump(() =>
            {
                try
                {
                    input?.Invoke(inputStream);
                }
                finally
                {
                    inputStream.Dispose();
                }
            }),
            Pump(() =>
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = outputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maxOutput)
                    {
                        Volatile.Write(ref overflow, true);
                        ended.TrySetResult(false);
                        return;
                    }

                    output.Write(buffer, 0, read);
                }
            }),
            Pump(() =>
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = errorStream.Read(buffer, 0, buffer.Length)) > 0) error.Add(buffer, read);
            }),
        ];

        bool finished;
        try
        {
            finished = ended.Task.Wait((int)timeout.TotalMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Stop(process, pumps);
            throw;
        }

        if (!finished)
        {
            Stop(process, pumps);
            throw new TimeoutException($"Tesseract took longer than {timeout} and was stopped.");
        }

        if (Volatile.Read(ref overflow)) Stop(process, pumps);
        bool settled = Task.WaitAll(pumps, SettleMilliseconds, CancellationToken.None);

        // Checked after the pumps are done: the process may have ended with its last output unread.
        if (Volatile.Read(ref overflow)) throw new TesseractOcrException($"Tesseract wrote more than {maxOutput} bytes and was stopped.");
        if (!settled) throw new TesseractOcrException("Tesseract ended but its output stayed open.");
        return new Result(process.ExitCode, output.ToArray(), error.Bytes());
    }

    /// <summary>Runs on a thread of its own, as these block on pipes for as long as the process lives. A pipe closing under it ends it quietly.</summary>
    private static Task Pump(Action work) => Task.Factory.StartNew(
        () =>
        {
            try
            {
                work();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The process ended, or was killed, with the pipe in use.
            }
        },
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    private static void Stop(Process process, Task[] pumps)
    {
        try
        {
#if NET
            process.Kill(entireProcessTree: true);
#else
            process.Kill();
#endif
            process.WaitForExit(SettleMilliseconds);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // It ended first, or the system will not let it be killed: either way nothing more can be done.
        }

        Task.WaitAll(pumps, SettleMilliseconds, CancellationToken.None);
    }

    /// <summary>Keeps the last bytes written, so an error flood costs no memory and the message keeps its end.</summary>
    private sealed class TailBuffer(int capacity)
    {
        private readonly byte[] _bytes = new byte[capacity];
        private long _total;

        public void Add(byte[] buffer, int count)
        {
            lock (_bytes)
            {
                int from = Math.Max(0, count - capacity);
                for (int i = from; i < count; i++) _bytes[(_total + i - from) % capacity] = buffer[i];
                _total += count - from;
            }
        }

        public byte[] Bytes()
        {
            lock (_bytes)
            {
                int length = (int)Math.Min(_total, capacity);
                byte[] ordered = new byte[length];
                for (int i = 0; i < length; i++) ordered[i] = _bytes[(_total - length + i) % capacity];
                return ordered;
            }
        }
    }
}
