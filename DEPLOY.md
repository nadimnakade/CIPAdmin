# CIPAdmin — IIS Deployment Guide

## What to deploy

One package contains everything (API + frontend UI):

- **Zip**: `CIPAdmin/publish/CIPAdmin-deploy.zip` (~16 MB)
- **Folder**: `CIPAdmin/publish/backend/` (same content, unzipped)

The frontend (React build) is already inside as `wwwroot/` — the API serves it,
so you host **one IIS site**, not two. All `/api` calls are same-origin relative
paths; no CORS or reverse-proxy rules needed.

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

Edit `appsettings.json` in the package:

```jsonc
{
  "ConnectionStrings": {
    // Point at the production SQL Server:
    "Default": "Server=YOUR_SQL_HOST;Database=CIP;User ID=sa;Password=****;TrustServerCertificate=True;"
  },
  "Cibil": {
    "ApiToken": "<real X-API-TOKEN>"   // required for CIBIL pulls
  },
  "Jwt": {
    // CHANGE for production — any long random string:
    "Secret": "a-different-long-random-production-secret"
  }
}
```

Run `Database/schema.sql` against the production CIP database (it is idempotent —
safe to re-run; it creates the new tables, menu entries and role grants).

## 2. Create the site in IIS

1. Copy the package to e.g. `C:\inetpub\cipadmin` (keep the folder structure —
   `web.config`, `wwwroot\`, `tessdata\`, `x64\` must stay at the root).
2. **Application Pool**: create `CIPAdmin` pool →
   - .NET CLR Version: **No Managed Code**
   - Managed pipeline: Integrated
   - Identity: `ApplicationPoolIdentity` (or a domain account if you prefer
     Windows auth to SQL).
3. **Site**: create `CIPAdmin` site → physical path `C:\inetpub\cipadmin` →
   assign the pool. Bindings: `http *:80` (add `https :443` with a certificate —
   recommended since the app handles login credentials).
4. **Folder permissions**: give the app-pool identity (**IIS AppPool\CIPAdmin**)
   *Modify* on `C:\inetpub\cipadmin` — the app writes `pdf files\` (CIBIL PDFs)
   and `ocr uploads\` (OCR images) under it, creating them on startup.

## 3. First-run checks

| Check | Expected |
|---|---|
| `http://YOUR-SERVER/` | Login page (SPA served from wwwroot) |
| `http://YOUR-SERVER/swagger` | API docs |
| `http://YOUR-SERVER/employees` (deep link) | Same SPA — IIS fallback handled by the app itself |
| Login with a real account | Works; JWT issued |
| OCR page upload | Works (native Tesseract loads from `x64\`) |

Logs if something fails: enable `stdoutLogEnabled="true"` in `web.config`
(watch the `logs\` folder), and check Windows Event Viewer → Application.

## Updating an existing deployment

1. Stop the site's app pool.
2. Replace the folder contents with the new package (keep your edited
   `appsettings.json` — don't overwrite it).
3. Start the app pool.

## Notes

- The publish is **framework-dependent** — the Hosting Bundle supplies .NET 8.
  To run without installing .NET on the server, publish self-contained instead:
  `dotnet publish -c Release -r win-x64 --self-contained true -o ../publish/backend`
- Upload limits (CIBIL 25 MB / OCR 10 MB) already work under IIS via Kestrel;
  no `maxRequestLength` tweaks needed.
- The dev-only CORS origins (localhost:5173) are harmless in production —
  the UI is served from the same origin.
