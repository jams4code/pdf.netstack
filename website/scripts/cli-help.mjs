// Captures `pdfnetstack --help` from the tool built from this commit, so the command line page
// never drifts from the code.

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const website = fileURLToPath(new URL('..', import.meta.url));
const repo = join(website, '..');
const tool = join(repo, 'src/PdfNetStack.Cli/bin/Release/net10.0/PdfNetStack.Tool.dll');

if (!existsSync(tool)) {
  execFileSync('dotnet', ['build', 'src/PdfNetStack.Cli', '-c', 'Release', '-f', 'net10.0'], {
    cwd: repo,
    stdio: 'inherit',
  });
}
const help = execFileSync('dotnet', [tool, '--help'], { encoding: 'utf8' }).replace(/\r\n/g, '\n');
mkdirSync(join(website, 'src/generated'), { recursive: true });
writeFileSync(join(website, 'src/generated/cli-help.txt'), help.trimEnd() + '\n');
console.log('cli-help: captured');
