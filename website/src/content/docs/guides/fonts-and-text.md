---
title: Fonts and text in PDF with C# and .NET
description: Draw text in PDFs from C# with the 14 standard fonts or embedded, subset TrueType, OpenType and WOFF2 fonts, wrapped paragraphs and Arabic. MIT licensed.
since: '1.1'
sidebar:
  label: Fonts and text
faq:
  - q: Is text drawn by PdfNetStack searchable?
    a: Yes. Embedded fonts get a ToUnicode map, so text selects, copies and searches in any script the font covers. Shaped and right to left text also carries ActualText, so it copies in logical order.
  - q: Does embedding a font make the file large?
    a: No. Only the glyphs you draw are embedded, as a subset. The 14 standard fonts are not embedded at all, so a page of text in them weighs about 2 KB.
  - q: Can I write Arabic or Hebrew?
    a: Yes. Text runs through the Unicode bidirectional algorithm and an OpenType shaper, so Arabic letters join, ligatures form and mixed directions are ordered. Use a font that has the script's glyphs.
---

Text on PDF pages: the standard fonts, embedded TrueType and OpenType fonts, wrapped and justified
paragraphs, and right to left scripts.

## Short answer

`DrawString` writes one line of text at a baseline. `PdfStandardFont` gives the 14 fonts every PDF
viewer has, with nothing to embed. `PdfOpenTypeFont.Load` reads a .ttf, .otf, .ttc, .woff or
.woff2 file and embeds only the glyphs you draw.

```csharp sample="fonts-text.cs" region="standard"
```

![Text in Helvetica, Inter and Arabic drawn with PdfNetStack](/previews/fonts.webp)

## One line of text

`DrawString(text, font, size, brush, x, y)` places the start of the baseline at (x, y). To place
the top of the line instead, add `font.Ascent * size` to y. Another overload takes a pen and a
brush, so glyphs can be filled with a gradient, outlined, or both.

`MeasureString(text, size)` returns the width in points, which is all you need to right align or
center a line. Font metrics are fractions of the font size: multiply by the size to get points.

| Member | Meaning |
|---|---|
| `Ascent`, `Descent` | Height above and depth below the baseline used for line spacing (descent is positive) |
| `LineGap`, `GetLineHeight(size)` | Recommended extra space, and the default line height in points |
| `CapHeight`, `XHeight` | Height of flat capitals and of flat lowercase letters |
| `UnderlinePosition`, `UnderlineThickness` | Where an underline sits |
| `StrikeoutPosition`, `StrikeoutThickness` | Where a strikethrough sits |
| `HasGlyph(codePoint)`, `CanRender(text)` | Whether the font can draw a character or a string |

## The 14 standard fonts

`PdfStandardFont` has Helvetica, Times and Courier with their bold and italic styles, Symbol and
ZapfDingbats. Viewers supply them, so nothing is embedded.

- Latin text uses WinAnsiEncoding (Windows-1252). Up to 37 further characters per font and
  document, such as Central European letters and the minus sign, get codes through a
  /Differences array.
- Symbol and ZapfDingbats take their own Unicode characters.
- A character the font cannot draw throws an `ArgumentException`. For other scripts, embed a font.

## Embedded fonts

```csharp sample="fonts-text.cs" region="embed"
```

- TrueType and OpenType (CFF) fonts load from .ttf, .otf, .ttc and .otc collections, WOFF and
  WOFF2. For a collection, pass the index of the font; `CountFonts` says how many it holds.
- Drawn text embeds a subset of the glyphs used, as a Type 0 font with a ToUnicode map, so the
  text stays searchable and copyable.
- Pairs are kerned from the font's GPOS or kern table.
- A font is written into a document the first time text is drawn with it, once per document. One
  `PdfFont` instance serves every size and can be shared between documents and threads.
- A font whose license forbids embedding (the OS/2 fsType flags) is refused when first drawn. A
  damaged file raises `FontFormatException`.

## Font collections

`PdfFontCollection` finds fonts by family, weight and style the way CSS matches them. Adding a file
or a directory reads only the font's names and style; the font itself loads when first found.
`PdfFontCollection.System` holds the fonts installed on the machine.

```csharp sample="fonts-text.cs" region="collection"
```

`Find(family, weight, italic)` takes a CSS weight from 1 to 1000 (400 regular, 700 bold) and
returns the closest face, or null when the family is unknown. The collection is thread safe.

## Paragraphs

`PdfParagraph` holds rich text: spans, each with its own `PdfTextStyle`. `DrawText` lays the
paragraph out at a width, wraps and aligns its lines, and draws it with its top left corner at
(x, y). It returns the `PdfTextLayout`, whose `Height` tells how much room it took.

```csharp sample="fonts-text.cs" region="paragraph"
```

| `PdfParagraph` | Default | Notes |
|---|---|---|
| `Alignment` | `Start` | `Start`, `End`, `Left`, `Right`, `Center` or `Justify` (the last line aligns to the start) |
| `Direction` | `Auto` | From the first strong character, or `LeftToRight`, `RightToLeft` |
| `LineHeight` | null | A multiple of each span's font size, as CSS `line-height` takes a number; null uses the font's own spacing |
| `FirstLineIndent` | 0 | In points |

| `PdfTextStyle` | Default | Notes |
|---|---|---|
| `Font`, `Size` | | Required |
| `Brush` | black | Fill of the glyphs and decorations; null for outlines only |
| `Pen` | null | Outline of the glyphs |
| `Fallbacks` | none | Fonts tried in order for characters `Font` lacks |
| `LetterSpacing`, `WordSpacing` | 0 | Extra space in points |
| `Underline`, `Strikethrough` | false | Decorations |

`PdfTextStyle` is a record, so `body with { Font = interBold }` derives a variant.

Lines break at Unicode line break opportunities (UAX #14). A word wider than the line breaks
between characters. Line feeds start a new line, tabs advance four spaces, and soft hyphens
(U+00AD) show a hyphen when a word breaks there.

## Right to left and Arabic

Text runs through the Unicode bidirectional algorithm (UAX #9) and an OpenType shaper: Arabic and
Persian letters join and take their contextual forms, ligatures form, vowel marks sit on their
letters, and mixed right to left and left to right text is ordered with mirrored brackets. The
line breaking and bidi implementations pass the Unicode 16 conformance suites, and the shaper
matches HarfBuzz on the fonts the test suite compares.

```csharp sample="fonts-text.cs" region="rtl"
```

In a right to left paragraph, `Start` alignment means the right edge. Shaped and right to left
text carries ActualText, so it copies and searches in logical order.

## Text that continues on the next page

`PdfTextLayout.Create` measures a paragraph without drawing it. Each line reports its `Top`,
`Baseline` and `Height`, so you can decide how many lines fit, then draw a range of lines with
`DrawText(layout, x, y, first, count)` and continue on the next page.

```csharp sample="fonts-flow.cs" region="flow"
```

## Limits

- CFF2 fonts are not supported yet. Variable fonts draw their default instance.
- Indic and other complex shapers, kashida justification and dictionary hyphenation are on the
  [roadmap](/roadmap/). Lines are filled greedily.
- Standard fonts cover Windows-1252 plus up to 37 further characters per font and document.

## Related

- [Drawing](/guides/drawing/)
- [HTML to PDF](/guides/html-to-pdf/): CSS layout with `@font-face` and web fonts
- [PDF/A](/guides/pdf-a/): archival output needs embedded fonts
- API: [PdfFont](/api/pdfnetstack-fonts/pdffont/), [PdfStandardFont](/api/pdfnetstack-fonts/pdfstandardfont/), [PdfOpenTypeFont](/api/pdfnetstack-fonts/pdfopentypefont/), [PdfFontCollection](/api/pdfnetstack-fonts/pdffontcollection/), [PdfParagraph](/api/pdfnetstack-drawing/pdfparagraph/), [PdfTextStyle](/api/pdfnetstack-drawing/pdftextstyle/), [PdfTextLayout](/api/pdfnetstack-drawing/pdftextlayout/)
