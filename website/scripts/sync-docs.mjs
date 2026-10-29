// Copies the repository's own docs into the site at build time, so they are never edited twice:
// CHANGELOG.md, docs/ROADMAP.md, docs/FIDELITY.md and docs/ARCHITECTURE.md. Adds frontmatter and
// rewrites relative links to site pages or to the file on GitHub.

import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const website = fileURLToPath(new URL('..', import.meta.url));
const repo = join(website, '..');
const docs = join(website, 'src/content/docs');
const GITHUB = 'https://github.com/jams4code/pdf.netstack';

const SOURCES = [
  {
    from: 'CHANGELOG.md',
    to: 'changelog.md',
    title: 'Changelog',
    description: 'Every change to PdfNetStack by release, newest first: what is in main and not yet on NuGet, and what each published version added.',
  },
  {
    from: 'docs/ROADMAP.md',
    to: 'roadmap.md',
    title: 'Roadmap',
    description: 'What PdfNetStack has released, what the next release contains, what comes after it, and what is out of scope for the project.',
  },
  {
    from: 'docs/FIDELITY.md',
    to: 'concepts/fidelity.md',
    title: 'Fidelity and verification',
    description: 'How PdfNetStack proves an optimized PDF equivalent to its source: graph comparison, JPEG coefficient checks, render comparison and tests.',
  },
  {
    from: 'docs/ARCHITECTURE.md',
    to: 'concepts/architecture.md',
    title: 'Architecture',
    description: 'How the PdfNetStack optimizer is built: the pipeline from parsing to verification, the source layout, design decisions and threading.',
  },
];

/** Site page for each synced or well known repository file. */
const PAGE_FOR = {
  'CHANGELOG.md': '/changelog/',
  'docs/ROADMAP.md': '/roadmap/',
  'docs/FIDELITY.md': '/concepts/fidelity/',
  'docs/ARCHITECTURE.md': '/concepts/architecture/',
};

function lastCommitDate(file) {
  try {
    return execFileSync('git', ['log', '-1', '--format=%cI', '--', file], { cwd: repo, encoding: 'utf8' }).trim();
  } catch {
    return '';
  }
}

for (const doc of SOURCES) {
  let text = readFileSync(join(repo, doc.from), 'utf8').replace(/\r\n/g, '\n');
  text = text.replace(/^# .*\n+/, '');
  const dir = posix.dirname(doc.from);
  text = text.replace(/\]\((?!https?:|#|mailto:)([^)\s]+)\)/g, (_, link) => {
    const [path, hash = ''] = link.split('#');
    const resolved = posix.normalize(posix.join(dir, path));
    const page = PAGE_FOR[resolved];
    if (page) return `](${page}${hash ? '#' + hash : ''})`;
    return `](${GITHUB}/blob/main/${resolved}${hash ? '#' + hash : ''})`;
  });
  const date = lastCommitDate(doc.from);
  const front = [
    '---',
    `title: ${JSON.stringify(doc.title)}`,
    `description: ${JSON.stringify(doc.description)}`,
    `editUrl: ${GITHUB}/edit/main/${doc.from}`,
    ...(date ? [`lastUpdated: ${date}`] : []),
    '---',
    '',
    `:::note[Synced from the repository]`,
    `This page is generated from [${doc.from}](${GITHUB}/blob/main/${doc.from}) at every build.`,
    ':::',
    '',
  ].join('\n');
  const out = join(docs, doc.to);
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, front + text);
}
console.log(`sync-docs: ${SOURCES.length} pages`);
