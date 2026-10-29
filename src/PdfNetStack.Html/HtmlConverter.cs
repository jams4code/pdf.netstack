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

        var fonts = new FontResolver(options.Fonts, options.UseSystemFonts);
        fonts.AddFontFaces(resolver.FontFaces, resources.Load);
        BlockBox root = new BoxBuilder(resolver, resources.LoadImage).Build(doc);
        var master = new PageMaster(resolver, root.Style, options.PageStyle);
        List<PageRun> runs = LayoutRuns(root, master, fonts);
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
        List<BoxFragment> fixedBoxes = [.. runs.SelectMany(r => r.Engine.Fixed)];
        (CssColor canvas, Box? propagated) = Painter.CanvasBackground(runs[0].Layout);
        for (int i = 0; i < pages.Count; i++)
        {
            (PageRun run, Paginator.Slice slice) = pageRuns[i];
            BoxFragment layout = run.Layout;
            PageGeometry g = master.Geometry(i, run.Name);
            using PdfGraphics graphics = pages[i].CreateGraphics();
            double pageWidth = g.Width * PointsPerPixel, pageHeight = g.Height * PointsPerPixel;
            CssColor pageColor = g.Style.Color("background-color");
            if (pageColor.A > 0) graphics.DrawRectangle(null, new PdfSolidBrush(Painter.ToPdf(pageColor)), 0, 0, pageWidth, pageHeight);
            if (canvas.A > 0) graphics.DrawRectangle(null, new PdfSolidBrush(Painter.ToPdf(canvas)), 0, 0, pageWidth, pageHeight);

            using (graphics.Save())
            {
                graphics.TranslateTransform(g.Margin.Left * PointsPerPixel, g.Margin.Top * PointsPerPixel);
                graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);

                // Table headers repeated at the top of a page that continues a table.
                double offset = 0;
                foreach (Paginator.RepeatedHeader header in slice.Headers)
                {
                    using (graphics.Save())
                    {
                        graphics.TranslateTransform(0, offset - header.Header.Y);
                        new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, Anchor, propagated, resources.LoadImage).Paint(header.Header);
                    }

                    offset += header.Advance;
                }

                graphics.TranslateTransform(0, slice.Inset - slice.Top);

                // The page shows its slice; content may overflow sideways into the margins.
                graphics.IntersectClip(-g.Margin.Left, slice.Top, g.Width, slice.Bottom - slice.Top);
                new Painter(graphics, slice.Top, slice.Bottom, Anchor, propagated, resources.LoadImage).Paint(layout);
            }

            // position: fixed repeats on every page, placed against the page area (CSS 2.1, 9.6.1).
            foreach (BoxFragment fixedBox in fixedBoxes)
            {
                using (graphics.Save())
                {
                    graphics.TranslateTransform(g.Margin.Left * PointsPerPixel, g.Margin.Top * PointsPerPixel);
                    graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                    new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, Anchor, propagated, resources.LoadImage).Paint(fixedBox);
                }
            }

            int page = i;
            MarginBoxes(graphics, master, run.Engine, i, pages.Count, run.Name, (name, policy) => StringValue(strings, name, policy, page));
        }

        if (options.Bookmarks) Bookmarks(pdf, runs, pages);
        return pdf;
    }

    /// <summary>A stretch of the document laid out for one page name: its canvas and the pages it fills.</summary>
    private sealed record PageRun(BoxFragment Layout, LayoutEngine Engine, List<Paginator.Slice> Slices, string? Name, int FirstPage);

    /// <summary>
    /// Lays out the document in runs of one page name each (CSS Paged Media 3, 8): a child of the
    /// body whose page differs from the child before it starts a new page, and is laid out at that
    /// page type's width, so a landscape section gets the landscape width.
    /// </summary>
    private static List<PageRun> LayoutRuns(BlockBox root, PageMaster master, FontResolver fonts)
    {
        Box? body = root.Children.FirstOrDefault(c => c.Element?.Name == "body");
        var groups = new List<(string? Name, List<Box> Boxes)>();
        foreach (Box child in body?.Children ?? [])
        {
            string? name = PageName(child);
            if (groups.Count == 0 || groups[^1].Name != name) groups.Add((name, []));
            groups[^1].Boxes.Add(child);
        }

        if (body is null || groups.Count <= 1) return [LayoutRun(root, master, fonts, 0, groups.Count == 1 ? groups[0].Name : null)];

        var runs = new List<PageRun>();
        List<Box> all = [.. body.Children];
        try
        {
            int first = 0;
            foreach ((string? name, List<Box> boxes) in groups)
            {
                body.SetChildren(boxes);
                PageRun run = LayoutRun(root, master, fonts, first, name);
                runs.Add(run);
                first += run.Slices.Count;
            }
        }
        finally
        {
            body.SetChildren(all);
        }

        return runs;
    }

    private static PageRun LayoutRun(BlockBox root, PageMaster master, FontResolver fonts, int first, string? name)
    {
        PageGeometry g = master.Geometry(first, name);
        var engine = new LayoutEngine(fonts);
        BoxFragment layout = engine.Layout(root, g.ContentWidth, g.ContentHeight);
        List<Paginator.Slice> slices = Paginator.Paginate(layout, i => master.Geometry(first + i, name).ContentHeight);
        return new PageRun(layout, engine, slices, name, first);
    }

    /// <summary>The page property as a name; auto takes the body's page, which has none.</summary>
    private static string? PageName(Box box)
    {
        List<CssValue> v = box.Style["page"];
        return v.Count == 1 && v[0].IsToken && v[0].Token.Type == CssTokenType.Ident && !v[0].IsIdent("auto") ? v[0].Token.Value : null;
    }

    /// <summary>Converts an HTML string to PDF bytes.</summary>
    /// <param name="html">The HTML.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    public static byte[] ConvertToPdf(string html, HtmlConverterOptions? options = null) => ConvertToDocument(html, options).ToArray();

    /// <summary>Converts an HTML string and writes the PDF to a stream.</summary>
    /// <param name="html">The HTML.</param>
    /// <param name="output">Where the PDF is written.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
    public static void ConvertToPdf(string html, Stream output, HtmlConverterOptions? options = null)
    {
        Compat.ThrowIfNull(output, nameof(output));
        ConvertToDocument(html, options).Save(output);
    }

    /// <summary>
    /// Converts an HTML file. Relative URLs resolve against the file's folder, and files in that
    /// folder may load, unless <paramref name="options"/> sets its own base or loader.
    /// </summary>
    /// <param name="path">The HTML file, read as UTF-8 unless it starts with a byte order mark.</param>
    /// <param name="options">Page size, fonts and how resources load; defaults when null.</param>
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

    private static IEnumerable<Fragment> Walk(Fragment f)
    {
        yield return f;
        IEnumerable<Fragment> children = f switch
        {
            BoxFragment b => b.Children,
            LineFragment l => l.Items,
            _ => [],
        };
        foreach (Fragment c in children)
        {
            foreach (Fragment d in Walk(c)) yield return d;
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
    private static void MarginBoxes(PdfGraphics graphics, PageMaster master, LayoutEngine engine, int index, int count, string? pageName, Func<string, string, string> strings)
    {
        PageGeometry g = master.Geometry(index, pageName);
        foreach ((string name, ComputedStyle style, List<CssValue> content) in master.MarginBoxes(index, pageName))
        {
            string text = MarginText(content, index + 1, count, strings);
            if (text.Length == 0) continue;
            (double x, double y, double w, double h)? area = MarginArea(name, g);
            if (area is not (double ax, double ay, double aw, double ah) || aw <= 0 || ah <= 0) continue;

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

    /// <summary>The area of a margin box in page px; the three boxes of a side share it evenly.</summary>
    private static (double X, double Y, double W, double H)? MarginArea(string name, PageGeometry g)
    {
        Edges m = g.Margin;
        double cw = g.Width - m.Horizontal, ch = g.Height - m.Vertical;
        double third = cw / 3, thirdH = ch / 3;
        return name switch
        {
            "top-left-corner" => (0, 0, m.Left, m.Top),
            "top-left" => (m.Left, 0, third, m.Top),
            "top-center" => (m.Left + third, 0, third, m.Top),
            "top-right" => (m.Left + (2 * third), 0, third, m.Top),
            "top-right-corner" => (g.Width - m.Right, 0, m.Right, m.Top),
            "bottom-left-corner" => (0, g.Height - m.Bottom, m.Left, m.Bottom),
            "bottom-left" => (m.Left, g.Height - m.Bottom, third, m.Bottom),
            "bottom-center" => (m.Left + third, g.Height - m.Bottom, third, m.Bottom),
            "bottom-right" => (m.Left + (2 * third), g.Height - m.Bottom, third, m.Bottom),
            "bottom-right-corner" => (g.Width - m.Right, g.Height - m.Bottom, m.Right, m.Bottom),
            "left-top" => (0, m.Top, m.Left, thirdH),
            "left-middle" => (0, m.Top + thirdH, m.Left, thirdH),
            "left-bottom" => (0, m.Top + (2 * thirdH), m.Left, thirdH),
            "right-top" => (g.Width - m.Right, m.Top, m.Right, thirdH),
            "right-middle" => (g.Width - m.Right, m.Top + thirdH, m.Right, thirdH),
            "right-bottom" => (g.Width - m.Right, m.Top + (2 * thirdH), m.Right, thirdH),
            _ => null,
        };
    }
}
