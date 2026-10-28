# PdfNetStack

Compress, optimize and edit PDF files in .NET. Pure C#, MIT licensed, no native binaries, no
Ghostscript. Created and maintained by Jamal Abdelkhalek at [JADEV](https://github.com/jams4code).

PdfNetStack makes PDFs smaller without changing a single rendered pixel, then proves it: the
output is re-read and compared to the source object by object before it is returned. If anything
differs, you get your original bytes back.

```csharp
using PdfNetStack;

var result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");
```

## Features

- **Lossless PDF compression:** object streams, compressed cross-reference streams, unused object
  removal, Flate recompression at level 9, PNG prediction for raw images, duplicate font, image
  and resource merging.
- **Lossless JPEG optimization:** Huffman tables rebuilt per image, progressive JPEGs rewritten as
  baseline, metadata segments dropped. DCT coefficients stay identical.
- **Optional lossy JPEG compression:** `JpegQuality` requantizes JPEG images at a libjpeg quality,
  never finer than the original.
- **Built-in equivalence proof** on every rewrite, also available on its own as
  `PdfEquivalence.FindDifference` to check the output of any PDF tool.
- **Editing:** open, create, merge, split, reorder, rotate, remove and import pages; document
  info; bookmarks.
- **Encryption:** open RC4, AES-128 and AES-256 protected PDFs; write AES-256 or AES-128 with
  user and owner passwords and permissions.
- **Safe on untrusted files:** bounded parsing and decoding, decompression bomb limits, fuzz
  tested. Signed and encrypted files are never rewritten by the optimizer.

## Editing

```csharp
PdfDocument doc = PdfDocument.Open("contract.pdf");
doc.Pages.RemoveAt(0);
doc.Pages[0].Rotate(90);
doc.Pages.Import(PdfDocument.Open("annex.pdf").Pages);
doc.Info.Title = "Signed contract";
doc.Security.Encrypt(userPassword: "read", ownerPassword: "admin");
doc.Save("contract-final.pdf");   // optimized and verified by default
```

## Compatibility

| Target | Runs on | Dependencies |
|---|---|---|
| `net8.0`, `net10.0` | .NET 8, 9, 10 and later. Trim and Native AOT compatible | None |
| `netstandard2.0` | .NET Framework 4.6.2+, Mono, Unity, .NET Core 2.0+ | `System.Memory` (Microsoft, MIT) |

Tested on Windows, Linux and macOS, on .NET Framework 4.8, .NET 8 and .NET 10.

## Results

On a real-world corpus of 1,296 PDFs (569 MiB), every optimized file was rendered with PDFium and
compared with its original: zero differing pixels, zero verification failures.

## Links

- [Documentation and source](https://github.com/jams4code/pdf.netstack)
- [Command line tool: PdfNetStack.Tool](https://www.nuget.org/packages/PdfNetStack.Tool)
- [Azure Blob Storage pipeline: PdfNetStack.Azure](https://www.nuget.org/packages/PdfNetStack.Azure)
- [Fidelity and verification method](https://github.com/jams4code/pdf.netstack/blob/main/docs/FIDELITY.md)
- [Changelog](https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md)
- [Report an issue](https://github.com/jams4code/pdf.netstack/issues)
