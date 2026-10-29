---
title: Reduce PDF size for email in C#
description: Make a PDF small enough to email in .NET by downsampling oversized images and storing photographs as JPEG, every output verified. MIT licensed.
since: '1.1'
sidebar:
  label: Reduce PDF size
---

Shrink a PDF further than lossless compression allows, by lowering the resolution and quality of
its photographs.

## Short answer

Set the lossy image options on `PdfOptimizerOptions` and optimize as usual. `MaxImageDpi`
downsamples images drawn sharper than the page needs, `ConvertImagesToJpeg` stores raw
photographs as JPEG, and `JpegQuality` sets the quality of new JPEGs and requantizes existing ones.
Text, vectors, fonts, line art and screenshots are untouched, and the output is still verified
against the source.

```csharp sample="recipe-reduce-pdf-size.cs" region="short"
```

On the sample brochure, a lossless 1600 pixel photograph drawn about 6 inches wide, this takes the
file from 2,452,280 to 62,318 bytes.

## Variations

- Add `MinImageSsim = 0.95` to keep any image whose change would be visible; that image then keeps
  its own encoding, or stays raw after downsampling.
- Version 1.0 on NuGet has `JpegQuality` only: it requantizes existing JPEG images and does
  nothing else lossy.
- From the command line: `pdfnetstack optimize in.pdf out.pdf --max-dpi 150 --to-jpeg --jpeg-quality 80`.

## Related

- [Compress PDF](/guides/compress-pdf/#lossy-image-options): every lossy option and what the proof checks
- [Downsample images in a PDF](/recipes/downsample-pdf-images/)
- [Command line](/cli/)
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/)
