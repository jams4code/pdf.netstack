---
title: PdfNetStack, Ghostscript and qpdf compared
description: 'Ghostscript and qpdf next to PdfNetStack for compressing and restructuring PDFs: how each works, licenses in plain words, and when to pick each.'
draft: true
sidebar:
  label: Ghostscript and qpdf
---

Ghostscript and qpdf are native tools that can make PDFs smaller or change their structure, run
from a .NET application as external processes or native libraries. PdfNetStack does that work in managed .NET code. This
page sets the three side by side.

## What each does well

**Ghostscript.** "An interpreter for the PostScript language and PDF files"
([site](https://www.ghostscript.com/)). Its pdfwrite device re-creates a document: "a new PDF file
is being created, which should look the same as the original". Presets such as `/screen` and
`/ebook` downsample images to 72 and 150 dpi, and the documentation warns that they "may result in
a PDF of poorer quality" than the input. It writes PDF/A versions 1 to 3 at conformance level b
([pdfwrite documentation](https://ghostscript.readthedocs.io/en/latest/VectorDevices.html)), and
its raster devices render pages to PNG, JPEG and TIFF
([devices](https://ghostscript.readthedocs.io/en/latest/Devices.html)).

**qpdf.** "A command-line tool and C++ library that performs content-preserving transformations on
PDF files", supporting linearization, encryption and more. It "does not render PDFs or perform text
extraction" ([repository](https://github.com/qpdf/qpdf)). It can generate object streams,
recompress Flate streams at a chosen level, write linearized output and split pages into separate
files ([command line documentation](https://qpdf.readthedocs.io/en/stable/cli.html)).

**PdfNetStack.** A lossless optimizer in managed .NET code: object streams, a compressed
cross-reference stream, removal of unreachable objects, Flate recompression, PNG prediction for raw
images, lossless JPEG re-encoding and deduplication. It then reads its own output back and
compares it to the source object by object, and returns the original bytes when anything differs
([compression](/guides/compress-pdf/), [fidelity](/concepts/fidelity/)). Lossy JPEG
requantization is opt-in. It also edits, merges, splits and encrypts (1.0, on NuGet).

## Two approaches to smaller files

Ghostscript rebuilds the document from what it interprets. That allows changes such as image
downsampling, and the result is a new document that should look the same as the original.

qpdf and PdfNetStack keep the document's content and change how it is stored. qpdf does not
resample images. PdfNetStack is lossless by default and, from 1.1, can downsample images and
convert raw photographs to JPEG when you opt in, with a perceptual quality gate. It adds a proof:
every rewrite is compared to its source, lossy image changes are checked against the resampled
source, and signed, encrypted and damaged files are returned unchanged by default
([safety model](/concepts/safety-model/)).

## Licensing in plain words

- **Ghostscript.** Available under the GNU AGPL v3 or a commercial license from Artifex
  ([repository](https://github.com/ArtifexSoftware/ghostpdl)). Artifex states that its open source
  editions cannot be deployed as part of a server based application or service without meeting
  the AGPL, which includes disclosing your application's source; otherwise a commercial license is
  required ([Artifex licensing](https://artifex.com/licensing)).
- **qpdf.** Apache License 2.0 ([repository](https://github.com/qpdf/qpdf)).
- **PdfNetStack.** MIT.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Features

| | PdfNetStack | Ghostscript | qpdf |
|---|---|---|---|
| License | MIT | AGPL-3.0 or commercial | Apache-2.0 |
| How it runs | Managed .NET library, in process | Native interpreter | Native C++ library and command line tool |
| How it compresses | Rewrites storage, content unchanged, output verified | Re-creates the document | Rewrites storage, content preserved |
| Object streams | Yes | Not listed | Yes, generated on request |
| Flate recompression | Yes, level 9 by default | Not listed | Yes, chosen level |
| Image downsampling | 1.1, opt-in (`MaxImageDpi`) | Yes, with presets | No |
| JPEG | Lossless re-encoding; opt-in requantization; 1.1: opt-in conversion of raw photographs | Not listed | Not listed |
| Equivalence check of the output | Yes, on every rewrite | Not listed | Not listed |
| Linearized output | No | Not listed | Yes |
| Encryption | AES-256 and AES-128 | Not listed | Yes |
| Split and merge | Yes | Not listed | Split pages into files |
| PDF/A output | 1.1: PDF/A-2b, 2u, 3b, 3u | PDF/A-1, 2 and 3, level b | Not listed |
| Render pages to images | No | Yes | No |

Sources: [Ghostscript site](https://www.ghostscript.com/),
[pdfwrite](https://ghostscript.readthedocs.io/en/latest/VectorDevices.html),
[Ghostscript devices](https://ghostscript.readthedocs.io/en/latest/Devices.html),
[qpdf repository](https://github.com/qpdf/qpdf),
[qpdf command line](https://qpdf.readthedocs.io/en/stable/cli.html).
"Not listed" means the feature is not in the pages cited on the check date.

## When to pick Ghostscript

- You want presets such as `/screen` and `/ebook` that re-create the whole document for size, and
  accept the changes that brings.
- You need to render pages to images, or to process PostScript.
- The AGPL fits your deployment, or a commercial license from Artifex suits you.

## When to pick qpdf

- You need linearized (fast web view) output, which PdfNetStack does not produce.
- You want a structural tool from a shell script or a C++ program, outside .NET.

## When to pick PdfNetStack

- You want smaller files with nothing lost, and a proof of it for every file.
- You want to stay in managed .NET code, with no external process or native binary to ship.
- You want MIT licensing.

## Related

- [Compare overview](/compare/)
- [Compress PDF files](/guides/compress-pdf/)
- [Command line](/cli/)
- [Benchmarks](/benchmarks/)

Product names are trademarks of their respective owners, mentioned for reference only, without
affiliation or endorsement.

Checked on 2026-10-29.
