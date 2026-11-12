---
title: PdfNetStack, Ghostscript and qpdf compared
description: 'Ghostscript and qpdf next to PdfNetStack for compressing and restructuring PDFs: how each works, licenses in plain words, and when to pick each.'
sidebar:
  label: Ghostscript and qpdf
---

Ghostscript and qpdf are native tools that can make PDFs smaller or change their structure, run
from a .NET application as external processes or native libraries. PdfNetStack does that work in
managed .NET code. This page sets the three side by side.

## What each does well

**Ghostscript.** "Ghostscript is an interpreter for the PostScript® language and PDF files"
([site](https://www.ghostscript.com/)). The site lists compression, PDF/A conversion and ZUGFeRD
and Factur-X e-invoices among its uses. Its pdfwrite device re-creates a document: "a new PDF file
is being created, which should look the same as the original". Presets such as `/screen` and
`/ebook` downsample images to 72 and 150 dpi, and the documentation warns that they "may result in
a PDF of poorer quality" than the input. pdfwrite writes object streams by default, and can write
linearized and encrypted files, and PDF/A versions 1 to 3 at conformance level b
([pdfwrite documentation](https://ghostscript.readthedocs.io/en/latest/VectorDevices.html)). Its
raster devices render pages to PNG, JPEG and TIFF
([devices](https://ghostscript.readthedocs.io/en/latest/Devices.html)).

**qpdf.** qpdf "is a command-line tool and C++ library that performs content-preserving
transformations on PDF files. It supports linearization, encryption, and numerous other
features." It also splits and merges files, and it "does not render PDFs or perform text
extraction" ([repository](https://github.com/qpdf/qpdf)). Its manual covers generating object
streams, recompressing Flate streams at a chosen level, linearized output, splitting pages into
files, and an opt-in `--optimize-images` that recompresses images as JPEG when that makes them
smaller ([command line documentation](https://qpdf.readthedocs.io/en/stable/cli.html)).

**PdfNetStack.** A lossless optimizer in managed .NET code: object streams, a compressed
cross-reference stream, removal of unreachable objects and unused resources, Flate recompression,
PNG prediction for raw images, lossless JPEG re-encoding and deduplication. By default it then
reads its own output back, compares it to the source object by object, and returns the original
bytes when anything differs ([compression](/guides/compress-pdf/),
[fidelity](/concepts/fidelity/)). Lossy options are opt-in: JPEG requantization since 1.0, and
since 1.1 downsampling, conversion of photographs to JPEG, palettes for drawings and screenshots,
and the `Balanced` and `Smallest` presets. It also edits, merges, splits and encrypts (1.0).

## Two approaches to smaller files

Ghostscript rebuilds the document from what it interprets. That allows changes such as image
downsampling or a new color space, and the result is a new document that should look the same as
the original. Its documentation says that when a modified file is the goal, "pdfwrite performs
these tasks well".

By default, qpdf and PdfNetStack keep the document's content and change how it is stored. qpdf's
`--optimize-images` is opt-in: it recompresses images as JPEG, at a quality set by
`--jpeg-quality`. PdfNetStack is lossless by default and, from 1.1, can downsample images, convert
photographs to JPEG and reduce drawings to a palette when you opt in, with an optional perceptual
quality gate (`MinImageSsim`). It adds a proof, on by default: every rewrite is compared to its
source, lossy image changes are checked against the source image, and signed, encrypted and
damaged files are returned unchanged ([safety model](/concepts/safety-model/)).

## Licensing in plain words

- **Ghostscript.** The GNU AGPL, version 3 or later, in the repository's license file
  ([LICENSE](https://github.com/ArtifexSoftware/ghostpdl/blob/master/LICENSE)), or a commercial
  license from Artifex. Its documentation says GhostPDL, which brings the Ghostscript family
  together, "is available both under the GNU Affero GPL license and for commercial licensing from
  Artifex" ([documentation](https://ghostscript.readthedocs.io/en/latest/Readme.html)). Artifex's
  licensing page states: "You cannot deploy our open-source as part of a server-based application
  or service, without disclosing your own application’s full source code under AGPL to any users
  interacting with it." It adds: "If you can't meet the requirements of the GNU AGPLv3 above, a
  commercial license is required" ([Artifex licensing](https://artifex.com/licensing)).
- **qpdf.** Apache License 2.0 ([repository](https://github.com/qpdf/qpdf)).
- **PdfNetStack.** MIT.

Read each license yourself, or ask your counsel, before you decide. This page is not legal advice.

## Features

| | PdfNetStack | Ghostscript | qpdf |
|---|---|---|---|
| License | MIT | AGPL-3.0 or commercial | Apache-2.0 |
| How it runs | Managed .NET library, in process | Native interpreter | Native C++ library and command line tool |
| How it compresses | Rewrites storage, lossless by default, output verified | Re-creates the document | Rewrites storage, content preserved |
| Object streams | Yes | Yes, on by default (`WriteObjStms`) | Yes, generated on request |
| Flate recompression | Yes, level 9 by default | Yes, level set by `StreamEffort` | Yes, chosen level |
| Image downsampling | 1.1, opt-in (`MaxImageDpi`) | Yes, with presets or per image type | Not listed |
| JPEG | Lossless re-encoding; opt-in requantization; 1.1: opt-in conversion of photographs | Encodes images as JPEG; passes JPEG data through by default (`PassThroughJPEGImages`) | Opt-in: images to JPEG (`--optimize-images`) |
| Equivalence check of the output | Yes, on by default | Not listed | Not listed |
| Linearized output | 1.1, opt-in (`Linearize`) | Yes (`-dFastWebView`) | Yes |
| Encryption | AES-256 and AES-128 | Yes, revision 2 or 3 | Yes |
| Split and merge | Yes | Page selection (`-sPageList`), in a re-created file | Yes |
| PDF/A output | 1.1: PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e, 4f | PDF/A-1, 2 and 3, level b | No, per its manual |
| Render pages to images | No | Yes | No |

Sources: [Ghostscript site](https://www.ghostscript.com/),
[pdfwrite](https://ghostscript.readthedocs.io/en/latest/VectorDevices.html),
[Ghostscript devices](https://ghostscript.readthedocs.io/en/latest/Devices.html),
[Ghostscript usage](https://ghostscript.readthedocs.io/en/latest/Use.html) (page selection),
[qpdf repository](https://github.com/qpdf/qpdf),
[qpdf command line](https://qpdf.readthedocs.io/en/stable/cli.html).
"Not listed" means the feature is not in the pages cited on the check date.

## When to pick Ghostscript

- You want presets such as `/screen` and `/ebook` that re-create the whole document for size, and
  accept the changes that brings.
- You need to render pages to images, or to process PostScript.
- The AGPL fits your deployment, or a commercial license from Artifex suits you.

## When to pick qpdf

- You want to check existing linearized files (`--check-linearization`) or inspect a file's
  structure from the command line.
- You want a structural tool from a shell script or a C++ program, outside .NET.
- You want Apache-2.0 licensing.

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

Checked on 2026-11-12.
