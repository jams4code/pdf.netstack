---
title: PdfNetStack and headless Chromium compared
description: Puppeteer Sharp and Playwright print HTML to PDF through a browser. How that compares with PdfNetStack.Html, a C# layout engine, and when to use each.
sidebar:
  label: Puppeteer Sharp and Playwright
---

Puppeteer Sharp and Playwright for .NET drive a real browser from C#, and a browser can print a
page to PDF. `PdfNetStack.Html` converts HTML and CSS to PDF with a layout engine written in C#,
without a browser. It shipped in PdfNetStack 1.1 and is on NuGet.

## What each does well

**Puppeteer Sharp.** A ".NET port of the official Node.JS Puppeteer API". It drives Chrome, and
Firefox through WebDriver BiDi. Its examples download a browser with `BrowserFetcher`, open a page
and call `page.PdfAsync` ([repository](https://github.com/hardkoded/puppeteer-sharp)). Its API
documentation notes: "Generating a pdf is currently only supported in Chrome headless"
([API](https://www.puppeteersharp.com/api/PuppeteerSharp/Page/)). `PdfOptions` takes header and
footer templates with page numbers, and options for a tagged PDF and a document outline
([PdfOptions](https://www.puppeteersharp.com/api/PuppeteerSharp/PdfOptions/)).

**Playwright for .NET.** A browser automation library from Microsoft. Its NuGet package bundles
Node.js runtimes for Windows, Linux and macOS
([NuGet](https://www.nuget.org/packages/Microsoft.Playwright/1.64.1), package contents), and
`playwright.ps1 install` downloads Chromium, Firefox and WebKit builds
([browsers](https://playwright.dev/dotnet/docs/browsers)). `Page.PdfAsync` "generates a pdf of the
page with `print` css media". Its options include header and footer templates with page numbers,
a tagged PDF and a document outline ([API](https://playwright.dev/dotnet/docs/api/class-page#page-pdf)).

Both run the page in a browser engine, so JavaScript runs and layout matches that browser.

**PdfNetStack.Html.** A layout engine in C# that runs inside your process: the CSS cascade, block,
inline, table, flex and grid layout, floats and positioning, paged media with `@page`, margin
boxes, named pages and running headers and footers, repeated table headers, tagged PDF, web fonts,
and right to left and Arabic text. Since 1.2, `hyphens: auto` hyphenates with the patterns of the
`PdfNetStack.Hyphenation` package. No browser to download or keep updated, and nothing is fetched
from the network unless you pass a resource loader ([HTML to PDF](/guides/html-to-pdf/)). The
result is a `PdfDocument` that can be signed, encrypted or saved as PDF/A with the rest of
PdfNetStack.

## Licensing in plain words

- **Puppeteer Sharp.** MIT ([NuGet](https://www.nuget.org/packages/PuppeteerSharp/25.15.0)).
- **Playwright for .NET.** MIT ([NuGet](https://www.nuget.org/packages/Microsoft.Playwright/1.64.1)).
  The browsers they download come with their own licenses.
- **PdfNetStack.Html.** MIT.

Read each license yourself before you decide. This page is not legal advice.

## Features

| | PdfNetStack.Html 1.2.0 | Puppeteer Sharp 25.15.0 | Playwright for .NET 1.64.1 |
|---|---|---|---|
| License | MIT | MIT | MIT |
| What runs | Managed .NET code in your process | A downloaded browser; PDF output needs Chrome headless | A bundled Node.js runtime and downloaded browsers |
| JavaScript | No | Yes | Yes |
| Rendering | C# layout engine | The browser's engine | The browser's engine |
| Network access during conversion | None without a resource loader | As the browser loads the page; requests can be intercepted and aborted | As the browser loads the page; requests can be routed and aborted |
| Headers and footers with page numbers | CSS margin boxes and running elements | `HeaderTemplate` and `FooterTemplate` options | `HeaderTemplate` and `FooterTemplate` options |
| CSS margin boxes, named pages, running elements | Yes | As the browser supports them | As the browser supports them |
| Bookmarks from headings | Yes, on by default | `Outline` option, off by default | `Outline` option, off by default |
| Tagged PDF | Yes, on by default | `Tagged` option, on by default | `Tagged` option, off by default |
| Sign, encrypt, PDF/A on the result | Yes, with PdfNetStack | Not listed | Not listed |

Sources: [Puppeteer Sharp repository](https://github.com/hardkoded/puppeteer-sharp),
[Puppeteer Sharp `Page` API](https://www.puppeteersharp.com/api/PuppeteerSharp/Page/),
[Puppeteer Sharp `PdfOptions` API](https://www.puppeteersharp.com/api/PuppeteerSharp/PdfOptions/)
and [source at 25.15.0](https://github.com/hardkoded/puppeteer-sharp/blob/v25.15.0/lib/PuppeteerSharp/PdfOptions.cs)
for the defaults, [Playwright browsers](https://playwright.dev/dotnet/docs/browsers),
[Playwright `Page` API](https://playwright.dev/dotnet/docs/api/class-page#page-pdf), the NuGet
pages linked above. Chrome added page margin boxes for printing and PDF export in version 131
([New in Chrome 131](https://developer.chrome.com/blog/new-in-chrome-131)). "Not listed" means
the feature is not in the product's own documentation on the check date.

## When to pick a headless browser

- The page needs JavaScript: charts drawn by script, single page applications, content loaded
  after the page opens. `PdfNetStack.Html` does not run scripts.
- You need output that matches what a specific browser shows on screen, including CSS features
  `PdfNetStack.Html` does not support yet, such as multi-column layout.
- You already run browsers for testing and want PDF output from the same setup.

## When to pick PdfNetStack.Html

- You render your own templates, invoices and reports, and want no browser binaries to download,
  patch and keep in your containers.
- You convert HTML you do not fully trust and want no network access by default.
- You want to sign, encrypt or save the result as PDF/A in the same library, or change it as a
  `PdfDocument` before saving.

## Related

- [Compare overview](/compare/)
- [HTML to PDF](/guides/html-to-pdf/)
- [CSS support](/guides/html-to-pdf/css-support/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-11-12.
