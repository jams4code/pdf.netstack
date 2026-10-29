using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// A CMap (ISO 32000-1, 9.7.5 and 9.10.3; Adobe Technical Note 5014 and 5411): the code space
/// that splits a string into codes of 1 to 4 bytes, and mappings from codes to CIDs (an encoding)
/// or to Unicode (a ToUnicode map).
/// </summary>
/// <remarks>
/// CMaps come from untrusted files. Ranges are kept as ranges and never expanded, the number of
/// mappings is capped at <see cref="MaxEntries"/>, and usecmap chains at <see cref="MaxDepth"/>.
/// Parsing never throws: a damaged CMap keeps what was read before the damage.
/// </remarks>
internal sealed class CMap
{
    /// <summary>Mappings one CMap may hold, a range counting once however wide.</summary>
    public const int MaxEntries = 1 << 18;

    /// <summary>How deep usecmap may chain.</summary>
    public const int MaxDepth = 8;

    private const int MaxCodespaces = 256;

    // How far back a lookup looks among ranges that start before the code, for overlapping ranges.
    private const int OverlapScan = 16;

    private readonly List<Codespace> _codespace = [];
    private readonly Dictionary<uint, int> _cids = [];
    private readonly Dictionary<uint, string> _unicode = [];
    private readonly List<Range> _cidRanges = [];
    private readonly List<Range> _unicodeRanges = [];
    private readonly List<Range> _notdefRanges = [];
    private CMap? _parent;
    private int _entries;
    private bool _sorted = true;

    private enum UnicodeForm
    {
        None,
        Utf16,
        Utf8,
        Utf32,
    }

    private UnicodeForm _form;
    private bool _identity;
    private bool _wmodeDefined;

    /// <summary>0 for horizontal writing, 1 for vertical (ISO 32000-1, 9.7.5.2).</summary>
    public int WMode { get; private set; }

    /// <summary>
    /// The /Ordering of the Adobe character collection the CIDs belong to, for a predefined CJK CMap
    /// and an embedded one that uses it; null otherwise.
    /// </summary>
    public string? Ordering { get; private set; }

    /// <summary>True once a usecmap parent is set.</summary>
    public bool HasParent => _parent is not null;

    /// <summary>True when the CMap defines how codes are split.</summary>
    public bool HasCodespace => _codespace.Count > 0;

    /// <summary>True when a code's CID is known: an identity CMap, or one with CID mappings.</summary>
    public bool MapsCids => _identity || _cids.Count > 0 || _cidRanges.Count > 0 || (_parent?.MapsCids ?? false);

    /// <summary>
    /// A predefined CMap: Identity-H and Identity-V, Adobe's CJK CMaps (<see cref="PredefinedCMaps"/>),
    /// and for any other name of the Unicode form (Uni...-UCS2, -UTF16, -UTF8 and -UTF32) a CMap
    /// whose codes give their text but no CIDs. Null for any other name.
    /// </summary>
    /// <remarks>The CJK CMaps are shared by every document and are never changed.</remarks>
    public static CMap? Predefined(string? name)
    {
        if (name is null) return null;
        var cmap = new CMap { WMode = name.EndsWith("-V", StringComparison.Ordinal) ? 1 : 0 };
        if (name is "Identity-H" or "Identity-V")
        {
            cmap._identity = true;
            cmap.AddCodespace([0x00, 0x00], [0xFF, 0xFF]);
            return cmap;
        }

        if (PredefinedCMaps.Get(name) is CMap packed) return packed;

        if (!name.StartsWith("Uni", StringComparison.Ordinal)) return null;
        if (name.Contains("-UCS2-") || name.Contains("-UTF16-"))
        {
            cmap._form = UnicodeForm.Utf16;
            if (name.Contains("-UCS2-"))
            {
                cmap.AddCodespace([0x00, 0x00], [0xFF, 0xFF]);
            }
            else
            {
                cmap.AddCodespace([0x00, 0x00], [0xD7, 0xFF]);
                cmap.AddCodespace([0xE0, 0x00], [0xFF, 0xFF]);
                cmap.AddCodespace([0xD8, 0xDC, 0x00, 0x00], [0xDB, 0xDF, 0xFF, 0xFF]);
            }
        }
        else if (name.Contains("-UTF8-"))
        {
            cmap._form = UnicodeForm.Utf8;
            cmap.AddCodespace([0x00], [0x7F]);
            cmap.AddCodespace([0xC0, 0x80], [0xDF, 0xBF]);
            cmap.AddCodespace([0xE0, 0x80, 0x80], [0xEF, 0xBF, 0xBF]);
            cmap.AddCodespace([0xF0, 0x80, 0x80, 0x80], [0xF7, 0xBF, 0xBF, 0xBF]);
        }
        else if (name.Contains("-UTF32-"))
        {
            cmap._form = UnicodeForm.Utf32;
            cmap.AddCodespace([0x00, 0x00, 0x00, 0x00], [0x00, 0x10, 0xFF, 0xFF]);
        }
        else
        {
            return null;
        }

        return cmap;
    }

    /// <summary>
    /// Parses a CMap program. <paramref name="useCMap"/> resolves the name a usecmap operator
    /// names, given the depth the parent will parse at; it is not called beyond <see cref="MaxDepth"/>.
    /// </summary>
    public static CMap Parse(ReadOnlyMemory<byte> data, Func<string, int, CMap?>? useCMap = null, int depth = 0)
    {
        var cmap = new CMap();
        cmap.Read(data, useCMap, depth);
        return cmap;
    }

    /// <summary>
    /// Reads the next code at <paramref name="at"/>: the code space decides its length (ISO 32000-1,
    /// 9.7.6.2). Bytes that match no range take the length of a range their first byte fits, else
    /// the shortest length, so a damaged string still advances.
    /// </summary>
    /// <returns>The number of bytes read, at least 1.</returns>
    public int NextCode(ReadOnlySpan<byte> s, int at, out uint code)
    {
        int remaining = s.Length - at;
        for (int length = 1; length <= 4 && length <= remaining; length++)
        {
            foreach (Codespace c in _codespace)
            {
                if (c.Length == length && c.Contains(s.Slice(at, length)))
                {
                    code = Value(s.Slice(at, length));
                    return length;
                }
            }
        }

        int fallback = int.MaxValue;
        foreach (Codespace c in _codespace)
        {
            if (s[at] >= c.Low[0] && s[at] <= c.High[0])
            {
                fallback = c.Length;
                break;
            }
        }

        if (fallback == int.MaxValue)
        {
            foreach (Codespace c in _codespace) fallback = Math.Min(fallback, c.Length);
        }

        if (fallback == int.MaxValue) fallback = 1;
        fallback = Math.Min(fallback, remaining);
        code = Value(s.Slice(at, fallback));
        return fallback;
    }

    /// <summary>The CID for a code, or null when the CMap does not map it.</summary>
    public int? Cid(uint code)
    {
        if (_identity) return (int)Math.Min(code, int.MaxValue);
        EnsureSorted();

        // A packed Unicode CMap keeps its mappings by code point, whatever the form of its codes.
        uint key = _form == UnicodeForm.None ? code : Scalar(code);
        if (_cids.TryGetValue(key, out int cid)) return cid;
        if (Find(_cidRanges, key) is Range r) return (int)Math.Min(r.Cid + (long)(key - r.Low), int.MaxValue);
        if (_parent?.Cid(code) is int inherited) return inherited;
        return Find(_notdefRanges, key) is Range n ? n.Cid : null;
    }

    /// <summary>
    /// The code point a code of a Unicode CMap stands for, read from its bytes in the CMap's form.
    /// A code that is no valid character gets a value no mapping holds.
    /// </summary>
    private uint Scalar(uint code)
    {
        const uint invalid = uint.MaxValue;
        if (_form == UnicodeForm.Utf16 && code > 0xFFFF)
        {
            uint high = code >> 16, low = code & 0xFFFF;
            return high is >= 0xD800 and <= 0xDBFF && low is >= 0xDC00 and <= 0xDFFF ? 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00) : invalid;
        }

        if (_form != UnicodeForm.Utf8 || code <= 0x7F) return code;

        // A lead byte with 7 - length bits of the value, then continuation bytes of six bits each.
        int length = code > 0xFFFFFF ? 4 : code > 0xFFFF ? 3 : 2;
        uint value = (code >> (8 * (length - 1))) & (0x7Fu >> length);
        for (int i = length - 2; i >= 0; i--)
        {
            uint b = (code >> (8 * i)) & 0xFF;
            if ((b & 0xC0) != 0x80) return invalid;
            value = (value << 6) | (b & 0x3F);
        }

        return value;
    }

    /// <summary>The text for a code, or null when the CMap does not map it.</summary>
    public string? Unicode(uint code, int length)
    {
        switch (_form)
        {
            case UnicodeForm.Utf16:
                return length == 4 ? char.ConvertFromUtf32(0x10000 + ((int)((code >> 16) - 0xD800) << 10) + (int)((code & 0xFFFF) - 0xDC00)) : Char((int)code);
            case UnicodeForm.Utf32:
                return code <= 0x10FFFF ? Char((int)code) : null;
            case UnicodeForm.Utf8:
                byte[] bytes = new byte[length];
                for (int i = 0; i < length; i++) bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
                string text = Encoding.UTF8.GetString(bytes);
                return text == "\uFFFD" ? null : text;
        }

        EnsureSorted();
        if (_unicode.TryGetValue(code, out string? value)) return value;
        if (Find(_unicodeRanges, code) is Range r)
        {
            uint offset = code - r.Low;
            if (r.Values is not null) return offset < r.Values.Length ? r.Values[offset] : null;
            if (r.Start is null) return null;
            char[] units = (char[])r.Start.Clone();
            units[^1] = (char)(units[^1] + offset);
            return Text(units);
        }

        return _parent?.Unicode(code, length);
    }

    private void Read(ReadOnlyMemory<byte> data, Func<string, int, CMap?>? useCMap, int depth)
    {
        var parser = new PdfParser(data);
        var operands = new List<PdfObject>(4);
        string? mode = null;
        int group = 0;
        while (true)
        {
            int start = parser.Position;
            Token t;
            try
            {
                t = parser.NextToken();
            }
            catch (PdfFormatException)
            {
                // An unterminated string: nothing after it can be read.
                return;
            }

            if (t.Kind == TokenKind.EndOfInput) return;
            if (t.Kind == TokenKind.Keyword)
            {
                string keyword = Encoding.ASCII.GetString(parser.Span[t.Start..t.End].ToArray());
                switch (keyword)
                {
                    case "begincodespacerange" or "begincidchar" or "beginbfchar":
                        (mode, group) = (keyword, 2);
                        break;
                    case "begincidrange" or "beginnotdefrange" or "beginbfrange":
                        (mode, group) = (keyword, 3);
                        break;
                    case "endcodespacerange" or "endcidchar" or "endbfchar" or "endcidrange" or "endnotdefrange" or "endbfrange":
                        mode = null;
                        break;
                    case "usecmap" when operands.Count > 0 && operands[^1] is PdfName parent && depth < MaxDepth && _parent is null:
                        Inherit(useCMap?.Invoke(parent.Value, depth + 1));
                        break;
                    case "def" when operands.Count >= 2 && operands[^2] is PdfName { Value: "WMode" } && operands[^1] is PdfNumber { IsInteger: true } w:
                        WMode = w.IntegerValue == 1 ? 1 : 0;
                        _wmodeDefined = true;
                        break;
                }

                operands.Clear();
                continue;
            }

            PdfObject value;
            parser.Position = start;
            try
            {
                value = parser.ParseObject();
            }
            catch (PdfFormatException)
            {
                parser.Position = Math.Max(t.End, start + 1);
                operands.Clear();
                continue;
            }

            if (mode is null)
            {
                // Outside a section only the last operands matter, for def and usecmap.
                if (operands.Count == 4) operands.RemoveAt(0);
                operands.Add(value);
                continue;
            }

            operands.Add(value);
            if (operands.Count < group) continue;
            if (_entries < MaxEntries) Entry(mode, operands);
            operands.Clear();
        }
    }

    /// <summary>One entry of a section: a code space range, a CID or notdef mapping, or a Unicode mapping.</summary>
    private void Entry(string mode, List<PdfObject> o)
    {
        if (o[0] is not PdfString first || first.Value.Length is 0 or > 4) return;
        uint low = Value(first.Value);
        uint high = low;
        if (o.Count == 3)
        {
            if (o[1] is not PdfString last || last.Value.Length is 0 or > 4) return;
            high = Value(last.Value);
            if (high < low) return;
        }

        _entries++;
        switch (mode)
        {
            case "begincodespacerange" when o[1] is PdfString h && h.Value.Length == first.Value.Length:
                AddCodespace(first.Value, h.Value);
                break;
            case "begincidchar" when Integer(o[1]) is int cid:
                _cids[low] = cid;
                break;
            case "begincidrange" when Integer(o[2]) is int cid:
                Add(_cidRanges, new Range(low, high, cid, null, null));
                break;
            case "beginnotdefrange" when Integer(o[2]) is int cid:
                Add(_notdefRanges, new Range(low, high, cid, null, null));
                break;
            case "beginbfchar" when Destination(o[1]) is string text:
                _unicode[low] = text;
                break;
            case "beginbfrange" when o[2] is PdfString start && start.Value.Length > 0:
                char[] units = Units(start.Value);
                if (units.Length > 0) Add(_unicodeRanges, new Range(low, high, 0, units, null));
                break;
            case "beginbfrange" when o[2] is PdfArray array:
                // One destination per code; the array may not be longer than the range.
                long count = Math.Min(array.Count, (long)high - low + 1);
                count = Math.Min(count, MaxEntries - _entries);
                var values = new string?[count];
                for (int i = 0; i < count; i++) values[i] = Destination(array[i]);
                _entries += (int)count;
                Add(_unicodeRanges, new Range(low, (uint)(low + count - 1), 0, null, values));
                break;
        }
    }

    /// <summary>
    /// Takes a parent's code space, its collection, and its writing mode unless this CMap defines
    /// its own; codes this CMap does not map are looked up in the parent (usecmap).
    /// </summary>
    public void Inherit(CMap? parent)
    {
        if (parent is null) return;
        _parent = parent;
        if (!_wmodeDefined) WMode = parent.WMode;
        Ordering ??= parent.Ordering;
        foreach (Codespace c in parent._codespace)
        {
            if (_codespace.Count < MaxCodespaces) _codespace.Add(c);
        }
    }

    /// <summary>
    /// Reads a packed CMap record of <see cref="PredefinedCMaps"/> at <paramref name="at"/>, laid out
    /// as tools/make_cjk_cmaps.py describes; <paramref name="parent"/> loads a usecmap parent by
    /// its name index.
    /// </summary>
    internal static CMap Packed(ReadOnlySpan<byte> data, int at, string ordering, Func<int, CMap?> parent)
    {
        byte flags = data[at++];
        var cmap = new CMap
        {
            WMode = flags & 1,
            _wmodeDefined = true,
            Ordering = ordering,

            // Forms 1 to 4 are UCS2, UTF16, UTF8 and UTF32; a UCS2 code reads as a UTF16 one.
            _form = ((flags >> 1) & 7) switch
            {
                1 or 2 => UnicodeForm.Utf16,
                3 => UnicodeForm.Utf8,
                4 => UnicodeForm.Utf32,
                _ => UnicodeForm.None,
            },
        };
        int parentIndex = PredefinedCMaps.Varint(data, ref at) - 1;
        for (int count = data[at++]; count > 0; count--)
        {
            int length = data[at++];
            cmap.AddCodespace(data.Slice(at, length).ToArray(), data.Slice(at + length, length).ToArray());
            at += 2 * length;
        }

        uint low = 0;
        int cid = 0;
        for (int count = PredefinedCMaps.Varint(data, ref at); count > 0; count--)
        {
            low += (uint)PredefinedCMaps.Varint(data, ref at);
            uint high = low + (uint)PredefinedCMaps.Varint(data, ref at);
            cid += PredefinedCMaps.Zigzag(data, ref at);
            cmap._cidRanges.Add(new Range(low, high, cid, null, null));
            cid += (int)(high - low) + 1;
            low = high + 1;
        }

        low = 0;
        for (int count = PredefinedCMaps.Varint(data, ref at); count > 0; count--)
        {
            low += (uint)PredefinedCMaps.Varint(data, ref at);
            uint high = low + (uint)PredefinedCMaps.Varint(data, ref at);
            cmap._notdefRanges.Add(new Range(low, high, PredefinedCMaps.Varint(data, ref at), null, null));
            low = high + 1;
        }

        if (parentIndex >= 0) cmap.Inherit(parent(parentIndex));
        return cmap;
    }

    /// <summary>
    /// Sets <c>scalars[cid]</c>, where still 0, to each code of a packed Unicode CMap that maps to
    /// that CID: the lowest code wins, since the ranges are in code order. The parent is not read.
    /// </summary>
    internal void Invert(int[] scalars)
    {
        foreach (Range r in _cidRanges)
        {
            for (long k = r.Low; k <= r.High; k++)
            {
                long cid = r.Cid + (k - r.Low);
                if (cid >= 0 && cid < scalars.Length && scalars[cid] == 0) scalars[cid] = (int)k;
            }
        }
    }

    private void AddCodespace(byte[] low, byte[] high)
    {
        if (_codespace.Count < MaxCodespaces && low.Length is >= 1 and <= 4 && low.Length == high.Length) _codespace.Add(new Codespace(low, high));
    }

    private void Add(List<Range> ranges, Range range)
    {
        if (ranges.Count > 0 && ranges[^1].Low > range.Low) _sorted = false;
        ranges.Add(range);
    }

    private void EnsureSorted()
    {
        if (_sorted) return;

        // A stable sort keeps the order of definition among ranges that start at the same code.
        foreach (List<Range> list in new[] { _cidRanges, _unicodeRanges, _notdefRanges })
        {
            Range[] ordered = [.. list.Select((r, i) => (r, i)).OrderBy(e => e.r.Low).ThenBy(e => e.i).Select(e => e.r)];
            list.Clear();
            list.AddRange(ordered);
        }

        _sorted = true;
    }

    /// <summary>The range holding a code: the last starting at or before it, or one shortly before for overlaps.</summary>
    private static Range? Find(List<Range> ranges, uint code)
    {
        int lo = 0, hi = ranges.Count - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (ranges[mid].Low <= code)
            {
                at = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int i = at; i >= 0 && i > at - OverlapScan; i--)
        {
            if (code <= ranges[i].High) return ranges[i];
        }

        return null;
    }

    /// <summary>A bfchar or bfrange destination: UTF-16BE text, or a glyph name in old files.</summary>
    private static string? Destination(PdfObject value) => value switch
    {
        PdfString s => Text(Units(s.Value)),
        PdfName n => GlyphList.ToUnicode(n.Value),
        _ => null,
    };

    /// <summary>UTF-16BE code units; a lone byte is one unit.</summary>
    private static char[] Units(byte[] bytes)
    {
        if (bytes.Length == 1) return [(char)bytes[0]];
        var units = new char[bytes.Length / 2];
        for (int i = 0; i < units.Length; i++) units[i] = (char)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        return units;
    }

    /// <summary>Text from code units: null when empty or only NUL; a lone surrogate becomes U+FFFD.</summary>
    private static string? Text(char[] units)
    {
        var sb = new StringBuilder(units.Length);
        for (int i = 0; i < units.Length; i++)
        {
            char c = units[i];
            if (char.IsHighSurrogate(c) && i + 1 < units.Length && char.IsLowSurrogate(units[i + 1]))
            {
                sb.Append(c).Append(units[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('\uFFFD');
            }
            else if (c != '\0')
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? Char(int scalar) =>
        scalar is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(scalar) : null;

    private static int? Integer(PdfObject value) =>
        value is PdfNumber { IsInteger: true } n && n.IntegerValue is >= 0 and <= int.MaxValue ? (int)n.IntegerValue : null;

    private static uint Value(ReadOnlySpan<byte> bytes)
    {
        uint v = 0;
        foreach (byte b in bytes) v = (v << 8) | b;
        return v;
    }

    /// <summary>A code space range: each byte of a code lies between the bytes of Low and High.</summary>
    private sealed class Codespace(byte[] low, byte[] high)
    {
        public byte[] Low { get; } = low;

        public byte[] High { get; } = high;

        public int Length => Low.Length;

        public bool Contains(ReadOnlySpan<byte> code)
        {
            for (int i = 0; i < code.Length; i++)
            {
                if (code[i] < Low[i] || code[i] > High[i]) return false;
            }

            return true;
        }
    }

    /// <summary>Codes Low to High: to CIDs from Cid, or to text from Start (incremented) or Values.</summary>
    private sealed record Range(uint Low, uint High, int Cid, char[]? Start, string?[]? Values);
}
