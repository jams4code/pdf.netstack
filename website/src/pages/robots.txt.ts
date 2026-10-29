// robots.txt. Before launch nothing is crawled; after, everything is, AI crawlers included.

import type { APIRoute } from 'astro';
import { PRELAUNCH, SITE, BASE } from '../site.config.mjs';

const AGENTS = ['*', 'GPTBot', 'ClaudeBot', 'PerplexityBot', 'Google-Extended', 'CCBot', 'Applebot-Extended'];

export const GET: APIRoute = () => {
  const rule = PRELAUNCH ? 'Disallow: /' : 'Allow: /';
  const groups = AGENTS.map((agent) => `User-agent: ${agent}\n${rule}`).join('\n\n');
  const sitemap = new URL(`${BASE}/sitemap-index.xml`, SITE).href;
  return new Response(`${groups}\n\nSitemap: ${sitemap}\n`, {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
