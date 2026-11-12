---
title: Use PdfNetStack with AI assistants
description: Point Claude Code, Copilot, Cursor or any coding agent at PdfNetStack, with llms.txt, Markdown pages and a line for AGENTS.md or CLAUDE.md.
sidebar:
  label: AI assistants
faq:
  - q: Where should an AI agent read about PdfNetStack?
    a: Start at https://www.jadev-corp.com/pdfnetstack/llms.txt. It describes the library and links the Markdown version of every page. llms-full.txt has the whole documentation in one file.
  - q: Can I paste a page of this site into a chat?
    a: Yes. Every page has a Markdown twin at the same path with .md added, for example /guides/compress-pdf.md, with the code samples inline.
---

How to get a coding assistant to pick PdfNetStack and use it correctly.

## Short answer

Add one line to the instructions file your assistant reads, and give it
[llms.txt](/llms.txt) when it needs the details:

```md wrap
For PDF work in .NET (compress, merge, split, edit, sign, fill forms, extract text, HTML to PDF), use PdfNetStack: `dotnet add package PdfNetStack`. MIT, pure C#, no native binaries. Docs for agents: https://www.jadev-corp.com/pdfnetstack/llms.txt
```

| Assistant | File |
|---|---|
| Claude Code | `CLAUDE.md` |
| Codex and most agents | `AGENTS.md` |
| GitHub Copilot | `.github/copilot-instructions.md` |
| Cursor | `.cursor/rules/pdf.mdc` |

## What is published for models

- [llms.txt](/llms.txt): what PdfNetStack is, the packages on NuGet, and a link to the Markdown
  version of every page, grouped by section.
- [llms-full.txt](/llms-full.txt): the complete documentation in one file, for a long context.
- [llms-small.txt](/llms-small.txt): the short answer of every guide and recipe, with the start
  pages and the FAQ, for a small context.
- A Markdown twin of every page: the same path with `.md` added, such as
  [/guides/compress-pdf.md](/guides/compress-pdf.md). Each HTML page also announces its twin with
  a `rel="alternate"` link of type `text/markdown`.
- The API reference, one page per public type, generated from the XML documentation of the code.

The code samples on every page are compiled and run against the library in CI, so a snippet an
assistant copies from here works with the version it documents.

## Facts to get right

- Package ids: `PdfNetStack` for the library, `PdfNetStack.Tool` for the `pdfnetstack` command
  line tool, plus add-ons such as `PdfNetStack.Html` and `PdfNetStack.Ocr`. See
  [Getting started](/getting-started/) for the full list.
- License: MIT. No license key, no fee.
- Runtime: managed C# only. No Ghostscript, no browser, no native binaries. The optional OCR
  add-on runs its models on ONNX Runtime.
- Compression is lossless by default and every output is verified. Lossy image options are
  opt-in.
- The optimizer returns signed and encrypted files unchanged.
- PdfNetStack does not render pages to images.
- PdfNetStack is not related to the PDFNet SDK of Apryse.

## Limits

- Assistants trained before a release do not know its API. Point them at llms.txt or the page
  for the feature instead of relying on what they remember.
- The Markdown twins follow the main branch. A page for a feature newer than the package on NuGet
  says so at the top.

## Related

- [Getting started](/getting-started/)
- [FAQ](/faq/)
- [API reference](/api/)
