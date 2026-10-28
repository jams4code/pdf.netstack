using System.Text;

namespace PdfNetStack.Objects;

/// <summary>Base type of the in-memory PDF object model.</summary>
internal abstract class PdfObject;

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
}

internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public static PdfBoolean Get(bool value) => value ? True : False;
}

/// <summary>
/// Integers and reals share one type. The value is kept as canonical ASCII text so that
/// reals survive a round trip exactly, with no floating point conversion involved.
/// </summary>
internal sealed class PdfNumber : PdfObject
{
    private PdfNumber(byte[] text, bool isInteger, long integerValue)
    {
        Text = text;
        IsInteger = isInteger;
        IntegerValue = integerValue;
    }

    /// <summary>Canonical ASCII representation, ready to serialize.</summary>
    public byte[] Text { get; }

    public bool IsInteger { get; }

    /// <summary>Valid only when <see cref="IsInteger"/> is true.</summary>
    public long IntegerValue { get; }

    public static PdfNumber FromInteger(long value) =>
        new(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), true, value);

    /// <summary>
    /// Parses a numeric token and canonicalizes it: no leading plus, no redundant zeros,
    /// no trailing decimal point. The numeric value never changes. Tokens that do not look
    /// like a well formed number are kept verbatim.
    /// </summary>
    public static PdfNumber Parse(ReadOnlySpan<byte> raw)
    {
        int i = 0;
        bool negative = false;
        if (i < raw.Length && (raw[i] == (byte)'+' || raw[i] == (byte)'-'))
        {
            negative = raw[i] == (byte)'-';
            i++;
        }

        int intStart = i;
        while (i < raw.Length && IsDigit(raw[i])) i++;
        int intEnd = i;
        int fracStart = -1, fracEnd = -1;
        if (i < raw.Length && raw[i] == (byte)'.')
        {
            i++;
            fracStart = i;
            while (i < raw.Length && IsDigit(raw[i])) i++;
            fracEnd = i;
        }

        bool wellFormed = i == raw.Length && (intEnd > intStart || fracEnd > fracStart);
        if (!wellFormed)
        {
            return new PdfNumber(raw.ToArray(), false, 0);
        }

        ReadOnlySpan<byte> intDigits = raw[intStart..intEnd];
        while (!intDigits.IsEmpty && intDigits[0] == (byte)'0') intDigits = intDigits[1..];
        ReadOnlySpan<byte> fracDigits = fracStart >= 0 ? raw[fracStart..fracEnd] : default;
        while (!fracDigits.IsEmpty && fracDigits[^1] == (byte)'0') fracDigits = fracDigits[..^1];

        if (intDigits.IsEmpty && fracDigits.IsEmpty)
        {
            return new PdfNumber("0"u8.ToArray(), true, 0);
        }

        var text = new byte[(negative ? 1 : 0) + intDigits.Length + (fracDigits.IsEmpty ? 0 : fracDigits.Length + 1)];
        int p = 0;
        if (negative) text[p++] = (byte)'-';
        intDigits.CopyTo(text.AsSpan(p));
        p += intDigits.Length;
        if (!fracDigits.IsEmpty)
        {
            text[p++] = (byte)'.';
            fracDigits.CopyTo(text.AsSpan(p));
        }

        if (fracDigits.IsEmpty && intDigits.Length <= 18)
        {
            long value = 0;
            foreach (byte b in intDigits) value = value * 10 + (b - '0');
            return new PdfNumber(text, true, negative ? -value : value);
        }

        return new PdfNumber(text, false, 0);
    }

    /// <summary>Approximate value for geometry and policy decisions. Never used for output.</summary>
    public double ToDouble() =>
        IsInteger
            ? IntegerValue
            : double.TryParse(Encoding.ASCII.GetString(Text), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public override string ToString() => Encoding.ASCII.GetString(Text);
}

/// <summary>A PDF name. Bytes are mapped one to one onto chars (Latin-1) after #xx unescaping.</summary>
internal sealed class PdfName : PdfObject
{
    public PdfName(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => "/" + Value;
}

internal sealed class PdfString : PdfObject
{
    public PdfString(byte[] value, bool wasHex = false)
    {
        Value = value;
        WasHex = wasHex;
    }

    public byte[] Value { get; }

    /// <summary>Source syntax hint only. Hex and literal strings are semantically identical.</summary>
    public bool WasHex { get; }
}

internal sealed class PdfArray : PdfObject
{
    public PdfArray() => Items = [];

    public PdfArray(List<PdfObject> items) => Items = items;

    public List<PdfObject> Items { get; }

    public int Count => Items.Count;

    public PdfObject this[int index]
    {
        get => Items[index];
        set => Items[index] = value;
    }
}

/// <summary>
/// Insertion ordered dictionary. Most PDF dictionaries hold a handful of keys, so lookups are
/// linear until the dictionary grows, then switch to a hash index.
/// </summary>
internal sealed class PdfDictionary : PdfObject
{
    private const int IndexThreshold = 12;
    private readonly List<string> _keys;
    private readonly List<PdfObject> _values;
    private Dictionary<string, int>? _index;

    public PdfDictionary(int capacity = 4)
    {
        _keys = new List<string>(capacity);
        _values = new List<PdfObject>(capacity);
    }

    public int Count => _keys.Count;

    public IReadOnlyList<string> Keys => _keys;

    public PdfObject? this[string key]
    {
        get => TryGetValue(key, out PdfObject? value) ? value : null;
        set
        {
            if (value is null) Remove(key);
            else Set(key, value);
        }
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public bool TryGetValue(string key, out PdfObject value)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            value = null!;
            return false;
        }

        value = _values[i];
        return true;
    }

    public void Set(string key, PdfObject value)
    {
        int i = IndexOf(key);
        if (i >= 0)
        {
            _values[i] = value;
            return;
        }

        _keys.Add(key);
        _values.Add(value);
        if (_index is not null) _index[key] = _keys.Count - 1;
        else if (_keys.Count > IndexThreshold) BuildIndex();
    }

    public bool Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _keys.RemoveAt(i);
        _values.RemoveAt(i);
        if (_index is not null) BuildIndex();
        return true;
    }

    public KeyValuePair<string, PdfObject> EntryAt(int i) => new(_keys[i], _values[i]);

    public void SetValueAt(int i, PdfObject value) => _values[i] = value;

    public PdfObject ValueAt(int i) => _values[i];

    public string? GetName(string key) => this[key] is PdfName n ? n.Value : null;

    private int IndexOf(string key)
    {
        if (_index is not null) return _index.TryGetValue(key, out int i) ? i : -1;
        for (int i = 0; i < _keys.Count; i++)
        {
            if (string.Equals(_keys[i], key, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private void BuildIndex()
    {
        _index = new Dictionary<string, int>(_keys.Count * 2, StringComparer.Ordinal);
        for (int i = 0; i < _keys.Count; i++) _index[_keys[i]] = i;
    }
}

internal sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary;
        Data = data;
    }

    public PdfDictionary Dictionary { get; }

    /// <summary>Encoded stream bytes, exactly as stored between stream and endstream.</summary>
    public ReadOnlyMemory<byte> Data { get; set; }
}

internal sealed class PdfReference : PdfObject
{
    public PdfReference(int number, int generation)
    {
        Number = number;
        Generation = generation;
    }

    public int Number { get; }

    public int Generation { get; }

    public override string ToString() => $"{Number} {Generation} R";
}
