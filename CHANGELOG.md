# Changelog

## Unreleased

- HTML: absurd sizes no longer exhaust memory. A font size such as 1e300px laid out one line
  across an unbounded number of pages. Computed lengths are now clamped to a million px either
  way (percentages to a million percent), font sizes to 10000px, unitless line heights to 1000
  and page sizes to 14400 points a side, and `HtmlConverterOptions.MaxPages` (default 10000)
  ends a conversion that needs more pages with the new `HtmlConversionException`.
- Variable fonts in font matching. `PdfFontCollection` counts a variable font for every weight,
  width and slant its axes cover, and registers its named instances as faces; faces are also
  found by full or PostScript name. `Find(family, PdfFontQuery)` matches width, then style, then
  weight with ranges as CSS Fonts 4 does (5.2), and returns the instance at the values found,
  with `OpticalSize` and `Variations` applied over them (7.2): ital for italic, slnt for oblique,
  never both. `PdfFontStyle` gives normal, italic or oblique at any angle. Static families match
  as before, now also by OS/2 width class.
- HTML: `@font-face` takes ranges in `font-weight`, `font-stretch` (and `font-width`) and
  `font-style` (`oblique 0deg 15deg`), and the `font-variation-settings` and
  `font-named-instance` descriptors. A missing descriptor is `auto`: the face matches as normal
  and the variation follows the style unclamped. `font-style: oblique <angle>`,
  `font-optical-sizing` (opsz set to the font size in CSS px) and `font-variation-settings` are
  supported; the `font` shorthand resets the last two. Rules with identical descriptors: the
  last one wins.
- Variable font instances are made once per location and the default location is the font
  itself, so a document embeds one program per location drawn, however many sizes use it.
  `@font-face` rules that share a file share its font. A damaged variation table makes matching
  fall back to the font's default instance.
- HTML bounds the variable font instances a document can ask for: axis values snap to a grid
  (whole units for wght, wdth and opsz, half degrees for slnt, tenths for ital, a hundredth of
  the range for other axes) and each font makes at most 32 instances per conversion, the nearest
  one standing in beyond that. Font values that are not finite are dropped and the rest clamped,
  and a style value that font matching still rejects falls back to the family's default instead
  of failing the conversion. `font-stretch` accepts only finite percentages.
- `PdfFontQuery` checks each value when it is set and copies `Variations`, so an invalid query
  throws where it is built.
- `tools/make_variable_font_fixtures.py` also builds Test-Axes.ttf, a font with wght, wdth,
  opsz, ital and slnt axes for the matching tests.
- PDF/A-1b output (`PdfConformance.PdfA1B`): PDF 1.4 with a classic cross-reference table and no
  object streams, also when the optimizer runs. Transparency (opacity, soft masks, alpha images,
  blend modes, transparency groups), JPEG 2000, optional content, embedded files, file attachment
  and other post PDF 1.4 annotations, OpenType font programs and ICC profiles newer than version 2
  are refused with the rest of the problems; subset CID fonts get a CIDSet stream read from the
  embedded program.
- PDF/A-4, 4e and 4f output (`PdfA4`, `PdfA4E`, `PdfA4F`): PDF 2.0 with `pdfaid:rev` 2020. The
  information dictionary written holds only the modification date; the document keeps its values
  and the XMP carries them. Every embedded file is an associated file with its relationship; 4f
  needs at least one and takes any type, plain 4 and 4e take PDF/A-1, 2 and 4 files only, and 4e
  allows 3D and RichMedia annotations with their GoTo3DView and RichMediaExecute actions.
- PDF/A checks for every part: actions are walked (open action, links, widgets, bookmarks and
  `Next` chains) and Launch, JavaScript, ResetForm, ImportData, Hide, media and other forbidden
  types, and named actions other than page navigation, are refused, as are additional actions on
  pages, widgets and fields, XFA, transfer functions, external stream data, PostScript and
  reference XObjects and OPI. LZW streams are re-encoded with Flate, image interpolation is
  switched off and NeedAppearances is removed. Saving an invoice as anything but PDF/A-3 is
  refused.
- Signature validation, the X.509 checks of ETSI EN 319 102-1: certificate policy processing as in
  RFC 5280, 6.1 with policy mappings, policy constraints and inhibit anyPolicy, on the policy graph
  of RFC 9618 (`PdfValidationOptions.AcceptablePolicies`, `RequireExplicitPolicy`,
  `InhibitPolicyMapping`, `InhibitAnyPolicy`; `PdfCertificateValidation.Policy` and
  `ValidPolicies`); name constraints on DNS names, email addresses, URI hosts, directory names
  and IP addresses, the trust anchor's included (`PdfCertificateCheck.NameConstraints`), with
  hosts compared in one canonical form (one trailing dot dropped, IDN as ASCII, lower case, URIs
  read by `System.Uri`), directory strings decoded in every string type and prepared as RFC 4518
  says, and anything that cannot be read or judged failing closed, including constraints that
  cannot be read, which fail every certificate below them; and the
  QCStatements of ETSI EN 319 412-5 (`PdfCertificateCheck.QcStatements`, `PdfQcStatements`),
  with the qualification they claim (`PdfCertificateValidation.Qualification`: QESig, QESeal,
  AdESig-QC and the rest). No trusted list is downloaded. Critical policy, name constraint and
  QC statement extensions no longer fail a certificate as unknown.
- Text extraction is about 3.8 times faster with identical output: `ExtractText` on a 60 page,
  27,000 word report in an embedded TrueType font went from 255 ms to 67 ms and from 300 MB to
  69 MB allocated. Reading order sorts runs of touching letters instead of single letters and
  decides each line once per size and baseline; the content interpreter no longer copies the text
  state for every glyph, shares operator names and reads CID widths without a copy per glyph.
- HTML conversion does less repeated work per line of text: a tab width is measured only for runs
  that contain a tab, the paragraph text is copied once for all its runs, and a font's OpenType
  features are read once per script. Output is unchanged.
- The command line tool is published as native executables, compiled ahead of time, for
  linux-x64, linux-arm64, win-x64, osx-x64 and osx-arm64, attached to each GitHub release with a
  `SHA256SUMS.txt`. No .NET install is needed to run them.
- `tools/PdfNetStack.Benchmarks` measures text extraction and HTML conversion with
  BenchmarkDotNet; it is not part of the solution or the packages.
- Timing tests take the fastest of several runs, so a busy machine cannot fail them.
- Shaping for more scripts, matching HarfBuzz glyph for glyph on the fonts we compare:
  - The Universal Shaping Engine, with its categories generated from the Unicode Character
    Database and Microsoft's overrides by `tools/make_shaping_data.py`: Balinese, Javanese,
    Sinhala, Tibetan, Sundanese, Batak, Buginese, Cham, Tai Tham, Lepcha, Meetei Mayek, Chakma,
    Grantha, Newa, Sharada, Tirhuta and the other Brahmic scripts HarfBuzz gives it, Indic fonts
    made for it (dev3 and the other version 3 tags), and Mongolian, N'Ko, Adlam and the other
    scripts that join like Arabic (a Mongolian free variation selector takes its letter's form),
    and Egyptian hieroglyph clusters.
  - Thai and Lao: sara am splits into nikhahit and sara aa, and fonts without Thai substitutions
    get their marks placed through the Windows or Macintosh private use glyphs.
  - Khmer (coeng ro and pre-base vowels reordered, split vowels decomposed, cfar), Myanmar
    (kinzi, medials and pre-base vowels reordered) and Hangul (syllables composed or decomposed
    for the font, ljmo, vjmo and tjmo, tone marks).
  - All shapers: invalid vowel sequences of the Indic and USE scripts get a dotted circle, cmap
    format 14 variation sequences pick their glyphs, a combining grapheme joiner that blocks no
    reordering is skipped by lookups, a ZWJ keeps a mark from attaching to the base before it
    (mark and mkmk see joiners), and contextual lookups whose nested lookups add glyphs index the
    sequence as HarfBuzz does.
- Text extraction reads right to left lines in logical order when they start or end with a Latin
  word: the paragraph direction comes from both ends of the line, and punctuation next to the
  word stays in it ("To." rather than "To ."). Numbers in right to left lines follow the
  weak rules of UAX #9: after Arabic, "2026-114" (drawn 114-2026) reads in logical order, while
  dates, amounts and percentages stay whole.
- Public benchmark corpus: `eng/corpus/manifest.json` lists 77 public domain and openly licensed
  PDFs (U.S. government publications, arXiv papers under CC BY 4.0, public domain scans and
  Chinese, Japanese and Korean documents from Wikimedia Commons, and test files of the veraPDF,
  qpdf, PDFium, pypdf and PDF Association projects) by URL, SHA-256, size, license, where the
  license is stated and what each file exercises. No file is committed. `tools/fetch_corpus.py`
  downloads and verifies them; `tools/corpus_bench.py` runs `tools/PdfNetStack.CorpusRunner` on
  each one, one process per measurement, for lossless and lossy optimization (size, time, peak
  memory), checks every rewrite again with `PdfEquivalence`, compares the text and decoded images
  the library reads before and after, compares PDFium renders, and writes a Markdown and a JSON
  report. The `Corpus benchmark` workflow runs it weekly and on demand.
- Fixed, found by the corpus:
  - A cross-reference `/Prev` chain that loops is treated as damage and the file is rebuilt from
    its objects. A newest section naming itself as `/Prev` used to hide every older section, so
    the file opened without its page tree, and the optimizer (with `AllowRepairedFiles` or not)
    could rewrite it as a document without pages.
  - Content stream compaction judges whether a stream ends inside a comment from its tokens. A
    `%` inside a string on the last line, common in the two byte codes of composite fonts, was
    taken for a comment once the stream was compacted onto one line, so the proof rejected the
    rewrite and the whole document was returned unchanged.
  - JBIG2: the work budget is eight times the page instead of four. Scans that code the page as
    one symbol placed by a text region spend five, and failed to decode.
- Optimizer, lossless structural passes, each proven by the equivalence check against its own
  analysis of the source (docs/FIDELITY.md gives the rules):
  - `PruneUnusedResources` removes fonts, XObjects, graphics states, patterns, shadings, color
    spaces and property lists that no content using the resource dictionary names, following
    forms, tiling patterns, Type 3 glyph procedures, soft masks and annotation appearances, and
    inherited and shared resource dictionaries. Anything it cannot read for certain is kept.
  - `CompactContentStreams` rewrites content streams with minimal white space, canonical numbers,
    shortest strings and no comments; inline image data is copied exactly.
  - `MergeFontSubsets` merges subsets of one TrueType font embedded separately, as merged
    documents carry them, into one program when no glyph any page shows changes.
  - Deduplication compares streams by their decoded content, so differently encoded copies of a
    font or image merge.
  - `Linearize` (opt-in) writes a linearized file (Annex F) with page offset, shared object and
    outline hint tables, laid out the way qpdf checks it. Inherited page attributes move onto the
    pages. `OptimizationStatistics` reports `ResourcesPruned`, `ContentStreamsCompacted`,
    `FontProgramsMerged` and `Linearized`; the command line tool takes `--linearize`,
    `--no-prune`, `--no-compact` and `--no-font-merge`.
- `PdfDocument.OpenOnDemand(Stream)` opens a document on a seekable stream without reading it into
  memory: objects are parsed when first used and stream data is read when asked for, with 64 bit
  offsets. `PdfDocument.SaveIncremental(Stream)` saves the changes since opening as an incremental
  update, appending in place when given the source stream; existing signatures stay valid.
- `PdfEquivalence.FindDifference` accepts the structural passes above and inherited page
  attributes moved onto pages.
- `tools/make_optimizer_samples.py` and `tools/measure_optimizer.py` measure each pass on
  synthetic files and check linearized output with qpdf when it is installed.
- Annotations: `PdfAnnotationCollection.AddStamp` (the fourteen standard stamps, or a custom one
  from a `PdfForm` or a drawing callback), `AddInk`, `AddSquare`, `AddCircle`, `AddLine` (with
  `PdfLineEnding` at either end), `AddPolygon`, `AddPolyline` and `AddFileAttachment` (the file
  embedded behind an icon; `PdfAnnotation.Attachment` reads it back). `PdfAnnotationOptions`
  sets the color, interior color, border width, dashes, opacity, comment, author and subject.
  Each comes with a generated appearance; below full opacity the drawing is a transparency group
  faded as a whole. PDF/A-3 output declares the files of file attachment annotations as
  associated files, and PDF/A-2 refuses them.
- Annotation flattening: `PdfAnnotationCollection.Flatten(annotation)`, `Flatten(options)` for a
  page and `PdfDocument.FlattenAnnotations(options)` draw each annotation's normal appearance (or
  the one its state selects) into the page content, fitted to its rectangle by bounding box and
  matrix as ISO 32000-1, 12.5.5 describes, upright on rotated pages for NoRotate annotations and
  inside its optional content group, then remove it with its pop up. Hidden and NoView
  annotations stay; links and widgets stay unless `PdfFlattenOptions` asks for them, and a
  flattened widget leaves the form. `PdfAcroForm.Flatten` uses the same placement.
- Form fields travel with their pages: `Pages.Import`, `Merge` and `Split` copy each widget's
  field and ancestors into the target form, with the source form's default appearance,
  alignment, resources and calculation order. A top level field whose name the target already
  uses is renamed with the first free suffix `_2`, `_3` and so on. Before, widgets were copied
  without their fields.
- Format actions: a field whose format action calls `AFNumber_Format`, `AFPercent_Format`,
  `AFDate_FormatEx`, `AFDate_Format`, `AFTime_Format`, `AFTime_FormatEx` or `AFSpecial_Format`
  shows its value formatted in the generated appearance (red for negative numbers when asked),
  and `PdfTextField.FormattedText` returns it. The call is parsed and the formatting done in C#;
  no JavaScript runs, and any other script leaves the value as it is.
- Fixed: form flattening threw on a widget whose appearance entry was not a stream.
- HTML grid layout follows CSS Grid 1 and 2 further: `grid-auto-flow` with `column` and `dense`
  packing; line names in track lists and `repeat()`, placement by name and by `span name`, with
  the `name-start` and `name-end` lines of areas and the implicit lines past the explicit grid;
  implicit tracks before the explicit grid for negative lines; `subgrid` on rows, columns or
  both, with the parent's line names, the subgrid's own name list, its own gaps, and its items
  sizing the parent's tracks through its margins, borders and padding. `repeat(auto-fill)`
  counts the other tracks of the list when working out how many repetitions fit, and the empty
  tracks of `repeat(auto-fit)` collapse with their gaps. The `grid-template` and `grid`
  shorthands are read, area strings and `auto-flow` included.
- HTML named pages: the `page` property works on any block in the block flow, not only children
  of `body` (CSS Paged Media 3, 8). A change of page name forces a break, and the content after
  it is laid out at its own page type's width. A box that spans the break is sliced: its top
  edge shows before the break and its bottom edge after, and the margin after the break is kept.
- HTML pagination: a forced break before the first content or after the last no longer adds an
  empty page, so `break-before: page` on the first element starts the document on page one.
- Fixed: HTML boxes with a `z-index` now paint in stacking order (CSS 2.1, Appendix E). A
  negative `z-index` paints over the stacking context's background and borders and under its
  blocks, floats and text; a positive one paints over all of it, in z-index order. A
  `position: fixed` box with a negative `z-index` paints under the content of every page, where
  before it painted over it. Positioned boxes, flex items and grid items with a `z-index`, and
  boxes with opacity below one or a transform, start a stacking context.
- Fixed: HTML `text-decoration-style: wavy` draws a wave instead of a straight line, for
  underline, overline and line-through. Its amplitude and wavelength follow the line's
  thickness, and it is clipped to the decorated text.
- PAdES B-T, B-LT and B-LTA. `PdfTimestampClient` builds RFC 3161 requests (hash, random nonce,
  optional policy) and checks the reply (status, hash, nonce, signature, a critical time stamping
  extended key usage); the application supplies the transport. `PdfSignatureOptions.Timestamp`
  adds a signature timestamp, `PdfSigner.AddDocumentTimestamp` a /DocTimeStamp, and
  `PdfSigner.AddValidationData` the document security store: certificate chains, OCSP responses
  and CRLs from an `IPdfRevocationSource`, and a VRI entry per signature, in an incremental update.
  Only revocation data that matches the certificate and is signed by its issuer or delegated
  responder is kept; delta and partitioned CRLs are not used. The library makes no network calls.
- Signature validation: `PdfSigner.Verify(pdf, PdfValidationOptions)` builds chains to caller
  supplied trust anchors and checks validity at the timestamp's time when its authority validates
  (otherwise now), key usage (signer,
  CA with path length, time stamp authority) and revocation from the document security store or
  supplied data, with OCSP freshness and delegated responders. `PdfCertificateValidation` reports
  each check per certificate as passed, failed, indeterminate or not checked, with the reasons.
  `PdfSignatureInfo` now also reports document timestamps and signature timestamps.
- Factur-X and ZUGFeRD: `PdfInvoice.Attach` embeds the invoice XML (checked for well formedness,
  without DTDs) as factur-x.xml, or xrechnung.xml for XRECHNUNG, with the profile's relationship;
  saving as PDF/A-3 writes the fx: XMP properties (MINIMUM to EXTENDED and XRECHNUNG) with their
  extension schema, checks the attachment is still there, and keeps the metadata when an opened
  invoice is saved again.
- PDF/A-3: the catalog's /AF array now references the attachment's file specification itself
  instead of a copy of it.
- CI: a veraPDF job validates PDF/A-2b, 2u, 3b and 3u files made by the library, including a
  Factur-X invoice signed to PAdES B-LTA.
- Lossy image options for the optimizer, all off by default:
  - `MaxImageDpi` (`--max-dpi`) downsamples images drawn above 1.5 times the target resolution,
    measured from the largest placement found on pages, in forms, tiling patterns, soft mask
    groups and annotation appearances. The filter is Lanczos with three lobes. Masks, images with
    a color key mask or a /Matte soft mask, bilevel, indexed, 16 bit, Lab, Separation and DeviceN
    images, and images reachable from anything the measurement does not walk (Type 3 glyphs,
    thumbnails, alternates) are left alone. A downsampled JPEG stays a JPEG, never finer than it was.
  - `ConvertImagesToJpeg` (`--to-jpeg`) re-encodes raw 8 bit gray and RGB photographs as baseline
    JPEG at `JpegQuality` (forward DCT, libjpeg quality tables, 4:2:0 below quality 90, Huffman
    tables optimized per image). A classifier leaves line art, screenshots, charts, palette and
    bilevel images lossless. A lossy result is kept only when it is at least 10% smaller than the
    best lossless one.
  - `MinImageSsim` (`--min-ssim`) is a perceptual gate on luminance SSIM: any image whose lossy
    change, requantization included, falls below it stays as it was, and
    `ImagesKeptByQualityGate` counts them. The proof checks resampled and converted images
    against the source resampled to their new size (components, aspect, mean color, a coarse
    comparison and SSIM), and with a gate also requantized ones. An image whose data is carried
    over unchanged must keep its size. `PdfEquivalence.FindDifference` takes the same gate and
    the optimizer's exclusions: soft masks, masks, stencil and color keyed images must match
    exactly, and images with a /Mask or a /Matte soft mask keep their size.
  - A JPEG with a color key mask is no longer requantized, since the key matches exact sample
    values.
  - `ProgressiveJpeg` (`--progressive`) also writes every JPEG the optimizer produces as
    progressive, with libjpeg's scan script and Huffman tables per scan, and keeps it when
    smaller. Coefficients are unchanged, so this one is lossless.
  - `OptimizationStatistics` reports `ImagesDownsampled`, `ImagesConvertedToJpeg` and
    `ImagesKeptByQualityGate`, and the result's reason names every lossy change.
- `CompressionEffort.Exhaustive` (`--exhaustive`): an original Deflate encoder in the manner of
  Zopfli, written from RFC 1951 and published descriptions: optimal parsing over every match
  length with costs refined over several passes, block splitting, and code lengths limited by
  package-merge. zlib's output is kept whenever it is smaller. On the synthetic files of
  `tools/make_lossy_corpus.py` it saves 3 to 5% more than level 9 on text, scans and photographs
  (nothing on line art and screenshots), for 2 to 150 times the CPU time, the most on long
  repetitive streams.
- `PdfNetStack.Azure` is discontinued so the project can focus on PDF itself. Reading from and
  writing to storage belongs to the application, around the stream APIs. Version 1.0.0 stays on
  NuGet and keeps working.
- Variable fonts: `PdfOpenTypeFont.GetInstance` gives a named instance (`NamedInstances`) or any
  axis values (`VariationAxes`, `IsVariable`), embedded as a static font. TrueType outlines move
  by their gvar deltas, with untouched points interpolated and composite offsets varied; advances
  come from HVAR or the phantom points, font metrics from MVAR (with hhea kept in step), control
  values from cvar, weight and width classes and the italic angle from the axes, through avar.
  CFF2 fonts load (at their default instance) and convert to CID-keyed CFF: blends resolve in
  charstrings and Private DICTs, subroutines are inlined, and argument runs longer than CFF's 48
  operands are split. The shaper applies GDEF variation deltas to GPOS values and anchors, and
  GSUB and GPOS feature variations. Outlines, advances and metrics match fontTools' instancer and
  shaping matches HarfBuzz on the synthetic fixtures and on the variable fonts we compare.
  Damaged variation data is a `FontFormatException`.
- Unicode normalization in the shaper, as HarfBuzz's normalizer does it: characters the font
  lacks decompose into ones it has, marks sort into canonical order (with the shaping tailorings
  for Hebrew, Arabic and others), a base and mark compose when the font has the composite, and
  spaces and the non-breaking hyphen without glyphs draw with the space and hyphen at their own
  widths. Font fallback, `CanRender` and HTML text use the same coverage. Data generated from the
  Unicode Character Database 16.0 by `tools/make_shaping_data.py`.
- Indic shaping for Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and
  Malayalam, per Microsoft's script development specifications: syllables, base consonant,
  reph, pre-base matras and consonants, the basic forms one feature at a time, final reordering,
  presentation forms, old (deva) and new (dev2) font specifications, and dotted circles for
  broken clusters. It matches HarfBuzz on a synthetic Devanagari and Bengali fixture and on the
  three faces of a font covering all nine scripts.
- Kashida justification (`PdfParagraph.Kashida`, off by default): justified Arabic lines elongate
  one join per word with tatweel, at the point Arabic typography prefers, up to three tatweel
  widths, before spaces widen. Tatweels carry no text, so copying gives the original words.
- Fixed: a mark drawn with a vertical offset (Arabic dots and vowels in many fonts) moved every
  glyph after it by its horizontal offset, so letters failed to join and lines came out wider
  than measured. Spaces at the edge of a right to left run are now written outside its
  ActualText, so viewers no longer read them on the wrong side of the neighboring run.
- Tagged PDF from HTML (`HtmlConverterOptions.Tagged`, on by default): headings, paragraphs,
  lists, tables with header and data cells, figures with their alt text, links tied to their
  annotations, sections and quotes form a structure tree in reading order (ISO 32000-1, 14.8);
  backgrounds, borders, text shadows, repeated table headers, fixed boxes and page margins are
  artifacts. The catalog is marked and viewers show the document title.
- JPEG 2000 images decode (`PdfPageImage.Decode`, JPXDecode): JP2 files and raw codestreams,
  ITU-T T.800 part 1. Every progression order and progression change, precincts, layers, tiles
  and tile-parts, PPM, PPT, SOP and EPH, all code-block styles, ROI, the 5/3 and 9/7 wavelets,
  RCT and ICT, subsampled, signed and 1 to 16 bit components, JP2 palettes and channel
  definitions. The image dictionary's color space wins, otherwise the JP2 header's (ISO 32000-1,
  7.4.9). Lossless images decode bit exact and lossy ones within one step of OpenJPEG. Sizes,
  counts and work are bounded. SMaskInData is not applied; HTJ2K (part 15) and part 2 extensions
  are not supported.
- JBIG2 images decode (`PdfPageImage.Decode`, JBIG2Decode with JBIG2Globals): generic,
  refinement, text and halftone regions with their symbol and pattern dictionaries and code
  tables, arithmetic, Huffman and MMR coded, per ITU-T T.88. Output is bounded by the image's
  size, and damaged data is a `PdfFormatException`. Tested bit for bit against PDFium on the T.88
  Annex H test datastream and synthetic streams.
- Named destinations: `PdfDocument.NamedDestinations` lists the names of the /Dests name tree
  and the catalog's /Dests, resolves them to pages, and renames them with every bookmark, link
  and GoTo action that refers to them, in their own string or name form, rewriting the name tree
  as one sorted leaf; colliding renames are refused. `PdfOutline.DestinationName` gives the name
  a bookmark points at.
- GIF images: `PdfImage.Load` reads GIF 87a and 89a (the first frame, interlaced or not, with its
  transparent color as a soft mask), so `DrawImage` and `<img>` in HTML take them too.
- BMP, TIFF and WebP images: `PdfImage.Load` reads BMP (1 to 32 bits, RLE4 and RLE8, bit fields,
  top down rows, alpha, an embedded JPEG or PNG), TIFF (strips and tiles, chunky and planar,
  uncompressed, PackBits, LZW and Deflate with the predictor, CCITT modified Huffman, Group 3 and
  Group 4, JPEG; bilevel, gray, palette, RGB, CMYK and YCbCr samples of 1 to 16 bits, associated
  and unassociated alpha, BigTIFF) and WebP (lossy and lossless, with alpha, the first frame of an
  animation). Alpha becomes a soft mask. A TIFF page stored as one strip of fax or JPEG data is
  embedded as is (CCITTFaxDecode, DCTDecode). `PdfImage.GetPageCount` and `PdfImage.Load(path,
  page)` give every page of a multi-page TIFF. Lossy WebP decodes to the pixels libwebp gives,
  bit for bit. Decoding is bounded, and damaged files fail with a `FormatException`. `<img>` in
  HTML takes all three formats.
- `PdfGraphics.DrawImage` fits an image into a box (`PdfImageFit`: fill, contain, cover, none,
  scale down), aligns it there (`PdfImageAlignment`, clipping what spills over) and fades it
  with an opacity.
- Form creation: `PdfAcroForm.AddTextField`, `AddCheckBox`, `AddRadioGroup` (buttons may span
  pages), `AddComboBox`, `AddListBox`, `AddSignatureField` and `AddPushButton` (optionally opening a
  web address), styled with `PdfFieldOptions` (standard font, size, colors, border, tooltip, read
  only, required, alignment, multiline, password, length limit, comb, editable, multiple
  selection). Every new field comes with its appearance and the print flag.
- New package `PdfNetStack.Ocr`: `PdfOcr.MakeSearchable` finds each page's
  scan, turns it upright, reads it and lays the words over it as invisible text; `Recognize`
  reports without changing the document. `OnnxOcrEngine` runs the PP-OCRv5 mobile models
  (PaddleOCR, Apache-2.0) on ONNX Runtime for Latin and Arabic script, with DB box detection, line
  orientation, CTC decoding, word boxes and right to left ordering in managed code, and model files
  checked against pinned SHA-256 hashes (`tools/fetch_ocr_models.py`). ONNX Runtime's telemetry is
  turned off before the first model loads. `IOcrEngine` plugs in any other engine.
- New package `PdfNetStack.Ocr.Models` (Apache-2.0 models, versioned on its own from 1.0.0): the
  four PP-OCRv5 model files with their license and notices, copied next to the application on build
  and publish; `OnnxOcrEngine.Load()` without a folder loads them from there. The files are never
  committed: CI and the release workflow fetch them, and packing fails when one is missing or does
  not match its pin. The license check now also covers bundled files and keeps ONNX Runtime out of
  every package but `PdfNetStack.Ocr`.
- OCR reads scans stored as several images: strips or tiles side by side, or a text mask over a
  background. The pieces are painted in drawing order into one gray canvas in page space, at the
  finest piece resolution up to `MaxOcrDpi`, decoded one at a time, with image count, combined
  pixels, canvas size and overlap capped before anything is decoded; word boxes map back to the
  page through the canvas.
- Text extraction: `PdfPage.ExtractText()` returns a page's text in reading order,
  `GetLetters()` each glyph with its text, bounds, baseline, font name, visible size and render
  mode, and `GetWords()` the words with their bounds. Glyphs are placed as the specification
  positions them (character, word and horizontal scaling, rise, TJ adjustments, forms, Type 3
  font matrices, vertical writing). Text comes from ToUnicode maps (bfchar and bfrange, ligatures,
  surrogate pairs, codes of 1 to 4 bytes), from the simple font encodings and /Differences with
  the Adobe Glyph List, from embedded CMaps with usecmap, the Identity and Unicode predefined
  CMaps, and embedded TrueType cmaps; spans marked with ActualText are replaced by their text, so
  right to left text the library writes reads back in logical order. Lines follow each run's own
  baseline, so rotated and vertical text read in order; columns of prose are found by XY cuts
  and read one after the other, right to left for Arabic and Hebrew pages, while tables keep
  reading row by row. CMaps, fonts and content are bounded and
  never throw.
- CJK text extraction: Adobe's predefined CMaps for the Adobe-Japan1, GB1, CNS1, Korea1 and KR
  collections (all 180 of cmap-resources, among them every CJK CMap of ISO 32000-1 Table 118, such
  as 90ms-RKSJ-H, GBK-EUC-H, B5pc-H, KSCms-UHC-H and the Uni...-UCS2, UTF16, UTF8 and UTF32 forms)
  split codes and give CIDs, so widths and vertical metrics follow the CMap, and the CID to
  Unicode tables of mapping-resources-pdf give text to composite fonts without a ToUnicode map,
  by the CMap's collection or the CIDFont's /CIDSystemInfo (ISO 32000-1, 9.10.2). The data is
  packed into one Brotli stream per collection (196 KB in all) and decompressed on first use.
  Embedded CMaps that use a predefined one inherit its collection, and keep their own /WMode.
- Simple fonts without /Encoding read their codes through the built-in encoding of an embedded
  CFF program (FontFile3, Type1C or OpenType): the Standard and Expert encodings and custom
  encodings of both formats with supplements, glyphs named through predefined or custom charsets.
- `PdfGraphics.DrawInvisibleText`: invisible, searchable text stretched to a box, optionally
  rotated, for OCR text layers. A built in glyphless font (Type0, Identity-H, one empty glyph,
  identity ToUnicode) takes any script; right to left text and characters outside the BMP also
  carry ActualText.
- Image extraction: `PdfPage.GetImages()` lists the images a page draws (XObjects, inside forms
  too, and inline images) with their placement, bounds and resolution. `PdfPageImage.Decode()`
  returns a `PdfBitmap` of 8 bit gray or RGB pixels from raw samples of 1 to 16 bits, from
  JPEGs (inverse DCT, fancy upsampling and color conversion as libjpeg does them, within two
  steps per sample) and from CCITT fax data (Group 3 one and two dimensional, Group 4, bit
  exact with libtiff), through the device, Cal, ICC based, Indexed and Lab spaces, and Separation
  and DeviceN (exact for a Type 2 tint transform, gray otherwise); `PdfBitmap.ToPng()` saves it. `GetEncodedData()` returns a JPEG or JPEG 2000 image as stored.
- New package `PdfNetStack.Html`: HTML and CSS to PDF in pure C#, without a browser.
  `HtmlConverter.ConvertToPdf`, `ConvertToDocument` and `ConvertFileToDocument`, configured with
  `HtmlConverterOptions` (base address, resource loader, fonts, page style, extra style sheet,
  bookmarks).
  - HTML parsed the way browsers repair it, with the legacy presentational attributes.
  - CSS: cascade, inheritance, `var()`, Selectors Level 4, media queries for print, `@import`,
    `@font-face`, `@page` with margin boxes, `calc()` and friends, CSS Color 4.
  - Layout: blocks with margin collapsing, inline text with Unicode line breaking, bidi and
    shaping, floats, relative, absolute and fixed positioning, tables in both border models,
    flexbox, grid with fr tracks, `minmax()`, `repeat()` and named areas, inline blocks,
    lists, counters and generated content.
  - Paged media: page sizes and margins, `:first`, `:left` and `:right`, named pages laid out
    at their own size, running headers and footers with page counters and named strings
    (`string-set`, `string()`), running elements (`position: running()` shown with
    `content: element()`, any content: logos, tables, styled text, changing with the page where
    they stand, with page counters inside), margin boxes that widen when
    their neighbours are empty, forced and avoided breaks, orphans and widows, table headers
    repeated on every page.
  - Painting: backgrounds with images and gradients, rounded borders, shadows, outlines,
    transforms, `object-fit`, decorations; links and bookmarks; document metadata from the head.
  - Safe by default for untrusted HTML: no network access without a loader, files only from
    the base folder, and limits on nesting, selectors, `var()` expansion, spans and tiling.
  - It depends on the exact `PdfNetStack` version it ships with.
- The `PdfNetStack` and `PdfNetStack.Azure` packages are checked against 1.0.0 on every pack, so
  a change that would break code built against the published version fails the build.
- SVG: `PdfSvg.Load` and `Parse`, and `PdfGraphics.DrawSvg`, draw SVG as PDF vectors: shapes,
  paths with arcs, groups, `use` and `symbol`, nested viewports with `viewBox` and
  `preserveAspectRatio`, transforms, solid colors, linear and radial gradients, clip paths,
  opacity, dashes, text in the standard fonts, embedded images, and `<style>` rules with type,
  class and id selectors. Each drawing is written once per document. Doctypes are skipped, so
  entity expansion cannot be abused, and size, depth and `use` nesting are capped. In
  `PdfNetStack.Html`, SVG works in `<img>`, CSS backgrounds and inline `<svg>`.
- Public object model: `PdfObject`, `PdfNull`, `PdfBoolean`, `PdfNumber`, `PdfName`,
  `PdfString`, `PdfArray`, `PdfDictionary`, `PdfStream` and `PdfReference` in
  `PdfNetStack.Objects`, with `PdfDocument.Catalog`, `Resolve`, `AddObject` and
  `GetStreamData`, and `PdfPage.Dictionary`, for features the high level API does not cover.
- Drawing: `PdfPage.CreateGraphics()` returns a `PdfGraphics` canvas (top left origin, points)
  for new and existing pages, in front of or behind the existing content. `PdfPath` with lines,
  Bezier curves, arcs, ellipses, polygons and rounded rectangles; `PdfPen`, `PdfColor` (gray, RGB,
  CMYK, opacity), solid, linear and radial gradient brushes; transforms, saved states, clipping.
  Only changed graphics state is written, and shared page resources are never modified.
- Text with the 14 standard fonts: `PdfStandardFont`, `PdfGraphics.DrawString` (fill with any
  brush including gradients, stroke with a pen, or both) and `PdfFont.MeasureString`, with
  ascent, descent, line height, cap height, x-height and underline metrics. Latin text uses
  WinAnsiEncoding; up to 37 further characters per font and document (Central European letters,
  the minus sign) get codes through /Differences. Symbol and ZapfDingbats take their Unicode
  characters. Control and format characters are skipped; a character the font lacks throws.
- Embedded fonts: `PdfOpenTypeFont.Load` reads TrueType and OpenType (CFF) fonts from .ttf,
  .otf, .ttc/.otc collections, WOFF and WOFF2 files. Drawn text embeds a subset of the glyphs
  used as a Type 0 font (CIDFontType2 or CIDFontType0C), with a W array and a ToUnicode map, so
  text is searchable and copyable in any script. Pair kerning from GPOS (formats 1 and 2, extension
  lookups) or the kern table. Line metrics follow browsers (typo metrics when the font sets
  USE_TYPO_METRICS, else hhea). Fonts whose fsType forbids embedding are refused; damaged fonts
  raise `FontFormatException`.
- `PdfFontCollection`: add fonts, files and directories, or use `PdfFontCollection.System` for
  installed fonts; `Find(family, weight, italic)` matches as CSS does. Scanning reads only the
  name, OS/2 and head tables; fonts load on first use.
- WOFF2 fonts: `PdfOpenTypeFont.Load`, `PdfFontCollection` (.woff2 files) and `@font-face` in
  `PdfNetStack.Html` read WOFF 2.0, including transformed glyf, loca and hmtx tables and font
  collections. Table checksums and head.checkSumAdjustment are recomputed. .NET 8 and later
  decompress with the built-in Brotli decoder; .NET Standard 2.0 uses a managed Brotli decoder
  (RFC 7932) with the static dictionary embedded. Lengths, offsets and counts are all checked, the
  decompressed size is capped at 128 MB as for WOFF, and damaged files raise
  `FontFormatException`.
- `PdfFont` gains `StrikeoutPosition` and `StrikeoutThickness`.
- Complex text: `DrawString` and `MeasureString` run the Unicode bidirectional algorithm and an
  OpenType shaper. Arabic and Persian join and take their contextual forms, ligatures form
  (lam-alef, fi), vowel marks sit on their letters, mixed right to left and left to right text is
  ordered correctly with mirrored brackets, and kerning follows GPOS or the kern table as
  HarfBuzz applies them. Shaped and right to left text carries ActualText, so it copies and
  searches in logical order.
- Paragraph layout: `PdfParagraph` (rich text spans of `PdfTextStyle`), `PdfTextLayout` and
  `PdfGraphics.DrawText`. Wrapping at Unicode line break opportunities, start, end, left, right,
  center and justified alignment, right to left paragraphs, fallback fonts per style, letter and
  word spacing, underline and strikethrough, first line indent, CSS line height, soft hyphens,
  tabs, and drawing a range of lines for text split across pages.
- Images: `PdfImage.Load` for JPEG (embedded without recompression, EXIF orientation, Adobe
  CMYK, ICC profiles, JFIF density) and PNG (all color types and bit depths, Adam7 interlacing,
  tRNS as color key or soft mask, alpha channels as soft masks, iCCP, pHYs; simple PNGs keep
  their compressed data with PNG predictors). `PdfGraphics.DrawImage` at natural or given size.
  Each image is written once per document.
- Form XObjects: `PdfForm.Create` for reusable drawings, `PdfForm.FromPage` to place any page of
  any document (rotation honored, resources imported once), `PdfGraphics.DrawForm`, and
  `PdfGraphics.Form` for drawing into a form.
- Annotations: `PdfPage.Annotations` reads every annotation (kind, rectangle, contents, link
  address, destination page including named destinations) and adds web and page links, notes,
  text markup (highlight, underline, strikeout, squiggly) and free text, each with an appearance
  stream. `PdfGraphics.AddLink` maps a canvas rectangle through the current transform.
- `PdfGraphics.BlendMode`: the 16 PDF blend modes, saved and restored with the graphics state.
- Interactive forms: `PdfDocument.Form` lists every field (`PdfTextField`, `PdfCheckBoxField`,
  `PdfRadioButtonField`, `PdfChoiceField`, `PdfPushButtonField`, `PdfSignatureField`) with fully
  qualified names, flags, options and widgets. Setting values regenerates appearances (borders,
  backgrounds, DA font and color, auto size, multiline, comb, password) and drops XFA.
  `PdfAcroForm.Flatten` stamps the appearances into the pages and removes the form.
- Document features: `PdfDocument.Viewer` (page layout, page mode, window options, right to left
  reading, opening page and zoom), `PdfDocument.PageLabels` (roman, decimal and letter ranges
  with prefixes, label lookup), `PdfDocument.Attachments` (embed, read, replace, remove files)
  and `PdfDocument.Language`.
- PDF/A: `PdfSaveOptions.Conformance` writes PDF/A-2b, 2u, 3b or 3u. Problems that cannot be
  repaired are listed together in one exception; the output gets an sRGB output intent with a
  profile the library generates, XMP metadata synchronized with the document information,
  printable annotations, and PDF/A-3 associated file entries for attachments.
- Digital signatures: `PdfSigner.Sign` adds a PAdES B-B signature (ETSI.CAdES.detached, SHA-256,
  384 or 512, RSA or ECDSA, ESS signing certificate v2) in an incremental update, matching the
  file's cross-reference style, optionally with a visible appearance. `PdfSigner.Verify` checks
  each signature's byte range, digest and signature and reports the signer certificate. The CMS
  structure is encoded by the library; no new dependency.
- Packages now include `THIRD-PARTY-NOTICES.md`, which carries Adobe's notice for the standard
  font metrics.

## 1.0.0 (2026-10-28)

First release on NuGet: `PdfNetStack`, `PdfNetStack.Tool` and `PdfNetStack.Azure`.

- JPEG images: lossless re-encoding on by default (`OptimizeJpegImages`). Huffman tables are
  rebuilt for each image, progressive and restart marked JPEGs become baseline, and segments PDF
  readers ignore are dropped. Coefficients are untouched and verification compares them one by one.
- Opt-in JPEG requantization (`JpegQuality`, CLI `--jpeg-quality`): coarser tables at a libjpeg
  quality, never finer than the image's own, applied in the coefficient domain so the color model
  cannot change. Soft masks stay lossless. Verification checks each coefficient is exactly the
  requantized source value. `OptimizationStatistics.JpegImagesRecompressed` and
  `JpegImagesRequantized`; the Azure sidecar records the quality, and lossy blobs carry
  `jpegquality` metadata.
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
- JPEG reader hardening: oversubscribed Huffman tables are refused before use, at most 64 scans
  per image, frames that claim more blocks than their data can code are refused as decompression
  bombs, JFIF and Adobe segments are kept once, and an image with a component no scan covers is
  left untouched.
- Verification accepts requantized coefficients only on the images the optimizer requantized;
  every other JPEG must stay coefficient identical.
- Requantization follows the Adobe marker over `/ColorTransform` when choosing chroma tables, as
  ISO 32000-1 specifies.
- NuGet packages: icon, a readme per package, package validation.

## 0.1.0

First public version.

- Lossless optimizer: unreachable object removal, object streams, compressed xref streams,
  stream recompression, PNG prediction for raw images, iterative deduplication, compact syntax.
- Equivalence verifier on every rewrite, public as `PdfEquivalence.FindDifference`.
- Refuses encrypted, signed, incrementally updated and damaged files unless allowed.
- PDF/A-1 detection with classic xref output.
- `pdfnetstack` command line tool: optimize, batch, inspect, verify.
- `PdfNetStack.Azure`: Blob Storage pipeline with content-addressed output and provenance sidecar.
