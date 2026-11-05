---
title: Get started with PdfNetStack
description: Install PdfNetStack from NuGet, compress your first PDF, edit and create documents in C#, and build against the source for the 1.1 features.
sidebar:
  label: Getting started
faq:
  - q: Is PdfNetStack free for commercial use?
    a: Yes. It is MIT licensed, and on .NET 8 and later the core package has no dependencies. On .NET Standard 2.0 it uses Microsoft's System.Memory, also MIT.
  - q: Which .NET versions does PdfNetStack support?
    a: .NET 8, 9, 10 and later through the net8.0 and net10.0 targets, and .NET Framework 4.6.2 to 4.8.1, Mono and Unity through netstandard2.0.
---

Install the package, run one call, and you have a smaller PDF that is proven equivalent to the
original.

## Short answer

Add the `PdfNetStack` package and call the optimizer. That is the whole setup: no native
binaries, no Ghostscript, nothing else to install.

```bash
dotnet add package PdfNetStack
```

```csharp sample="compress.cs"
```

With the .NET 10 SDK you can also save the code above as `compress.cs` and run it with
`dotnet run compress.cs`: the `#:package` line pulls the package in.

## Requirements

| Target | Runs on | Dependencies |
|---|---|---|
| `net8.0`, `net10.0` | .NET 8, 9, 10 and later. Trim and Native AOT compatible | None |
| `netstandard2.0` | .NET Framework 4.6.2 to 4.8.1, Mono, Unity, Xamarin, .NET Core 2.0+ | `System.Memory` (Microsoft, MIT) |

The same managed code runs on Windows, Linux and macOS. CI runs the test suite on .NET Framework
4.8, .NET 8 and .NET 10. One difference: .NET Standard's zlib has no level 9, so
`CompressionEffort.Maximum` compresses a little less there.

## Packages

| Package | What it is | On NuGet |
|---|---|---|
| `PdfNetStack` | The library | 1.1.0 |
| `PdfNetStack.Tool` | The `pdfnetstack` command line tool | 1.1.0 |
| `PdfNetStack.Html` | HTML and CSS to PDF | 1.1.0 |
| `PdfNetStack.Hyphenation` | Hyphenation patterns for paragraphs and `hyphens: auto` | Next release |
| `PdfNetStack.Ocr` | OCR add-on for scanned PDFs | 1.1.0 |
| `PdfNetStack.Ocr.Models` | The OCR models, versioned on their own | 1.0.0 |
| `PdfNetStack.Ocr.Tool` | The `pdfnetstack-ocr` command line tool, models included | 1.1.0 |

```bash
dotnet add package PdfNetStack            # library
dotnet tool install -g PdfNetStack.Tool   # command line: pdfnetstack
```

## Your first edit

Open a document, change its pages, save it. Saving runs the optimizer and its verification by
default, so edited files come out compact.

```csharp sample="edit-first.cs" region="short"
```

More in [Edit pages](/guides/edit-pages/).

## Your first new PDF

Version 1.1 adds a drawing canvas. The origin is the top left corner of the page and units are
points.

```csharp sample="create-first.cs" region="short"
```

![The page this code draws: a dark blue band with the words Hello, PDF](/previews/hello.webp)

More in [Drawing](/guides/drawing/) and [Fonts and text](/guides/fonts-and-text/).

## Build from source

Every feature on this site is in the 1.1 packages on NuGet. To try what is in the main branch
before the next release, build against a clone of the repository. You need the .NET 10 SDK.

```bash
git clone https://github.com/jams4code/pdf.netstack
dotnet build pdf.netstack/PdfNetStack.sln -c Release
```

In a .NET 10 file-based app, reference the project instead of the package, here for a clone in
the same folder as the file:

```csharp
#:project pdf.netstack/src/PdfNetStack/PdfNetStack.csproj
```

In a project file, use a project reference, here for a clone next to the project folder:

```xml
<ItemGroup>
  <ProjectReference Include="..\pdf.netstack\src\PdfNetStack\PdfNetStack.csproj" />
  <!-- HTML to PDF -->
  <ProjectReference Include="..\pdf.netstack\src\PdfNetStack.Html\PdfNetStack.Html.csproj" />
</ItemGroup>
```

Unreleased API can still change before its release. The [changelog](/changelog/) lists every
change by version.

## How the samples on this site run

Every C# sample on this site is a file in the repository's `website/samples` folder. CI compiles
and runs each of them against the source the site was built from, checks that every PDF it writes
opens again, and renders the images you see from those PDFs. The OCR samples are compiled only,
because they need model files CI does not download.

## Next steps

- [Compress PDF](/guides/compress-pdf/): options, results and what the optimizer refuses
- [Command line](/cli/): the same features from a terminal
- [Recipes](/recipes/): short answers to single tasks
- [API reference](/api/): every public type
