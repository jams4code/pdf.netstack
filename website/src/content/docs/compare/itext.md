---
title: PdfNetStack and iText compared
description: 'iText Core next to PdfNetStack for PDF work in .NET: strengths, the AGPL and commercial licenses in plain words, features, and when to pick iText.'
draft: true
sidebar:
  label: iText
---

iText Core is a broad PDF library for .NET and Java with add-ons for HTML, OCR and redaction.
PdfNetStack is an MIT licensed .NET library centred on lossless compression and editing, with
drawing, forms, signatures and HTML to PDF in its next release. This page sets the two side by
side.

## What each does well

**iText.** Its README describes "a high-performance, battle-tested library that allows you to
create, adapt, inspect and maintain PDF documents": creation with a layout engine, manipulation
such as merging, digital signing, form creation and editing, PDF/A and PDF/UA, FIPS compliant
cryptography, barcodes and SVG ([repository](https://github.com/itext/itext-dotnet)). Add-ons
extend it: pdfHTML converts HTML and CSS to PDF, pdfOCR performs OCR, pdfSweep redacts and
pdfCalligraph adds scripts such as Arabic, Hebrew and Thai
([repository](https://github.com/itext/itext-dotnet)). Text extraction is part of the kernel API
([PdfTextExtractor](https://api.itextpdf.com/iText/dotnet/8.0.2/classi_text_1_1_kernel_1_1_pdf_1_1_canvas_1_1_parser_1_1_pdf_text_extractor.html)).

**PdfNetStack.** It makes existing PDFs smaller without changing a rendered pixel and proves every
output equivalent to its source before returning it ([compression](/guides/compress-pdf/),
[fidelity](/concepts/fidelity/)). It edits, merges, splits and encrypts documents (1.0, on
NuGet). Version 1.1, in the main branch and not yet on NuGet, adds a drawing canvas, embedded
OpenType fonts with Arabic and right to left shaping, forms, PAdES B-B to B-LTA signatures, PDF/A-2 and 3
output, text and image extraction, and HTML and CSS to PDF.

## Licensing in plain words

- **iText.** Dual licensed: AGPL-3.0 or a commercial license
  ([repository](https://github.com/itext/itext-dotnet)). iText's own summary of the AGPL says that
  if you run iText on a network you must make your application's source code available under the
  AGPL, disclose changes to iText, and keep iText's producer line and license in output metadata.
  "If you cannot comply with the AGPLv3 terms, we offer commercial licensing options"
  ([iText AGPL page](https://itextpdf.com/how-buy/AGPLv3-license)). The pdfHTML add-on is published
  under the same AGPL terms ([NuGet](https://www.nuget.org/packages/itext.pdfhtml/6.3.4)).
- **PdfNetStack.** MIT, for every package.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Features

| | PdfNetStack | iText Core 9.8.0 and add-ons |
|---|---|---|
| License | MIT | AGPL-3.0 or commercial |
| Targets | .NET 8 and 10, .NET Standard 2.0 | .NET Framework 4.6.1, .NET Standard 2.0 |
| Create documents | 1.1: canvas, fonts, paragraphs | Yes, with a layout engine |
| Edit and merge | 1.0 | Yes |
| Lossless compression, every output verified | 1.0 | Not listed |
| Forms | 1.1: create, fill, flatten | Yes |
| Digital signatures | 1.1: PAdES B-B to B-LTA, sign and validate | Yes |
| PDF/A | 1.1: PDF/A-2b, 2u, 3b, 3u | Yes |
| PDF/UA | Tagged PDF from HTML (1.1) | Yes |
| Text extraction | 1.1 | Yes |
| HTML to PDF | 1.1, `PdfNetStack.Html` | pdfHTML add-on |
| OCR | `PdfNetStack.Ocr` add-on, not on NuGet | pdfOCR add-on |
| Redaction | No | pdfSweep add-on |
| Arabic and right to left text | 1.1, built in | pdfCalligraph add-on |

Sources for the iText column: [repository](https://github.com/itext/itext-dotnet),
[NuGet](https://www.nuget.org/packages/itext/9.8.0) (target frameworks of the package),
add-ons on NuGet ([pdfHTML](https://www.nuget.org/packages/itext.pdfhtml),
[pdfOCR](https://www.nuget.org/packages/itext.pdfocr.tesseract4),
[pdfSweep](https://www.nuget.org/packages/itext.pdfsweep),
[pdfCalligraph](https://www.nuget.org/packages/itext.pdfcalligraph)),
[PdfTextExtractor](https://api.itextpdf.com/iText/dotnet/8.0.2/classi_text_1_1_kernel_1_1_pdf_1_1_canvas_1_1_parser_1_1_pdf_text_extractor.html).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick iText

- You need a feature PdfNetStack does not have: redaction, PDF/UA for documents built in code, or
  OCR from a released package.
- You need forms, signatures, PDF/A or text extraction today from released packages, while
  PdfNetStack's are in 1.1.
- The AGPL fits your project, or a commercial license suits you.
- You share code or knowledge with a Java team, since iText Core also exists for Java
  ([iText for Java](https://github.com/itext/itext-java)).

## When to pick PdfNetStack

- You want MIT licensing, with no source disclosure or producer line obligations.
- You need to shrink existing PDFs losslessly, with a proof that the output matches the input.
- Arabic and right to left shaping, HTML to PDF and text extraction in one MIT library (1.1).

## Related

- [Compare overview](/compare/)
- [Digital signatures](/guides/signatures/)
- [HTML to PDF](/guides/html-to-pdf/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-10-29.
