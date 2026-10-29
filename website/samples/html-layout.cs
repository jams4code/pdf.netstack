#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// preview: html-layout layout.pdf 0.75
using PdfNetStack;
using PdfNetStack.Html;

#region css
const string Css = """
    :root { --accent: oklch(0.55 0.2 260); --gap: 10pt }
    body { font-family: sans-serif; font-size: 9.5pt; color: #0f172a }
    h1 { font-size: clamp(16pt, 4vw, 24pt); color: var(--accent); margin: 0 0 8pt }

    .page { display: grid; grid-template-columns: 140pt 1fr; grid-template-areas: "side main" "foot foot";
            gap: var(--gap) }
    aside { grid-area: side; padding: 8pt; border-radius: 6pt;
            background: radial-gradient(circle at top left, #dbeafe, #ffffff) }
    main { grid-area: main }
    footer { grid-area: foot; border-top: 1pt dashed #94a3b8; padding-top: 4pt; color: #64748b }

    .cards { display: flex; flex-wrap: wrap; gap: var(--gap) }
    .card { flex: 1 1 120pt; padding: 8pt; border: 1pt solid #cbd5e1; border-radius: 6pt;
            box-shadow: 0 2pt 4pt rgb(15 23 42 / 0.15) }
    .card:nth-child(2 of .card) { border: 3pt double var(--accent) }
    .card :is(h3, h4) { margin: 0 0 4pt; text-transform: uppercase; letter-spacing: 0.05em }

    .figure { float: right; width: 90pt; margin: 0 0 6pt 8pt; position: relative }
    .badge { position: absolute; top: -6pt; right: -6pt; padding: 2pt 4pt; color: white;
             background: #dc2626; border-radius: 4pt; transform: rotate(8deg) }
    .note { width: calc(100% - 20pt); text-decoration: underline dotted #f59e0b }

    ol.steps { counter-reset: step; list-style: none; padding: 0 }
    ol.steps li { counter-increment: step; margin-bottom: 3pt }
    ol.steps li::before { content: "Step " counter(step) ": "; font-weight: bold; color: var(--accent) }
    li:not(:last-child) { color: #334155 }
    """;
#endregion

string html = $$"""
    <html lang="en"><head><style>{{Css}}</style></head><body>
    <div class="page">
      <aside><h4>Contents</h4><ul><li>Cards</li><li>Floats</li><li>Steps</li></ul></aside>
      <main>
        <h1>Layout in CSS</h1>
        <div class="cards">
          <div class="card"><h3>Flexbox</h3>Wrap, gap, grow and shrink.</div>
          <div class="card"><h3>Grid</h3>Tracks, fr units and named areas.</div>
          <div class="card"><h3>Tables</h3>Both border models, spans.</div>
        </div>
        <p><span class="figure"><img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='90' height='60'%3E%3Crect width='90' height='60' rx='6' fill='%23bfdbfe'/%3E%3C/svg%3E" alt="Chart placeholder"><span class="badge">New</span></span>
        Text wraps around a floated box. The badge is positioned against it and rotated with a
        transform. Lines break at Unicode line break opportunities and the paragraph continues
        below the float once it ends, the way a browser lays it out for print.</p>
        <p class="note">A width computed with calc() and a dotted underline.</p>
        <ol class="steps"><li>Write the HTML</li><li>Add the style sheet</li><li>Convert to PDF</li></ol>
      </main>
      <footer>Generated with PdfNetStack.Html</footer>
    </div>
    </body></html>
    """;

PdfDocument doc = HtmlConverter.ConvertToDocument(html, new HtmlConverterOptions { UseSystemFonts = false });
doc.Save("layout.pdf");
Console.WriteLine(doc.Pages[0].ExtractText());
