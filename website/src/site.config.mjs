// Site wide constants. Read by astro.config.mjs, the build scripts and the page components.

/** Production origin. Canonical URLs, the sitemap and JSON-LD use it. DOCS_SITE overrides it. */
export const SITE = process.env.DOCS_SITE ?? 'https://www.jadev-corp.com';

/**
 * Base path. The site is served at https://www.jadev-corp.com/pdfnetstack/: Cloudflare Pages hosts
 * the files at its root, and the company site (Vercel) proxies /pdfnetstack/* to it, so every page
 * and every link to the docs counts for the company domain. DOCS_BASE='' builds for a host root.
 */
export const BASE = process.env.DOCS_BASE ?? '/pdfnetstack';

/**
 * Pre-launch mode: every page carries a noindex robots meta tag and robots.txt disallows all
 * crawling. Off since the launch.
 */
export const PRELAUNCH = false;

export const REPO = 'https://github.com/jams4code/pdf.netstack';
export const NUGET = 'https://www.nuget.org/packages/PdfNetStack';
export const CORP = 'https://www.jadev-corp.com';
export const ORG_ID = 'https://www.jadev-corp.com/#organization';
export const PERSON_ID = 'https://www.jadev-corp.com/jamal#person';

/**
 * What is on NuGet today. Samples whose features are newer than the published version show how to
 * build against a clone of the repository instead of a `#:package` line. Update at each release.
 */
export const RELEASE = {
  /** Feature versions available from NuGet. */
  published: ['1.0', '1.1', '1.2'],
  /** Package versions on NuGet, by package id. Packages missing here are not on NuGet. */
  packages: {
    PdfNetStack: '1.2.0',
    'PdfNetStack.Html': '1.2.0',
    'PdfNetStack.Hyphenation': '1.2.0',
    'PdfNetStack.Ocr': '1.2.0',
    'PdfNetStack.Ocr.Models': '1.0.0',
    'PdfNetStack.Ocr.Tesseract': '1.2.0',
    'PdfNetStack.Ocr.Tool': '1.2.0',
    'PdfNetStack.Tool': '1.2.0',
  },
};
