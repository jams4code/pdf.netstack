---
title: Safety model
description: Which PDFs the PdfNetStack optimizer refuses to rewrite, how hostile input is bounded, and why the output is always the verified rewrite or your input.
sidebar:
  label: Safety model
faq:
  - q: Is PdfNetStack safe to run on PDFs from untrusted sources?
    a: It is built for it. Parsing and decoding are bounded in depth, size and work, damaged objects never crash a run, and the optimizer returns the input unchanged rather than throwing on a file it cannot read.
  - q: Will the optimizer ever break a signed PDF?
    a: No. Any file with a signature byte range, a signature dictionary, usage rights or a document security store is returned byte for byte.
---

The optimizer has one promise: the file it writes is either a rewrite proven equivalent to your
input, or your input unchanged. This page lists the rules behind that promise.

## Short answer

Before rewriting, the optimizer checks the file. Encrypted, signed, incrementally updated, damaged
and oversized files are returned unchanged with the disposition `UnsupportedRetained` and a reason.
After rewriting, it parses its own output and compares it to the source; any difference returns the
input with `OriginalRetained`. Every parser and decoder is bounded, so hostile input ends in a
result, not a hang or a crash.

## Files returned unchanged

| The file is | Reason reported | To rewrite it anyway |
|---|---|---|
| Not a readable PDF | `Not a readable PDF: ...` | Not possible |
| Larger than `MaxInputBytes` (1 GiB) | `Input exceeds the configured size limit.` | Raise `MaxInputBytes` |
| Encrypted | `Encrypted PDF.` | Open it with the owner password and save it, see [Encryption](/guides/encryption/) |
| Digitally signed, or with usage rights: any `/ByteRange`, signature dictionary, `/Perms` or `/DSS` | `Digitally signed PDF.` or `Digitally signed or usage-rights enabled PDF.` | Never: a rewrite would invalidate the signatures |
| Incrementally updated (more than one revision) | `PDF has N revisions (incremental updates). ...` | `AllowIncrementalUpdates`: the visible document is unchanged, earlier revisions are discarded |
| Damaged: cross-reference reconstruction, repaired stream lengths, unreadable objects, or references whose generation numbers disagree with the cross-reference data | `PDF is damaged and needed repair. ...` and similar | `AllowRepairedFiles`: the output is a clean file, but another reader may have repaired the input differently |

A rewrite is also discarded, with `OriginalRetained`, when it saves less than `MinSavingPercent`
(1% by default), when verification finds a difference, or when the rewrite fails for any other
reason. The reason says which.

PDF/A-1 files are detected from their XMP metadata and written with a classic cross-reference table
and no object streams, because PDF/A-1 predates both.

## Hostile input is bounded

| Threat | Bound |
|---|---|
| Deep nesting of arrays and dictionaries | Nesting depth capped at 256 |
| Chains of references | Resolution depth capped |
| Decompression bombs | Each decoded stream is capped by `MaxDecodedStreamBytes` (256 MiB by default); streams over the limit are left untouched |
| `/Prev` loops in the cross-reference chain | Detected and terminated |
| Wrong stream lengths | Recovered from the `endstream` keyword |
| Unreadable objects | Recorded, never thrown out of the optimizer |
| JPEG data | Oversubscribed Huffman tables are refused, at most 64 scans per image, frames that claim more blocks than their data can code are refused |
| Content streams | Operations, nesting of forms and graphics states, and decoded bytes are capped |
| Images | Pixel counts are capped before any buffer is allocated: 2^28 by default, lower per document with `PdfLoadOptions.MaxImagePixels` (1.1) and per image file with `PdfImage.Load(data, page, maxPixels)` |
| Fonts | Damaged font files raise `FontFormatException`; WOFF and WOFF2 output is capped at 128 MB |

Opening a document for editing applies the same bounds. A file that cannot be read raises
`PdfFormatException`, the one exception the parser documents for bad data, and decoders such as
the image, font and SVG readers either raise their documented exception or stop at their limit.

## Verification

The verifier walks the source and output graphs in lockstep from the trailer. Dictionaries must
have the same keys and values, arrays the same items, references must line up, and every stream
must decode to identical bytes with identical remaining filters. JPEG images are compared by DCT
coefficient. Encoding keys (`/Length`, `/Filter`, `/DecodeParms`, `/DL`) are the only other
differences it accepts. With `JpegQuality` set, a JPEG image may also differ by exactly the
requantization the optimizer applied, and by nothing else.

The structural passes of 1.1 change more than encoding, and none is taken on trust. The verifier
runs its own analysis of the source: a resource entry may be missing only where no content can
name it, a rewritten content stream must read as the same tokens, a merged font program must
keep every glyph shown with the same outline and metrics, and streams may merge when they decode
to the same bytes. Anything the analysis cannot read for certain keeps its resources, and when
its work budget runs out nothing is pruned. The [fidelity](/concepts/fidelity/) page gives every
rule.

The lossy image options of 1.1 (`MaxImageDpi`, `ConvertImagesToJpeg`) change pixels, so their proof
is a bound rather than an identity, and only for the images the optimizer changed. A changed image
must keep its components and aspect, must not grow, and is decoded and compared with the source
resampled to its new size: mean colors, a coarse thumbnail, and luminance SSIM of at least
`MinImageSsim` (0.5 without a gate). Soft masks, masks, stencil and color keyed images must still
match exactly, and every other part of the document as before.

`Verify = false` skips this step. Turn it off only for inputs a validated pipeline has already
checked. The full method is on the [fidelity](/concepts/fidelity/) page.

## Tests behind the rules

The test suite covers every refusal rule, truncated and malicious input for every decoder, and
fuzzing: mutated files (byte flips, deletions, truncation, injected tokens, duplicated regions)
must never throw and must produce either the exact input or a verified rewrite. CI runs 600 fuzz
cases on every build.

## Related

- [Compress PDF](/guides/compress-pdf/): the options named on this page
- [Fidelity and verification](/concepts/fidelity/): what equivalent means, and how it is checked
- [Memory and performance](/concepts/memory-and-performance/): sizing the limits for your workload
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [OptimizationDisposition](/api/pdfnetstack/optimizationdisposition/), [PdfFormatException](/api/pdfnetstack/pdfformatexception/)
