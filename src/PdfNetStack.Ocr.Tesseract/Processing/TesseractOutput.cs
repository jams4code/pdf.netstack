using System.Globalization;
using System.Text;
using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr.Tesseract.Processing;

/// <summary>
/// Reads what the tesseract executable writes. The recognized text comes from a hostile document
/// and TSV has no escaping, so nothing here is trusted: a row that does not parse is dropped, boxes
/// are clamped to the image, and nothing is allocated beyond the size of the output.
/// </summary>
internal static class TesseractOutput
{
    private const int Columns = 12;
    private const int WordLevel = 5;
    private const string Header = "level\t";
    private const int MinimumMajorVersion = 4;

    private static readonly char[] Tab = ['\t'];

    /// <summary>The output as text; bytes that are not UTF-8 become U+FFFD.</summary>
    public static string Decode(byte[] output) => Encoding.UTF8.GetString(output);

    /// <summary>The version from <c>--version</c>: the first line reads <c>tesseract 5.5.0</c> or <c>tesseract v5.3.0.20221214</c>.</summary>
    /// <exception cref="TesseractOcrException">This is not Tesseract, or it is older than version 4, whose options differ.</exception>
    public static string Version(string answer, string executable)
    {
        const string Name = "tesseract ";
        foreach (string raw in answer.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(Name, StringComparison.OrdinalIgnoreCase)) continue;
            string version = line.Substring(Name.Length).Trim().TrimStart('v');
            int dot = version.IndexOf('.');
            string first = dot > 0 ? version.Substring(0, dot) : "";
            if (int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            {
                if (major >= MinimumMajorVersion) return version;
                throw new TesseractOcrException($"{executable} is Tesseract {version}. Version {MinimumMajorVersion} or later is needed.");
            }
        }

        throw new TesseractOcrException($"{executable} does not report a Tesseract version.");
    }

    /// <summary>
    /// The languages from <c>--list-langs</c>: a line reading <c>List of available languages in "folder/" (2):</c>
    /// (older versions leave the folder out), then one name per line.
    /// </summary>
    public static (string Folder, HashSet<string> Languages) Languages(string answer)
    {
        const string Title = "List of available languages";
        string folder = "";
        var languages = new HashSet<string>(StringComparer.Ordinal);
        bool listing = false;
        foreach (string raw in answer.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(Title, StringComparison.Ordinal))
            {
                listing = true;
                int open = line.IndexOf('"'), close = line.LastIndexOf('"');
                if (open >= 0 && close > open) folder = line.Substring(open + 1, close - open - 1);
            }
            else if (listing && TesseractArguments.IsValidLanguage(line) && line.IndexOf('+') < 0)
            {
                languages.Add(line);
            }
        }

        return (folder, languages);
    }

    /// <summary>The lines of a TSV result, in the order Tesseract reads them, with boxes inside the image.</summary>
    /// <exception cref="TesseractOcrException">The output is not TSV.</exception>
    public static List<OcrLine> Lines(byte[] output, int width, int height)
    {
        var lines = new List<OcrLine>();
        string text = Decode(output);
        if (text.Length == 0) return lines;
        if (!text.StartsWith(Header, StringComparison.Ordinal)) throw new TesseractOcrException("Tesseract's output is not TSV.");

        var words = new List<OcrWord>();
        (int, int, int, int) current = default;
        int start = text.IndexOf('\n');
        while (start >= 0 && start < text.Length)
        {
            int end = text.IndexOf('\n', start + 1);
            string row = end < 0 ? text.Substring(start + 1) : text.Substring(start + 1, end - start - 1);
            start = end;
            if (!TryWord(row, width, height, out (int, int, int, int) key, out OcrWord? word)) continue;
            if (words.Count > 0 && key != current)
            {
                lines.Add(Line(words));
                words = [];
            }

            current = key;
            words.Add(word!);
        }

        if (words.Count > 0) lines.Add(Line(words));
        return lines;
    }

    /// <summary>One word row: level, page, block, paragraph, line, word, left, top, width, height, confidence, text.</summary>
    private static bool TryWord(string row, int width, int height, out (int, int, int, int) key, out OcrWord? word)
    {
        key = default;
        word = null;
        string[] fields = row.Split(Tab, Columns);
        if (fields.Length != Columns || !TryInt(fields[0], out int level) || level != WordLevel) return false;
        if (!TryInt(fields[1], out int page) || !TryInt(fields[2], out int block) || !TryInt(fields[3], out int paragraph) || !TryInt(fields[4], out int number)) return false;
        if (!TryInt(fields[6], out int left) || !TryInt(fields[7], out int top) || !TryInt(fields[8], out int across) || !TryInt(fields[9], out int down)) return false;
        if (!double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence)) return false;

        string text = Clean(fields[11]);
        if (text.Length == 0) return false;

        // The sums are long, as the numbers can be anything: a box wholly outside the image is no box.
        double x0 = Clamp(left, width), y0 = Clamp(top, height), x1 = Clamp((long)left + across, width), y1 = Clamp((long)top + down, height);
        if (!(x1 > x0 && y1 > y0)) return false;

        key = (page, block, paragraph, number);
        word = new OcrWord(text, Corners(x0, y0, x1, y1), Scale(confidence));
        return true;
    }

    private static bool TryInt(string field, out int value) => int.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static double Clamp(long value, int limit) => Math.Max(0, Math.Min(value, limit));

    /// <summary>Tesseract's 0 to 100, or -1 when it has none, as 0 to 1. Anything else is no confidence.</summary>
    private static double Scale(double confidence) => double.IsNaN(confidence) || confidence <= 0 ? 0 : Math.Min(1, confidence / 100);

    /// <summary>The text without control characters, which have no place in a word, or surrounding white space.</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c)) clean.Append(c);
        }

        return clean.ToString().Trim();
    }

    private static PdfPoint[] Corners(double x0, double y0, double x1, double y1) => [new PdfPoint(x0, y0), new PdfPoint(x1, y0), new PdfPoint(x1, y1), new PdfPoint(x0, y1)];

    /// <summary>A line from its words: Tesseract gives them, and each word's letters, in reading order, so right to left text is already logical.</summary>
    private static OcrLine Line(List<OcrWord> words)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0, sum = 0, characters = 0;
        foreach (OcrWord word in words)
        {
            x0 = Math.Min(x0, word.Quad[0].X);
            y0 = Math.Min(y0, word.Quad[0].Y);
            x1 = Math.Max(x1, word.Quad[2].X);
            y1 = Math.Max(y1, word.Quad[2].Y);
            sum += word.Confidence * word.Text.Length;
            characters += word.Text.Length;
        }

        string text = string.Join(" ", words.Select(w => w.Text));
        return new OcrLine(text, Corners(x0, y0, x1, y1), sum / characters, words, MostlyRightToLeft(text));
    }

    /// <summary>Whether most of the letters are in the Hebrew, Arabic, Syriac or N'Ko blocks and their presentation forms.</summary>
    private static bool MostlyRightToLeft(string text)
    {
        int right = 0, left = 0;
        foreach (char c in text)
        {
            if (c is (>= '֐' and <= 'ࣿ') or (>= 'יִ' and <= '﷿') or (>= 'ﹰ' and <= '﻿'))
            {
                if (char.IsLetter(c)) right++;
            }
            else if (char.IsLetter(c))
            {
                left++;
            }
        }

        return right > left;
    }
}
