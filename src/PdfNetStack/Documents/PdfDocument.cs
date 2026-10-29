using System.IO.Compression;
using System.Security.Cryptography;
using PdfNetStack.Documents;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Security;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>Options for <see cref="PdfDocument.Save(Stream, PdfSaveOptions?)"/>.</summary>
public sealed record PdfSaveOptions
{
    /// <summary>Shared default instance.</summary>
    public static PdfSaveOptions Default { get; } = new();

    /// <summary>Pack objects into compressed object streams (PDF 1.5). Default true.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>
    /// Run the lossless optimizer on the saved file: stream recompression and deduplication,
    /// with verification. Merged documents often share fonts and images, so this pays off. Default true.
    /// </summary>
    public bool Optimize { get; init; } = true;

    /// <summary>Flate effort for streams the writer compresses. Default <see cref="CompressionEffort.Maximum"/>.</summary>
    public CompressionEffort Compression { get; init; } = CompressionEffort.Maximum;
}

/// <summary>
/// A PDF document opened for editing or created from scratch. Pages can be added, removed,
/// reordered, rotated and imported from other documents; metadata and bookmarks can be read
/// and changed. Saving writes a new, single revision file.
/// </summary>
/// <remarks>
/// Editing a digitally signed document invalidates its signatures. Content of imported pages, including fonts, images, annotations and their
/// appearance streams, is copied; resources shared by several imported pages are copied once.
/// </remarks>
public sealed class PdfDocument
{
    private readonly HashSet<int> _pageTreeNodes = new();
    private readonly HashSet<int> _everPages = new();
    private readonly Dictionary<PdfDocument, PageImporter> _importers = new();
    private readonly List<Drawing.PdfGraphics> _openGraphics = new();
    private readonly Dictionary<Fonts.PdfFont, Fonts.FontResource> _fonts = new();
    private readonly Dictionary<Drawing.PdfImage, PdfReference> _images = new();
    private PdfAcroForm? _form;
    private PdfOutlineCollection? _outlines;

    private PdfDocument(string version, StandardSecurity? security = null)
    {
        Version = version;
        Pages = new PdfPageCollection(this);
        Info = new PdfDocumentInfo(this);
        Security = new PdfSecurity(this, security);
    }

    /// <summary>Encryption of the opened document and of the saved output.</summary>
    public PdfSecurity Security { get; }

    internal Dictionary<int, PdfObject> Objects { get; } = new();

    internal int NextNumber { get; set; } = 1;

    /// <summary>
    /// The document catalog (ISO 32000-1, 7.7.2), root of the object graph. Use it with
    /// <see cref="Resolve(PdfObject?)"/> and <see cref="AddObject(PdfObject)"/> for features the
    /// high level API does not cover, such as viewer preferences or page labels.
    /// </summary>
    public PdfDictionary Catalog { get; private set; } = new();

    /// <summary>Per stream decode ceiling for <see cref="GetStreamData(PdfStream)"/>.</summary>
    internal long DecodeLimit { get; private set; } = 256L << 20;

    internal int CatalogNumber { get; private set; }

    internal int? InfoNumber { get; set; }

    internal PdfObject? Id { get; private set; }

    /// <summary>The interactive form: fields, values, flattening. Empty when the document has none.</summary>
    public PdfAcroForm Form => _form ??= new PdfAcroForm(this);

    /// <summary>PDF version from the file header, for example "1.7".</summary>
    public string Version { get; private set; }

    /// <summary>The pages, in order.</summary>
    public PdfPageCollection Pages { get; }

    /// <summary>Document information: title, author, dates and custom entries.</summary>
    public PdfDocumentInfo Info { get; }

    /// <summary>The bookmark tree. Loaded on first access.</summary>
    public PdfOutlineCollection Outlines => _outlines ??= PdfOutlineCollection.Load(this);

    /// <summary>Creates an empty document. Add pages before saving.</summary>
    public static PdfDocument Create()
    {
        var doc = new PdfDocument("1.7");
        doc.Catalog = new PdfDictionary();
        doc.Catalog.Set("Type", new PdfName("Catalog"));
        doc.CatalogNumber = doc.Add(doc.Catalog);
        return doc;
    }

    /// <summary>Opens a document from a file.</summary>
    /// <param name="path">File to open.</param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <exception cref="PdfFormatException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    public static PdfDocument Open(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    /// <summary>Opens a document from a stream, read to its end.</summary>
    public static PdfDocument Open(Stream stream, string? password = null)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Open(buffer.ToArray(), password);
    }

    /// <summary>
    /// Opens a document from memory. Stream data is referenced, not copied, so the buffer must
    /// not change while the document is in use.
    /// </summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> data, string? password = null, long maxDecodedStreamBytes = 256L << 20)
    {
        PdfFile file = PdfFile.Open(data, maxDecodedStreamBytes);
        if (file.Trailer["Encrypt"] is not null and not PdfNull) file.Unlock(password);

        var doc = new PdfDocument(file.HeaderVersion, file.Security) { DecodeLimit = maxDecodedStreamBytes };
        ObjectGraph graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, CancellationToken.None);
        foreach (int number in graph.Order) doc.Objects[number] = file.GetObject(number);
        doc.NextNumber = (doc.Objects.Count == 0 ? 0 : doc.Objects.Keys.Max()) + 1;

        if (file.Trailer["Root"] is not PdfReference root || doc.Resolve(root) is not PdfDictionary catalog)
        {
            throw new PdfFormatException("Document catalog is missing or not a dictionary.");
        }

        doc.Catalog = catalog;
        doc.CatalogNumber = root.Number;
        if (file.Trailer["Info"] is PdfReference info && doc.Resolve(info) is PdfDictionary) doc.InfoNumber = info.Number;
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) doc.Id = id;
        doc.LoadPageTree();
        return doc;
    }

    /// <summary>Creates a document holding all pages of the given documents, in order.</summary>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents)
    {
        Compat.ThrowIfNull(documents, nameof(documents));
        PdfDocument result = Create();
        foreach (PdfDocument source in documents) result.Pages.Import(source.Pages);
        return result;
    }

    /// <summary>Returns one new document per page.</summary>
    public IEnumerable<PdfDocument> Split()
    {
        foreach (PdfPage page in Pages.ToList())
        {
            PdfDocument single = Create();
            single.Pages.Import([page]);
            yield return single;
        }
    }

    /// <summary>Saves to a file, replacing it atomically.</summary>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                Save(stream, options);
            }

            Compat.MoveOverwrite(temp, full);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Saves the document as a new single revision PDF.</summary>
    public void Save(Stream stream, PdfSaveOptions? options = null)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        stream.Write(ToArray(options));
    }

    /// <summary>Saves the document to a byte array.</summary>
    public byte[] ToArray(PdfSaveOptions? options = null)
    {
        options ??= PdfSaveOptions.Default;

        // Drawing still open is written now, so a missing Dispose does not lose it.
        foreach (Drawing.PdfGraphics g in _openGraphics.ToArray()) g.Dispose();

        // Font dictionaries were reserved on first use; widths, encodings and subsets depend on
        // everything drawn, so they are written now.
        foreach (Fonts.FontResource font in _fonts.Values) font.Finish();
        if (Pages.Count == 0) throw new InvalidOperationException("A PDF must have at least one page.");
        Security.RequireOwner();

        CompressionLevel level = options.Compression switch
        {
            CompressionEffort.Fast => CompressionLevel.Fastest,
            CompressionEffort.Balanced => CompressionLevel.Optimal,
            _ => Compat.MaximumCompression,
        };

        // Rebuild the page tree as one flat node. Old intermediate nodes and removed pages are
        // dead: references to them, from destinations or annotations, are written as null.
        var pagesNode = new PdfDictionary();
        pagesNode.Set("Type", new PdfName("Pages"));
        int pagesNumber = Add(pagesNode);
        var kids = new PdfArray();
        foreach (PdfPage page in Pages)
        {
            page.Dictionary.Set("Parent", new PdfReference(pagesNumber, 0));
            kids.Items.Add(new PdfReference(page.Number, 0));
        }

        pagesNode.Set("Kids", kids);
        pagesNode.Set("Count", PdfNumber.FromInteger(Pages.Count));
        Catalog.Set("Pages", new PdfReference(pagesNumber, 0));
        _outlines?.WriteTo(this);

        var dead = new HashSet<int>(_pageTreeNodes);
        var live = new HashSet<int>(Pages.Select(p => p.Number));
        foreach (int n in _everPages)
        {
            if (!live.Contains(n)) dead.Add(n);
        }

        dead.Remove(pagesNumber);

        var trailer = new PdfDictionary();
        trailer.Set("Root", new PdfReference(CatalogNumber, 0));
        if (InfoNumber is { } info && Objects.ContainsKey(info)) trailer.Set("Info", new PdfReference(info, 0));
        Id ??= NewId();
        trailer.Set("ID", Id);

        // Reachability over the object store; unreachable objects are simply not written.
        var order = new List<int>();
        var index = new Dictionary<int, int>();
        var queue = new Queue<int>();
        void Visit(PdfObject? o, int depth)
        {
            switch (o)
            {
                case PdfReference r when !dead.Contains(r.Number) && Objects.ContainsKey(r.Number) && !index.ContainsKey(r.Number):
                    index[r.Number] = order.Count + 1;
                    order.Add(r.Number);
                    queue.Enqueue(r.Number);
                    break;
                case PdfArray a when depth < PdfParser.MaxNesting:
                    foreach (PdfObject item in a.Items) Visit(item, depth + 1);
                    break;
                case PdfDictionary d when depth < PdfParser.MaxNesting:
                    for (int i = 0; i < d.Count; i++) Visit(d.ValueAt(i), depth + 1);
                    break;
                case PdfStream s:
                    Visit(s.Dictionary, depth + 1);
                    break;
            }
        }

        Visit(trailer, 0);
        while (queue.Count > 0) Visit(Objects[queue.Dequeue()], 0);

        // Pages node is rebuilt on every save; drop it so a second save starts clean.
        var objects = order.Select(n => Objects[n]).ToList();
        int Map(PdfReference r) => index.TryGetValue(r.Number, out int n) ? n : -1;
        bool objectStreams = options.UseObjectStreams;
        string version = objectStreams && string.CompareOrdinal(Version, "1.5") < 0 ? "1.5" : Version;
        byte[] written = PdfDocumentWriter.Write(objects, trailer, Map, new WriterSettings(objectStreams, 200, version, level)).ToArray();
        Objects.Remove(pagesNumber);

        if (options.Optimize)
        {
            var optimizerOptions = new PdfOptimizerOptions
            {
                MinSavingPercent = 0,
                Compression = options.Compression,
                UseObjectStreams = objectStreams,
            };
            (written, _) = PdfOptimizer.Optimize(written, optimizerOptions);
        }

        return Security.Target is { } encryption ? Encrypted(written, encryption, objectStreams, level) : written;
    }

    /// <summary>
    /// Encryption is the last step: the finished, optimized and verified plain file is re-read and
    /// written once more with every string and stream encrypted.
    /// </summary>
    private static byte[] Encrypted(byte[] plain, StandardSecurity encryption, bool objectStreams, CompressionLevel level)
    {
        PdfFile file = PdfFile.Open(plain, 1L << 30);
        ObjectGraph graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, CancellationToken.None);
        var objects = graph.Order.Select(file.GetObject).ToList();
        var index = new Dictionary<int, int>();
        for (int i = 0; i < graph.Order.Count; i++) index[graph.Order[i]] = i + 1;

        objects.Add(encryption.Dictionary);
        int encryptNumber = objects.Count;
        const int encryptMarker = int.MaxValue;
        int Map(PdfReference r) => r.Number == encryptMarker ? encryptNumber : index.TryGetValue(r.Number, out int n) ? n : -1;

        string version = file.HeaderVersion;
        string minimum = encryption.StreamMethod switch
        {
            CryptMethod.AesV3 => "1.7",
            CryptMethod.AesV2 => "1.6",
            _ => "1.4",
        };
        if (string.CompareOrdinal(version, minimum) < 0) version = minimum;
        if (objectStreams && string.CompareOrdinal(version, "1.5") < 0) version = "1.5";

        // AES-256 belongs to PDF 2.0; a 1.7 file announces it through Adobe extension level 8.
        if (encryption.Revision >= 5 && string.CompareOrdinal(version, "2.0") < 0 && file.ResolveDictionary(file.Trailer["Root"]) is { } catalog)
        {
            var adbe = new PdfDictionary();
            adbe.Set("BaseVersion", new PdfName("1.7"));
            adbe.Set("ExtensionLevel", PdfNumber.FromInteger(8));
            PdfDictionary extensions = file.ResolveDictionary(catalog["Extensions"]) is { } existing ? Clone(existing) : new PdfDictionary();
            extensions.Set("ADBE", adbe);
            catalog.Set("Extensions", extensions);
        }

        var trailer = new PdfDictionary();
        if (file.Trailer["Root"] is { } root) trailer.Set("Root", root);
        if (file.Trailer["Info"] is { } info) trailer.Set("Info", info);
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);
        trailer.Set("Encrypt", new PdfReference(encryptMarker, 0));

        var settings = new WriterSettings(objectStreams, 200, version, level)
        {
            Encryption = encryption,
            EncryptionDictionaryNumber = encryptNumber,
        };
        return PdfDocumentWriter.Write(objects, trailer, Map, settings).ToArray();
    }

    /// <summary>The first /ID entry, creating the ID when the document has none. Key derivation for AES-128 needs it.</summary>
    internal byte[] EnsureId()
    {
        Id ??= NewId();
        return Id is PdfArray { Count: > 0 } a && a[0] is PdfString s ? s.Value : [];
    }

    private static PdfArray NewId()
    {
        byte[] id = Compat.RandomBytes(16);
        return new PdfArray([new PdfString(id, wasHex: true), new PdfString(id, wasHex: true)]);
    }

    /// <summary>
    /// Adds <paramref name="obj"/> as a new indirect object and returns a reference to it. Streams
    /// must be added this way. The object is written on save only if something references it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is a reference.</exception>
    public PdfReference AddObject(PdfObject obj)
    {
        Compat.ThrowIfNull(obj, nameof(obj));
        if (obj is PdfReference) throw new ArgumentException("A reference cannot be an indirect object.", nameof(obj));
        return new PdfReference(Add(obj), 0);
    }

    /// <summary>
    /// Decodes a stream's data: Flate, LZW, ASCIIHex, ASCII85 and RunLength filters and PNG or TIFF
    /// predictors are undone. Image codecs (DCTDecode, JPXDecode, JBIG2Decode, CCITTFaxDecode) are
    /// left in place, so an image comes back as its JPEG or JPEG 2000 file.
    /// </summary>
    /// <exception cref="PdfFormatException">The data is damaged or exceeds the decode limit.</exception>
    public byte[] GetStreamData(PdfStream stream)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        try
        {
            List<Filters.FilterSpec> filters = Filters.StreamCodec.GetFilters(stream.Dictionary, Resolve);
            using Filters.DecodedStream decoded = Filters.StreamCodec.Decode(stream.Data, filters, Filters.DecodeDepth.Full, DecodeLimit, Resolve);
            return decoded.Data.ToArray();
        }
        catch (Filters.StreamDecodeException e)
        {
            throw new PdfFormatException("Stream data cannot be decoded: " + e.Message, e);
        }
    }

    internal int Add(PdfObject obj)
    {
        int number = NextNumber++;
        Objects[number] = obj;
        return number;
    }

    internal void RegisterGraphics(Drawing.PdfGraphics graphics) => _openGraphics.Add(graphics);

    internal void UnregisterGraphics(Drawing.PdfGraphics graphics) => _openGraphics.Remove(graphics);

    /// <summary>The image XObject for <paramref name="image"/> in this document, written on first use.</summary>
    internal PdfReference GetImageReference(Drawing.PdfImage image)
    {
        if (!_images.TryGetValue(image, out PdfReference? reference))
        {
            reference = image.AddTo(this);
            _images[image] = reference;
        }

        return reference;
    }

    /// <summary>The document's resource for <paramref name="font"/>, created on first use.</summary>
    internal Fonts.FontResource GetFontResource(Fonts.PdfFont font)
    {
        if (!_fonts.TryGetValue(font, out Fonts.FontResource? resource))
        {
            resource = font.CreateResource(this);
            _fonts[font] = resource;
        }

        return resource;
    }

    internal void RegisterPage(int number) => _everPages.Add(number);

    internal bool IsPage(int number) => _everPages.Contains(number);

    internal bool IsPageTreeNode(int number) => _pageTreeNodes.Contains(number);

    /// <summary>
    /// Follows references to the object they point to. Returns <paramref name="obj"/> itself when
    /// it is not a reference, and <see cref="PdfNull.Instance"/> for a reference to a missing object.
    /// </summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        for (int guard = 0; obj is PdfReference r && guard < 32; guard++)
        {
            obj = Objects.TryGetValue(r.Number, out PdfObject? target) ? target : PdfNull.Instance;
        }

        return obj is PdfReference ? PdfNull.Instance : obj;
    }

    internal PdfDictionary? ResolveDictionary(PdfObject? obj) => Resolve(obj) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    internal PageImporter ImporterFor(PdfDocument source)
    {
        if (!_importers.TryGetValue(source, out PageImporter? importer))
        {
            importer = new PageImporter(source, this);
            _importers[source] = importer;
        }

        return importer;
    }

    private static readonly string[] InheritableKeys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    /// <summary>
    /// Flattens the page tree. Inheritable attributes are copied onto each page, so pages stay
    /// correct when the tree is rebuilt or the page is moved to another document.
    /// </summary>
    private void LoadPageTree()
    {
        if (Catalog["Pages"] is not PdfReference rootRef) return;
        var seenNodes = new HashSet<int>();
        var stack = new Stack<(int Number, Dictionary<string, PdfObject> Inherited, int Depth)>();
        stack.Push((rootRef.Number, new Dictionary<string, PdfObject>(), 0));
        var ordered = new List<(int Number, Dictionary<string, PdfObject> Inherited)>();

        // Depth first, children in order: push in reverse.
        while (stack.Count > 0)
        {
            (int number, Dictionary<string, PdfObject> inherited, int depth) = stack.Pop();
            if (Resolve(new PdfReference(number, 0)) is not PdfDictionary node) continue;
            bool isPage = node.GetName("Type") == "Page" || (node.GetName("Type") != "Pages" && node["Kids"] is null);
            if (isPage)
            {
                ordered.Add((number, inherited));
                continue;
            }

            if (!seenNodes.Add(number) || depth > 64) continue;
            _pageTreeNodes.Add(number);
            var next = new Dictionary<string, PdfObject>(inherited);
            foreach (string key in InheritableKeys)
            {
                if (node[key] is { } value) next[key] = value;
            }

            if (Resolve(node["Kids"]) is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (kids[i] is PdfReference kid) stack.Push((kid.Number, next, depth + 1));
                }
            }
        }

        var used = new HashSet<int>();
        foreach ((int number, Dictionary<string, PdfObject> inherited) in ordered)
        {
            var dict = (PdfDictionary)Resolve(new PdfReference(number, 0))!;
            int pageNumber = number;
            if (!used.Add(number))
            {
                // The same page object listed twice: give the second listing its own object.
                dict = Clone(dict);
                pageNumber = Add(dict);
            }

            foreach ((string key, PdfObject value) in inherited)
            {
                if (dict[key] is null) dict.Set(key, value);
            }

            Pages.Attach(new PdfPage(this, pageNumber, dict));
        }
    }

    internal static PdfDictionary Clone(PdfDictionary d)
    {
        var copy = new PdfDictionary(d.Count);
        for (int i = 0; i < d.Count; i++)
        {
            (string key, PdfObject value) = d.EntryAt(i);
            copy.Set(key, value);
        }

        return copy;
    }
}
