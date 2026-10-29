# PdfNetStack

Compress, optimize and edit PDF files in .NET. Pure C#, MIT licensed, no native binaries, no
dependencies on .NET 8 and later.

[![CI](https://github.com/jams4code/pdf.netstack/actions/workflows/ci.yml/badge.svg)](https://github.com/jams4code/pdf.netstack/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PdfNetStack.svg)](https://www.nuget.org/packages/PdfNetStack)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

PdfNetStack makes PDFs smaller without changing a single rendered pixel. It rewrites the file
with compressed object streams and a compressed cross-reference stream, drops objects nothing
points to, recompresses streams, adds PNG prediction to raw images, and merges duplicate fonts,
images and resources. Then it reads its own output back and proves, object by object, that the
document is the same as the one you gave it. If anything is off, you get your original bytes back.

```csharp
using PdfNetStack;

var result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");
```

## Purpose

PdfNetStack exists to give every .NET developer a PDF toolkit that is permissively licensed all
the way down, safe to run on files from strangers, and honest about what it changes. It optimizes,
edits, merges, splits and protects PDFs, and is growing toward full PdfSharp parity under MIT.
It is created and maintained by Jamal Abdelkhalek at JADEV.

## Compatibility

| Target | Runs on | Dependencies |
|---|---|---|
| `net8.0`, `net10.0` | .NET 8, 9, 10 and later | None |
| `netstandard2.0` | .NET Framework 4.6.2 to 4.8.1, Mono, Unity, Xamarin, .NET Core 2.0+ | `System.Memory` (Microsoft, MIT) |

CI runs the full test suite on .NET Framework 4.8, .NET 8 and .NET 10, on Windows, Linux and
macOS. The whole real world corpus also passes on .NET Framework 4.8. One difference: .NET
Standard's zlib has no level 9, so `CompressionEffort.Maximum` compresses a little less there.

## Why another PDF tool

Most PDF compression in the .NET world shells out to Ghostscript, depends on a copyleft or
commercial library, or re-distills the document and quietly loses forms, tags or metadata.
PdfNetStack takes a narrower job and does it with proof:

- **Lossless by construction.** Text, vectors, fonts, images, annotations, forms, bookmarks,
  structure tags, metadata and attachments are carried over. Nothing is rasterized.
- **Verified, not assumed.** Every rewrite is re-parsed and compared to the source, with stream
  content compared after decoding. A mismatch means the original is returned.
- **Fail safe.** Encrypted, signed and damaged files are returned byte for byte. The output is
  always a valid PDF: the verified rewrite or your input.
- **No native code, no processes.** One managed assembly for .NET 8 and .NET 10, trim and AOT
  compatible, same behaviour on Windows, Linux and macOS.
- **MIT all the way down.** On .NET 8 and later the core package has no dependencies; on .NET
  Standard 2.0 it uses Microsoft's `System.Memory`. CI blocks any runtime package that is not on a
  reviewed license allowlist.

### Why C# and not a native library

Optimization is mostly parsing, hashing and zlib. Modern .NET does all three at close to native
speed (`Span<T>`, SIMD-backed hashing, zlib-ng under `System.IO.Compression` on .NET 9+), and the
work parallelizes across streams. A Rust or C++ core would mean shipping native binaries per
platform and a marshalling layer, for little measured gain. If profiling ever shows a hot spot
worth it, the pipeline stages are isolated enough to swap one out.

## Install

```bash
dotnet add package PdfNetStack            # library
dotnet tool install -g PdfNetStack.Tool   # command line: pdfnetstack
dotnet add package PdfNetStack.Azure      # optional Blob Storage pipeline
```

## Usage

### Library

```csharp
// In memory
(byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(File.ReadAllBytes("in.pdf"));

// Streams
await using var input = File.OpenRead("in.pdf");
await using var output = File.Create("out.pdf");
PdfOptimizationResult r = await PdfOptimizer.OptimizeAsync(input, output);

// Options
var options = new PdfOptimizerOptions
{
    Compression = CompressionEffort.Balanced,   // faster, slightly larger
    AllowIncrementalUpdates = true,             // flatten edit history into one revision
    MaxDegreeOfParallelism = 2,                 // cap CPU and memory per document
    JpegQuality = 60,                           // opt-in, lossy: requantize JPEG images
};
```

`PdfOptimizationResult` tells you what was written and why: `Disposition`, sizes, SHA-256 of
input and output, page count, a human readable `Reason`, and per pass statistics and timings.

### Editing

```csharp
using var input = File.OpenRead("contract.pdf");
PdfDocument doc = PdfDocument.Open(input);

doc.Pages.RemoveAt(0);                         // drop the cover
doc.Pages.Move(2, 0);                          // reorder
doc.Pages[0].Rotate(90);                       // rotate clockwise
doc.Pages.Add(PdfRectangle.A4);                // blank page

PdfDocument annex = PdfDocument.Open("annex.pdf");
doc.Pages.Import(annex.Pages);                 // fonts and images shared between pages are copied once

doc.Info.Title = "Signed contract";
doc.Outlines.Add("Annex", doc.Pages[^1]);      // bookmarks, read and written

doc.Save("contract-final.pdf");                // optimized and verified by default

PdfDocument merged = PdfDocument.Merge([PdfDocument.Open("a.pdf"), PdfDocument.Open("b.pdf")]);
foreach (PdfDocument single in merged.Split()) { /* one document per page */ }
```

Inherited page attributes (resources, media box, rotation) are materialized on each page, so
pages survive reordering and moving between documents. Annotations travel with their page and
are copied fresh for every import; links to pages imported in the same call are remapped, links
to pages left behind become empty. Editing a signed document invalidates its signatures.

### Drawing

```csharp
using PdfNetStack.Drawing;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())          // origin top left, y down, points
{
    var header = new PdfLinearGradientBrush(new PdfPoint(0, 0), new PdfPoint(595, 0),
        PdfColor.FromHex("#1E3A8A"), PdfColor.FromHex("#2563EB"));
    g.DrawRectangle(null, header, 0, 0, 595, 140);
    g.DrawRoundedRectangle(new PdfPen(PdfColor.FromHex("#CBD5E1")), PdfColor.White, 40, 170, 515, 220, 16);
    g.DrawEllipse(null, PdfColor.FromHex("#14B8A6").WithAlpha(0.5), 60, 430, 180, 180);

    PdfFont bold = PdfStandardFont.HelveticaBold;                 // using PdfNetStack.Fonts
    g.DrawString("Invoice 2026-114", bold, 28, PdfColor.White, 40, 80);   // x, baseline y
    double width = bold.MeasureString("Total: €1,250.00", 14);
    g.DrawString("Total: €1,250.00", bold, 14, PdfColor.Black, 555 - width, 360);
}
doc.Save("drawing.pdf");
```

Paths with lines, Bezier curves, arcs, ellipses, polygons and rounded rectangles (elliptical
corners sized like CSS `border-radius`); pens with caps, joins and dashes; gray, RGB and CMYK
colors with opacity; linear and radial gradients; transforms, saved states and clipping.
`CreateGraphics(PdfGraphicsLayer.Background)` draws underneath a page's existing content, for
letterheads and watermarks. The API follows PdfSharp's `XGraphics` naming.

The 14 standard PDF fonts (Helvetica, Times, Courier with their bold and italic styles,
Symbol, ZapfDingbats) need no embedding, so a page of text weighs about 2 KB. They cover Western
and Central European languages (Windows-1252 plus Ł, ő, č and the like).

Any TrueType or OpenType font works too, embedded as a subset of the glyphs you draw:

```csharp
using PdfNetStack.Fonts;

PdfFont inter = PdfOpenTypeFont.Load("fonts/Inter-Regular.ttf");          // .ttf .otf .ttc .woff
PdfFont? title = PdfFontCollection.System.Find("Segoe UI", weight: 600);   // installed fonts

var brand = new PdfFontCollection();
brand.AddDirectory("fonts");
PdfFont heading = brand.Find("Inter", weight: 700) ?? PdfStandardFont.HelveticaBold;
```

Embedded text is kerned from the font's GPOS or kern table, stays searchable and copyable in
any script the font covers (a ToUnicode map is written), and the subset keeps only the outlines
used, so a page in Inter costs a few KB. `PdfFontCollection` matches family, weight and italic
the way CSS does. Text can be filled with a gradient, outlined with a pen, or both;
`MeasureString`, `Ascent`, `Descent` and `GetLineHeight` give the metrics for layout. Fonts are
parsed defensively: a damaged file raises `FontFormatException`, and a font whose license
forbids embedding (OS/2 fsType) is refused. Not supported yet: WOFF2, CFF2 variable fonts.

Paragraphs wrap, align and justify, with rich text and right to left scripts:

```csharp
var body = new PdfTextStyle(inter, 11) { Fallbacks = [notoArabic] };
var p = new PdfParagraph { Alignment = PdfTextAlignment.Justify, LineHeight = 1.5 }
    .Add("Payment is due within ", body)
    .Add("30 days", body with { Font = interBold })
    .Add(". المبلغ المستحق: 1,250.00 €", body);
PdfTextLayout layout = g.DrawText(p, 50, 120, 300);   // layout.Height is the room it took
```

Text goes through the Unicode line breaking (UAX #14) and bidirectional (UAX #9) algorithms,
both passing the full Unicode conformance suites, and an OpenType shaper that matches HarfBuzz
glyph for glyph: ligatures, kerning, Arabic joining and vowel marks. Lines break greedily at
break opportunities, overlong words break between characters, soft hyphens show a hyphen when
used, and line height follows CSS. `PdfTextLayout` measures before drawing and can draw a range
of lines, for text that continues on the next page.

Images and reusable drawings:

```csharp
PdfImage photo = PdfImage.Load("photo.jpg");      // JPEG copied as is, EXIF orientation honored
PdfImage logo = PdfImage.Load("logo.png");        // PNG with alpha becomes a soft mask
g.DrawImage(logo, 40, 30, 120, 40);

PdfForm page = PdfForm.FromPage(doc, other.Pages[0]);   // any page of any document
g.DrawForm(page, 0, 0, g.Width / 2, g.Height / 2);      // n-up, stamps, templates
```

PNGs of every color type and bit depth are supported, interlaced or not; simple ones keep their
compressed data, so embedding costs no recompression. ICC profiles and pixel density are kept.
Each image and font is written once per document however often it is drawn.

Annotations use the same top left coordinates and carry their own appearance, so every viewer
shows them alike:

```csharp
page.Annotations.AddLink(72, 700, 120, 14, new Uri("https://example.com"));
page.Annotations.AddLink(72, 720, 120, 14, doc.Pages[3], top: 200);
page.Annotations.AddTextMarkup(PdfTextMarkup.Highlight, 72, 100, 300, 16, comment: "Check");
page.Annotations.AddNote(520, 40, "Please review", author: "Finance");
g.AddLink(0, 0, 100, 20, new Uri("https://example.com"));   // follows the canvas transform
```

Fill and flatten forms:

```csharp
PdfDocument doc = PdfDocument.Open("application.pdf");
((PdfTextField)doc.Form["name"]!).Text = "Jamal Abdelkhalek";
((PdfCheckBoxField)doc.Form["subscribe"]!).Checked = true;
((PdfRadioButtonField)doc.Form["plan"]!).Selected = "pro";
((PdfChoiceField)doc.Form["country"]!).Select("Morocco");
doc.Form.Flatten();                                  // optional: values become page content
doc.Save("filled.pdf");
```

Every field kind is read (text, check box, radio group, combo and list box, push button,
signature) with fully qualified names, inherited attributes and widget pages. Setting a value
redraws the field's appearance from its border, background, font and color settings, with
multiline wrapping, comb cells, password masking and automatic font size, so filled forms look
the same in every viewer. A font the form embeds whole is reused; others map to the closest
standard font.

Document settings: `doc.Viewer` (layout, panels, opening page, right to left reading),
`doc.PageLabels` (i, ii, iii then 1, 2, 3), `doc.Attachments` (embedded files, as e-invoices
use) and `doc.Language`.

### Encryption

```csharp
PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Security.Encrypt(userPassword: "read", ownerPassword: "admin",
    permissions: PdfPermissions.Print | PdfPermissions.CopyContent);   // AES-256 by default
doc.Save("report-protected.pdf");

PdfDocument opened = PdfDocument.Open("report-protected.pdf", password: "admin");
opened.Pages.RemoveAt(0);
opened.Save("trimmed.pdf");          // keeps the original passwords and permissions
opened.Security.RemoveEncryption();  // or save it unprotected
```

Opening supports every standard security handler revision: RC4 40 to 128 bit, AES-128 and
AES-256 (revisions 2 to 6), user and owner passwords, crypt filters and unencrypted metadata.
New protection is written as AES-256 (revision 6) or AES-128 (revision 4). A document opened with
only the user password can be read but not saved, and pages can be copied out of it only when
its permissions allow copying.

### Command line

```bash
pdfnetstack optimize in.pdf out.pdf
pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./corpus                  # measure only, writes nothing
pdfnetstack optimize in.pdf out.pdf --jpeg-quality 60   # lossy, JPEG images only
pdfnetstack inspect in.pdf                  # where the bytes go
pdfnetstack verify original.pdf other.pdf   # check any tool's output for equivalence

pdfnetstack merge out.pdf a.pdf b.pdf c.pdf
pdfnetstack split in.pdf ./pages
pdfnetstack extract in.pdf out.pdf 1-3,7,9-  # page ranges, any order, repeats allowed
pdfnetstack rotate in.pdf out.pdf 90 2-4
pdfnetstack meta in.pdf out.pdf --title "Annual report" --author "Finance"

pdfnetstack encrypt in.pdf out.pdf --user-password u --owner-password o --allow print,copy
pdfnetstack decrypt in.pdf out.pdf --password o
```

### Azure Blob Storage

```csharp
var pipeline = new BlobPdfOptimizer(new Uri("https://account.blob.core.windows.net"));
BlobOptimizationOutcome outcome = await pipeline.OptimizeAsync("incoming", "docs/manual.pdf", "optimized");
```

Uses managed identity through `DefaultAzureCredential`, never account keys. The source blob is
read at a pinned ETag and never modified. Output goes to a different container under
`pdf/<sha256 prefix>/<sha256>.pdf` with a conditional write, so retries and concurrent workers
are idempotent. A JSON sidecar records source URI, ETag, both hashes, sizes, disposition, reason,
engine version and timings.

## What it does

| Pass | Effect | Lossless |
|---|---|---|
| Garbage collection | Drops objects unreachable from the catalog, including old revisions | Yes |
| Object streams | Packs non-stream objects into Flate compressed object streams (PDF 1.5) | Yes |
| Xref stream | Replaces the text xref table with a predictor encoded, compressed stream | Yes |
| Stream recompression | Removes ASCIIHex/ASCII85 layers, turns LZW and RunLength into Flate, recompresses Flate at zlib level 9, keeps a result only when smaller | Yes |
| Image prediction | Tries PNG predictors on raw images of 8 bits or less per component | Yes |
| JPEG re-encoding | Rebuilds each JPEG's Huffman tables for that image, writes progressive and restart-marked JPEGs as plain baseline, drops segments PDF readers ignore (Exif, XMP, embedded ICC, comments). Works on the DCT coefficients, which stay identical | Yes |
| Deduplication | Merges byte identical fonts, images, ICC profiles, resource dictionaries and arrays, iterating until merges stop | Yes |
| Compact syntax | Minimal separators, canonical numbers, null entries dropped | Yes |
| Thumbnails, PieceInfo | Optional removal of page thumbnails and application private data | No, opt-in |
| JPEG requantization | `JpegQuality`: coarser quantization at a libjpeg quality, never finer than the image's own; soft masks stay lossless | No, opt-in |

JPEG data is decoded to DCT coefficients and never to pixels, so components, chroma sampling and
color model pass through untouched: a CMYK image stays CMYK. JPEG 2000, JBIG2 and CCITT data is
never decoded or touched.

## Safety model

The optimizer refuses to rewrite, and returns the input unchanged, when the file is:

- encrypted,
- digitally signed or has usage rights (any `/ByteRange`, signature dictionary, `/Perms` or `/DSS`),
- incrementally updated, unless `AllowIncrementalUpdates` is set,
- damaged: xref reconstruction, stream length repair, unreadable objects, or references whose
  generation numbers disagree with the xref, unless `AllowRepairedFiles` is set,
- larger than `MaxInputBytes`.

PDF/A-1 files are detected from their XMP metadata and written with a classic xref table and
no object streams, because PDF/A-1 predates both.

Hostile input is bounded: nesting depth is capped, decoded stream size is capped
(`MaxDecodedStreamBytes`, default 256 MiB) as a zip bomb defence, `/Prev` loops terminate,
wrong stream lengths are recovered from `endstream`, and unreadable objects never crash the run.

The verifier walks the source and output graphs in lockstep from the trailer. Dictionaries
must have the same keys and values, arrays the same items, references must line up, and every
stream must decode to identical bytes with identical remaining filters. JPEG images are
compared by DCT coefficient, so a re-encoded image passes only if every coefficient is the
same. Encoding keys (`/Length`, `/Filter`, `/DecodeParms`) are the only other differences it
accepts. With `JpegQuality` set, a JPEG image may also differ by exactly the requantization the
optimizer applies, checked coefficient by coefficient, and by nothing else.
See [docs/FIDELITY.md](docs/FIDELITY.md) for the full method and its limits.

## Results

Measured on a mixed real world corpus of 1,296 files (569 MiB): invoices, reports, manuals,
scanned documents, presentations and a 631 page spare parts catalogue, on an 8 core Windows
machine with default options.

| | |
|---|---|
| Optimized | 1,003 files, total size 568.9 MiB to 469.0 MiB (17.6% saved) |
| Returned unchanged | 38 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs (empty or resource fork files) |
| Verification failures | 0 |
| Throughput | about 65 s for the whole corpus, 8 to 9 MB/s, lossless JPEG re-encoding included |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,118 files optimized, 20.1% saved |

Independent render check: every optimized file was rendered with PDFium at 150 DPI and
compared with the original. 1,118 documents, 2,161 pages, zero differing pixels, identical
extracted text. The script is in [tools/render_compare.py](tools/render_compare.py).

The 631 page catalogue, 72.6 MiB of CAD drawings already Flate compressed: 6,075 duplicate
objects merged, 3.1% saved, about 5 seconds with all cores, peak working set about 690 MB.
Image heavy files gain the most, often 20 to 40%, mostly from PNG prediction on raw images.

Editing path: every document in the corpus was rebuilt by importing all of its pages into a new
document and saving it. 1,152 documents, 2,233 pages, zero differing pixels against the
originals at 150 DPI.

Encryption: AES-256 and AES-128 files written by PdfNetStack open in PDFium with either password
and render identically to their sources; real AES-128 contracts from other producers decrypt to
pixel identical output; RC4 files written for the legacy read path render identically in PDFium.

Your mileage depends on what produced the PDF. Files from well tuned producers that already use
object streams have little left to gain losslessly.

## Options

| Option | Default | Notes |
|---|---|---|
| `MinSavingPercent` | 1.0 | Keep the original unless the rewrite saves at least this much |
| `Compression` | `Maximum` | `Balanced` cut corpus time by about a third for about 2% larger output |
| `UseObjectStreams` | true | Off writes a classic xref table |
| `ObjectsPerStream` | 200 | Larger compresses better, smaller loads single pages faster |
| `DeduplicateObjects` | true | |
| `RecompressStreams` | true | |
| `UseImagePredictors` | true | |
| `OptimizeJpegImages` | true | Lossless JPEG re-encoding |
| `JpegQuality` | null | 1 to 100, libjpeg scale. Lossy, JPEG images only |
| `Verify` | true | Disable only for inputs you already trust |
| `AllowIncrementalUpdates` | false | Flattens edit history into one revision |
| `AllowRepairedFiles` | false | Rewrites damaged files into clean ones |
| `RemovePageThumbnails` | false | Not lossless |
| `RemovePieceInfo` | false | Not lossless for the authoring app, invisible to viewers |
| `MaxInputBytes` | 1 GiB | |
| `MaxDecodedStreamBytes` | 256 MiB | Per stream decode ceiling |
| `MaxDegreeOfParallelism` | CPU count | Lower it to cap memory on image heavy files |

## Toward PdfSharp feature parity, then HTML to PDF

PdfNetStack is growing into a full MIT alternative to PdfSharp, then an HTML and CSS to PDF
engine in pure C#, each step with the same test and corpus discipline as the optimizer. Details
in the [roadmap](docs/ROADMAP.md).

| Step | Scope | Status |
|---|---|---|
| Editing | Open, create, merge, split, import, reorder, rotate and remove pages; document info; bookmarks | Done (1.0) |
| Encryption | Open RC4, AES-128 and AES-256 files; write AES-128/256 with permissions | Done (1.0) |
| Object model | Public `PdfDictionary`, `PdfArray`, `PdfStream` and friends for anything the API lacks | Done (next release) |
| Drawing | Canvas on new and existing pages: paths, colors, transforms, clipping, gradients | Done (next release) |
| Fonts | Standard 14 fonts, TrueType/OpenType/WOFF embedding with subsetting, kerning, font collections | Done (next release) |
| Text layout | Line breaking, alignment, justification, rich text, Arabic and RTL | Done (next release) |
| Images and overlays | JPEG and PNG placement, pages of other PDFs as watermarks, letterheads, n-up | Done (next release) |
| Annotations | Web and page links, notes, highlight, underline, strikeout, free text, blend modes | Done (next release) |
| Forms (AcroForm) | Reading, filling with generated appearances, flattening | Done (next release) |
| Signatures and PDF/A | PAdES signing, PDF/A-2b and 3b output | Planned |
| HTML and CSS | WeasyPrint-style layout engine: CSS cascade, flexbox, tables, paged media, web fonts | Planned |

### Low level access

```csharp
using PdfNetStack.Objects;

PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Catalog.Set("PageMode", new PdfName("UseOutlines"));        // open with bookmarks shown
var content = PdfStream.FromData("0 0 1 rg 72 72 144 144 re f"u8);
doc.Pages[0].Dictionary.Set("Contents", doc.AddObject(content));  // replace page content
```

## Limits

- Lossy compression covers JPEG images only and works on their coefficients: it does not
  downsample, does not convert raw images to JPEG, and has no perceptual quality gate yet. See the
  [roadmap](docs/ROADMAP.md).
- Arithmetic coded, 12 bit, lossless and hierarchical JPEGs, JPEGs with more than 64 scans, and
  damaged JPEG data, are carried over untouched.
- Font subsetting, unused resource pruning and content stream rewriting are not done.
- Linearized (fast web view) output is not produced; linearization is dropped.
- The whole document is held in memory. Peak memory depends on decoded image sizes and on
  parallelism: plan for about 10 times the file size, more for image heavy files, or lower
  `MaxDegreeOfParallelism`.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Fidelity and verification](docs/FIDELITY.md)
- [Roadmap](docs/ROADMAP.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## License

MIT. See [LICENSE](LICENSE). Runtime dependency licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
