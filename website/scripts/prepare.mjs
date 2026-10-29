// Everything the site needs before `astro build`: the synced docs, the command line help, and
// the API reference (DocFX metadata, then scripts/api.mjs). Runs as part of `npm run build`.

import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const website = fileURLToPath(new URL('..', import.meta.url));
const run = (cmd, args) => execFileSync(cmd, args, { cwd: website, stdio: 'inherit' });

run('node', ['scripts/sync-docs.mjs']);
run('node', ['scripts/cli-help.mjs']);
// DocFX takes a minute or two; set SKIP_DOCFX=1 to reuse obj/api from an earlier run.
if (!process.env.SKIP_DOCFX || !existsSync(new URL('../obj/api', import.meta.url))) {
  run('dotnet', ['tool', 'restore']);
  run('dotnet', ['docfx', 'metadata', 'docfx.json']);
}
run('node', ['scripts/api.mjs']);
run('node', ['scripts/check-names.mjs']);
