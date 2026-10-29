// Prose lint for the site: the product and company names are spelled one way, and the em dash and
// en dash are never used. Checks the content pages (outside code blocks) and the samples.

import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const website = fileURLToPath(new URL('..', import.meta.url));

const RULES = [
  { re: /[–—]/, message: 'em or en dash: use a comma, colon, parentheses or a new sentence' },
  { re: /PDFNetStack|PdfNETStack|Pdf\.NetStack|PDF\.NetStack|Pdfnetstack/, message: 'write PdfNetStack' },
  // pdf.netstack is the repository name: allowed in URLs and paths only.
  { re: /(?<![/\w])pdf\.netstack(?![\w/]*\.csproj|\/)/i, message: 'write PdfNetStack (pdf.netstack only in the repository URL)', skipUrls: true },
  { re: /\b(Jadev|JaDev|JADev)\b/, message: 'write JADEV' },
];

function* walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(path);
    else yield path;
  }
}

let problems = 0;
function check(file, lines) {
  lines.forEach((line, i) => {
    for (const rule of RULES) {
      const text = rule.skipUrls ? line.replace(/https?:\/\/\S+/g, '') : line;
      if (rule.re.test(text)) {
        console.error(`${relative(website, file)}:${i + 1}: ${rule.message}\n  ${line.trim()}`);
        problems++;
      }
    }
  });
}

for (const file of walk(join(website, 'src/content/docs'))) {
  if (!/\.mdx?$/.test(file)) continue;
  let inCode = false;
  const prose = readFileSync(file, 'utf8')
    .split('\n')
    .map((line) => {
      if (/^\s*(```|~~~)/.test(line)) {
        inCode = !inCode;
        return '';
      }
      return inCode ? '' : line.replace(/`[^`]*`/g, '');
    });
  check(file, prose);
}
for (const file of walk(join(website, 'samples'))) {
  if (file.endsWith('.cs')) check(file, readFileSync(file, 'utf8').split('\n'));
}

if (problems) {
  console.error(`check-names: ${problems} problem(s)`);
  process.exit(1);
}
console.log('check-names: ok');
