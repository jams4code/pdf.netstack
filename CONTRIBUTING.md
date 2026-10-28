# Contributing to PdfNetStack

Thank you for helping. This page tells you what the project is for, what we accept, how to work
on the code, and the rules that keep a PDF library trustworthy.

## What the project is for

PdfNetStack gives .NET developers a PDF toolkit they can use anywhere without license worries:

- **MIT all the way down.** The core package has no dependencies on modern .NET, and every
  dependency of every package is MIT licensed and checked in CI.
- **Safe on untrusted input.** Hostile files cannot crash the process, exhaust memory beyond the
  configured limits, or hang it. Bad input is refused, never half processed.
- **Honest about changes.** Lossless operations are proven lossless. Anything that is not lossless
  is opt-in and documented as such.
- **Broad reach.** One code base for .NET Framework 4.6.2 and later, Mono, Unity and modern .NET.

The long term goal is feature parity with the PdfSharp family under MIT. The roadmap is in
[docs/ROADMAP.md](docs/ROADMAP.md).

Out of scope: rendering PDFs to images, OCR, and wrappers around native or external tools.

## Ways to help

- Report a bug, especially a file that is handled wrongly. See "Reporting bugs" below.
- Pick an issue labelled `good first issue` or `help wanted`.
- Improve documentation and examples.
- Work on a roadmap item. Open an issue first so we can agree on the approach before you write
  a lot of code.

## Ground rules

1. **Lossless stays lossless.** A change that can alter rendering, text, structure or metadata
   must be opt-in, off by default, and documented as not lossless.
2. **The verifier is the contract.** Never weaken `EquivalenceVerifier` to make a new pass pass.
   If a pass needs a new accepted difference, argue for it in the pull request.
3. **No new runtime dependencies in `PdfNetStack`.** On .NET 8 and later the core depends on the
   base class library only; on .NET Standard 2.0 only on Microsoft's `System.Memory` polyfill.
   Other packages may add MIT licensed dependencies after a license review; add them to
   `eng/license-allowlist.json` in the same pull request with the license you verified.
4. **No native code and no external processes** in shipped packages.
5. **No real world documents in the repository.** Build test fixtures in code with `PdfBuilder`,
   so their content and license are clear. Never commit customer or personal files. The one
   exception is `tests/PdfNetStack.Tests/Fixtures/Jpeg`: synthetic test cards written by
   libjpeg-turbo, because an encoder other than ours is the point of those tests. Regenerate them
   with `python tools/make_jpeg_fixtures.py`; never add photographs.
6. **Every target framework stays green.** Code in `src/PdfNetStack` must compile for
   `netstandard2.0`. Use the helpers in `src/PdfNetStack/Compat` instead of APIs that only exist
   on modern .NET.

## Development setup

You need the .NET 10 SDK. Tests also run on .NET 8, and on Windows on .NET Framework 4.8.

```bash
git clone https://github.com/jams4code/pdf.netstack
cd pdf.netstack
dotnet build PdfNetStack.sln -c Release
dotnet test PdfNetStack.sln -c Release
pwsh eng/check-licenses.ps1
```

### Checking against real documents

Real PDFs never enter the repository, but you should run your change against the documents you
have. Point the opt-in tests at a folder:

```bash
PDFNETSTACK_CORPUS=/path/to/pdfs dotnet test -c Release --filter "FullyQualifiedName~Corpus"
```

For changes that affect output bytes, also compare renders with PDFium (development only):

```bash
pip install pypdfium2 numpy
dotnet run --project src/PdfNetStack.Cli -c Release -- batch /path/to/pdfs ./out
python tools/render_compare.py /path/to/pdfs ./out
```

## Coding standards

- Nullable reference types on, analyzers on, warnings are errors.
- Public APIs carry XML documentation. Explain why, not what the name already says.
- Prefer small, sealed, internal types. Public surface grows only on purpose.
- Bound everything that reads untrusted input: nesting depth, sizes, loop counts.
- Match the style of the surrounding code.

## Tests

Every change needs tests that fail without it. Depending on the change:

- unit tests next to the area you touched (`SyntaxTests`, `ReaderTests`, `FilterTests`,
  `OptimizerTests`, `DocumentTests`, `SecurityTests`),
- a regression test in `HostileInputTests` for any crash, hang or memory problem,
- mutation fuzzing stays green (`RobustnessTests`; set `PDFNETSTACK_FUZZ_ITERATIONS` for a long run).

## Commits and pull requests

- One topic per pull request. Keep it reviewable.
- Commit messages: a short prefix and a summary, for example `fix: bound outline recursion`.
  Prefixes: `feat`, `fix`, `perf`, `docs`, `test`, `refactor`, `build`, `chore`.
- CI must pass on Linux, Windows and macOS.
- For output changes, paste the summary line of `tools/render_compare.py`.
- For performance changes, include before and after numbers and the machine you used.
- Pull requests are squash merged by a maintainer after review.

## AI-assisted contributions

AI tools are welcome as tools. The person who opens the pull request remains the author and is
accountable for every line.

- **Disclose it.** Tick the AI box in the pull request template and say which tool you used and
  for what (for example "generated the first draft of the LZW tests").
- **Understand it.** You must be able to explain any line in review without asking the tool.
  Pull requests whose author cannot answer questions about them will be closed.
- **Run it.** Test results, benchmarks and render comparisons in a pull request must come from
  real runs on your machine or CI, never from a model's description of what would happen.
- **Keep it small.** Large, sweeping, machine-written changes are hard to review and will be
  asked to split or closed. Open an issue first.
- **Verify reports.** Do not file issues or security reports produced by a tool without
  reproducing them. Include the file or steps that show the problem.
- **Respect licenses.** Do not submit code a tool reproduced from other projects unless you have
  confirmed its license is compatible with MIT and kept the attribution.

The same standard applies to everyone: correct, tested, reviewed code that you understand.

## Reporting bugs

Include the `pdfnetstack inspect` output for the file, the options used, and the `Reason` from
the result. If you can share the file, attach it or a minimized version. Security problems go
through private reporting; see [SECURITY.md](SECURITY.md).

## License of contributions

By submitting a contribution you agree that it is licensed under the MIT license of this
repository, the same terms under which you received the code.

## Conduct

Be respectful and constructive. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Maintainers

PdfNetStack is created and maintained by Jamal Abdelkhalek at JADEV.
