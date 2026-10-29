---
title: Convert an HTML invoice to PDF in C#
description: Turn an HTML and CSS invoice into a PDF in .NET with page numbers in the margin, without a browser or Chromium. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: HTML invoice to PDF
---

Render an invoice written in HTML and CSS to a PDF, with a page footer.

## Short answer

Pass the HTML to `HtmlConverter.ConvertToPdf` from the `PdfNetStack.Html` package. The `@page` rule
sets the paper size and margins, and its `@bottom-center` margin box numbers the pages. No browser
runs, and nothing is fetched from the network.

```csharp sample="recipe-html-invoice-to-pdf.cs"
```

## Variations

- `HtmlConverter.ConvertFileToDocument("invoice.html")` reads a file, and its style sheets, images
  and fonts from the same folder.
- `ConvertToDocument` returns a `PdfDocument`, so the invoice can be signed, encrypted or saved as
  PDF/A before it is written.
- Fonts declared with `@font-face` are embedded. The generic families `serif`, `sans-serif` and
  `monospace` map to the standard PDF fonts, which need no embedding.

## Related

- [HTML to PDF](/guides/html-to-pdf/)
- [PDF/A-3 with an XML attachment](/recipes/pdf-a-3-xml-attachment/)
- API: [HtmlConverter](/api/pdfnetstack-html/htmlconverter/), [HtmlConverterOptions](/api/pdfnetstack-html/htmlconverteroptions/)
