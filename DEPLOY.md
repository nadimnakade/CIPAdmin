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
# 1..6 in order
for f in Database/schema.sql \
         Backend/sql/USER-SINGLE-SESSION.sql \
         Backend/sql/TBL_CIP_APP_SETTINGS.sql \
         Backend/sql/CIBIL-PULL-LOG.sql \
         Backend/sql/APPLICATION-MODULES-MENU.sql \
         Backend/sql/PUNCHIN-MENU.sql ; do
  echo "== $f"
  sqlcmd -S tcp:YOUR-SQL-HOST,1433 -d CIP -U sa -P **** -C -b -h -1 -W -l 30 -i "$f" \
    || { echo "FAILED at $f — stop and do not copy the package"; break; }
done
```

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

## Where things live

| Path | What |
|---|---|
| `publish/CIPAdmin-deploy.zip` | The deployable package |
| `publish/frontend/` | SPA, baked for base `/CIPadmin/`, API `/CIPAdmin_API` |
| `publish/backend/` | .NET publish, `PathBase=/CIPAdmin_API`, `Swagger.Enabled=false` |
| `Backend/sql/DEPLOY-CHECK.sql` | Read-only pre-flight probe |
| `Backend/sql/build-deploy.sh` | One-shot clean build + zip |
| `Backend/sql/smoke-test-publish.sh` | Checks the built package actually serves and logs in |
| `Backend/sql/make-deploy-zip.sh` | Zips the two publish folders |
