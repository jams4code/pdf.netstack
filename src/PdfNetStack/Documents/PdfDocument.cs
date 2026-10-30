using System.IO.Compression;
using System.Runtime.ExceptionServices;
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

    /// <summary>Pack objects into compressed object streams (PDF 1.5). Default true. PDF/A-1 output never uses them.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>
    /// Run the lossless optimizer on the saved file: stream recompression and deduplication,
    /// with verification. Merged documents often share fonts and images, so this pays off. Default true.
    /// </summary>
    public bool Optimize { get; init; } = true;

    /// <summary>
    /// Whether the optimizer proves its rewrite equivalent to the file as first written, by reading
    /// both back and comparing them, before it uses the rewrite. Default true. The proof takes about
    /// half the optimizer's time. Turn it off for documents whose content this library drew itself,
    /// as <c>HtmlConverter.ConvertToPdf</c> does: whenever the proof would pass, the bytes are the
    /// same either way. Applies only with <see cref="Optimize"/>.
    /// </summary>
    public bool VerifyOptimization { get; init; } = true;

    /// <summary>Flate effort for streams the writer compresses. Default <see cref="CompressionEffort.Maximum"/>.</summary>
    public CompressionEffort Compression { get; init; } = CompressionEffort.Maximum;

    /// <summary>
    /// A standard to conform to, such as PDF/A-3b for archiving and e-invoices. Saving checks what
    /// cannot be repaired and throws listing every problem, then adds the output intent and
    /// metadata the standard requires.
    /// </summary>
    public PdfConformance Conformance { get; init; }
}

/// <summary>
/// A PDF document opened for editing or created from scratch. Pages can be added, removed,
/// reordered, rotated and imported from other documents; metadata and bookmarks can be read
/// and changed. Saving writes a new, single revision file; an opened document can also save its
/// changes as an incremental update with <see cref="SaveIncremental(Stream)"/>.
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

    /// <summary>True once drawing or images used DeviceCMYK, which PDF/A with an RGB intent forbids.</summary>
    internal bool UsesDeviceCmyk { get; set; }

    /// <summary>The Factur-X invoice attached with <see cref="PdfInvoice.Attach"/>, written to the PDF/A-3 metadata on save.</summary>
    internal InvoiceMetadata? Invoice { get; set; }
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

    internal ObjectStore Objects { get; private set; } = new();

    // The file an opened document came from, the stream it was read from when opened on demand,
    // and what the page tree looked like after opening: what an incremental save compares with.
    private PdfFile? _file;
    private Stream? _sourceStream;
    private List<int> _openedPages = [];
    private bool _pageTreeMatchesFile = true;
    private readonly Dictionary<int, List<(string Key, PdfObject Value)>> _inherited = new();

    // What the file holds, parsed afresh, for incremental saves to compare with.
    private PdfFile? _baseline;

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

    /// <summary>How viewers present the document: page layout, panels, window options, opening page.</summary>
    public PdfViewerSettings Viewer => new(this);

    /// <summary>The page numbers viewers show, such as a roman numbered preface.</summary>
    public PdfPageLabels PageLabels => new(this);

    /// <summary>Files embedded in the document.</summary>
    public PdfAttachmentCollection Attachments => new(this);

    /// <summary>The document's natural language as a BCP 47 tag, such as "en-US" or "ar", for screen readers and search.</summary>
    public string? Language
    {
        get => Resolve(Catalog["Lang"]) is PdfString s ? s.ToText() : null;
        set
        {
            if (value is null) Catalog.Remove("Lang");
            else Catalog.Set("Lang", PdfString.FromText(value));
        }
    }

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

    /// <summary>The named destinations bookmarks and links point at, to list, resolve and rename.</summary>
    public PdfNamedDestinations NamedDestinations => new(this);

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

        var doc = new PdfDocument(file.HeaderVersion, file.Security) { DecodeLimit = maxDecodedStreamBytes, _file = file };
        ObjectGraph graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, CancellationToken.None);
        foreach (int number in graph.Order) doc.Objects[number] = file.GetObject(number);
        doc.NextNumber = (doc.Objects.Count == 0 ? 0 : doc.Objects.Keys.Max()) + 1;
        doc.NextNumber = Math.Max(doc.NextNumber, FirstFreeNumber(file));
        doc.Attach(file);
        return doc;
    }

    /// <summary>
    /// Opens a document from a seekable stream without reading the file into memory. The
    /// cross-reference data and the page tree are read when opening; every other object is
    /// parsed when first used, and stream data (page content, images, fonts) is read from
    /// <paramref name="stream"/> each time it is needed and not kept. Memory use therefore
    /// follows what the application touches, not the size of the file: a document of any size
    /// can be read page by page, and <see cref="SaveIncremental(Stream)"/> writes only what
    /// changed. <see cref="ToArray(PdfSaveOptions?)"/> still builds the whole output in memory.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere. It must stay open and unchanged while the document is in use, and must not be read by anyone else meanwhile. The document never disposes it.</param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <param name="maxDecodedStreamBytes">Ceiling for any single decoded stream.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read or cannot seek.</exception>
    /// <exception cref="PdfFormatException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    public static PdfDocument OpenOnDemand(Stream stream, string? password = null, long maxDecodedStreamBytes = 256L << 20)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        PdfFile file = PdfFile.Open(stream, maxDecodedStreamBytes);
        if (file.Trailer["Encrypt"] is not null and not PdfNull) file.Unlock(password);

        var doc = new PdfDocument(file.HeaderVersion, file.Security) { DecodeLimit = maxDecodedStreamBytes, _file = file, _sourceStream = stream };
        doc.Objects = new ObjectStore(file);
        doc.NextNumber = FirstFreeNumber(file);
        doc.Attach(file);
        return doc;
    }

    /// <summary>
    /// The first number no object of the file uses, so objects added later never take the number
    /// of one the file still lists, such as an object stream an incremental update must not hide.
    /// </summary>
    private static int FirstFreeNumber(PdfFile file)
    {
        long highest = file.Xref.Count == 0 ? 0 : file.Xref.Keys.Max();
        long size = file.Trailer["Size"] is PdfNumber { IsInteger: true } n && n.IntegerValue <= highest + 1_000_000 ? n.IntegerValue : 0;
        return (int)Math.Min(int.MaxValue / 2, Math.Max(size, highest + 1));
    }

    /// <summary>Reads the catalog, document information and page tree of an opened file.</summary>
    private void Attach(PdfFile file)
    {
        if (file.Trailer["Root"] is not PdfReference root || Resolve(root) is not PdfDictionary catalog)
        {
            throw new PdfFormatException("Document catalog is missing or not a dictionary.");
        }

        Catalog = catalog;
        CatalogNumber = root.Number;
        if (file.Trailer["Info"] is PdfReference info && Resolve(info) is PdfDictionary) InfoNumber = info.Number;
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) Id = id;
        LoadPageTree();
        _openedPages = Pages.Select(p => p.Number).ToList();
    }

    /// <summary>Creates a document holding all pages of the given documents, in order.</summary>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents)
    {
        Compat.ThrowIfNull(documents, nameof(documents));
        PdfDocument result = Create();
        foreach (PdfDocument source in documents) result.Pages.Import(source.Pages);
        return result;
    }

    /// <summary>
    /// Flattens the annotations of every page: each one's appearance becomes page content and the
    /// annotation goes. Links and form widgets stay unless <paramref name="options"/> asks for
    /// them; hidden annotations stay. Returns how many were flattened.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfDocument doc = PdfDocument.Open("reviewed.pdf");
    /// doc.FlattenAnnotations();
    /// doc.Save("reviewed-flat.pdf");
    /// </code>
    /// </example>
    public int FlattenAnnotations(PdfFlattenOptions? options = null) => Pages.Sum(page => page.Annotations.Flatten(options));

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

    /// <summary>
    /// Saves the changes made since the document was opened as an incremental update (ISO
    /// 32000-1, 7.5.6): the original file byte for byte, followed by the objects that changed or
    /// were added and a cross-reference section that points back to the original's. Signatures
    /// over the original bytes stay valid, and for a large file only the update is new data.
    /// </summary>
    /// <remarks>
    /// Each object the document has read is compared with the file: it is written when anything
    /// in it differs. New objects are written when a written object refers to them. When pages
    /// were added, removed or reordered, the page tree is written again as one node. Nothing is
    /// recompressed or optimized, and <see cref="PdfSaveOptions"/> does not apply.
    /// </remarks>
    /// <param name="output">
    /// Receives the original file followed by the update, written from its current position.
    /// When it is the stream given to <see cref="OpenOnDemand"/>, only the update is written, at
    /// the end of that stream, and later saves build on the updated file.
    /// </param>
    /// <exception cref="InvalidOperationException">The document was created rather than opened, or has no pages.</exception>
    /// <exception cref="NotSupportedException">
    /// The document is encrypted or set to be encrypted, or it was damaged and had to be rebuilt
    /// when opened. Save such documents in full with <see cref="Save(Stream, PdfSaveOptions?)"/>.
    /// </exception>
    public void SaveIncremental(Stream output)
    {
        Compat.ThrowIfNull(output, nameof(output));
        if (_file is null) throw new InvalidOperationException("Only an opened document can be saved incrementally.");
        if (Security.Source is not null || Security.Target is not null) throw new NotSupportedException("Incremental saving of encrypted documents is not supported.");
        if (_file.WasReconstructed) throw new NotSupportedException("The document was damaged and rebuilt when opened; save it in full instead.");

        FinishFonts();
        if (Pages.Count == 0) throw new InvalidOperationException("A PDF must have at least one page.");
        _outlines?.WriteTo(this);

        PdfFile baseline = _baseline ??= _file.Fork();
        if (!PageTreeUnchanged(baseline))
        {
            var pagesNode = new PdfDictionary { ["Type"] = new PdfName("Pages") };
            int pagesNumber = Add(pagesNode);
            var kids = new PdfArray();
            foreach (PdfPage page in Pages)
            {
                page.Dictionary.Set("Parent", new PdfReference(pagesNumber, 0));
                kids.Items.Add(new PdfReference(page.Number, baseline.GenerationOf(page.Number)));
            }

            pagesNode.Set("Kids", kids);
            pagesNode.Set("Count", PdfNumber.FromInteger(Pages.Count));
            Catalog.Set("Pages", new PdfReference(pagesNumber, 0));
        }

        Dictionary<int, (PdfObject Object, int Generation)> update = ChangedObjects(baseline);
        var trailer = new PdfDictionary();
        trailer.Set("Root", new PdfReference(CatalogNumber, baseline.GenerationOf(CatalogNumber)));
        if (InfoNumber is { } info && Objects.ContainsKey(info)) trailer.Set("Info", new PdfReference(info, baseline.GenerationOf(info)));
        if (Id is not null) trailer.Set("ID", Id);

        long previousSize = baseline.Trailer["Size"] is PdfNumber { IsInteger: true } size ? size.IntegerValue : 0;
        int xrefNumber = (int)Math.Min(int.MaxValue - 1, Math.Max(Math.Max(previousSize, NextNumber), update.Count == 0 ? 0 : update.Keys.Max() + 1L));
        NextNumber = xrefNumber + 1;

        bool inPlace = ReferenceEquals(output, _sourceStream);
        if (!inPlace)
        {
            const int chunk = 1 << 20;
            for (long at = 0; at < baseline.Length; at += chunk)
            {
                output.Write(baseline.Source.Read(at, Math.Min(baseline.Length, at + chunk)).Span);
            }
        }

        // Nothing changed: the file as it is already says everything.
        if (update.Count == 0)
        {
            output.Flush();
            return;
        }

        ReadOnlySpan<byte> tail = baseline.Source.Read(baseline.Length - 1, baseline.Length).Span;
        bool needsBreak = tail.Length == 1 && tail[0] is not ((byte)'\n' or (byte)'\r');
        long start = baseline.Length + (needsBreak ? 1 : 0);
        byte[] section = IncrementalWriter.Write(update, trailer, start, baseline.OffsetBase, previousSize,
            baseline.LastStartXref(), baseline.UsesXrefStreams, xrefNumber);

        if (inPlace) output.Seek(0, SeekOrigin.End);
        if (needsBreak) output.WriteByte((byte)'\n');
        output.Write(section);
        output.Flush();

        if (inPlace)
        {
            // The stream now holds the update; later saves compare with it and append to it.
            _baseline = PdfFile.Open(output, DecodeLimit);
            foreach (int number in update.Keys) _inherited.Remove(number);
            _openedPages = Pages.Select(p => p.Number).ToList();
            _pageTreeMatchesFile = true;
        }
    }

    /// <summary>True when the pages are those the file's page tree lists, in its order, under its root.</summary>
    private bool PageTreeUnchanged(PdfFile baseline) =>
        _pageTreeMatchesFile
        && Pages.Select(p => p.Number).SequenceEqual(_openedPages)
        && Catalog["Pages"] is PdfReference pages
        && baseline.ResolveDictionary(baseline.Trailer["Root"])?["Pages"] is PdfReference original
        && pages.Number == original.Number
        && Pages.All(p => p.Dictionary["Parent"] is PdfReference parent && baseline.Exists(parent.Number));

    /// <summary>
    /// Objects that differ from <paramref name="baseline"/>, and the objects new to the file that
    /// they or the trailer reach, with the generation each is written under.
    /// </summary>
    private Dictionary<int, (PdfObject Object, int Generation)> ChangedObjects(PdfFile baseline)
    {
        var update = new Dictionary<int, (PdfObject, int)>();
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var x = new ByteSink(1024);
        var y = new ByteSink(1024);
        var candidates = new HashSet<int>(Objects.Held);
        candidates.UnionWith(_file!.CachedNumbers);
        var pending = new Queue<PdfObject>();
        foreach (int number in candidates.OrderBy(n => n))
        {
            if (!baseline.Exists(number) || !Objects.TryGetValue(number, out PdfObject current)) continue;
            if (!Differs(number, current, baseline.GetObject(number))) continue;
            update[number] = (current, baseline.GenerationOf(number));
            pending.Enqueue(current);
        }

        foreach (int number in new[] { CatalogNumber, InfoNumber ?? -1 })
        {
            if (number >= 0 && !baseline.Exists(number) && Objects.TryGetValue(number, out PdfObject root) && update.TryAdd(number, (root, 0))) pending.Enqueue(root);
        }

        while (pending.Count > 0)
        {
            foreach (int number in References(pending.Dequeue()))
            {
                if (baseline.Exists(number) || update.ContainsKey(number) || !Objects.TryGetValue(number, out PdfObject added)) continue;
                update[number] = (added, 0);
                pending.Enqueue(added);
            }
        }

        return update;

        bool Differs(int number, PdfObject current, PdfObject original)
        {
            if (current is PdfStream cs)
            {
                if (original is not PdfStream os || cs.Version != os.Version || cs.IsDeferred != os.IsDeferred) return true;
                return !Same(cs.Dictionary, os.Dictionary);
            }

            if (original is PdfStream) return true;

            if (current is PdfDictionary page && _inherited.TryGetValue(number, out List<(string Key, PdfObject Value)>? pushed))
            {
                // Inherited attributes were copied onto the page when it was opened; that is no change.
                page = Clone(page);
                foreach ((string key, PdfObject value) in pushed)
                {
                    if (ReferenceEquals(page[key], value)) page.Remove(key);
                }

                current = page;
            }

            return !Same(current, original);
        }

        bool Same(PdfObject a, PdfObject b)
        {
            x.Clear();
            y.Clear();
            serializer.Write(x, a);
            serializer.Write(y, b);
            return x.WrittenSpan.SequenceEqual(y.WrittenSpan);
        }
    }

    /// <summary>Numbers of the objects <paramref name="obj"/> refers to directly.</summary>
    private static List<int> References(PdfObject obj)
    {
        var found = new List<int>();
        var stack = new Stack<(PdfObject Value, int Depth)>();
        stack.Push((obj, 0));
        while (stack.Count > 0)
        {
            (PdfObject value, int depth) = stack.Pop();
            if (depth > PdfParser.MaxNesting) continue;
            switch (value)
            {
                case PdfReference r:
                    found.Add(r.Number);
                    break;
                case PdfStream s:
                    stack.Push((s.Dictionary, depth + 1));
                    break;
                case PdfDictionary d:
                    for (int i = 0; i < d.Count; i++) stack.Push((d.ValueAt(i), depth + 1));
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array.Items) stack.Push((item, depth + 1));
                    break;
            }
        }

        return found;
    }

    /// <summary>Saves the document to a byte array.</summary>
    public byte[] ToArray(PdfSaveOptions? options = null)
    {
        options ??= PdfSaveOptions.Default;

        // Drawing still open is written now, so a missing Dispose does not lose it.
        foreach (Drawing.PdfGraphics g in _openGraphics.ToArray()) g.Dispose();

        // Font dictionaries were reserved on first use; widths, encodings and subsets depend on
        // everything drawn, so they are written now. Subsetting and compressing each program is
        // the font's own work, so fonts do it side by side, then write in order.
        Fonts.FontResource[] fonts = [.. _fonts.Values];
        var finish = new Action?[fonts.Length];
        var failures = new ExceptionDispatchInfo?[fonts.Length];
        if (fonts.Length > 1)
        {
            Parallel.For(0, fonts.Length, i =>
            {
                try
                {
                    finish[i] = fonts[i].Prepare();
                }
                catch (Exception e)
                {
                    failures[i] = ExceptionDispatchInfo.Capture(e);
                }
            });
        }

        for (int i = 0; i < fonts.Length; i++)
        {
            failures[i]?.Throw();
            (finish[i] ?? fonts[i].Prepare())();
        }

        if (Pages.Count == 0) throw new InvalidOperationException("A PDF must have at least one page.");
        if (Invoice is not null && options.Conformance == PdfConformance.None)
        {
            throw new InvalidOperationException("A Factur-X or ZUGFeRD invoice must be saved as PDF/A-3: set PdfSaveOptions.Conformance to PdfA3B or PdfA3U.");
        }

        // Bookmarks are written first, so PDF/A sees their actions as they will be saved.
        _outlines?.WriteTo(this);
        int archive = PdfArchive.Part(options.Conformance);
        if (archive != 0) PdfArchive.Prepare(this, options.Conformance);
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

        var dead = new HashSet<int>(_pageTreeNodes);
        var live = new HashSet<int>(Pages.Select(p => p.Number));
        foreach (int n in _everPages)
        {
            if (!live.Contains(n)) dead.Add(n);
        }

        dead.Remove(pagesNumber);

        var trailer = new PdfDictionary();
        trailer.Set("Root", new PdfReference(CatalogNumber, 0));
        int? archiveInfo = null;
        if (InfoNumber is { } info && Objects.ContainsKey(info))
        {
            // PDF/A-4 keeps the document information in XMP; the dictionary written holds only the modification date (ISO 19005-4, 6.1.3).
            if (archive == 4 && Resolve(new PdfReference(info, 0)) is PdfDictionary full)
            {
                var dates = new PdfDictionary();
                if (full["ModDate"] is { } modified) dates.Set("ModDate", modified);
                archiveInfo = Add(dates);
                trailer.Set("Info", new PdfReference(archiveInfo.Value, 0));
            }
            else
            {
                trailer.Set("Info", new PdfReference(info, 0));
            }
        }

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
        // PDF/A-1 is PDF 1.4, which predates object and xref streams; PDF/A-4 is PDF 2.0.
        bool objectStreams = options.UseObjectStreams && archive != 1;
        string version = PdfArchive.HeaderVersion(archive) ?? (objectStreams && string.CompareOrdinal(Version, "1.5") < 0 ? "1.5" : Version);
        // The optimizer pass below does the exhaustive search; without it, the writer does.
        var settings = new WriterSettings(objectStreams, 200, version, level)
        {
            Exhaustive = options.Compression == CompressionEffort.Exhaustive && !options.Optimize,
        };
        byte[] written = PdfDocumentWriter.Write(objects, trailer, Map, settings).ToArray();
        Objects.Remove(pagesNumber);
        if (archiveInfo is int infoCopy) Objects.Remove(infoCopy);

        if (options.Optimize)
        {
            var optimizerOptions = new PdfOptimizerOptions
            {
                MinSavingPercent = 0,
                Compression = options.Compression,
                UseObjectStreams = objectStreams,
                Verify = options.VerifyOptimization,
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

    /// <summary>Writes the widths, encodings and subsets of the fonts used so far.</summary>
    internal void FinishFonts()
    {
        foreach (Drawing.PdfGraphics g in _openGraphics.ToArray()) g.Dispose();
        foreach (Fonts.FontResource font in _fonts.Values) font.Finish();
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
                _pageTreeMatchesFile = false;
            }

            foreach ((string key, PdfObject value) in inherited)
            {
                if (dict[key] is not null) continue;
                dict.Set(key, value);
                if (!_inherited.TryGetValue(pageNumber, out List<(string Key, PdfObject Value)>? keys))
                {
                    keys = [];
                    _inherited[pageNumber] = keys;
                }

                keys.Add((key, value));
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
