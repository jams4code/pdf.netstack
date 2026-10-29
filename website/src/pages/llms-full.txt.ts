// /llms-full.txt: every page except the API reference, as Markdown, in reading order.

import type { APIRoute } from 'astro';
import { getPages, toMarkdown } from '../lib/markdown';

export const GET: APIRoute = async () => {
  const pages = (await getPages()).filter((p) => p.id !== 'api' && !p.id.startsWith('api/'));
  return new Response(pages.map(toMarkdown).join('\n---\n\n'), {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
