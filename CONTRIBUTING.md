# Contributing

Thanks for helping. A few rules keep this project trustworthy.

## Ground rules

1. **Lossless stays lossless.** A change that can alter rendering, text, structure or metadata
   must be opt-in, off by default, and documented as not lossless.
2. **The verifier is the contract.** Never weaken `EquivalenceVerifier` to make a new pass pass.
   If a pass needs a new accepted difference, argue for it in the pull request.
3. **No new runtime dependencies in `PdfNetStack`.** The core package depends on the .NET base
   library only. Other packages may add MIT licensed dependencies after a license review; add
   them to `eng/license-allowlist.json` in the same pull request.
4. **No native code and no external processes** in shipped packages.
5. **No real world documents in the repository.** Build test fixtures in code with
   `PdfBuilder`, so their content and license are clear.

## Building

```bash
dotnet build PdfNetStack.sln -c Release
dotnet test PdfNetStack.sln -c Release
pwsh eng/check-licenses.ps1
```

Requires the .NET 10 SDK; tests run on .NET 8 and .NET 10.

## Pull requests

- One topic per pull request, with tests that fail without the change.
- For performance changes, include before and after numbers from `pdfnetstack batch` on a corpus
  you can describe, and the machine you ran on.
- For anything that touches output bytes, run `tools/render_compare.py` on a corpus and include
  the summary line.
- Keep public APIs documented with XML comments.

## Reporting bugs

Include the `pdfnetstack inspect` output for the file, the options used, and the `Reason` from the
result. If you can share the file, attach it or a minimized version.
