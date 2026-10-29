---
title: Downsample images in a PDF in C#
description: Lower the resolution of oversized images in a PDF from .NET with MaxImageDpi, measured where each image is drawn, the result verified. MIT licensed.
since: '1.1'
sidebar:
  label: Downsample images
---

Bring every image down to the resolution the page actually shows it at.

## Short answer

Set `MaxImageDpi` on `PdfOptimizerOptions`. The optimizer measures each image at the largest size
it is drawn anywhere in the document, and resamples images drawn above 1.5 times the target down
to the target, with a Lanczos filter. A raw image stays lossless after resampling and a JPEG stays a
JPEG, so nothing changes format unless you also set `ConvertImagesToJpeg`.

```csharp sample="recipe-downsample-pdf-images.cs" region="short"
```

The sample's photograph, 1600 pixels drawn 3 inches wide (about 530 dpi), becomes a 150 dpi image
and the file shrinks by 93.8%.

## Notes

- Masks, bilevel, indexed, 16 bit, Lab, Separation and DeviceN images, JPEG 2000, JBIG2 and CCITT
  images, and images inside Type 3 glyphs keep their resolution.
- Each downsampled image is decoded and compared with an exact resample of the original: same
  components and aspect, mean colors kept, and a minimum SSIM. `MinImageSsim` raises that minimum.
- Command line: `pdfnetstack optimize in.pdf out.pdf --max-dpi 150`.

## Related

- [Compress PDF](/guides/compress-pdf/#lossy-image-options)
- [Reduce PDF size for email](/recipes/reduce-pdf-size/)
- [Command line](/cli/)
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [OptimizationStatistics](/api/pdfnetstack/optimizationstatistics/)
