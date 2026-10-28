# Changelog

## 0.1.0 (unreleased)

First public version.

- Lossless optimizer: unreachable object removal, object streams, compressed xref streams,
  stream recompression, PNG prediction for raw images, iterative deduplication, compact syntax.
- Equivalence verifier on every rewrite, public as `PdfEquivalence.FindDifference`.
- Refuses encrypted, signed, incrementally updated and damaged files unless allowed.
- PDF/A-1 detection with classic xref output.
- `pdfnetstack` command line tool: optimize, batch, inspect, verify.
- `PdfNetStack.Azure`: Blob Storage pipeline with content-addressed output and provenance sidecar.
