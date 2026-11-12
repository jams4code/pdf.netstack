---
title: PdfNetStack and QuestPDF compared
description: 'QuestPDF next to PdfNetStack for generating and processing PDFs in .NET: strengths, licenses in plain words, features, and when to pick QuestPDF.'
sidebar:
  label: QuestPDF
---

QuestPDF is a .NET library for generating PDF documents from a fluent C# API. PdfNetStack is an
MIT licensed .NET library centred on lossless compression and editing. Since 1.1 it also draws,
fills forms, signs, extracts text and converts HTML to PDF. This page sets the two side by side.

## What each does well

**QuestPDF.** Its README describes a library to "design documents the way you design software:
with clean, maintainable, strong-typed C# code", on a layout engine "built specifically for PDF
generation" with "automatic pagination". It lists page setup, headers, footers, tables, rows,
columns and lists; document operations (merging documents, extracting pages, attaching files,
extending metadata, overlays and underlays, optimizing for the web); encryption with access
limits; PDF/A, PDF/UA-1 and EN 16931 e-invoicing (ZUGFeRD and Factur-X); rich text, fonts, images,
SVG, and right to left text. A companion app gives a live preview with hot reload
([repository](https://github.com/QuestPDF/QuestPDF)). It runs on Windows, Linux and macOS, on
.NET 6 and later and .NET Framework 4.6.2 and later ([repository](https://github.com/QuestPDF/QuestPDF)).

**PdfNetStack.** It makes existing PDFs smaller without changing a rendered pixel and proves every
output equivalent to its source before returning it ([compression](/guides/compress-pdf/),
[fidelity](/concepts/fidelity/)). Version 1.0 edits, merges, splits and encrypts documents.
Version 1.1 adds a drawing canvas, embedded OpenType fonts, paragraph layout, forms, PAdES
signatures, PDF/A output, Factur-X and ZUGFeRD invoices, text and image extraction, and HTML and
CSS to PDF. Version 1.2 adds hyphenation. All three versions are on NuGet.

## Licensing in plain words

- **QuestPDF.** A free Community License and paid Professional and Enterprise licenses. In the
  license's own plain language summary, "QuestPDF is free for individuals and businesses with
  annual gross revenue under USD 1,000,000, and for charitable organisations, academic
  institutions, and open-source projects. Public-sector entities and publicly traded companies are
  not eligible, regardless of revenue." An organization that does not qualify "must obtain a paid
  license before production use". The Community License is source-available, not an OSI approved
  open source license. The license selection guide gives the exact categories and conditions
  (version 3.0, effective 6 July 2026:
  [Community License](https://www.questpdf.com/license/community.html),
  [license selection guide](https://www.questpdf.com/license/guide.html)).
- **PdfNetStack.** MIT, for every package, with no revenue or organization conditions.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Native code

The QuestPDF 2026.9.2 NuGet package ships two native libraries, `questpdf_skia` and
`questpdf_qpdf`, for Windows (x64, x86, Arm64), Linux (x64, Arm64, musl x64) and macOS (x64,
Arm64) ([NuGet](https://www.nuget.org/packages/QuestPDF/2026.9.2), package contents and release
notes). Version 2024.3.0 replaced SkiaSharp with "a custom native layer built on top of Skia"
([release notes](https://github.com/QuestPDF/QuestPDF/releases/tag/2024.3.0)), and the document
operations "are created using the qpdf library"
([document operations](https://www.questpdf.com/concepts/document-operations.html)).

The PdfNetStack core, `PdfNetStack.Html` and `PdfNetStack.Hyphenation` are managed code only, and
publish with Native AOT and trimming on .NET 8 and .NET 10. Native code comes only with the
optional `PdfNetStack.Ocr` add-on, through ONNX Runtime. `PdfNetStack.Ocr.Tesseract` (1.2) leaves
those libraries out and runs a `tesseract` executable you install yourself.

## Features

| | PdfNetStack | QuestPDF 2026.9.2 |
|---|---|---|
| License | MIT | Community License (free under conditions) or paid |
| Native libraries | None in the core | Skia based layer and qpdf, per platform |
| Generate documents in code | 1.1: canvas, fonts, paragraphs | Yes, fluent layout API with automatic pagination |
| HTML and CSS to PDF | 1.1, `PdfNetStack.Html` | No, documents are built in code |
| Merge, extract pages, overlays | 1.0 for pages, 1.1 for overlays | Yes |
| Lossless compression, every output verified | 1.0 | Not listed |
| Encryption | 1.0: AES-256 and AES-128 written | Yes: 40, 128 and 256 bit |
| PDF/A | 1.1: PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e, 4f | PDF/A-2b, 2u, 2a, 3b, 3u, 3a |
| PDF/UA | Not claimed; tagged PDF from HTML (1.1) | PDF/UA-1 |
| E-invoice metadata (ZUGFeRD, Factur-X) | 1.1: invoice attachment and XMP metadata with PDF/A-3 | Yes, EN 16931 |
| Forms | 1.1: create, fill, flatten | Not listed |
| Digital signatures | 1.1: PAdES B-B to B-LTA | Not listed |
| Text extraction | 1.1 | Not listed |

Sources for the QuestPDF column: [repository](https://github.com/QuestPDF/QuestPDF),
[features overview](https://www.questpdf.com/features-overview.html),
[document operations](https://www.questpdf.com/concepts/document-operations.html),
[NuGet](https://www.nuget.org/packages/QuestPDF/2026.9.2) and the files in that package,
[license selection guide](https://www.questpdf.com/license/guide.html).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick QuestPDF

- You build reports and invoices in C# and want a layout API that pages content for you, with a
  live preview while you design.
- You need PDF/UA-1 output, or the accessible PDF/A levels 2a and 3a.
- Your organization qualifies for the Community License, or a paid license suits you.

## When to pick PdfNetStack

- You need to shrink or edit existing PDFs, with a proof that the output matches the input.
- You want MIT licensing with no conditions, or managed code only, for example for Native AOT.
- You prefer HTML and CSS templates, or need forms, signatures or text extraction (1.1).

## Related

- [Compare overview](/compare/)
- [HTML to PDF](/guides/html-to-pdf/)
- [PDF/A](/guides/pdf-a/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
