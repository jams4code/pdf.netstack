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
        var engine = new LayoutEngine(fonts);
        BoxFragment layout = engine.Layout(root, master.Geometry(0).ContentWidth, master.Geometry(0).ContentHeight);
        List<Paginator.Slice> slices = Paginator.Paginate(layout, i => master.Geometry(i).ContentHeight);

        var pdf = PdfDocument.Create();
        Metadata(pdf, doc);
        var pages = new List<PdfPage>();
        for (int i = 0; i < slices.Count; i++)
        {
            PageGeometry g = master.Geometry(i);
            pages.Add(pdf.Pages.Add(new PdfRectangle(0, 0, g.Width * PointsPerPixel, g.Height * PointsPerPixel)));
        }

        Dictionary<string, (int Page, double Y)> anchors = Anchors(layout, slices);
        (PdfPage Page, double Top)? Anchor(string id) =>
            anchors.TryGetValue(id, out var a) ? (pages[a.Page], (master.Geometry(a.Page).Margin.Top + slices[a.Page].Inset + a.Y - slices[a.Page].Top) * PointsPerPixel) : null;

        (CssColor canvas, Box? propagated) = Painter.CanvasBackground(layout);
        for (int i = 0; i < slices.Count; i++)
        {
            PageGeometry g = master.Geometry(i);
            Paginator.Slice slice = slices[i];
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
            foreach (BoxFragment fixedBox in engine.Fixed)
            {
                using (graphics.Save())
                {
                    graphics.TranslateTransform(g.Margin.Left * PointsPerPixel, g.Margin.Top * PointsPerPixel);
                    graphics.ScaleTransform(PointsPerPixel, PointsPerPixel);
                    new Painter(graphics, double.MinValue / 4, double.MaxValue / 4, Anchor, propagated, resources.LoadImage).Paint(fixedBox);
                }
            }

            MarginBoxes(graphics, master, engine, i, slices.Count);
        }

        if (options.Bookmarks) Bookmarks(pdf, layout, slices, pages);
        return pdf;
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
    private static Dictionary<string, (int Page, double Y)> Anchors(BoxFragment layout, List<Paginator.Slice> slices)
    {
        var anchors = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
        foreach (Fragment f in Walk(layout))
        {
            HtmlElement? element = f switch
            {
                BoxFragment b => b.Box.Element,
                InlineFragment i => i.Box.Element,
                _ => null,
            };
            string? id = element?.GetAttribute("id") ?? (element?.Name == "a" ? element.GetAttribute("name") : null);
            if (string.IsNullOrEmpty(id) || anchors.ContainsKey(id!)) continue;
            int page = PageOf(slices, f.Y);
            anchors[id!] = (page, Math.Max(slices[page].Top, f.Y));
        }

        return anchors;
    }

    /// <summary>Bookmarks from bookmark-level (headings in the default style sheet), nested by level.</summary>
    private static void Bookmarks(PdfDocument pdf, BoxFragment layout, List<Paginator.Slice> slices, List<PdfPage> pages)
    {
        var stack = new List<(int Level, PdfOutline Outline)>();
        var seen = new HashSet<Box>();
        foreach (BoxFragment f in Walk(layout).OfType<BoxFragment>())
        {
            List<CssValue> levelValue = f.Style["bookmark-level"];
            if (levelValue.Count != 1 || !levelValue[0].IsToken || levelValue[0].Token.Type != CssTokenType.Number || !seen.Add(f.Box)) continue;
            int level = Math.Max(1, (int)levelValue[0].Token.Number);
            string label = BookmarkLabel(f);
            if (label.Length == 0) continue;

            PdfPage page = pages[PageOf(slices, f.Y)];
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
        string text = f.Box.Element?.TextContent ?? "";
        return string.Join(" ", text.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Running headers and footers (CSS Paged Media 3, 5): text with page counters, in the page margins.</summary>
    private static void MarginBoxes(PdfGraphics graphics, PageMaster master, LayoutEngine engine, int index, int count)
    {
        PageGeometry g = master.Geometry(index);
        foreach ((string name, ComputedStyle style, List<CssValue> content) in master.MarginBoxes(index))
        {
            string text = MarginText(content, index + 1, count);
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

    private static string MarginText(List<CssValue> content, int page, int pages)
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
        }

        return sb.ToString();
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
