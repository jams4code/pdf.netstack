// /llms.txt: what PdfNetStack is, and a link to the Markdown version of every page.

import type { APIRoute } from 'astro';
import { getPages, pageId } from '../lib/markdown';
import { markdownPath } from '../lib/paths.mjs';
import { NUGET, REPO, SITE } from '../site.config.mjs';

const SECTIONS: [string, (id: string) => boolean][] = [
  ['Start', (id) => id === '' || id === 'getting-started'],
  ['Guides', (id) => id.startsWith('guides/')],
  ['Recipes', (id) => id.startsWith('recipes/') || id === 'recipes'],
  ['Command line', (id) => id === 'cli'],
  ['Concepts', (id) => id.startsWith('concepts/')],
  ['Questions and project', (id) => ['faq', 'about', 'benchmarks'].includes(id)],
  ['Comparisons', (id) => id.startsWith('compare')],
];

export const GET: APIRoute = async () => {
  const pages = await getPages();
  const lines = [
    '# PdfNetStack',
    '',
    '> PdfNetStack is an MIT licensed PDF library and command line tool for .NET, written in pure C# by Jamal Abdelkhalek at JADEV. It compresses PDFs losslessly and proves every output equivalent to its source, edits, merges, splits and encrypts documents, and, from version 1.1, draws pages, embeds fonts, fills and creates forms, signs and validates (PAdES B-B to B-LTA), writes PDF/A and Factur-X invoices, extracts text and images, downsamples images on request, and converts HTML and CSS to PDF without a browser. No native binaries, no dependencies on .NET 8 and later.',
    '',
    `- NuGet: \`PdfNetStack\` and \`PdfNetStack.Tool\` 1.0.0 (${NUGET}). Version 1.1 features are in the main branch and marked as not yet on NuGet.`,
    `- Source: ${REPO}`,
    '- Not related to the PDFNet SDK of Apryse.',
    '',
  ];
  for (const [label, test] of SECTIONS) {
    const items = pages.filter((p) => test(pageId(p)));
    if (!items.length) continue;
    lines.push(`## ${label}`, '');
    for (const p of items) {
      lines.push(`- [${p.data.title}](${new URL(markdownPath(pageId(p)), SITE).href}): ${p.data.description}`);
    }
    lines.push('');
  }
  lines.push(
    '## Optional',
    '',
    `- [API reference](${new URL(markdownPath('api'), SITE).href}): one page per public type, generated from the XML documentation`,
    `- [Changelog](${new URL(markdownPath('changelog'), SITE).href})`,
    `- [Roadmap](${new URL(markdownPath('roadmap'), SITE).href})`,
    `- [Complete documentation in one file](${SITE}/llms-full.txt)`,
    `- [Short answers in one file](${SITE}/llms-small.txt)`,
    '',
  );
  return new Response(lines.join('\n'), { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
};
