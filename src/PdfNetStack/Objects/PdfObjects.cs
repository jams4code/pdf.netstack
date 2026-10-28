using System.Collections;
using System.Text;
using PdfNetStack.Documents;

namespace PdfNetStack.Objects;

/// <summary>
/// Base type of the PDF object model (ISO 32000-1, 7.3): the values a PDF file is made of. Use it
/// to read or change anything the high level API does not cover, through
/// <see cref="PdfDocument.Catalog"/>, <see cref="PdfPage.Dictionary"/> and
/// <see cref="PdfDocument.Resolve(PdfObject?)"/>.
/// </summary>
public abstract class PdfObject
{
    private protected PdfObject()
    {
    }
}

/// <summary>The PDF null object.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The only instance.</summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    /// <inheritdoc/>
    public override string ToString() => "null";
}

/// <summary>A PDF boolean.</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The true value.</summary>
    public static readonly PdfBoolean True = new(true);

    /// <summary>The false value.</summary>
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <summary>Returns the shared instance for <paramref name="value"/>.</summary>
    public static PdfBoolean Get(bool value) => value ? True : False;

    /// <inheritdoc/>
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>
/// A PDF integer or real. The value is kept as canonical ASCII text so that reals read from a
/// file survive a round trip exactly, with no floating point conversion involved.
/// </summary>
public sealed class PdfNumber : PdfObject
{
    private PdfNumber(byte[] text, bool isInteger, long integerValue)
    {
        Text = text;
        IsInteger = isInteger;
        IntegerValue = integerValue;
    }

    /// <summary>Canonical ASCII representation, ready to serialize.</summary>
    internal byte[] Text { get; }

    /// <summary>True for integers, false for reals.</summary>
    public bool IsInteger { get; }

    /// <summary>The integer value. Valid only when <see cref="IsInteger"/> is true.</summary>
    public long IntegerValue { get; }

    /// <summary>Creates an integer.</summary>
    public static PdfNumber FromInteger(long value) =>
        new(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), true, value);

    /// <summary>
    /// Creates a real, rounded to 6 decimal places and written without an exponent, as PDF
    /// requires. Whole values become integers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static PdfNumber FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "PDF numbers must be finite.");
        string text = Math.Round(value, 6).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return Parse(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// Parses a numeric token and canonicalizes it: no leading plus, no redundant zeros,
    /// no trailing decimal point. The numeric value never changes. Tokens that do not look
    /// like a well formed number are kept verbatim.
    /// </summary>
    internal static PdfNumber Parse(ReadOnlySpan<byte> raw)
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

    /// <summary>The value as a double. Exact for integers up to 2^53.</summary>
    public double ToDouble() =>
        IsInteger
            ? IntegerValue
            : double.TryParse(Encoding.ASCII.GetString(Text), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>The number as written in the file.</summary>
    public override string ToString() => Encoding.ASCII.GetString(Text);
}

/// <summary>A PDF name such as /Type. Bytes map one to one onto chars (Latin-1) after #xx unescaping.</summary>
public sealed class PdfName : PdfObject
{
    /// <summary>Creates a name.</summary>
    /// <param name="value">The name without its leading slash, for example "Page".</param>
    public PdfName(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The name without its leading slash.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;
}

/// <summary>A PDF string: a sequence of bytes, which may hold text or binary data.</summary>
public sealed class PdfString : PdfObject
{
    internal PdfString(byte[] value, bool wasHex = false)
    {
        Value = value;
        WasHex = wasHex;
    }

    /// <summary>Creates a string holding a copy of <paramref name="bytes"/>.</summary>
    public PdfString(ReadOnlySpan<byte> bytes)
        : this(bytes.ToArray())
    {
    }

    internal byte[] Value { get; }

    /// <summary>Source syntax hint only. Hex and literal strings are semantically identical.</summary>
    internal bool WasHex { get; }

    /// <summary>The raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => Value;

    /// <summary>
    /// Creates a text string (ISO 32000-1, 7.9.2.2): PDFDocEncoding when every character fits,
    /// otherwise UTF-16 with a byte order mark.
    /// </summary>
    public static PdfString FromText(string text) => PdfText.Encode(text ?? throw new ArgumentNullException(nameof(text)));

    /// <summary>Decodes the bytes as a text string: UTF-16 or UTF-8 with a byte order mark, otherwise PDFDocEncoding.</summary>
    public string ToText() => PdfText.Decode(Value);

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>A PDF array.</summary>
public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    /// <summary>Creates an empty array.</summary>
    public PdfArray() => Items = [];

    internal PdfArray(List<PdfObject> items) => Items = items;

    internal List<PdfObject> Items { get; }

    /// <summary>Number of items.</summary>
    public int Count => Items.Count;

    /// <summary>The item at <paramref name="index"/>. Items may be references; resolve them through the document.</summary>
    public PdfObject this[int index]
    {
        get => Items[index];
        set => Items[index] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Creates an array of the given items.</summary>
    public static PdfArray Of(params PdfObject[] items)
    {
        Compat.ThrowIfNull(items, nameof(items));
        if (Array.IndexOf(items, null) >= 0) throw new ArgumentException("Items cannot be null; use PdfNull.Instance.", nameof(items));
        return new PdfArray([.. items]);
    }

    /// <summary>Creates an array of numbers.</summary>
    public static PdfArray Of(params double[] numbers)
    {
        Compat.ThrowIfNull(numbers, nameof(numbers));
        var items = new List<PdfObject>(numbers.Length);
        foreach (double n in numbers) items.Add(PdfNumber.FromDouble(n));
        return new PdfArray(items);
    }

    /// <summary>Appends an item.</summary>
    public void Add(PdfObject item) => Items.Add(item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>Inserts an item at <paramref name="index"/>.</summary>
    public void Insert(int index, PdfObject item) => Items.Insert(index, item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>Removes the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index) => Items.RemoveAt(index);

    /// <summary>Removes every item.</summary>
    public void Clear() => Items.Clear();

    /// <inheritdoc/>
    public IEnumerator<PdfObject> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A PDF dictionary with keys in insertion order. Keys are names without the leading slash.
/// Most PDF dictionaries hold a handful of keys, so lookups are linear until the dictionary
/// grows, then switch to a hash index.
/// </summary>
public sealed class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<string, PdfObject>>
{
    private const int IndexThreshold = 12;
    private readonly List<string> _keys;
    private readonly List<PdfObject> _values;
    private Dictionary<string, int>? _index;

    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary()
        : this(4)
    {
    }

    internal PdfDictionary(int capacity)
    {
        _keys = new List<string>(capacity);
        _values = new List<PdfObject>(capacity);
    }

    /// <summary>Number of entries.</summary>
    public int Count => _keys.Count;

    /// <summary>The keys, in insertion order.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>
    /// The value for <paramref name="key"/>, or null when absent. The value may be a reference;
    /// resolve it through the document. Setting null removes the entry.
    /// </summary>
    public PdfObject? this[string key]
    {
        get => TryGetValue(key, out PdfObject? value) ? value : null;
        set
        {
            if (value is null) Remove(key);
            else Set(key, value);
        }
    }

    /// <summary>True when the key is present.</summary>
    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <summary>Gets the value for <paramref name="key"/> when present.</summary>
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

    /// <summary>Adds or replaces an entry. Replacing keeps the key's position.</summary>
    public void Set(string key, PdfObject value)
    {
        Compat.ThrowIfNull(key, nameof(key));
        Compat.ThrowIfNull(value, nameof(value));
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

    /// <summary>Removes an entry; returns false when it was absent.</summary>
    public bool Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _keys.RemoveAt(i);
        _values.RemoveAt(i);
        if (_index is not null) BuildIndex();
        return true;
    }

    /// <summary>The value of <paramref name="key"/> when it is a direct name, without the slash.</summary>
    public string? GetName(string key) => this[key] is PdfName n ? n.Value : null;

    internal KeyValuePair<string, PdfObject> EntryAt(int i) => new(_keys[i], _values[i]);

    internal void SetValueAt(int i, PdfObject value) => _values[i] = value;

    internal PdfObject ValueAt(int i) => _values[i];

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, PdfObject>> GetEnumerator()
    {
        for (int i = 0; i < _keys.Count; i++) yield return new KeyValuePair<string, PdfObject>(_keys[i], _values[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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

/// <summary>
/// A PDF stream: a dictionary plus encoded data. Streams are always indirect objects; add new
/// ones with <see cref="PdfDocument.AddObject(PdfObject)"/> and refer to the returned reference.
/// </summary>
public sealed class PdfStream : PdfObject
{
    /// <summary>Creates a stream from data already encoded as the dictionary's /Filter says.</summary>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        Data = data;
    }

    /// <summary>
    /// Creates a stream from plain data, Flate compressed unless <paramref name="compress"/> is
    /// false. /Filter and /DecodeParms of <paramref name="dictionary"/> are replaced to match.
    /// </summary>
    public static PdfStream FromData(ReadOnlySpan<byte> data, PdfDictionary? dictionary = null, bool compress = true)
    {
        dictionary ??= new PdfDictionary();
        dictionary.Remove("DecodeParms");
        dictionary.Remove("DL");
        if (!compress)
        {
            dictionary.Remove("Filter");
            return new PdfStream(dictionary, data.ToArray());
        }

        dictionary.Set("Filter", new PdfName("FlateDecode"));
        return new PdfStream(dictionary, Filters.StreamCodec.Deflate(data, System.IO.Compression.CompressionLevel.Optimal));
    }

    /// <summary>The stream dictionary. /Length is written on save and need not be kept current.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Encoded stream bytes, exactly as stored between stream and endstream.</summary>
    public ReadOnlyMemory<byte> Data { get; set; }
}

/// <summary>
/// A reference to an indirect object. References belong to one document: obtain them from
/// <see cref="PdfDocument.AddObject(PdfObject)"/> or from that document's objects, and resolve
/// them with <see cref="PdfDocument.Resolve(PdfObject?)"/>.
/// </summary>
public sealed class PdfReference : PdfObject
{
    internal PdfReference(int number, int generation)
    {
        Number = number;
        Generation = generation;
    }

    /// <summary>Object number.</summary>
    public int Number { get; }

    /// <summary>Generation number.</summary>
    public int Generation { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Number} {Generation} R";
}
