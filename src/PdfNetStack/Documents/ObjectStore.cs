using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Documents;

/// <summary>
/// The indirect objects of a document by number. A document opened in memory holds every
/// reachable object here. A document opened on demand holds only what was added or replaced,
/// and reads everything else from its file when first asked for; the file keeps what it has
/// parsed, so an object read once is the same instance on every later lookup.
/// </summary>
internal sealed class ObjectStore
{
    private readonly Dictionary<int, PdfObject> _objects = new();
    private readonly PdfFile? _file;
    private readonly HashSet<int> _removed = new();
    private bool _closed;

    public ObjectStore()
    {
    }

    /// <param name="file">The file objects not held here are read from.</param>
    public ObjectStore(PdfFile file) => _file = file;

    /// <summary>Objects added or replaced since the document was opened, and, in memory, every loaded object.</summary>
    public IEnumerable<int> Held => _objects.Keys;

    public int Count => _objects.Count;

    /// <summary>True for a document read on demand, whose objects are read from its file when first asked for.</summary>
    public bool ReadsFile => _file is not null;

    public IEnumerable<int> Keys => _objects.Keys;

    public PdfObject this[int number]
    {
        get => TryGetValue(number, out PdfObject? value) ? value : throw new KeyNotFoundException($"Object {number} does not exist.");
        set
        {
            ThrowIfClosed();
            _objects[number] = value;
            _removed.Remove(number);
        }
    }

    public bool TryGetValue(int number, out PdfObject value)
    {
        ThrowIfClosed();
        if (_objects.TryGetValue(number, out PdfObject? held))
        {
            value = held;
            return true;
        }

        if (_file is not null && !_removed.Contains(number) && _file.Exists(number) && _file.GetObject(number) is not PdfNull and var read)
        {
            value = read;
            return true;
        }

        value = PdfNull.Instance;
        return false;
    }

    public bool ContainsKey(int number) => TryGetValue(number, out _);

    /// <summary>The held objects; objects still only in the file are not listed.</summary>
    public Dictionary<int, PdfObject>.Enumerator GetEnumerator() => _objects.GetEnumerator();

    public bool Remove(int number)
    {
        ThrowIfClosed();
        bool existed = ContainsKey(number);
        _objects.Remove(number);
        if (_file is not null) _removed.Add(number);
        return existed;
    }

    /// <summary>Ends the store's use when its document is disposed: every later lookup throws.</summary>
    public void Close()
    {
        _closed = true;
        _objects.Clear();
        _removed.Clear();
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(PdfDocument), "The document was disposed; its objects can no longer be read.");
    }
}
