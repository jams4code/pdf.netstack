# Architecture

PdfNetStack is a rewriting optimizer, not a PDF library. It understands enough of the file
format to rebuild the object graph exactly, and nothing about rendering. That narrow scope is
what keeps it small, fast and provable.

## Pipeline

```
input bytes
   |
   v
PdfFile.Open ............ header, xref chain (tables, streams, hybrid), object streams,
   |                      reconstruction for damaged files; stream data sliced, not copied
   v
Safety screen ........... encrypted, signed, incremental, damaged, too large -> return input
   |
   v
ObjectGraph.Collect ..... breadth first from /Root and /Info; everything else is garbage;
   |                      marks identity sensitive objects; optional stripping
   v
StreamRecompressor ...... parallel; decode compression layers, try candidates, keep smallest
   |
   v
Deduplicator ............ canonical serialization + SHA-256, merged to a fixpoint
   |
   v
PdfDocumentWriter ....... renumber, pack object streams, write xref stream
   |
   v
EquivalenceVerifier ..... re-open source and output, walk both graphs, compare decoded streams
   |
   v
output = verified rewrite, or input bytes
```

## Source layout

| Folder | Responsibility |
|---|---|
| `Objects/` | Object model. Numbers keep canonical text so reals never pass through floating point. Dictionaries keep insertion order for deterministic output. |
| `Parsing/` | Tokenizer, object parser, `PdfFile` (xref loading, lazy object resolution, object streams, reconstruction). |
| `Filters/` | Flate, LZW, ASCIIHex, ASCII85, RunLength decoders; PNG and TIFF predictors; pooled, size capped buffers. |
| `Optimization/` | Reachability, recompression, deduplication, verification. |
| `Writing/` | Token serializer with minimal separators; document writer for compact and classic layouts. |
| `PdfOptimizer.cs` | Public entry point and orchestration. Every exception path returns the input. |

## Design decisions

**Read everything into memory.** PDFs are random access by design and every pass touches most of
the file. A single buffer with `ReadOnlyMemory<byte>` slices is the simplest correct model and
the fastest one. Stream data is never copied until it is re-encoded.

**Numbers as canonical text.** `0.50` and `.5` are the same number, but `0.1 + 0.2` in floating
point is not `0.3`. Keeping the digits avoids any chance of drift and still lets the writer emit
the shortest form.

**Recompression decodes only compression layers.** Predictor transforms stay in place under the
new Flate layer, so a predicted image or xref stream is recompressed without reimplementing the
predictor on the encode side. Candidate encodings are compared by total cost, data plus the
`/Filter` and `/DecodeParms` entries they need.

**Fast trial, slow winner.** For images, plain and PNG predicted data are both compressed at the
fastest zlib level, and only the smaller one is compressed at the configured level. Streams are
scheduled largest first so one big image never runs alone at the end.

**Identity before equality.** Two equal annotation dictionaries are still two annotations. The
graph walk marks objects reached through identity keys (`/Parent`, `/Kids`, `/Annots`, `/P`,
`/Pg`, `/Obj`, outline links, form fields and more), and the deduplicator also skips types such
as pages, annotations and structure elements. Everything else is merged only when its canonical
serialization, with references mapped through the merge table, is identical.

**Pooled buffers for decoded data.** Decoders write into `ArrayPool` backed buffers that are
handed out as leases and returned once consumed. On a 72 MiB drawing catalogue this took peak
working set from 1.8 GB to under 700 MB.

**Verification is a separate parse.** The optimizer mutates its object model as it works, so the
verifier opens the original bytes again and compares two independent parses. A writer bug and a
reader bug would have to cancel out exactly to slip through, and the independent PDFium render
check in `tools/` covers that case.

## Threading

Parsing and graph work run on one thread. Stream recompression and stream comparison run in
parallel, after every object they need has been resolved, so workers never call back into the
parser. `PdfFile.GetObject` is still locked as a guard. `MaxDegreeOfParallelism` caps both
phases and is the main lever on peak memory.
