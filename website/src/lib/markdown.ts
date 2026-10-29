// Markdown versions of the docs pages, for the .md twins and the llms files.

import { getCollection, type CollectionEntry } from 'astro:content';
import { expandSamplesInMarkdown } from './samples.mjs';
import { markdownPath, pageUrl } from './paths.mjs';
import { RELEASE, SITE } from '../site.config.mjs';

type Entry = CollectionEntry<'docs'>;

/** Reading order for the llms files: earlier prefixes first, then alphabetical. */
const ORDER = [
  'index',
  'getting-started',
  'guides/compress-pdf',
  'guides/edit-pages',
  'guides/large-files',
  'guides/drawing',
  'guides/fonts-and-text',
  'guides/images',
  'guides/forms',
  'guides/annotations',
  'guides/signatures',
  'guides/pdf-a',
  'guides/encryption',
  'guides/extract-text',
  'guides/extract-images',
  'guides/ocr',
  'guides/html-to-pdf',
  'guides/low-level-objects',
  'recipes',
  'cli',
  'concepts',
  'faq',
  'benchmarks',
  'about',
  'compare',
  'roadmap',
  'changelog',
  'api',
];

function rank(id: string) {
  const i = ORDER.findIndex((p) => id === p || id.startsWith(p + '/'));
  return i < 0 ? ORDER.length : i;
}

/** Published docs pages in reading order. */
export async function getPages(): Promise<Entry[]> {
  const pages = await getCollection('docs', (e) => e.id !== '404' && (import.meta.env.DEV || !e.data.draft));
  return pages.sort((a, b) => rank(a.id) - rank(b.id) || a.id.localeCompare(b.id));
}

export function pageId(entry: Entry) {
  return entry.id === 'index' ? '' : entry.id;
}

const ASIDE_LABEL: Record<string, string> = {
  note: 'Note',
  tip: 'Tip',
  caution: 'Caution',
  danger: 'Danger',
};

/** Turns a page's source into plain Markdown: samples inlined, MDX and directives removed. */
export function toMarkdown(entry: Entry): string {
  let body = entry.body ?? '';
  body = expandSamplesInMarkdown(body);
  // MDX: imports and the few layout components the landing page uses.
  body = body
    .replace(/^import .*$/gm, '')
    .replace(
      /<LinkCard\s+title="([^"]+)"\s+href="([^"]+)"(?:\s+description="([^"]+)")?[^>]*\/>/g,
      (_, title: string, href: string, text?: string) => `- [${title}](${href})${text ? `: ${text}` : ''}`,
    )
    .replace(/<Card\s+title="([^"]+)"[^>]*>/g, '### $1\n')
    .replace(/<TabItem\s+label="([^"]+)"[^>]*>/g, '**$1**\n')
    .replace(/<\/?(CardGrid|Card|Tabs|TabItem|LinkButton)\b[^>]*>/g, '')
    // The landing page's figures: <div><strong>n</strong> <span>what</span></div>.
    .replace(/^\s*<div><strong>(.*?)<\/strong>\s*<span>(.*?)<\/span><\/div>\s*$/gm, '- **$1** $2')
    .replace(/^\s*<\/?div\b[^>]*>\s*$/gm, '');
  // Starlight asides become block quotes.
  body = body.replace(
    /^:::(note|tip|caution|danger)(?:\[([^\]]*)\])?\n([\s\S]*?)^:::\s*$/gm,
    (_, kind: string, title: string | undefined, text: string) =>
      text
        .trim()
        .split('\n')
        .map((line, i) => `> ${i === 0 ? `**${title || ASIDE_LABEL[kind]}:** ` : ''}${line}`)
        .join('\n'),
  );
  // Site links become absolute, so the text stands on its own.
  body = body.replace(/\]\(\/(?!\/)/g, `](${SITE}/`);
  const { title, description, faq, since } = entry.data;
  const parts = [`# ${entry.data.hero?.title ?? title}`, `> ${description}`];
  if (since && !RELEASE.published.includes(since)) {
    parts.push(
      since === 'unreleased'
        ? 'Status: not on NuGet yet.'
        : `Available since ${since}, which is not on NuGet yet.`,
    );
  } else if (since) {
    parts.push(`Available since ${since}.`);
  }
  parts.push(body.replace(/\n{3,}/g, '\n\n').trim());
  if (faq?.length) {
    parts.push(faq.map(({ q, a }) => `## ${q}\n\n${a}`).join('\n\n'));
  }
  parts.push(`Source: ${pageUrl(pageId(entry))}`);
  return parts.filter(Boolean).join('\n\n') + '\n';
}

/** The "Short answer" section of a guide or recipe, or the description when it has none. */
export function shortAnswer(entry: Entry): string {
  const md = toMarkdown(entry);
  const match = /^## Short answer\n([\s\S]*?)(?=^## |^Source: )/m.exec(md);
  return match ? match[1].trim() : (entry.data.description ?? '');
}

export { markdownPath };
