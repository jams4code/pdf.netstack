---
title: Create PDF/A files in C# and .NET
description: Save PDF/A-2b, 2u, 3b and 3u from .NET, with XML attachments for e-invoices, every blocking problem listed at once. Pure C#, MIT licensed.
since: '1.1'
sidebar:
  label: PDF/A
faq:
  - q: Which PDF/A levels can PdfNetStack write?
    a: PDF/A-2b, PDF/A-2u, PDF/A-3b and PDF/A-3u, chosen with PdfSaveOptions.Conformance. PDF/A-1 files are detected and kept PDF/A-1 compatible by the optimizer, but not written from scratch.
  - q: Does PdfNetStack ship a third party ICC profile?
    a: No. The sRGB output intent uses a profile the library computes from the sRGB definition, so nothing third party ships.
  - q: Can I make Factur-X or ZUGFeRD invoices?
    a: PdfNetStack writes PDF/A-3 with the invoice XML attached and declared as an associated file. The Factur-X and ZUGFeRD XMP metadata is not written yet; it is on the roadmap.
---

Save a document as PDF/A for long term archiving, and attach the XML of an e-invoice to it.

## Short answer

Set `Conformance` in `PdfSaveOptions` when you save. PdfNetStack first checks what it cannot
repair and lists every problem in one exception. When the document passes, it adds an sRGB output
intent, XMP metadata that matches the document information, and the entries PDF/A requires for
attachments, then writes the file.

```csharp sample="pdfa-invoice.cs" region="invoice"
```

![An invoice saved as PDF/A-3b with an embedded font](/previews/pdfa-invoice.webp)

## Levels

| `PdfConformance` | Standard | Use it for |
|---|---|---|
| `PdfA2B` | PDF/A-2b (ISO 19005-2) | Archiving with a reliable visual appearance |
| `PdfA2U` | PDF/A-2u | PDF/A-2b with every character mapped to Unicode, so text extracts reliably |
| `PdfA3B` | PDF/A-3b (ISO 19005-3) | PDF/A-2b that may embed files of any type, as e-invoices do |
| `PdfA3U` | PDF/A-3u | PDF/A-3b with Unicode text |
| `None` | | The default: no particular standard |

## What is checked

Saving refuses the document, with an `InvalidOperationException` that lists every problem, when:

- a font is not embedded. The 14 standard fonts are never embedded, so draw with a
  `PdfOpenTypeFont` (see [Fonts and text](/guides/fonts-and-text/)),
- for the u levels, a composite font has no ToUnicode map (fonts PdfNetStack embeds always have
  one),
- DeviceCMYK colors or images are used, since the output intent is sRGB,
- the document is encrypted,
- the document declares a PDF version above 1.7,
- the document has document level JavaScript, document additional actions, or an open action that
  runs a script or launches a program,
- a page has movie, sound, screen, 3D or rich media annotations, or an annotation other than a
  link or popup without an appearance,
- a PDF/A-2 document has attachments.

```csharp sample="pdfa-invoice.cs" region="problems"
```

For `report.pdf`, drawn with standard fonts and given an attachment, the message reads:

```text
The document cannot be saved as PDF/A-2b:
- Font Helvetica-Bold is not embedded. PDF/A embeds every font: draw with a PdfOpenTypeFont instead of the standard fonts.
- Font Helvetica is not embedded. PDF/A embeds every font: draw with a PdfOpenTypeFont instead of the standard fonts.
- Font Times-Roman is not embedded. PDF/A embeds every font: draw with a PdfOpenTypeFont instead of the standard fonts.
- PDF/A-2 cannot embed files; use PDF/A-3 for attachments such as invoice XML.
```

## What is added and repaired

- An sRGB output intent, with an ICC profile the library computes from the sRGB definition
  (IEC 61966-2-1). No third party profile ships.
- XMP metadata with the PDF/A identification and the same title, author, subject, keywords,
  producer, creator tool and dates as `doc.Info`. A missing creation date and producer are filled
  in, and the modification date is set to the time of saving.
- Annotation flags: every annotation is set to print, and its invisible, hidden and no view
  flags are cleared.
- For PDF/A-3, every attachment is declared as an associated file of the document, with a MIME
  type and a modification date.

Set `doc.Info.Title` and `doc.Language` before saving: the title goes into the XMP metadata, and
the language into the catalog, where viewers and screen readers read it.

## Attachments for e-invoices

PDF/A-3 may embed files of any type: `doc.Attachments.Add(name, data, mimeType, description)`
embeds one. Each attachment is declared with the relationship `Unspecified` unless it already has
one. To declare another, such as `Alternative` or `Data`, set it on the attachment's dictionary
through the [object model](/guides/low-level-objects/):

```csharp sample="pdfa-invoice.cs" region="relationship"
```

Attachments can also be listed, read with `GetData()`, replaced and removed. See
[Edit pages](/guides/edit-pages/) for the other document settings.

## Documents from other sources

Conformance works on any `PdfDocument`: one you open, one you create, or one returned by
`HtmlConverter.ConvertToDocument` in [HTML to PDF](/guides/html-to-pdf/). The same checks apply,
so the fonts it uses must be embedded.

## Limits

- PDF/A-2 and PDF/A-3 only. PDF/A-1 is not written; existing PDF/A-1 files keep a classic xref
  table when the optimizer rewrites them.
- Problems are reported, not repaired: PdfNetStack does not embed missing fonts, convert CMYK or
  remove scripts for you.
- The Factur-X and ZUGFeRD XMP metadata is not written yet, and veraPDF validation is not yet part
  of CI. Both are on the [roadmap](/roadmap/).

## Related

- [Signatures](/guides/signatures/): sign the archived file last
- [Fonts and text](/guides/fonts-and-text/): embedded fonts
- [Low level objects](/guides/low-level-objects/)
- API: [PdfSaveOptions](/api/pdfnetstack/pdfsaveoptions/), [PdfConformance](/api/pdfnetstack/pdfconformance/), [PdfAttachmentCollection](/api/pdfnetstack/pdfattachmentcollection/), [PdfAttachment](/api/pdfnetstack/pdfattachment/)
