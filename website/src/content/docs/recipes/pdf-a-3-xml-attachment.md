---
title: Create a Factur-X invoice PDF in C#
description: Make a Factur-X or ZUGFeRD e-invoice in .NET, a PDF/A-3 file with the invoice XML attached and described in its metadata. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Factur-X invoice
---

Turn an invoice PDF and its XML into one Factur-X or ZUGFeRD hybrid invoice.

## Short answer

Draw or convert the invoice, attach its XML with `PdfInvoice.Attach` and the profile it follows,
then save as PDF/A-3b. The XML is checked for well formedness and embedded as `factur-x.xml`
with the relationship the profile calls for, and saving writes the Factur-X XMP metadata with its
extension schema.

```csharp sample="recipe-pdf-a-3-xml-attachment.cs" region="invoice"
```

## Notes

- PDF/A needs embedded fonts, so the sample draws with an OpenType font rather than one of the
  standard 14. An invoice made with [HTML to PDF](/guides/html-to-pdf/) works the same way when
  its fonts are embedded.
- Profiles: `Minimum`, `BasicWL`, `Basic`, `EN16931`, `Extended` and `XRechnung`. PdfNetStack does
  not validate the XML against the profile's rules; check the invoice data before you attach it.
- To sign the invoice, sign the saved bytes last, see [Signatures](/guides/signatures/).

## Related

- [PDF/A](/guides/pdf-a/): levels, checks and the invoice options
- [Convert an HTML invoice to PDF](/recipes/html-invoice-to-pdf/)
- API: [PdfInvoice](/api/pdfnetstack/pdfinvoice/), [PdfInvoiceProfile](/api/pdfnetstack/pdfinvoiceprofile/), [PdfSaveOptions](/api/pdfnetstack/pdfsaveoptions/)
