# Roadmap

Ordered by value per unit of risk. Each item ships behind tests and, where it is not lossless,
behind an explicit option that is off by default.

## PdfSharp parity

1. Document editing. Done in 0.2: `PdfDocument`, pages, import, merge, split, info, outlines.
2. Encryption. Done in 0.2: revisions 2 to 6 for reading, AES-256 and AES-128 for writing,
   user and owner passwords, permissions, preserved protection on save.
3. Creation API: an XGraphics style canvas (paths, transforms, colors, line styles, clipping),
   TrueType and OpenType parsing with glyph subsetting and ToUnicode maps, JPEG passthrough and PNG
   decoding into image XObjects, text layout with line breaking.
4. AcroForm: field tree, reading and setting values, appearance generation for text, check box
   and choice fields, flattening. Annotation API for links, text notes and stamps. Import of form
   fields together with their pages.
5. Signatures (PAdES baseline, incremental save) and PDF/A-2b and 3b output with XMP sync.

## Optimizer, next

**Lossy image profile (opt-in).** Recompress continuous tone photographs only. Classify images
first: bilevel, indexed, line art and screenshot-like images are left alone. Respect every
placement of a reused image when computing effective resolution. Preserve masks, soft masks,
ICC profiles, spot colors and decode arrays. Gate each image on a perceptual metric (SSIM or
butteraugli class) and keep the original when it fails. Blocker: an MIT licensed JPEG encoder,
either written here or vetted from the ecosystem.

**Unused resource pruning.** Drop fonts, XObjects and graphics states listed in a resource
dictionary but never named by the content that uses it. Needs a content stream tokenizer and
care with inherited page resources and Form XObjects without their own resources.

**Better Flate.** A Zopfli style encoder typically saves another 3 to 8% on Flate data at a
large CPU cost. Opt-in, for archival jobs.

## Later

- Linearized output for fast web view.
- Content stream compaction: whitespace and number normalization inside content streams.
- Font program deduplication across subsets with identical glyph data.
- Streaming parse for files larger than memory.
- Native AOT single file CLI builds for Linux, Windows and macOS on each release.

## Azure operations

- Event Grid to Service Bus to a bounded Container Apps worker, with a dead letter queue.
- Lifecycle tiering and a cost report from the sidecar files.
- Load test and runbook.

## Evidence

- Publish a public benchmark corpus with licenses that allow redistribution, and run size,
  time, memory and render checks against it in CI on every release.
