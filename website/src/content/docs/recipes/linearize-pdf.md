---
title: Linearize a PDF for fast web view in C#
description: Write linearized PDFs in .NET so viewers show the first page before the whole file has downloaded, with every output verified. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Linearize for the web
---

Lay a PDF out first page first, so a browser shows it while the rest is still downloading.

## Short answer

Set `Linearize = true` in `PdfOptimizerOptions` and optimize the file. The output is linearized as
ISO 32000-1 Annex F describes, with hint tables, and verified against the source like any other
rewrite. `Statistics.Linearized` tells you whether it was.

```csharp sample="recipe-linearize-pdf.cs"
```

On the command line: `pdfnetstack optimize report.pdf report.web.pdf --linearize`.

## Notes

- A linearized file has no object streams, so it can be larger than the input. A linearized
  rewrite of a file that is not linearized yet is kept even then.
- The other passes still run: unused resources go, content streams are compacted and duplicates
  merge.
- A page tree that cannot be laid out page by page is written as usual, and the result's reason
  says why.

## Related

- [Compress PDF](/guides/compress-pdf/#fast-web-view)
- [Command line](/cli/)
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [OptimizationStatistics](/api/pdfnetstack/optimizationstatistics/)
