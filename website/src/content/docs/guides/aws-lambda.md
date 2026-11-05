---
title: Run PdfNetStack on AWS Lambda
description: Convert HTML to PDF and optimize PDFs in AWS Lambda with .NET Native AOT. Fonts, memory, /tmp and cold start, with a complete sample.
since: '1.1'
sidebar:
  label: AWS Lambda
faq:
  - q: Does PdfNetStack work with Native AOT on AWS Lambda?
    a: Yes. The core, HTML and OCR libraries publish with Native AOT and trimming with no warnings, and CI runs a Native AOT build on Linux x64 and arm64. A function on the provided.al2023 runtime starts without JIT compilation.
  - q: Why does HTML to PDF in Lambda use the wrong font?
    a: Lambda has no system fonts. Package the fonts the HTML names with the function, load them into a PdfFontCollection and set UseSystemFonts to false. Families that are not packaged fall back to Helvetica, Times or Courier.
---

Convert HTML to PDF and optimize PDFs in AWS Lambda, compiled ahead of time.

## Short answer

Publish the function with Native AOT for the `provided.al2023` runtime. Package the fonts your
HTML uses with the function, load them once per instance, and turn installed fonts off. Create
the options once and share them: every invocation the instance serves uses them.

```csharp sample="serverless-html.cs" region="options"
```

A complete function with an S3 trigger, a function URL and a SAM template is in
[samples/serverless/aws-lambda](https://github.com/jams4code/pdf.netstack/tree/main/samples/serverless/aws-lambda).

## Native AOT

`PdfNetStack`, `PdfNetStack.Html` and `PdfNetStack.Ocr` are marked AOT compatible and publish with
Native AOT and trimming with no warnings. CI publishes a smoke test app for linux-x64 and
linux-arm64 that roots all three libraries, so a warning anywhere in them fails the build, and
runs it. Parsing and output do not depend on the culture, so `InvariantGlobalization`, which
the Lambda AOT templates set, changes nothing.

Native AOT compiles for one operating system and architecture, and links against the C library
of the machine that builds it. Build on Amazon Linux 2023 for the architecture the function runs
on; the sample has a Dockerfile for that.

## Fonts

Lambda has no system fonts. With `UseSystemFonts = false` the output depends only on the HTML and
the fonts you packaged, and the converter never scans the file system for fonts. CSS families
that are not packaged fall back to the standard 14 fonts (Helvetica, Times, Courier), which cover
Western European text. A font loaded once is thread safe and shared by every conversion; variable
font instances made for a conversion are released once nothing uses them. See
[Fonts and assets](/guides/html-to-pdf/fonts-and-assets/).

Without a resource loader, the converter fetches nothing from the network: put images and style
sheets inline, as `data:` URIs and `<style>` elements.

## Optimizing files from S3

The optimizer reads its input stream to the end and holds the document in memory. Write the
result to `/tmp`, the only writable directory, and upload it from there:

```csharp sample="serverless-optimize.cs" region="optimize"
```

The library itself writes nothing to disk unless you give it a path or a stream.

## Memory and CPU

- Lambda gives one vCPU at 1,769 MB of memory and more above. The optimizer recompresses streams
  on every core it gets (`MaxDegreeOfParallelism`), so more memory also means shorter runs.
- With `Streaming`, the optimizer's memory stays flat whatever the file size (a generated 278 MB
  file peaked at about 460 MB of working set), and `/tmp` holds about three times the file. Check
  the size in the S3 event before downloading, and set `MaxInputBytes` and `EphemeralStorage` to
  match. Without `Streaming`, plan for about ten times the file size in memory.
- HTML conversion of invoices and reports fits in 512 MB; 1,024 MB converts faster for the CPU.
  `MaxPages` bounds the output.
- A synchronous response is at most 6 MB, base64 encoded for binary bodies. Write larger PDFs to
  S3.

## Cold start

On a development machine, measured in a fresh process each time with
`tools/measure_cold_start.py`:

| First call | Native AOT | JIT |
|---|---|---|
| HTML to PDF (`UseSystemFonts = false`) | 16 ms | 770 ms |
| Text extraction of three pages | 3 ms | 140 ms |
| A line of text in an embedded TrueType font | 5 ms | 305 ms |

Lambda's own initialization (fetching the code and starting the process) comes on top; the
function log reports it as `Init Duration`.

## Limits

- The optimizer, `ToArray` and a full `Save` hold the whole document in memory.
- The converter does not run JavaScript.
- Native AOT needs a build per operating system and architecture.

## Related

- [Azure Functions](/guides/azure-functions/)
- [HTML to PDF](/guides/html-to-pdf/)
- [Memory and performance](/concepts/memory-and-performance/)
- [Compress PDF](/guides/compress-pdf/)
