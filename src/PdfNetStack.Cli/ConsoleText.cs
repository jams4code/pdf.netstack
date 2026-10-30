using System.Globalization;
using System.Text;

namespace PdfNetStack.Cli;

/// <summary>Console output shared by the command line tools.</summary>
internal static class ConsoleText
{
    /// <summary>
    /// Text from a document is untrusted. Control, C1 and bidirectional override characters are
    /// shown as escapes so a crafted title cannot drive the terminal.
    /// </summary>
    internal static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            bool unsafeChar = char.IsControl(c) || c is >= '\u0080' and <= '\u009F' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜';
            if (unsafeChar) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MiB",
        >= 1L << 10 => $"{bytes / 1024.0:F1} KiB",
        _ => $"{bytes} B",
    };
}
