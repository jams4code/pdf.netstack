---
title: PdfNetStack and PdfPig compared
description: 'PdfPig next to PdfNetStack for reading text and content from PDFs in .NET: strengths, licenses, features, and when to pick PdfPig.'
sidebar:
  label: PdfPig
---

PdfPig is an Apache-2.0 licensed .NET library for reading text and content from PDF files.
PdfNetStack is an MIT licensed .NET library centred on lossless compression and editing, with text
and image extraction since 1.1. This page sets the two side by side.

## What each does well

**PdfPig.** Its README says it "supports reading text and content from PDF files. It also supports
basic PDF file creation." It gives each letter with its location, width, font size and font name,
words through pluggable word extractors, and document layout analysis tools for page segmentation
and reading order. It reads images, annotations, hyperlinks, bookmarks, form fields and embedded
files, opens password protected documents, and merges files
([repository](https://github.com/UglyToad/PdfPig)). Images come with their placement and size,
and common image types convert to PNG ([wiki](https://github.com/UglyToad/PdfPig/wiki/Images)).
Documents it creates can target PDF/A-1a, 1b, 2a, 2b, 3a or 3b
([release notes](https://github.com/UglyToad/PdfPig/releases/tag/v0.1.2),
[source](https://github.com/UglyToad/PdfPig/blob/v0.1.16/src/UglyToad.PdfPig/Writer/PdfAStandard.cs)).
The 0.1.16 NuGet package targets .NET Framework 4.6.2 and 4.7.1, .NET Standard 2.0 and 2.1, and
.NET 6, 8 and 9 ([NuGet](https://www.nuget.org/packages/PdfPig/0.1.16)).

The README on the main branch also describes incremental updates, composite TrueType fonts for
text with many distinct characters such as Chinese, Japanese or Korean, and embedding files in
created documents. Those are in the 0.1.17 alpha prereleases on NuGet, not in 0.1.16.

**PdfNetStack.** It makes existing PDFs smaller without changing a rendered pixel and proves every
output equivalent to its source before returning it ([compression](/guides/compress-pdf/)). It
edits, merges, splits and encrypts documents (1.0). Version 1.1 adds text extraction (page text in
reading order with column detection, words and letters with bounds, fonts and sizes), image
extraction with decoding to pixels, OCR for scans, and the creation features: a canvas, embedded
fonts, forms, signatures, PDF/A and HTML to PDF. Version 1.2 adds hyphenation and a Tesseract OCR
engine. All three versions are on NuGet.

## Licensing in plain words

- **PdfPig.** Apache-2.0 ([repository](https://github.com/UglyToad/PdfPig),
  [NuGet](https://www.nuget.org/packages/PdfPig/0.1.16)). A permissive license with an explicit
  patent grant and a requirement to keep notices
  ([license text](https://www.apache.org/licenses/LICENSE-2.0)).
- **PdfNetStack.** MIT. A permissive license that requires keeping the copyright and license
  notice.

Both allow commercial use. Read each license yourself before you decide. This page is not legal
advice.

## Features

| | PdfNetStack | PdfPig 0.1.16 |
|---|---|---|
| License | MIT | Apache-2.0 |
| Letters with position, font and size | 1.1 | Yes |
| Words | 1.1 | Yes, with several word extractors |
| Page text in reading order | 1.1, with column detection | Layout analysis and reading order tools |
| Images | 1.1: placement, resolution, decoding to pixels and PNG | Placement, size, PNG for common types |
| Read annotations, bookmarks, form fields | 1.1 for annotations and forms, 1.0 for bookmarks | Yes, read only |
| Fill and flatten forms | 1.1 | No, forms are read only |
| Create documents | 1.1: canvas, embedded OpenType fonts, any script the font covers | Basic: Standard 14 and TrueType fonts, ASCII text |
| Edit existing documents | 1.0: pages, metadata, bookmarks | Very limited changes, per its README |
| Merge | 1.0 | Yes |
| Encryption | 1.0: read and write | Opens password protected files |
| PDF/A output | 1.1: PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e, 4f | PDF/A-1a, 1b, 2a, 2b, 3a, 3b for documents it creates |
| OCR of scans | 1.1, add-on packages | Not listed |
| Lossless compression, every output verified | 1.0 | Not listed |
| Signatures, HTML to PDF | 1.1 | Not listed |

Sources for the PdfPig column: [repository](https://github.com/UglyToad/PdfPig),
[wiki](https://github.com/UglyToad/PdfPig/wiki),
[release notes](https://github.com/UglyToad/PdfPig/releases),
[NuGet](https://www.nuget.org/packages/PdfPig/0.1.16).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick PdfPig

- You only need to read text and content, and want a library focused on that.
- You want its document layout analysis tools, such as page segmentation and reading order
  detectors, to build your own extraction pipeline.
- You prefer the Apache-2.0 license and its explicit patent grant.

## When to pick PdfNetStack

- You need to change documents as well as read them: fill forms, edit pages, encrypt, sign, or
  compress losslessly with a proof.
- You need to create documents with text in any script the font covers, including Arabic and
  right to left (1.1).
- You need to make scanned pages searchable with OCR (1.1, Tesseract engine in 1.2).

## Related

- [Compare overview](/compare/)
- [Extract text](/guides/extract-text/)
- [Edit pages](/guides/edit-pages/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
