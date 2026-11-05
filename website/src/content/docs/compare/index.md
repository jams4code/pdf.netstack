---
title: Compare PdfNetStack with other PDF tools
description: How PdfNetStack compares with PDFsharp, QuestPDF, iText, PdfPig, wkhtmltopdf, headless Chromium, Ghostscript and qpdf. Licenses and features, with sources.
draft: true
sidebar:
  label: Overview
  order: 0
---

PdfNetStack is one of several ways to work with PDF files from .NET. These pages set it next to the
libraries and tools developers usually weigh it against, with a source for every fact about the
other products.

## How to read these tables

- Facts about PdfNetStack come from its repository. **1.0** and **1.1** say which
  release, both on NuGet, added a feature.
- Facts about other products come from their own websites, repositories, documentation and NuGet
  packages, linked on each product's page.
- **Not listed** means the feature is not in the product's own documentation or feature list on the
  check date. It does not mean the feature is impossible.
- Versions are the latest stable releases on NuGet or the product's site on the check date.

## Libraries

| | PdfNetStack | [PDFsharp and MigraDoc](/compare/pdfsharp/) | [QuestPDF](/compare/questpdf/) | [iText](/compare/itext/) | [PdfPig](/compare/pdfpig/) |
|---|---|---|---|---|---|
| License | MIT | AGPL-3.0 or commercial (6.5.0); MIT up to 6.2.4 | QuestPDF Community License (free under conditions) or paid | AGPL-3.0 or commercial | Apache-2.0 |
| Native binaries in the package | None | None | Yes: Skia based layer and qpdf | None | None |
| Create PDFs | 1.1: canvas, fonts, paragraphs | Yes | Yes, fluent layout API | Yes | Basic, ASCII text only |
| Edit pages, merge, split | 1.0 | Yes | Merge, extract pages, overlays | Yes | Merge, very limited editing |
| Compress existing PDFs | 1.0, lossless, every output verified; 1.1 adds opt-in image downsampling and JPEG conversion | Not listed | Not listed | Not listed | Not listed |
| HTML to PDF | 1.1, `PdfNetStack.Html` | Not listed | No, by design | pdfHTML add-on | Not listed |
| Forms | 1.1: create, fill, flatten | Not listed | Not listed | Yes | Read only |
| Digital signatures | 1.1: PAdES B-B to B-LTA, validation against your trust anchors | Yes, since 6.2.0 | Not listed | Yes | Not listed |
| PDF/A | 1.1: 2b, 2u, 3b, 3u; Factur-X and ZUGFeRD | Documented | Yes | Yes | Not listed |
| Encryption | 1.0: AES-256, AES-128 | Yes | Yes | Yes | Opens protected files |
| Text extraction | 1.1: text, words, letters | Not listed | Not listed | Yes | Yes, with layout analysis |
| OCR | Add-on package | Not listed | Not listed | pdfOCR add-on | Not listed |
| Render PDF pages to images | No, out of scope | Not listed | Not listed | Not listed | Not listed |

## Tools and browsers

| | PdfNetStack | [wkhtmltopdf](/compare/wkhtmltopdf/) | [Puppeteer Sharp](/compare/headless-chromium/) | [Playwright for .NET](/compare/headless-chromium/) | [Ghostscript](/compare/ghostscript-qpdf/) | [qpdf](/compare/ghostscript-qpdf/) |
|---|---|---|---|---|---|---|
| License | MIT | LGPL-3.0 | MIT | MIT | AGPL-3.0 or commercial | Apache-2.0 |
| What it runs | Managed .NET code | A native program on Qt WebKit | A downloaded Chrome or Firefox | A bundled Node.js and downloaded browsers | A native interpreter | A native C++ library and command line tool |
| HTML to PDF | 1.1, no JavaScript | Yes | Yes | Yes | No, reads PostScript and PDF | No, transforms existing PDFs |
| Compress or restructure PDFs | 1.0, lossless, verified; 1.1 adds opt-in lossy image options | Not listed | Not listed | Not listed | Yes, by re-creating the PDF | Yes, content preserving |
| Render PDF pages to images | No | Not listed | Not listed | Not listed | Yes | No |

The wkhtmltopdf repository was archived by its owner on 2023-01-02.

## Pages

- [PDFsharp and MigraDoc](/compare/pdfsharp/): creating documents in code
- [QuestPDF](/compare/questpdf/): documents from a fluent layout API
- [iText](/compare/itext/): a broad PDF SDK under AGPL or a commercial license
- [PdfPig](/compare/pdfpig/): reading text and content
- [wkhtmltopdf](/compare/wkhtmltopdf/): HTML to PDF on Qt WebKit
- [Puppeteer Sharp and Playwright](/compare/headless-chromium/): HTML to PDF in a real browser
- [Ghostscript and qpdf](/compare/ghostscript-qpdf/): compression and structure

Each page ends with a section on when to pick the other tool. Commercial SDKs are not covered here.

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-10-29.
