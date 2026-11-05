# PdfNetStack.Hyphenation

Hyphenation patterns for [PdfNetStack](https://www.nuget.org/packages/PdfNetStack) paragraphs and
for `hyphens: auto` in [PdfNetStack.Html](https://www.nuget.org/packages/PdfNetStack.Html). The
algorithm lives in the core library; this package adds the data, about 520 KB compressed, so
applications that do not hyphenate do not carry it.

```csharp
using PdfNetStack.Drawing;
using PdfNetStack.Hyphenation;

var p = new PdfParagraph { Alignment = PdfTextAlignment.Justify, Hyphenator = PdfHyphenationPatterns.ForLanguage("de") }
    .Add("Die Rechtschreibung regelt die Silbentrennung.", new PdfTextStyle(font, 11));
```

```csharp
using PdfNetStack.Html;
using PdfNetStack.Hyphenation;

// Elements with hyphens: auto and a lang attribute are hyphenated in their language.
byte[] pdf = HtmlConverter.ConvertToPdf(
    """<html lang="fr"><body style="hyphens: auto; text-align: justify">...</body></html>""",
    new HtmlConverterOptions { Hyphenators = PdfHyphenationPatterns.ForLanguage });
```

## Languages

`en-us`, `en-gb`, `de` (reformed spelling), `fr`, `nl`, `es`, `it`, `pt`, `af`, `be`, `bg`, `cy`,
`da`, `el-monoton`, `el-polyton`, `et`, `eu`, `ga`, `gl`, `hr`, `la`, `lt`, `nb`, `nn`, `pl`, `sk`,
`sl`, `sq`, `uk`. Each loads on first use.

Not included, because their only patterns have licenses this package does not take: `ca`, `cs`,
`fi`, `hu`, `id`, `is`, `lv`, `mk`, `ro`, `ru`, `sr`, `sv`, `th`, `tr` and a few others. Patterns
you are licensed to use go through `PdfHyphenator.Create`.

## Licenses

The code is MIT. The patterns come from [hyph-utf8](https://github.com/hyphenation/tex-hyphen) and
keep their own terms: MIT, BSD-3-Clause, the Unlicense and short permissive notices, listed with
every copyright holder in `LICENSE.txt` in the package.
