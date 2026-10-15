// Regression guard for two production outages, both of which compiled cleanly and
// shipped a broken bundle:
//
//   1. ROOT-ABSOLUTE ASSETS. vite.config.ts does `base: process.env.VITE_BASE || '/'`,
//      so a build run without VITE_BASE emits index.html pointing at /assets/... The
//      SPA is deployed at /CIPadmin/, so every browser resolved those to
//      1up.co.in/assets/... -> 404 -> blank page. `npm run build` alone does this.
//
//   2. .mjs ASSETS. IIS (8.5 and 10) has no MIME mapping for .mjs, so it answers a
//      hard 404 for the PDF.js worker even though the file IS on disk - the SPA
//      rewrite never fires precisely because the file exists. PDF.js then logs
//      "Setting up fake worker" and runs the 1.2 MB worker on the main thread.
//
// Both are invisible to `tsc`: the bundle typechecks and the CSS contrast gate passes.
// This inspects what actually got emitted.
//
// Run: node tools/check-dist.mjs [distDir]        (default: <Frontend>/dist)
// Exits non-zero when anything is wrong, so `&&`-chained build scripts stop before
// anything is copied into wwwroot or the deploy zip.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(process.argv[2] || path.join(here, '..', 'dist'));

// Vite's default asset directory. A ref rooted here means `base` was '/' - i.e. the
// build ran without VITE_BASE, which is outage (1).
const ROOTED_ASSETS = /^\/assets\//;

// Extensions IIS cannot serve out of the box on the production server.
const UNMAPPABLE = ['.mjs'];

let failed = 0;
let passed = 0;
const check = (label, ok, fix) => {
  if (ok) passed++;
  else failed++;
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}` + (ok ? '' : `\n         -> ${fix}`));
};
const blame = (items) => items.forEach((i) => console.log(`         offending: ${i}`));

// Git Bash rewrites POSIX-looking env values (`VITE_BASE=/CIPadmin/` becomes
// `/C:/Program Files/Git/CIPadmin/`) before node sees them. Comparing against that
// would fail every correct build, so it is detected and the optional base check is
// reported as skipped rather than silently passing or falsely failing.
const envBase = process.env.VITE_BASE;
// Git Bash hands native programs either form: `/C:/Program Files/Git/CIPadmin/` or
// `C:/Program Files/Git/CIPadmin/`. A legitimate VITE_BASE never contains a drive
// letter, so both are unambiguous.
const mangled = !!envBase && /^\/?[A-Za-z]:[\\/]/.test(envBase);
const base =
  envBase && !mangled
    ? '/' + envBase.replace(/^\/+/, '').replace(/\/+$/, '') + (envBase === '/' ? '' : '/')
    : null;

const indexHtml = path.join(dist, 'index.html');
if (!fs.existsSync(indexHtml)) {
  console.log(`  FAIL  ${path.join(dist, 'index.html')} does not exist - nothing was built`);
  process.exit(1);
}
const html = fs.readFileSync(indexHtml, 'utf8');

// --- no asset IIS would refuse ----------------------------------------------------
const walk = (dir) =>
  fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = path.join(dir, e.name);
    return e.isDirectory() ? walk(p) : [p];
  });
const badExt = walk(dist)
  .filter((f) => UNMAPPABLE.some((x) => f.endsWith(x)))
  .map((f) => path.relative(dist, f));
check(
  `no ${UNMAPPABLE.join(' / ')} asset emitted into ${path.relative(process.cwd(), dist) || 'dist'}`,
  badExt.length === 0,
  `IIS has no MIME mapping for ${UNMAPPABLE.join(', ')} and will 404 these. ` +
    'Rename the asset at build time (build.rollupOptions.output.assetFileNames in vite.config.ts).'
);
blame(badExt);

const refs = [...html.matchAll(/\b(?:src|href)\s*=\s*"([^"]+)"/g)].map((m) => m[1]);
const locals = refs.filter((r) => r.startsWith('/') && !r.startsWith('//'));

check('index.html references at least one asset', refs.length > 0, 'the build emitted no tags at all');

// --- the root-absolute rule: independent of the environment -----------------------
const rooted = locals.filter((r) => ROOTED_ASSETS.test(r));
check(
  'no asset URL is root-absolute (build ran without a sub-path base)',
  rooted.length === 0,
  'index.html points at /assets/... but the SPA is served from /CIPadmin/, so these 404. ' +
    'Rebuild through Backend/sql/build-deploy.sh, or set VITE_BASE=/CIPadmin/ before `vite build`.'
);
blame(rooted);

// --- optional: refs match the declared deploy base --------------------------------
if (base === null) {
  console.log(
    `  SKIP  every asset URL is under the deploy base` +
      (mangled ? `   (VITE_BASE arrived MSYS-mangled: ${envBase})` : '   (VITE_BASE not set)')
  );
} else if (base === '/') {
  console.log(`  SKIP  every asset URL is under the deploy base   (VITE_BASE is root '/')`);
} else {
  const offBase = locals.filter((r) => !r.startsWith(base));
  check(`every asset URL is under the deploy base ${base}`, offBase.length === 0,
    `these were built for a different base than ${base}; they will 404 in production.`);
  blame(offBase);
}

const refMjs = locals.filter((r) => r.endsWith('.mjs'));
check('index.html references no .mjs file', refMjs.length === 0, 'IIS will 404 it - emit .js instead.');
blame(refMjs);

// --- every reference resolves to a real file --------------------------------------
// A ref is "<base>assets/x.js"; the base prefix is whatever the build was told, so it
// is stripped progressively rather than assumed. This works even when VITE_BASE is
// unavailable (see SKIP above) and still fails for a genuinely absent file.
const resolves = (ref) => {
  const parts = ref.replace(/^\/+/, '').split('/');
  for (let i = 0; i < parts.length; i++) {
    if (fs.existsSync(path.join(dist, parts.slice(i).join('/')))) return true;
  }
  return false;
};
const missing = locals.filter((r) => !resolves(r));
check(
  'every referenced asset exists on disk in dist',
  missing.length === 0,
  'index.html points at a file the build did not produce - a stale or partial build.'
);
missing.forEach((r) => console.log(`         missing: ${r}`));

console.log(`\n  ${passed}/${passed + failed} passed`);
process.exit(failed === 0 ? 0 : 1);
