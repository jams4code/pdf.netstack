# PdfNetStack documentation site

The site at https://www.jadev-corp.com/pdfnetstack/, built with Astro Starlight from this folder. It is
static HTML: nothing runs on a server, and the only scripts on a page are Starlight's own, the
copy buttons, and Pagefind once search opens.

## Build it

You need Node 22 or later, the .NET 10 SDK (with .NET 8), and Python 3 for the sample inputs and
previews.

```bash
dotnet build ../PdfNetStack.sln -c Release      # the library the samples and API pages use
npm ci
pip install -r scripts/requirements.txt
node scripts/run-samples.mjs                    # compile and run every sample
python scripts/previews.py                      # render the preview images from their output
npm run build                                   # API reference, site, links and head checks
npx astro preview                               # serve dist/ on http://localhost:4321
```

`npm run build` runs `scripts/prepare.mjs` first: it syncs the repository docs, captures
`pdfnetstack --help`, runs DocFX and turns its output into the API pages, and lints names and
dashes. Set `SKIP_DOCFX=1` to reuse the DocFX output of an earlier run. `npm run dev` serves the
site with live reload; draft pages show there and nowhere else.

## Layout

| Path | What it holds |
|---|---|
| `src/content/docs/` | The pages, in Markdown. The folder structure is the URL structure |
| `src/content/docs/compare/` | Comparison pages, `draft: true` until the owner signs them off |
| `samples/` | Every C# sample, one .NET 10 file-based app each |
| `scripts/` | Build steps: docs sync, API pages, samples runner, previews, checks |
| `src/site.config.mjs` | Site address, base path, pre-launch switch, what is on NuGet |
| `src/route-data.ts` | Per page head tags: JSON-LD, Open Graph image, Markdown twin link |
| `src/pages/` | The `.md` twin of every page, `llms*.txt`, `robots.txt`, Open Graph images |
| `public/` | Files served as is: `_headers` for Cloudflare Pages, favicon, sample previews |
| `docfx.json` | DocFX metadata settings for the API reference |

Generated, never edited by hand (and ignored by git): `src/content/docs/api/`, `changelog.md`,
`roadmap.md`, `concepts/fidelity.md` and `concepts/architecture.md` (copied from the repository
at every build), and `src/generated/`.

## Writing a page

- Frontmatter: `title` of 3 to 46 characters (the site adds " | PdfNetStack"), `description` of 50
  to 155 characters, `since: '1.0'`, `'1.1'` or `'unreleased'` for pages about one feature, and
  optionally `faq` (questions and answers, shown at the end of the page and emitted as FAQPage
  JSON-LD). The build fails on missing or overlong values.
- Guides open with a one line lede and a `## Short answer`, and end with `## Limits` and
  `## Related`. Recipes are short: one question, one sample.
- A page for a version that is not on NuGet gets its badge and a "build from source" note
  automatically. Do not write them by hand.
- Links are root relative with a trailing slash: `/guides/compress-pdf/`. A broken internal link
  fails the build.
- `scripts/check-names.mjs` fails on the em dash and the en dash, and on any spelling of
  PdfNetStack or JADEV other than those two.
- Every claim must be backed by the repository: the code, the tests, the README or the changelog.

## Samples

Every C# block on the site comes from a file in `samples/`:

````md
```csharp sample="compress.cs" region="short"
```
````

Without `region`, the page shows the whole file. A sample file looks like this:

```csharp
#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
// preview: name output.pdf 0.4        (optional: render page 1 for the page, top 40%)
// run: build-only <reason>            (optional: compile, do not run)
using PdfNetStack;

#region short
...
#endregion
```

- `#:project` builds against this commit. Readers see `#:package PdfNetStack@<version>` instead
  when the sample's `since` version and its package are on NuGet (`RELEASE` in
  `src/site.config.mjs`), else a `#:project` line for a clone of the repository.
- The `since`, `preview` and `run` lines and the region markers never show on the site.
- `scripts/run-samples.mjs` runs each sample in its own folder under `samples/work`, seeded with
  the inputs `scripts/make_sample_inputs.py` and `samples/_inputs.cs` write (synthetic PDFs,
  images and open fonts) and the test suite's synthetic variable font. Nothing is third party.
  A sample that fails to build, exits non-zero or writes a PDF that does not open again fails the
  run. `SAMPLES_WORK=<dir>` uses another work
  folder; pass file names to run only those samples.
- A page shows a preview with `![alt text](/previews/<name>.webp)`. `scripts/previews.py` renders
  them with PDFium; the images are committed so a local build works without running the samples,
  and CI renders them again before every build.

## Checks

| Check | Where | Fails the build when |
|---|---|---|
| Samples | `scripts/run-samples.mjs` | a sample does not build, fails, or writes a broken PDF |
| Content schema | `src/content.config.ts` | a title or description is missing or too long |
| Names and dashes | `scripts/check-names.mjs` | an em dash, an en dash, or a misspelled name |
| Links | starlight-links-validator | an internal link or anchor does not exist |
| Head tags and weight | `scripts/check-dist.mjs` | a page lacks its canonical, description, Open Graph image, JSON-LD or Markdown twin; a key page exceeds 120 KB or 25 KB of script, compressed |
| npm licenses | `scripts/check-licenses.mjs` | a package is not on the allow list |
| Lighthouse | `lighthouserc.json` | performance, accessibility or best practices under 95, LCP over 2 s, CLS over 0.02 on five key pages |

The SEO category is a warning before launch, because every page carries `noindex` until then.

## Releases

At each release:

1. Update `RELEASE` in `src/site.config.mjs`: add the version to `published` and the package
   versions. Samples then show `#:package` lines, and the "not yet on NuGet" badges and notes go
   away by themselves.
2. Check the changelog page and the version numbers on the landing page and in the FAQ.

## Hosting

The site lives at `https://www.jadev-corp.com/pdfnetstack/`, so every page and every link to the
docs counts for the company domain:

- The build uses the base path `/pdfnetstack` (`src/site.config.mjs`). Links, canonicals, the
  sitemap, Markdown twins and previews follow it. The files sit at the root of `dist/`.
- The `Docs` workflow deploys `dist/` to the root of the Cloudflare Pages project `pdfnetstack`.
- The company site (Next.js on Vercel, repository `jadev-landing`) proxies `/pdfnetstack/*` to
  `https://pdfnetstack.pages.dev/*` and lets these paths keep their trailing slash.
- `public/_redirects` also serves every file under `/pdfnetstack/` on Pages itself, so pull
  request previews work with the base path: open `https://pr-<n>.pdfnetstack.pages.dev/pdfnetstack/`.
- Canonical URLs point at jadev-corp.com. The production pages.dev host cannot send `noindex`,
  because the proxy would pass the header on to jadev-corp.com; previews do send it.
- `robots.txt` and `llms.txt` at the root of jadev-corp.com list the docs sitemap and llms.txt.

The deploy runs once the `docs` environment holds `CLOUDFLARE_API_TOKEN` and
`CLOUDFLARE_ACCOUNT_ID`; pull requests from this repository deploy a preview when the
`docs-preview` environment holds them too. To set it up:

1. In Cloudflare, create the Pages project `pdfnetstack` (direct upload). No custom domain.
2. Create an API token with only the Account > Cloudflare Pages > Edit permission, for this
   account. Nothing else.
3. In the GitHub repository settings, create the `docs` environment, limit it to the `main`
   branch, and add the two secrets. Add them to `docs-preview` for pull request previews.

The deploy action is pinned to a commit; Dependabot proposes updates to it.

`DOCS_BASE='' npm run build` builds for the root of a host instead.

Lighthouse keeps the SEO category as a warning: it runs on localhost, while every canonical URL
points at jadev-corp.com, which its canonical audit reports as another domain.
