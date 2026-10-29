---
title: Extract text from PDF files in C#
description: Read PDF text in reading order in .NET, with words and letters, their bounds, baselines, fonts and sizes. Columns, right to left and rotated text. MIT.
since: '1.1'
sidebar:
  label: Extract text
faq:
  - q: Does text extraction need OCR?
    a: No, for PDFs that contain text. Scanned pages are images; the PdfNetStack.Ocr add-on adds a text layer to them first.
  - q: What unit are the coordinates in?
    a: Points (1/72 inch) in the page's default user space, from the bottom left corner, before the page's /Rotate.
---

Get the text of a page in reading order, or every word and glyph with where it sits.

## Short answer

Open the document and call `ExtractText()` on a page. Lines come top to bottom, words are
separated by a space and lines by a line feed. `GetWords()` and `GetLetters()` return the same
text with positions, fonts and sizes.

```csharp sample="extract-text-words.cs" region="text"
```

## Words with their position

`GetWords()` returns the words in the order `ExtractText` reads them. A word ends at a space
character or at a gap wider than 0.15 times the font size.

```csharp sample="extract-text-words.cs" region="words"
```

```text
Quarterly  left 72 bottom 727.4 width 97.8
report     left 175.9 bottom 727.4 width 63.6
Page       left 72 bottom 704.7 width 25.7
1          left 100.7 bottom 704.7 width 6.1
of         left 109.9 bottom 704.7 width 9.2
```

`Bounds` is the smallest rectangle around the word's letters, and `Letters` gives the letters
themselves. Use the bounds to search, highlight or redact.

## Every letter

`GetLetters()` returns every glyph the page draws, in content order, with text inside form
XObjects included.

```csharp sample="extract-text-words.cs" region="letters"
```

| `PdfLetter` | Meaning |
|---|---|
| `Text` | What the glyph stands for: usually one character, several for a ligature such as "fi" |
| `Bounds` | The glyph's box: its advance width, from the font's descent to its ascent |
| `BaselineStart`, `BaselineEnd` | Where the glyph's baseline starts and ends |
| `Angle` | The baseline direction in degrees: 0 for ordinary text, -90 for vertical writing |
| `FontName` | The base font name without a subset prefix, such as Helvetica |
| `FontSize` | The size as it appears on the page, in points |
| `RenderMode` | The text rendering mode: 0 fill, 1 stroke, 2 fill and stroke, 3 invisible, and so on |

Glyphs are placed as the PDF specification positions them: character, word and horizontal
scaling, text rise, TJ adjustments, form XObjects, Type 3 font matrices and vertical writing.
`RenderMode` 3 marks invisible text, such as an OCR layer over a scan.

## Reading order

- Lines follow each run of text's own baseline, so rotated text and vertical writing read in
  order too.
- Columns of prose are found and read one after the other: a gutter of an em or more between
  blocks of at least three lines of a dozen characters. Columns read left to right, or right to
  left on pages whose text is mostly Arabic or Hebrew.
- Narrower or shorter side by side text, such as tables and label and value pairs, reads row by
  row, which is what tables want.
- Superscripts join their own line rather than the line above.
- Right to left text comes back in logical order, and spans marked with ActualText read as their
  replacement text, so shaped text reads the way it was typed.

Two columns whose lines share their baselines read one after the other:

```csharp sample="extract-text-columns.cs" region="columns"
```

```text
Left column, line one of
the text that reads first,
before the right column
that shares its rows.
Right column, line one of
the text that reads after
the left column is done,
from top to bottom.
```

## Where the text comes from

For each glyph, the text comes from the font's ToUnicode map, else from its encoding and glyph
names (the Adobe Glyph List, `uniXXXX` and `uXXXX` names), else from an embedded TrueType cmap.
ToUnicode maps with ranges, ligatures, surrogate pairs and codes of 1 to 4 bytes, embedded CMaps,
and the Identity and Unicode predefined CMaps are read. A glyph with no Unicode mapping reads as
U+FFFD.

## Coordinates

Positions are in points in the page's default user space: the origin is the bottom left corner
and y goes up, before the page's `/Rotate` is applied. Drawing uses the top left corner instead.
[Coordinates and units](/concepts/coordinates-and-units/) explains both and how to convert.

## Damaged and hostile files

Damaged content and fonts are read as far as they go. CMaps, fonts and content streams are
bounded and never throw, and at most 500,000 letters are read per page.

## Limits

- Composite fonts with a predefined CJK CMap other than Identity and the Unicode CMaps (such as
  90ms-RKSJ-H) rely on the font's ToUnicode map for text and use default widths for spacing.
  Adobe CJK CMaps are on the [roadmap](/roadmap/).
- Text without a Unicode mapping reads as U+FFFD.
- Scanned pages have no text until OCR adds it: see [OCR](/guides/ocr/).
- At most 500,000 letters are read per page.

## Related

- [OCR](/guides/ocr/): make scans searchable first
- [Extract images](/guides/extract-images/)
- [Coordinates and units](/concepts/coordinates-and-units/)
- API: [PdfPage](/api/pdfnetstack/pdfpage/), [PdfWord](/api/pdfnetstack/pdfword/), [PdfLetter](/api/pdfnetstack/pdfletter/)
