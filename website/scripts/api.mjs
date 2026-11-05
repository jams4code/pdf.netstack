// Turns the DocFX Markdown in obj/api into Starlight pages under src/content/docs/api: one page
// per namespace and per type, cross references rewritten to site URLs, and a "Used in" list of
// the guides whose text or samples mention the type. Also writes the API sidebar groups.
//
// Run `dotnet docfx metadata docfx.json` first (scripts/prepare.mjs does).

import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const source = join(root, 'obj/api');
const target = join(root, 'src/content/docs/api');
const docsDir = join(root, 'src/content/docs');
const samplesDir = join(root, 'samples');
const generated = join(root, 'src/generated');

/** Package that ships each namespace. */
const PACKAGE = {
  'PdfNetStack.Html': 'PdfNetStack.Html',
  'PdfNetStack.Hyphenation': 'PdfNetStack.Hyphenation',
  'PdfNetStack.Ocr': 'PdfNetStack.Ocr',
};
const NAMESPACE_ORDER = [
  'PdfNetStack',
  'PdfNetStack.Drawing',
  'PdfNetStack.Fonts',
  'PdfNetStack.Signing',
  'PdfNetStack.Objects',
  'PdfNetStack.Html',
  'PdfNetStack.Hyphenation',
  'PdfNetStack.Ocr',
];

const nsSlug = (ns) => ns.toLowerCase().replace(/\./g, '-');
const yaml = (s) => JSON.stringify(s);

/** Plain text of a DocFX summary: no tags, no Markdown links, one line. */
function plain(md) {
  return md
    .replace(/<xref href="([^"]+)"[^>]*><\/xref>/g, (_, uid) => shortName(decodeURIComponent(uid)))
    .replace(/<\/?[a-z][^>]*>/g, '')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/\\([()[\]<>*_`#.\-!])/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
}

/** Last name of a uid without its parameter list: "A.B.Method(X,Y)" becomes "Method". */
function shortName(uid) {
  const noParams = uid.replace(/\(.*\)$/, '');
  return noParams.slice(noParams.lastIndexOf('.') + 1).replace(/`\d+$/, '');
}

/** Description for search results: the summary, sized to 50 to 155 characters. */
function describe(name, kind, ns, summary) {
  let text = summary || `The ${name} ${kind} in ${ns}.`;
  if (text.length < 50) text = `${text.replace(/\.?$/, '.')} ${name} ${kind}, ${ns} namespace.`;
  if (text.length < 50) text = `${text} PdfNetStack API reference.`;
  if (text.length > 155) text = text.slice(0, 152).replace(/\s+\S*$/, '') + '...';
  return text;
}

if (!existsSync(source)) {
  console.error('obj/api is missing: run `dotnet docfx metadata docfx.json` first.');
  process.exit(1);
}

// Pass 1: classify every file and collect the anchors it defines.
const files = readdirSync(source).filter((f) => f.endsWith('.md'));
const pages = new Map(); // file name -> page info
const anchors = new Map(); // anchor id -> file name
for (const file of files) {
  const text = readFileSync(join(source, file), 'utf8').replace(/\r\n/g, '\n');
  const head = /^# <a id="[^"]+"><\/a> (Namespace|Class|Struct|Enum|Interface|Delegate) (.+)$/m.exec(text);
  if (!head) throw new Error(`Unexpected DocFX page: ${file}`);
  const kind = head[1].toLowerCase();
  const uid = file.slice(0, -3);
  const ns = kind === 'namespace' ? head[2] : /^Namespace: \[([^\]]+)\]/m.exec(text)?.[1];
  const name = kind === 'namespace' ? head[2] : head[2].replace(/\\/g, '');
  const path = kind === 'namespace' ? `/api/${nsSlug(ns)}/` : `/api/${nsSlug(ns)}/${shortName(uid).toLowerCase()}/`;
  pages.set(file, { file, uid, kind, ns, name, path, text });
  for (const m of text.matchAll(/<a id="([^"]+)"><\/a>/g)) anchors.set(m[1], file);
}

/** Site link for a DocFX uid, or null when it is outside the documented API. */
function resolveUid(uid) {
  const file = `${uid}.md`;
  if (pages.has(file)) return pages.get(file).path;
  const anchor = uid.replace(/[^A-Za-z0-9]/g, '_');
  if (anchors.has(anchor)) return `${pages.get(anchors.get(anchor)).path}#${anchor}`;
  // A member uid whose anchor DocFX shortened: link its type.
  const typeUid = uid.replace(/\(.*\)$/, '').replace(/\.[^.]+$/, '');
  if (pages.has(`${typeUid}.md`)) return pages.get(`${typeUid}.md`).path;
  return null;
}

// Which guides mention which types: page bodies plus the samples they include.
const guides = [];
function collectGuides(dir, prefix = '') {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const rel = prefix + entry.name;
    if (entry.isDirectory()) {
      if (rel !== 'api') collectGuides(join(dir, entry.name), rel + '/');
      continue;
    }
    if (!/\.mdx?$/.test(entry.name)) continue;
    const id = rel.replace(/\.mdx?$/, '').replace(/(^|\/)index$/, '');
    if (!/^(guides|recipes|getting-started|concepts\/(coordinates|memory))/.test(id)) continue;
    const body = readFileSync(join(dir, entry.name), 'utf8');
    if (/^draft: true$/m.test(body)) continue;
    const title = /^title: (.+)$/m.exec(body)?.[1].replace(/^["']|["']$/g, '') ?? id;
    let text = body;
    for (const m of body.matchAll(/sample="([\w-]+\.cs)"/g)) {
      const file = join(samplesDir, m[1]);
      if (existsSync(file)) text += readFileSync(file, 'utf8');
    }
    guides.push({ id, title, text });
  }
}
collectGuides(docsDir);
guides.sort((a, b) => a.id.localeCompare(b.id));

function usedIn(name) {
  const re = new RegExp(`\\b${name}\\b`);
  return guides.filter((g) => re.test(g.text));
}

/** Rewrites one DocFX page body for the site. */
function convert(page) {
  let body = page.text;
  body = body.replace(/^# .*\n+/, '');
  // Members inherited from object add nothing to these pages.
  body = body.replace(/^#### Inherited Members\n[\s\S]*?(?=^#{1,4} |(?![\s\S]))/m, '');
  // DocFX puts Inheritance, Implements and the like under the title as fourth level headings.
  // They are labels, not sections: a jump from the title to h4 breaks the heading order.
  const firstSection = body.search(/^## /m);
  const head = firstSection < 0 ? body : body.slice(0, firstSection);
  body = head.replace(/^#### (.+)$/gm, '**$1**') + body.slice(head.length);
  // Links between DocFX pages.
  body = body.replace(/\]\(([\w.`-]+)\.md(#[^)]*)?\)/g, (whole, uid, hash) => {
    const p = pages.get(`${uid}.md`);
    return p ? `](${p.path}${hash ?? ''})` : whole;
  });
  // Cross references in summaries and remarks.
  body = body.replace(/<xref href="([^"]+)"[^>]*><\/xref>/g, (_, raw) => {
    const uid = decodeURIComponent(raw);
    const link = resolveUid(uid);
    const name = shortName(uid);
    return link ? `[${name}](${link})` : `\`${name}\``;
  });
  // DocFX writes "PdfNetStack.dll" as the assembly; name the package too.
  if (page.kind !== 'namespace') {
    const pkg = PACKAGE[page.ns] ?? 'PdfNetStack';
    body = body.replace(/^Assembly: (.+?)[ \t]*$/m, `Assembly: $1  \nPackage: \`${pkg}\``);
  }
  // Generic type arguments such as IReadOnlyList<PdfPage\> are text, not HTML. Code blocks show
  // their text as is, so they are left alone.
  body = body
    .split(/(^```[\s\S]*?^```$)/m)
    .map((part, i) =>
      i % 2
        ? part
        : part.replace(/<(?!\/?(?:a|code|pre|xref|b|i|em|strong|br|p|ul|ol|li|table|tr|td|th|sup|sub)\b)(?=[A-Za-z])/g, '&lt;'),
    )
    .join('');
  return body.replace(/\n{3,}/g, '\n\n').trim();
}

rmSync(target, { recursive: true, force: true });
mkdirSync(target, { recursive: true });

const namespaces = [...pages.values()].filter((p) => p.kind === 'namespace');
namespaces.sort((a, b) => NAMESPACE_ORDER.indexOf(a.name) - NAMESPACE_ORDER.indexOf(b.name));

for (const page of pages.values()) {
  const dir = join(target, nsSlug(page.ns));
  mkdirSync(dir, { recursive: true });
  const summary = plain(
    /\n\n(?!Namespace:|Assembly:)([^#\n`|][\s\S]*?)\n\n/.exec(page.text.replace(/^# .*\n+/, ''))?.[1] ?? '',
  );
  let front;
  let body = convert(page);
  if (page.kind === 'namespace') {
    const pkg = PACKAGE[page.name] ?? 'PdfNetStack';
    front = {
      title: `${page.name} namespace`,
      description: describe(page.name, 'namespace', page.name, `Types in the ${page.name} namespace of the ${pkg} package: every class, struct and enum, with a one line summary.`),
      sidebar: { label: 'Overview', order: 0 },
    };
    body = `Package: \`${pkg}\`\n\n${body}`;
  } else {
    front = {
      title: `${page.name} ${page.kind}`,
      description: describe(page.name, page.kind, page.ns, summary),
      sidebar: { label: page.name },
    };
    const uses = usedIn(page.name);
    if (uses.length) {
      body += `\n\n## Used in\n\n${uses.map((g) => `- [${g.title}](/${g.id}/)`).join('\n')}`;
    }
  }
  if (front.title.length > 46) front.title = page.kind === 'namespace' ? page.name : page.name;
  const fm = [
    '---',
    `title: ${yaml(front.title)}`,
    `description: ${yaml(front.description)}`,
    `sidebar:`,
    `  label: ${yaml(front.sidebar.label)}`,
    ...(front.sidebar.order !== undefined ? [`  order: ${front.sidebar.order}`] : []),
    'editUrl: false',
    'lastUpdated: false',
    '---',
  ].join('\n');
  const name = page.kind === 'namespace' ? 'index.md' : `${shortName(page.uid).toLowerCase()}.md`;
  writeFileSync(join(dir, name), `${fm}\n\n${body}\n`);
}

// The API overview: namespaces with their package and type count.
const overview = [
  '---',
  'title: "API reference"',
  'description: "Every public type of every PdfNetStack package, generated from the XML documentation of the current source."',
  'editUrl: false',
  'lastUpdated: false',
  '---',
  '',
  'Generated from the XML documentation comments of the source this site was built from, so it',
  'matches the main branch. Types added after 1.0 are part of 1.1, which is not on NuGet yet; the',
  '[changelog](/changelog/) lists what is new.',
  '',
  '| Namespace | Package | Types |',
  '|---|---|---|',
  ...namespaces.map((ns) => {
    const count = [...pages.values()].filter((p) => p.ns === ns.name && p.kind !== 'namespace').length;
    return `| [${ns.name}](${ns.path}) | \`${PACKAGE[ns.name] ?? 'PdfNetStack'}\` | ${count} |`;
  }),
  '',
];
writeFileSync(join(target, 'index.md'), overview.join('\n'));

mkdirSync(generated, { recursive: true });
writeFileSync(
  join(generated, 'api-sidebar.json'),
  JSON.stringify(
    namespaces.map((ns) => ({
      label: ns.name,
      collapsed: true,
      items: [{ autogenerate: { directory: `api/${nsSlug(ns.name)}` } }],
    })),
    null,
    2,
  ),
);
console.log(`api: ${pages.size - namespaces.length} types in ${namespaces.length} namespaces`);
