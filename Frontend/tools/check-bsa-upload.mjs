#!/usr/bin/env node
/**
 * Regression guard for the CIBIL Analyzer upload rule.
 *
 * Bug: /bsa-analyzer required a bank statement, so the CIBIL-only path was
 * unreachable even though the backend has supported it all along
 * (/api/bsa/analyze-flex accepts either file). The screen also called
 * /api/bsa/analyze, which declares `statement` as required and answers
 * 400 "The statement field is required" when only a CIBIL report is sent.
 *
 * These assertions are on the SOURCE, not on a rendered page, because the failure
 * mode is a one-token regression that a build will not catch: it compiles, deploys,
 * and only shows up when a user uploads a CIBIL report alone.
 *
 * Run: node tools/check-bsa-upload.mjs
 * Exit code 1 on any failure, so it works as a CI gate.
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const PAGE = join(here, '..', 'src', 'pages', 'BsaAnalyzerPage.tsx');
const src = readFileSync(PAGE, 'utf8');
// /financial-analysis is the second analyzer and shares the upload panel, the grid
// markup and the analyze endpoint. Assert the rule on BOTH so fixing one screen
// cannot leave the other regressing.
const FA_PAGE = join(here, '..', 'src', 'pages', 'FinancialAnalysisPage.tsx');
const fa = readFileSync(FA_PAGE, 'utf8');

let passed = 0;
let failed = 0;

function check(label, condition, detail = '') {
  if (condition) {
    console.log(`  PASS  ${label}`);
    passed++;
  } else {
    console.log(`  FAIL  ${label}${detail ? ` — ${detail}` : ''}`);
    failed++;
  }
}

console.log('CIBIL Analyzer upload rule — either file is enough\n');

// 1. Neither file selected must be the ONLY rejection. If the guard still mentions
//    "bank statement file first", a CIBIL-only upload is impossible again.
const guard = src.match(/const analyze = async \(\) => \{[\s\S]{0,400}?return;\s*\n\s*\}/)?.[0] ?? '';
check(
  'rejects only when BOTH uploads are missing',
  /if \(!statement && !cibil\)/.test(guard),
  'expected `if (!statement && !cibil)` in analyze()',
);
check(
  'does not require the bank statement',
  !/if \(!statement\)\s*\{/.test(guard),
  'a bare `if (!statement)` still blocks a CIBIL-only upload',
);
check(
  'tells the user either upload is accepted',
  /bank statement, a CIBIL report, or both/i.test(src),
);

// 2. The statement must be appended conditionally, or the multipart body still
//    contains an empty "statement" part and the server sees it as supplied.
check(
  'statement is appended only when present',
  /if \(statement\) form\.append\('statement', statement\);/.test(src),
);
check('cibil is appended only when present', /if \(cibil\) form\.append\('cibil', cibil\);/.test(src));

// 3. Must call the endpoint that accepts either file. Checked by extracting the quoted
//    endpoint string rather than pattern-matching the whole file, so the presence of the
//    word in a comment cannot satisfy (or break) the assertion.
const endpoints = [...src.matchAll(/['"`](\/api\/bsa\/[a-z-]+)['"`]/g)].map((m) => m[1]);
const uploads = endpoints.filter((e) => e === '/api/bsa/analyze' || e === '/api/bsa/analyze-flex');
check(
  'uploads to /api/bsa/analyze-flex',
  uploads.length === 1 && uploads[0] === '/api/bsa/analyze-flex',
  `upload endpoints found: ${JSON.stringify(uploads)}`,
);
check(
  'never uploads to the statement-required /api/bsa/analyze',
  !uploads.includes('/api/bsa/analyze'),
);

// 4. The button must not be gated on the statement alone.
check(
  'Analyze button enabled when either file is chosen',
  /disabled=\{busy \|\| \(!statement && !cibil\)\}/.test(src),
);

// 5. CIBIL-only must not land on the empty Transactions tab, and must not show
//    structural zeroes that read as "this applicant has no income".
check(
  'a CIBIL-only run lands on a tab that has content',
  /statementProvided === false \? 'Summary' : 'Transactions'/.test(src),
);
check(
  'statement stat cards are hidden when no statement was supplied',
  /const hasStatement = result\?\.statementProvided !== false;/.test(src) &&
    /hasStatement \? \[/.test(src),
);
check(
  'transaction-derived tabs explain themselves instead of showing zeroes',
  /built from bank statement transactions/.test(src),
);

// 6. Both upload slots must read as optional, not one as required.
const requiredStars = (src.match(/bsa-upload-label[^<]*<span>\*<\/span>/g) ?? []).length;
check('no upload slot is marked compulsory', requiredStars === 0, `${requiredStars} slot(s) still marked *`);

// 7. Reopening a saved analysis must honour the same rule as a fresh run.
check(
  'history restore does not force the empty Transactions tab',
  /entry\.result\.statementProvided === false \? 'Summary' : 'Transactions'/.test(src),
  'a saved CIBIL-only analysis would reopen on an empty tab',
);

// 8. The Excel export must not ship a sheet of structural zeros. The export is more
//    dangerous than the screen because the file gets emailed and filed, where "Avg
//    Monthly Salary 0" reads as a finding about the applicant.
check(
  'Excel export omits statement figures when no statement was supplied',
  /if \(!hasStatement\) \{[\s\S]{0,400}?Bank statement section omitted/.test(src),
);
check(
  'Excel export labels the missing data as unknown, not zero',
  /they are NOT zero - they are unknown/i.test(src),
);
// accountSummary.period comes from parsed transactions, so it is empty on a
// CIBIL-only run. An unconditional "Period: " line ships a blank value.
check(
  'Excel export omits the empty Period line when no statement was supplied',
  /if \(hasStatement && a\.accountSummary\.period\)/.test(src),
);

// 9. "At least one file" must be STATED, not merely enforced. Both screens disable
//    Analyze when nothing is selected, and both label each slot "(optional)" — read
//    together that says "nothing is required", which is wrong. Without a visible
//    prompt the user cannot tell mandatory-one from broken.
for (const [label, code] of [['CIBIL Analyzer', src], ['Financial Analysis', fa]]) {
  check(
    `${label}: tells the user at least one file is mandatory`,
    /!statement && !cibil && !busy[\s\S]{0,300}?bsa-need-file/.test(code),
  );
check(
  `${label}: says the two files are alternatives, not both`,
  /Required: a bank statement[\s\S]{0,60}?<b>or<\/b>/i.test(code),
);
}

// 10. No message may still tell the user to upload BOTH. One did, on the
//     Underwriting empty state, directly contradicting the rule above.
check(
  'no analyzer message still demands both files',
  !/Upload both a statement and a CIBIL report/i.test(src) &&
    !/Upload both a statement and a CIBIL report/i.test(fa),
);

console.log(`\n  ${passed}/${passed + failed} passed`);
process.exit(failed === 0 ? 0 : 1);