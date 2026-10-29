// The Markdown twin of every docs page, at <path>.md, with samples inlined.

import type { APIRoute, GetStaticPaths } from 'astro';
import { getPages, toMarkdown } from '../lib/markdown';

export const getStaticPaths: GetStaticPaths = async () =>
  (await getPages()).map((entry) => ({ params: { slug: entry.id }, props: { entry } }));

export const GET: APIRoute = ({ props }) =>
  new Response(toMarkdown(props.entry), {
    headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
  });
