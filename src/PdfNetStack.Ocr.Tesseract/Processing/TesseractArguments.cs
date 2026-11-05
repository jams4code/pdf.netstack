using System.Diagnostics;
using System.Text;

namespace PdfNetStack.Ocr.Tesseract.Processing;

/// <summary>The command lines given to the tesseract executable, and how they reach it.</summary>
internal static class TesseractArguments
{
    private const int MaxLanguageParts = 16;
    private const int MaxLanguagePartLength = 64;
    private const string ScriptPrefix = "script/";

    /// <summary>
    /// Reads an image from standard input and writes TSV to standard output. TSV is asked for with
    /// a variable rather than the <c>tsv</c> config file, which is not in every data folder and is
    /// otherwise looked for relative to the working directory.
    /// </summary>
    public static IReadOnlyList<string> Recognize(string language, string? tessdata, int pageSegmentationMode, int? dpi)
    {
        var arguments = new List<string> { "stdin", "stdout" };
        AddDataFolder(arguments, tessdata);
        arguments.Add("-l");
        arguments.Add(language);
        arguments.Add("--psm");
        arguments.Add(pageSegmentationMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (dpi is int value)
        {
            arguments.Add("--dpi");
            arguments.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add("-c");
        arguments.Add("tessedit_create_tsv=1");
        return arguments;
    }

    /// <summary>Lists the installed languages.</summary>
    public static IReadOnlyList<string> ListLanguages(string? tessdata)
    {
        var arguments = new List<string>();
        AddDataFolder(arguments, tessdata);
        arguments.Add("--list-langs");
        return arguments;
    }

    /// <summary>Reports the version.</summary>
    public static IReadOnlyList<string> Version() => ["--version"];

    private static void AddDataFolder(List<string> arguments, string? tessdata)
    {
        if (tessdata is null) return;
        arguments.Add("--tessdata-dir");
        arguments.Add(tessdata);
    }

    /// <summary>
    /// A language as Tesseract takes it: names joined with <c>+</c>, each letters, digits, <c>_</c>
    /// and <c>-</c>, optionally under <c>script/</c>. Nothing that could read as an option, climb out
    /// of the data folder or name a file elsewhere gets through, wherever the request came from.
    /// </summary>
    public static bool IsValidLanguage(string language)
    {
        if (string.IsNullOrEmpty(language)) return false;
        string[] parts = language.Split('+');
        if (parts.Length > MaxLanguageParts) return false;
        foreach (string part in parts)
        {
            string name = part.StartsWith(ScriptPrefix, StringComparison.Ordinal) ? part.Substring(ScriptPrefix.Length) : part;
            if (name.Length is 0 or > MaxLanguagePartLength || !IsAsciiLetterOrDigit(name[0])) return false;
            foreach (char c in name)
            {
                if (!IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    /// <summary>A full path with no trailing separator, which some Tesseract versions mishandle.</summary>
    public static string NormalizeFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed[trimmed.Length - 1] == ':' ? full : trimmed;
    }

    /// <summary>The executable to run: the path given, or the first on the PATH.</summary>
    /// <exception cref="FileNotFoundException">It is not there.</exception>
    /// <exception cref="ArgumentException">It is a batch file, which Windows runs through a shell.</exception>
    public static string ResolveExecutable(string? configured, string? path, bool windows)
    {
        const string Help = "Install it (https://tesseract-ocr.github.io/tessdoc/Installation.html, apt install tesseract-ocr, brew install tesseract), or set ExecutablePath.";
        if (configured is null)
        {
            return FindOnPath(path, windows) ?? throw new FileNotFoundException($"Tesseract was not found on the PATH. {Help}", windows ? "tesseract.exe" : "tesseract");
        }

        string extension = Path.GetExtension(configured);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{configured} is a batch file, which would run through a shell. Name the tesseract executable itself.", nameof(configured));
        }

        string full = Path.GetFullPath(configured);
        return File.Exists(full) ? full : throw new FileNotFoundException($"Tesseract was not found at {full}. {Help}", full);
    }

    /// <summary>
    /// The first tesseract in the PATH's absolute folders. Looking here, rather than leaving it to
    /// the operating system, keeps out the current folder, which Windows searches first and an
    /// empty PATH entry means on Unix: a document's folder must not get to choose what runs.
    /// </summary>
    public static string? FindOnPath(string? path, bool windows)
    {
        string file = windows ? "tesseract.exe" : "tesseract";
        foreach (string entry in (path ?? "").Split(Path.PathSeparator))
        {
            string folder = entry.Trim().Trim('"');
            try
            {
                if (folder.Length == 0 || !Path.IsPathRooted(folder)) continue;
                string candidate = Path.Combine(folder, file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Characters no path may hold, on .NET Framework: not a folder to look in.
            }
        }

        return null;
    }

    /// <summary>
    /// A process start with no shell and every stream redirected. It runs in the executable's own
    /// folder: Tesseract can fall back to data relative to where it runs, which must not be the
    /// caller's folder or a temporary folder anyone can write to.
    /// </summary>
    public static ProcessStartInfo Start(string executable, IReadOnlyList<string> arguments, int threads)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
#if NET
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
#else
        start.Arguments = string.Join(" ", arguments.Select(Quote));
#endif

        // Numbers in the output are written under the process's locale: a decimal comma would not parse.
        start.EnvironmentVariables["LC_ALL"] = "C";

        // Tesseract's documented way to limit its threads.
        if (threads > 0) start.EnvironmentVariables["OMP_THREAD_LIMIT"] = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }

    /// <summary>
    /// One argument for a command line, under the rules C programs parse it by on Windows and .NET
    /// applies on Unix: backslashes count only before a quote, where they double. Only the .NET
    /// Standard build needs this, as it has no argument list; it is tested on every target.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (2 * backslashes) + 1 : backslashes);
            quoted.Append(c);
            backslashes = 0;
        }

        quoted.Append('\\', 2 * backslashes);
        return quoted.Append('"').ToString();
    }
}
