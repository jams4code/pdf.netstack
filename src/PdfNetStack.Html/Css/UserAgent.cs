namespace PdfNetStack.Html.Css;

/// <summary>
/// Default styles for HTML elements. The values follow the rendering section of the HTML
/// Standard (WHATWG, CC BY 4.0), plus print defaults such as keeping headings with what follows.
/// </summary>
internal static class UserAgent
{
    public const string StyleSheet = """
        html, address, blockquote, body, center, dialog, div, figure, figcaption, footer, form, header,
        hr, legend, listing, main, p, plaintext, pre, search, xmp, article, aside, h1, h2, h3, h4, h5, h6,
        hgroup, nav, section, dd, dl, dt, menu, ol, ul, details, fieldset, optgroup, summary { display: block }
        head, script, style, title, meta, link, base, template, noscript, area, datalist, param, rp, noframes,
        [hidden]:not([hidden=until-found i]), input[type=hidden i] { display: none }

        body { margin: 8px }
        p, blockquote, figure, listing, plaintext, pre, xmp, dl { margin-top: 1em; margin-bottom: 1em }
        blockquote, figure { margin-left: 40px; margin-right: 40px }
        dd { margin-left: 40px }
        address, i, cite, dfn, em, var { font-style: italic }
        b, strong, th { font-weight: bold }
        center { text-align: center }

        h1 { margin-top: 0.67em; margin-bottom: 0.67em; font-size: 2em }
        h2 { margin-top: 0.83em; margin-bottom: 0.83em; font-size: 1.5em }
        h3 { margin-top: 1em; margin-bottom: 1em; font-size: 1.17em }
        h4 { margin-top: 1.33em; margin-bottom: 1.33em; font-size: 1em }
        h5 { margin-top: 1.67em; margin-bottom: 1.67em; font-size: 0.83em }
        h6 { margin-top: 2.33em; margin-bottom: 2.33em; font-size: 0.67em }
        h1, h2, h3, h4, h5, h6 { font-weight: bold; break-after: avoid; break-inside: avoid }

        ul, ol, menu { margin-top: 1em; margin-bottom: 1em; padding-left: 40px }
        ul, menu { list-style-type: disc }
        ol { list-style-type: decimal }
        :is(ul, ol, menu) :is(ul, ol, menu) { margin-top: 0; margin-bottom: 0 }
        :is(ul, ol, menu) :is(ul, menu) { list-style-type: circle }
        :is(ul, ol, menu) :is(ul, ol, menu) :is(ul, menu) { list-style-type: square }
        li { display: list-item; text-align: match-parent }
        ::marker { unicode-bidi: isolate; font-variant-numeric: tabular-nums }

        pre, listing, xmp, plaintext, code, kbd, samp, tt { font-family: monospace }
        pre, listing, xmp, plaintext { white-space: pre }
        textarea { white-space: pre-wrap }
        nobr { white-space: nowrap }
        small { font-size: smaller }
        big { font-size: larger }
        sub { vertical-align: sub; font-size: smaller }
        sup { vertical-align: super; font-size: smaller }
        u, ins { text-decoration: underline }
        s, strike, del { text-decoration: line-through }
        abbr[title], acronym[title] { text-decoration: dotted underline }
        :link { color: #0000ee; text-decoration: underline }
        mark { background-color: yellow; color: black }
        q::before { content: open-quote }
        q::after { content: close-quote }

        hr { color: gray; border-style: inset; border-width: 1px; margin: 0.5em auto; overflow: hidden }
        fieldset { margin-left: 2px; margin-right: 2px; border: 2px groove #c0c0c0; padding: 0.35em 0.75em 0.625em }
        legend { padding-left: 2px; padding-right: 2px }
        input, select, textarea, button { display: inline-block }

        table { display: table; border-spacing: 2px; border-collapse: separate; box-sizing: border-box; text-indent: 0 }
        caption { display: table-caption; text-align: center }
        colgroup { display: table-column-group }
        col { display: table-column }
        thead { display: table-header-group; vertical-align: middle }
        tbody { display: table-row-group; vertical-align: middle }
        tfoot { display: table-footer-group; vertical-align: middle }
        tr { display: table-row; vertical-align: inherit }
        td, th { display: table-cell; vertical-align: inherit; padding: 1px }
        th { text-align: center }
        img { break-inside: avoid }

        [dir=rtl i] { direction: rtl }
        [dir=ltr i] { direction: ltr }
        bdi, output, [dir] { unicode-bidi: isolate }
        bdo, bdo[dir] { unicode-bidi: isolate-override }
        """;
}
