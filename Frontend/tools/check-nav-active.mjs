// Regression guard for the sidebar highlighting bug.
//
// react-router's NavLink.isActive is PREFIX based. With /applications and
// /applications/existing-loans both in the menu, /applications stayed lit on every
// Applications page. Layout.tsx now computes `active` itself; this mirrors that logic
// and asserts exactly one item is highlighted per route.
//
// Run: node tools/check-nav-active.mjs

const MENU = [
  '/',
  '/cibil',
  '/employees',
  '/applications',
  '/applications/new',
  '/applications/scans',
  '/applications/bulk',
  '/applications/existing-loans',
  '/settings',
];

const norm = (p) => p.replace(/\/+$/, '') || '/';

function makeIsActive(pathname, menuPaths) {
  const current = norm(pathname);
  return (to) => {
    const target = norm(to);
    if (current === target) return true;
    return current.startsWith(target + '/') && !menuPaths.has(current);
  };
}

const menuPaths = new Set(MENU.map(norm));

// [route, the ONE path that must be highlighted]
const CASES = [
  ['/', '/'],
  ['/applications', '/applications'],
  ['/applications/existing-loans', '/applications/existing-loans'],
  ['/applications/new', '/applications/new'],
  ['/applications/scans', '/applications/scans'],
  ['/applications/bulk', '/applications/bulk'],
  ['/applications/bulk/', '/applications/bulk'],        // trailing slash
  ['/settings', '/settings'],
  ['/cibil', '/cibil'],
  // Edit form: a descendant with no menu entry of its own keeps its parent lit.
  ['/applications/123', '/applications'],
  // Must not leak onto unrelated prefixes.
  ['/applicationsx', null],
  ['/settingsx', null],
];

let failed = 0;
for (const [route, expected] of CASES) {
  const isActive = makeIsActive(route, menuPaths);
  const lit = MENU.filter((p) => isActive(p));

  const ok = expected === null ? lit.length === 0 : lit.length === 1 && lit[0] === expected;

  if (!ok) failed++;
  const mark = ok ? 'PASS' : 'FAIL';
  console.log(
    `  ${mark}  ${route.padEnd(34)} highlighted=[${lit.join(', ')}]` +
      (ok ? '' : `   expected=[${expected ?? 'none'}]`)
  );
}

// The exact bug report: on the existing-loans page, /applications must NOT also be lit.
const bug = (() => {
  const isActive = makeIsActive('/applications/existing-loans', menuPaths);
  return isActive('/applications') === false;
})();
if (!bug) failed++;
console.log(`  ${bug ? 'PASS' : 'FAIL'}  /applications is NOT lit on /applications/existing-loans`);

console.log(`\n  ${CASES.length + 1 - failed}/${CASES.length + 1} passed`);
process.exit(failed === 0 ? 0 : 1);
