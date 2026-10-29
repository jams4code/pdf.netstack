// URL helpers shared by the route middleware, the endpoints and the components. Page ids are
// content collection ids without "index": "" for the landing page, "guides/compress-pdf" otherwise.

import { BASE, SITE } from '../site.config.mjs';

const base = BASE.replace(/\/$/, '');

/** Site relative path of a page, with the trailing slash the site uses. */
export function pagePath(id) {
  return id ? `${base}/${id}/` : `${base}/`;
}

/** Absolute URL of a page. */
export function pageUrl(id) {
  return new URL(pagePath(id), SITE).href;
}

/** Path of the Markdown twin of a page. */
export function markdownPath(id) {
  return `${base}/${id || 'index'}.md`;
}

/** Path of the Open Graph image of a page. */
export function ogImagePath(id) {
  return `${base}/og/${id || 'index'}.png`;
}
