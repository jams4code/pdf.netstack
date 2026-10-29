# OCR add-on: design

Status: proposal. `PdfNetStack.Ocr` is an optional package that makes scanned PDFs searchable with
an invisible text layer (render mode 3) and returns text with word boxes. The core stays pure
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
`IOcrEngine`, but its .NET wrappers ship Windows binaries only and need `System.Reflection.Emit`
(no Native AOT). Safety bonus: ORT only sees float tensors built by managed code, so PDF bytes
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
| JPXDecode | none | Very hard to write. Use `CoreJ2K` 2.4.1.108 (managed, BSD-3-Clause per nuspec, released 2026-10-09) in the add-on; its JJ2000 notices need review |
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
    public IPageRasterizer? Rasterizer { get; init; }         // optional fallback, see below
}

/// <summary>One word: text in logical order, quad in page space (PDF user space, y up).</summary>
public sealed record OcrWord(string Text, PdfPoint[] Quad, double Confidence, bool RightToLeft);
// PdfOcrReport -> PdfOcrPage { Index, Status (Done, SkippedHasText, SkippedUnsupported, Failed),
//                               Reason, IReadOnlyList<OcrLine> Lines }; OcrLine holds OcrWords.
```

### Pipeline per page

1. Walk the content (section 2), collecting image draws with their full CTM through nested
   forms. If the page already shows text and `SkipPagesWithText` is set, stop.
2. One image covering most of the crop box: decode it. Several axis aligned images at one scale
   (strips, MRC background plus JBIG2 text mask): composite into one gray canvas, stencil masks
   painting black. Anything else (vector art, rotated pieces): use `Rasterizer` if set, else
   report `SkippedUnsupported`. The core does not grow a renderer for this.
3. Check `Width * Height` against `MaxImagePixels` before decoding to gray. Above `MaxOcrDpi`,
   box filter down and keep the scale.
4. Rotate pixels upright from `/Rotate` and the image matrix (multiples of 90 in practice), then
   run the engine. Its line classifier fixes 180 degree lines; 90 degree page errors wait for
   phase 3 (try both, keep the higher confidence).
5. Map boxes to page space and write the text layer.

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

### Packaging

`PdfNetStack.Ocr` targets netstandard2.0, net8.0 and net10.0 and references
`Microsoft.ML.OnnxRuntime` (MIT; its `ThirdPartyNotices.txt` needs review). Models ship in
`PdfNetStack.Ocr.Models` (det, cls, latin and arabic rec: about 22 MB, Apache-2.0 with NOTICE) so
users can pin or replace them. Add both to `eng/license-allowlist.json` and the project to
`eng/check-licenses.ps1`. No cloud resources, so no cost.

## 4. Plan

| Phase | Work | Estimate |
|---|---|---|
| 0 Core prerequisites (MIT, no deps) | Content walker and an internal image placement API; raw sample to gray with color spaces and Decode arrays; JPEG pixel decode on top of `JpegReader`; CCITT G3/G4 decoder; `GlyphlessFont` and `DrawInvisibleText`; InternalsVisibleTo | 17 to 24 days |
| 1 Add-on MVP | `IOcrEngine`, `OnnxOcrEngine` (DB boxes, cls, CTC, word split, Arabic reversal), model package with hashes, `MakeSearchable` for single image pages, Latin and Arabic, report | 8 to 12 days |
| 2 Coverage | JBIG2 decoder (generic, symbol, text, refinement; halftone last); multi-image compositing; inline images | 13 to 20 days |
| 3 Extras | JPX through CoreJ2K; 90 degree page orientation; `IPageRasterizer` hook; CLI `ocr` command; optional Tesseract adapter | 7 to 10 days |

### Tests

All fixtures are synthetic, generated by scripts in `tools/` like `make_image_fixtures.py`.
No real customer PDFs.

- Pages: `tools/make_ocr_fixtures.py` renders known text with OFL fonts (Noto Sans, Noto Naskh
  Arabic) at 200, 300 and 600 DPI with noise, blur and up to 2 degrees of skew, encoded as 1 bit
  Flate, CCITT G4 (Pillow with libtiff), JPEG gray and RGB, and JBIG2 generic and symbol mode
  (jbig2enc, Apache-2.0, run offline). Ground truth text sits next to each file. Only rendered
  pixels are committed, never fonts.
- Decoders: CCITT and JBIG2 bit exact against the generator's bitmap; JPEG within 1 per sample of
  libjpeg (Pillow) on `Fixtures/Jpeg`; hand built raw cases (Indexed, Decode `[1 0]`, 16 bpc,
  stencil masks).
- Mapping: a fake `IOcrEngine` returns fixed pixel boxes for images placed at 0, 90, 180 and
  270 degrees, skewed, mirrored and in nested forms; quads must match to 1e-6. No model needed.
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
- JBIG2 is the largest job and common in scanner output; until phase 2 those scans are skipped.
- ORT floating point differs across CPUs, so OCR output is not byte reproducible, unlike the rest
  of PdfNetStack. Mixed vector and image pages need a user supplied rasterizer.
