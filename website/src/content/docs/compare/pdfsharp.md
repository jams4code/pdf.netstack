---
title: PdfNetStack and PDFsharp compared
description: 'PDFsharp and MigraDoc next to PdfNetStack for .NET PDF work: what each does well, licenses in plain words, features, and when to pick PDFsharp.'
sidebar:
  label: PDFsharp and MigraDoc
---

PDFsharp and MigraDoc are long standing .NET libraries for creating PDF documents. PdfNetStack is
an MIT licensed .NET library centred on lossless compression and editing. Since 1.1 it also draws,
fills forms, signs, extracts text and converts HTML to PDF. This page sets the two side by side.

## What each does well

**PDFsharp and MigraDoc.** PDFsharp "creates and processes PDF documents on the fly" and draws
pages "using drawing routines known from GDI+". MigraDoc is "a document generator based on a
document object model": you add paragraphs, tables, charts and sections, and it does the layout
and creates page breaks, with PDF or RTF output ([documentation](https://docs.pdfsharp.net/)). The
feature list includes modifying, merging and splitting existing files, 40 and 128 bit encryption
and AES 256 bit encryption, and TrueType fonts and OpenType fonts with TrueType outlines
([features](https://docs.pdfsharp.net/PDFsharp/Overview/Features.html)). Digital signatures based
on CMS arrived in PDFsharp 6.2.0
([signatures](https://docs.pdfsharp.net/PDFsharp/Topics/PDF-Features/Signatures.html)). The
documentation has articles on creating PDF/A documents
([PDF/A](https://docs.pdfsharp.net/PDFsharp/Topics/PDF-Features/Archiving.html)) and PDF/UA
documents ([PDF/UA](https://docs.pdfsharp.net/PDFsharp/Topics/PDF-Features/Accessibility.html)).
The libraries target .NET 8, 9 and 10, .NET Framework 4.6.2 and .NET Standard 2.0
([documentation](https://docs.pdfsharp.net/)).

**PdfNetStack.** It makes existing PDFs smaller without changing a rendered pixel and proves every
output equivalent to its source before returning it ([compression](/guides/compress-pdf/),
[fidelity](/concepts/fidelity/)). Version 1.0 edits, merges, splits and encrypts documents.
Version 1.1 adds a drawing canvas, embedded OpenType fonts with shaping for Arabic and right to
left text, paragraph layout, forms, PAdES signatures, PDF/A output, text and image extraction, and
HTML to PDF. Version 1.2 adds hyphenation. All three versions are on NuGet.

## Licensing in plain words

- **PDFsharp and MigraDoc.** From version 6.5.0 and 7.0.0 Preview 2, "PDFsharp and MigraDoc are
  published under a Dual License consisting of AGPL and a commercial license"
  ([license](https://docs.pdfsharp.net/General/License/License.html)). The 6.5.0 NuGet packages
  carry the license expression `AGPL-3.0-only`
  ([PDFsharp](https://www.nuget.org/packages/PDFsharp/6.5.0),
  [PDFsharp-MigraDoc](https://www.nuget.org/packages/PDFsharp-MigraDoc/6.5.0)). Version 6.2.4,
  every 6.x version before it and the 7.0.0-preview-1 prerelease are listed on NuGet under MIT
  ([PDFsharp 6.2.4](https://www.nuget.org/packages/PDFsharp/6.2.4)). Check the version you depend
  on.
- **PdfNetStack.** MIT, for every package. On .NET 8 and later the core package has no
  dependencies.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Features

| | PdfNetStack | PDFsharp and MigraDoc 6.5.0 |
|---|---|---|
| License | MIT | AGPL-3.0 or commercial |
| Targets | .NET 8 and 10, .NET Standard 2.0 (.NET Framework 4.6.2 and later) | .NET 8, 9 and 10, .NET Framework 4.6.2, .NET Standard 2.0 |
| Native binaries in the package | None | None |
| Create documents in code | 1.1: canvas, fonts, paragraphs | Yes: PDFsharp drawing, MigraDoc document model |
| Document model with automatic page breaks | No; HTML and CSS through `PdfNetStack.Html` (1.1) | Yes, MigraDoc |
| RTF output | No | Yes, MigraDoc |
| Modify, merge, split | 1.0 | Yes |
| Lossless compression, every output verified | 1.0 | Not listed |
| Encryption | 1.0: AES-256 and AES-128 written; RC4, AES-128 and AES-256 read | Yes: 40 and 128 bit, AES 256 bit |
| Digital signatures | 1.1: PAdES B-B to B-LTA, sign and validate | Yes, CMS based, since 6.2.0 |
| PDF/A | 1.1: PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e, 4f | Yes, for new documents |
| Forms | 1.1: create, fill, flatten | Limited AcroForm support, per its FAQ |
| Text extraction | 1.1: text, words, letters | Low level only, per its FAQ |
| HTML to PDF | 1.1, `PdfNetStack.Html` | Not out of the box, per its FAQ |

Sources for the PDFsharp column: [documentation](https://docs.pdfsharp.net/),
[features](https://docs.pdfsharp.net/PDFsharp/Overview/Features.html),
[signatures](https://docs.pdfsharp.net/PDFsharp/Topics/PDF-Features/Signatures.html),
[PDF/A](https://docs.pdfsharp.net/PDFsharp/Topics/PDF-Features/Archiving.html),
[FAQ](https://docs.pdfsharp.net/PDFsharp/Overview/FAQ.html),
[license](https://docs.pdfsharp.net/General/License/License.html),
[NuGet](https://www.nuget.org/packages/PDFsharp/6.5.0) and the files in that package.
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick PDFsharp and MigraDoc

- You want a document object model that lays out paragraphs, tables and charts and breaks pages
  for you, from code, without HTML and CSS. MigraDoc does that.
- You need RTF as well as PDF from the same document.
- Your project already uses PDFsharp and the license of the version you use works for you.

## When to pick PdfNetStack

- You need to shrink existing PDFs losslessly, with a proof that the output matches the input.
- You want MIT licensing for every package.
- You need form filling, text extraction or HTML to PDF from the same library (1.1).

## Related

- [Compare overview](/compare/)
- [Compress PDF files](/guides/compress-pdf/)
- [Edit pages](/guides/edit-pages/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
