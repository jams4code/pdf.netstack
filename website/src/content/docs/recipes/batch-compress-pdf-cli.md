---
title: Batch compress PDFs from the command line
description: Compress every PDF in a folder on Windows, Linux or macOS with the pdfnetstack tool, losslessly, with a CSV report per file. MIT licensed.
since: '1.0'
sidebar:
  label: Batch compress a folder
---

Optimize a whole folder of PDFs in one command, with a report of what each file gained.

## Short answer

Install the `pdfnetstack` tool and run `batch` with an input and an output folder. Each file is
optimized losslessly and verified, and `--report` writes one CSV row per document. Without an
output folder, `batch` only measures and writes nothing.

```bash
dotnet tool install -g PdfNetStack.Tool

pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./incoming                     # measure only, writes nothing
pdfnetstack batch ./incoming ./optimized --jpeg-quality 60 --jobs 4
```

`--jobs` sets how many documents run in parallel, `--threads` the threads per document. The tool
needs the .NET 8 or later runtime.

## The same in C#

```csharp sample="recipe-batch-compress-pdf-cli.cs"
```

Files that are already well compressed come back as `OriginalRetained`: the rewrite saved less
than `MinSavingPercent` (1% by default), so the original bytes were kept.

## Related

- [Command line](/cli/): every command and option
- [Compress PDF](/guides/compress-pdf/)
- API: [PdfOptimizer](/api/pdfnetstack/pdfoptimizer/)
