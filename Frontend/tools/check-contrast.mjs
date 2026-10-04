#!/usr/bin/env node
/**
 * Design-token contrast gate.
 *
 * Parses src/styles/tokens.css and asserts WCAG 2.1 AA for every declared
 * foreground/background pairing:
 *   - --tone-*-fg against --tone-*-bg      -> 4.5:1 (normal text)
 *   - --c-text* against the surfaces        -> 4.5:1 (normal text)
 *   - --focus-ring-color against surfaces   -> 3.0:1 (SC 1.4.11 non-text)
 *
 * Run: npm run check:contrast
 * Exit code is 1 on any violation, so it works as a pre-commit / CI gate.
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const TOKENS = join(here, '..', 'src', 'styles', 'tokens.css');
const AA_TEXT = 4.5;
const AA_NON_TEXT = 3.0;

const src = readFileSync(TOKENS, 'utf8');

/** Read every `--name: value;` declaration from :root. */
const vars = {};
for (const m of src.matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/gi)) {
  const v = m[2].trim();
  if (!v.startsWith('#')) continue; // colours only
  vars[m[1]] = v.slice(0, 7);
}

const srgb = (hex) => {
  const h = hex.replace('#', '');
  const full = h.length === 3 ? [...h].map((c) => c + c).join('') : h;
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255);
};
const lum = (hex) => {
  const [r, g, b] = srgb(hex).map((c) =>
    c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)
  );
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const ratio = (a, b) => {
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

const results = [];
const check = (label, fg, bg, min) => {
  if (!vars[fg] || !vars[bg]) {
    results.push({ label, fg, bg, value: null, min, ok: false, note: 'token missing' });
    return;
  }
  const r = ratio(vars[fg], vars[bg]);
  results.push({ label, fg: vars[fg], bg: vars[bg], value: r, min, ok: r >= min });
};

// Semantic tones are guaranteed pairs by naming convention.
for (const tone of ['green', 'red', 'blue', 'amber', 'purple', 'neutral']) {
  check(`tone ${tone}`, `--tone-${tone}-fg`, `--tone-${tone}-bg`, AA_TEXT);
  check(`tone ${tone} on surface`, `--tone-${tone}-fg`, '--c-surface', AA_TEXT);
}

// Text ramp against every surface it can legally sit on.
const surfaces = ['--c-surface', '--c-surface-alt', '--c-surface-sunk'];
const textTokens = ['--c-ink', '--c-text', '--c-text-sub', '--c-text-muted'];
for (const t of textTokens) for (const s of surfaces) check(`${t} on ${s}`, t, s, AA_TEXT);

// Focus ring must be perceivable against every surface (WCAG 1.4.11).
for (const s of surfaces) check(`focus ring on ${s}`, '--focus-ring-color', s, AA_NON_TEXT);

// Links and action fills must meet text contrast, not just 3:1.
check('accent on surface', '--c-accent', '--c-surface', AA_TEXT);
check('action on invert', '--c-action', '--c-text-invert', AA_TEXT);

const failed = results.filter((r) => !r.ok);
const pad = (s, n) => String(s).padEnd(n);

console.log(`\nDesign-token contrast gate — ${results.length} checks\n`);
for (const r of results) {
  const val = r.value === null ? 'MISSING' : `${r.value.toFixed(2)}:1`;
  console.log(
    `  ${r.ok ? 'PASS' : 'FAIL'}  ${pad(r.label, 34)} ${pad(r.fg || '-', 9)} on ${pad(r.bg || '-', 9)} ${pad(val, 10)} (min ${r.min})`
  );
}
console.log(
  `\n  ${results.length - failed.length}/${results.length} passed` +
    `${failed.length ? ` — ${failed.length} FAILED\n` : '\n'}\n`
);

process.exit(failed.length ? 1 : 0);