// Code samples live in website/samples as .NET file-based apps that CI compiles and runs. Pages
// include them by name; this module turns a sample into what a reader sees.

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RELEASE } from '../site.config.mjs';

// Bundled endpoints lose their source location, so astro.config.mjs records the website folder.
const SAMPLES_DIR = process.env.WEBSITE_ROOT
  ? join(process.env.WEBSITE_ROOT, 'samples/')
  : fileURLToPath(new URL('../../samples/', import.meta.url));
const PROJECT_LINE = /^#:project \.\.\/\.\.\/src\/([\w.]+)\/\1\.csproj\s*$/;
const SINCE_LINE = /^\/\/ since: ([\w.]+)\s*$/;
// Lines for the tooling only: `// run: build-only ...` (scripts/run-samples.mjs) and
// `// preview: ...` (scripts/previews.py). Readers never see them.
const TOOL_LINE = /^\/\/ (run|preview): /;
const REGION_START = /^\s*#region\b\s*(.*)$/;
const REGION_END = /^\s*#endregion\b/;

/** Reads a sample file and its `// since:` version. */
export function readSample(name) {
  if (!/^[\w-]+\.cs$/.test(name)) throw new Error(`Invalid sample name: ${name}`);
  const text = readFileSync(SAMPLES_DIR + name, 'utf8').replace(/\r\n/g, '\n');
  const since = text.split('\n').map((l) => SINCE_LINE.exec(l)?.[1]).find(Boolean);
  if (!since) throw new Error(`Sample ${name} has no "// since:" line`);
  return { text, since };
}

/** True when the sample's features and package are on NuGet today. */
function isPublished(since, packageId) {
  return RELEASE.published.includes(since) && packageId in RELEASE.packages;
}

/** The whole file as a reader runs it: `#:package` when published, a clone path otherwise. */
function displayFile({ text, since }) {
  const out = [];
  let noted = false;
  for (const line of text.split('\n')) {
    if (SINCE_LINE.test(line) || TOOL_LINE.test(line) || REGION_START.test(line) || REGION_END.test(line)) continue;
    const project = PROJECT_LINE.exec(line);
    if (!project) {
      out.push(line);
      continue;
    }
    const id = project[1];
    if (isPublished(since, id)) {
      out.push(`#:package ${id}@${RELEASE.packages[id]}`);
    } else {
      if (!noted) {
        out.push(
          since === 'unreleased' ? '// Not on NuGet yet. Clone' : `// Uses features from ${since}, not on NuGet yet. Clone`,
          '// https://github.com/jams4code/pdf.netstack next to this file to build against it.',
        );
        noted = true;
      }
      out.push(`#:project pdf.netstack/src/${id}/${id}.csproj`);
    }
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trim() + '\n';
}

/** The lines of one `#region name`, dedented, without nested region markers. */
function displayRegion({ text }, region, name) {
  const lines = text.split('\n');
  const start = lines.findIndex((l) => REGION_START.exec(l)?.[1].trim() === region);
  if (start < 0) throw new Error(`Sample ${name} has no region "${region}"`);
  const body = [];
  let depth = 1;
  for (let i = start + 1; i < lines.length; i++) {
    if (REGION_START.test(lines[i])) {
      depth++;
      continue;
    }
    if (REGION_END.test(lines[i]) && --depth === 0) break;
    if (!REGION_END.test(lines[i])) body.push(lines[i]);
  }
  const indent = Math.min(...body.filter((l) => l.trim()).map((l) => /^ */.exec(l)[0].length));
  return body.map((l) => l.slice(indent)).join('\n').trim() + '\n';
}

/** What a page shows for `sample="name.cs"` with an optional `region="..."`. */
export function renderSample(name, region) {
  const sample = readSample(name);
  return region ? displayRegion(sample, region, name) : displayFile(sample);
}

/**
 * A text file written at build time into src/generated, such as `pdfnetstack --help` captured by
 * scripts/cli-help.mjs, for a fence with `generated="cli-help.txt"`.
 */
export function renderGenerated(name) {
  if (!/^[\w-]+\.txt$/.test(name)) throw new Error(`Invalid generated file name: ${name}`);
  const dir = process.env.WEBSITE_ROOT
    ? join(process.env.WEBSITE_ROOT, 'src/generated/')
    : fileURLToPath(new URL('../generated/', import.meta.url));
  return readFileSync(dir + name, 'utf8').replace(/\r\n/g, '\n');
}

/** Parses the `sample`, `region` and `generated` attributes of a code fence's meta string. */
export function parseSampleMeta(meta) {
  if (!meta) return null;
  const sample = /\bsample="([^"]+)"/.exec(meta)?.[1];
  const generated = /\bgenerated="([^"]+)"/.exec(meta)?.[1];
  if (!sample && !generated) return null;
  const region = /\bregion="([^"]+)"/.exec(meta)?.[1];
  const rest = meta.replace(/\b(sample|region|generated)="[^"]*"/g, '').trim();
  return { sample, region, generated, rest };
}

/** The code a parsed fence shows, and the file title to give it (none for parts of a file). */
export function renderFence(parsed) {
  if (parsed.generated) return { code: renderGenerated(parsed.generated), title: '' };
  return {
    code: renderSample(parsed.sample, parsed.region),
    title: parsed.region ? '' : parsed.sample,
  };
}

/** Expands every sample fence in raw Markdown, for the Markdown twins and llms files. */
export function expandSamplesInMarkdown(markdown) {
  return markdown.replace(/^([ \t]*)```(\w+)([^\n]*)\n([\s\S]*?)^\1```[ \t]*$/gm, (whole, indent, lang, meta) => {
    const parsed = parseSampleMeta(meta);
    if (!parsed) return whole;
    const { code, title } = renderFence(parsed);
    return `${indent}\`\`\`${lang}${title ? ` title="${title}"` : ''}\n${code}${indent}\`\`\``;
  });
}
