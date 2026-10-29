// /llms-small.txt: the short answer of every guide and recipe, plus the start pages and the FAQ.

import type { APIRoute } from 'astro';
import { getPages, pageId, shortAnswer, toMarkdown } from '../lib/markdown';
import { pageUrl } from '../lib/paths.mjs';

const FULL = new Set(['', 'getting-started', 'faq']);

export const GET: APIRoute = async () => {
  const pages = (await getPages()).filter((p) => {
    const id = pageId(p);
    return FULL.has(id) || id.startsWith('guides/') || id.startsWith('recipes/') || id === 'cli';
  });
  const parts = pages.map((p) =>
    FULL.has(pageId(p))
      ? toMarkdown(p)
      : `# ${p.data.title}\n\n${shortAnswer(p)}\n\nMore: ${pageUrl(pageId(p))}\n`,
  );
  return new Response(parts.join('\n---\n\n'), {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
