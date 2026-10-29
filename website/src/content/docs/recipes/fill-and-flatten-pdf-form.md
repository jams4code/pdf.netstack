---
title: Fill and flatten a PDF form in C#
description: Fill AcroForm text fields, check boxes, radio buttons and combo boxes in .NET, then flatten the values into the page. MIT licensed, pure C#.
since: '1.1'
sidebar:
  label: Fill and flatten a form
---

Fill an interactive PDF form from code, then make the values part of the page.

## Short answer

Look fields up by name in `doc.Form`, cast to the field type, and set the value. Setting a value
redraws the field's appearance, so the filled form looks the same in every viewer. `Flatten`
stamps the appearances into the pages and removes the form, so the values can no longer be edited.

```csharp sample="recipe-fill-and-flatten-pdf-form.cs"
```

![The application form with a name, a checked box, the pro plan and Morocco filled in](/previews/recipe-filled-form.webp)

## Notes

- Field names are fully qualified: a field `city` inside `address` is `address.city`.
  `doc.Form.Fields` lists them all.
- Skip `Flatten` to keep the form editable after filling it.
- A font the form embeds whole is reused for the new appearance; others map to the closest
  standard font.

## Related

- [Forms](/guides/forms/): reading, filling, flattening and creating fields
- API: [PdfAcroForm](/api/pdfnetstack/pdfacroform/), [PdfTextField](/api/pdfnetstack/pdftextfield/), [PdfChoiceField](/api/pdfnetstack/pdfchoicefield/)
