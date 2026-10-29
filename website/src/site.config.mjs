// Site wide constants. Read by astro.config.mjs, the build scripts and the page components.

/** Production origin. Canonical URLs, the sitemap and JSON-LD use it. DOCS_SITE overrides it. */
export const SITE = process.env.DOCS_SITE ?? 'https://pdfnetstack.jadev-corp.com';

/**
 * Base path. Empty for the subdomain. Set DOCS_BASE=/pdfnetstack to serve the site from a
 * subpath of another host (see the website plan, appendix A).
 */
export const BASE = process.env.DOCS_BASE ?? '';

/**
 * Pre-launch mode: every page carries a noindex robots meta tag and robots.txt disallows all
 * crawling. Set to false in the launch task, once the custom domain is live.
 */
export const PRELAUNCH = true;

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
  published: ['1.0'],
  /** Package versions on NuGet, by package id. Packages missing here are not on NuGet. */
  packages: {
    PdfNetStack: '1.0.0',
    'PdfNetStack.Tool': '1.0.0',
  },
};
