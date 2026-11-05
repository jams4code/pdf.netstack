# OCR add-on: design

Status: built. Phases 0, 1 and 2 are done, and of phase 3 the page orientation, the rasterizer
hook, the command line tool and the Tesseract engine (section 5). `PdfNetStack.Ocr`, `PdfNetStack.Ocr.Models` and
`PdfNetStack.Ocr.Tool` are ready for NuGet, not published yet (section 4 has the status and the
publish steps). `PdfNetStack.Ocr` is an optional package that makes scanned PDFs searchable
with an invisible text layer (render mode 3) and returns text with word boxes. The core stays pure
managed, MIT, dependency free; native code and models live only in the add-on. External facts
were checked on 2026-10-29 against the NuGet API, the GitHub API and the package contents.

## 1. Engines

| Option | Wrapper license | Engine and data license | Native binaries in the NuGet package | Maintenance | Data size |
|---|---|---|---|---|---|
| `Tesseract` 5.2.0 (charlesw) | Apache-2.0 | Tesseract Apache-2.0, Leptonica BSD-2 style, tessdata Apache-2.0 | `x64/` and `x86/` Windows DLLs only (tesseract50, leptonica 1.82) | Last release 2022-11-30, last commit 2025-04-29: stale | eng fast 4.1 MB, ara fast 1.4 MB, ara best 12.6 MB |
| `TesseractOCR` 5.5.2 (Sicos1977 fork) | Apache-2.0 | as above, engine 5.5 | `x64/` and `x86/` Windows DLLs only (tesseract55, leptonica 1.85); Linux through system packages (project wiki, Docker), no macOS, no arm64 | Release 2026-03-05, active | as above |
| PP-OCRv5 ONNX models on `Microsoft.ML.OnnxRuntime` 1.31.1 | MIT (runtime) | PaddleOCR models Apache-2.0 | win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64. No osx-x64, no x86 | Release 2026-10-28, very active | det 4.8 MB, latin rec 7.9 MB, arabic rec 8.0 MB, line orientation 1.0 MB |
| `RapidOcrNet` 4.2.0 (PP-OCR on ONNX Runtime) | Apache-2.0 | as above | Through ORT and SkiaSharp natives; also pulls Clipper2 (BSL-1.0). net8.0 and net10.0 only | Release 2026-09-09, active | latin set bundled (13.9 MB package) |
| `Sdcb.PaddleOCR` 3.3.1 | Apache-2.0 | Paddle Inference Apache-2.0, OpenCvSharp4 Apache-2.0 | Paddle Inference and OpenCV per-RID packages, large | Release 2026-05-26, active | same models, Paddle format |
| `PaddleOCRSharp` 6.2.0 | Apache-2.0 | as above | `Paddle.Runtime.win_x64` only | Release 2026-06-12 | 30+ MB bundled |

Sources: nuget.org package pages and nuspecs (licenses, dates, file listings); GitHub
tesseract-ocr/tessdata_fast and tessdata_best (sizes); RapidOCR `python/rapidocr/default_models.yaml`
(ONNX URLs and SHA-256 pins on modelscope.cn). Tesseract released 5.5.4 on 2026-10-28; no .NET
wrapper ships it yet.

Accuracy:
- Latin: both engines do well on clean 300 DPI print. PaddleOCR reports 84.7% line accuracy for
  `latin_PP-OCRv5_mobile_rec` on its own 3,111 image set (paddleocr.ai, PP-OCRv5 multilingual).
  No Tesseract number exists on the same data.
- Arabic: PaddleOCR reports 81.27% for `arabic_PP-OCRv5_mobile_rec` (2,676 images; also covers
  Persian, Urdu, Pashto, Kurdish, Sindhi, Uyghur, Balochi). For Tesseract `ara` the only number
  found is CER 0.436 on one hard set (QARI-OCR, arXiv 2506.02295), plus forum reports of about 90%
  with `tessdata_best`. Not comparable; we measure on our own fixtures (section 4). PaddleOCR
  emits Arabic in visual order and reverses it in `ppocr/postprocess/rec_postprocess.py` (lines
  44-45 and 174-175 on main); our decoder must too. Not verified whether RapidOcrNet does.
- PP-OCRv6 is multilingual but RapidOCR's table lists no Arabic for it, so v5 stays.

Excluded: Ghostscript, MuPDF/PyMuPDF (AGPL) and Calamari (GPL-3.0) are copyleft. OCRmyPDF is
MPL-2.0 Python and drives Ghostscript. Surya's code is Apache-2.0 but its weights use a modified
AI Pubs Open Rail-M license with a revenue cap. Windows.Media.Ocr is Windows only; cloud OCR is a
service, not a library; IronOCR and Aspose.OCR are commercial. docTR/OnnxTR (Apache-2.0) have no
maintained .NET port.

**Recommendation: ONNX Runtime with PP-OCRv5 mobile models, behind our own small managed
pipeline.** It is the only option shipping natives for Windows, Linux and macOS on x64 and arm64
(macOS x64 excepted) from one MIT package, with a maintained Arabic model and Apache-2.0 model
files. We write pre and post processing ourselves (resize, DB boxes, CTC decode, word split,
Arabic reversal: about 800 lines) rather than take RapidOcrNet, which adds SkiaSharp natives and
Clipper2 and needs net8.0+; it is the reference to read. Tesseract can come later as a second
`IOcrEngine` (section 5 has where that went). Safety bonus: ORT only sees float tensors built by managed code, so PDF bytes
never reach native code.

## 2. What the core offers today

**Finding images and their placement: missing.** (Paths are under `src/PdfNetStack/`.) The core rewrites objects and knows "nothing
about rendering" (`docs/ARCHITECTURE.md:4`). `PdfParser.NextToken` (`Parsing/PdfParser.cs:83-149`)
tokenizes content streams, but nothing tracks `q`, `Q`, `cm` or `Do`. `cm` and `Do` appear only on
the write side (`Drawing/PdfGraphics.cs:118,238,514`). Inline images (`BI ID EI`) are not parsed
anywhere. Needed: a small content walker over `q Q cm Do BI/ID/EI BT/ET`, recursing into form
XObjects with their `/Matrix` and `/Resources`, depth and operator capped. About 300 lines.

**Stream filters.** `StreamCodec.IsDecodable` (`Filters/StreamCodec.cs:95-96`) covers Flate, LZW,
ASCIIHex, ASCII85 and RunLength, plus PNG and TIFF predictors. `PdfDocument.GetStreamData` leaves
DCT, JPX, JBIG2 and CCITT encoded (`Documents/PdfDocument.cs:410-429`).

| Gap | Today | Managed effort |
|---|---|---|
| DCTDecode to pixels | `JpegReader` decodes Huffman data to quantized coefficients, "without an inverse DCT" (`Imaging/JpegReader.cs:5-10`); baseline, extended, progressive, 8 bit | Medium, 4 to 6 days: dequantize, integer IDCT, chroma upsampling, YCbCr, CMYK and YCCK with the Adobe inversion. Most of the hard part exists |
| CCITTFaxDecode (G3 1D, G3 2D, G4) | none | Medium, 5 to 7 days, about 700 lines. T.4/T.6 are small, fully specified; parameters K, Columns, Rows, BlackIs1, EncodedByteAlign, EndOfBlock |
| JBIG2Decode with JBIG2Globals | none | Hard, 10 to 15 days, 3,000+ lines: MQ arithmetic decoder, generic regions (MMR and arithmetic), symbol dictionaries, text regions, refinement, halftone. jbig2enc, a common encoder, writes generic and symbol/text regions |
| JPXDecode | done | `Jpeg2000Decoder` in the core: managed, no dependency |
| Raw samples to gray | none on the read side. Color spaces are only counted (`ColorComponents`, `Optimization/StreamRecompressor.cs:248`) and written for PNG embedding (`Drawing/PdfImage.cs:294-310`) | Small, 3 to 4 days: unpack 1/2/4/8/16 bpc, apply `/Decode`, `/ImageMask`, Indexed lookup, DeviceGray/RGB/CMYK, Cal*, ICCBased by `/N` (profile ignored), Separation and DeviceN as inverted tint, Lab approximated |

OCR needs luminance only, so every path ends in 8 bit gray; soft masks are ignored. Arithmetic,
12 bit and lossless JPEGs stay unsupported (`README.md:475`) and such pages are skipped. Core
decoders are written from the specifications. PdfPig (github.com/UglyToad/PdfPig, Apache-2.0) has
C# CCITT and JBIG2 filters; a port keeps Apache-2.0 headers and a NOTICE, so it would go in the
add-on, never the core.

**Invisible text: close.** `PdfGraphics.WriteText` takes a render mode and writes `Tr`
(`Drawing/PdfGraphics.cs:622-636`), but callers pass only 0, 1, 2 (`:454-459`) and 7 (`:444`).
The text matrix is fixed at `1 0 0 -1 x y Tm` (`:638`) and `Tz` is never written, so no rotation
or horizontal scaling. `DrawGlyphs` is the internal entry the HTML engine uses (`:420`); RTL runs
get `/ActualText` (`:431-434`, `:480-494`). Fonts cannot be subclassed outside the core
(`private protected PdfFont()`, `Fonts/PdfFont.cs:16`), standard fonts are WinAnsi only, and
`ShapeRun` throws on a missing glyph (`PdfFont.cs:129`). The change, 2 to 3 days, all internal:

1. `GlyphlessFont : PdfFont`: Type0, Identity-H, CID = UTF-16 code unit, `/DW 500`, every CID
   mapped to one empty glyph of a generated TrueType program of a few hundred bytes, identity
   ToUnicode. No shaping, any script. Tesseract's `src/api/pdfrenderer.cpp` uses the same scheme.
2. `PdfGraphics.DrawInvisibleText(string text, PdfMatrix textMatrix, double size, double
   horizontalScale, bool actualText)`: `BT 3 Tr /F size Tf a b c d e f Tm hs Tz <codes> Tj ET`,
   optionally inside `/Span <</ActualText ...>> BDC`.
3. `<InternalsVisibleTo Include="PdfNetStack.Ocr" />` beside the others (`PdfNetStack.csproj:23-26`).

## 3. Design

### Public API (add-on)

```csharp
/// <summary>Recognizes text in images. Implementations must be safe to call from several threads.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Recognizes one upright 8 bit gray image; boxes are in its pixel coordinates.</summary>
    OcrImageResult Recognize(OcrBitmap image, OcrRequest request, CancellationToken cancellationToken);
}

/// <summary>The bundled engine: PP-OCRv5 models on ONNX Runtime.</summary>
public sealed class OnnxOcrEngine : IOcrEngine
{
    /// <summary>Loads models from a folder; each file is checked against its pinned SHA-256.</summary>
    public static OnnxOcrEngine Load(string modelDirectory, OnnxOcrEngineOptions? options = null);
}

/// <summary>Adds a text layer to scanned pages and reports what was found.</summary>
public sealed class PdfOcr
{
    /// <summary>Uses <paramref name="engine"/>; the caller keeps ownership and disposes it.</summary>
    public PdfOcr(IOcrEngine engine, PdfOcrOptions? options = null);
    /// <summary>Adds invisible text to every scanned page; returns per page results and skip reasons.</summary>
    public PdfOcrReport MakeSearchable(PdfDocument document, CancellationToken cancellationToken = default);
    /// <summary>Recognizes text without changing the document.</summary>
    public PdfOcrReport Recognize(PdfDocument document, CancellationToken cancellationToken = default);
}

public sealed class PdfOcrOptions
{
    public IReadOnlyList<string> Languages { get; init; } = ["latin"];   // "latin", "arabic"
    public bool SkipPagesWithText { get; init; } = true;
    public double MinimumConfidence { get; init; } = 0.5;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public long MaxImagePixels { get; init; } = 1L << 27;    // about 11.5k by 11.5k
    public double MaxOcrDpi { get; init; } = 400;             // larger scans are downsampled first
    public double MinimumCoverage { get; init; } = 0.5;       // share of the crop box a scan covers
    public bool DetectOrientation { get; init; } = true;       // pages scanned sideways, see below
    public IPdfPageRasterizer? Rasterizer { get; init; }      // optional fallback, see below
    public double RasterizerDpi { get; init; } = 300;
}

/// <summary>Renders a page as shown (crop box, turned by /Rotate) to 8 bit gray; supplied by the application.</summary>
public interface IPdfPageRasterizer
{
    OcrBitmap? Render(PdfPage page, int pageIndex, double dpi, CancellationToken cancellationToken);
}

/// <summary>One word: text in logical order, quad in page space (PDF user space, y up).</summary>
public sealed record OcrWord(string Text, PdfPoint[] Quad, double Confidence, bool RightToLeft);
// PdfOcrReport -> PdfOcrPage { Index, Status (Done, SkippedHasText, SkippedNoScan,
//                               SkippedUnsupported, Failed), Reason, IReadOnlyList<OcrLine> Lines,
//                               Orientation }; OcrLine holds OcrWords.
```

### Pipeline per page

1. Walk the content (section 2), collecting image draws with their full CTM through nested
   forms. If the page already shows text and `SkipPagesWithText` is set, stop.
2. Several axis aligned images that cover the crop box together (strips, tiles, MRC background
   plus JBIG2 text mask): composite into one gray canvas in page space, stencil masks painting
   black (next section). Otherwise the one image covering most of the crop box: decode it.
   Anything else (vector art, rotated pieces, encodings not decoded): use `Rasterizer` if set,
   else report `SkippedNoScan` or `SkippedUnsupported`. The core does not grow a renderer for
   this (see "Rasterizer hook").
3. Check `Width * Height` against `MaxImagePixels` before decoding to gray. Above `MaxOcrDpi`,
   box filter down and keep the scale.
4. Rotate pixels upright from `/Rotate` and the image matrix (multiples of 90 in practice), then
   run the engine. Its line classifier fixes 180 degree lines.
5. If most of the text read does not run left to right, the content itself is turned: find the
   orientation (see "Page orientation") and read the page again turned back.
6. Map boxes to page space and write the text layer.

### Page orientation

A page fed into a scanner sideways or upside down gives a scan whose content is turned, with no
`/Rotate` or image matrix saying so. The engine still finds lines on it: the detector returns
tall boxes for text running down or up the page, which it reads turned a quarter, and the line
orientation model turns lines it sees upside down. Each line's quad then runs down, left or up.
The lines come back in the wrong order, though, and a sideways line reads less reliably, so
`PdfOcr` looks at the lines it got:

- A line's direction is its quad's top edge, rounded to a quarter turn. Weighed by length times
  confidence, when the lines that do not run left to right outweigh those that do, the page is
  taken as turned. An upright page costs nothing more.
- A copy reduced to at most 1600 pixels on its long side (about 140 dpi for A4) is read in each
  of the four orientations, in the language that read the most text on the page. The score of an
  orientation is the length times confidence of the lines that run left to right in it, so lines
  the orientation model had to turn do not count. The highest score wins and a tie keeps the
  page as it was. Four reads of the reduced copy, then one of the page: the work is bounded.
- The page is read in the winning orientation; a word corner maps back through that turn, then
  through the scan's own way back to page space. `PdfOcrPage.Orientation` reports how far the
  content stood turned clockwise. `DetectOrientation = false` turns all of this off.

On the fixture page turned a quarter, a half and three quarters, the lines come back in reading
order with orientation on, and out of order with it off (`ReadsPagesScannedSidewaysWithTheModels`).

### Rasterizer hook

Pages with no scan to decode (text drawn as vector outlines, vector art with images too small to
be a scan, scans in an encoding the core does not decode, pieces at odd angles) can be read through
`PdfOcrOptions.Rasterizer`, an `IPdfPageRasterizer` the application supplies. PdfNetStack does not
render and takes no native dependency for it; an application can wrap a PDFium binding (PDFium is
BSD-3-Clause and Apache-2.0, and MIT licensed .NET bindings exist) or any other renderer.

- It is asked only for pages that would otherwise be `SkippedNoScan` or `SkippedUnsupported`,
  one page at a time on the calling thread, before that page's text layer is written, so a
  renderer may read the original file at the page index instead of the `PdfPage`.
- It renders the page as shown: the crop box turned by `/Rotate`, on white, in 8 bit gray. The
  resolution is `RasterizerDpi` (300), at most `MaxOcrDpi`, lowered before the call until the
  page fits `MaxImagePixels`.
- A null result leaves the page skipped as before. A bitmap over the pixel limit, or whose shape
  is not the page's as shown (within a pixel and 1%), is `SkippedUnsupported`; an exception from
  the renderer is `Failed` with its message, except cancellation, which ends the run.
- A pixel maps back through the `/Rotate` turn to the crop box. Orientation detection then works
  on rendered pages as on scans.

### Scans in several images

Some scanners and PDF writers store a page as horizontal strips of a few dozen to a few hundred
rows, as tiles, or as a background with a 1 bit text mask over it (mixed raster content). Such a
page is put back together before recognition (`Processing/Mosaic.cs`):

- Pieces are the page's images that overlap the crop box and land upright or turned by quarter
  turns (mirrored or not), so their bounds are exactly what they cover. When there are two or
  more and their union covers `MinimumCoverage` of the crop box (overlaps counted once, by a sweep
  over their edges), they are composed; otherwise the single image path above applies. If only
  pieces turned by other angles would cover enough, the page is `SkippedUnsupported`.
- The canvas covers the pieces' union bounds clipped to the crop box, in page space, at the finest
  piece resolution and no finer than `MaxOcrDpi`. Pieces are painted in drawing order: a canvas
  pixel whose center falls inside a piece takes the piece's pixel under it, so pieces that meet
  edge to edge leave no seam and a scan cut into strips comes back pixel for pixel. A piece finer
  than the canvas is box filtered down to it first. A stencil mask only darkens.
- The canvas is then turned by the page's `/Rotate`. A word corner maps back through that turn
  to canvas pixel (x, y), then to page point (left + x / sx, top - y / sy), sx and sy being the
  canvas pixels per point.
- Bounds, all checked before anything is decoded: at most 4,096 images per page; the pieces'
  pixels together and the canvas each at most `MaxImagePixels`; the pieces' footprints on the
  canvas together at most four canvases, so an image drawn over itself many times cannot multiply
  the work. One piece is decoded at a time and dropped once painted: peak memory is the canvas
  plus one piece and its gray copy. Cancellation is checked between pieces.

### Coordinate mapping

An engine corner is first mapped back through the inverse of the upright rotation R and divided
by the downsampling scale s, giving pixel (u, v) with v down. PDF image space puts that pixel at
unit square point (u / w, 1 - v / h) (ISO 32000-1, 8.9.5), and the page point is
`ctm.Transform(new PdfPoint(u / w, 1 - v / h))`, with `ctm` the CTM at the `Do` including every
enclosing form `/Matrix`. All four corners are kept, so skewed, rotated and mirrored placements
come out exactly. `PdfGraphics` draws in unrotated media box space, y down
(`PdfGraphics.cs:76-77,116-118`), so the writer applies its flip once and leaves `/Rotate` alone.

### Text layer

- Font: `GlyphlessFont` for every script. A real font adds shaping, coverage gaps and size for
  nothing. ToUnicode returns the recognized characters; non-BMP ones go through `/ActualText`.
- One `BT ... ET` per word. Baseline direction (ux, uy) is the quad's bottom edge, unit length;
  the text matrix is `[ux, uy, -uy, ux, x0, y0]` and the font size `sz` is the word height,
  set with `Tf`. Horizontal scale `Tz = 100 * wordLength / (sz * 0.5 * n)`, n being the UTF-16
  length, so selection highlights match the image word.
- Right to left words: text in logical order, origin at the word's right end, text matrix
  mirrored on x (`a` and `b` negated), as `pdfrenderer.cpp` `AffineMatrix` does. Each Arabic
  word also gets `/ActualText`, which the core already emits for RTL runs.
- Non-final words carry a trailing space inside the gap, so extractors see word breaks.
- Words below `MinimumConfidence` are dropped from the layer but kept in the report.
- The layer goes in as a foreground content stream; rendering must be pixel identical before and
  after, which `tools/render_compare.py` can prove.

### Threading and memory

Pages run in parallel up to `MaxDegreeOfParallelism`; walking and decoding only read the
document. Text layers are written on one thread afterwards, since `PdfGraphics` is not thread
safe (`PdfGraphics.cs:72`). The engine pools ORT sessions, one per worker, intra-op threads 1,
so we do not rely on concurrent `Run` on one session (not confirmed in the official docs). Peak
memory per worker is decoded samples, one gray copy and the engine tensors. A4 at 300 DPI
is 8.7 Mpx: 8.7 MB gray, 26 MB RGB. Detection input is resized to a fixed long side, so tensor
memory does not grow with the scan. Buffers come from `ArrayPool`, as in the core.

### Hostile input

- Pixel cap and overflow checks on `Width`, `Height`, `BitsPerComponent` and components before
  any allocation. The per stream `DecodeLimit` (`Documents/PdfDocument.cs:84`, 256 MiB default)
  covers every compression layer; the PNG reader's 2^28 pixel cap (`Imaging/PngDecoder.cs:13`)
  is the precedent.
- CCITT and JBIG2 output is bounded by the image dictionary's size, never the stream's claims;
  JBIG2 striped pages (height 0xFFFFFFFF) end at `/Height`. Symbol counts, region sizes and
  refinement depth are capped; decoders check cancellation per row.
- Form recursion and operator counts are capped. Model files are SHA-256 checked before ORT
  loads them; the library never downloads anything. A page failure is reported as `Failed`.
- Scans in several images: image count, combined pixels, canvas size and painting work are capped
  before decoding (previous section).

### Packaging

Three packages, so users can pin or replace the models, and the core packages stay free of ONNX
(a fourth, `PdfNetStack.Ocr.Tesseract`, is in section 5):

- `PdfNetStack.Ocr` targets netstandard2.0, net8.0 and net10.0 and references
  `Microsoft.ML.OnnxRuntime` (MIT; its native binaries carry `ThirdPartyNotices.txt`, all
  permissive). It depends on exactly the core version it was built with, since it uses core
  internals. About 145 KB.
- `PdfNetStack.Ocr.Models` (`src/PdfNetStack.Ocr.Models`) holds no code: the four model files
  under `models/`, `LICENSE.txt` (Apache-2.0) and `NOTICE.txt` (PaddleOCR and RapidOCR), and
  `build/PdfNetStack.Ocr.Models.targets` (also under `buildTransitive/`), which copies the models
  into a `pdfnetstack-ocr-models` folder of the build and publish output, outside a single file
  bundle. `OnnxOcrEngine.Load()` reads them from there; `Load(folder)` still takes any folder.
  License expression `Apache-2.0 AND MIT` (the build file is MIT). 21.8 MB of models, a 20.1 MB
  package. It is versioned on its own (`PackageVersion` in the project, 1.0.0) and changes only
  with the model files, so a library release does not push the same models again.
- The models are never committed (`*.onnx` is ignored). CI and the release workflow run
  `python3 tools/fetch_ocr_models.py src/PdfNetStack.Ocr.Models/models` before packing, and the
  project's `VerifyOcrModels` target fails the pack when a file is missing or its SHA-256 differs
  from the pin. The pins live in three places (`OnnxOcrEngine`, the models project, the fetch
  script); `OcrTests.PinsTheSameModelsEverywhere` keeps them equal.
- `PdfNetStack.Ocr.Tool` (`src/PdfNetStack.Ocr.Tool`) is the command line tool,
  `pdfnetstack-ocr <in.pdf> <out.pdf> [--languages latin,arabic] [--models folder]`, with
  `--text`, `--json`, `--no-orientation`, `--all-pages`, `--min-confidence`, `--threads`,
  `--password` and `--no-optimize`. It loads the models from the `pdfnetstack-ocr-models` folder
  next to it by default. See "Command line" below for why it is a package of its own.
- `eng/check-licenses.ps1` checks every project; `eng/license-allowlist.json` lists ONNX Runtime
  under `onlyIn` (it may ship with `PdfNetStack.Ocr` and `PdfNetStack.Ocr.Tool` and nothing
  else) and the four model files under `bundledFiles` with their license.

### Command line

The `pdfnetstack` tool (`PdfNetStack.Tool`) is a few megabytes of managed code, also published as
native executables compiled ahead of time. Putting OCR in it would add ONNX Runtime's native
libraries (17 to 45 MB per platform, 130 MB for the five desktop platforms) and 21 MB of models
for every user, most of whom never run OCR, and the native executables would need those files
shipped beside them. One tool package cannot hold ONNX Runtime only for the users who want it: a
.NET tool is installed whole, and loading the add-on from elsewhere at run time would need a
plugin loader and a second install step anyway.

So OCR is a second tool package, `PdfNetStack.Ocr.Tool`, command `pdfnetstack-ocr`:

- It references `PdfNetStack.Ocr` and carries the models (imported through the models package's
  build file, so they land in `pdfnetstack-ocr-models` beside the tool) and ONNX Runtime's native
  libraries for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64. The Android and iOS
  builds and Windows import libraries in the ONNX Runtime package are left out of the publish.
- One target framework, net8.0 with `RollForward` set to `Major`, so it runs on .NET 8 and later
  and carries the native files once: a 70 MB package. License expression `MIT AND Apache-2.0`,
  with ONNX Runtime's `LICENSE` and `ThirdPartyNotices.txt` under `onnxruntime/` and the models'
  `LICENSE.txt` and `NOTICE.txt` under `models/`.
- Packing fails when the models were not fetched, so a tool without them is never published.
- `pdfnetstack ocr ...` in the main tool runs `pdfnetstack-ocr` with the same arguments, found
  next to the `pdfnetstack` executable or on the `PATH`, and says how to install it when it is
  missing. The main tool keeps no ONNX dependency and stays compatible with native AOT.

No cloud resources, so no cost.

## 4. Plan

Status: phase 0 is complete: the content walker, image placement (public as
`PdfPage.GetImages()`), raw samples with color spaces and Decode arrays, JPEG pixel decoding,
inline images, the CCITT Group 3 and 4 decoder, the glyphless font and invisible text (public as
`PdfGraphics.DrawInvisibleText`). Phase 1 is done in `src/PdfNetStack.Ocr`, with the models
package in `src/PdfNetStack.Ocr.Models`. In phase 2, JBIG2 decoding is done in the core and
multi-image compositing in the add-on. In phase 3, page orientation, the rasterizer hook and the
command line tool are done. Integration tests run when `PDFNETSTACK_OCR_MODELS` names
the model folder, or when the models were fetched into the models project (the test project
imports its build file, as an application would).

Publishing (owner, later): the release workflow already packs the three packages on a version tag.
1. Make sure the nuget.org trusted publishing policy (or the `NUGET_API_KEY` secret) belongs to
   the account that will own the new IDs, `PdfNetStack.Ocr`, `PdfNetStack.Ocr.Models`,
   `PdfNetStack.Ocr.Tool` and `PdfNetStack.Ocr.Tesseract`.
2. Move the CHANGELOG `Unreleased` section under the new version heading.
3. Tag and push: `git tag v1.1.0 && git push origin v1.1.0`. The workflow fetches and checks the
   models, packs every package and pushes them; `PdfNetStack.Ocr.Models` 1.0.0 goes up once and
   later releases skip it until its `PackageVersion` changes.
4. To check locally first: `python tools/fetch_ocr_models.py src/PdfNetStack.Ocr.Models/models`,
   then `dotnet pack PdfNetStack.sln -c Release -o artifacts`.

| Phase | Work | Estimate |
|---|---|---|
| 0 Core prerequisites (MIT, no deps) | Content walker and an internal image placement API; raw sample to gray with color spaces and Decode arrays; JPEG pixel decode on top of `JpegReader`; CCITT G3/G4 decoder; `GlyphlessFont` and `DrawInvisibleText`; InternalsVisibleTo | 17 to 24 days |
| 1 Add-on MVP | `IOcrEngine`, `OnnxOcrEngine` (DB boxes, cls, CTC, word split, Arabic reversal), model package with hashes, `MakeSearchable` for single image pages, Latin and Arabic, report | 8 to 12 days |
| 2 Coverage | JBIG2 decoder (generic, symbol, text, refinement; halftone last), done; multi-image compositing, done; inline images | 13 to 20 days |
| 3 Extras | JPX (done in the core instead); page orientation, done; `IPdfPageRasterizer` hook, done; command line tool, done; Tesseract engine, done (section 5) | 7 to 10 days |

### Tests

All fixtures are synthetic, generated by scripts in `tools/` like `make_image_fixtures.py`.
No real customer PDFs.

- Pages: `tools/make_ocr_fixtures.py` renders known text with OFL fonts (Noto Sans, Noto Naskh
  Arabic) at 200, 300 and 600 DPI with noise, blur and up to 2 degrees of skew, encoded as 1 bit
  Flate, CCITT G4 (Pillow with libtiff), JPEG gray and RGB, and JBIG2 generic and symbol mode
  (jbig2enc, Apache-2.0, run offline). Ground truth text sits next to each file. Only rendered
  pixels are committed, never fonts. Built so far: one page, set by `PdfNetStack.Html` and
  rasterized at 200 DPI, plus the same page cut into strips and tiles, every strip edge and tile
  edge running through a line of text.
- Pieces: the strips and tiles put back together must give the engine the page pixel for pixel;
  word boxes from strips, tiles meeting inside a word, strips under a page rotation and a stencil
  mask over a background must map back to the same page points; the limits are each tested.
- Decoders: CCITT and JBIG2 bit exact against the generator's bitmap; JPEG within 1 per sample of
  libjpeg (Pillow) on `Fixtures/Jpeg`; hand built raw cases (Indexed, Decode `[1 0]`, 16 bpc,
  stencil masks).
- Mapping: a fake `IOcrEngine` returns fixed pixel boxes for images placed at 0, 90, 180 and
  270 degrees, skewed, mirrored and in nested forms; quads must match to 1e-6. No model needed.
- Orientation: the fake engine reports a line running down, left or up when its mark is in another
  corner; pages turned each way, alone and under a `/Rotate`, must be found, read once turned
  back, and map back to the same page points with the word running the way the text runs; the
  reduced copy and the number of reads are checked. With the models, the fixture page turned
  three ways reads in order and extracts.
- Rasterizer: a fake renderer marks the page as shown; vector pages, a page under `/Rotate` and
  a scan in an unknown encoding are rendered and map back, pages with text or a readable scan are
  not; declining, throwing, the wrong shape, too many pixels and cancellation are each tested.
- Command line: argument errors leave nothing written; with the models, an upside down scan
  becomes searchable and its text file reads in order; the forwarding command finds the tool.
- Text layer: assert `3 Tr`, `Tz`, mirrored `Tm` for RTL and the ToUnicode map; extract with
  PDFium (script beside `tools/render_compare.py`) in ground truth order for Latin and Arabic;
  renders before and after must be pixel identical.
- Accuracy: an opt-in CI category fetches the pinned models, records CER per script as a
  baseline, and fails on a regression of more than one point.
- Hostile input: extend `tests/PdfNetStack.Tests/HostileInputTests.cs` with size overflow, CCITT
  and JBIG2 bombs, self-drawing forms and truncated streams; each fails fast within the cap.

### Risks

- Arabic: 81% on Paddle's own set may not hold on poor scans; our baseline decides stable or
  preview. Word boxes come from CTC timing and are approximate, worse for joined Arabic letters.
- RTL extraction order differs between viewers: PDFium in tests, others by hand before release.
- ORT: no osx-x64 or x86 in 1.31.1, musl not verified, 17 to 45 MB of natives per runtime.
- JBIG2 is the largest job and common in scanner output; it is now decoded in the core.
- ORT floating point differs across CPUs, so OCR output is not byte reproducible, unlike the rest
  of PdfNetStack. Mixed vector and image pages need a user supplied rasterizer.
- Orientation is decided by the recognizer's own confidence. A page with very little text, or
  text the loaded languages do not cover, may show no clear winner and is then read as it is.

## 5. The Tesseract engine

The roadmap waited for a maintained .NET wrapper with builds for Linux and macOS. One now ships
them, so the question was looked at again on 2026-11-05, against the NuGet API, the zip directories
of the packages, the GitHub API and Tesseract's and Leptonica's source.

| Option | License | win-x64 | linux-x64 | linux-arm64 | osx-x64 | osx-arm64 | .NET targets | Last release | Maintenance |
|---|---|---|---|---|---|---|---|---|---|
| `Tesseract` 5.2.0 (charlesw) | Apache-2.0 | yes | no | no | no | no | netstandard2.0, net47, net48 | 2022-11-30 | Last commit 2025-04-29. Open: #503, #529, #687 (Linux), #596 (arm64) |
| `TesseractOCR` 5.5.2 (Sicos1977) | Apache-2.0 | yes | no | no | no | no | netstandard2.0, netstandard2.1 | 2026-03-05 | Last commit 2026-05-13. Linux by system packages or Docker; closed: #46, #47, #66 (no Native AOT) |
| `Tesseract.CrossPlatform` 5.5.2.200 (jbtule, on `Tesseract.Native`) | Apache-2.0 | yes | yes | yes | no | yes | net8.0, net9.0, net10.0 | 2026-09-10 | Created 2026-09-06, last commit 2026-09-14, one maintainer, 1 star, about 4,100 downloads |
| An installed `tesseract` executable | Apache-2.0, not redistributed | any install | any install | any install | any install | any install | any | Tesseract 5.5.4, 2026-10-28; Ubuntu 24.04 has 5.3.4, Homebrew 5.5.4 | Upstream project, very active |

`Tesseract.CrossPlatform` meets the letter of the roadmap's condition. It was not chosen:

- Two months old with one maintainer is too early to call maintained, and `PdfNetStack.Ocr`
  supports .NET Framework, which this wrapper dropped.
- Its native packages are 10 to 30 MB each, compressed, 57 to 73 MB unpacked on Linux and macOS.
  They carry libcurl, OpenSSL, libarchive and libxml2 for Tesseract's optional features, which an
  OCR add-on would then load into the application and depend on one maintainer to patch.
- Native code would run in the application's process, on pixels from hostile documents. A crash
  there cannot be caught.
- Its own documentation says a build without a runtime identifier finds no native library, so
  applications would have to publish for a specific platform.

Driving the executable costs a process start and a load of the language data per image, and the
user has to install Tesseract, which the package cannot do for them. In return it adds no
dependency and no native code to the application, runs wherever Tesseract can be installed
(Alpine packages it, 5.5.3 on edge, so musl works; Homebrew has no prebuilt bottle for Intel Macs),
on .NET Framework too, and a crash, a hang or a memory blowup costs one image. `OnnxOcrEngine`
remains the default: it needs no install and detects sideways pages.

### How it runs

`tesseract stdin stdout [--tessdata-dir D] -l L --psm P [--dpi N] -c tessedit_create_tsv=1`, from
managed code with no shell, in the executable's own folder: a build with no compiled-in data path
looks relative to where it runs, which must not be the caller's folder or a temporary folder anyone
can write to. The sources behind each choice, read at 5.3.4 and on main:

- Input: an 8 bit BMP with a gray palette on standard input, written row by row from the bitmap.
  `TessBaseAPI::ProcessPagesInternal` reads standard input into memory and `pixReadMem` decodes
  it. Leptonica's BMP reader works from memory on every platform and is always built; its PNM
  reader goes through `fopenReadFromMemory`, a temporary file on Windows, and would leave the
  scan on disk. `ImageThresholder::SetImage` removes the palette.
- Output: TSV rather than hOCR, as it is one row per word with box and confidence. It is requested
  by variable: the `tsv` config file is not in every data folder, and `read_config_file` then
  tries the name relative to the working directory. `GetTSVText` writes each word's symbols with
  no escaping, so the parser takes the text as the rest of the row and trusts nothing: a row that
  does not parse is dropped, boxes are clamped to the image, control characters are removed, and a
  result that does not start with the header is an error. `ResultIterator` gives right to left
  text in logical order. `LC_ALL=C` keeps a decimal point in the confidence.
- Arguments: an argument list on .NET, and on .NET Standard a command line quoted by the rules C
  programs parse, tested through a real process on each framework. Language names can come from
  a request, so they are checked against a strict form: nothing that could read as an option or
  climb out of the data folder gets through, then each must be one Tesseract listed at load. Batch
  files are refused, as Windows runs them through a shell.
- Finding the executable: only absolute folders of the `PATH`, as Windows otherwise searches the
  current folder first and an empty entry means it on Unix.
- Threads: every stream is pumped on its own thread, so the process cannot block on a full pipe.
  `OMP_THREAD_LIMIT=1`, the setting Tesseract's FAQ gives, as `PdfOcr` already reads pages in
  parallel. `MaxConcurrentProcesses` bounds the rest. Each call is its own process, so calls
  share nothing.
- Bounds: output over `MaxOutputBytes`, `Timeout`, or cancellation kills the process. .NET 8 and later
  kill its tree; on .NET Framework only the process itself goes, so a wrapper script's child keeps
  running, which is why `ExecutablePath` should name the program itself. `Dispose` refuses new
  calls and ends those waiting, but lets a running process finish, so a call keeps its real result.
  Standard error keeps its last 4 KiB. A process that ends but leaves its output open, through
  something it started, stops being waited for after 5 seconds.

### Packaging and footprint

`PdfNetStack.Ocr.Tesseract` targets netstandard2.0, net8.0 and net10.0 and holds 36 KB of code. It
needs `IOcrEngine` and `PdfOcr` from `PdfNetStack.Ocr`, which depends on ONNX Runtime, so its
dependency excludes native and build assets. Measured with `dotnet publish` of a net10.0 console
app from packed packages: 3.0 MB with this package alone, with no native files, and 241 MB with
`PdfNetStack.Ocr` referenced as well, which brings ONNX Runtime's natives back on purpose. The
pipeline ran in both.

### Limits

- Sideways pages: boxes come back axis aligned, so `PdfOcr`'s check never fires and `Orientation`
  stays 0. Tesseract read the fixture page turned a quarter (`ReportsWhatHappensToPagesScannedSideways`),
  but the text layer then does not follow the text's direction. Upside down lines are not turned. Tesseract's own orientation detection (`--psm 1`)
  needs `osd.traineddata` and still reports axis aligned boxes, which would misplace the text layer.
- Versions: the engine refuses anything before 4 and has run against 5.3.4 only, in CI. Tesseract
  4.0.0, 4.1.0 and 4.1.1 write `--version` and `--list-langs` to standard output by their source; the
  engine also reads standard error when standard output has no answer, tested with the stand-in
  and not against a real 4.x.
- Several languages: one request such as `eng+ara`, since `PdfOcr` merges lines only when their
  boxes match to the pixel, and two Tesseract layouts need not do that.
- No speed or accuracy comparison is claimed here yet. The CI job `tesseract` runs the engine
  against the distribution's Tesseract and logs both engines' timings and error rates on the
  fixture page (`MeasuresAgainstTheBundledEngine`).

### Tests

`tests/PdfNetStack.FakeTesseract` stands in for the executable, so that process handling is tested
on every framework, .NET Framework included, with nothing installed: arguments as received, the
bitmap as sent (read back by the core's decoder), failing, hanging, flooding and not reading,
cancelling, the concurrency limit, and the whole pipeline. The parser takes hostile rows, random
bytes and mutated rows. `TesseractRealTests` runs against a real Tesseract, skipped when there is
none unless `PDFNETSTACK_REQUIRE_TESSERACT` is set, as it is in CI.
