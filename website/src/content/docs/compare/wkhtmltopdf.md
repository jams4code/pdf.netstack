---
title: PdfNetStack and wkhtmltopdf compared
description: 'wkhtmltopdf next to PdfNetStack.Html for HTML to PDF in .NET: project status, licenses, what each renders, and when wkhtmltopdf still fits.'
draft: true
sidebar:
  label: wkhtmltopdf
---

wkhtmltopdf is a command line tool that renders HTML to PDF with the Qt WebKit engine.
`PdfNetStack.Html` converts HTML and CSS to PDF with a layout engine written in C#. It is part of
PdfNetStack 1.1, on NuGet.

## What each does well

**wkhtmltopdf.** It renders HTML into PDF, and its sibling wkhtmltoimage into image formats, "using
the Qt WebKit rendering engine", headless, without a display
([repository](https://github.com/wkhtmltopdf/wkhtmltopdf)). Pages may run JavaScript, which is on
by default, headers and footers can be HTML documents with page numbers, and a table of contents
can be generated from the headings ([usage](https://wkhtmltopdf.org/usage/wkhtmltopdf.txt)).

**PdfNetStack.Html.** A layout engine in C#, no browser: the CSS cascade, block, inline, table,
flex and grid layout, floats and positioning, paged media with `@page`, margin boxes, named pages
and running headers and footers, repeated table headers, tagged PDF for accessibility, web fonts,
and right to left and Arabic text. Nothing is fetched from the network unless you pass a resource
loader ([HTML to PDF](/guides/html-to-pdf/)). The result is a `PdfDocument` that can be signed,
encrypted or saved as PDF/A with the rest of PdfNetStack.

## Project status

- The wkhtmltopdf repository was "archived by the owner on Jan 2, 2023" and is read only
  ([repository](https://github.com/wkhtmltopdf/wkhtmltopdf)).
- Its status page explains that "QtWebKit was eventually deprecated in 2015 and removed in 2016",
  and advises: "Do not use wkhtmltopdf with any untrusted HTML"
  ([status page](https://wkhtmltopdf.org/status.html)).

## Licensing in plain words

- **wkhtmltopdf.** LGPL-3.0 ([repository](https://github.com/wkhtmltopdf/wkhtmltopdf)).
- **PdfNetStack.Html.** MIT, like the rest of PdfNetStack.

Read each license yourself before you decide. This page is not legal advice.

## Features

| | PdfNetStack.Html | wkhtmltopdf |
|---|---|---|
| License | MIT | LGPL-3.0 |
| How it runs | Managed .NET code in your process | A native program, called as a separate process |
| Engine | C# layout engine | Qt WebKit |
| JavaScript | No | Yes, on by default |
| Headers and footers with page numbers | CSS margin boxes and running elements | HTML header and footer documents |
| Table of contents | Bookmarks from headings | Generated from headings |
| Flexbox and grid | Yes | Not listed |
| Tagged PDF | Yes, on by default | Not listed |
| Untrusted HTML | No network without a loader, files only under the base folder, bounded parsing and layout | Status page advises against it |
| HTML to images | No | Yes, wkhtmltoimage |
| Maintained | Yes | Repository archived on 2023-01-02 |
| Available | 1.1, on NuGet | Released |

Sources for the wkhtmltopdf column: [repository](https://github.com/wkhtmltopdf/wkhtmltopdf),
[status page](https://wkhtmltopdf.org/status.html), [usage](https://wkhtmltopdf.org/usage/wkhtmltopdf.txt).
"Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick wkhtmltopdf

- Your templates are tuned to WebKit's rendering and already produce the output you want, and you
  convert only HTML you control, in a confined environment as its status page recommends.
- Your templates need JavaScript to build the page. `PdfNetStack.Html` does not run scripts; for
  pages built by script, also consider [headless Chromium](/compare/headless-chromium/).
- You need HTML to PDF today from a released package.

## When to pick PdfNetStack.Html

- You want a maintained engine that runs inside your .NET process, with no native program to
  install or update.
- You convert HTML you do not fully trust.
- You need paged media features such as running headers, named pages and repeated table headers,
  or tagged PDF.

## Related

- [Compare overview](/compare/)
- [HTML to PDF](/guides/html-to-pdf/)
- [Paged media](/guides/html-to-pdf/paged-media/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-10-29.
