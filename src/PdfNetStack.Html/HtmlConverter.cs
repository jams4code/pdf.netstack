using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Html;

/// <summary>
/// Converts HTML and CSS to PDF without a browser: the document is parsed as browsers do, styled
/// with the cascade, laid out in CSS boxes and split into pages per the paged media rules.
/// </summary>
/// <example>
/// <code>
/// byte[] pdf = HtmlConverter.ConvertToPdf("&lt;h1&gt;Invoice&lt;/h1&gt;&lt;p&gt;Total: 42 EUR&lt;/p&gt;");
/// PdfDocument doc = HtmlConverter.ConvertFileToDocument("report.html");
/// doc.Save("report.pdf");
/// </code>
/// </example>
public static class HtmlConverter
{
    private const double PointsPerPixel = 0.75;

    // Box building, layout and painting recurse once per nesting level, up to the parser's limit
    // of 512. A stack overflow ends the process and cannot be caught, so the work runs on a thread
    // whose stack has room for the deepest tree; the size is reserved, not committed.
    private const int StackSize = 64 << 20;

    /// <summary>Converts an HTML string to a PDF document.</summary>
    /// <param name="html">The HTML. Malformed markup is repaired as browsers repair it.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    /// <exception cref="HtmlConversionException">The document needs more pages than <see cref="HtmlConverterOptions.MaxPages"/>.</exception>
    public static PdfDocument ConvertToDocument(string html, HtmlConverterOptions? options = null)
    {
        Compat.ThrowIfNull(html, nameof(html));
        options ??= new HtmlConverterOptions();
        PdfDocument? result = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    result = Convert(html, options);
                }
                catch (Exception e)
                {
                    // Rethrown on the caller's thread, with its original stack trace.
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                }
            },
            StackSize)
        {
            Name = "PdfNetStack.Html",
            IsBackground = true,
        };
        worker.Start();
        worker.Join();
        failure?.Throw();
        return result!;
    }

    private static PdfDocument Convert(string html, HtmlConverterOptions options)
    {
        var resources = new Resources(options.BaseUri, options.ResourceLoader);
        HtmlDocument doc = HtmlParser.Parse(html);

        // Media queries see the default page; @page rules may change it later.
        (double probeWidth, double probeHeight) = PageMaster.PageSize(CssParser.ParseDeclarations(options.PageStyle).LastOrDefault(d => d.Property == "size")?.Value, 16);
        var resolver = new StyleResolver(new CssMedia(probeWidth, probeHeight));
        if (options.StyleSheet is string sheet) resolver.AddStyleSheet(sheet, CssOrigin.Author, resources.LoadText);
        resolver.AddDocument(doc, resources.LoadText);

        var fonts = new FontResolver(options.Fonts, options.UseSystemFonts)
        {
            Hyphenators = options.Hyphenators is null ? null : new HyphenatorCache(options.Hyphenators),
        };
        fonts.AddFontFaces(resolver.FontFaces, resources.Load);
        var builder = new BoxBuilder(resolver, resources.LoadImage);
        BlockBox root = builder.Build(doc);
        var running = new RunningElements(builder.Running, builder.PageCounters, resolver, fonts, resources.LoadImage);
        var master = new PageMaster(resolver, root.Style, options.PageStyle);
        List<PageRun> runs = LayoutRuns(root, master, fonts, options.MaxPages);
        int pageCount = runs.Sum(r => r.Slices.Count);

        var pdf = PdfDocument.Create();
        Metadata(pdf, doc);
        var pages = new List<PdfPage>();
        var pageRuns = new List<(PageRun Run, Paginator.Slice Slice)>();
        foreach (PageRun run in runs)
        {
            foreach (Paginator.Slice slice in run.Slices)
            {
                PageGeometry g = master.Geometry(pages.Count, run.Name);
                pages.Add(pdf.Pages.Add(new PdfRectangle(0, 0, g.Width * PointsPerPixel, g.Height * PointsPerPixel)));
                pageRuns.Add((run, slice));
            }
        }

        Dictionary<string, (PageRun Run, int Page, double Y)> anchors = Anchors(runs);
        (PdfPage Page, double Top)? Anchor(string id)
        {
            if (!anchors.TryGetValue(id, out var a)) return null;
            Paginator.Slice slice = pageRuns[a.Page].Slice;
            return (pages[a.Page], (master.Geometry(a.Page, a.Run.Name).Margin.Top + slice.Inset + a.Y - slice.Top) * PointsPerPixel);
        }

        List<StringAssignment> strings = Strings(runs);
        Tagging? tags = options.Tagged ? new Tagging(pdf, doc.Html.GetAttribute("lang")) : null;
        running.Place(runs);
        // Fixed boxes in z-index order, tree order for equal values; auto counts as zero.
        List<BoxFragment> fixedBoxes = [.. runs.SelectMany(r => r.Engine.Fixed).OrderBy(f => Painter.ZIndex(f) ?? 0)];
        List<BoxFragment> fixedBelow = [.. fixedBoxes.Where(f => Painter.ZIndex(f) < 0)];
        List<BoxFragment> fixedAbove = [.. fixedBoxes.Where(f => !(Painter.ZIndex(f) < 0))];
        (CssColor canvas, Box? propagated) = Painter.CanvasBackground(runs[0].Layout);
        var indexes = new Dictionary<PageRun, PaintIndex>();
        for (int i = 0; i < pages.Count; i++)
        {
            (PageRun run, Paginator.Slice slice) = pageRuns[i];
            BoxFragment layout = run.Layout;
            if (!indexes.TryGetValue(run, out PaintIndex? index)) indexes[run] = index = new PaintIndex(layout);
            PageGeometry g = master.Geometry(i, run.Name);
            using PdfGraphics graphics = pages[i].CreateGraphics();
            double pageWidth = g.Width * PointsPerPixel, pageHeight = g.Height * PointsPerPixel;
            CssColor pageColor = g.Style.Color("background-color");
            if ((pageColor.A > 0 || canvas.A > 0) && tags is not null) graphics.BeginArtifact();
            if (pageColor.A > 0) graphics.DrawRectangle(null, new PdfSolidBrush(Painter.ToPdf(pageColor)), 0, 0, pageWidth, pageHeight);
            if (canvas.A > 0) graphics.DrawRectangle(null, new PdfSolidBrush(Painter.ToPdf(canvas)), 0, 0, pageWidth, pageHeight);
            if ((pageColor.A > 0 || canvas.A > 0) && tags is not null) graphics.EndMarkedContent();

            // position: fixed repeats on every page, placed against the page area (CSS 2.1, 9.6.1).
            void PaintFixed(IEnumerable<BoxFragment> boxes)
            {
                foreach (BoxFragment fixedBox in boxes)
                {
                    using (graphics.Save())
                    {
                        graphics.TranslateTransform(g.Margin.Left * PointsPerPixel, g.Margin.Top * PointsPerPixel);
                        graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                        new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, Anchor, propagated, resources.LoadImage, tags, artifact: true).Paint(fixedBox);
                    }
                }
            }

            // The page's content, or with a phase only the root's own background and borders, or the rest.
            void PaintContent(Painter.Phase phase)
            {
                using (graphics.Save())
                {
                    graphics.TranslateTransform(g.Margin.Left * PointsPerPixel, g.Margin.Top * PointsPerPixel);
                    graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                    if (phase != Painter.Phase.Background) PaintHeaders();
                    graphics.TranslateTransform(0, slice.Inset - slice.Top);

                    // The page shows its slice; content may overflow sideways into the margins.
                    graphics.IntersectClip(-g.Margin.Left, slice.Top, g.Width, slice.Bottom - slice.Top);
                    new Painter(graphics, slice.Top, slice.Bottom, Anchor, propagated, resources.LoadImage, tags, index: index).Paint(layout, phase);
                }
            }

            // Table headers repeated at the top of a page that continues a table.
            void PaintHeaders()
            {
                double offset = 0;
                foreach (Paginator.RepeatedHeader header in slice.Headers)
                {
                    using (graphics.Save())
                    {
                        graphics.TranslateTransform(0, offset - header.Header.Y);
                        // A header repeated on a later page is pagination, not content (ISO 32000-1, 14.8.2.2).
                        new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, Anchor, propagated, resources.LoadImage, tags, artifact: true).Paint(header.Header);
                    }

                    offset += header.Advance;
                }
            }

            // Fixed boxes are in the root stacking context: a negative z-index paints over the root's
            // background and borders and under the page's content, the rest over it (CSS 2.1, Appendix E).
            if (fixedBelow.Count > 0)
            {
                PaintContent(Painter.Phase.Background);
                PaintFixed(fixedBelow);
                PaintContent(Painter.Phase.Foreground);
            }
            else
            {
                PaintContent(Painter.Phase.All);
            }

            PaintFixed(fixedAbove);

            int page = i;
            // Running headers and footers are pagination artifacts, as readers skip them.
            if (tags is not null) graphics.BeginArtifact();
            MarginBoxes(graphics, master, run.Engine, i, pages.Count, run.Name, (name, policy) => StringValue(strings, name, policy, page), running);
            if (tags is not null) graphics.EndMarkedContent();
        }

        if (options.Bookmarks) Bookmarks(pdf, runs, pages);
        tags?.Finish();
        return pdf;
    }

    /// <summary>A stretch of the document laid out for one page name: its canvas and the pages it fills.</summary>
    private sealed record PageRun(BoxFragment Layout, LayoutEngine Engine, List<Paginator.Slice> Slices, string? Name, int FirstPage);

    /// <summary>
    /// Lays out the document in runs of one page name each (CSS Paged Media 3, 8): where the used
    /// page name changes between two boxes of the block flow, at any depth, a new page starts, and
    /// the run after the break is laid out at its page type's width, so a landscape section gets
    /// the landscape width.
    /// </summary>
    private static List<PageRun> LayoutRuns(BlockBox root, PageMaster master, FontResolver fonts, int maxPages)
    {
        var named = new NamedPages(root);
        var runs = new List<PageRun>();
        int first = 0;
        for (int i = 0; i < named.Count; i++)
        {
            using (named.Keep(i))
            {
                PageRun run = LayoutRun(root, master, fonts, first, maxPages - first, named.Name(i), named.First(i), named.Last(i));
                runs.Add(run);
                first += run.Slices.Count;
            }
        }

        return runs;
    }

    /// <param name="start">The box after the break the run starts at, or null for the first run.</param>
    /// <param name="end">The box before the break the run ends at, or null for the last run.</param>
    private static PageRun LayoutRun(BlockBox root, PageMaster master, FontResolver fonts, int first, int maxPages, string? name, Box? start, Box? end)
    {
        PageGeometry g = master.Geometry(first, name);
        var engine = new LayoutEngine(fonts);
        BoxFragment layout = engine.Layout(root, g.ContentWidth, g.ContentHeight);
        BoxFragment? startFragment = start is null ? null : NamedPages.Find(layout, start);
        BoxFragment? endFragment = end is null ? null : NamedPages.Find(layout, end);
        double? top = startFragment is null ? null : NamedPages.Start(startFragment);
        List<Paginator.Slice> slices = Paginator.Paginate(layout, i => master.Geometry(first + i, name).ContentHeight, maxPages, top, endFragment);
        return new PageRun(layout, engine, slices, name, first);
    }

    /// <summary>Converts an HTML string to PDF bytes.</summary>
    /// <param name="html">The HTML.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    /// <exception cref="HtmlConversionException">The document needs more pages than <see cref="HtmlConverterOptions.MaxPages"/>.</exception>
    public static byte[] ConvertToPdf(string html, HtmlConverterOptions? options = null) => ConvertToDocument(html, options).ToArray(SaveOptions(options));

    /// <summary>Converts an HTML string and writes the PDF to a stream.</summary>
    /// <param name="html">The HTML.</param>
    /// <param name="output">Where the PDF is written.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    /// <exception cref="HtmlConversionException">The document needs more pages than <see cref="HtmlConverterOptions.MaxPages"/>.</exception>
    public static void ConvertToPdf(string html, Stream output, HtmlConverterOptions? options = null)
    {
        Compat.ThrowIfNull(output, nameof(output));
        ConvertToDocument(html, options).Save(output, SaveOptions(options));
    }

    /// <summary>
    /// Converts an HTML string and writes the PDF to a stream, asynchronously: no thread waits
    /// while the document is laid out, and <paramref name="output"/> is only written with
    /// asynchronous calls, as response bodies in ASP.NET Core and Azure Functions require.
    /// </summary>
    /// <remarks>
    /// Layout runs on a thread of its own, as <see cref="ConvertToDocument"/> does, and the PDF is
    /// then saved with <see cref="PdfDocument.SaveAsync(Stream, PdfSaveOptions?, CancellationToken)"/>,
    /// with <see cref="HtmlConverterOptions.SaveOptions"/>.
    /// </remarks>
    /// <param name="html">The HTML.</param>
    /// <param name="output">Where the PDF is written, from its current position. It is flushed, not closed.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    /// <param name="cancellationToken">
    /// Observed before layout starts and while the PDF is saved; layout itself, bounded by
    /// <see cref="HtmlConverterOptions.MaxPages"/>, runs to its end. When it fires, the call ends
    /// with <see cref="OperationCanceledException"/> and what reached the stream is not a complete file.
    /// </param>
    /// <exception cref="HtmlConversionException">The document needs more pages than <see cref="HtmlConverterOptions.MaxPages"/>.</exception>
    public static async Task ConvertToPdfAsync(string html, Stream output, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(html, nameof(html));
        Compat.ThrowIfNull(output, nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        HtmlConverterOptions effective = options ?? new HtmlConverterOptions();
        PdfDocument doc = await ConvertOnWorkerAsync(html, effective).ConfigureAwait(false);
        await doc.SaveAsync(output, SaveOptions(options), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lays out on a thread with a deep stack, completing the task there, so the caller's thread is free meanwhile.</summary>
    private static Task<PdfDocument> ConvertOnWorkerAsync(string html, HtmlConverterOptions options)
    {
        var done = new TaskCompletionSource<PdfDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(
            () =>
            {
                try
                {
                    done.SetResult(Convert(html, options));
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            },
            StackSize)
        {
            Name = "PdfNetStack.Html",
            IsBackground = true,
        };
        worker.Start();
        return done.Task;
    }

    // The converter draws every page itself, so the optimizer's rewrite of it needs no proof.
    private static readonly PdfSaveOptions Unverified = new() { VerifyOptimization = false };

    private static PdfSaveOptions SaveOptions(HtmlConverterOptions? options) => options?.SaveOptions ?? Unverified;

    /// <summary>
    /// Converts an HTML file. Relative URLs resolve against the file's folder, and files in that
    /// folder may load, unless <paramref name="options"/> sets its own base or loader.
    /// </summary>
    /// <param name="path">The HTML file, read as UTF-8 unless it starts with a byte order mark.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    /// <exception cref="HtmlConversionException">The document needs more pages than <see cref="HtmlConverterOptions.MaxPages"/>.</exception>
    public static PdfDocument ConvertFileToDocument(string path, HtmlConverterOptions? options = null)
    {
        Compat.ThrowIfNull(path, nameof(path));
        string full = Path.GetFullPath(path);
        string html = File.ReadAllText(full, Encoding.UTF8);
        options ??= new HtmlConverterOptions();
        if (options.BaseUri is null)
        {
            options = new HtmlConverterOptions
            {
                BaseUri = new Uri(full),
                ResourceLoader = options.ResourceLoader,
                Fonts = options.Fonts,
                UseSystemFonts = options.UseSystemFonts,
                PageStyle = options.PageStyle,
                StyleSheet = options.StyleSheet,
                Bookmarks = options.Bookmarks,
                Tagged = options.Tagged,
                SaveOptions = options.SaveOptions,
                Hyphenators = options.Hyphenators,
                MaxPages = options.MaxPages,
            };
        }

        return ConvertToDocument(html, options);
    }

    private static void Metadata(PdfDocument pdf, HtmlDocument doc)
    {
        if (doc.Title is { Length: > 0 } title) pdf.Info.Title = title;
        foreach (HtmlElement meta in doc.Head.ChildElements.Where(e => e.Name == "meta"))
        {
            string? content = meta.GetAttribute("content");
            if (string.IsNullOrWhiteSpace(content)) continue;
            switch (meta.GetAttribute("name")?.ToLowerInvariant())
            {
                case "author":
                    pdf.Info.Author = content;
                    break;
                case "description":
                    pdf.Info.Subject = content;
                    break;
                case "keywords":
                    pdf.Info.Keywords = content;
                    break;
                case "generator":
                    pdf.Info.Creator = content;
                    break;
            }
        }

        if (doc.Html.GetAttribute("lang") is { Length: > 0 } lang) pdf.Language = lang;
    }

    private static int PageOf(List<Paginator.Slice> slices, double y)
    {
        for (int i = 0; i < slices.Count; i++)
        {
            if (y < slices[i].Bottom - 0.01) return i;
        }

        return slices.Count - 1;
    }

    /// <summary>Every fragment in tree order, depth first, without an iterator per level.</summary>
    private static IEnumerable<Fragment> Walk(Fragment root)
    {
        var stack = new Stack<Fragment>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Fragment f = stack.Pop();
            yield return f;
            List<Fragment>? children = f switch
            {
                BoxFragment b => b.Children,
                LineFragment l => l.Items,
                _ => null,
            };
            if (children is null) continue;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>Where each element id first appears, for internal links.</summary>
    private static Dictionary<string, (PageRun Run, int Page, double Y)> Anchors(List<PageRun> runs)
    {
        var anchors = new Dictionary<string, (PageRun, int, double)>(StringComparer.Ordinal);
        foreach (PageRun run in runs)
        {
            foreach (Fragment f in Walk(run.Layout))
            {
                HtmlElement? element = f switch
                {
                    BoxFragment b => b.Box.Element,
                    InlineFragment i => i.Box.Element,
                    _ => null,
                };
                string? id = element?.GetAttribute("id") ?? (element?.Name == "a" ? element.GetAttribute("name") : null);
                if (string.IsNullOrEmpty(id) || anchors.ContainsKey(id!)) continue;
                int slice = PageOf(run.Slices, f.Y);
                anchors[id!] = (run, run.FirstPage + slice, Math.Max(run.Slices[slice].Top, f.Y));
            }
        }

        return anchors;
    }

    /// <summary>Bookmarks from bookmark-level (headings in the default style sheet), nested by level.</summary>
    private static void Bookmarks(PdfDocument pdf, List<PageRun> runs, List<PdfPage> pages)
    {
        var stack = new List<(int Level, PdfOutline Outline)>();
        var seen = new HashSet<Box>();
        foreach ((PageRun run, BoxFragment f) in runs.SelectMany(r => Walk(r.Layout).OfType<BoxFragment>().Select(f => (r, f))))
        {
            List<CssValue> levelValue = f.Style["bookmark-level"];
            if (levelValue.Count != 1 || !levelValue[0].IsToken || levelValue[0].Token.Type != CssTokenType.Number || !seen.Add(f.Box)) continue;
            int level = Math.Max(1, (int)levelValue[0].Token.Number);
            string label = BookmarkLabel(f);
            if (label.Length == 0) continue;

            PdfPage page = pages[run.FirstPage + PageOf(run.Slices, f.Y)];
            while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
            PdfOutline outline = stack.Count == 0 ? pdf.Outlines.Add(label, page) : stack[^1].Outline.Add(label, page);
            stack.Add((level, outline));
        }
    }

    private static string BookmarkLabel(BoxFragment f)
    {
        List<CssValue> label = f.Style["bookmark-label"];
        if (label.All(v => v.IsWhitespace || (v.IsToken && v.Token.Type == CssTokenType.String)) && label.Any(v => !v.IsWhitespace))
        {
            return string.Concat(label.Where(v => !v.IsWhitespace).Select(v => v.Token.Value)).Trim();
        }

        // content(text), the default: the element's text with white space collapsed.
        return Collapse(f.Box.Element?.TextContent ?? "");
    }

    /// <summary>Running headers and footers (CSS Paged Media 3, 5): text with page counters, in the page margins.</summary>
    private static void MarginBoxes(PdfGraphics graphics, PageMaster master, LayoutEngine engine, int index, int count, string? pageName, Func<string, string, string> strings, RunningElements running)
    {
        PageGeometry g = master.Geometry(index, pageName);
        List<(string Name, ComputedStyle Style, List<CssValue> Content)> boxes = master.MarginBoxes(index, pageName);
        var present = new HashSet<string>(boxes.Select(b => b.Name), StringComparer.Ordinal);
        foreach ((string name, ComputedStyle style, List<CssValue> content) in boxes)
        {
            (double x, double y, double w, double h)? area = MarginArea(name, g, present);
            if (area is not (double ax, double ay, double aw, double ah) || aw <= 0 || ah <= 0) continue;

            // content: element(name) shows a running element, laid out at the box's width.
            if (content.FirstOrDefault(v => v.Function == "element") is CssValue element)
            {
                running.Draw(graphics, element, style, index, count, ax, ay, aw, ah);
                continue;
            }

            string text = MarginText(content, index + 1, count, strings);
            if (text.Length == 0) continue;

            var box = new TextBox(style, text);
            var inline = new InlineLayout(engine, style, null, [box], aw);
            if (inline.IsEmpty) continue;
            List<LineFragment> lines = inline.Layout(ax, 0, out double height);

            // Vertically centred in its area, as margin boxes are by default.
            double top = ay + ((ah - height) / 2);
            foreach (LineFragment line in lines) line.Translate(0, top);
            using (graphics.Save())
            {
                graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, _ => null, null).PaintLines(lines);
            }
        }
    }

    /// <summary>
    /// Running elements (CSS Generated Content for Paged Media 3, 2.3): elements taken out of the
    /// flow with position: running(name), shown in margin boxes with content: element(name). A
    /// block-level element shows from the page its anchor falls on; element() picks among those of
    /// a page as string() does. An inline one, which has no anchor, shows on every page.
    /// </summary>
    private sealed class RunningElements(Dictionary<string, Box> unanchored, List<(TextBox Box, string Counter, string Style)> pageCounters, StyleResolver resolver, FontResolver fonts, Func<string, HtmlImage?> images)
    {
        private readonly Dictionary<(Box, double, int), BoxFragment> _laidOut = [];
        private readonly Dictionary<Box, List<(TextBox Box, string Counter, string Style)>> _counters = [];
        private readonly List<(string Name, int Page, Box Box)> _placed = [];

        /// <summary>Finds every anchor, in document order, and the page it falls on.</summary>
        public void Place(List<PageRun> runs)
        {
            foreach (PageRun run in runs)
            {
                foreach (BoxFragment f in Walk(run.Layout).OfType<BoxFragment>())
                {
                    if (f.Box is RunningAnchorBox anchor) _placed.Add((anchor.Name, run.FirstPage + PageOf(run.Slices, f.Y), anchor.Running));
                }
            }
        }

        public void Draw(PdfGraphics graphics, CssValue element, ComputedStyle style, int page, int pages, double x, double y, double width, double height)
        {
            List<CssValue> args = [.. element.Children.Where(c => c.IsToken && c.Token.Type == CssTokenType.Ident)];
            if (args.Count == 0) return;
            string name = args[0].Token.Value, policy = args.Count > 1 ? args[1].Token.Value.ToLowerInvariant() : "first";
            Box? box = Choose(name, policy, page);
            if (box is null) return;

            // Page counters inside take this page's numbers, and the layout is then the page's own.
            List<(TextBox Box, string Counter, string Style)> counters = Counters(box);
            foreach ((TextBox text, string counter, string counterStyle) in counters) text.Text = CounterStyle.Format(counter == "page" ? page + 1 : pages, counterStyle);
            (Box, double, int) key = (box, width, counters.Count > 0 ? page : -1);
            if (!_laidOut.TryGetValue(key, out BoxFragment? fragment))
            {
                // An anonymous block in the margin box's style holds the element, whatever its display;
                // its own engine, since the page's keeps the state of the page's layout.
                var holder = new BlockBox(resolver.Anonymous(style, "block"), null) { IsAnonymous = true };
                holder.SetChildren([box]);
                fragment = new LayoutEngine(fonts).Layout(holder, width);
                _laidOut[key] = fragment;
            }

            // Vertically centred in its area, as margin boxes are by default.
            using (graphics.Save())
            {
                graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                graphics.TranslateTransform(x, y + ((height - fragment.Height) / 2));
                new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, _ => null, null, images).Paint(fragment);
            }
        }

        /// <summary>The page counters in an element, found once.</summary>
        private List<(TextBox Box, string Counter, string Style)> Counters(Box box)
        {
            if (_counters.TryGetValue(box, out List<(TextBox, string, string)>? found)) return found;
            var inside = new HashSet<Box>();
            var stack = new Stack<Box>([box]);
            while (stack.Count > 0)
            {
                Box b = stack.Pop();
                if (!inside.Add(b)) continue;
                foreach (Box c in b.Children) stack.Push(c);
                if (b is BlockBox { Marker: Box marker }) stack.Push(marker);
            }

            return _counters[box] = [.. pageCounters.Where(c => inside.Contains(c.Box))];
        }

        /// <summary>The element a page shows, chosen as for string(): first, start, last or first-except.</summary>
        private Box? Choose(string name, string policy, int page)
        {
            Box? before = null, first = null, last = null;
            bool any = false;
            foreach ((string n, int p, Box b) in _placed)
            {
                if (n != name) continue;
                any = true;
                if (p < page) before = b;
                else if (p == page) (first, last) = (first ?? b, b);
            }

            if (!any) return unanchored.TryGetValue(name, out Box? only) ? only : null;
            return policy switch
            {
                "start" => before ?? first,
                "last" => last ?? before,
                "first-except" => first is null ? before : null,
                _ => first ?? before,
            };
        }
    }

    /// <summary>Margin box content: strings, page counters and named strings from string-set.</summary>
    private static string MarginText(List<CssValue> content, int page, int pages, Func<string, string, string> strings)
    {
        var sb = new StringBuilder();
        foreach (CssValue v in content)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.String)
            {
                sb.Append(v.Token.Value);
            }
            else if (v.Function == "counter")
            {
                List<CssValue> args = v.Children.Where(c => c.IsToken && c.Token.Type == CssTokenType.Ident).ToList();
                if (args.Count == 0) continue;
                string counter = args[0].Token.Value;
                string counterStyle = args.Count > 1 ? args[1].Token.Value : "decimal";
                if (counter == "page") sb.Append(CounterStyle.Format(page, counterStyle));
                else if (counter == "pages") sb.Append(CounterStyle.Format(pages, counterStyle));
            }
            else if (v.Function == "string")
            {
                List<CssValue> args = v.Children.Where(c => c.IsToken && c.Token.Type == CssTokenType.Ident).ToList();
                if (args.Count > 0) sb.Append(strings(args[0].Token.Value, args.Count > 1 ? args[1].Token.Value.ToLowerInvariant() : "first"));
            }
        }

        return sb.ToString();
    }

    /// <summary>A string-set assignment (CSS Generated Content for Paged Media 3, 1): a named string's value from here on.</summary>
    private sealed record StringAssignment(string Name, int Page, string Value);

    /// <summary>Every string-set assignment, in document order, with the page it falls on.</summary>
    private static List<StringAssignment> Strings(List<PageRun> runs)
    {
        var result = new List<StringAssignment>();
        var seen = new HashSet<(Box, string)>();
        foreach (PageRun run in runs)
        {
            foreach (BoxFragment f in Walk(run.Layout).OfType<BoxFragment>())
            {
                List<CssValue> value = f.Style["string-set"];
                if (value.Count == 0 || (value.Count == 1 && value[0].IsIdent("none"))) continue;
                int page = run.FirstPage + PageOf(run.Slices, f.Y);
                foreach (List<CssValue> part in SplitCommas(value))
                {
                    List<CssValue> items = part.Where(v => !v.IsWhitespace).ToList();
                    if (items.Count < 2 || !items[0].IsToken || items[0].Token.Type != CssTokenType.Ident) continue;
                    string name = items[0].Token.Value;

                    // A box split across pages, or a table and its wrapper, assigns once.
                    if (!seen.Add((f.Box is TableWrapperBox wrapper ? wrapper.Children[0] : f.Box, name))) continue;
                    var text = new StringBuilder();
                    foreach (CssValue v in items.Skip(1))
                    {
                        if (v.IsToken && v.Token.Type == CssTokenType.String) text.Append(v.Token.Value);
                        else if (v.Function == "content") text.Append(Collapse(f.Box.Element?.TextContent ?? ""));
                        else if (v.Function == "attr" && v.Children.FirstOrDefault(c => !c.IsWhitespace) is { IsToken: true } attribute)
                        {
                            text.Append(f.Box.Element?.GetAttribute(attribute.Token.Value.ToLowerInvariant()) ?? "");
                        }
                    }

                    result.Add(new StringAssignment(name, page, text.ToString()));
                }
            }
        }

        return result;
    }

    private static IEnumerable<List<CssValue>> SplitCommas(List<CssValue> value)
    {
        var part = new List<CssValue>();
        foreach (CssValue v in value)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma)
            {
                yield return part;
                part = [];
            }
            else
            {
                part.Add(v);
            }
        }

        yield return part;
    }

    private static string Collapse(string text) => string.Join(" ", text.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A named string on a page. first: the first value set on the page, else the one carried in.
    /// start: the value carried into the page. last: the last value set on the page.
    /// first-except: empty on a page that sets it, else the value carried in.
    /// </summary>
    private static string StringValue(List<StringAssignment> strings, string name, string policy, int page)
    {
        string? before = null, first = null, last = null;
        foreach (StringAssignment s in strings)
        {
            if (s.Name != name) continue;
            if (s.Page < page) before = s.Value;
            else if (s.Page == page) (first, last) = (first ?? s.Value, s.Value);
        }

        return policy switch
        {
            "start" => before ?? first ?? "",
            "last" => last ?? before ?? "",
            "first-except" => first is null ? before ?? "" : "",
            _ => first ?? before ?? "",
        };
    }

    /// <summary>
    /// The area of a margin box in page px. The three boxes along a side share it as CSS Paged
    /// Media 3, 5.3.2 resolves them for boxes without set widths: a center box alone takes the whole
    /// side, beside others a third; without a center box, a start or end box alone takes the whole
    /// side and two take half each.
    /// </summary>
    private static (double X, double Y, double W, double H)? MarginArea(string name, PageGeometry g, ICollection<string> present)
    {
        Edges m = g.Margin;
        double cw = g.Width - m.Horizontal, ch = g.Height - m.Vertical;
        (double Offset, double Length) Slot(string side, string start, string middle, string end, string slot, double span)
        {
            bool hasStart = present.Contains(side + start), hasMiddle = present.Contains(side + middle), hasEnd = present.Contains(side + end);
            if (hasMiddle)
            {
                if (!hasStart && !hasEnd) return slot == middle ? (0, span) : (0, 0);
                return slot == start ? (0, span / 3) : slot == middle ? (span / 3, span / 3) : (2 * span / 3, span / 3);
            }

            if (hasStart && hasEnd) return slot == start ? (0, span / 2) : (span / 2, span / 2);
            return (0, span);
        }

        (double Offset, double Length) Across(string side, string slot) => Slot(side, "-left", "-center", "-right", slot, cw);
        (double Offset, double Length) Down(string side, string slot) => Slot(side, "-top", "-middle", "-bottom", slot, ch);
        switch (name)
        {
            case "top-left-corner": return (0, 0, m.Left, m.Top);
            case "top-right-corner": return (g.Width - m.Right, 0, m.Right, m.Top);
            case "bottom-left-corner": return (0, g.Height - m.Bottom, m.Left, m.Bottom);
            case "bottom-right-corner": return (g.Width - m.Right, g.Height - m.Bottom, m.Right, m.Bottom);
        }

        int dash = name.IndexOf('-');
        if (dash < 0) return null;
        string side = name.Substring(0, dash), slot = name.Substring(dash);
        switch (side)
        {
            case "top" or "bottom":
            {
                (double offset, double length) = Across(side, slot);
                return (m.Left + offset, side == "top" ? 0 : g.Height - m.Bottom, length, side == "top" ? m.Top : m.Bottom);
            }

            case "left" or "right":
            {
                (double offset, double length) = Down(side, slot);
                return (side == "left" ? 0 : g.Width - m.Right, m.Top + offset, side == "left" ? m.Left : m.Right, length);
            }

            default:
                return null;
        }
    }
}
