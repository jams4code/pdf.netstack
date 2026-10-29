---
title: Low level PDF objects in C#
description: Read and change any part of a PDF from C# with PdfDictionary, PdfArray, PdfStream and friends, for what the high level API does not cover. MIT.
since: '1.1'
sidebar:
  label: Low level objects
---

Reach every dictionary, array and stream of a PDF when the high level API does not cover what you
need.

## Short answer

`doc.Catalog` is the root of the object graph and `page.Dictionary` is a page's own dictionary.
`doc.Resolve` follows references, `doc.AddObject` adds new indirect objects, and
`doc.GetStreamData` decodes a stream. Changes are written by the next `Save`.

```csharp sample="objects-catalog.cs" region="catalog"
```

## The object types

The types live in the `PdfNetStack.Objects` namespace and mirror ISO 32000-1, section 7.3.

| Type | Holds | Create with |
|---|---|---|
| `PdfNull` | The null object | `PdfNull.Instance` |
| `PdfBoolean` | true or false | `PdfBoolean.True`, `PdfBoolean.Get(value)` |
| `PdfNumber` | An integer or a real, kept as canonical text so values read from a file survive a round trip exactly | `PdfNumber.FromInteger(42)`, `PdfNumber.FromDouble(0.5)` |
| `PdfName` | A name such as `/Type`, without the slash in `Value` | `new PdfName("Type")` |
| `PdfString` | Bytes, which may be text | `PdfString.FromText("Hello")`; read with `ToText()` |
| `PdfArray` | An ordered list | `new PdfArray()`, `PdfArray.Of(...)` |
| `PdfDictionary` | Keys without the slash, in insertion order | `new PdfDictionary { ["Type"] = new PdfName("Annot") }` |
| `PdfStream` | A dictionary plus encoded data | `PdfStream.FromData(bytes)`, Flate compressed unless `compress: false` |
| `PdfReference` | A reference to an indirect object of one document | Returned by `doc.AddObject` or found in the document |

A dictionary's indexer returns null for a missing key, and setting null removes the entry.
`GetName(key)` returns a name value without its slash.

## Read

Values in dictionaries and arrays may be references. Pass them through `doc.Resolve`, which returns
the object itself when it is not a reference, and `PdfNull.Instance` for a reference to a missing
object.

```csharp sample="objects-catalog.cs" region="read"
```

`GetStreamData` undoes the Flate, LZW, ASCIIHex, ASCII85 and RunLength filters and the PNG and
TIFF predictors. Image codecs (DCTDecode, JPXDecode, JBIG2Decode, CCITTFaxDecode) are left in
place, so an image stream comes back as its JPEG or JPEG 2000 file; for pixels, use
[`PdfPageImage.Decode`](/guides/extract-images/). Damaged data, or data over the document's decode
limit, raises `PdfFormatException`.

## Write

Streams are always indirect objects: add them with `doc.AddObject` and store the returned
reference. An added object is written on save only if something references it.

```csharp sample="objects-catalog.cs" region="write"
```

The square in this sample is placed in PDF default user space, from the bottom left of the page.
The [drawing canvas](/guides/drawing/) does the same job with top left coordinates and keeps track
of resources for you; see [Coordinates and units](/concepts/coordinates-and-units/).

## What is already on the object

`page.Dictionary` already carries the inherited attributes (resources, boxes, rotation), copied
onto each page when the document opens. `doc.Catalog` is the document catalog, where viewer
settings, page labels, named destinations, outlines and the form live. Most of these have a high
level API too: `doc.Viewer`, `doc.PageLabels`, `doc.NamedDestinations`, `doc.Outlines` and
`doc.Form`.

## Limits

- References belong to one document. Use `Pages.Import` to move pages between documents rather
  than copying references.
- Saving rebuilds the page tree as one flat node and writes only objects reachable from the
  catalog, the information dictionary and the document ID.
- The object model does not check what you write against the specification. A dictionary with a
  wrong key is saved as is.

## Related

- [Edit pages](/guides/edit-pages/): the high level editing API
- [Drawing](/guides/drawing/): content streams without writing operators by hand
- [Architecture](/concepts/architecture/): how the parser and writer use these types
- API: [PdfDictionary](/api/pdfnetstack-objects/pdfdictionary/), [PdfArray](/api/pdfnetstack-objects/pdfarray/), [PdfStream](/api/pdfnetstack-objects/pdfstream/), [PdfReference](/api/pdfnetstack-objects/pdfreference/), [PdfDocument](/api/pdfnetstack/pdfdocument/)
