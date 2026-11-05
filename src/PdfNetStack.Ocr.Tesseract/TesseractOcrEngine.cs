using System.Runtime.InteropServices;
using PdfNetStack.Ocr.Tesseract.Processing;

namespace PdfNetStack.Ocr.Tesseract;

/// <summary>Settings for <see cref="TesseractOcrEngine"/>.</summary>
public sealed class TesseractOcrEngineOptions
{
    /// <summary>The tesseract executable. Null looks for it in the absolute folders of the PATH, never in the current folder.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The folder holding the <c>.traineddata</c> files. Null leaves it to Tesseract: <c>TESSDATA_PREFIX</c>, else where it was installed.</summary>
    public string? TessdataPath { get; init; }

    /// <summary>
    /// The languages to check for when loading, as they will be requested: the names in
    /// <see cref="LanguageMap"/> or Tesseract's own, such as <c>fra</c> or <c>eng+ara</c>.
    /// Loading fails when the data for one is missing.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = ["latin"];

    /// <summary>
    /// What a requested language means to Tesseract. By default <c>latin</c> is <c>eng</c> and
    /// <c>arabic</c> is <c>ara</c>, so that <see cref="PdfOcrOptions"/> works unchanged. A name not
    /// listed here goes to Tesseract as it is.
    /// </summary>
    public IReadOnlyDictionary<string, string> LanguageMap { get; init; } = new Dictionary<string, string> { ["latin"] = "eng", ["arabic"] = "ara" };

    /// <summary>Tesseract's page segmentation mode: 3 is automatic layout without orientation detection. 0 and 2 give no text and are refused.</summary>
    public int PageSegmentationMode { get; init; } = 3;

    /// <summary>The resolution to tell Tesseract about. Null lets it estimate one from the text, as the images come from pages of any resolution.</summary>
    public int? Dpi { get; init; }

    /// <summary>
    /// Threads each process may use, through <c>OMP_THREAD_LIMIT</c>. The default of 1 suits
    /// <see cref="PdfOcr"/>, which already reads pages in parallel. 0 leaves it to Tesseract.
    /// </summary>
    public int ThreadsPerProcess { get; init; } = 1;

    /// <summary>Processes running at once; further calls wait their turn. Each process holds the image and the language data in memory.</summary>
    public int MaxConcurrentProcesses { get; init; } = Environment.ProcessorCount;

    /// <summary>The time one image may take before its process is killed and <see cref="TimeoutException"/> is thrown.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The output one image may produce before its process is killed and <see cref="TesseractOcrException"/> is thrown.</summary>
    public long MaxOutputBytes { get; init; } = 64L << 20;
}

/// <summary>Tesseract could not read an image: it failed, or what it wrote cannot be used.</summary>
public sealed class TesseractOcrException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TesseractOcrException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened.</param>
    public TesseractOcrException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause.</param>
    public TesseractOcrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// OCR with an installed Tesseract: each image is one <c>tesseract</c> process that reads the pixels
/// from its standard input and writes its results to its standard output, with no shell and no
/// temporary files. A process that crashes, hangs or floods its output only costs that image.
/// Safe to call from several threads, up to <see cref="TesseractOcrEngineOptions.MaxConcurrentProcesses"/>
/// at once. Tesseract and its language data are installed separately.
/// </summary>
/// <example>
/// <code>
/// using var engine = TesseractOcrEngine.Load(new TesseractOcrEngineOptions { Languages = ["fra+ara"] });
/// var report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["fra+ara"] }).MakeSearchable(document);
/// </code>
/// </example>
public sealed class TesseractOcrEngine : IOcrEngine
{
    // Leptonica refuses larger bitmap files (src/bmpio.c): said here first, with a clear message.
    private const int MaxSide = 1_000_000;
    private const long MaxPixels = 400_000_000;

    // Started before the image is ever seen: --version and --list-langs answer at once or not at all.
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private const long QueryOutputBytes = 1L << 20;

    private readonly string _executable;
    private readonly string? _tessdata;
    private readonly TesseractOcrEngineOptions _options;
    private readonly Dictionary<string, string> _map;
    private readonly HashSet<string> _installed;
    private readonly string _dataFolder;
    private readonly SemaphoreSlim _gate;
    private volatile bool _disposed;

    private TesseractOcrEngine(string executable, string? tessdata, string version, string dataFolder, HashSet<string> installed, Dictionary<string, string> map, TesseractOcrEngineOptions options)
    {
        _executable = executable;
        _tessdata = tessdata;
        Version = version;
        _dataFolder = dataFolder;
        _installed = installed;
        _map = map;
        _options = options;
        _gate = new SemaphoreSlim(options.MaxConcurrentProcesses);
    }

    /// <summary>The version the executable reports, such as <c>5.5.0</c>.</summary>
    public string Version { get; }

    /// <summary>The languages Tesseract has data for, as it names them.</summary>
    public IReadOnlyCollection<string> InstalledLanguages => _installed;

    /// <summary>The data folder Tesseract reports, or empty when it does not say.</summary>
    internal string DataFolder => _dataFolder;

    /// <summary>
    /// Finds Tesseract, checks its version and that the data for <see cref="TesseractOcrEngineOptions.Languages"/> is there.
    /// </summary>
    /// <param name="options">Where things are, and tuning; defaults when null.</param>
    /// <exception cref="FileNotFoundException">The executable or the data for a language is missing. The message says where to get it.</exception>
    /// <exception cref="DirectoryNotFoundException"><see cref="TesseractOcrEngineOptions.TessdataPath"/> does not exist.</exception>
    /// <exception cref="ArgumentException">A language name or a setting is not valid.</exception>
    /// <exception cref="TesseractOcrException">The executable fails, or is not Tesseract 4 or later.</exception>
    public static TesseractOcrEngine Load(TesseractOcrEngineOptions? options = null)
    {
        options ??= new TesseractOcrEngineOptions();
        Validate(options);

        string? tessdata = null;
        if (options.TessdataPath is not null)
        {
            tessdata = TesseractArguments.NormalizeFolder(options.TessdataPath);
            if (!Directory.Exists(tessdata)) throw new DirectoryNotFoundException($"The Tesseract data folder {tessdata} does not exist. {DataHelp}");
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in options.LanguageMap) map[pair.Key] = pair.Value;

        string executable = TesseractArguments.ResolveExecutable(options.ExecutablePath, Environment.GetEnvironmentVariable("PATH"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        string version = TesseractOutput.Version(Query(executable, TesseractArguments.Version()), executable);
        (string folder, HashSet<string> installed) = TesseractOutput.Languages(Query(executable, TesseractArguments.ListLanguages(tessdata)));
        if (folder.Length == 0) folder = tessdata ?? "";

        var engine = new TesseractOcrEngine(executable, tessdata, version, folder, installed, map, options);
        try
        {
            foreach (string language in options.Languages) engine.Check(language, loading: true);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private const string DataHelp = "Language data is not part of this package: get the .traineddata files from https://github.com/tesseract-ocr/tessdata_fast or from your package manager (apt install tesseract-ocr-ara, brew install tesseract-lang), and name their folder with TessdataPath.";

    private static void Validate(TesseractOcrEngineOptions options)
    {
        if (options.PageSegmentationMode is < 1 or > 13 or 2) throw new ArgumentException($"Page segmentation mode {options.PageSegmentationMode} gives no text. Use 1 or 3 to 13.", nameof(options));
        if (options.Dpi is < 1 or > 10000) throw new ArgumentException($"{options.Dpi} is not a resolution.", nameof(options));
        if (options.ThreadsPerProcess < 0) throw new ArgumentException("ThreadsPerProcess cannot be negative.", nameof(options));
        if (options.MaxConcurrentProcesses < 1) throw new ArgumentException("MaxConcurrentProcesses must be at least 1.", nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentException("Timeout must be positive and under 24 days.", nameof(options));
        if (options.MaxOutputBytes is < 1 or > 1L << 30) throw new ArgumentException("MaxOutputBytes must be between 1 byte and 1 GiB.", nameof(options));
    }

    /// <summary>Asks the executable something that needs no image.</summary>
    private static string Query(string executable, IReadOnlyList<string> arguments)
    {
        ChildProcess.Result result = ChildProcess.Run(TesseractArguments.Start(executable, arguments, 0), null, QueryOutputBytes, QueryTimeout, CancellationToken.None);
        if (result.ExitCode != 0) throw new TesseractOcrException($"{executable} failed with exit code {result.ExitCode}: {result.Error}");
        return TesseractOutput.Decode(result.Output);
    }

    /// <summary>What Tesseract calls a requested language, checked against what it has.</summary>
    private string Check(string language, bool loading)
    {
        string tesseract = _map.TryGetValue(language, out string? mapped) ? mapped : language;
        if (!TesseractArguments.IsValidLanguage(tesseract)) throw new ArgumentException($"'{tesseract}' is not a Tesseract language: use names like eng, chi_sim, script/Latin, joined with +.", nameof(language));
        foreach (string part in tesseract.Split('+'))
        {
            if (_installed.Contains(part)) continue;
            string message = $"Tesseract has no data for '{part}' in {(_dataFolder.Length > 0 ? _dataFolder : "its data folder")}. {DataHelp}";
            if (loading) throw new FileNotFoundException(message, part + ".traineddata");
            throw new ArgumentException(message, nameof(language));
        }

        return tesseract;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The language is not installed or not valid, or the image is larger than Tesseract reads.</exception>
    /// <exception cref="TimeoutException">The process took longer than <see cref="TesseractOcrEngineOptions.Timeout"/> and was killed.</exception>
    /// <exception cref="TesseractOcrException">The process failed, or its output cannot be used.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process was killed.</exception>
    public OcrImageResult Recognize(OcrBitmap image, OcrRequest request, CancellationToken cancellationToken)
    {
        Compat.ThrowIfNull(image, nameof(image));
        Compat.ThrowIfNull(request, nameof(request));
        Compat.ThrowIfDisposed(_disposed, this);
        if (image.Width > MaxSide || image.Height > MaxSide || (long)image.Width * image.Height > MaxPixels)
        {
            throw new ArgumentException($"Tesseract does not read an image of {image.Width} by {image.Height} pixels.", nameof(image));
        }

        string language = Check(request.Language, loading: false);
        cancellationToken.ThrowIfCancellationRequested();
        _gate.Wait(cancellationToken);
        try
        {
            IReadOnlyList<string> arguments = TesseractArguments.Recognize(language, _tessdata, _options.PageSegmentationMode, _options.Dpi);
            ChildProcess.Result result = ChildProcess.Run(
                TesseractArguments.Start(_executable, arguments, _options.ThreadsPerProcess),
                stream => GrayBitmapFile.Write(stream, image, cancellationToken),
                _options.MaxOutputBytes,
                _options.Timeout,
                cancellationToken);
            if (result.ExitCode != 0) throw new TesseractOcrException($"Tesseract failed with exit code {result.ExitCode}: {result.Error}");
            return new OcrImageResult(TesseractOutput.Lines(result.Output, image.Width, image.Height));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
