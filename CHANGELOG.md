# Changelog

## 0.2.0 (unreleased)

- Document editing API: `PdfDocument.Open`, `Create`, `Save`, `Merge`, `Split`; pages can be
  added, inserted, removed, moved, rotated and imported from other documents with shared
  resources copied once; `Info` for metadata with Unicode and date support; `Outlines` for
  reading, editing and writing bookmarks while keeping their original styling and actions.
- CLI: `merge`, `split`, `extract`, `rotate`, `meta`, `encrypt`, `decrypt`.
- Encryption: open documents protected with any standard security handler revision (RC4, AES-128,
  AES-256), with user or owner password; write AES-256 or AES-128 with permissions; documents
  opened with the owner password keep their protection on save. `PdfDocument.Security`.
- .NET Standard 2.0 target: .NET Framework 4.6.2+, Mono and Unity. Tests run on .NET Framework 4.8.
- Contribution guide with an AI-assisted contribution policy, and a code of conduct.
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
