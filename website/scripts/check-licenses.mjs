// Lists the license of every installed npm package and fails on one that is not on the allow
// list. Website packages run at build time only; what ships is the static output, plus the small
// scripts Starlight, Pagefind and Expressive Code add to pages (all MIT).

import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const modules = fileURLToPath(new URL('../node_modules', import.meta.url));

const PERMISSIVE = new Set([
  'MIT', 'ISC', 'BSD-2-Clause', 'BSD-3-Clause', 'Apache-2.0', '0BSD', 'CC0-1.0',
  'BlueOak-1.0.0', 'Python-2.0', 'Unlicense', 'CC-BY-4.0', 'MIT-0',
]);

// Reviewed exceptions: build tools used unmodified, never part of the published site.
const REVIEWED = {
  // CSS minifier inside Vite. MPL-2.0 is file level copyleft: it covers changes to its own files,
  // which we never make, and nothing of it ships.
  lightningcss: 'MPL-2.0',
  'lightningcss-': 'MPL-2.0',
  // Optional image library Astro installs. The site uses the passthrough image service, so it is
  // never called. Its prebuilt libvips binaries are LGPL-3.0, loaded dynamically, never shipped.
  '@img/': 'Apache-2.0 AND LGPL-3.0-or-later',
  // Open fonts that render the Open Graph images and feed the font samples. Not shipped as files.
  '@fontsource/': 'OFL-1.1',
};

function licenseOf(pkg) {
  const l = pkg.license ?? pkg.licenses?.map((x) => x.type ?? x).join(' OR ');
  return typeof l === 'object' ? l.type : (l ?? 'UNKNOWN');
}

function allowed(name, license) {
  const ids = license.replace(/[()]/g, '').split(/\s+(?:OR|AND)\s+/);
  const ok = license.includes(' OR ')
    ? ids.some((id) => PERMISSIVE.has(id))
    : ids.every((id) => PERMISSIVE.has(id));
  if (ok) return true;
  return Object.keys(REVIEWED).some((key) =>
    key.endsWith('/') || key.endsWith('-') ? name.startsWith(key) : name === key,
  );
}

const seen = new Map();
function walk(dir) {
  if (!existsSync(dir)) return;
  for (const entry of readdirSync(dir)) {
    if (entry.startsWith('.')) continue;
    const path = join(dir, entry);
    if (entry.startsWith('@')) {
      walk(path);
      continue;
    }
    const manifest = join(path, 'package.json');
    if (existsSync(manifest)) {
      const pkg = JSON.parse(readFileSync(manifest, 'utf8'));
      if (pkg.name) seen.set(`${pkg.name}@${pkg.version}`, { name: pkg.name, license: licenseOf(pkg) });
    }
    walk(join(path, 'node_modules'));
  }
}
walk(modules);

const counts = {};
const bad = [];
for (const [id, { name, license }] of seen) {
  counts[license] = (counts[license] ?? 0) + 1;
  if (!allowed(name, license)) bad.push(`${id}: ${license}`);
}
for (const [license, n] of Object.entries(counts).sort((a, b) => b[1] - a[1])) {
  console.log(`${String(n).padStart(4)}  ${license}`);
}
if (bad.length) {
  console.error(`Not on the allow list:\n${bad.join('\n')}`);
  process.exit(1);
}
console.log(`licenses: ${seen.size} packages ok`);
