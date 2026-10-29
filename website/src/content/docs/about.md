---
title: About PdfNetStack
description: Who builds PdfNetStack, why it exists, how it is licensed and how to contribute. An original MIT licensed PDF library for .NET by JADEV.
sidebar:
  label: About
---

PdfNetStack is a PDF library and command line tool for .NET, written in C# and published under
the MIT license.

## Why it exists

PdfNetStack gives .NET developers a PDF toolkit that is permissively licensed all the way down,
safe to run on files from strangers, and honest about what it changes. It optimizes, creates,
edits, merges, splits and protects PDFs, extracts their text and images, and converts HTML and CSS
to PDF.

It started with one narrow job: making PDFs smaller without shelling out to Ghostscript, without a
copyleft or commercial dependency, and without silently losing forms, tags or metadata. That job
set the rules the rest of the library follows: lossless by default, every rewrite verified,
bounded on hostile input, and no native code in the core packages.

## Who builds it

PdfNetStack is created and maintained by [Jamal Abdelkhalek](https://www.jadev-corp.com/jamal) at
[JADEV](https://www.jadev-corp.com/en). The packages on NuGet are published by
the `jadev` account.

- Source and issues: [github.com/jams4code/pdf.netstack](https://github.com/jams4code/pdf.netstack)
- Packages: [PdfNetStack on NuGet](https://www.nuget.org/packages/PdfNetStack)
- Company: [jadev-corp.com](https://www.jadev-corp.com/en)

The repository is named `pdf.netstack`. The product, the packages and the namespaces are all
called PdfNetStack.

## Original work

PdfNetStack is a greenfield project. Its code is original and written from the published
specifications: ISO 32000 for PDF, OpenType, Unicode, and the HTML and CSS standards.

Over years of building software, its author has used many PDF and document tools, and their
strengths shaped the feature set and the API: PdfSharp and MigraDoc, PDFTron (now Apryse), iText,
QuestPDF, wkhtmltopdf, WeasyPrint, PDFium, pdf.js, Poppler and qpdf. HarfBuzz, libjpeg, libtiff
and PDFium also serve as reference implementations in the test suite, where outputs are compared.

No source code from any of these projects is included. The few third party data tables the
library uses, such as the Unicode character data and the Adobe Glyph List, are listed with their
licenses in
[THIRD-PARTY-NOTICES.md](https://github.com/jams4code/pdf.netstack/blob/main/THIRD-PARTY-NOTICES.md).
Product names are trademarks of their respective owners and are mentioned for reference only,
without affiliation or endorsement. PdfNetStack is not related to the PDFNet SDK of Apryse.

## License

MIT. See [LICENSE](https://github.com/jams4code/pdf.netstack/blob/main/LICENSE). On .NET 8 and
later the core package has no dependencies; on .NET Standard 2.0 it uses Microsoft's
`System.Memory`, also MIT. CI blocks any runtime package that is not on a reviewed license
allowlist. Runtime dependency licenses are listed in THIRD-PARTY-NOTICES.md.

## Contributing

Bug reports with a file that is handled wrongly are the most useful contribution. Issues labelled
`good first issue` and `help wanted` are open to anyone, and roadmap items start with an issue to
agree on the approach. The rules are in
[CONTRIBUTING.md](https://github.com/jams4code/pdf.netstack/blob/main/CONTRIBUTING.md), including
the policy for AI assisted contributions. Security issues follow the
[security policy](https://github.com/jams4code/pdf.netstack/blob/main/SECURITY.md).

## This site

This site is built from the `website` folder of the repository, in the same pull requests as the
code it describes. The API reference is generated from the XML documentation of the source, and
every C# sample is compiled and run in CI. Each page is also available as Markdown at the same
address with `.md` instead of the trailing slash, and the whole documentation is in
[llms.txt](/llms.txt) for tools and assistants.
