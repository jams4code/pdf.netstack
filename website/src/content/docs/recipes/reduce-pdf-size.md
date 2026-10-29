---
title: Reduce PDF size for email in C#
description: Make a PDF small enough to email in .NET with lossless optimization plus opt-in JPEG requantization, every output verified. MIT licensed.
since: '1.0'
sidebar:
  label: Reduce PDF size
---

Shrink a PDF further than lossless compression allows, by lowering the quality of its JPEG images.

## Short answer

Set `JpegQuality` on `PdfOptimizerOptions` and optimize as usual. On top of the lossless passes,
JPEG images are requantized at that libjpeg quality (1 to 100), never finer than they were. Text,
vectors, fonts and other images are untouched, and the output is still verified against the
source.

```csharp sample="recipe-reduce-pdf-size.cs"
```

```bash
dotnet add package PdfNetStack
```

## Notes

Requantization works on the JPEG coefficients, so the color model cannot change, and soft masks
stay lossless. It does not downsample images or convert raw images to JPEG. Without `JpegQuality`
the optimizer is lossless.

## Related

- [Compress PDF](/guides/compress-pdf/)
- [Command line](/cli/): `pdfnetstack optimize in.pdf out.pdf --jpeg-quality 60`
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/)
