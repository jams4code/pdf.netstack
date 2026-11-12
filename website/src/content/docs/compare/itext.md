---
title: PdfNetStack and iText compared
description: 'iText Core next to PdfNetStack for PDF work in .NET: strengths, the AGPL and commercial licenses in plain words, features, and when to pick iText.'
sidebar:
  label: iText
---

iText Core is a broad PDF library for .NET and Java, with add-ons for HTML, OCR, redaction,
complex scripts and file size optimization. PdfNetStack is an MIT licensed .NET library centred
on lossless compression and editing. Since 1.1 it also creates documents, fills forms, signs
documents, writes PDF/A, extracts text and converts HTML to PDF. This page sets the two side by side.

## What each does well

**iText.** Its README describes "a high-performance, battle-tested library that allows you to
create, adapt, inspect and maintain PDF documents": creation with a layout engine, manipulation
such as merging, digital signing, form creation and editing, PDF/A and PDF/UA, FIPS compliant
cryptography, barcodes and SVG ([repository](https://github.com/itext/itext-dotnet)). Add-ons
extend it: pdfHTML converts HTML and CSS to PDF, pdfOCR performs OCR, pdfSweep redacts,
pdfCalligraph adds scripts such as Arabic, Hebrew and Thai, pdfOptimizer reduces file size and
pdfXFA flattens XFA forms ([repository](https://github.com/itext/itext-dotnet)). pdfOptimizer has
preset profiles, one of them lossless, and can revert a file or image that grew
([pdfOptimizer](https://itextpdf.com/products/compress-pdf-pdfoptimizer)). Text extraction is part
of the kernel API
([PdfTextExtractor](https://api.itextpdf.com/iText/dotnet/9.8.0/classi_text_1_1_kernel_1_1_pdf_1_1_canvas_1_1_parser_1_1_pdf_text_extractor.html)).

**PdfNetStack.** By default it makes existing PDFs smaller without changing a rendered pixel, and
proves every output equivalent to its source before returning it
([compression](/guides/compress-pdf/), [fidelity](/concepts/fidelity/)). Version 1.0 edits,
merges, splits and encrypts documents. Version 1.1 adds a drawing canvas, embedded OpenType fonts
with Arabic and right to left shaping, forms, PAdES B-B to B-LTA signatures, PDF/A output, text and
image extraction, HTML and CSS to PDF, OCR and opt-in lossy image options. Version 1.2 adds
hyphenation and a Tesseract OCR engine. All three versions are on NuGet.

## Licensing in plain words

- **iText.** Dual licensed: AGPL-3.0 or a commercial license
  ([repository](https://github.com/itext/itext-dotnet)). iText's AGPL page says: "You may not
  deploy it on a network without disclosing the full source code of your own applications under
  the AGPL license." The same page says you must disclose changes to iText, include the iText
  copyright and the AGPL license in output file metadata, and keep the producer line in every PDF
  created or manipulated with iText. "If you cannot comply with the AGPLv3 terms, we offer
  commercial licensing options" ([iText AGPL page](https://itextpdf.com/how-buy/AGPLv3-license)).
  The pdfHTML package on NuGet also gives the AGPL as its license
  ([NuGet](https://www.nuget.org/packages/itext.pdfhtml/6.3.4)).
- **PdfNetStack.** MIT for its code, in every package. The packages that bundle the OCR models
  (`PdfNetStack.Ocr.Models`, `PdfNetStack.Ocr.Tool`) add Apache-2.0 for the models, and
  `PdfNetStack.Hyphenation` keeps the permissive terms of each pattern set, listed in its license
  file.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Features

| | PdfNetStack | iText Core 9.8.0 and add-ons |
|---|---|---|
| License | MIT | AGPL-3.0 or commercial |
| Targets | .NET 8 and 10, .NET Standard 2.0 | .NET Framework 4.6.1, .NET Standard 2.0 |
| Create documents | 1.1: canvas, fonts, paragraphs | Yes, with a layout engine |
| Edit and merge | 1.0 | Yes |
| Compress existing PDFs | 1.0, lossless by default; 1.1 adds opt-in lossy image options | pdfOptimizer add-on, with a lossless profile |
| Equivalence check of the output | Yes, on by default | Not listed |
| Forms | 1.1: create, fill, flatten | Yes |
| XFA forms | No | pdfXFA add-on flattens them |
| Digital signatures | 1.1: PAdES B-B to B-LTA, sign and validate | Yes |
| PDF/A | 1.1: PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e, 4f | Yes |
| PDF/UA | Not claimed; tagged PDF from HTML (1.1) | Yes |
| Text extraction | 1.1 | Yes |
| HTML to PDF | 1.1, `PdfNetStack.Html` | pdfHTML add-on |
| OCR | 1.1: `PdfNetStack.Ocr` package; 1.2: Tesseract engine | pdfOCR add-on |
| Redaction | No | pdfSweep add-on |
| Arabic and right to left text | 1.1, built in | pdfCalligraph add-on |
| Barcodes | No | Yes |

Sources for the iText column: [repository](https://github.com/itext/itext-dotnet),
[NuGet](https://www.nuget.org/packages/itext/9.8.0) (target frameworks of the package),
add-ons on NuGet ([pdfHTML](https://www.nuget.org/packages/itext.pdfhtml),
[pdfOCR](https://www.nuget.org/packages/itext.pdfocr.tesseract4),
[pdfSweep](https://www.nuget.org/packages/itext.pdfsweep),
[pdfCalligraph](https://www.nuget.org/packages/itext.pdfcalligraph)),
[pdfOptimizer](https://itextpdf.com/products/compress-pdf-pdfoptimizer),
[pdfXFA](https://itextpdf.com/products/flatten-pdf-pdfxfa),
[PdfTextExtractor](https://api.itextpdf.com/iText/dotnet/9.8.0/classi_text_1_1_kernel_1_1_pdf_1_1_canvas_1_1_parser_1_1_pdf_text_extractor.html).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick iText

- You need a feature PdfNetStack does not have: redaction, PDF/UA, XFA flattening or barcodes.
- The AGPL fits your project, or a commercial license suits you.
- You share code or knowledge with a Java team, since iText Core also exists for Java
  ([iText for Java](https://github.com/itext/itext-java)).

## When to pick PdfNetStack

- You want MIT licensing, with no source disclosure or producer line obligations.
- You need to shrink existing PDFs losslessly, with a proof that the output matches the input.
- You want Arabic and right to left shaping, HTML to PDF, text extraction and OCR from one project
  under permissive licenses.

## Related

- [Compare overview](/compare/)
- [Digital signatures](/guides/signatures/)
- [HTML to PDF](/guides/html-to-pdf/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
