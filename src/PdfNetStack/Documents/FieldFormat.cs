using System.Globalization;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>
/// The formatting a field's format action (/AA /F) asks of the Acrobat form functions
/// AFNumber_Format, AFPercent_Format, AFDate_Format, AFDate_FormatEx, AFTime_Format,
/// AFTime_FormatEx and AFSpecial_Format, done in C# so generated appearances show values as
/// viewers do. No JavaScript runs: the script must be one call to a known function with literal
/// arguments, otherwise the value shows as it is. A value the format cannot read also shows as it is.
/// </summary>
internal static class FieldFormat
{
    private const int MaxScriptLength = 4096;
    private const int MaxDecimals = 10;

    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    // AFDate_Format and AFTime_Format take an index into these lists.
    private static readonly string[] DateFormats = ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];
    private static readonly string[] TimeFormats = ["HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"];

    /// <summary>The text a field shows for <paramref name="value"/>, and the color a negative number asks for, or null.</summary>
    public static (string Text, PdfColor? Color) Apply(PdfField field, string value)
    {
        if (value.Length == 0 || Script(field) is not string script || Call.Parse(script) is not Call call) return (value, null);
        return Apply(call, value) ?? (value, null);
    }

    /// <summary>Formats with a parsed call; null when the function is unknown or the value does not fit it.</summary>
    internal static (string Text, PdfColor? Color)? Apply(Call call, string value)
    {
        switch (call.Name)
        {
            case "AFNumber_Format":
                return Number(value, call.Int(0, 0), call.Int(1, 0), call.Int(2, 0), call.Text(4, ""), call.Bool(5, true));
            case "AFPercent_Format":
                return Percent(value, call.Int(0, 0), call.Int(1, 0), call.Bool(2, false)) is string percent ? (percent, null) : null;
            case "AFDate_FormatEx":
            case "AFTime_FormatEx":
                return call.Text(0, "") is { Length: > 0 } custom && Date(value, custom) is string date ? (date, null) : null;
            case "AFDate_Format":
                return call.Int(0, -1) is int d && d >= 0 && d < DateFormats.Length && Date(value, DateFormats[d]) is string listed ? (listed, null) : null;
            case "AFTime_Format":
                return call.Int(0, -1) is int t && t >= 0 && t < TimeFormats.Length && Date(value, TimeFormats[t]) is string time ? (time, null) : null;
            case "AFSpecial_Format":
                return Special(value, call.Int(0, -1)) is string special ? (special, null) : null;
            default:
                return null;
        }
    }

    /// <summary>The format action's script, when the field has one.</summary>
    private static string? Script(PdfField field)
    {
        PdfDocument doc = field.Doc;
        if (doc.Resolve(field.Dictionary["AA"]) is not PdfDictionary aa || doc.Resolve(aa["F"]) is not PdfDictionary action) return null;
        if (action.GetName("S") != "JavaScript") return null;
        string? script = doc.Resolve(action["JS"]) switch
        {
            PdfString s when s.Value.Length <= MaxScriptLength * 2 => s.ToText(),
            PdfStream s => Decode(doc, s),
            _ => null,
        };
        return script is { Length: <= MaxScriptLength } ? script : null;
    }

    /// <summary>A script stream, decoded under a limit of its own so a compression bomb stops early.</summary>
    private static string? Decode(PdfDocument doc, PdfStream stream)
    {
        try
        {
            List<Filters.FilterSpec> filters = Filters.StreamCodec.GetFilters(stream.Dictionary, doc.Resolve);
            using Filters.DecodedStream decoded = Filters.StreamCodec.Decode(stream.Data, filters, Filters.DecodeDepth.Full, MaxScriptLength * 2, doc.Resolve);
            return decoded.PendingPredictor is null ? Documents.PdfText.Decode(decoded.Data.ToArray()) : null;
        }
        catch (Filters.StreamDecodeException)
        {
            return null;
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// AFNumber_Format: <paramref name="decimals"/> places; separators by style (0 "1,234.56",
    /// 1 "1234.56", 2 "1.234,56", 3 "1234,56", 4 "1'234.56"); negatives by style (0 minus sign,
    /// 1 red, 2 parentheses, 3 red parentheses); the currency before or after the number.
    /// </summary>
    internal static (string Text, PdfColor? Color)? Number(string value, int decimals, int separators, int negative, string currency, bool currencyFirst)
    {
        if (ParseNumber(value, separators) is not decimal number) return null;
        decimal rounded = Math.Round(number, Math.Max(0, Math.Min(decimals, MaxDecimals)), MidpointRounding.AwayFromZero);
        string digits = Digits(Math.Abs(rounded), decimals, separators);
        string text = currencyFirst ? currency + digits : digits + currency;
        if (rounded >= 0) return (text, null);
        PdfColor red = PdfColor.Rgb(1, 0, 0);
        return negative switch
        {
            1 => (text, red),
            2 => ("(" + text + ")", null),
            3 => ("(" + text + ")", red),
            _ => ("-" + text, null),
        };
    }

    /// <summary>AFPercent_Format: the value times 100 with a percent sign, separators as for numbers.</summary>
    internal static string? Percent(string value, int decimals, int separators, bool percentFirst)
    {
        if (ParseNumber(value, separators) is not decimal number || Math.Abs(number) > decimal.MaxValue / 100) return null;
        decimal rounded = Math.Round(number * 100, Math.Max(0, Math.Min(decimals, MaxDecimals)), MidpointRounding.AwayFromZero);
        string digits = Digits(Math.Abs(rounded), decimals, separators);
        string sign = rounded < 0 ? "-" : "";
        return percentFirst ? sign + "%" + digits : sign + digits + "%";
    }

    /// <summary>
    /// A date or time in an Acrobat date format, such as "mm/dd/yyyy" or "h:MM tt". The value is
    /// read in that format first, then as an ISO 8601 or PDF date.
    /// </summary>
    internal static string? Date(string value, string format)
    {
        List<string> tokens = Tokens(format);
        if ((Scan(value, tokens) ?? Iso(value)) is not DateTime date) return null;
        var sb = new StringBuilder();
        foreach (string token in tokens)
        {
            int hour12 = date.Hour % 12 == 0 ? 12 : date.Hour % 12;
            sb.Append(token switch
            {
                "d" => date.Day.ToString(CultureInfo.InvariantCulture),
                "dd" => date.Day.ToString("00", CultureInfo.InvariantCulture),
                "ddd" => DayNames[(int)date.DayOfWeek].Substring(0, 3),
                "dddd" => DayNames[(int)date.DayOfWeek],
                "m" => date.Month.ToString(CultureInfo.InvariantCulture),
                "mm" => date.Month.ToString("00", CultureInfo.InvariantCulture),
                "mmm" => MonthNames[date.Month - 1].Substring(0, 3),
                "mmmm" => MonthNames[date.Month - 1],
                "yy" => (date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                "yyyy" => date.Year.ToString("0000", CultureInfo.InvariantCulture),
                "H" => date.Hour.ToString(CultureInfo.InvariantCulture),
                "HH" => date.Hour.ToString("00", CultureInfo.InvariantCulture),
                "h" => hour12.ToString(CultureInfo.InvariantCulture),
                "hh" => hour12.ToString("00", CultureInfo.InvariantCulture),
                "M" => date.Minute.ToString(CultureInfo.InvariantCulture),
                "MM" => date.Minute.ToString("00", CultureInfo.InvariantCulture),
                "s" => date.Second.ToString(CultureInfo.InvariantCulture),
                "ss" => date.Second.ToString("00", CultureInfo.InvariantCulture),
                "t" => date.Hour < 12 ? "a" : "p",
                "tt" => date.Hour < 12 ? "am" : "pm",
                _ => token[0] == '\\' ? token.Substring(1) : token,
            });
        }

        return sb.ToString();
    }

    /// <summary>AFSpecial_Format: 0 ZIP code, 1 ZIP+4, 2 phone number, 3 social security number, from the value's digits.</summary>
    internal static string? Special(string value, int kind)
    {
        string d = new(value.Where(c => c is >= '0' and <= '9').ToArray());
        if (value.Any(c => char.IsLetter(c))) return null;
        string? mask = (kind, d.Length) switch
        {
            (0, 5) => "99999",
            (1, 9) => "99999-9999",
            (2, 10) => "(999) 999-9999",
            (2, 7) => "999-9999",
            (3, 9) => "999-99-9999",
            _ => null,
        };
        if (mask is null) return null;
        var sb = new StringBuilder(mask.Length);
        int next = 0;
        foreach (char c in mask) sb.Append(c == '9' ? d[next++] : c);
        return sb.ToString();
    }

    /// <summary>
    /// A number as typed. With both a point and a comma, the last is the decimal mark. A lone
    /// kind of separator is the style's decimal mark (a comma for styles 2 and 3, a point
    /// otherwise), or else a group separator when it splits the digits in threes.
    /// </summary>
    private static decimal? ParseNumber(string value, int separators)
    {
        string s = value.Trim().Replace(" ", "").Replace("'", "");
        if (s.Length == 0 || s.Length > 64) return null;
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        char? mark = null;
        if (dot >= 0 && comma >= 0)
        {
            mark = dot > comma ? '.' : ',';
        }
        else if (dot >= 0 || comma >= 0)
        {
            char only = dot >= 0 ? '.' : ',';
            bool preferred = separators is 2 or 3 ? only == ',' : only == '.';
            if (preferred || !Grouped(s, only)) mark = only;
        }

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == mark) sb.Append('.');
            else if (c is not ('.' or ',')) sb.Append(c);
        }

        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        return decimal.TryParse(sb.ToString(), style, CultureInfo.InvariantCulture, out decimal n) ? n : null;
    }

    /// <summary>True when <paramref name="separator"/> splits the digits into groups of three, as in 1,234,567.</summary>
    private static bool Grouped(string s, char separator)
    {
        string[] groups = s.TrimStart('-', '+').Split(separator);
        return groups.Length > 1 && groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(g => g.Length == 3);
    }

    private static string Digits(decimal value, int decimals, int separators)
    {
        decimals = Math.Max(0, Math.Min(decimals, MaxDecimals));
        string plain = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int point = plain.IndexOf('.');
        string whole = point < 0 ? plain : plain.Substring(0, point);
        string fraction = point < 0 ? "" : plain.Substring(point + 1);
        (string group, string mark) = separators switch
        {
            1 => ("", "."),
            2 => (".", ","),
            3 => ("", ","),
            4 => ("'", "."),
            _ => (",", "."),
        };
        var sb = new StringBuilder();
        for (int i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(group);
            sb.Append(whole[i]);
        }

        if (fraction.Length > 0) sb.Append(mark).Append(fraction);
        return sb.ToString();
    }

    /// <summary>Splits a date format into fields (runs of d, m, y, H, h, M, s, t) and literal text; a backslash quotes the next character.</summary>
    private static List<string> Tokens(string format)
    {
        var tokens = new List<string>();
        for (int i = 0; i < format.Length;)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                tokens.Add(format.Substring(i, 2));
                i += 2;
                continue;
            }

            int max = c switch { 'd' or 'm' => 4, 'y' => 4, 'H' or 'h' or 'M' or 's' or 't' => 2, _ => 0 };
            int run = 1;
            while (run < max && i + run < format.Length && format[i + run] == c) run++;
            string token = format.Substring(i, run);
            bool field = max > 0 && !(c == 'y' && run is 1 or 3);
            tokens.Add(field ? token : "\\" + token);
            i += run;
        }

        return tokens;
    }

    /// <summary>Reads a date written in the format; parts the format lacks default to January 1 of the current year, midnight.</summary>
    private static DateTime? Scan(string value, List<string> tokens)
    {
        int year = DateTime.Today.Year, month = 1, day = 1, hour = 0, minute = 0, second = 0;
        bool? pm = null;
        int pos = 0;
        foreach (string token in tokens)
        {
            SkipSpaces();
            if (token[0] == '\\')
            {
                foreach (char c in token.Substring(1))
                {
                    if (char.IsWhiteSpace(c)) continue;
                    if (pos >= value.Length || char.ToLowerInvariant(value[pos]) != char.ToLowerInvariant(c)) return null;
                    pos++;
                }

                continue;
            }

            switch (token)
            {
                case "d" or "dd":
                    if (Digits(2) is not int dd) return null;
                    day = dd;
                    break;
                case "ddd" or "dddd":
                    while (pos < value.Length && char.IsLetter(value[pos])) pos++;
                    break;
                case "m" or "mm":
                    if (Digits(2) is not int mm) return null;
                    month = mm;
                    break;
                case "mmm" or "mmmm":
                    int start = pos;
                    while (pos < value.Length && char.IsLetter(value[pos])) pos++;
                    string word = value.Substring(start, pos - start);
                    int found = word.Length >= 3 ? System.Array.FindIndex(MonthNames, n => n.StartsWith(word.Substring(0, 3), StringComparison.OrdinalIgnoreCase)) : -1;
                    if (found < 0) return null;
                    month = found + 1;
                    break;
                case "yy":
                    if (Digits(2) is not int yy) return null;
                    year = yy < 50 ? 2000 + yy : 1900 + yy;
                    break;
                case "yyyy":
                    if (Digits(4) is not int yyyy) return null;
                    year = yyyy;
                    break;
                case "H" or "HH" or "h" or "hh":
                    if (Digits(2) is not int hh) return null;
                    hour = hh;
                    break;
                case "M" or "MM":
                    if (Digits(2) is not int min) return null;
                    minute = min;
                    break;
                case "s" or "ss":
                    if (Digits(2) is not int sec) return null;
                    second = sec;
                    break;
                case "t" or "tt":
                    if (pos >= value.Length || char.ToLowerInvariant(value[pos]) is not ('a' or 'p')) return null;
                    pm = char.ToLowerInvariant(value[pos]) == 'p';
                    pos++;
                    if (pos < value.Length && char.ToLowerInvariant(value[pos]) == 'm') pos++;
                    break;
            }
        }

        SkipSpaces();
        if (pos != value.Length) return null;
        if (pm is bool afternoon)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (afternoon ? 12 : 0);
        }

        return Valid(year, month, day, hour, minute, second);

        void SkipSpaces()
        {
            while (pos < value.Length && char.IsWhiteSpace(value[pos])) pos++;
        }

        int? Digits(int max)
        {
            int start = pos, n = 0;
            while (pos < value.Length && pos - start < max && value[pos] is >= '0' and <= '9') n = n * 10 + (value[pos++] - '0');
            return pos > start ? n : null;
        }
    }

    /// <summary>An ISO 8601 date (2024-03-07, with an optional time) or a PDF date (D:20240307143000).</summary>
    private static DateTime? Iso(string value)
    {
        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            string d = new(s.Substring(2).TakeWhile(c => c is >= '0' and <= '9').Take(14).ToArray());
            if (d.Length < 4) return null;
            int Part(int at, int length, int fallback)
            {
                if (d.Length < at + length) return fallback;
                int n = 0;
                for (int i = at; i < at + length; i++) n = n * 10 + (d[i] - '0');
                return n;
            }

            return Valid(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0));
        }

        string[] formats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "HH:mm", "HH:mm:ss"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out DateTime date)
            ? date.Year == 1 && date.Month == 1 && date.Day == 1 ? new DateTime(DateTime.Today.Year, 1, 1, date.Hour, date.Minute, date.Second) : date
            : null;
    }

    private static DateTime? Valid(int year, int month, int day, int hour, int minute, int second) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            && hour is >= 0 and <= 23 && minute is >= 0 and <= 59 && second is >= 0 and <= 59
            ? new DateTime(year, month, day, hour, minute, second)
            : null;

    /// <summary>One call to a function with literal arguments: numbers, strings, true and false.</summary>
    internal sealed class Call
    {
        private readonly List<object> _args;

        private Call(string name, List<object> args)
        {
            Name = name;
            _args = args;
        }

        public string Name { get; }

        /// <summary>The call a script is made of, or null when the script is anything else.</summary>
        public static Call? Parse(string script)
        {
            int pos = 0;
            Skip();
            int start = pos;
            while (pos < script.Length && (char.IsLetterOrDigit(script[pos]) || script[pos] == '_')) pos++;
            if (pos == start || char.IsDigit(script[start])) return null;
            string name = script.Substring(start, pos - start);
            Skip();
            if (!Take('(')) return null;
            var args = new List<object>();
            Skip();
            if (!Take(')'))
            {
                while (true)
                {
                    Skip();
                    if (Argument() is not object arg || args.Count >= 16) return null;
                    args.Add(arg);
                    Skip();
                    if (Take(')')) break;
                    if (!Take(',')) return null;
                }
            }

            Skip();
            Take(';');
            Skip();
            return pos == script.Length ? new Call(name, args) : null;

            void Skip()
            {
                while (pos < script.Length && char.IsWhiteSpace(script[pos])) pos++;
            }

            bool Take(char c)
            {
                if (pos >= script.Length || script[pos] != c) return false;
                pos++;
                return true;
            }

            object? Argument()
            {
                if (pos >= script.Length) return null;
                char c = script[pos];
                if (c is '"' or '\'') return Quoted(c);
                int from = pos;
                while (pos < script.Length && (char.IsLetterOrDigit(script[pos]) || script[pos] is '.' or '-' or '+')) pos++;
                string word = script.Substring(from, pos - from);
                if (word == "true") return true;
                if (word == "false") return false;
                return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && !double.IsInfinity(n) ? n : null;
            }

            string? Quoted(char quote)
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < script.Length && script[pos] != quote)
                {
                    char c = script[pos++];
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (pos >= script.Length) return null;
                    char e = script[pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u' when Hex(pos) is int code:
                            sb.Append((char)code);
                            pos += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }

                return Take(quote) ? sb.ToString() : null;
            }

            int? Hex(int at)
            {
                if (at + 4 > script.Length) return null;
                int code = 0;
                for (int i = at; i < at + 4; i++)
                {
                    int digit = script[i] switch
                    {
                        >= '0' and <= '9' => script[i] - '0',
                        >= 'a' and <= 'f' => script[i] - 'a' + 10,
                        >= 'A' and <= 'F' => script[i] - 'A' + 10,
                        _ => -1,
                    };
                    if (digit < 0) return null;
                    code = code * 16 + digit;
                }

                return code;
            }
        }

        public int Int(int index, int fallback) => index < _args.Count
            ? _args[index] switch
            {
                double d when d is >= -1e6 and <= 1e6 => (int)d,
                bool b => b ? 1 : 0,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
                _ => fallback,
            }
            : fallback;

        public bool Bool(int index, bool fallback) => index < _args.Count
            ? _args[index] switch
            {
                bool b => b,
                double d => d != 0,
                string s => s.Length > 0 && s != "0" && s != "false",
                _ => fallback,
            }
            : fallback;

        public string Text(int index, string fallback) => index < _args.Count
            ? _args[index] switch
            {
                string s => s,
                double d => d.ToString(CultureInfo.InvariantCulture),
                _ => fallback,
            }
            : fallback;
    }
}
