// Compiles and runs every code sample against the library source, the way CI does. A sample that
// fails to build, exits non-zero, or writes a PDF that does not open again fails the run.
//
// Each sample runs in its own folder under samples/work, seeded with the inputs from
// samples/_inputs.cs and scripts/make_sample_inputs.py. Requires the solution built in Release
// (`dotnet build PdfNetStack.sln -c Release`), Python with Pillow, and the .NET 10 SDK.
//
// A sample with a `// run: build-only` line is compiled but not run: it needs something CI does
// not have, such as the OCR model files. The rest of that line says why.
//
// Usage: node scripts/run-samples.mjs [name.cs...]
// SAMPLES_JOBS=4 sets the parallelism, SAMPLES_WORK=<dir> the work folder (default samples/work).

import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { cpus } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const website = fileURLToPath(new URL('..', import.meta.url));
const samples = join(website, 'samples');
const work = process.env.SAMPLES_WORK ? resolve(process.env.SAMPLES_WORK) : join(samples, 'work');
const inputs = join(work, '_inputs');
const jobs = Number(process.env.SAMPLES_JOBS) || Math.max(1, Math.min(4, cpus().length));
const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');

const FONTS = {
  'Inter-Regular.woff2': '@fontsource/inter/files/inter-latin-400-normal.woff2',
  'Inter-Bold.woff2': '@fontsource/inter/files/inter-latin-700-normal.woff2',
  'NotoSansArabic-Regular.woff2': '@fontsource/noto-sans-arabic/files/noto-sans-arabic-arabic-400-normal.woff2',
};

const BUILD_ONLY = /^\/\/ run: build-only(\s|$)/m;

/** Runs one sample in `cwd` (or only builds it); resolves with its exit code and output. */
function runSample(file, cwd, args = [], buildOnly = false) {
  return new Promise((done) => {
    const command = buildOnly
      ? ['build', '-c', 'Release', '-p:BuildProjectReferences=false', join(samples, file)]
      : ['run', '-c', 'Release', '-p:BuildProjectReferences=false', '--file', join(samples, file), '--', ...args];
    const child = spawn('dotnet', command, {
      cwd,
      env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
    });
    let output = '';
    child.stdout.on('data', (d) => (output += d));
    child.stderr.on('data', (d) => (output += d));
    const timer = setTimeout(() => child.kill(), 300_000);
    child.on('close', (code) => {
      clearTimeout(timer);
      done({ code: code ?? 1, output });
    });
  });
}

function listPdfs(dir) {
  return readdirSync(dir).filter((f) => f.toLowerCase().endsWith('.pdf')).map((f) => join(dir, f));
}

const requested = process.argv.slice(2);
const all = readdirSync(samples).filter((f) => f.endsWith('.cs') && !f.startsWith('_')).sort();
const selected = requested.length ? all.filter((f) => requested.includes(f)) : all;
if (requested.length && selected.length !== requested.length) {
  console.error(`Unknown sample in: ${requested.join(', ')}`);
  process.exit(1);
}

if (!existsSync(join(website, '../src/PdfNetStack/bin/Release'))) {
  console.error('Build the solution first: dotnet build PdfNetStack.sln -c Release');
  process.exit(1);
}

// Inputs: images, fonts from the npm font packages and the test fixtures, then the generated
// documents.
rmSync(work, { recursive: true, force: true });
mkdirSync(join(inputs, 'fonts'), { recursive: true });
const images = spawnSync(python, [join(website, 'scripts/make_sample_inputs.py'), inputs], { stdio: 'inherit' });
if (images.status !== 0) process.exit(1);
for (const [name, from] of Object.entries(FONTS)) {
  copyFileSync(join(website, 'node_modules', from), join(inputs, 'fonts', name));
}
// The repository's synthetic variable font, from tools/make_variable_font_fixtures.py.
copyFileSync(join(website, '../tests/PdfNetStack.Tests/Fixtures/Fonts/Test-Variable.ttf'), join(inputs, 'fonts', 'Variable.ttf'));
const made = await runSample('_inputs.cs', inputs);
if (made.code !== 0) {
  console.error(made.output);
  process.exit(1);
}
const inputFiles = new Set(readdirSync(inputs));

// Samples, a few at a time.
const failures = [];
const outputs = [];
const queue = [...selected];
async function worker() {
  while (queue.length) {
    const file = queue.shift();
    const dir = join(work, file.replace(/\.cs$/, ''));
    cpSync(inputs, dir, { recursive: true });
    const started = Date.now();
    const before = new Map(readdirSync(dir).map((f) => [f, statSync(join(dir, f)).mtimeMs]));
    const buildOnly = BUILD_ONLY.test(readFileSync(join(samples, file), 'utf8'));
    const result = await runSample(file, dir, [], buildOnly);
    writeFileSync(join(dir, 'output.txt'), result.output);
    const seconds = ((Date.now() - started) / 1000).toFixed(1);
    if (result.code !== 0) {
      failures.push(file);
      console.error(`FAIL ${file} (${seconds}s)\n${result.output}`);
      continue;
    }
    if (buildOnly) {
      console.log(`ok   ${file} (built only, ${seconds}s)`);
      continue;
    }
    for (const pdf of listPdfs(dir)) {
      const name = pdf.slice(dir.length + 1);
      if (!inputFiles.has(name) || before.get(name) !== statSync(pdf).mtimeMs) outputs.push(pdf);
    }
    console.log(`ok   ${file} (${seconds}s)`);
  }
}
await Promise.all(Array.from({ length: jobs }, worker));

// Every PDF a sample wrote must open again.
if (outputs.length) {
  const check = await runSample('_check.cs', work, outputs);
  console.log(check.output.trim());
  if (check.code !== 0) failures.push('_check.cs');
}

if (failures.length) {
  console.error(`samples: ${failures.length} failed: ${failures.join(', ')}`);
  process.exit(1);
}
console.log(`samples: ${selected.length} ran, ${outputs.length} PDF(s) checked`);
