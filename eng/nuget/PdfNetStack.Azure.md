# PdfNetStack.Azure

Compress PDF files in Azure Blob Storage with [PdfNetStack](https://www.nuget.org/packages/PdfNetStack):
read a source blob, write the optimized PDF to another container under a content-addressed name,
with a JSON provenance sidecar. Managed identity only, no account keys. By Jamal Abdelkhalek at
[JADEV](https://github.com/jams4code). MIT licensed.

```csharp
using PdfNetStack.Azure;

var pipeline = new BlobPdfOptimizer(new Uri("https://account.blob.core.windows.net"));
BlobOptimizationOutcome outcome = await pipeline.OptimizeAsync("incoming", "docs/manual.pdf", "optimized");
Console.WriteLine($"{outcome.OutputBlobName}: {outcome.Result.Disposition}");
```

- Authenticates with `DefaultAzureCredential` (managed identity in Azure, developer login locally),
  or any `TokenCredential` you pass.
- The source blob is read at a pinned ETag and never modified.
- Output goes to `pdf/<sha256 prefix>/<sha256>.pdf` with a conditional write, so retries and
  concurrent workers are idempotent.
- The sidecar records source URI, ETag, both hashes, sizes, disposition, reason, engine version
  and timings.

Grant the identity Storage Blob Data Reader on the source container and Storage Blob Data
Contributor on the target container.

## Links

- [Documentation and source](https://github.com/jams4code/pdf.netstack)
- [Report an issue](https://github.com/jams4code/pdf.netstack/issues)
