# CIPAdmin

Full-stack admin portal: **React 18 + TypeScript (Vite)** frontend, **ASP.NET Core 8 Web API** backend with **Swagger**, against the existing `CIP` SQL Server database.

## Features

| Screen | What it does |
|---|---|
| **Login** (1UP card UI) | JWT auth, port of the legacy `Chk_Credential`. Google Authenticator second factor driven entirely by the per-employee `IS_BYPASS` flag, exactly like the legacy portal: **`IS_BYPASS = 1` → straight to the dashboard**, **`IS_BYPASS = 0` → the authenticator is mandatory**. An enrolled user is asked for a 6-digit code (`require_otp`); a user who has never enrolled gets a QR code to scan (`enroll_otp`) and completes setup with the first valid code. "Remember me" extends token lifetime. |
| **Authenticator Setup** (admin) | Manages the second factor per employee, replacing the legacy `Verify/ResetGoogleAuth` screen. Lists each employee's required/not-required state and whether they are enrolled. **Reset** clears the stored secret so a user with a lost phone re-scans a QR at next login (it deliberately does *not* touch `IS_BYPASS`, since flipping that would silently switch the second factor off). **Require it / Stop requiring** flips `IS_BYPASS` for one employee. Granted to SUPER ADMIN and ADMIN only — the API returns 403 to any other role, so the menu grant matches the endpoint exactly. |
| **Dashboard** | Stat cards (CIBIL pulled today + Δ, total reports + Δ, active employees, active DIDs), 7-day trend line chart, reports-by-type donut. |
| **CIBIL Checker** | Form matching the shared UI with a monthly **quota chip** (used/limit + progress bar). Regular users have a configurable monthly pull limit (default 50); admins bypass it. Only admins may use `?regenerate=true`. Same-month successful reports are served from cache, with one requester quota credit and no second vendor call. A failed vendor attempt freezes that PAN for everyone for the rest of the month, since each vendor request costs about ₹40 whether it succeeds or fails; only an explicit admin re-pull bypasses the freeze. Each completed `OK` or `FAILED` vendor call consumes one credit from its requester; blocked retries and in-flight `PENDING` requests do not. Failure reasons are visible to admins only. Attempts appear in My Reports and the Failed Pull Attempts list; saved Reports rows explicitly display `Status=OK`. |
| **Employees (DID Management)** | `TBL_CIP_EMPLOYEE_DID` (references `TBL_CIP_EMPLOYEE_MASTER.EMP_CODE`). `DID_NUMBER` stores the **SIP username**; `EXTENSION_NUMBER` is a persisted computed column = last 3 characters of the SIP username. Summary tiles, inline Assign panel (employee picker auto-fills code/name/designation; extension auto-fills from SIP username), search (matches name/code/SIP username/extension), status + designation filters, Excel export, change SIP username (history kept in `TBL_CIP_EMPLOYEE_DID_HISTORY`), release (soft delete). Duplicate SIP usernames rejected server-side (HTTP 409). |
| **Employee Master** | Server-paged view of all 10k+ employees in `TBL_CIP_EMPLOYEE_MASTER`: search by name/code/email, designation + active filters, server-side column sorting. Each row has **Documents** and **Offer Letter** actions. |
| **Company Category** | Port of CIPPortal's CompanyCategory module on `TBL_CIP_COMPANY_CATEGORY` (1.7M+ rows, served 1 row per company via MAX(COMPANY_KEY)). Bulk **.xlsx upload** with Product + Bank (legacy "Company Name"/"Final Category" format, deduped per product+bank), paged/filterable/sortable grid, inline edit, delete. |
| **OCR to Excel** | `TBL_CIP_OCR_JOBS` + `Backend/ocr uploads/` (image + extracted-grid JSON per job). Upload a photo/screenshot of a spreadsheet (JPG/PNG/BMP/GIF/WEBP/TIFF ≤ 10 MB); Tesseract OCR rebuilds the grid (rows from line clustering, columns from word-gap edges), shown in an editable preview next to the original image. Edits save back to history, any past conversion can be re-opened, and the grid exports to a real `.xlsx` (numeric columns stay numeric via `ExcelExporter.BuildFromArrays`). History: own conversions, or everyone's for admins. | | **Lead Master** | Lead tracking on `TBL_CIP_LEAD_MASTER` (new table): search, status + active filters, server-side sorting and paging, add/edit modal (name, mobile, email, location, source, remarks, assignee, status), activate/deactivate, soft delete. Lists and assignment are scoped to the viewer's reporting subtree (admins see everything; leads they created stay visible even when unassigned). |
  | **BSA Analyzer** | Full C# port of the Bank Statement Analyzer (`Backend/Bsa/`): upload a statement (PDF/CSV/XLSX/TXT, ≤ 25 MB) and optionally a CIBIL report (PDF/TXT, with password); the rule engine categorizes salary, PF, loan EMIs, disbursements, credit-card payments, app loans, bounces, rent, SIPs, insurance, utilities, stock-market flows and APY, builds monthly summaries/insights/risk flags, parses the CIBIL text (score, personal, enquiries, accounts, DPD, adverse flags), cross-verifies lenders vs the statement, and produces FOIR-based eligibility + underwriting grade. Tabbed preview with per-section tables, stat cards and one-click **.xlsx export**. Stateless — no DB persistence per request. |
| **Reports** | All pulled CIBIL/bureau reports; view/download the stored PDF (JWT-authorized stream). Grid columns include **Location** and **Center Manager** (the employee who pulled the report) plus **CIBIL Pull Date** and **CIBIL Pull Time** as separate sortable columns. **Export to Excel** writes the full filtered set with the date and time as real date/time cells, so Excel's own Data > Sort orders rows chronologically. Admin-only tiles show CIBIL portal pull count and, when the vendor publishes a balance endpoint (`Cibil:BalanceUrl`), the remaining credit balance. |
| **Generic Reports** | Port of CIPPortal's GenericReport module: report-type list from `USP_CIP_FILL_DROPDOWN`, runs `USP_GENERIC_REPORT` (all 10 MIS report SPs) for any month/year, paginated dynamic grid, and **real .xlsx export** (OpenXML writer, no Interop). |
| **Dialer Live** | Port of the DialerCount module: KPI tiles (total calls, active callers, connected, disbursed), calls-by-location bar chart, disposition donut, and a sortable/filterable per-caller table with totals row. Calls the legacy hierarchy-aware SP `USP_DIALER_LIVE_COUNT_REPORT @UserKey` (same as the portal's `CallLogLive`) — the SP itself restricts rows to the caller's reporting subtree, so every user sees only their team; the hourly chart runs for admins only. 60s auto-refresh with live pulse indicator. |
| **Menu Master** (admin) | Role → sidebar-menu visibility matrix over `TBL_CIP_MENU_MASTER` / `TBL_CIP_ROLE_MENU` (roles from `TBL_CIP_ROLE_MASTER`). The sidebar renders from `GET /api/menus` (role-filtered, static fallback if the API is down); saving replaces a role's grants atomically. Seeded so every active role sees everything except the Administration group (roles 1/2 only). |
| **Role & Privilege Master** (admin) | CRUD over `TBL_CIP_ROLE_MASTER` (rename, activate/deactivate, delete with employee-usage guard) plus a per-role privilege checklist of every sidebar menu. Built-in roles 1/2 (Super Admin / Admin) cannot be renamed, deactivated, or deleted; admin roles must keep at least one menu. Write endpoints are admin-only (`Admin` policy). |
| **Settings** | Placeholder. |

## Employee documents & offer letters

Every employee row opens a **Documents drawer**: upload PAN / Aadhaar / Bank / Photo / etc. (PDF, image, Word; 10 MB cap, extension whitelist), preview inline, download, or soft-delete. Generated **offer letters** are system-made PDFs (no Word/Interop dependency) built from the employee's master data — name, designation, location, DOJ, and annual CTC (`SALARY × 12`, Indian numbering + amount in words), mirroring the legacy `cv.docx` placeholders. They are stored as `SOURCE = 'OFFER'` documents alongside uploads in `TBL_CIP_EMPLOYEE_DOCUMENTS`, files under `Backend/employee documents/<EMP_CODE>/`. All document endpoints are hierarchy-scoped (403 outside your subtree).

## Hierarchy data scoping

Admins (ROLE_ID 1/2) see all data. Every other role sees only their **reporting subtree** (self + downline via `REPORTING_MANAGER_ID`, resolved by a cached recursive CTE in `HierarchyService`):

| Endpoint | Scoping |
|---|---|
| `GET /api/employees/master` | only subtree employees |
| `GET /api/employees/dids` (+ `stats`, `export`, `without-did`) | only subtree assignments (generic DID rows visible to all) |
| `GET /api/cibil/reports` | only reports pulled by self + downline |
| `GET /api/cibil/file/{id}` | 403 when the puller is outside the subtree |
| `GET /api/dialer/live` | scoped inside the legacy SP via `@UserKey` |
| `GET /api/dashboard` | cards + trend reflect the subtree |
| `GET /api/menus` | menus for the caller's role (Menu Master) |

## CIBIL pull counting

Monthly usage comes from **`TBL_CIP_CIBIL_PULL_LOG`**, not report rows; this captures cached usage under the requester and fixes the "0 / 10" quota bug.

| Event | Counts toward the monthly limit | Consumes a vendor credit |
|---|---|---|
| Fresh pull (`API`) or admin regenerate (`REGEN`) | yes | yes |
| Served from cache (`CACHE`) or legacy PDF (`LEGACY`) | yes | no |
| Vendor call failed (`STATUS='FAILED'`) | **yes (1)** | yes — failed calls are billed too |
| Duplicate blocked before a vendor call / PDF re-download | no | no |

Admins can see all reports and failed attempts. Regular users see the reports and attempts within their own reporting team; failure reasons remain admin-only.

A failed PAN is frozen globally for the rest of the month; only an admin can explicitly re-pull. Failed calls consume one requester credit; blocked repeats do not. Failure reasons are admin-only. See `Backend/sql/CIBIL-PULL-LOG.sql`, `Backend/sql/CIBIL-DEDUPE-FAILED.sql`, and `docs/CIBIL-REPORTS-SPEC.md` for details.

## Configuration

`CIPAdmin/Backend/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=CIP;User ID=sa;Password=123456;TrustServerCertificate=True;"
  },
  "Jwt": { "Key": "...", "Issuer": "CIPAdmin", "Audience": "CIPAdmin", "ExpireMinutes": 480 },
  "Cibil": {
    "ApiUrl": "https://api.thefinrep.com/v1/tu-cibil/pdf",
    "ApiToken": "<your x-api-token>",
    "MonthlyPullLimit": 50,   // regular users' monthly pull cap (admins exempt)
    "StorageFolder": "pdf files"
  }
}
```

> Set `Cibil:ApiToken` to your real token — it is sent as the `x-api-token` header upstream and never exposed to the browser.

## Run (development)

**1. Database** — one-time: execute `CIPAdmin/Database/schema.sql` against the `CIP` database
(`sqlcmd -S localhost -U sa -P 123456 -C -d CIP -i CIPAdmin\Database\schema.sql`).
Creates `TBL_CIP_EMPLOYEE_DID`, `TBL_CIP_EMPLOYEE_DID_HISTORY`, `TBL_CIP_CIBIL_REPORTS`, `TBL_CIP_AUDIT_LOG` (all SQL Server 2014-compatible) and seeds DIDs from existing employees. Existing tables are untouched (`IF NOT EXISTS` guards).

**2. Backend** (http://localhost:5000, Swagger at `/swagger`):

```bash
cd CIPAdmin/Backend
dotnet run
```

**3. Frontend** (http://localhost:5173, proxies `/api` → :5000):

```bash
cd CIPAdmin/Frontend
npm install
npm run dev
```

**Production:** `npm run build` emits `CIPAdmin/Frontend/dist` — serve it from IIS/static hosting and reverse-proxy `/api` to the Kestrel backend (or publish the backend to IIS with ASP.NET Core Hosting Bundle).

## API surface (Swagger)

| Method | Route | Notes |
|---|---|---|
| POST | `/api/auth/login` | `{username,password,rememberMe}` → `{valid,nextStep,user{...token}}`; `nextStep=require_otp/enroll_otp` for MFA |
| POST | `/api/auth/verify-otp` | `{username,code}` → JWT |
| GET | `/api/auth/session` | current user from JWT |
| GET | `/api/dashboard` | cards + trend + byType |
| POST | `/api/cibil/generate?regenerate=` | cache-first PAN logic + monthly quota + admin regen; `{source:CACHE\|API\|REGEN, report, downloadUrl, quota}`; 403 `{code:QUOTA_EXCEEDED, quota, existingReport, hasPendingRequest}` on limit reached; 409 `{code:DUPLICATE_PULL, lastAttempt, canRePull}` when this user's successful pull, any user's failed pull, or any user's in-flight PENDING pull blocks the PAN this month; only an admin can re-pull after failure; 403 `{code:RE-PULL_ADMIN_ONLY}` when a non-admin passes `regenerate=true` |
| POST | `/api/cibil/check` | `{pan,reportType}` → `{exists, report, lastAttempt, blocked, pulledByMe, canRePull}` (this month only; successful-pull popup state is per-user, but `lastAttempt`/`blocked` also reflect a global FAILED or in-flight PENDING attempt; `pulledByMe` gates the per-user download popup; `lastAttempt.errorMessage` is sent to admins only) |
| GET | `/api/cibil/quota` | current user's monthly quota `{used, limit, remaining, effectiveLimit, approvedLimit}` |
| POST | `/api/cibil/limit-request` | user requests a higher monthly limit `{requestedLimit, reason}` |
| GET | `/api/cibil/my-limit-requests` | the current user's own limit requests |
| GET | `/api/cibil/limit-requests?status=` | **admin**: all users' limit requests |
| POST | `/api/cibil/limit-requests/{id}/decide` | **admin**: `{approve, approvedLimit?}` — approval raises the user's effective cap for that month |
| GET | `/api/cibil/reports?search=&reportType=&from=&to=` | list |
| GET | `/api/cibil/download/{id}?download=false` | inline PDF stream (auth) |
| POST | `/api/generic-reports/run` | `{reportType,month,year}` → headers+rows of `USP_GENERIC_REPORT` |
| POST | `/api/generic-reports/export` | same input → real `.xlsx` download |
| GET | `/api/generic-reports/types` | report-type dropdown from `USP_CIP_FILL_DROPDOWN` |
| GET | `/api/dialer/live?start=&end=` | agent rows + totals + location/hourly slices for the live dialer screen |
| GET/POST/PUT/DELETE | `/api/employees/dids...` | DID CRUD (assign / change / release, history on change) |
| GET | `/api/employees/dids/stats` | DID page summary tiles |
| GET | `/api/employees/dids/export` | filtered DID list as .xlsx |
| GET | `/api/employees/designations` | designation filter dropdown |
| GET | `/api/employees/master` | server-paged employee master (`search,designationId,active,sortBy,sortDir,page,pageSize`) |
| GET/PUT/DELETE | `/api/company-categories...` | paged company list (`search,category,productId,bankId,sortBy,sortDir,page,pageSize`), edit, delete |
| GET | `/api/company-categories/banks\|products\|categories` | upload/filter dropdowns |
| POST | `/api/company-categories/upload` | multipart xlsx + productId + bankId → bulk insert (deduped) |
| GET | `/api/employees/without-did` | dropdown of employees without a DID |

## Notes

- Logins use the existing `TBL_CIP_EMPLOYEE_MASTER` rows (`USERNAME`/`PASSWORD`, `IS_BYPASS`/`IS_GOOGLE_AUTHENTICATED`/`GOOGLE_AUTH_SECRET_KEY`). **There is no global OTP on/off switch** — the per-employee `IS_BYPASS` flag is the single source of truth, so protecting one account never affects the other 10,000.
  - `IS_BYPASS = 1` → JWT issued immediately, no second factor.
  - `IS_BYPASS = 0` → authenticator mandatory. Enrolled users enter a 6-digit code; users who have never enrolled are shown a QR code first.
- The enrollment secret is written to the database **before** the QR is shown, because the code typed on the next screen is checked against the stored value. A pending-but-unconfirmed secret is **reused** on a later login rather than regenerated, so a page refresh cannot invalidate a QR the user has already scanned. An admin reset clears it and forces a fresh scan.
- TOTP secrets are 80-bit, Base32-encoded per RFC 6238 (16 characters), matching what Google Authenticator itself produces. Codes are accepted with a ±30 s window.
- The OTP step is throttled: 5 invalid codes for one account within 15 minutes locks it. A failure-counter error **fails closed** rather than allowing the step.
- PDFs are stored per pulling user: `CIPAdmin/Backend/pdf files/<UserName>/CIBIL_<NAME>_<PAN>_<timestamp>.pdf` (e.g. `pdf files/Sarfaraz_Liyaqat_Ali/…`); `FILE_PATH` in the DB is absolute. The layout is responsive with a hamburger drawer on mobile and a collapse-to-icon-rail toggle on desktop.
- Navigation is grouped: **Masters** (Employee Master, Employees DID, Company Category) and **Reports** (Dialer Live, Generic Reports, CIBIL Reports), with a Ctrl+K topbar search, breadcrumb bar, and a Need Help card in the sidebar. Brand logo lives at `Frontend/public/assets/logo.png` (source copy in `Frontend/assets/`).
- Authenticator tests: `Backend/sql/otp-selftest.sql` creates three throwaway employees covering every branch (bypass / enrolled / never-enrolled) and `Backend/sql/otp-e2e-test.py` drives them through a running backend, computing the TOTP codes locally so no phone is needed. Teardown: `sqlcmd … -v Remove=1 -i Backend/sql/otp-selftest.sql`.
