---
title: Run PdfNetStack on Azure Functions
description: Convert HTML to PDF and optimize PDFs in Azure Functions on the .NET isolated worker. Fonts, memory, temporary files and cold start.
since: '1.1'
sidebar:
  label: Azure Functions
faq:
  - q: Which Azure Functions plan suits PDF work?
    a: Flex Consumption, with 2,048 MB instances for optimization. With the streaming optimizer, memory stays flat whatever the file size, and the temporary directory holds about three times each file. The Consumption plan caps an instance at 1.5 GB.
  - q: How do I make the first request faster on Azure Functions?
    a: Publish ready to run. On a development machine the first HTML conversion in a fresh process took about 470 ms ready to run against about 800 ms with the JIT alone.
---

Convert HTML to PDF and optimize PDFs in Azure Functions, on the .NET isolated worker.

## Short answer

Package the fonts your HTML uses with the app, load them once, and turn installed fonts off.
Create the options once: every invocation the worker runs shares them, concurrent ones included.
Publish ready to run.

```csharp sample="serverless-html.cs" region="options"
```

A complete app with an HTTP trigger for HTML and an Event Grid blob trigger for optimization is
in [samples/serverless/azure-functions](https://github.com/jams4code/pdf.netstack/tree/main/samples/serverless/azure-functions).

## Fonts

Function hosts have few fonts installed or none, and what is there can change with the image.
With `UseSystemFonts = false` the output depends only on the HTML and the fonts you packaged.
CSS families that are not packaged fall back to the standard 14 fonts. A loaded font is thread
safe and shared by concurrent conversions; variable font instances made for a conversion are
released once nothing uses them. See [Fonts and assets](/guides/html-to-pdf/fonts-and-assets/).

Without a resource loader, the converter fetches nothing from the network: put images and style
sheets inline.

## Optimizing blobs

The optimizer reads its input stream to the end and holds the document in memory. Write the
result to the temporary directory and upload it from there:

```csharp sample="serverless-optimize.cs" region="optimize"
```

`Path.GetTempPath()` is the instance's local storage. It is limited and differs by plan, so cap
the input size. The library itself writes nothing to disk unless you give it a path or a stream.

## Memory and concurrency

- Flex Consumption runs instances of 512 MB, 2,048 MB or 4,096 MB, and runs several invocations
  on one instance. Size memory for the concurrent work. With `Streaming`, each document being
  optimized takes a few hundred MB whatever its size, and about three times its size in the
  temporary directory; without it, about ten times the file size in memory.
- The Consumption plan caps an instance at 1.5 GB for all its concurrent invocations.
- `maxDegreeOfParallelism` under `extensions.blobs` in `host.json` bounds concurrent blobs per
  instance; `MaxDegreeOfParallelism` in `PdfOptimizerOptions` bounds the threads of one
  document.

## Cold start

Publish ready to run (`-p:PublishReadyToRun=true` with a runtime identifier), so less code is
compiled on the first request. On a development machine, in a fresh process each time, with
`tools/measure_cold_start.py`:

| First call | Ready to run | JIT |
|---|---|---|
| HTML to PDF (`UseSystemFonts = false`) | 470 ms | 800 ms |
| Text extraction of three pages | 110 ms | 250 ms |
| A line of text in an embedded TrueType font | 330 ms | 570 ms |

The machine was busy during this run, so compare the columns rather than the absolute times.
The [AWS Lambda](/guides/aws-lambda/) guide has Native AOT figures.

## Limits

- The optimizer, `ToArray` and a full `Save` hold the whole document in memory.
- The converter does not run JavaScript.

## Related

- [AWS Lambda](/guides/aws-lambda/)
- [HTML to PDF](/guides/html-to-pdf/)
- [Memory and performance](/concepts/memory-and-performance/)
- [Compress PDF](/guides/compress-pdf/)
