---
title: Fill and create PDF forms in C# and .NET
description: Read, fill, flatten and create AcroForm fields in C#, with appearances generated so filled forms look the same in every viewer. Pure .NET, MIT licensed.
since: '1.1'
sidebar:
  label: Forms
faq:
  - q: Will filled values show in every PDF viewer?
    a: Yes. Setting a value redraws the field's appearance from its border, background, font and color settings, so the form does not rely on the viewer to rebuild it.
  - q: How do I make a filled form read only?
    a: Call doc.Form.Flatten(). Every field's appearance is drawn into its page and the form is removed, so the values stay visible and printable but can no longer be edited.
  - q: Does PdfNetStack support XFA forms?
    a: No. Documents that also carry an XFA form lose it when values change, so viewers show the AcroForm values.
---

Interactive PDF forms (AcroForm): read every field, fill values, flatten the result, or create new
fields.

## Short answer

`doc.Form` is the document's form. Look a field up by its name, cast it to its kind and set the
value: the field's appearance is redrawn at once. `Flatten` turns the values into plain page
content.

```csharp sample="forms-fill.cs" region="fill"
```

## Reading fields

```csharp sample="forms-fill.cs" region="read"
```

`Fields` lists every field with its fully qualified name (`address.city`), and `Form["name"]` finds
one. `Form.Exists` tells whether the document has a form at all. Each field has a `Kind`, a `Value`
as text, a `ToolTip`, `ReadOnly` and `Required` flags, and its `Widgets`: the visible parts, each
with its `Page` and `Rectangle`. Attributes inherited from parent fields and the form defaults are
resolved.

| Field type | Kind | Value and members |
|---|---|---|
| `PdfTextField` | `Text` | `Text`; `Multiline`, `Password`, `Comb`, `MaxLength` |
| `PdfCheckBoxField` | `CheckBox` | `Checked`; `OnValue`, often "Yes" or "On" |
| `PdfRadioButtonField` | `RadioButton` | `Selected`; `Options` lists each button's value |
| `PdfChoiceField` | `ComboBox` or `ListBox` | `Select(value)`, `Select(values)`, `SelectedValues`, `Options`; `Editable`, `MultiSelect` |
| `PdfPushButtonField` | `PushButton` | No value |
| `PdfSignatureField` | `Signature` | `IsSigned` |
| `PdfUnknownField` | `Unknown` | A field of a type the library does not know |

## Filling and appearances

Setting `Text`, `Checked`, `Selected` or calling `Select` regenerates the field's appearance from
its border, background, font and color settings, with multiline wrapping, comb cells, password
masking and automatic font size. A font the form embeds whole is reused; other fonts map to the
closest standard font. Documents that also carry an XFA form lose it when a value changes, so
viewers show the AcroForm values.

## Flattening

```csharp sample="forms-fill.cs" region="flatten"
```

`Flatten` draws every field's current appearance into its page and removes the form. The values
stay visible and printable but can no longer be edited. Hidden fields are dropped.

## Creating fields

Positions are points from the page's top left corner, as for drawing: x, y, width, height.

```csharp sample="forms-create.cs" region="create"
```

![A registration form with text fields, a check box, radio buttons, a combo box, a list box and a signature field](/previews/forms.webp)

| Method | Creates |
|---|---|
| `AddTextField` | A text box, with an optional initial value |
| `AddCheckBox` | A check box, checked or not |
| `AddRadioGroup` | Radio buttons sharing one value; buttons may sit on different pages |
| `AddComboBox` | A drop down list |
| `AddListBox` | A scrolling list, with several selections when `MultiSelect` is set |
| `AddSignatureField` | An empty signature field, to be signed later by this library or a viewer |
| `AddPushButton` | A button with a caption, opening a web address when one is given |

Each new field comes with its appearance and the print flag. Field names must be unique in the
form and contain no dots.

### Field options

`PdfFieldOptions` sets how a new field looks and behaves. Options a field kind has no use for are
ignored.

| Option | Default | Notes |
|---|---|---|
| `Font` | Helvetica | One of the standard fonts, which every viewer has to type with |
| `FontSize` | 0 | 0 fits the text to the field |
| `TextColor` | black | |
| `BorderColor`, `BorderWidth` | gray, 1 | Null color for no border |
| `BackgroundColor` | white | Null for transparent |
| `ToolTip` | null | Also read by screen readers |
| `ReadOnly`, `Required` | false | |
| `Alignment` | `Left` | Text fields |
| `Multiline`, `Password` | false | Text fields |
| `MaxLength`, `Comb` | 0, false | Text fields: length limit, and one character per equal cell |
| `Editable` | false | Combo boxes accept text outside the options |
| `MultiSelect` | false | List boxes accept several selections |

To sign a document, see [Signatures](/guides/signatures/).

## Limits

- XFA forms are not supported: they are dropped when values change.
- Importing fields together with their pages, and JavaScript formatting actions, are on the
  [roadmap](/roadmap/).
- New fields use the standard fonts, so their text is limited to what those fonts cover.

## Related

- [Annotations](/guides/annotations/)
- [Signatures](/guides/signatures/)
- [Coordinates and units](/concepts/coordinates-and-units/)
- API: [PdfAcroForm](/api/pdfnetstack/pdfacroform/), [PdfField](/api/pdfnetstack/pdffield/), [PdfFieldOptions](/api/pdfnetstack/pdffieldoptions/), [PdfTextField](/api/pdfnetstack/pdftextfield/), [PdfChoiceField](/api/pdfnetstack/pdfchoicefield/)
