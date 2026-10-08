# CIPAdmin — IIS Deployment Guide

## Which SQL files to run, and in what order

Run these against the **production CIP database**, in this order. Every one of them
is idempotent — re-running is safe and is the supported way to apply a migration to a
server that may already be partly up to date.

| # | File | What it creates / changes |
|---|------|---------------------------|
| 1 | `Database/schema.sql` | Base tables, role masters, existing menus |
| 2 | `Backend/sql/USER-SINGLE-SESSION.sql` | `TBL_CIP_ACTIVE_SESSION` (one active login per user) |
| 3 | `Backend/sql/TBL_CIP_APP_SETTINGS.sql` | `TBL_CIP_APP_SETTINGS` |
| 4 | `Backend/sql/CIBIL-PULL-LOG.sql` | `TBL_CIP_CIBIL_PULL_LOG` |
| 5 | `Backend/sql/APPLICATION-MODULES-MENU.sql` | Applications section (2000) + children 2001–2007, and their role grants |
| 6 | `Backend/sql/PUNCHIN-MENU.sql` | Attendance section (3000) + Punch In / Out (3001), and their role grants |
| 7 | `Backend/sql/AUTHENTICATOR-ADMIN-MENU.sql` | "Authenticator Setup" admin menu (2100), granted to SUPER ADMIN + ADMIN |

Optional — only if you use those screens:

| File | What it adds |
|------|--------------|
| `Backend/sql/FINANCIAL-ANALYSIS-MENU.sql` | Financial Analysis menu |
| `Backend/sql/NEW-TAX-REGIME-MENU.sql` | New Tax Regime Calculator menu (external link) |
| `Backend/sql/CIBIL-REPORTS-MENU-TL.sql` | Reports sub-menu for TL roles |

**Back up the database first.** `schema.sql` touches the shared menu tables.

### Verify before you copy files

`Backend/sql/DEPLOY-CHECK.sql` is read-only — it only SELECTs. Run it after the
migrations above and confirm **no row says `MISSING`**:

```bash
sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W \
       -i Backend/sql/DEPLOY-CHECK.sql
```

It checks the tables, the menus, the 94 expected menu/role grants, and the nine
legacy stored procedures the new modules call. The `-b` flag matters: without it
sqlcmd exits 0 even when a statement fails.

### The exact command line

```bash
# 1..7 in order
for f in Database/schema.sql \
         Backend/sql/USER-SINGLE-SESSION.sql \
         Backend/sql/TBL_CIP_APP_SETTINGS.sql \
         Backend/sql/CIBIL-PULL-LOG.sql \
         Backend/sql/APPLICATION-MODULES-MENU.sql \
         Backend/sql/PUNCHIN-MENU.sql \
         Backend/sql/AUTHENTICATOR-ADMIN-MENU.sql ; do
  echo "== $f"
  sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 -i "$f" \
    || { echo "FAILED at $f — stop and do not copy the package"; break; }
done
```

### Rolling out the authenticator

The authenticator is driven per employee by `IS_BYPASS`, and on the current data
**every employee has `IS_BYPASS = 1`**, so after this migration nobody is asked for a
code yet. That is the safe default — turn it on deliberately, one account at a time,
from **Authenticator Setup** (SUPER ADMIN / ADMIN only) or in SQL:

```sql
-- Require the authenticator for one employee. They will see a QR code at next login.
UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
   SET IS_BYPASS = 0, IS_GOOGLE_AUTHENTICATED = 0, GOOGLE_AUTH_SECRET_KEY = NULL
 WHERE USERNAME = 'some.user';

-- Lost phone: clear the secret so they re-scan a QR. Leave IS_BYPASS alone.
UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
   SET IS_GOOGLE_AUTHENTICATED = 0, GOOGLE_AUTH_SECRET_KEY = NULL
 WHERE USERNAME = 'some.user';
```

`DEPLOY-CHECK.sql` prints an `OTP USAGE` line reporting how many employees currently
skip the authenticator, so you can confirm the rollout state from the pre-flight.

## What to deploy

- **Zip**: `CIPAdmin/publish/CIPAdmin-deploy.zip` (~18 MB)
- **Folder**: `CIPAdmin/publish/` — `frontend/` (SPA) and `backend/` (API)

The build is baked for the **two-site production layout**:

- `frontend/` → served at `https://1up.co.in/CIPadmin/` (a static-files IIS site)
- `backend/`  → served at `https://1up.co.in/CIPAdmin_API/` (an IIS application
  running ASP.NET Core 8)

`backend/wwwroot/` also contains the same SPA, so `backend/` alone works as a
single-site deployment if you prefer that. `PathBase` in `appsettings.json` is what
lets the API live under `/CIPAdmin_API/` while its routes stay `/api/...`.

## Prerequisites on the IIS server (Windows Server 2012 / 8.2)

1. **.NET 8 Hosting Bundle** (NOT just the runtime — the bundle installs the
   AspNetCoreModuleV2 IIS module):
   https://dotnet.microsoft.com/en-us/download/dotnet/8.0 →
   *"ASP.NET Core Runtime 8.0.x → Windows → Hosting Bundle"* (x64).
   After installing: `net stop was /y && net start w3svc` (or restart the server).
2. **IIS** enabled (Server Manager → Add Roles → Web Server (IIS)).
3. **Microsoft Visual C++ 2015–2022 Redistributable (x64)** — required by the
   native Tesseract DLLs (OCR module): https://aka.ms/vs/17/release/vc_redist.x64.exe
4. **SQL Server** reachable from the server (the app connects via the connection
   string below; TDS is built in, no client install needed).

## 1. Prepare the production config (before copying)

Edit `backend/appsettings.json` in the package. Three values MUST change:

```jsonc
{
  "ConnectionStrings": {
    // REQUIRED: point at the production SQL Server. The shipped value is
    // "Server=localhost", which will NOT work on the server.
    "Default": "Server=YOUR_SQL_HOST,1433;Database=CIP;User ID=sa;Password=****;TrustServerCertificate=True;"
  },
  "Cibil": {
    // REQUIRED for CIBIL pulls. The shipped value is the placeholder
    // "<paste-your-x-api-token>" - without it CIBIL report pulls will fail.
    "ApiToken": "<real X-API-TOKEN>"
  },
  "Jwt": {
    // RECOMMENDED: a new long random string per environment. Changing it
    // invalidates every issued token, so everyone logs in again.
    "Secret": "a-different-long-random-production-secret"
  },

  // Leave these as shipped - the frontend is baked to these exact paths.
  "PathBase": "/CIPAdmin_API",
  "Swagger": { "Enabled": false }
}
```

`PathBase` matters: the frontend calls `/CIPAdmin_API/api/...`, and this setting is
what lets the API strip that prefix and still match its own `/api/...` routes. If you
host the API somewhere else, change it **and** rebuild the frontend with a matching
`VITE_API_BASE`, otherwise the two disagree and every call 404s.

## 2. Create the sites in IIS

### 2a. The API — an application at /CIPAdmin_API

1. Copy `backend/` to e.g. `C:\inetpub\cipadmin-api` (keep the folder structure —
   `web.config`, `wwwroot\`, `tessdata\`, `x64\` must stay at the root).
2. **Application Pool**: create `CIPAdminApi` pool →
   - .NET CLR Version: **No Managed Code**
   - Managed pipeline: **Integrated**
   - Identity: `ApplicationPoolIdentity` (or a domain account if you prefer
     Windows auth to SQL).
3. Create the site `https://1up.co.in`, then add an **Application** named
   `CIPAdmin_API` → physical path `C:\inetpub\cipadmin-api` → pool `CIPAdminApi`.
   Or point an existing site's `CIPAdmin_API` folder at it.
4. **Folder permissions**: give **IIS AppPool\CIPAdminApi** *Modify* on
   `C:\inetpub\cipadmin-api` — the app writes `pdf files\` (CIBIL PDFs) and
   `ocr uploads\` (OCR images) under it, creating them on startup.

### 2b. The SPA — a static site at /CIPadmin

1. Copy `frontend/` to e.g. `C:\inetpub\cipadmin-spa`.
2. Add an IIS **Application** named `CIPadmin` → physical path
   `C:\inetpub\cipadmin-spa`. Keep the pool as **No Managed Code**; the shipped
   `frontend\web.config` handles the SPA rewrite and no-cache headers for
   `index.html`.
3. The frontend needs read access only.

Both apps sit under the same host so requests are same-origin — no CORS rules and
no reverse proxy needed.

## 3. First-run checks

| Check | Expected |
|---|---|
| `https://1up.co.in/CIPadmin/` | The new split login page |
| `POST https://1up.co.in/CIPAdmin_API/api/auth/login` | JSON with `valid:true` and a token |
| `GET .../CIPAdmin_API/api/auth/session` (no token) | **401**, not the login page — a 200 here means the SPA fallback swallowed the API route |
| `https://1up.co.in/CIPadmin/punch-in` (deep link, F5) | Same SPA — the frontend `web.config` rewrite handles it |
| Login with a real account | Works; JWT issued; Applications + Attendance menus visible for your role |
| OCR page upload | Works (native Tesseract loads from `x64\`) |
| `.../CIPAdmin_API/swagger/v1/swagger.json` | **404** — Swagger is disabled in production by design. (`.../swagger` itself returns 200, but that is just the SPA `index.html` from the catch-all fallback, not API docs.) |

Logs if something fails: enable `stdoutLogEnabled="true"` in `backend\web.config`
(watch the `logs\` folder), and check Windows Event Viewer → Application.

## Updating an existing deployment

1. Stop the app pool.
2. Replace the folder contents with the new package (keep your edited
   `appsettings.json` — don't overwrite it).
3. Start the app pool.

## Rebuilding the package locally

```bash
bash Backend/sql/build-deploy.sh      # typecheck, vite build, publish, zip
bash Backend/sql/smoke-test-publish.sh 5099   # optional, needs the app running
```

The clean steps in that script are load-bearing: `dotnet publish` does **not** empty
its output folder, and the Web SDK ships `Backend/wwwroot` verbatim — so without them
a release silently accumulates stale hashed assets and can ship the *previous*
release's UI.

## Notes

- The publish is **framework-dependent** — the Hosting Bundle supplies .NET 8.
  To run without installing .NET on the server, publish self-contained instead:
  `dotnet publish -c Release -r win-x64 --self-contained true -o ../publish/backend`
- Upload limits (CIBIL 25 MB / OCR 10 MB) already work under IIS via Kestrel;
  no `maxRequestLength` tweaks needed.
- The dev-only CORS origins (localhost:5173) are harmless in production —
  the UI is served from the same origin.
- **Known pre-existing bug (not introduced by this release):** for accounts whose
  login requires OTP, `POST /api/auth/login` returns `valid:true` with a **null**
  `user`, and `LoginPage` treats a missing `user` as failure — so the OTP screen is
  never reached. Super Admin / bypass accounts are unaffected. Fix before enabling
  OTP for any role.
  **Fixed in this release** — the client now branches on `nextStep` before looking at
  `user`, and the enrollment secret is persisted before the QR is shown.

## Troubleshooting: `GET /api/applications` returns 500

Work through these in order. Stop at the first one that identifies the cause.

**Step 1 — `Backend/sql/DIAGNOSE-500.sql`** (read-only). Reports whether
`USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING`, `USP_CIP_EXISTINGLOAN_DETAILS` and the
tables they read exist, with row counts and a `missing_procs` / `missing_tables`
summary. Anything non-zero is a genuinely incomplete database restore.

If this reports **0 missing but the API still 500s**, the objects are present and the
cause is something else. Go to step 2 — restoring the procs will not help.

**Step 2 — `Backend/sql/VERIFY-SIGNATURES.sql`** (read-only). An object can exist and
still be uncallable. This checks, in order:

1. **Which SQL login you are connected as.** This is usually the whole answer. Run it
   as `sa` and it proves nothing about the API's permissions; edit the `@app_login`
   variable at the top to the `User ID` in the deployed `appsettings.json`
   `ConnectionStrings:Default` and re-run as that login.
2. **`EXECUTE` permission** on both procs, via `HAS_PERMS_BY_NAME` (resolves
   inherited grants through roles, which is what actually decides access).
3. **The TVP `dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type`.** User-defined table
   types live in `sys.types`, not `sys.objects`, so **no object-based diagnostic can
   report a missing one** — including `DIAGNOSE-500.sql`. A missing type breaks only
   `/api/applications/existing-loans`, which is a distinctive symptom.
4. **Parameter signatures** for both procs, matched by name against what the C# binds.
5. **Cross-database / linked-server references** inside the procedure bodies.
6. **A live `EXEC` of both procedures**, exactly as the API calls them, inside
   `TRY/CATCH`. This is the authoritative check: it prints the real
   `ERROR_NUMBER()` and message instead of guessing. Edit `@emp` to a real
   `empCode` (Admin is 10) — the paging proc builds a temp hierarchy from that user.

**Step 3 — if the objects all exist but the API still 500s, run
`Backend/sql/FIND-APP-DB.sql`.** This is the case where SSMS works and the API does
not, and the difference is always the connection context. Edit the three variables at
the top (`@AppLogin`, `@AppDatabase`, `@AppServer`) to the values from the DEPLOYED
`appsettings.json` — the one sitting next to `CIPAdmin.Backend.exe` on the server,
not the repository copy — then run it. It tests, in order:

1. **Wrong database or server.** The connection string may name a DB where these procs
   genuinely do not exist, which makes every result in SSMS true *and* the 500 real.
2. **Permissions, impersonated.** It uses `EXECUTE AS USER` so you do not have to
   reconnect SSMS as the app login, then actually **runs both procedures as that
   login** and reports the real SQL error. Do not trust `HAS_PERMS_BY_NAME` here: under
   impersonation it returns NULL even when the grant exists and the call succeeds.
   Note the TVP needs its own grant — `GRANT EXECUTE ON TYPE::dbo.<Type> TO <login>` —
   and without it only `/existing-loans` fails, which looks identical to a missing object.
3. **Command timeout.** The app uses 60s (180s for existing-loans); SSMS has none, so
   a slow procedure succeeds in SSMS and dies in the app.

**Step 4 — read the response.** The current build returns a JSON body on an unhandled
exception, so the browser or curl shows the actual cause instead of a blank 500:

```json
{ "error": "...", "detail": "...", "sqlErrorNumber": 2812 }
```

`sqlErrorNumber` `262`/`297`/`300` mean permissions; `2812` a missing procedure;
`2715`/`208` a missing type or table. An **empty** body means an **older build** is
deployed — redeploy and retry.

**Step 5 — the server log.** IIS swallows stdout unless logging is configured. Check
the IIS log for the request, and enable failed-request tracing or stdout logging for
the application pool if you need the stack trace.

## Where things live

| Path | What |
|---|---|
| `publish/CIPAdmin-deploy.zip` | The deployable package |
| `publish/frontend/` | SPA, baked for base `/CIPadmin/`, API `/CIPAdmin_API` |
| `publish/backend/` | .NET publish, `PathBase=/CIPAdmin_API`, `Swagger.Enabled=false` |
| `Backend/sql/DEPLOY-CHECK.sql` | Read-only pre-flight probe |
| `Backend/sql/ERROR-LOG.sql` | Creates `TBL_CIP_API_ERROR_LOG` (admin Error Log screen) |
| `Backend/sql/ERROR-LOG-MENU.sql` | Adds the admin "Error Log" menu (run after ERROR-LOG.sql) |
| `Backend/sql/test-error-log.sh` | End-to-end test: breaks a proc on purpose, asserts it is captured |
| `Backend/sql/DIAGNOSE-500.sql` | Read-only: do the legacy procs/tables exist at all? |
| `Backend/sql/VERIFY-SIGNATURES.sql` | Read-only: if the objects exist but the API still 500s, run this |
| `Backend/sql/FIND-APP-DB.sql` | Read-only: finds why SSMS works but the API 500s (wrong DB, permissions, timeout) |
| `Backend/sql/AUTHENTICATOR-ADMIN-MENU.sql` | Authenticator Setup menu (run after PUNCHIN-MENU.sql) |
| `Backend/sql/LIMIT-REQUEST-DEDUPE.sql` | One pending CIBIL limit request per employee per month (unique filtered index). Idempotent; reports existing duplicates instead of failing. |
| `Backend/sql/CIBIL-DEDUPE-FAILED.sql` | Freezes a PAN for the rest of the month after ANY pull attempt (success or failure): attempt-lookup index, admin-only `ERROR_MESSAGE` column, unique in-flight guard. Idempotent; run after CIBIL-PULL-LOG.sql. |
| `Backend/sql/otp-selftest.sql` | Test fixture: 3 employees covering every authenticator branch |
| `Backend/sql/otp-e2e-test.py` | End-to-end authenticator test against a running backend |
| `Backend/sql/build-deploy.sh` | One-shot clean build + zip |
| `Backend/sql/smoke-test-publish.sh` | Checks the built package actually serves and logs in |
| `Backend/sql/make-deploy-zip.sh` | Zips the two publish folders |
