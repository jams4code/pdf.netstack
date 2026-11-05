---
title: CSS support in PdfNetStack.Html
description: Which CSS selectors, properties, layout modes and at-rules the PdfNetStack.Html converter supports when turning HTML into PDF, and the known gaps.
since: '1.1'
sidebar:
  label: CSS support
faq:
  - q: Does PdfNetStack.Html support flexbox and CSS grid?
    a: Yes. Flexbox with wrap, gap, grow, shrink, alignment and order, and grid with fr tracks, minmax(), repeat() with auto-fill and auto-fit, named areas and lines, row and column auto-placement with dense packing, and subgrid.
  - q: Are @supports, @keyframes and animations supported?
    a: No. Rules that only matter on screen, such as @keyframes and @supports, are skipped. Print layout does not need them.
---

What the converter reads, module by module. A property it does not know, or a value that does
not match the property's grammar, is dropped as browsers drop it, and the rest of the rule still
applies.

## Short answer

PdfNetStack.Html implements the CSS a print template needs: the cascade, Selectors Level 4,
block, inline, table, flex and grid layout, floats, positioning, paged media, web fonts,
backgrounds, borders, shadows and transforms. It has no multi-column layout, no vertical writing
modes and no JavaScript.

```csharp sample="html-layout.cs" region="css"
```

![A page laid out with a grid: a sidebar with a radial gradient, three cards in a wrapping flex row, a floated figure with a rotated badge, a grid gallery with a spanning item, a subgrid table and numbered steps](/previews/html-layout.webp)

## Cascade and values

| Feature | Support |
|---|---|
| Origins, specificity, order, `!important` | Yes |
| Inheritance, `inherit`, `initial`, `unset`, `revert` | Yes |
| Custom properties and `var()` with fallbacks | Yes, expansion bounded |
| `calc()`, `min()`, `max()`, `clamp()` | Yes |
| Lengths | `px`, `pt`, `pc`, `cm`, `mm`, `q`, `in`, `em`, `rem`, `ex`, `ch`, `lh`, `vw`, `vh` and the other viewport units, percentages |
| Colors | Named colors, hex, `rgb()`, `hsl()`, `hwb()`, `lab()`, `lch()`, `oklab()`, `oklch()`, `currentcolor`, transparency |
| `style` attribute | Yes |
| Legacy attributes | `align`, `bgcolor`, `width`, `cellpadding`, `cellspacing`, `border`, `font` |

## Selectors

| Feature | Support |
|---|---|
| Type, class, id, universal | Yes |
| Attributes | `[a]`, `=`, `~=`, `|=`, `^=`, `$=`, `*=`, with the `i` flag |
| Combinators | Descendant, `>`, `+`, `~` |
| Logical | `:is()`, `:where()`, `:not()` with selector lists |
| Structural | `:first-child`, `:last-child`, `:only-child`, the `-of-type` forms, `:nth-child()`, `:nth-last-child()` with `of S`, `:nth-of-type()`, `:nth-last-of-type()`, `:root`, `:empty` |
| Other | `:lang()`, `:dir()`, `:link`, `:any-link`, `:checked`, `:disabled`, `:enabled`, `:required`, `:optional`, `:read-only`, `:read-write` |
| Pseudo-elements | `::before`, `::after`, `::marker` |
| Not supported | `:has()`, `::first-line`, `::first-letter`. Interactive states such as `:hover` never match in print |

## At-rules

| Rule | Support |
|---|---|
| `@media` | Yes, as print, with the page's width, height and orientation |
| `@import` | Yes, with media conditions, nesting and count bounded |
| `@font-face` | Yes: TrueType, OpenType, WOFF and WOFF2, `local()`, weight ranges and styles |
| `@page` | Yes: sizes, margins, margin boxes, `:first`, `:left`, `:right`, named pages. See [Paged media](/guides/html-to-pdf/paged-media/) |
| `@supports`, `@keyframes`, `@layer` and others | Skipped |

## Layout

| Mode | Support |
|---|---|
| Block | Margin collapsing, `box-sizing`, min and max sizes, `overflow` clipping |
| Inline | Unicode line breaking, `text-align` with `justify`, `text-indent`, `white-space`, `vertical-align` (every value), soft hyphens, `hyphens`, `overflow-wrap`, `word-break` |
| Floats | `float`, `clear`, text wrapping around floats |
| Positioning | `relative`, `absolute`, `fixed` (repeated on every page), `z-index` in stacking order: positioned boxes, flex and grid items with a `z-index`, and boxes with opacity below one or a transform start a stacking context; a negative `z-index` paints under the context's content |
| Tables | Both border models, `rowspan` and `colspan`, fixed and automatic widths, captions, header and footer groups, header rows repeated on every page |
| Flexbox | Direction, wrap, `gap`, grow, shrink and basis, `justify-content`, `align-items`, `align-self`, `align-content`, auto margins, `order` |
| Grid | `fr`, `auto`, `min-content`, `max-content`, `minmax()`, `repeat()` with counts, `auto-fill` and `auto-fit` (empty tracks collapse); line names in track lists and `repeat()`, placement by line, name, `span name` and named areas with their `-start` and `-end` lines; implicit tracks on both sides; `grid-auto-flow` with `row`, `column` and `dense`; `subgrid` on rows, columns or both; the `grid-template` and `grid` shorthands; alignment |
| Other | `inline-block`, `inline-flex`, `inline-grid`, `flow-root`, `display: contents`, `display: none`, list items with markers |
| Not supported | Multi-column layout, vertical writing modes |

## Text and fonts

| Feature | Support |
|---|---|
| Font selection | `font-family` lists, `font-weight`, `font-style`, `font-size` keywords and lengths, the `font` shorthand |
| Shaping | Kerning, ligatures, Arabic joining and vowel marks, Indic scripts, Unicode normalization, matching HarfBuzz on the fonts compared in tests |
| Direction | `direction`, `unicode-bidi`, `dir`, with the Unicode bidirectional algorithm |
| Spacing and case | `letter-spacing`, `word-spacing`, `text-transform`, `line-height`, `tab-size` |
| Decoration | `text-decoration` underline, overline and line-through, in solid, double, dotted, dashed and wavy, with color and thickness; `text-shadow` without blur |
| Not supported | `font-variant-*`, `font-feature-settings`, `font-stretch`, `hyphenate-character`, `hyphenate-limit-chars`, `text-overflow` |

### Hyphenation

`hyphens: auto` hyphenates text in the language of the nearest `lang` attribute, as long as
`HtmlConverterOptions.Hyphenators` finds patterns for it. Without that option, or without a `lang`,
`auto` behaves like `manual`, which is what happened before and stays the default. Add the
`PdfNetStack.Hyphenation` package for the patterns:

```csharp sample="fonts-hyphenation.cs" region="html"
```

`hyphens: none` also turns off soft hyphens. Text under `white-space: nowrap` or
`word-break: break-all` is not hyphenated, and a word split across inline elements counts as one.
Min-content sizes count the points, so a narrow table column can hyphenate instead of overflowing.
See [Hyphenation](/guides/fonts-and-text/#hyphenation) for the languages and the words left alone.

## Generated content and lists

| Feature | Support |
|---|---|
| `content` | Strings, `attr()`, `counter()`, `counters()`, quotes, `url()` images |
| Counters | `counter-reset`, `counter-increment`, `counter-set`, nested counters |
| List styles | `disc`, `circle`, `square`, `decimal`, `decimal-leading-zero`, roman, alphabetic, `lower-greek`, `arabic-indic`, `persian`, `devanagari` and others; `list-style-position`; `list-style-image` |

## Painting

| Feature | Support |
|---|---|
| Backgrounds | Colors, images, `linear-gradient()` and `radial-gradient()`, several layers, `background-position`, `size`, `repeat`, `origin` and `clip` |
| Borders | Widths, colors, rounded corners with elliptical radii; `solid`, `dashed`, `dotted` and `double` styles. `groove`, `ridge`, `inset` and `outset` are drawn flat |
| Shadows and outlines | `box-shadow` with approximated blur (inset shadows are not drawn), `outline` with `outline-offset` |
| Effects | `opacity` (applied per element), `visibility`, 2D `transform` (`translate`, `scale`, `rotate`, `skew`, `matrix`) with `transform-origin` |
| Images | `object-fit`, `object-position` |
| Not supported | `filter`, `clip-path`, `mix-blend-mode`, conic and repeating gradients, 3D transforms |

## Paged media and output

| Feature | Support |
|---|---|
| Breaks | `break-before`, `break-after`, `break-inside`, the legacy `page-break-*` properties, `orphans`, `widows` |
| Named pages | `page` on any block in the block flow; a change of page name forces a break. No effect inside flex, grid and table containers |
| Running content | `string-set` and `string()`, `position: running()` and `element()`, `counter(page)` and `counter(pages)` |
| Bookmarks | `bookmark-level` and `bookmark-label`; headings get levels by default |
| Not supported | `target-counter()`, `leader()`, footnotes |

## Limits

- No JavaScript: pages must be rendered HTML, not built in the browser.
- Images: JPEG, PNG, GIF, BMP, TIFF and WebP (the first frame of animations), and SVG without
  filters, masks or patterns.
- An inline running element shows on every page rather than from where it stands.
- `font-weight` and `font-variation-settings` do not choose a variable font instance yet; that is
  on the [roadmap](/roadmap/).

## Related

- [HTML to PDF overview](/guides/html-to-pdf/)
- [Paged media](/guides/html-to-pdf/paged-media/)
- [Fonts and assets](/guides/html-to-pdf/fonts-and-assets/)
- [Roadmap](/roadmap/)
- API: [HtmlConverter](/api/pdfnetstack-html/htmlconverter/)
