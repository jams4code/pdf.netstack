---
title: PdfNetStack and wkhtmltopdf compared
description: 'wkhtmltopdf next to PdfNetStack.Html for HTML to PDF in .NET: project status, licenses, what each renders, and when wkhtmltopdf still fits.'
sidebar:
  label: wkhtmltopdf
---

wkhtmltopdf is a command line tool that renders HTML to PDF with the Qt WebKit engine.
`PdfNetStack.Html` converts HTML and CSS to PDF with a layout engine written in C#. It shipped in
PdfNetStack 1.1 and is on NuGet.

## What each does well

**wkhtmltopdf.** wkhtmltopdf and wkhtmltoimage are command line tools that render HTML into PDF and
image formats "using the Qt WebKit rendering engine". They run headless, without a display, and
there is also a C library ([website](https://wkhtmltopdf.org/)). Pages may run JavaScript, which
is on by default. Headers and footers can be text or HTML documents with page numbers, headings
become PDF bookmarks, and a table of contents can be generated from the headings
([usage](https://wkhtmltopdf.org/usage/wkhtmltopdf.txt)).

**PdfNetStack.Html.** A layout engine in C#, no browser: the CSS cascade, block, inline, table,
flex and grid layout, floats and positioning, paged media with `@page`, margin boxes, named pages
and running headers and footers, repeated table headers, tagged PDF for accessibility, web fonts,
and right to left and Arabic text. Since 1.2, `hyphens: auto` hyphenates with the patterns of the
`PdfNetStack.Hyphenation` package. Nothing is fetched from the network unless you pass a resource
loader ([HTML to PDF](/guides/html-to-pdf/)). The result is a `PdfDocument` that can be signed,
encrypted or saved as PDF/A with the rest of PdfNetStack.

## Project status

- The wkhtmltopdf repository was "archived by the owner on Jan 2, 2023" and is read only
  ([repository](https://github.com/wkhtmltopdf/wkhtmltopdf)).
- The current stable series is 0.12.6, released in June 2020
  ([downloads](https://wkhtmltopdf.org/downloads.html)).
- Its status page explains that "QtWebKit was eventually deprecated in 2015 and removed in 2016",
  and advises: "Do not use wkhtmltopdf with any untrusted HTML"
  ([status page](https://wkhtmltopdf.org/status.html)).

## Licensing in plain words

- **wkhtmltopdf.** LGPL-3.0 ([repository](https://github.com/wkhtmltopdf/wkhtmltopdf),
  [website](https://wkhtmltopdf.org/)).
- **PdfNetStack.Html.** MIT, like the rest of PdfNetStack.

Read each license yourself before you decide. This page is not legal advice.

## Features

| | PdfNetStack.Html | wkhtmltopdf |
|---|---|---|
| License | MIT | LGPL-3.0 |
| How it runs | Managed .NET code in your process | A native command line program, or its C library |
| Engine | C# layout engine | Qt WebKit |
| JavaScript | No | Yes, on by default |
| Headers and footers with page numbers | CSS margin boxes and running elements | Text, or HTML header and footer documents |
| Bookmarks from headings | Yes, on by default | Yes, on by default |
| Printed table of contents with page numbers | No: `target-counter()` and `leader()` are not supported | Generated from headings |
| Flexbox and grid | Yes | Not listed |
| Tagged PDF | Yes, on by default | Not listed |
| Untrusted HTML | No network without a loader, files only under the base folder, bounded parsing and layout | Status page advises against it |
| HTML to images | No | Yes, wkhtmltoimage |
| Latest release | 1.2.0, November 2026 | 0.12.6, June 2020 |
| Maintained | Yes | Repository archived on 2023-01-02 |

Sources for the wkhtmltopdf column: [website](https://wkhtmltopdf.org/),
[repository](https://github.com/wkhtmltopdf/wkhtmltopdf),
[status page](https://wkhtmltopdf.org/status.html),
[downloads](https://wkhtmltopdf.org/downloads.html),
[usage](https://wkhtmltopdf.org/usage/wkhtmltopdf.txt).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick wkhtmltopdf

- Your templates are tuned to WebKit's rendering and already produce the output you want, and you
  convert only HTML you control. Its status page also suggests running it under a Mandatory
  Access Control system such as AppArmor or SELinux.
- Your templates need JavaScript to build the page. `PdfNetStack.Html` does not run scripts. For
  sites that use dynamic JavaScript, the wkhtmltopdf status page itself suggests puppeteer or one
  of its wrappers; see [headless Chromium](/compare/headless-chromium/).
- You need images as well as PDF from the same HTML. wkhtmltoimage renders to image formats;
  `PdfNetStack.Html` writes PDF only.

## When to pick PdfNetStack.Html

- You want a maintained engine that runs inside your .NET process, with no native program to
  install or update.
- You convert HTML you do not fully trust.
- You want paged media from CSS: margin boxes, running elements, named pages and repeated table
  headers, or tagged PDF.

## Related

- [Compare overview](/compare/)
- [HTML to PDF](/guides/html-to-pdf/)
- [Paged media](/guides/html-to-pdf/paged-media/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
