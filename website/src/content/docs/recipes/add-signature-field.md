---
title: Add a signature field to a PDF in C#
description: Add an empty signature field to a PDF in .NET so a signer can sign it in their PDF viewer, with border and tooltip. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Add a signature field
---

Place an empty signature box on a page for someone to sign later in their PDF viewer.

## Short answer

Call `doc.Form.AddSignatureField` with the page, a field name and the box position and size,
measured from the page's top left corner in points. `PdfFieldOptions` sets the border, background
and tooltip.

```csharp sample="recipe-add-signature-field.cs"
```

## Notes

To sign in code instead, use `PdfSigner.Sign`. It adds its own signature field, with an optional
visible appearance, in an incremental update, and does not fill an existing empty field. See
[Signatures](/guides/signatures/).

## Related

- [Forms](/guides/forms/)
- [Signatures](/guides/signatures/)
- API: [PdfAcroForm](/api/pdfnetstack/pdfacroform/), [PdfFieldOptions](/api/pdfnetstack/pdffieldoptions/)
