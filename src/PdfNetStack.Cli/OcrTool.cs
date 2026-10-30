using System.ComponentModel;
using System.Diagnostics;

namespace PdfNetStack.Cli;

/// <summary>
/// The ocr command. OCR needs ONNX Runtime's native libraries and about 21 MB of models, which
/// users who never run it should not download, so it ships as a tool of its own,
/// <c>pdfnetstack-ocr</c> (package PdfNetStack.Ocr.Tool). This command runs it with the same
/// arguments, found next to this executable or on the PATH.
/// </summary>
internal static class OcrTool
{
    public const string Name = "pdfnetstack-ocr";

    public const string Missing = "The ocr command runs pdfnetstack-ocr, which is not installed. Install it with:\n  dotnet tool install -g PdfNetStack.Ocr.Tool";

    /// <summary>Runs the OCR tool and returns its exit code, or null when it is not installed.</summary>
    public static int? Run(string[] args)
    {
        string? tool = Find(Environment.GetEnvironmentVariable("PATH"), AppContext.BaseDirectory, OperatingSystem.IsWindows());
        if (tool is null) return null;

        // No shell: each argument reaches the tool as it was given.
        var start = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"{tool} did not start: {e.Message}", e);
        }
    }

    /// <summary>The OCR tool's executable: next to this one first, then the first on the PATH.</summary>
    internal static string? Find(string? path, string baseDirectory, bool windows)
    {
        string file = windows ? Name + ".exe" : Name;
        IEnumerable<string> folders = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'))
            .Prepend(baseDirectory);
        foreach (string folder in folders)
        {
            if (folder.Length == 0) continue;
            string candidate = Path.Combine(folder, file);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
