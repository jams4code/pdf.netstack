# Changelog

## 0.2.0 (unreleased)

- Document editing API: `PdfDocument.Open`, `Create`, `Save`, `Merge`, `Split`; pages can be
  added, inserted, removed, moved, rotated and imported from other documents with shared
  resources copied once; `Info` for metadata with Unicode and date support; `Outlines` for
  reading, editing and writing bookmarks while keeping their original styling and actions.
- CLI: `merge`, `split`, `extract`, `rotate`, `meta`.
- Hardening from review: bounded resolution depth, bounded object regions, predictor row
  clamping, identity checks in deduplication and verification, per document decode budget.

## 0.1.0

First public version.

- Lossless optimizer: unreachable object removal, object streams, compressed xref streams,
  stream recompression, PNG prediction for raw images, iterative deduplication, compact syntax.
- Equivalence verifier on every rewrite, public as `PdfEquivalence.FindDifference`.
- Refuses encrypted, signed, incrementally updated and damaged files unless allowed.
- PDF/A-1 detection with classic xref output.
- `pdfnetstack` command line tool: optimize, batch, inspect, verify.
- `PdfNetStack.Azure`: Blob Storage pipeline with content-addressed output and provenance sidecar.
