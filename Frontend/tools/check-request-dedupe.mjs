#!/usr/bin/env node
/**
 * Regression guard for the CIBIL limit-request double submit.
 *
 * Bug: the "Submit Request to Administrator" button could fire POST
 * /api/cibil/limit-request twice from a double click. `disabled={reqSaving}`
 * does NOT prevent this: setState is not applied synchronously, so a second
 * click landing in the same frame still reads reqSaving === false and posts
 * again. The service's HasPendingLimitRequestAsync read is a read-then-write
 * with no lock, so both requests can pass the pre-check and both INSERT.
 *
 * These assertions are on the SOURCE, because the failure needs a real double
 * click to reproduce and a build will never catch it.
 *
 * Run: node tools/check-request-dedupe.mjs
 * Exit code 1 on any failure, so it works as a CI gate.
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, '..', 'src', 'pages', 'CibilCheckerPage.tsx'), 'utf8');
const svc = readFileSync(
  join(here, '..', '..', 'Backend', 'Services', 'CibilService.cs'), 'utf8');
const ctl = readFileSync(
  join(here, '..', '..', 'Backend', 'Controllers', 'CibilController.cs'), 'utf8');

let passed = 0;
let failed = 0;

function check(label, condition) {
  if (condition) {
    console.log(`  PASS  ${label}`);
    passed++;
  } else {
    console.log(`  FAIL  ${label}`);
    failed++;
  }
}

console.log('CIBIL limit request — no double submit\n');

// 1. A ref guard, set BEFORE the first await. State alone is not enough:
//    setReqSaving(true) does not take effect until React re-renders, so a
//    second click in the same frame slips past a `disabled` check.
check(
  'uses a ref guard, not state alone, to stop re-entry',
  /const reqSavingRef = useRef\(false\)/.test(page),
);
check(
  'ref is flipped before any await so the window is closed immediately',
  /if \(reqSavingRef\.current\) return;[\s\S]{0,80}?reqSavingRef\.current = true;/.test(page),
);
check(
  'ref is released in finally so an error does not wedge the form',
  /finally \{[\s\S]{0,120}?reqSavingRef\.current = false;/.test(page),
);

// 2. The button must still be disabled - the ref is the backstop, the disabled
//    attribute is still what the user sees.
check(
  'submit button stays disabled while saving',
  /disabled=\{reqSaving \|\| !reqLimit\}/.test(page),
);
check(
  'submit button declares type="submit" so Enter reaches the guarded handler',
  /type="submit" className="btn primary" disabled=\{reqSaving \|\| !reqLimit\}/.test(page),
);

// 3. Exactly one POST per submission: the ref guard must sit at the top of the
//    handler, before the api.post call, not after it.
const handler = page.slice(page.indexOf('const submitLimitRequest'));
check(
  'guard precedes the POST, so the second click never reaches the API',
  handler.indexOf('reqSavingRef.current = true') < handler.indexOf('api.post'),
);

// 4. Server side. The browser guard is bypassable (two tabs, a direct call), so
//    the read-then-write in the service needs a real constraint behind it.
check(
  'service still pre-checks for an existing Pending request',
  /HasPendingLimitRequestAsync\(empCode\)/.test(svc),
);
check(
  'duplicate-key error is mapped to a 400, not surfaced as a 500',
  /SqlException ex\) when \(ex\.Number is 2601 or 2627\)/.test(ctl),
);
check(
  'duplicate-key 400 uses the same wording as the pre-check',
  /already have a pending request for this month/i.test(ctl),
);

console.log(`\n  ${passed}/${passed + failed} passed`);
process.exit(failed === 0 ? 0 : 1);