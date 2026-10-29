---
title: PdfNetStack and QuestPDF compared
description: 'QuestPDF next to PdfNetStack for generating and processing PDFs in .NET: strengths, licenses in plain words, features, and when to pick QuestPDF.'
draft: true
sidebar:
  label: QuestPDF
---

QuestPDF is a .NET library for generating PDF documents from a fluent C# API. PdfNetStack is an
MIT licensed .NET library centred on lossless compression and editing, with drawing, forms,
signatures and HTML to PDF in its next release. This page sets the two side by side.

## What each does well

**QuestPDF.** Its README describes "a modern library for PDF document generation" whose "fluent C#
API lets you design complex layouts with clean, readable code", on "a powerful layout engine built
specifically for PDF generation": page setup, headers and footers, tables, rows, columns, lists
and automatic pagination. It lists document operations (merging files, extracting pages,
attaching files, metadata editing, overlays), encryption with access limits, PDF/A, PDF/UA-1 and
EN 16931 e-invoicing (ZUGFeRD and Factur-X), rich text, fonts, images, SVG, and right to left
text. A companion app gives a live preview with hot reload
([repository](https://github.com/QuestPDF/QuestPDF)). It runs on Windows, Linux and macOS, on
.NET 6 and later and .NET Framework 4.6.2 and later ([repository](https://github.com/QuestPDF/QuestPDF)).

**PdfNetStack.** It makes existing PDFs smaller without changing a rendered pixel and proves every
output equivalent to its source before returning it ([compression](/guides/compress-pdf/),
[fidelity](/concepts/fidelity/)). It edits, merges, splits and encrypts documents (1.0, on
NuGet). Version 1.1, in the main branch and not yet on NuGet, adds a drawing canvas, embedded
OpenType fonts, paragraph layout, forms, PAdES signatures, PDF/A-2 and 3 output, text and image
extraction, and HTML and CSS to PDF.

## Licensing in plain words

- **QuestPDF.** Two tiers. The Community License is free for individuals and businesses with
  annual gross revenue under USD 1 million, qualifying non-profits, open source projects under an
  OSI approved license, and publicly funded or non-profit academic institutions. Government
  bodies and publicly traded companies are excluded regardless of revenue. Everyone else needs a
  Professional or Enterprise license before production use
  ([license selection guide](https://www.questpdf.com/license/guide.html),
  [license documents](https://www.questpdf.com/license/)).
- **PdfNetStack.** MIT, for every package, with no revenue or organization conditions.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Native code

The QuestPDF 2026.9.1 NuGet package ships native libraries for Windows, Linux and macOS (x64 and
Arm64, plus Windows x86 and Linux musl x64): a Skia based layer and qpdf
([NuGet](https://www.nuget.org/packages/QuestPDF/2026.9.1), package contents). Version 2024.3.0
replaced SkiaSharp with "a custom native layer built on top of Skia"
([release notes](https://github.com/QuestPDF/QuestPDF/releases/tag/2024.3.0)).

The PdfNetStack core is managed code only, trim and Native AOT compatible on .NET 8 and later.
Only the optional OCR add-on uses native code (ONNX Runtime).

## Features

| | PdfNetStack | QuestPDF 2026.9.1 |
|---|---|---|
| License | MIT | Community License (free under conditions) or paid |
| Native libraries | None in the core | Skia based layer and qpdf, per platform |
| Generate documents in code | 1.1: canvas, fonts, paragraphs | Yes, fluent layout API with automatic pagination |
| HTML and CSS to PDF | 1.1, `PdfNetStack.Html` | No, documents are built in code |
| Merge, extract pages, overlays | 1.0 for pages, 1.1 for overlays | Yes |
| Lossless compression, every output verified | 1.0 | Not listed |
| Encryption | 1.0 | Yes |
| PDF/A | 1.1: PDF/A-2b, 2u, 3b, 3u | Yes |
| PDF/UA | Tagged PDF from HTML (1.1) | PDF/UA-1 |
| E-invoice metadata (ZUGFeRD, Factur-X) | Attachments for PDF/A-3 (1.1); the metadata is on the roadmap | Yes, EN 16931 |
| Forms | 1.1: create, fill, flatten | Not listed |
| Digital signatures | 1.1: PAdES B-B | Not listed |
| Text extraction | 1.1 | Not listed |

Sources for the QuestPDF column: [repository](https://github.com/QuestPDF/QuestPDF),
[NuGet](https://www.nuget.org/packages/QuestPDF/2026.9.1),
[license selection guide](https://www.questpdf.com/license/guide.html).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick QuestPDF

- You build reports and invoices in C# and want a layout API that pages content for you, with a
  live preview while you design.
- You need PDF/UA-1 or ZUGFeRD and Factur-X output today from a released package.
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

Checked on 2026-10-29.
