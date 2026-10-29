using System.Globalization;
using PdfNetStack.Filters;
using PdfNetStack.Objects;

namespace PdfNetStack.Writing;

/// <summary>
/// Writes a linearized PDF (ISO 32000-1, Annex F): the first page and everything needed to show
/// it come first with a cross-reference section of their own, so a viewer reading over a network
/// can display page one before the rest arrives, and a primary hint stream tells it where every
/// other page and shared object starts.
/// </summary>
/// <remarks>
/// Objects are assigned to the parts of Annex F by who uses them, the way qpdf does it, so its
/// <c>--check-linearization</c> computes the same layout from the output: the catalog and the
/// objects opening the document needs (viewer preferences, open action, interactive form) in
/// part 4; the first page with everything only it uses, and what it shares with others, in part
/// 6; each further page with what only it uses in part 7; objects several later pages share in
/// part 8; and the page tree, outlines, document information and the rest in part 9. Every
/// object is written uncompressed, as each needs its own offset, and the hint tables use one
/// object per shared group. Page objects must not inherit attributes; see
/// <see cref="Optimization.InheritedAttributes"/>.
/// </remarks>
internal static class LinearizedWriter
{
    private static readonly HashSet<string> OpenDocumentKeys = new(StringComparer.Ordinal) { "ViewerPreferences", "PageMode", "Threads", "OpenAction", "AcroForm" };
    private static readonly byte[] BinaryMarker = [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];

    // Width every number in the linearization dictionary and the first trailer is padded to,
    // so their size is known before the offsets they hold.
    private const int NumberWidth = 10;

    private enum UserKind
    {
        Page,
        Thumbnail,
        TrailerKey,
        RootKey,
        Root,
    }

    private readonly record struct User(UserKind Kind, int Page = 0, string Key = "");

    /// <summary>The linearized file, or null when the objects have no page tree to lay out.</summary>
    /// <param name="objects">Object i is numbered i + 1 in <paramref name="mapReference"/>'s numbering.</param>
    /// <param name="trailer">Root, Info and ID.</param>
    /// <param name="mapReference">Maps any reference in the objects to its object's number, or -1.</param>
    /// <param name="settings">Version and compression; object streams and encryption are not used.</param>
    public static ReadOnlyMemory<byte>? Write(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings)
    {
        var layout = Layout.Build(objects, trailer, mapReference);
        return layout is null ? null : Assemble(layout, objects, trailer, mapReference, settings);
    }

    /// <summary>Which objects go where, in the numbering of the input.</summary>
    private sealed class Layout
    {
        public List<int> Part4 { get; } = [];

        public List<int> Part6 { get; } = [];

        public List<List<int>> Part7 { get; } = [];

        public List<int> Part8 { get; } = [];

        public List<int> Part9 { get; } = [];

        public List<int> Pages { get; } = [];

        public int OutlineRoot { get; set; } = -1;

        public int OutlineCount { get; set; }

        /// <summary>For each page after the first, the objects it shares that the shared object hint table lists.</summary>
        public List<List<int>> SharedReferences { get; } = [];

        public static Layout? Build(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> map)
        {
            int Target(PdfObject? o) => o is PdfReference r && map(r) is var n && n >= 1 && n <= objects.Count ? n : -1;
            PdfObject Get(int n) => objects[n - 1];

            int rootNumber = Target(trailer["Root"]);
            if (rootNumber < 0 || Get(rootNumber) is not PdfDictionary catalog) return null;

            var layout = new Layout();
            if (!CollectPages(catalog["Pages"], 0, new HashSet<int>())) return null;
            if (layout.Pages.Count == 0) return null;

            // Who uses each object, found the way qpdf finds it: page by page without crossing
            // into other pages or back up the tree, then the trailer's and the catalog's entries.
            var usersOf = new Dictionary<int, HashSet<User>>();
            var objectsOf = new Dictionary<User, SortedSet<int>>();
            for (int i = 0; i < layout.Pages.Count; i++) Traverse(new User(UserKind.Page, i), PdfNull.Instance, layout.Pages[i]);
            foreach ((string key, PdfObject value) in Entries(trailer))
            {
                if (key != "Root") Traverse(new User(UserKind.TrailerKey, Key: key), value, -1);
            }

            foreach ((string key, PdfObject value) in Entries(catalog))
            {
                Traverse(new User(UserKind.RootKey, Key: key), value, -1);
            }

            Record(new User(UserKind.Root), rootNumber);

            var root = new HashSet<int>();
            var open = new SortedSet<int>();
            var outlines = new SortedSet<int>();
            var firstPrivate = new HashSet<int>();
            var firstShared = new HashSet<int>();
            var otherPrivate = new HashSet<int>();
            var otherShared = new SortedSet<int>();
            var thumbPrivate = new HashSet<int>();
            var thumbShared = new SortedSet<int>();
            var other = new SortedSet<int>();
            foreach ((int number, HashSet<User> users) in usersOf)
            {
                bool inOpen = false, inFirst = false, inOutlines = false, isRoot = false;
                int otherPages = 0, thumbs = 0, others = 0;
                foreach (User u in users)
                {
                    switch (u.Kind)
                    {
                        case UserKind.TrailerKey:
                            if (u.Key == "Encrypt") inOpen = true;
                            else others++;
                            break;
                        case UserKind.Thumbnail:
                            thumbs++;
                            break;
                        case UserKind.RootKey:
                            if (OpenDocumentKeys.Contains(u.Key)) inOpen = true;
                            else if (u.Key == "Outlines") inOutlines = true;
                            else others++;
                            break;
                        case UserKind.Page:
                            if (u.Page == 0) inFirst = true;
                            else otherPages++;
                            break;
                        case UserKind.Root:
                            isRoot = true;
                            break;
                    }
                }

                if (isRoot) root.Add(number);
                else if (inOutlines) outlines.Add(number);
                else if (inOpen) open.Add(number);
                else if (inFirst && others == 0 && otherPages == 0 && thumbs == 0) firstPrivate.Add(number);
                else if (inFirst) firstShared.Add(number);
                else if (otherPages == 1 && others == 0 && thumbs == 0) otherPrivate.Add(number);
                else if (otherPages > 1) otherShared.Add(number);
                else if (thumbs == 1 && others == 0) thumbPrivate.Add(number);
                else if (thumbs > 1) thumbShared.Add(number);
                else other.Add(number);
            }

            if (root.Count != 1) return null;
            bool outlinesFirst = catalog.GetName("PageMode") == "UseOutlines" && catalog["Outlines"] is not null;

            // Part 4: the catalog and what opening the document needs.
            layout.Part4.Add(rootNumber);
            layout.Part4.AddRange(open);

            // Part 6: the first page object, then what only it uses, then what it shares.
            int first = layout.Pages[0];
            if (!firstPrivate.Remove(first)) return null;
            layout.Part6.Add(first);
            SortedSet<int> firstUses = objectsOf[new User(UserKind.Page, 0)];
            foreach (int n in firstUses)
            {
                if (firstPrivate.Remove(n)) layout.Part6.Add(n);
            }

            foreach (int n in firstUses)
            {
                if (firstShared.Remove(n)) layout.Part6.Add(n);
            }

            if (outlinesFirst) layout.PlaceOutlines(layout.Part6, outlines, Target(catalog["Outlines"]));

            // Part 7: every other page, page object first, with what only that page uses.
            for (int i = 1; i < layout.Pages.Count; i++)
            {
                int page = layout.Pages[i];
                if (!otherPrivate.Remove(page)) return null;
                var section = new List<int> { page };
                foreach (int n in objectsOf[new User(UserKind.Page, i)])
                {
                    if (otherPrivate.Remove(n)) section.Add(n);
                }

                layout.Part7.Add(section);
            }

            // Part 8: objects several later pages share.
            layout.Part8.AddRange(otherShared);

            // Part 9: the page tree, thumbnails, outlines, then everything else.
            if (objectsOf.TryGetValue(new User(UserKind.RootKey, Key: "Pages"), out SortedSet<int>? tree))
            {
                foreach (int n in tree)
                {
                    if (other.Remove(n)) layout.Part9.Add(n);
                }
            }

            for (int i = 0; i < layout.Pages.Count; i++)
            {
                int thumb = Target(((PdfDictionary)Get(layout.Pages[i]))["Thumb"]);
                if (thumb > 0 && thumbPrivate.Remove(thumb)) layout.Part9.Add(thumb);
                if (!objectsOf.TryGetValue(new User(UserKind.Thumbnail, i), out SortedSet<int>? thumbObjects)) continue;
                foreach (int n in thumbObjects)
                {
                    if (thumbPrivate.Remove(n)) layout.Part9.Add(n);
                }
            }

            layout.Part9.AddRange(thumbShared);
            if (!outlinesFirst) layout.PlaceOutlines(layout.Part9, outlines, Target(catalog["Outlines"]));
            layout.Part9.AddRange(other);
            if (firstPrivate.Count + firstShared.Count + otherPrivate.Count + thumbPrivate.Count + outlines.Count > 0) return null;

            // Objects no user reaches, such as those behind a page object that is not in the
            // page tree, are still referenced; they go last.
            var placed = new HashSet<int>(layout.Part4.Concat(layout.Part6).Concat(layout.Part7.SelectMany(p => p)).Concat(layout.Part8).Concat(layout.Part9));
            layout.Part9.AddRange(Referenced(objects, trailer, Target).Where(n => !placed.Contains(n)).OrderBy(n => n));

            // Shared object references of each later page: objects with more than one user that
            // the shared object hint table lists, which holds part 6 and then part 8.
            var listed = new HashSet<int>(layout.Part6.Concat(layout.Part8));
            for (int i = 1; i < layout.Pages.Count; i++)
            {
                layout.SharedReferences.Add(objectsOf[new User(UserKind.Page, i)].Where(n => usersOf[n].Count > 1 && listed.Contains(n)).ToList());
            }

            return layout;

            bool CollectPages(PdfObject? node, int depth, HashSet<int> path)
            {
                int n = Target(node);
                if (n < 0 || depth > 64 || !path.Add(n)) return false;
                if (Get(n) is not PdfDictionary dict) return false;
                if (dict.GetName("Type") == "Page")
                {
                    layout.Pages.Add(n);
                    return true;
                }

                if (dict.GetName("Type") != "Pages" || dict["Kids"] is not PdfArray kids) return false;
                foreach (PdfObject kid in kids)
                {
                    if (!CollectPages(kid, depth + 1, path)) return false;
                }

                return true;
            }

            void Record(User user, int number)
            {
                if (!usersOf.TryGetValue(number, out HashSet<User>? users))
                {
                    users = [];
                    usersOf[number] = users;
                }

                users.Add(user);
                if (!objectsOf.TryGetValue(user, out SortedSet<int>? owned))
                {
                    owned = [];
                    objectsOf[user] = owned;
                }

                owned.Add(number);
            }

            // One walk from a starting value, its visited set shared by the thumbnail it may
            // branch into, as qpdf walks it. A page met anywhere but at the start is not entered.
            void Traverse(User user, PdfObject start, int startNumber)
            {
                var visited = new HashSet<int>();
                var pending = new Stack<(User User, PdfObject Value, int Number, bool Top, int Depth)>();
                pending.Push((user, start, startNumber, true, 0));
                while (pending.Count > 0)
                {
                    (User owner, PdfObject value, int number, bool top, int depth) = pending.Pop();
                    if (depth > Parsing.PdfParser.MaxNesting) continue;
                    if (number < 0)
                    {
                        number = Target(value);
                        if (value is PdfReference && number < 0) continue;
                    }

                    PdfObject current = number > 0 ? Get(number) : value;
                    bool isPage = current is PdfDictionary { } pd && pd.GetName("Type") == "Page";
                    if (isPage && !top) continue;
                    if (number > 0)
                    {
                        if (!visited.Add(number)) continue;
                        Record(owner, number);
                    }

                    switch (current)
                    {
                        case PdfArray array:
                            for (int i = 0; i < array.Count; i++) pending.Push((owner, array[i], -1, false, depth + 1));
                            break;
                        case PdfDictionary dict:
                            Push(owner, dict, isPage, isStream: false, depth);
                            break;
                        case PdfStream stream:
                            Push(owner, stream.Dictionary, isPage: false, isStream: true, depth);
                            break;
                    }
                }

                void Push(User owner, PdfDictionary dict, bool isPage, bool isStream, int depth)
                {
                    foreach ((string key, PdfObject value) in Entries(dict))
                    {
                        if (value is PdfNull) continue;

                        // The writer puts /Length in the stream dictionary directly.
                        if (isStream && key == "Length") continue;
                        if (isPage && key == "Parent") continue;
                        User next = isPage && key == "Thumb" ? new User(UserKind.Thumbnail, owner.Page) : owner;
                        pending.Push((next, value, -1, false, depth + 1));
                    }
                }
            }
        }

        private void PlaceOutlines(List<int> part, SortedSet<int> outlines, int outlineRoot)
        {
            if (outlineRoot < 0 || !outlines.Remove(outlineRoot))
            {
                // Outline objects without an outline dictionary of their own: no hint table for them.
                part.AddRange(outlines);
                outlines.Clear();
                return;
            }

            OutlineRoot = outlineRoot;
            part.Add(outlineRoot);
            part.AddRange(outlines);
            OutlineCount = 1 + outlines.Count;
            outlines.Clear();
        }

        /// <summary>Entries in name order, the order qpdf visits them in.</summary>
        private static IEnumerable<(string Key, PdfObject Value)> Entries(PdfDictionary d)
        {
            var entries = new List<(string, PdfObject)>(d.Count);
            for (int i = 0; i < d.Count; i++) entries.Add((d.EntryAt(i).Key, d.ValueAt(i)));
            return entries.OrderBy(e => e.Item1, StringComparer.Ordinal);
        }

        /// <summary>Every object the written file refers to, from the trailer on.</summary>
        private static HashSet<int> Referenced(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfObject?, int> target)
        {
            var found = new HashSet<int>();
            var pending = new Stack<(PdfObject Value, int Depth)>();
            pending.Push((trailer, 0));
            while (pending.Count > 0)
            {
                (PdfObject value, int depth) = pending.Pop();
                if (depth > Parsing.PdfParser.MaxNesting) continue;
                switch (value)
                {
                    case PdfReference:
                        int n = target(value);
                        if (n > 0 && found.Add(n)) pending.Push((objects[n - 1], depth + 1));
                        break;
                    case PdfArray array:
                        foreach (PdfObject item in array.Items) pending.Push((item, depth + 1));
                        break;
                    case PdfDictionary dict:
                        for (int i = 0; i < dict.Count; i++) pending.Push((dict.ValueAt(i), depth + 1));
                        break;
                    case PdfStream stream:
                        foreach ((string key, PdfObject item) in Entries(stream.Dictionary))
                        {
                            if (key != "Length") pending.Push((item, depth + 1));
                        }

                        break;
                }
            }

            return found;
        }
    }

    private static ReadOnlyMemory<byte> Assemble(Layout layout, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings)
    {
        // Numbers: parts 7, 8 and 9 from 1 in file order, then the linearization dictionary,
        // part 4, part 6 and the hint stream, which the first page's cross-reference section covers.
        var main = layout.Part7.SelectMany(p => p).Concat(layout.Part8).Concat(layout.Part9).ToList();
        var renumber = new Dictionary<int, int>();
        foreach (int n in main) renumber[n] = renumber.Count + 1;
        int mainCount = main.Count;
        int lindictNumber = mainCount + 1;
        int next = lindictNumber + 1;
        foreach (int n in layout.Part4.Concat(layout.Part6)) renumber[n] = next++;
        int hintNumber = next;
        int size = hintNumber + 1;
        int firstSectionCount = size - lindictNumber;

        int Map(PdfReference r) => mapReference(r) is var n && renumber.TryGetValue(n, out int m) ? m : -1;
        var serializer = new PdfSerializer(Map);
        var plain = settings with { Encryption = null, EncryptionDictionaryNumber = -1 };
        var bodies = new Dictionary<int, byte[]>(renumber.Count);
        var sink = new ByteSink(4096);
        foreach ((int old, int number) in renumber)
        {
            sink.Clear();
            PdfDocumentWriter.WriteIndirect(sink, serializer, number, objects[old - 1], plain);
            bodies[old] = sink.ToArray();
        }

        // Fixed size pieces before the objects.
        byte[] header = [.. System.Text.Encoding.ASCII.GetBytes("%PDF-" + settings.Version + "\n"), .. BinaryMarker];
        int lindictLength = Lindict(lindictNumber, 0, 0, 0, 0, 0, 0, 0).Length;
        int firstXrefLength = FirstXrefHeader(lindictNumber, firstSectionCount).Length + 20 * firstSectionCount;
        int firstTrailerLength = FirstTrailer(trailer, serializer, size, 0).Length;
        long firstXrefOffset = header.Length + lindictLength;

        // Offsets as if the hint stream were absent, which is how the hint tables give them.
        var offset = new Dictionary<int, long>(renumber.Count);
        long position = firstXrefOffset + firstXrefLength + firstTrailerLength;
        foreach (int n in layout.Part4) Place(n);
        long hintOffset = position;
        foreach (int n in layout.Part6) Place(n);
        long firstPageEnd = position;
        foreach (int n in main) Place(n);
        long mainXrefOffset = position;

        byte[] hintData = HintTables(layout, bodies, offset, renumber, out int sharedTableOffset, out int outlineTableOffset);
        byte[] compressed = StreamCodec.Deflate(hintData, settings.CompressionLevel);
        var hint = new ByteSink(compressed.Length + 128);
        hint.WriteAscii($"{hintNumber} 0 obj\n<</Filter/FlateDecode/Length {compressed.Length}/S {sharedTableOffset}");
        if (outlineTableOffset >= 0) hint.WriteAscii($"/O {outlineTableOffset}");
        hint.Write(">>stream\n"u8);
        hint.Write(compressed);
        hint.Write("\nendstream\nendobj\n"u8);
        long hintLength = hint.Length;

        // Real offsets: everything from the first page on moves by the hint stream's length.
        long Real(long o) => o >= hintOffset ? o + hintLength : o;
        string mainXrefHeader = $"xref\n0 {mainCount + 1}";
        long spaceBeforeZero = Real(mainXrefOffset) + mainXrefHeader.Length;
        byte[] mainSection = MainSection(mainXrefHeader, main, n => Real(offset[n]), mainCount, firstXrefOffset);
        long fileLength = Real(mainXrefOffset) + mainSection.Length;

        var output = new ByteSink((int)Math.Min(fileLength, Compat.MaxArrayLength));
        output.Write(header);
        output.Write(Lindict(lindictNumber, fileLength, hintOffset, hintLength, renumber[layout.Pages[0]], Real(firstPageEnd), layout.Pages.Count, spaceBeforeZero));
        output.Write(FirstXrefHeader(lindictNumber, firstSectionCount));
        var firstSection = new SortedDictionary<int, long> { [lindictNumber] = header.Length, [hintNumber] = hintOffset };
        foreach (int n in layout.Part4.Concat(layout.Part6)) firstSection[renumber[n]] = Real(offset[n]);
        foreach (long o in firstSection.Values) output.WriteAscii(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        output.Write(FirstTrailer(trailer, serializer, size, Real(mainXrefOffset)));
        foreach (int n in layout.Part4) output.Write(bodies[n]);
        output.Write(hint.WrittenSpan);
        foreach (int n in layout.Part6.Concat(main)) output.Write(bodies[n]);
        output.Write(mainSection);
        return output.WrittenMemory;

        void Place(int n)
        {
            offset[n] = position;
            position += bodies[n].Length;
        }
    }

    private static string Pad(long value) => value.ToString(CultureInfo.InvariantCulture).PadRight(NumberWidth);

    private static byte[] Lindict(int number, long length, long hintOffset, long hintLength, int firstPage, long firstPageEnd, int pages, long spaceBeforeZero) =>
        System.Text.Encoding.ASCII.GetBytes(
            $"{number} 0 obj\n<</Linearized 1/L {Pad(length)}/H[ {Pad(hintOffset)} {Pad(hintLength)}]/O {Pad(firstPage)}/E {Pad(firstPageEnd)}/N {Pad(pages)}/T {Pad(spaceBeforeZero)}>>\nendobj\n");

    private static byte[] FirstXrefHeader(int first, int count) => System.Text.Encoding.ASCII.GetBytes($"xref\n{first} {count}\n");

    private static byte[] FirstTrailer(PdfDictionary source, PdfSerializer serializer, int size, long previous)
    {
        var sink = new ByteSink(256);
        sink.WriteAscii($"trailer\n<</Size {size}");
        foreach (string key in new[] { "Root", "Info", "ID" })
        {
            if (source[key] is not { } value) continue;
            PdfSerializer.WriteName(sink, key);
            serializer.Write(sink, value);
        }

        sink.WriteAscii($"/Prev {Pad(previous)}>>\nstartxref\n0\n%%EOF\n");
        return sink.ToArray();
    }

    private static byte[] MainSection(string header, List<int> main, Func<int, long> offsetOf, int count, long firstXrefOffset)
    {
        var sink = new ByteSink(64 + 20 * (count + 1));
        sink.WriteAscii(header + "\n0000000000 65535 f \n");
        foreach (int n in main) sink.WriteAscii(offsetOf(n).ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        sink.WriteAscii($"trailer\n<</Size {count + 1}>>\nstartxref\n{firstXrefOffset}\n%%EOF\n");
        return sink.ToArray();
    }

    /// <summary>
    /// The page offset hint table, the shared object hint table and, with outlines, the outline
    /// hint table (ISO 32000-1, F.4). Offsets are those of the file without the hint stream.
    /// Each run of per-page or per-group items starts on a byte boundary.
    /// </summary>
    private static byte[] HintTables(Layout layout, Dictionary<int, byte[]> bodies, Dictionary<int, long> offset, Dictionary<int, int> renumber, out int sharedTableOffset, out int outlineTableOffset)
    {
        int pageCount = layout.Pages.Count;
        var pageObjects = new int[pageCount];
        var pageLengths = new long[pageCount];
        pageObjects[0] = layout.Part6.Count;
        pageLengths[0] = layout.Part6.Sum(n => (long)bodies[n].Length);
        for (int i = 1; i < pageCount; i++)
        {
            pageObjects[i] = layout.Part7[i - 1].Count;
            pageLengths[i] = layout.Part7[i - 1].Sum(n => (long)bodies[n].Length);
        }

        var sharedIndex = new Dictionary<int, int>();
        foreach (int n in layout.Part6.Concat(layout.Part8)) sharedIndex[n] = sharedIndex.Count;
        var shared = new List<List<int>> { new() };
        foreach (List<int> refs in layout.SharedReferences) shared.Add(refs.Select(n => sharedIndex[n]).ToList());

        int minObjects = pageObjects.Min();
        long minLength = pageLengths.Min();
        int objectBits = Bits(pageObjects.Max() - minObjects);
        int lengthBits = Bits(pageLengths.Max() - minLength);
        int countBits = Bits(shared.Max(s => s.Count));
        int idBits = Bits(shared.SelectMany(s => s).DefaultIfEmpty(0).Max());

        var w = new BitWriter();
        w.Write(minObjects, 32);
        w.Write(offset[layout.Pages[0]], 32);
        w.Write(objectBits, 16);
        w.Write(minLength, 32);
        w.Write(lengthBits, 16);
        w.Write(0, 32); // least content stream offset: readers ignore the content items
        w.Write(0, 16);
        w.Write(minLength, 32); // content stream length: the page length, an upper bound
        w.Write(lengthBits, 16);
        w.Write(countBits, 16);
        w.Write(idBits, 16);
        w.Write(0, 16); // numerators of fractional positions: none
        w.Write(1, 16);
        foreach (int n in pageObjects) w.Write(n - minObjects, objectBits);
        w.Align();
        foreach (long l in pageLengths) w.Write(l - minLength, lengthBits);
        w.Align();
        foreach (List<int> s in shared) w.Write(s.Count, countBits);
        w.Align();
        foreach (int id in shared.SelectMany(s => s)) w.Write(id, idBits);
        w.Align();
        w.Align(); // numerators, zero bits each
        w.Align(); // content stream offsets, zero bits each
        foreach (long l in pageLengths) w.Write(l - minLength, lengthBits);
        w.Align();

        // Shared object hint table: part 6 then part 8, one object per group.
        sharedTableOffset = w.Length;
        var groups = layout.Part6.Concat(layout.Part8).Select(n => (long)bodies[n].Length).ToList();
        long minGroup = groups.Min();
        int groupBits = Bits(groups.Max() - minGroup);
        w.Write(layout.Part8.Count > 0 ? renumber[layout.Part8[0]] : 0, 32);
        w.Write(layout.Part8.Count > 0 ? offset[layout.Part8[0]] : 0, 32);
        w.Write(layout.Part6.Count, 32);
        w.Write(groups.Count, 32);
        w.Write(0, 16); // objects per group, less one: always zero
        w.Write(minGroup, 32);
        w.Write(groupBits, 16);
        foreach (long g in groups) w.Write(g - minGroup, groupBits);
        w.Align();
        foreach (long _ in groups) w.Write(0, 1); // no MD5 signatures
        w.Align();

        outlineTableOffset = -1;
        if (layout.OutlineRoot > 0)
        {
            outlineTableOffset = w.Length;
            List<int> outline = layout.Part6.Contains(layout.OutlineRoot) ? layout.Part6 : layout.Part9;
            int start = outline.IndexOf(layout.OutlineRoot);
            w.Write(renumber[layout.OutlineRoot], 32);
            w.Write(offset[layout.OutlineRoot], 32);
            w.Write(layout.OutlineCount, 32);
            w.Write(outline.Skip(start).Take(layout.OutlineCount).Sum(n => (long)bodies[n].Length), 32);
        }

        return w.ToArray();
    }

    /// <summary>Bits needed to write <paramref name="value"/>; zero for zero.</summary>
    private static int Bits(long value)
    {
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>Writes unsigned fields most significant bit first.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _used;

        /// <summary>Whole bytes written so far; call after <see cref="Align"/>.</summary>
        public int Length => _bytes.Count;

        public void Write(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_used == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = 0;
                    _used = 0;
                }
            }
        }

        /// <summary>Pads with zero bits to the next byte boundary.</summary>
        public void Align()
        {
            if (_used > 0) Write(0, 8 - _used);
        }

        public byte[] ToArray()
        {
            Align();
            return [.. _bytes];
        }
    }
}
