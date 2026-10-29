---
title: Check two PDFs for equivalence in C#
description: "Check another PDF tool's output against its input in .NET: same objects, same decoded content, JPEGs compared by coefficient. MIT licensed."
since: '1.0'
sidebar:
  label: Check equivalence
---

Find out whether a PDF written by any tool still holds the same document as the original.

## Short answer

`PdfEquivalence.FindDifference` walks both object graphs from the trailer and compares every
dictionary, array and reference, and every stream after decoding. It returns `null` when the two
are equivalent, or a description of the first difference.

```csharp sample="recipe-check-pdf-equivalence.cs" region="short"
```

```bash
dotnet add package PdfNetStack
```

In the sample, the optimized copy is equivalent and the copy with a page removed is not:
`trailer/Root/Pages/Kids: array length 3 vs 2`.

## Notes

This is the check the optimizer runs on its own output. Encoding keys such as `/Length`, `/Filter`
and `/DecodeParms` may differ. JPEG images must match coefficient by coefficient; pass
`allowJpegRequantization: true` to accept requantized JPEGs. From 1.1, an overload that also takes
`minImageSsim` accepts images that another tool downsampled or converted to JPEG, when they match
the source resampled to their size; soft masks, masks and stencil images must still match exactly.
The method and its limits are on the
[fidelity](/concepts/fidelity/) page.

## Related

- [Compress PDF](/guides/compress-pdf/)
- [Command line](/cli/): `pdfnetstack verify original.pdf other.pdf`
- API: [PdfEquivalence](/api/pdfnetstack/pdfequivalence/)
