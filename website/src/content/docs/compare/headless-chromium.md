---
title: PdfNetStack and headless Chromium compared
description: Puppeteer Sharp and Playwright print HTML to PDF through a browser. How that compares with PdfNetStack.Html, a C# layout engine, and when to use each.
draft: true
sidebar:
  label: Puppeteer Sharp and Playwright
---

Puppeteer Sharp and Playwright for .NET drive a real browser from C#, and a browser can print a
page to PDF. `PdfNetStack.Html` converts HTML and CSS to PDF with a layout engine written in C#,
without a browser. It is part of PdfNetStack 1.1, in the main branch and not yet on NuGet.

## What each does well

**Puppeteer Sharp.** A ".NET port of the official Node.JS Puppeteer API" for controlling headless
Chrome, with Firefox support through WebDriver BiDi. Its examples download a browser with
`BrowserFetcher`, open a page and call `page.PdfAsync`
([repository](https://github.com/hardkoded/puppeteer-sharp)).

**Playwright for .NET.** A browser automation library from Microsoft. Its NuGet package bundles a
Node.js runtime for each platform ([NuGet](https://www.nuget.org/packages/Microsoft.Playwright/1.64.1),
package contents), and `playwright.ps1 install` downloads Chromium, Firefox and WebKit builds
([browsers](https://playwright.dev/dotnet/docs/browsers)). `Page.PdfAsync` "generates a pdf of the
page with `print` css media" ([API](https://playwright.dev/dotnet/docs/api/class-page#page-pdf)).

Both run the page in a browser engine, so JavaScript runs and layout matches that browser.

**PdfNetStack.Html.** A layout engine in C# that runs inside your process: the CSS cascade, block,
inline, table, flex and grid layout, floats and positioning, paged media with `@page`, margin
boxes, named pages and running headers and footers, repeated table headers, tagged PDF, web fonts,
and right to left and Arabic text. No browser to download or keep updated, and nothing is fetched
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

| | PdfNetStack.Html | Puppeteer Sharp 25.15.0 | Playwright for .NET 1.64.1 |
|---|---|---|---|
| License | MIT | MIT | MIT |
| What runs | Managed .NET code in your process | A downloaded Chrome or Firefox | A bundled Node.js runtime and downloaded browsers |
| JavaScript | No | Yes | Yes |
| Rendering | C# layout engine | The browser's engine | The browser's engine |
| Network access during conversion | None without a resource loader | As the browser loads the page | As the browser loads the page |
| Paged media: margin boxes, named pages, running elements | Yes | Not listed | Not listed |
| Tagged PDF | Yes, on by default | Not listed | Not listed |
| Sign, encrypt, PDF/A on the result | Yes, with PdfNetStack (1.1) | Not listed | Not listed |
| Available | 1.1, not yet on NuGet | Released | Released |

Sources: [Puppeteer Sharp repository](https://github.com/hardkoded/puppeteer-sharp),
[Playwright browsers](https://playwright.dev/dotnet/docs/browsers),
[Playwright API](https://playwright.dev/dotnet/docs/api/class-page#page-pdf), the NuGet pages linked
above. "Not listed" means the feature is not in the product's own documentation on the check date.

## When to pick a headless browser

- The page needs JavaScript: charts drawn by script, single page applications, content loaded
  after the page opens. `PdfNetStack.Html` does not run scripts.
- You need output that matches what a specific browser shows on screen, including CSS features
  `PdfNetStack.Html` does not support yet, such as multi-column layout.
- You already run browsers for testing, and adding PDF output costs you nothing extra.
- You need HTML to PDF today from a released package.

## When to pick PdfNetStack.Html

- You render your own templates, invoices and reports, and want no browser binaries to download,
  patch and keep in your containers.
- You convert HTML you do not fully trust and want no network access by default.
- You need paged media features such as running headers and named pages, tagged PDF, or to sign
  and archive the result in the same library.

## Related

- [Compare overview](/compare/)
- [HTML to PDF](/guides/html-to-pdf/)
- [CSS support](/guides/html-to-pdf/css-support/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-10-29.
