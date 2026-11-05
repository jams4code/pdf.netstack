# PdfNetStack

Create, edit, compress and sign PDF files in .NET. Pure C#, MIT licensed, no native binaries, no
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
  and resource removal, compact content streams, Flate recompression at level 9, PNG prediction
  for raw images, duplicate font, image and resource merging, merging of separately embedded
  subsets of one font. Optional linearized output for fast web view.
- **Lossless JPEG optimization:** Huffman tables rebuilt per image, progressive JPEGs rewritten as
  baseline, metadata segments dropped. DCT coefficients stay identical.
- **Optional lossy JPEG compression:** `JpegQuality` requantizes JPEG images at a libjpeg quality,
  never finer than the original.
- **Optional lossy image options:** `MaxImageDpi` downsamples oversampled images,
  `ConvertImagesToJpeg` turns photographs into JPEG, and `MinImageSsim` keeps any image a change
  would visibly hurt. `CompressionEffort.Exhaustive` squeezes Flate streams further for archives.
- **Built-in equivalence proof** on every rewrite, also available on its own as
  `PdfEquivalence.FindDifference` to check the output of any PDF tool.
- **Editing:** open, create, merge, split, reorder, rotate, remove and import pages; document
  info; bookmarks. Large files open on demand from a stream and save as incremental updates.
- **Encryption:** open RC4, AES-128 and AES-256 protected PDFs; write AES-256 or AES-128 with
  user and owner passwords and permissions.
- **Creating documents:** a drawing canvas on new and existing pages; standard 14, TrueType,
  OpenType, WOFF and WOFF2 fonts embedded as subsets, variable font instances; text layout with
  justification, right to left and complex scripts; JPEG, PNG, GIF, BMP, TIFF, WebP and SVG images;
  pages of other PDFs as watermarks and letterheads.
- **Text and images out:** page text in reading order, letters and words with bounds and fonts;
  images per page decoded to pixels, including JPEG 2000, JBIG2 and CCITT scans.
- **Forms and annotations:** create, read and fill AcroForm fields with generated appearances;
  links, notes, markup, stamps, ink and shapes; flattening.
- **Signatures and archiving:** PAdES B-B, B-T, B-LT and B-LTA signing and verification with
  chain, revocation and policy checks; PDF/A-1b to PDF/A-4 output; Factur-X and ZUGFeRD invoices.
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
- [Fidelity and verification method](https://github.com/jams4code/pdf.netstack/blob/main/docs/FIDELITY.md)
- [Changelog](https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md)
- [Report an issue](https://github.com/jams4code/pdf.netstack/issues)
