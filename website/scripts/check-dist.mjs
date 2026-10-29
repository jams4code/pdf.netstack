// Checks the built site in dist/ after `astro build`: every page has the head tags search engines
// and link previews need, its Markdown twin and its Open Graph image, and the key pages stay
// within the weight budget of the website plan (section 5.7). Fails the build on any problem.

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';
import { BASE, SITE } from '../src/site.config.mjs';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const base = BASE.replace(/\/$/, '');

/** Budgets for a docs page on first load, compressed, images excluded. */
const BUDGET = { total: 120 * 1024, script: 25 * 1024 };
const BUDGET_PAGES = ['', 'getting-started', 'guides/compress-pdf', 'guides/html-to-pdf', 'api/pdfnetstack/pdfoptimizer'];

const problems = [];
const problem = (page, message) => problems.push(`${page}: ${message}`);

function* htmlFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'pagefind' && entry.name !== '_astro') yield* htmlFiles(path);
    } else if (entry.name === 'index.html') {
      yield path;
    }
  }
}

const attr = (html, re) => re.exec(html)?.[1];
const decode = (s) =>
  s.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");

/** Maps a site path such as /_astro/x.js to its file in dist. */
const fileOf = (url) => join(dist, decodeURI(url.slice(base.length)).replace(/^\//, ''));

let pages = 0;
for (const file of htmlFiles(dist)) {
  pages++;
  const id = relative(dist, file).split(sep).slice(0, -1).join('/');
  const label = `/${id}${id ? '/' : ''}`;
  const html = readFileSync(file, 'utf8');

  const title = decode(attr(html, /<title>([^<]*)<\/title>/) ?? '');
  if (!title) problem(label, 'no <title>');
  else if (title.length > 60) problem(label, `title is ${title.length} characters: ${title}`);

  const description = decode(attr(html, /<meta name="description" content="([^"]*)"/) ?? '');
  if (!description) problem(label, 'no meta description');
  else if (description.length > 155) problem(label, `description is ${description.length} characters`);

  const canonical = attr(html, /<link rel="canonical" href="([^"]+)"/);
  const expected = new URL(`${base}/${id}${id ? '/' : ''}`, SITE).href;
  if (canonical !== expected) problem(label, `canonical ${canonical} is not ${expected}`);

  const og = attr(html, /<meta property="og:image" content="([^"]+)"/);
  if (!og) problem(label, 'no og:image');
  else if (!existsSync(fileOf(new URL(og).pathname))) problem(label, `missing ${new URL(og).pathname}`);

  const md = attr(html, /<link rel="alternate" type="text\/markdown" href="([^"]+)"/);
  if (!md) problem(label, 'no Markdown alternate link');
  else if (!existsSync(fileOf(md))) problem(label, `missing ${md}`);

  const ld = attr(html, /<script type="application\/ld\+json">([\s\S]*?)<\/script>/);
  if (!ld) problem(label, 'no JSON-LD');
  else {
    try {
      const graph = JSON.parse(ld)['@graph'];
      if (!Array.isArray(graph) || !graph.length) problem(label, 'empty JSON-LD graph');
    } catch (error) {
      problem(label, `JSON-LD does not parse: ${error.message}`);
    }
  }

  if (/[–—]/.test(html.replace(/<pre[\s\S]*?<\/pre>/g, '').replace(/<script[\s\S]*?<\/script>/g, ''))) {
    problem(label, 'em or en dash in the page text');
  }
}

// Weight of the key pages on first load: the HTML, its style sheets and its module scripts.
// Pagefind loads only when search opens, so it is not counted.
const budgetRows = [];
for (const id of BUDGET_PAGES) {
  const file = join(dist, id, 'index.html');
  if (!existsSync(file)) {
    problem(`/${id}/`, 'budget page missing');
    continue;
  }
  const html = readFileSync(file, 'utf8');
  const gz = (buffer) => gzipSync(buffer, { level: 9 }).length;
  let total = gz(Buffer.from(html));
  let script = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)]
    .filter((m) => !/application\/ld\+json/.test(m[0]))
    .reduce((sum, m) => sum + gz(Buffer.from(m[1])), 0);
  const assets = new Set([
    ...[...html.matchAll(/<link rel="stylesheet" href="([^"]+)"/g)].map((m) => m[1]),
    ...[...html.matchAll(/<script type="module" src="([^"]+)"/g)].map((m) => m[1]),
  ]);
  for (const url of assets) {
    const path = fileOf(url);
    if (!existsSync(path)) {
      problem(`/${id}/`, `missing asset ${url}`);
      continue;
    }
    const size = gz(readFileSync(path));
    total += size;
    if (url.endsWith('.js')) script += size;
  }
  budgetRows.push(`  /${id}${id ? '/' : ''}: ${(total / 1024).toFixed(1)} KB, script ${(script / 1024).toFixed(1)} KB`);
  if (total > BUDGET.total) problem(`/${id}/`, `first load ${(total / 1024).toFixed(1)} KB over ${BUDGET.total / 1024} KB`);
  if (script > BUDGET.script) problem(`/${id}/`, `script ${(script / 1024).toFixed(1)} KB over ${BUDGET.script / 1024} KB`);
}

for (const file of ['robots.txt', 'sitemap-index.xml', 'llms.txt', 'llms-full.txt', 'llms-small.txt', '404.html']) {
  const path = join(dist, file);
  if (!existsSync(path) || statSync(path).size === 0) problem('/', `missing or empty ${file}`);
}

console.log(`check-dist: ${pages} pages, first load (gzip):\n${budgetRows.join('\n')}`);
if (problems.length) {
  console.error(problems.join('\n'));
  console.error(`check-dist: ${problems.length} problem(s)`);
  process.exit(1);
}
console.log('check-dist: ok');
