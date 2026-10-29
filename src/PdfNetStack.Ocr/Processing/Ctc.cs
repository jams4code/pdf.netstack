using System.Text;

namespace PdfNetStack.Ocr.Processing;

/// <summary>A character read by the recognizer and the output column it came from.</summary>
internal readonly record struct ReadChar(string Value, int Column, float Confidence);

/// <summary>
/// Greedy CTC decoding (Graves et al., 2006): the best class in each column, repeats merged,
/// blanks dropped. Class 0 is the blank, then the model's characters, then a space when the
/// model has one more class than characters.
/// </summary>
internal static class Ctc
{
    public static List<ReadChar> Decode(ReadOnlySpan<float> scores, int columns, int classes, IReadOnlyList<string> characters)
    {
        bool space = classes == characters.Count + 2;
        var result = new List<ReadChar>();
        int previous = 0;
        for (int t = 0; t < columns; t++)
        {
            ReadOnlySpan<float> column = scores.Slice(t * classes, classes);
            int best = 0;
            for (int c = 1; c < classes; c++)
            {
                if (column[c] > column[best]) best = c;
            }

            if (best != 0 && best != previous)
            {
                string value = best <= characters.Count ? characters[best - 1] : space ? " " : "�";
                result.Add(new ReadChar(value, t, column[best]));
            }

            previous = best;
        }

        return result;
    }

    /// <summary>
    /// Visual to logical order for right to left text read left to right: the runs are reversed,
    /// a run being one right to left character or a stretch of left to right ones (Latin letters,
    /// digits and the punctuation between them), which keep their own order.
    /// </summary>
    public static string Logical(string visual)
    {
        var runs = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < visual.Length; i++)
        {
            char c = visual[i];

            // Separators inside a number or a Latin word ("12.50", "2026/10/29") belong to it.
            bool joins = Separator(c) && current.Length > 0 && i + 1 < visual.Length && LeftToRight(visual[i + 1]);
            if (LeftToRight(c) || joins)
            {
                current.Append(c);
                continue;
            }

            if (current.Length > 0) runs.Add(current.ToString());
            current.Clear();
            runs.Add(c.ToString());
        }

        if (current.Length > 0) runs.Add(current.ToString());
        runs.Reverse();
        return string.Concat(runs);
    }

    /// <summary>True when the text has a right to left letter (Hebrew, Arabic and their extensions).</summary>
    public static bool HasRightToLeft(string text) =>
        text.Any(c => !LeftToRight(c) && c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿');

    // Latin letters and digits, Arabic-Indic digits included, run left to right inside right to left text.
    private static bool LeftToRight(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' or >= '٠' and <= '٩' or >= '۰' and <= '۹';

    private static bool Separator(char c) => c is '.' or ',' or ':' or '/' or '%' or '+' or '-' or '٫' or '٬';
}
