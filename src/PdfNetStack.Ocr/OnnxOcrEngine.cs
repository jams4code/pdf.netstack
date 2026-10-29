using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PdfNetStack.Drawing;
using PdfNetStack.Ocr.Processing;

namespace PdfNetStack.Ocr;

/// <summary>Settings for <see cref="OnnxOcrEngine"/>.</summary>
public sealed class OnnxOcrEngineOptions
{
    /// <summary>Scripts to load recognizers for: <c>latin</c>, <c>arabic</c>. Each needs its model file.</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["latin"];

    /// <summary>Longest side the detector sees; larger images are scaled down for detection only.</summary>
    public int MaxDetectionSide { get; init; } = 1920;

    /// <summary>Mean text probability a detected box needs.</summary>
    public double BoxThreshold { get; init; } = 0.6;

    /// <summary>Turn lines the orientation model reads as upside down. Needs its model file.</summary>
    public bool CorrectUpsideDownLines { get; init; } = true;

    /// <summary>Threads each model may use; 0 lets ONNX Runtime decide.</summary>
    public int ThreadsPerModel { get; init; }

    /// <summary>Check each model file against its pinned SHA-256 before loading it.</summary>
    public bool VerifyModels { get; init; } = true;
}

/// <summary>
/// OCR with the PP-OCRv5 mobile models (PaddleOCR, Apache-2.0) on ONNX Runtime: a DB text
/// detector, a line orientation classifier and CTC recognizers for Latin and Arabic script.
/// Pre and post processing are managed code; the native runtime only sees float tensors.
/// </summary>
/// <example>
/// <code>
/// using var engine = OnnxOcrEngine.Load("models", new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
/// var report = new PdfOcr(engine).MakeSearchable(document);
/// </code>
/// </example>
public sealed class OnnxOcrEngine : IOcrEngine
{
    /// <summary>The detection model's file name.</summary>
    public const string DetectionModel = "ch_PP-OCRv5_det_mobile.onnx";

    /// <summary>The line orientation model's file name.</summary>
    public const string OrientationModel = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";

    private const int LineHeight = 48;
    private const int MaxLineWidth = 3200;
    private const int BatchSize = 8;

    // RapidOCR v3.10.0 model table: the files and their SHA-256, as published on modelscope.cn.
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        [DetectionModel] = "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae",
        [OrientationModel] = "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
        ["latin_PP-OCRv5_rec_mobile.onnx"] = "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a",
        ["arabic_PP-OCRv5_rec_mobile.onnx"] = "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295",
    };

    // ImageNet normalization, applied per channel as PaddleOCR's detector was trained with.
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// ONNX Runtime's official builds send usage telemetry to Microsoft by default. PDF tooling has
    /// no business talking to the network, so before the first session the variable the runtime
    /// reads at start up is set (the native runtime sees it on Windows; on Linux and macOS it must
    /// be in the environment before the process starts) and non-essential events are turned off.
    /// </summary>
    private static readonly Lazy<bool> Quiet = new(() =>
    {
        Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
        OrtEnv.Instance().DisableTelemetryEvents();
        return true;
    });

    private readonly InferenceSession _detector;
    private readonly InferenceSession? _orientation;
    private readonly Dictionary<string, (InferenceSession Session, string[] Characters)> _recognizers;
    private readonly OnnxOcrEngineOptions _options;

    private OnnxOcrEngine(InferenceSession detector, InferenceSession? orientation, Dictionary<string, (InferenceSession, string[])> recognizers, OnnxOcrEngineOptions options)
    {
        _detector = detector;
        _orientation = orientation;
        _recognizers = recognizers;
        _options = options;
    }

    /// <summary>The recognizer file for a script.</summary>
    /// <param name="language"><c>latin</c> or <c>arabic</c>.</param>
    public static string RecognitionModel(string language) => $"{language}_PP-OCRv5_rec_mobile.onnx";

    /// <summary>The model files and their SHA-256 hashes, for download scripts and audits.</summary>
    public static IReadOnlyDictionary<string, string> ModelHashes => Pins;

    /// <summary>Loads the models from a folder.</summary>
    /// <param name="modelDirectory">The folder holding the .onnx files (see <see cref="ModelHashes"/>).</param>
    /// <param name="options">Languages and tuning; defaults when null.</param>
    /// <exception cref="FileNotFoundException">A model file is missing.</exception>
    /// <exception cref="InvalidDataException">A model file does not match its pinned hash.</exception>
    public static OnnxOcrEngine Load(string modelDirectory, OnnxOcrEngineOptions? options = null)
    {
        Compat.ThrowIfNull(modelDirectory, nameof(modelDirectory));
        options ??= new OnnxOcrEngineOptions();
        _ = Quiet.Value;
        var sessionOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (options.ThreadsPerModel > 0) sessionOptions.IntraOpNumThreads = options.ThreadsPerModel;

        var opened = new List<InferenceSession>();
        try
        {
            InferenceSession Open(string file)
            {
                string path = Path.Combine(modelDirectory, file);
                if (!File.Exists(path)) throw new FileNotFoundException($"OCR model {file} is not in {modelDirectory}.", path);
                byte[] model = File.ReadAllBytes(path);
                if (options.VerifyModels && Pins.TryGetValue(file, out string? pin) && Hex(SHA256Hash(model)) != pin)
                {
                    throw new InvalidDataException($"OCR model {file} does not match its pinned SHA-256.");
                }

                var session = new InferenceSession(model, sessionOptions);
                opened.Add(session);
                return session;
            }

            InferenceSession detector = Open(DetectionModel);
            InferenceSession? orientation = options.CorrectUpsideDownLines ? Open(OrientationModel) : null;
            var recognizers = new Dictionary<string, (InferenceSession, string[])>(StringComparer.OrdinalIgnoreCase);
            foreach (string language in options.Languages.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                InferenceSession session = Open(RecognitionModel(language.ToLowerInvariant()));

                // The recognizer's classes are listed in the model, one per line (PaddleOCR's export).
                if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? list))
                {
                    throw new InvalidDataException($"OCR model {RecognitionModel(language)} has no character list.");
                }

                // The list ends with a newline: the empty entry after it is not a class.
                List<string> characters = [.. list.Split('\n')];
                if (characters.Count > 0 && characters[^1].Length == 0) characters.RemoveAt(characters.Count - 1);
                recognizers[language] = (session, [.. characters]);
            }

            return new OnnxOcrEngine(detector, orientation, recognizers, options);
        }
        catch
        {
            foreach (InferenceSession s in opened) s.Dispose();
            throw;
        }
    }

    private static byte[] SHA256Hash(byte[] data)
    {
#if NET
        return SHA256.HashData(data);
#else
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
#endif
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <inheritdoc/>
    public OcrImageResult Recognize(OcrBitmap image, OcrRequest request, CancellationToken cancellationToken)
    {
        Compat.ThrowIfNull(image, nameof(image));
        Compat.ThrowIfNull(request, nameof(request));
        if (!_recognizers.TryGetValue(request.Language, out (InferenceSession Session, string[] Characters) recognizer))
        {
            throw new ArgumentException($"No recognizer loaded for '{request.Language}'.", nameof(request));
        }

        List<PdfPoint[]> boxes = Detect(image);
        cancellationToken.ThrowIfCancellationRequested();

        // Tall boxes are vertical lines of text: read them turned a quarter.
        var lines = new List<PdfPoint[]>(boxes.Count);
        foreach (PdfPoint[] box in boxes)
        {
            double w = Math.Max(Geometry.Distance(box[0], box[1]), Geometry.Distance(box[3], box[2]));
            double h = Math.Max(Geometry.Distance(box[0], box[3]), Geometry.Distance(box[1], box[2]));
            if (w < 2 || h < 2) continue;
            lines.Add(h / w >= 1.5 ? [box[1], box[2], box[3], box[0]] : box);
        }

        if (_orientation is not null) TurnUpsideDownLines(image, lines);
        cancellationToken.ThrowIfCancellationRequested();
        bool rightToLeft = request.Language.Equals("arabic", StringComparison.OrdinalIgnoreCase);
        return new OcrImageResult(Read(image, lines, recognizer.Session, recognizer.Characters, rightToLeft, cancellationToken));
    }

    /// <summary>Text boxes in image pixels, in reading order.</summary>
    private List<PdfPoint[]> Detect(OcrBitmap image)
    {
        int w = image.Width, h = image.Height;
        double ratio = Math.Min(1, (double)_options.MaxDetectionSide / Math.Max(w, h));

        // The network downsamples by 32: both sides must be multiples of it.
        int dw = Math.Max(32, (int)Math.Round(w * ratio / 32) * 32), dh = Math.Max(32, (int)Math.Round(h * ratio / 32) * 32);
        byte[] pixels = dw == w && dh == h ? image.Pixels : ImageOps.Resize(image.Pixels, w, h, dw, dh);

        var input = new DenseTensor<float>([1, 3, dh, dw]);
        Span<float> buffer = input.Buffer.Span;
        int plane = dw * dh;
        for (int c = 0; c < 3; c++)
        {
            float mean = Mean[c], std = Std[c];
            for (int i = 0; i < plane; i++) buffer[(c * plane) + i] = ((pixels[i] / 255f) - mean) / std;
        }

        float[] probability = Run(_detector, input, out _);
        if (probability.Length != plane) return [];
        return TextBoxes.Find(probability, dw, dh, 0.3, _options.BoxThreshold, 1.5, (double)w / dw, (double)h / dh, w, h);
    }

    /// <summary>The orientation model: lines it reads as turned half a circle, with confidence, are turned back.</summary>
    private void TurnUpsideDownLines(OcrBitmap image, List<PdfPoint[]> lines)
    {
        const int Height = 80, Width = 160;
        for (int start = 0; start < lines.Count; start += BatchSize)
        {
            int n = Math.Min(BatchSize, lines.Count - start);
            var input = new DenseTensor<float>([n, 3, Height, Width]);
            for (int k = 0; k < n; k++)
            {
                PdfPoint[] line = lines[start + k];
                double ratio = Geometry.Distance(line[0], line[1]) / Math.Max(1, Geometry.Distance(line[0], line[3]));
                int width = Math.Max(1, Math.Min(Width, (int)Math.Ceiling(Height * ratio)));
                Fill(input, k, ImageOps.Crop(image.Pixels, image.Width, image.Height, line, width, Height), width, Height, Width);
            }

            float[] scores = Run(_orientation!, input, out int[] shape);
            if (shape.Length != 2 || shape[1] != 2) return;
            for (int k = 0; k < n; k++)
            {
                if (scores[(2 * k) + 1] > scores[2 * k] && scores[(2 * k) + 1] > 0.9)
                {
                    PdfPoint[] q = lines[start + k];
                    lines[start + k] = [q[2], q[3], q[0], q[1]];
                }
            }
        }
    }

    /// <summary>Writes one gray crop into a batch, normalized to -1..1 and padded with zeros to the batch width.</summary>
    private static void Fill(DenseTensor<float> input, int index, byte[] crop, int width, int height, int batchWidth)
    {
        Span<float> buffer = input.Buffer.Span;
        int plane = height * batchWidth;
        for (int c = 0; c < 3; c++)
        {
            int origin = ((index * 3) + c) * plane;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) buffer[origin + (y * batchWidth) + x] = (crop[(y * width) + x] / 127.5f) - 1;
            }
        }
    }

    /// <summary>Recognizes the lines, batched by width so padding stays small.</summary>
    private static List<OcrLine> Read(OcrBitmap image, List<PdfPoint[]> lines, InferenceSession session, string[] characters, bool rightToLeft, CancellationToken cancellationToken)
    {
        int[] widths = [.. lines.Select(q =>
            Math.Max(8, Math.Min(MaxLineWidth, (int)Math.Ceiling(LineHeight * Geometry.Distance(q[0], q[1]) / Math.Max(1, Geometry.Distance(q[0], q[3]))))))];
        int[] order = [.. Enumerable.Range(0, lines.Count).OrderBy(i => widths[i])];
        var results = new OcrLine?[lines.Count];
        for (int start = 0; start < order.Length; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, order.Length - start);
            int batchWidth = Math.Max(320, Enumerable.Range(start, n).Max(k => widths[order[k]]));
            var input = new DenseTensor<float>([n, 3, LineHeight, batchWidth]);
            for (int k = 0; k < n; k++)
            {
                int i = order[start + k];
                Fill(input, k, ImageOps.Crop(image.Pixels, image.Width, image.Height, lines[i], widths[i], LineHeight), widths[i], LineHeight, batchWidth);
            }

            float[] scores = Run(session, input, out int[] shape);
            if (shape.Length != 3) continue;
            int columns = shape[1], classes = shape[2];
            for (int k = 0; k < n; k++)
            {
                int i = order[start + k];
                List<ReadChar> chars = Ctc.Decode(scores.AsSpan(k * columns * classes, columns * classes), columns, classes, characters);

                // Column t covers batchWidth / columns input pixels; the line's own pixels are the first widths[i].
                double perColumn = (double)batchWidth / columns / widths[i];
                results[i] = Line(lines[i], chars, perColumn, rightToLeft);
            }
        }

        return [.. results.Where(r => r is not null && r.Text.Length > 0).Select(r => r!)];
    }

    /// <summary>A line and its words, the words boxed from the columns their characters came from.</summary>
    private static OcrLine? Line(PdfPoint[] quad, List<ReadChar> chars, double perColumn, bool rightToLeft)
    {
        if (chars.Count == 0) return null;

        // Only a line with right to left letters is reordered; a Latin line read by the Arabic model stays as read.
        string visual = string.Concat(chars.Select(c => c.Value)).Trim();
        bool rtl = rightToLeft && Ctc.HasRightToLeft(visual);
        var words = new List<OcrWord>();
        int i = 0;
        while (i < chars.Count)
        {
            while (i < chars.Count && chars[i].Value == " ") i++;
            int first = i;
            while (i < chars.Count && chars[i].Value != " ") i++;
            if (i == first) continue;
            List<ReadChar> word = chars.GetRange(first, i - first);

            // From the start of the first character's column to the end of the last's, inside the line.
            double u0 = Math.Max(0, Math.Min(1, word[0].Column * perColumn)), u1 = Math.Max(u0, Math.Min(1, (word[^1].Column + 1) * perColumn));
            string text = string.Concat(word.Select(c => c.Value));
            words.Add(new OcrWord(
                rtl ? Ctc.Logical(text) : text,
                [Geometry.Along(quad, u0, 0), Geometry.Along(quad, u1, 0), Geometry.Along(quad, u1, 1), Geometry.Along(quad, u0, 1)],
                word.Average(c => c.Confidence)));
        }

        if (rtl) words.Reverse();
        return new OcrLine(rtl ? Ctc.Logical(visual) : visual, quad, chars.Average(c => c.Confidence), words, rtl);
    }

    private static float[] Run(InferenceSession session, DenseTensor<float> input, out int[] shape)
    {
        string name = session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        Tensor<float> output = results[0].AsTensor<float>();
        shape = output.Dimensions.ToArray();
        return output.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _detector.Dispose();
        _orientation?.Dispose();
        foreach ((InferenceSession session, _) in _recognizers.Values) session.Dispose();
    }
}
