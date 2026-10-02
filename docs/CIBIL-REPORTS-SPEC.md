# Specification — CIBIL Reports Tab Enhancements

**System:** CIPAdmin (ASP.NET 8 Web API + React/Vite SPA, SQL Server `CIP`)
**Module:** CIBIL Report Fetcher / Reports
**Applies to:** Admin tab (`Reports` page, `GET /api/cibil/reports`) and TL tab (`CIBIL Report Fetcher`, `GET /api/cibil/quota`)
**Status:** Draft for review — no code written yet
**Date:** 2026-10-02

---

## 1. Overview

The CIBIL Reports screen today shows only *stored artefacts* (one row per PAN per month) and the TL quota chip counts only *fresh vendor pulls*. Two gaps follow from that:

1. **No operational context on a report row.** An admin looking at a pulled report cannot tell which location it came from, who at that location pulled it, or the exact date and time of the pull (date and time are collapsed into one timestamp cell).
2. **Counts are wrong and incomplete.** A TL who pulls the same PAN repeatedly gets a cached PDF, but their usage chip stays at `0 / 10`. The system also has no way to tell the business how many CIBIL credits the vendor portal has actually consumed.

This change adds four columns to the reports grid, adds a sortable Excel export, and introduces a **pull-event log** (`TBL_CIP_CIBIL_PULL_LOG`) that becomes the single source of truth for every CIBIL pull — fresh, cached, legacy, or regenerated. All counts on both tabs are derived from that log, which fixes the `0 / 10` bug as a side effect and makes the portal pull total reportable.

---

## 2. Detailed Requirements

### 2.1 Admin tab — Reports grid

| # | Requirement | Acceptance criteria |
|---|---|---|
| A1 | Add a **Location** column showing the location of the employee who pulled the report. | Every row shows a location name (e.g. `Andheri`, `Nalasopara 23`) or `—` when the puller is unknown (legacy imports). Value is snapshotted at pull time and does not change if the employee is later transferred. |
| A2 | Add a **Center Manager** column identifying who pulled the report. | Column header reads exactly `Center Manager`; value is the puller's employee name as of pull time. For legacy imports the value is `legacy-import`. See §3.5 for what this column can and cannot mean given the master data. |
| A3 | Add **CIBIL Pull Date** and **CIBIL Pull Time** as two separate columns. | `CIBIL Pull Date` renders as `dd-MMM-yyyy`; `CIBIL Pull Time` renders as `HH:mm:ss` (24-hour, server local time = IST). The single combined `Pulled On` column is removed. |
| A4 | Both new date/time columns are sortable client-side via the shared table header control. | Clicking `CIBIL Pull Date` sorts chronologically (not alphabetically); clicking again reverses. The same holds for `CIBIL Pull Time`. |
| A5 | Add an **Export to Excel** button that downloads the full filtered result set (all rows, not just the current page). | File is a valid `.xlsx` named `CIBIL_Reports_yyyy-MM-dd_HHmmss.xlsx`. All filters and the hierarchy scope in force on screen are applied. |
| A6 | The exported **CIBIL Pull Date** column is a real Excel date cell, so Excel's *Sort* command orders rows chronologically. | Selecting any cell in that column and choosing Data ▸ Sort A→Z produces date order. Sorting by `dd-MMM-yyyy` text does **not** produce alphabetical order. See §4. |
| A7 | Add a **CIBIL portal pull count** tile above the grid showing the total number of pulls made against the CIBIL vendor portal. | Tile shows an integer and a `as of <timestamp>` line. Admins only. Value equals the count of log rows with `SOURCE = 'API' or 'REGEN'`. |
| A8 | Add a **vendor credit balance** figure next to the pull-count tile, showing remaining CIBIL credits on the vendor portal. | If the vendor exposes a balance endpoint the value is live; otherwise the tile shows `Unavailable` and the pull count remains authoritative. See §2.4 and A-7 in Assumptions. |
| A9 | Existing filters (search, report type), hierarchy scope, pagination and View/Download actions are unchanged. | A non-admin sees the same new columns but only for their own subtree, exactly as today. |

### 2.2 TL tab — CIBIL Report Fetcher

| # | Requirement | Acceptance criteria |
|---|---|---|
| T1 | **Every** CIBIL pull consumes one unit of the monthly limit, including a pull served from cache. | A TL whose PAN is already on file for the current month, and who generates again, sees the quota chip go from `n / 10` to `n+1 / 10`. |
| T2 | The quota chip shows the **exact** number of pulls used this month. | The displayed count always equals the number of that employee's current-month pull-log rows. It can never read `0` after at least one pull has been made. |
| T3 | A TL's downloaded/generated reports are listed in the **reports section** of the TL tab. | A "My Reports" section renders below the preview with one row per report the TL has pulled or downloaded, most recent first: Name, PAN, Type, Source, CIBIL Pull Date, CIBIL Pull Time, and a Download action. |
| T4 | The TL reports section reflects both freshly pulled and cached/legacy-served reports. | After a cached pull the TL's row appears in the list with source `CACHE` (or `LEGACY`) and the original pull date/time. |
| T5 | The TL reports section is scoped to that TL only. | A TL never sees another employee's reports, regardless of reporting line. |
| T6 | The limit-request panel continues to open when the effective limit is reached, now with an accurate count. | Panel header shows the real used count at the moment it opens. |
| T7 | Admin users are not subject to the monthly cap. | Admin quota chip is hidden or shows `Unlimited`, consistent with today's behaviour. |

### 2.3 Behaviour that must not change

- One-PAN-per-calendar-month caching for `SOURCE='API'` rows (unique index `UQ_CIP_CIBIL_PAN_TYPE_MONTH`) stays as-is.
- Legacy rows (`SOURCE='LEGACY'`) keep serving from disk forever, for every user.
- Admin *Regenerate* still forces a fresh vendor pull and still replaces the month's row in place.
- Quota enforcement moves to the log, so the effective-limit calculation (`max(default, employee limit, approved request)`) is unchanged — only `used` changes.

### 2.4 Vendor balance (`Cibil:BalanceUrl`)

- New optional config key `Cibil:BalanceUrl`. If set, the API calls it on demand with the same `x-api-token` header and reads a numeric balance from the response.
- Call is made with a 10-second timeout, is **not** cached, and any failure is swallowed into an `available: false` response so the Reports page still renders.
- If the key is absent, the endpoint returns `available: false` and the UI shows `Unavailable`.
- Vendor confirmation of the actual balance endpoint and its response shape is **required before this tile can be marked done** (currently blocking — see Assumptions).

---

## 3. Data Fields

### 3.1 New table — `TBL_CIP_CIBIL_PULL_LOG` (pull-event ledger)

One row per CIBIL pull *event*. This is the counting source of truth; `TBL_CIP_CIBIL_REPORTS` remains the report/artefact store.

| Column | Type | Null | Description |
|---|---|---|---|
| `LOG_ID` | `INT IDENTITY(1,1)` | no | Primary key. |
| `REPORT_ID` | `INT` | yes | `TBL_CIP_CIBIL_REPORTS.REPORT_ID` that was served. NULL only if the vendor call failed. |
| `PAN_NO` | `VARCHAR(10)` | no | PAN requested, uppercase, no separators. |
| `REPORT_TYPE` | `VARCHAR(20)` | no | `cibil` / `experian` / `equifax` / `crif`. |
| `EMP_CODE` | `INT` | no | Employee who made the request. |
| `EMP_NAME` | `VARCHAR(200)` | no | Snapshot of the requester's name. |
| `LOCATION_ID` | `INT` | yes | Snapshot of `TBL_CIP_EMPLOYEE_MASTER.LOCATION_ID` at request time. |
| `LOCATION_NAME` | `VARCHAR(500)` | yes | Snapshot of `TBL_CIP_LOCATION_MASTER.LOC_NAME` at request time. |
| `PULLED_BY_NAME` | `VARCHAR(100)` | yes | Name recorded on the report row — the "who pulled" value shown in the grid. |
| `PULL_MONTH` | `VARCHAR(7)` | no | `CONVERT(VARCHAR(7), PULLED_DATE, 126)`. Quota window key. Persisted computed column. |
| `SOURCE` | `VARCHAR(10)` | no | `API` (fresh vendor pull) · `CACHE` (same-month reuse) · `LEGACY` (pre-migration PDF) · `REGEN` (admin forced refresh). |
| `PULLED_DATE` | `DATETIME` | no | Server timestamp of the event, `DEFAULT (GETDATE())`. |
| `IP_ADDRESS` | `VARCHAR(45)` | yes | Requesting client IP, for audit. |
| `USER_AGENT` | `VARCHAR(400)` | yes | Truncated user agent. |

Indexes:
- `IX_CIP_PULL_LOG_EMP_MONTH ON (EMP_CODE, PULL_MONTH)` — quota counts and the TL's own list.
- `IX_CIP_PULL_LOG_DATE ON (PULLED_DATE)` — admin date filters and portal totals.
- `IX_CIP_PULL_LOG_REPORT ON (REPORT_ID)` — report-row event history.

### 3.2 New columns on `TBL_CIP_CIBIL_REPORTS`

Added as **nullable** columns so existing rows and the persisted computed column keep working.

| Column | Type | Null | Description |
|---|---|---|---|
| `LOCATION_ID` | `INT` | yes | Location of the pulling employee at pull time. FK-shaped to `TBL_CIP_LOCATION_MASTER.LOC_ID`. |
| `LOCATION_NAME` | `VARCHAR(500)` | yes | Denormalised `LOC_NAME`; avoids a join per row and survives employee master edits. Width matches `TBL_CIP_LOCATION_MASTER.LOC_NAME varchar(500)`. |
| `CENTER_MANAGER` | `VARCHAR(200)` | yes | Explicit "Center Manager" label for the puller. Backfilled from `PULLED_BY_NAME`. See §3.5. |

`PULLED_DATE` (existing `DATETIME`) is split at the API layer into `pullDate` and `pullTime`; **no schema change** is needed for the separate date/time columns.

### 3.3 API response changes

`GET /api/cibil/reports` — each row gains:

| JSON field | Type | Source |
|---|---|---|
| `locationName` | `string \| null` | `LOCATION_NAME`, else `—` |
| `centerManager` | `string \| null` | `CENTER_MANAGER`, else `PULLED_BY_NAME` |
| `pullDate` | `string` (`yyyy-MM-dd`) | `PULLED_DATE` date part |
| `pullTime` | `string` (`HH:mm:ss`) | `PULLED_DATE` time part |

Existing fields (`reportId`, `panNo`, `applicantName`, `mobile`, `reportType`, `fileName`, `fileSize`, `pulledByName`, `pulledDate`, `source`) are unchanged, so current consumers keep working.

New endpoints:

| Method | Route | Purpose | Response |
|---|---|---|---|
| `GET` | `/api/cibil/reports/export` | Excel export, same filters/scope as `reports` | `.xlsx` stream |
| `GET` | `/api/cibil/portal-usage` | Admin only. Portal totals + vendor balance | `{ portalPulls, periodPulls, monthPulls, from, to, vendor: { available, balance, checkedOn } }` |
| `GET` | `/api/cibil/vendor-balance` | Admin only. On-demand vendor balance probe | `{ available, balance, message, checkedOn }` |
| `GET` | `/api/cibil/my-reports` | TL tab list of the caller's own pull events | `{ total, rows: [ { logId, reportId, panNo, applicantName, reportType, source, pullDate, pullTime, locationName, centerManager } ] }` |

`GET /api/cibil/quota` — `used` is redefined as the current-month log count for the caller (see §5). All other `QuotaInfo` fields are unchanged.

### 3.4 Excel column layout

| # | Column | Excel cell type | Sortable as |
|---|---|---|---|
| 1 | Report ID | Number | number |
| 2 | Applicant Name | Text | text |
| 3 | PAN | Text (never coerced — leading letters) | text |
| 4 | Mobile | Text | text |
| 5 | Report Type | Text | text |
| 6 | Source | Text | text |
| 7 | Location | Text | text |
| 8 | Center Manager | Text | text |
| 9 | CIBIL Pull Date | **Date serial, `dd-mmm-yyyy`** | **chronological** |
| 10 | CIBIL Pull Time | **Time serial, `hh:mm:ss`** | chronological |
| 11 | File Name | Text | text |
| 12 | File Size (bytes) | Number | number |

---

## 3.5 Master data findings — what "Center Manager" can resolve to

Verified directly against the `CIP` database (read-only queries, 2026-10-02). This section exists because the answer changes the design.

### The two master tables

`TBL_CIP_DESIGNATION_MASTER` — 16 rows:

| Column | Type | Notes |
|---|---|---|
| `DESIG_ID` | `int` | PK, not null |
| `DESIG_NAME` | `varchar(100)` | not null |
| `DESIG_ACTIVE` | `int` | not null |

`TBL_CIP_ROLE_MASTER` — 20 rows:

| Column | Type | Notes |
|---|---|---|
| `ROLE_ID` | `int` | PK, not null |
| `ROLE_NAME` | `varchar(100)` | not null |
| `ROLE_READ` | `varchar(100)` | not null |
| `ROLE_WRITE` | `varchar(100)` | not null |
| `ROLE_ACTIVE` | `int` | not null |

Both are already joined elsewhere (`AuthService`, `RolesController`, `MenuController`, `EmployeesController`), so either is safe to use in the reports query.

### Finding 1 — "Centre Manager" exists as a designation, but nobody holds it

`TBL_CIP_DESIGNATION_MASTER` contains `DESIG_ID = 4, DESIG_NAME = 'Centre Manager'`, `DESIG_ACTIVE = 1`.

**Zero employees** in `TBL_CIP_EMPLOYEE_MASTER` have `DESIG_ID = 4`. Active designation distribution:

| DESIG_ID | DESIG_NAME | Employees |
|---|---|---|
| 1014 | Connector | 8281 |
| 12 | TSO | 1895 |
| 11 | Team Leader | 120 |
| 8 | Pick Up Executive | 53 |
| 7 | Operations Executive | 45 |
| 14 | SubBroker | 13 |
| 1 | Founder | 7 |
| 10 | Support | 6 |
| 13 | Pickup Admin | 2 |
| 2 | Branch Head | 1 |
| — | *(null)* | 1 |

`Centre Manager` (4), `Calling Team Leader` (3), `Coordinator` (5), `FSE` (6), `Senior TSO` (9) are all defined and active but unstaffed.

**Consequence:** resolving "Center Manager" as `DESIG_NAME = 'Centre Manager'` yields `—` on every row. It cannot be implemented that way.

### Finding 2 — `CENTER_HEAD` exists as a role, but nobody holds it either

`TBL_CIP_ROLE_MASTER` contains `ROLE_ID = 1018, ROLE_NAME = 'CENTER_HEAD'`, active.

**Zero employees** have `ROLE_ID = 1018`. Active role distribution (excluding Connectors, 8281): `SUPER ADMIN` 1 → 8, `ADMIN` 2 → 0, `OPS` 3 → 36, `TL` 4 → 118, `CALLER` 5 → 1953, `HR` 6 → 3, `PICKUP ADMIN` 1006 → 2, `TL-OPS` 1007 → 3, `DATA ENTRY` 1008 → 4, `HL-BL-OPS` 1009 → 1, `SUBBROKER` 1010 → 13, plus a handful of unset/designation-only rows.

**Consequence:** `CENTER_HEAD` is a defined-but-unused role. Resolving by role is equally dead.

### Finding 3 — who actually pulls CIBIL

The pullers are the `TL` role. From the screenshot's own pull-limit table, the TLs are `Shanawaz Noor Mohammed Qureshi` (1016) and `Himanshu Rajendra Chaudhari` (54896). Resolved:

| EMP_CODE | Name | ROLE | Designation | Location | Reports to |
|---|---|---|---|---|---|
| 1016 | Shanawaz Noor Mohammed Qureshi | TL (4) | Team Leader (11) | Andheri (1) | Sarfaraz Liyaqat Ali (Founder) |
| 54896 | Himanshu Rajendra Chaudhari | TL (4) | Team Leader (11) | Nalasopara 23 (4010) | *(unset, 0)* |

Every `TL` is a **designation 11 = Team Leader**; 114 of 118. So the operational title for the person pulling the report is *Team Leader*, not *Centre Manager*.

### Finding 4 — the reporting chain cannot supply a "Centre Manager" either

Walking `REPORTING_MANAGER_ID` one level up from the TLs gives: **Team Leader 59, Founder 50, Branch Head 8, unset 1**. Reporting managers are overwhelmingly *other Team Leaders* and *Founders* — there is no layer of "Centre Manager" in the actual org data. For employee 54896 the manager is `0` (unset), so the lookup returns NULL.

And one location can have many reporting managers: **Andheri alone has 49 TLs spread across 12 distinct reporting managers**. So "the Centre Manager for location X" is not even a well-defined single value — it would have to be the manager of the *specific puller*, which is just "the reporting manager" under a different label.

### Finding 5 — there is no location→manager table

`TBL_CIP_LOCATION_MASTER` (37 rows) holds only `LOC_ID int`, `LOC_NAME varchar(500)`, `LOC_ACTIVE int`, `LOC_LAT float`, `LOC_LONG float`. There is **no** manager, head, or owner column. No location name contains "Centre" or "Branch". There is no separate centre/branch master table (searched `%LOCATION%`, `%CENTER%`, `%CENTRE%`; only `TBL_CIP_LOCATION_MASTER`, `TBL_LOCATION_TRACKING`, `TBL_CIP_ALLOCATION`, `TMP_ALLOCATION` exist, none of which link a location to a manager).

### Recommendation

Implement **A2 as the puller's own name**, labelled `Center Manager` in the UI, sourced from `PULLED_BY_NAME`:

- It is the only definition that is populated for every row.
- It matches the original request wording — "Center Manager (who pulled the report)" — where the parenthetical explicitly defines the column as *who pulled it*.
- It requires no guesswork about org structure and cannot return `—` except for legacy imports.

To make the *label* honest rather than misleading, the column header carries a tooltip: *"Centre Manager — the employee who pulled this CIBIL report."* If the business later staffs `DESIG_ID = 4` or `ROLE_ID = 1018`, the column can be switched to resolve the real designation with a one-line SQL change; the field name and type stay the same.

**This is the one item in the spec that needs a business decision before implementation** — see Assumption 2.

---

## 4. Export Functionality

**Mechanism.** Reuse the existing dependency-free `ExcelExporter.BuildXlsx(DataTable, sheetName)` in `Backend/Services/ExcelExporter.cs`, which already writes `DateTime` cells as OLE-automation serials with a date number format. The export is produced server-side (not in the browser) so the file contains the full filtered set, not the current page, and so non-admins cannot export rows outside their subtree.

**Why the date column must be a real date cell.** The current writer falls back to `t="inlineStr"` text for anything that is not a numeric or `DateTime` CLR value. Text dates sort alphabetically, so `01-Apr-2026` would sort after `31-Dec-2025`. To satisfy A6:

- The export builds a `DataTable` where the date column is typed `System.DateTime` and the time column is typed `System.TimeSpan` (or a `DateTime` with a time-only number format).
- `ExcelExporter` gains one new cell style — number format `21` (`hh:mm:ss`) alongside the existing `14` (`m/d/yyyy`) — applied to the time column so it renders as a time and not as a 1900 date.
- Header row keeps style `1`; the sheet already freezes row 1 and sets column widths.

**Sorting behaviour in Excel.**

- Excel's Data ▸ Sort is available on the whole range because every column is a uniform type per column.
- Sorting by `CIBIL Pull Date` orders rows oldest → newest (or reverse on second activation) using Excel's own date comparison, independent of the row order the server emitted.
- Because the grid is emitted newest-first, a user sorting ascending gets a clean chronological view with no re-interpretation needed.
- PAN and Mobile remain text so Excel does not strip leading zeros or convert PANs to scientific notation.
- Row 1 is frozen and `A1:L1` carries an auto-filter so the header dropdowns are available.

**Scope and limits.** Export honours the same `reportType` / `from` / `to` / `search` filters and the same hierarchy scope as the on-screen grid. Maximum 50,000 rows per export; beyond that the endpoint returns HTTP 413 with a message telling the user to narrow the date range.

---

## 5. Counting Logic

### 5.1 Definitions

- **Pull event** = one completed `POST /api/cibil/generate` that returned a report to the user, whether or not the vendor was called.
- **Portal pull** = a pull event with `SOURCE` in (`API`, `REGEN`) — these are the only events that consume vendor CIBIL credits.
- **Quota window** = the calendar month, keyed by the log row's `PULL_MONTH` (`yyyy-MM`, server local/IST).

### 5.2 Rules

| Situation | Log row written? | `SOURCE` | Counts toward TL monthly limit? | Consumes vendor credit? |
|---|---|---|---|---|
| Fresh vendor pull (PAN not on file this month) | yes | `API` | **yes (1)** | yes |
| Admin *Regenerate* of an existing PAN | yes | `REGEN` | **yes (1)** | yes |
| Same PAN, same month, served from cache | **yes** | `CACHE` | **yes (1)** | no |
| Same PAN, served from a legacy PDF | **yes** | `LEGACY` | **yes (1)** | no |
| Vendor call failed (error surfaced to user) | yes | `API` with `STATUS='FAILED'` and `REPORT_ID` NULL | **no** | attempt only |
| Rejected because the monthly limit was already reached (HTTP 403) | no | — | no | no |
| PDF viewed or downloaded again from the Reports grid | no (no new pull event) | — | no | no |

**Every successful pull counts once, cached or not.** A TL can therefore exhaust their own monthly allowance by repeatedly pulling PANs that are already on file — which is the intended behaviour in this requirement.

### 5.3 Where the log row is written

Inside `CibilService.GenerateAsync`, in a single place, immediately before each return path, via one private helper `RecordPullAsync(...)`. The order is deliberate: **log first, then return the response**, so a pull that succeeds is always counted even if the HTTP response is later lost. Failed vendor calls are logged with `STATUS='FAILED'` and excluded from quota counts (filter `STATUS = 'OK'`).

Because the log is written on every path, the existing rule "quota only counts fresh pulls, cached returns are free" is intentionally reversed. This is the only behavioural regression risk in the change and is called out in Assumptions.

### 5.4 Derived counts

| Figure | Query source |
|---|---|
| TL `used` | `COUNT(1) FROM TBL_CIP_CIBIL_PULL_LOG WHERE EMP_CODE = @e AND PULL_MONTH = @m AND STATUS='OK'` |
| Admin portal total (tile A7) | `COUNT(1) … WHERE SOURCE IN ('API','REGEN') AND STATUS='OK'` (optionally date-bounded) |
| Admin month / period totals | same, filtered on `PULL_MONTH` or `PULLED_DATE` |
| Reports-grid row count | unchanged — count of `TBL_CIP_CIBIL_REPORTS` rows in scope |

The quota check inside `GenerateAsync` moves from counting report rows to counting log rows; the rest of `QuotaInfo` (`limit`, `effectiveLimit`, `approvedLimit`, `remaining`) is computed exactly as today.

### 5.5 Backfill

Existing history has no log rows, so pre-deployment usage cannot be recovered exactly. On first deploy, a one-off script inserts a single `SOURCE='BACKFILL'` row per existing `TBL_CIP_CIBIL_REPORTS` row with `PULLED_BY` and `PULL_MONTH` in the current month, so the current month's numbers start from the true count rather than zero. Older months are deliberately not backfilled. `BACKFILL` is not a valid value for new writes and is treated as non-portal for counting.

---

## 6. Bug Fix — the `0 / 10` count

**Symptom.** A TL with a limit of 10 sees the quota chip read `0 / 10` even after generating CIBIL reports.

**Root cause.** `CibilService.GetQuotaAsync` computes:

```sql
SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS
WHERE PULLED_BY = @e AND PULL_MONTH = @m
```

`TBL_CIP_CIBIL_REPORTS` holds **one row per report artefact**, not per pull. Two consequences:

1. **A cached pull is invisible.** The first pull for a PAN inserts a row and counts. The second TL to pull the same PAN this month hits the cache path, returns early, and never touches the table — so their count stays at whatever it was, and for a TL whose only activity is cached pulls it stays at `0`.
2. **Counting is attributed to the artefact, not the requester.** Even for fresh pulls, a TL's count reflects the PANs *they* first pulled, not the pulls *they* performed. A TL whose PANs were all pulled by someone else legitimately shows `0`, and a TL who pulled one PAN five times shows `1`.

**Fix.** Quota counting moves to `TBL_CIP_CIBIL_PULL_LOG`, which records one row per pull event for the requesting employee, cached events included (§5.2). `GetQuotaAsync` counts log rows for the current employee and month; the same log feeds the admin portal total.

**Expected behaviour after the fix.**

- TL generates a fresh PAN → chip reads `1 / 10`.
- TL generates the same PAN again → chip reads `2 / 10`, and the response carries the refreshed `quota` object.
- TL generates a PAN already pulled by a colleague → chip reads `1 / 10` for the TL (it did not before).
- After 10 successful pulls the 11th request returns HTTP 403 with the accurate `used = 10` and opens the limit-request panel.
- The chip can no longer read `0` once any pull in the month has been made by that employee.
- A failed vendor call does not consume quota.

**Regression risk.** TLs who relied on unlimited cached pulls will start hitting their limit. Mitigations: the current-month backfill (§5.5) keeps their existing count visible, and the limit-request approval path already lets an admin raise an individual TL's cap.

---

## 7. Assumptions

1. **"Location" = the puller's location**, resolved from `TBL_CIP_EMPLOYEE_MASTER.LOCATION_ID` → `TBL_CIP_LOCATION_MASTER.LOC_NAME` and snapshotted onto the report row at pull time. This is the employee's work location, which is what an admin reviewing a pull needs. If the business instead means the *applicant's* location, that field does not exist anywhere in the system and would need a new data source. Note `LOC_NAME` is `varchar(500)` in the live table, so the snapshot column is sized to match.
2. **"Center Manager" = the employee who performed the pull** (i.e. `PULLED_BY_NAME`, relabelled), because the master data offers no alternative — see §3.5. Both candidate sources are verified **empty**: `DESIG_ID = 4 'Centre Manager'` has 0 employees and `ROLE_ID = 1018 'CENTER_HEAD'` has 0 employees. The one-level-up reporting manager is also unusable as a "Centre Manager": 59 TLs report to a Team Leader, 50 to a Founder, and one location (Andheri) has 12 different reporting managers across its 49 TLs, so "the Centre Manager for a location" is not a single well-defined value. **If the business intends a true supervisory Centre Manager, the master data must be populated first** — this is a blocking decision, not a code change.
3. **Server time is IST.** Pull date and time are rendered and exported in the server's local time; no timezone conversion is applied. A stored UTC column would be a separate change.
4. **Month boundaries follow the server clock**, matching the existing `PULL_MONTH` computed column and `CurrentMonthKey()`. No per-location or per-financial-month calendar.
5. **Pull Time is time-of-day only** (`HH:mm:ss`), with the date carried in CIBIL Pull Date. Sorting by time alone therefore orders within a day, not across the month.
6. **Vendor balance is unverified.** No confirmed balance/credit endpoint exists for the CIBIL vendor (`api.thefinrep.com`); the only known signal is the `422 insufficient wallet balance` error the API returns on an empty wallet. The `Cibil:BalanceUrl` design assumes a configurable URL and a response containing a number. Until the vendor confirms, the tile shows `Unavailable` and the admin relies on the portal pull count (§5.4), which measures *this system's* consumption, not the vendor's remaining balance. These two numbers are not interchangeable — a balance tile is only meaningful once the vendor endpoint is confirmed.
7. **Downloading a PDF from the Reports grid is not a pull** and is not counted. Only `POST /api/cibil/generate` counts. Re-downloading the same report 50 times therefore does not consume quota.
8. **A pull blocked by the limit is not counted**, so a user cannot inflate their own count by retrying.
9. **Export scope matches on-screen scope** — non-admins export only their subtree. There is no separate "export everything" path for non-admins.
10. **The log is append-only.** No deletion UI; retention is handled by a SQL Agent job or manual purge, out of scope here. A monthly purge older than 13 months is recommended once volume is understood.
11. **Row-count limits** — export capped at 50,000 rows; the reports grid keeps its existing client-side pagination.
12. **Legacy rows show `legacy-import` as Center Manager and `—` as Location**, since the pre-migration standalone tool recorded no pulling employee. The ~290 registered legacy rows have `PULLED_BY = NULL`, so a live join to `TBL_CIP_EMPLOYEE_MASTER` yields nothing for them; the snapshot columns stay NULL and the UI renders the em-dash.
13. **Master data was verified against the local `CIP` database only.** The CIBIL report rows (290 legacy rows, 348 PDFs) live on production, not locally, so the puller→location→designation joins in §3.5 were validated against `TBL_CIP_EMPLOYEE_MASTER` and the master tables rather than against actual CIBIL report rows. Production should be re-checked once, after deployment, with the same queries before assuming the new columns are populated.

---

## 8. Implementation Order (proposed)

1. Schema script `Database/CIBIL-PULL-LOG.sql` — create `TBL_CIP_CIBIL_PULL_LOG`, add the three columns to `TBL_CIP_CIBIL_REPORTS`, add the current-month backfill.
2. Backend: `RecordPullAsync` in `CibilService`, wired into all `GenerateAsync` return paths; `GetQuotaAsync` switched to the log.
3. Backend: `locationName` / `centerManager` / `pullDate` / `pullTime` on `CibilReportInfo` and the `reports` query (join `TBL_CIP_LOCATION_MASTER` where the snapshot is null; `centerManager` from `PULLED_BY_NAME` per §3.5).
4. Backend: `GET /api/cibil/reports/export` and the `ExcelExporter` time-style addition.
5. Backend: `GET /api/cibil/portal-usage`, `GET /api/cibil/vendor-balance`, `GET /api/cibil/my-reports`.
6. Frontend: Reports page columns, sortable headers, export button, usage tiles.
7. Frontend: TL tab quota chip refresh on every pull, plus the "My Reports" section.
8. Verify: the `0 / 10` scenario end-to-end, then Excel date-sort check, then prod SQL execution.
