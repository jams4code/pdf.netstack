---
title: PDF/A-3 with an XML attachment in C#
description: Write a PDF/A-3b invoice with an embedded XML file in .NET, the structure e-invoice formats build on. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: PDF/A-3 with XML
---

Save a document as PDF/A-3 with an XML file attached, as e-invoices carry their data.

## Short answer

Add the XML with `doc.Attachments.Add`, then save with `Conformance = PdfConformance.PdfA3B`.
Saving checks what cannot be repaired, such as fonts that are not embedded, and lists every problem
at once. It then adds an sRGB output intent, XMP metadata that matches the document information,
and the associated file entries PDF/A-3 requires.

```csharp sample="recipe-pdf-a-3-xml-attachment.cs"
```

## Notes

- PDF/A needs embedded fonts, so the sample draws with an OpenType font rather than one of the
  standard 14.
- Factur-X and ZUGFeRD metadata is not written yet; it is on the [roadmap](/roadmap/). The
  attachment and the PDF/A-3 structure are in place.
- PDF/A-2b and 2u are available too; PDF/A-2 does not allow attachments.

## Related

- [PDF/A](/guides/pdf-a/)
- [Convert an HTML invoice to PDF](/recipes/html-invoice-to-pdf/)
- API: [PdfSaveOptions](/api/pdfnetstack/pdfsaveoptions/), [PdfAttachmentCollection](/api/pdfnetstack/pdfattachmentcollection/)
