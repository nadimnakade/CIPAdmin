"""Gate on the deploy zip's contents.

The zip is the artifact an operator copies to the server, and it is easy for it to
pick up files that only ever made sense on a developer machine: a server log from a
local smoke test, a wwwroot/CIPadmin/ mirror created to preview the SPA locally, a
scratch JSON file from a test script. None of those belong on a production server.

Run from the CIPAdmin/ directory. Exit 1 on anything suspicious, so it can gate a
build. Invoked automatically by make-deploy-zip.sh.
"""

import os
import sys
import zipfile

ZIP = os.path.join('publish', 'CIPAdmin-deploy.zip')

if not os.path.isfile(ZIP):
    print('check-zip: %s not found - build first' % ZIP)
    sys.exit(1)

z = zipfile.ZipFile(ZIP)
names = z.namelist()

bad = []
for n in names:
    low = n.lower()
    if low.endswith('.log'):
        bad.append(('test log', n))
    elif low.startswith('backend/wwwroot/cipadmin/'):
        bad.append(('local preview mirror', n))
    elif '_el' in low or '_smoke' in low or '_errorlog_test' in low:
        bad.append(('test scratch file', n))

print('check-zip: %d entries' % len(names))

if z.testzip() is not None:
    print('check-zip: FAIL - the archive is corrupt')
    sys.exit(1)

if bad:
    print('check-zip: FAIL - stray files in the deploy package:')
    for why, n in bad:
        print('   %-22s %s' % (why + ':', n))
    sys.exit(1)

# The scripts the feature cannot work without, plus the runbook that says which to run.
required = [
    'sql/ERROR-LOG.sql',
    'sql/ERROR-LOG-MENU.sql',
    'sql/DIAGNOSE-500.sql',
    'sql/FIND-APP-DB.sql',
    # Read-only. Reports which of the scripts below have not been run on the
    # target server yet, so a half-finished deployment is visible in one pass.
    'sql/DEPLOY-GAP-CHECK.sql',
    # Without this the double-submit fix is browser-only, and the UI guard is
    # bypassable by a second tab or a direct call to the endpoint.
    'sql/LIMIT-REQUEST-DEDUPE.sql',
    # Without this a FAILED vendor call can be retried until the credits run out.
    'sql/CIBIL-DEDUPE-FAILED.sql',
    'DEPLOY.md',
]
missing = [r for r in required if r not in names]
if missing:
    print('check-zip: FAIL - missing from the package: %s' % ', '.join(missing))
    sys.exit(1)

print('check-zip: OK - no stray files, archive intact, required scripts + runbook present')