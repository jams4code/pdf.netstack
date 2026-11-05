namespace PdfNetStack.Fonts;

/// <summary>
/// The instances made of one variable font program, by location, shared by the font as loaded and
/// all its instances. Instances are held weakly: one stays while a document or the application
/// holds it, so asking for its location again gives the same font, and memory does not grow with
/// every location a long running process was ever asked for (a shared font collection converting
/// untrusted HTML, say). Entries whose font was collected are dropped as the table grows. Thread
/// safe.
/// </summary>
internal sealed class FontInstances
{
    private const int MinPrune = 16;

    private readonly Dictionary<string, WeakReference<PdfOpenTypeFont>> _fonts = new(StringComparer.Ordinal);
    private int _pruneAt = MinPrune;

    /// <summary>The live instance at <paramref name="key"/>, or the one <paramref name="create"/> makes, which is then kept.</summary>
    public PdfOpenTypeFont GetOrAdd(string key, Func<PdfOpenTypeFont> create)
    {
        lock (_fonts)
        {
            if (_fonts.TryGetValue(key, out WeakReference<PdfOpenTypeFont>? weak) && weak.TryGetTarget(out PdfOpenTypeFont? live)) return live;
        }

        // Made outside the lock: instancing a font takes a while, and other locations need not wait.
        PdfOpenTypeFont made = create();
        lock (_fonts)
        {
            if (_fonts.TryGetValue(key, out WeakReference<PdfOpenTypeFont>? weak) && weak.TryGetTarget(out PdfOpenTypeFont? live)) return live;
            Set(key, made);
            return made;
        }
    }

    /// <summary>Keeps <paramref name="font"/> as the instance at <paramref name="key"/>.</summary>
    public void Add(string key, PdfOpenTypeFont font)
    {
        lock (_fonts) Set(key, font);
    }

    /// <summary>How many entries the table holds, collected ones included; for the tests.</summary>
    internal int Count
    {
        get
        {
            lock (_fonts) return _fonts.Count;
        }
    }

    private void Set(string key, PdfOpenTypeFont font)
    {
        if (_fonts.Count >= _pruneAt)
        {
            foreach (string dead in _fonts.Where(f => !f.Value.TryGetTarget(out _)).Select(f => f.Key).ToList()) _fonts.Remove(dead);
            _pruneAt = Math.Max(MinPrune, 2 * _fonts.Count);
        }

        _fonts[key] = new WeakReference<PdfOpenTypeFont>(font);
    }
}
