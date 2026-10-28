## What and why

<!-- One topic. Link the issue if there is one: Fixes #123 -->

## Evidence

- [ ] Tests added or updated, and they fail without this change
- [ ] `dotnet test` passes (CI runs Linux, Windows and macOS; .NET Framework 4.8, .NET 8 and .NET 10)
- [ ] For output changes: `tools/render_compare.py` summary line pasted below
- [ ] For performance changes: before and after numbers and the machine used
- [ ] No new runtime dependency in `PdfNetStack`; any other new package is on the license allowlist
- [ ] Core code compiles for `netstandard2.0` (use `src/PdfNetStack/Compat` helpers)

## AI assistance

- [ ] No AI tools were used
- [ ] AI tools were used. Tool and purpose: <!-- e.g. "Copilot, first draft of test fixtures" -->

I understand every line of this change and can explain it in review. Results above come from real runs.
