using System.Globalization;
using System.Text;
using PdfNetStack.Text;

namespace PdfNetStack.Drawing;

/// <summary>
/// Finds where words may break with a hyphen, by Liang's pattern algorithm as TeX defines it
/// (Liang 1983, and The TeXbook, appendix H). Give one to <see cref="PdfParagraph.Hyphenator"/>
/// to hyphenate a paragraph, or use <see cref="InsertSoftHyphens"/> on text of your own. The core
/// library has the algorithm and no patterns: the <c>PdfNetStack.Hyphenation</c> package has them
/// for many languages, and <see cref="Create"/> takes any others.
/// </summary>
/// <remarks>
/// A hyphenator is immutable and safe to share between threads. Letters fold to lower case by
/// invariant rules, so results do not depend on the current culture. These are left alone: words
/// with a soft hyphen (U+00AD), whose breaks are the author's; words joined to a digit, or holding a
/// capital past the first letter; words of all capitals, unless <see cref="HyphenateAllCaps"/>;
/// anything that looks like a web address, e-mail address or file path; words with letters outside
/// the patterns' alphabet; and words over 64 letters, as in TeX.
/// </remarks>
/// <example>
/// <code>
/// // Your own patterns, in TeX's format, and exceptions in \hyphenation's.
/// PdfHyphenator h = PdfHyphenator.Create(patterns, ["ta-ble", "project"]) with { MinLeft = 2, MinRight = 3 };
/// string text = h.InsertSoftHyphens("Hyphenation");   // "Hy\u00ADphen\u00ADation"
/// </code>
/// </example>
public sealed record PdfHyphenator
{
    private const int MaxLetters = 64;
    private const char SoftHyphen = '\u00AD';

    private PdfHyphenator(PatternTrie patterns, Dictionary<string, int[]> exceptions, Dictionary<char, char> letters)
    {
        Patterns = patterns;
        Exceptions = exceptions;
        Letters = letters;
    }

    private PatternTrie Patterns { get; }

    private Dictionary<string, int[]> Exceptions { get; init; }

    /// <summary>The alphabet: each letter, in either case, to its lower case form (TeX's \lccode).</summary>
    private Dictionary<char, char> Letters { get; init; }

    /// <summary>The fewest letters a word needs to be hyphenated at all. Default 5, as CSS <c>hyphens: auto</c> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MinWordLength
    {
        get => field;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "At least one letter.");
    } = 5;

    /// <summary>The fewest letters before a hyphen (TeX's \lefthyphenmin). Default 2.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MinLeft
    {
        get => field;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "At least one letter.");
    } = 2;

    /// <summary>The fewest letters after a hyphen (TeX's \righthyphenmin). Default 3.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MinRight
    {
        get => field;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "At least one letter.");
    } = 3;

    /// <summary>Whether words of all capitals are hyphenated too. Default false: most are acronyms and names.</summary>
    public bool HyphenateAllCaps { get; init; }

    /// <summary>A hyphenator from patterns in TeX's format, such as <c>hy3ph</c> and <c>.ach4</c>, and exceptions.</summary>
    /// <param name="patterns">Letters with a digit in any gap; a dot at either end marks the edge of a word.</param>
    /// <param name="exceptions">Words with their only breaks marked, such as <c>ta-ble</c>; a word without a hyphen never breaks.</param>
    /// <exception cref="ArgumentException">A pattern or exception is malformed.</exception>
    public static PdfHyphenator Create(IEnumerable<string> patterns, IEnumerable<string>? exceptions = null)
    {
        Compat.ThrowIfNull(patterns, nameof(patterns));
        var letters = new Dictionary<char, char>();
        PatternTrie trie = PatternTrie.Build(patterns, letters);
        var result = new PdfHyphenator(trie, [], letters);
        return exceptions is null ? result : result.WithExceptions(exceptions);
    }

    /// <summary>
    /// A copy of this hyphenator that also knows these words, which take precedence over the
    /// patterns and over earlier exceptions. <see cref="MinLeft"/> and <see cref="MinRight"/> still
    /// apply to them, as in TeX, and letters new to the alphabet join it.
    /// </summary>
    /// <param name="words">Words with their only breaks marked, such as <c>ta-ble</c>; a word without a hyphen never breaks.</param>
    /// <exception cref="ArgumentException">A word is empty or holds a digit, a dot or two hyphens in a row.</exception>
    public PdfHyphenator WithExceptions(IEnumerable<string> words)
    {
        Compat.ThrowIfNull(words, nameof(words));
        var exceptions = new Dictionary<string, int[]>(Exceptions);
        var letters = new Dictionary<char, char>(Letters);
        var key = new StringBuilder();
        var points = new List<int>();
        foreach (string word in words)
        {
            key.Clear();
            points.Clear();
            bool hyphen = true;
            foreach (char c in word ?? throw new ArgumentException("An exception is null.", nameof(words)))
            {
                if (c == '-')
                {
                    if (hyphen) throw new ArgumentException($"Malformed exception \"{word}\".", nameof(words));
                    points.Add(key.Length);
                    hyphen = true;
                    continue;
                }

                if (c is '.' or (>= '0' and <= '9') || char.IsWhiteSpace(c)) throw new ArgumentException($"Malformed exception \"{word}\".", nameof(words));
                key.Append(AddLetter(letters, c));
                hyphen = false;
            }

            if (key.Length == 0 || hyphen) throw new ArgumentException($"Malformed exception \"{word}\".", nameof(words));
            exceptions[key.ToString()] = [.. points];
        }

        return this with { Exceptions = exceptions, Letters = letters };
    }

    /// <summary>Where <paramref name="text"/> may break with a hyphen: indexes of the characters that would start the next line, in order.</summary>
    public IReadOnlyList<int> FindBreaks(string text)
    {
        Compat.ThrowIfNull(text, nameof(text));
        var breaks = new List<int>();
        FindBreaks(text, 0, text.Length, breaks);
        return breaks;
    }

    /// <summary>The text with a soft hyphen (U+00AD) at every place it may break. Layouts, viewers and browsers honor those without a hyphenator.</summary>
    public string InsertSoftHyphens(string text)
    {
        Compat.ThrowIfNull(text, nameof(text));
        var breaks = new List<int>();
        FindBreaks(text, 0, text.Length, breaks);
        if (breaks.Count == 0) return text;
        var sb = new StringBuilder(text.Length + breaks.Count);
        int last = 0;
        foreach (int b in breaks)
        {
            sb.Append(text, last, b - last).Append(SoftHyphen);
            last = b;
        }

        return sb.Append(text, last, text.Length - last).ToString();
    }

    /// <summary>
    /// Appends the hyphenation points of a range of a paragraph, in order. Characters next to the
    /// range count when deciding whether a word stops at its edge, so a word that continues past
    /// the range is left alone.
    /// </summary>
    internal void FindBreaks(string text, int start, int end, List<int> breaks)
    {
        int i = start;
        while (i < end)
        {
            if (IsSeparator(text[i]))
            {
                i++;
                continue;
            }

            int tokenEnd = i + 1;
            while (tokenEnd < end && !IsSeparator(text[tokenEnd])) tokenEnd++;
            if (!IsAddress(text, i, tokenEnd)) FindBreaksInToken(text, i, tokenEnd, breaks);
            i = tokenEnd;
        }
    }

    /// <summary>
    /// The line breaks of a paragraph with its hyphenation points added in order. A point already
    /// a line break stays one without a hyphen. Marks the added points in <paramref name="hyphens"/>.
    /// </summary>
    internal static List<LineBreak> MergeBreaks(List<LineBreak> breaks, List<int> points, bool[] hyphens)
    {
        var merged = new List<LineBreak>(breaks.Count + points.Count);
        int next = 0;
        foreach (int point in points)
        {
            while (next < breaks.Count && breaks[next].Position < point) merged.Add(breaks[next++]);
            if (next < breaks.Count && breaks[next].Position == point) continue;
            merged.Add(new LineBreak(point, false));
            hyphens[point] = true;
        }

        while (next < breaks.Count) merged.Add(breaks[next++]);
        return merged;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c == '\u200B';

    /// <summary>Web addresses, e-mail addresses, paths, domain names and the like: never hyphenated.</summary>
    private static bool IsAddress(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c is '@' or '\\' or '_') return true;
            if (c == ':' && i + 2 < end && text[i + 1] == '/' && text[i + 2] == '/') return true;
            if (c is '.' or '/' or ':' or '=' or '&' or '?' or '#' or '%' or '~' or '+'
                && i > start && i + 1 < end && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private void FindBreaksInToken(string text, int start, int end, List<int> breaks)
    {
        int i = start;
        while (i < end)
        {
            if (!IsWordCharacter(text[i]))
            {
                i++;
                continue;
            }

            int run = i;
            while (i < end && IsWordCharacter(text[i])) i++;
            FindBreaksInWord(text, run, i, breaks);
        }
    }

    private bool IsWordCharacter(char c) => Letters.ContainsKey(c) || IsMark(c);

    private static bool IsMark(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsApostrophe(char c) => c is '\'' or '\u2019' or '\u02BC';

    /// <summary>Whether the character beside a word, going in <paramref name="step"/>, makes it part of something larger.</summary>
    private static bool Continues(string text, int index, int step)
    {
        if (index < 0 || index >= text.Length) return false;
        char c = text[index];
        if (char.IsLetterOrDigit(c) || char.IsSurrogate(c) || IsMark(c)) return true;

        // Soft hyphens, joiners and word joiners: the author has decided about this word.
        if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format && c != '\u200B') return true;
        int beyond = index + step;
        return IsApostrophe(c) && beyond >= 0 && beyond < text.Length && char.IsLetterOrDigit(text[beyond]);
    }

    private void FindBreaksInWord(string text, int start, int end, List<int> breaks)
    {
        // Quotation marks at the edges are not part of the word, even when the alphabet has them.
        while (start < end && IsApostrophe(text[start])) start++;
        while (end > start && IsApostrophe(text[end - 1])) end--;
        if (end - start < Math.Max(MinWordLength, MinLeft + MinRight) || Continues(text, start - 1, -1) || Continues(text, end, 1)) return;

        Span<char> word = stackalloc char[MaxLetters + 2];
        Span<int> source = stackalloc int[MaxLetters + 1];
        word[0] = '.';
        int count = 0, letters = 0, capitals = 0;
        bool innerCapital = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (Letters.TryGetValue(c, out char lower))
            {
                if (count == MaxLetters) return;
                if (lower != c)
                {
                    capitals++;
                    if (count > 0 && !IsApostrophe(word[count])) innerCapital = true;
                }

                word[++count] = lower;
                source[count - 1] = i;
                if (!IsApostrophe(c) && !IsMark(c)) letters++;
                continue;
            }

            // A mark outside the alphabet counts only when it composes with its letter: patterns are precomposed.
            if (count > 0 && IsMark(c) && ShapingData.Compose(word[count], c, out int composed) && composed <= char.MaxValue && Letters.TryGetValue((char)composed, out lower))
            {
                word[count] = lower;
                continue;
            }

            return;
        }

        if (letters < MinWordLength || count < MinLeft + MinRight) return;
        if (capitals > 1 && capitals == letters)
        {
            if (!HyphenateAllCaps) return;
        }
        else if (innerCapital)
        {
            return;
        }

        word[count + 1] = '.';
        if (Exceptions.Count > 0 && Exceptions.TryGetValue(word.Slice(1, count).ToString(), out int[]? points))
        {
            foreach (int p in points) AddBreak(text, source, p, count, breaks);
            return;
        }

        // The gap before word[k] takes the largest digit any matching pattern puts there; odd allows a break.
        Span<byte> gaps = stackalloc byte[MaxLetters + 3];
        gaps.Clear();
        Patterns.Apply(word.Slice(0, count + 2), gaps);
        for (int p = MinLeft; p <= count - MinRight; p++)
        {
            if ((gaps[p + 1] & 1) == 1) AddBreak(text, source, p, count, breaks);
        }
    }

    /// <summary>A break after <paramref name="letter"/> letters, inside the minimums and not between a letter and its mark.</summary>
    private void AddBreak(string text, ReadOnlySpan<int> source, int letter, int count, List<int> breaks)
    {
        if (letter < MinLeft || letter > count - MinRight) return;
        int index = source[letter];
        if (!IsMark(text[index])) breaks.Add(index);
    }

    /// <summary>Adds a letter and its other case to the alphabet; returns its lower case form.</summary>
    private static char AddLetter(Dictionary<char, char> letters, char c)
    {
        char lower = char.ToLowerInvariant(c);
        letters[lower] = lower;
        letters[c] = lower;
        char upper = char.ToUpperInvariant(lower);
        if (upper != lower && char.ToLowerInvariant(upper) == lower) letters[upper] = lower;

        // Capital sharp s: it has no simple case mapping from the small letter.
        if (lower == '\u00DF') letters['\u1E9E'] = lower;
        return lower;
    }

    /// <summary>The patterns in a trie, as compact arrays. Node 0 is the root; as a child or sibling, 0 means none.</summary>
    private sealed class PatternTrie
    {
        private readonly char[] _label;
        private readonly int[] _child;
        private readonly int[] _sibling;
        private readonly int[] _value;
        private readonly byte[][] _values;

        private PatternTrie(char[] label, int[] child, int[] sibling, int[] value, byte[][] values)
        {
            _label = label;
            _child = child;
            _sibling = sibling;
            _value = value;
            _values = values;
        }

        public static PatternTrie Build(IEnumerable<string> patterns, Dictionary<char, char> letters)
        {
            List<char> label = ['\0'];
            List<int> child = [0], sibling = [0], value = [0];
            List<byte[]> values = [[]];
            var shared = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern)) throw new ArgumentException("A pattern is empty.", nameof(patterns));
                var digits = new char[pattern.Length + 1];
                int node = 0, length = 0;
                bool digit = false;
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c is >= '0' and <= '9')
                    {
                        if (digit) throw new ArgumentException($"Malformed pattern \"{pattern}\".", nameof(patterns));
                        digits[length] = c;
                        digit = true;
                        continue;
                    }

                    digit = false;
                    if (c == '.')
                    {
                        // The edge of a word: first or last, with only a digit around it.
                        bool edge = length == 0 || pattern.AsSpan(i + 1).TrimStart("0123456789".AsSpan()).Length == 0;
                        if (!edge || pattern.Length == 1) throw new ArgumentException($"Malformed pattern \"{pattern}\".", nameof(patterns));
                    }
                    else
                    {
                        c = AddLetter(letters, c);
                    }

                    node = FindOrAdd(node, c, label, child, sibling, value);
                    length++;
                }

                if (length == 0) throw new ArgumentException($"Malformed pattern \"{pattern}\".", nameof(patterns));
                int kept = length + 1;
                string key = new(digits, 0, kept);
                if (!shared.TryGetValue(key, out int index))
                {
                    var bytes = new byte[kept];
                    for (int i = 0; i < kept; i++) bytes[i] = digits[i] == '\0' ? (byte)0 : (byte)(digits[i] - '0');
                    index = values.Count;
                    values.Add(bytes);
                    shared[key] = index;
                }

                value[node] = index;
            }

            return new PatternTrie([.. label], [.. child], [.. sibling], [.. value], [.. values]);
        }

        private static int FindOrAdd(int node, char c, List<char> label, List<int> child, List<int> sibling, List<int> value)
        {
            for (int n = child[node]; n != 0; n = sibling[n])
            {
                if (label[n] == c) return n;
            }

            int added = label.Count;
            label.Add(c);
            child.Add(0);
            sibling.Add(child[node]);
            value.Add(0);
            child[node] = added;
            return added;
        }

        /// <summary>Runs every pattern that matches inside the dotted word, keeping the largest digit in each gap.</summary>
        public void Apply(ReadOnlySpan<char> word, Span<byte> gaps)
        {
            for (int start = 0; start < word.Length; start++)
            {
                int node = 0;
                for (int k = start; k < word.Length; k++)
                {
                    char c = word[k];
                    int n = _child[node];
                    while (n != 0 && _label[n] != c) n = _sibling[n];
                    if (n == 0) break;
                    node = n;
                    if (_value[node] == 0) continue;
                    byte[] digits = _values[_value[node]];
                    for (int d = 0; d < digits.Length; d++)
                    {
                        if (digits[d] > gaps[start + d]) gaps[start + d] = digits[d];
                    }
                }
            }
        }
    }
}
