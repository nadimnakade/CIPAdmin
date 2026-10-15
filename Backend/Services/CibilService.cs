using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using CIPAdmin.Backend.Models;

namespace CIPAdmin.Backend.Services;

public interface ICibilService
{
    Task<CibilReportInfo?> FindByPanAsync(string pan, string reportType = "cibil", bool currentMonthOnly = false);
    Task<CibilReportInfo?> FindLegacyByPanAsync(string pan, string reportType = "cibil");
    /// <summary>Latest pull ATTEMPT for a PAN+type in the current calendar month,
    /// whatever its outcome (OK / FAILED / in-flight PENDING). This is what the
    /// duplicate-pull freeze is decided on — see Backend/sql/CIBIL-DEDUPE-FAILED.sql.
    /// Pass empCode to see only that user's attempts (per-user popup/freeze state);
    /// a PENDING pull in flight by ANYONE still counts either way.</summary>
    Task<PullAttemptInfo?> FindLastAttemptAsync(string pan, string reportType = "cibil", int? empCode = null);
    Task<CibilGenerateResponse> GenerateAsync(CibilRequest request, int pulledBy, string pulledByName, bool isAdmin, bool regenerate, CancellationToken ct = default);
    Task<List<object>> QueryEmployeeLimitsAsync(string? search, string filter);
    Task<object> SetEmployeeLimitAsync(int empCode, int monthlyLimit, int setBy);
    Task ClearEmployeeLimitAsync(int empCode);
    /// <summary>Evict the cached default pull limit so an admin's change applies on the
    /// next quota read instead of up to 60 seconds later.</summary>
    void InvalidatePullLimitCache();
    Task<object> ImportLegacyFilesAsync();
    Task<List<CibilReportInfo>> ListAsync(string? reportType = null, DateTime? from = null, DateTime? to = null, string? search = null, int[]? scopedCodes = null);
    /// <summary>The caller's own pull events for the TL tab's "My Reports" list. Includes
    /// FAILED and PENDING attempts: a failed pull is still the caller's own record, and
    /// hiding it is what made a failed pull look like it "never happened".</summary>
    Task<List<MyPullRow>> ListMyPullsAsync(int empCode, int take = 100);
    /// <summary>Pull attempts that produced no report (FAILED / PENDING), scoped exactly
    /// like ListAsync: admins see all, everyone else sees their own plus their downline.</summary>
    Task<List<FailedPullRow>> ListFailedPullsAsync(int[]? scopedCodes, int viewerEmpCode, string? search = null, int take = 200);
    /// <summary>Admin: portal-wide CIBIL consumption plus the vendor credit balance.</summary>
    Task<PortalUsageInfo> GetPortalUsageAsync(DateTime? from = null, DateTime? to = null);
    /// <summary>Probe the vendor's balance endpoint. Never throws — reports availability instead.</summary>
    Task<VendorBalanceInfo> GetVendorBalanceAsync();
    Task<(byte[] bytes, CibilReportInfo report)?> GetFileAsync(int reportId);
    Task<int?> GetPulledByAsync(int reportId);
    /// <summary>True when the pull ledger already records this report as SERVED to the
    /// given employee. GenerateAsync writes an OK row for every user it hands a saved
    /// report to — including a requester who did not pull the PAN themselves — so this
    /// is the record of "the checker gave you this file". The download endpoint
    /// authorises on it: see CibilController.Download.</summary>
    Task<bool> WasReportServedToAsync(int reportId, int empCode);
    string StorageFolderPath();
    Task<QuotaInfo> GetQuotaAsync(int empCode, string empName);
    Task<LimitRequestInfo> SubmitLimitRequestAsync(int empCode, string empName, LimitRequestInput input);
    Task<List<LimitRequestInfo>> GetMyLimitRequestsAsync(int empCode);
    Task<List<LimitRequestInfo>> GetAllLimitRequestsAsync(string? status = null);
    Task<List<LimitRequestInfo>> GetSubtreeLimitRequestsAsync(int[] subtreeCodes, string? status = null);
    Task<bool> IsRequestInScopeAsync(int requestId, int[] subtreeCodes);
    Task<LimitRequestInfo> DecideLimitRequestAsync(int requestId, bool approve, int decidedBy, string decidedByName, int? approvedLimit);
}

/// <summary>HTTP 403: the user hit their monthly pull cap. Carries quota + any cached PAN report
/// so the client can show the existing PDF and a limit-request form.</summary>
public class QuotaExceededException : Exception
{
    public QuotaInfo Quota { get; }
    public CibilReportInfo? ExistingReport { get; }
    public bool HasPendingRequest { get; }
    public QuotaExceededException(string message, QuotaInfo quota, CibilReportInfo? existing = null, bool hasPendingRequest = false)
        : base(message) { Quota = quota; ExistingReport = existing; HasPendingRequest = hasPendingRequest; }
}

/// <summary>HTTP 409: admin regeneration blocked because the saved PDF for this PAN/month is missing on disk.</summary>
public class RegenerateBlockedException : Exception
{
    public CibilReportInfo Report { get; }
    public RegenerateBlockedException(string message, CibilReportInfo report) : base(message) { Report = report; }
}

/// <summary>HTTP 409: the PAN already has a pull attempt this month — whatever its
/// outcome — and no fresh pull may be made without an admin re-pull. A failed vendor
/// call leaves no report row, so without this freeze every retry hit the vendor and
/// burned a credit. Carries the blocking attempt so the client can show when/by whom
/// the attempt was made, and (admins only) why it failed.</summary>
public class DuplicatePullBlockedException : Exception
{
    public PullAttemptInfo Attempt { get; }
    public DuplicatePullBlockedException(string message, PullAttemptInfo attempt) : base(message)
    { Attempt = attempt; }
}

/// <summary>HTTP 403: a re-pull (regenerate=true) was requested by a non-admin.
/// Re-pulls are admin-only irrespective of the previous pull's status.</summary>
public class RePullForbiddenException : Exception
{
    public RePullForbiddenException(string message) : base(message) { }
}

public class CibilService : ICibilService
{
    private readonly IDbContext _db;
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<CibilService> _logger;
    private readonly IMemoryCache _memoryCache;

    public CibilService(IDbContext db, HttpClient http, IConfiguration config, IMemoryCache memoryCache, ILogger<CibilService> logger)
    {
        _db = db; _http = http; _config = config; _memoryCache = memoryCache; _logger = logger;
    }

    public string StorageFolderPath()
    {
        var folder = _config["Cibil:StorageFolder"] ?? "pdf files";
        return Path.IsPathRooted(folder) ? folder : Path.Combine(AppContext.BaseDirectory, folder);
    }

    /// <summary>Each pulling user gets their own subfolder under the storage root:
    /// pdf files/&lt;UserName&gt;/CIBIL_xxx.pdf</summary>
    public string UserStorageFolderPath(string userName)
    {
        var safe = Regex.Replace((userName ?? "").Trim(), @"[^\w\-]", "_");
        if (safe.Length == 0) safe = "Unknown";
        var path = Path.Combine(StorageFolderPath(), safe);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void EnsureStorageFolder(IConfiguration config)
    {
        var folder = config["Cibil:StorageFolder"] ?? "pdf files";
        var path = Path.IsPathRooted(folder) ? folder : Path.Combine(AppContext.BaseDirectory, folder);
        Directory.CreateDirectory(path);
    }

    /// <summary>Default monthly pull limit: the DB-configured value (editable by
    /// admins on the Settings page) falling back to Cibil:MonthlyPullLimit, then 50.
    /// Cached for 60s so frequent quota checks don't hammer the settings table.</summary>
    private async Task<int> BaseMonthlyLimitAsync()
    {
        var key = "cibil-pull-limit";
        if (_memoryCache.TryGetValue(key, out int cached) && cached > 0) return cached;
        try
        {
            var v = await _db.ScalarAsync(
                "SELECT SETTING_VALUE FROM dbo.TBL_CIP_APP_SETTINGS WHERE SETTING_KEY = 'Cibil.MonthlyPullLimit'");
            if (v != null && v != DBNull.Value && int.TryParse(v.ToString(), out var n) && n > 0)
            {
                _memoryCache.Set(key, n, TimeSpan.FromSeconds(60));
                return n;
            }
        }
        catch { /* settings table missing — fall back to config */ }
        return int.TryParse(_config["Cibil:MonthlyPullLimit"], out var cfg) && cfg > 0 ? cfg : 50;
    }

    /// <summary>Drop the cached default pull limit so an admin's change is visible on the
    /// very next quota read instead of up to 60 seconds later. Called after the setting
    /// or a per-employee override is written.</summary>
    public void InvalidatePullLimitCache() => _memoryCache.Remove("cibil-pull-limit");

    private static string CurrentMonthKey() => DateTime.Now.ToString("yyyy-MM");

    // ------------------------------------------------------------------ cache

    public async Task<CibilReportInfo?> FindByPanAsync(string pan, string reportType = "cibil", bool currentMonthOnly = false)
    {
        pan = (pan ?? "").Trim().ToUpperInvariant();
        if (pan.Length == 0) return null;

        var monthClause = currentMonthOnly ? " AND PULL_MONTH = @m" : "";
        var dt = await _db.QueryAsync($@"
            SELECT TOP 1 REPORT_ID, PAN_NO, APPLICANT_NAME, MOBILE, REPORT_TYPE,
                   FILE_NAME, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE
            FROM dbo.TBL_CIP_CIBIL_REPORTS
            WHERE PAN_NO = @pan AND REPORT_TYPE = @type{monthClause}
            ORDER BY PULLED_DATE DESC",
            p =>
            {
                p.AddWithValue("@pan", pan);
                p.AddWithValue("@type", reportType ?? "cibil");
                if (currentMonthOnly) p.AddWithValue("@m", CurrentMonthKey());
            });

        return dt.Rows.Count == 0 ? null : MapRow(dt.Rows[0]);
    }

    /// <summary>Latest report for a PAN whose SOURCE = 'LEGACY' (standalone-era PDF
    /// registered by the import). Legacy reports serve from disk forever — they never
    /// age out of the per-month cache rule; only an admin Regenerate re-pulls.</summary>
    public async Task<CibilReportInfo?> FindLegacyByPanAsync(string pan, string reportType = "cibil")
    {
        pan = (pan ?? "").Trim().ToUpperInvariant();
        if (pan.Length == 0) return null;

        var dt = await _db.QueryAsync($@"
            SELECT TOP 1 REPORT_ID, PAN_NO, APPLICANT_NAME, MOBILE, REPORT_TYPE,
                   FILE_NAME, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE
            FROM dbo.TBL_CIP_CIBIL_REPORTS
            WHERE PAN_NO = @pan AND REPORT_TYPE = @type AND SOURCE = 'LEGACY'
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.TBL_CIP_CIBIL_REPORTS newer
                  WHERE newer.PAN_NO = TBL_CIP_CIBIL_REPORTS.PAN_NO
                    AND newer.REPORT_TYPE = TBL_CIP_CIBIL_REPORTS.REPORT_TYPE
                    AND newer.PULLED_DATE > TBL_CIP_CIBIL_REPORTS.PULLED_DATE)
            ORDER BY PULLED_DATE DESC",
            p =>
            {
                p.AddWithValue("@pan", pan);
                p.AddWithValue("@type", reportType ?? "cibil");
            });

        return dt.Rows.Count == 0 ? null : MapRow(dt.Rows[0]);
    }

    /// <summary>The most recent pull attempt for a PAN+type in the current calendar
    /// month, whatever its outcome. The old freeze only looked at report rows, which
    /// exist solely after a SUCCESSFUL pull — failed attempts were written to the
    /// ledger (STATUS='FAILED') but never read back, so each retry cost a vendor credit.
    /// Month-scoped: the freeze lasts until the calendar month rolls over.
    ///
    /// Successful history is user-scoped to preserve the existing popup behavior.
    /// FAILED attempts are a global freeze because the vendor charges for failed calls
    /// too; an in-flight PENDING attempt also blocks globally. If a user has no personal
    /// attempt, return the most recent global FAILED/PENDING row so Check shows the
    /// exact global freeze/re-pull state before Generate is submitted.</summary>
    public async Task<PullAttemptInfo?> FindLastAttemptAsync(string pan, string reportType = "cibil", int? empCode = null)
    {
        pan = (pan ?? "").Trim().ToUpperInvariant();
        if (pan.Length == 0) return null;

        // A failed attempt is a global vendor-credit freeze for this PAN+type this
        // month: every user sees it and must not spend another vendor credit retrying.
        // Successful history remains user-scoped for the existing popup behavior; an
        // in-flight request is always global.
        var dt = await FindAttemptRowAsync(pan, reportType, empCode);
        if (dt.Rows.Count == 0)
            dt = await FindAttemptRowAsync(pan, reportType, null, failedOnly: true);
        if (dt.Rows.Count == 0 && empCode.HasValue)
            dt = await FindAttemptRowAsync(pan, reportType, null, pendingOnly: true);
        if (dt.Rows.Count == 0) return null;
        var r = dt.Rows[0];
        return new PullAttemptInfo
        {
            LogId = Convert.ToInt32(r["LOG_ID"]),
            PanNo = r["PAN_NO"].ToString() ?? "",
            ReportType = r["REPORT_TYPE"].ToString() ?? "cibil",
            Status = r["STATUS"].ToString() ?? "",
            Source = r["SOURCE"].ToString() ?? "",
            RequestedBy = r["EMP_NAME"] == DBNull.Value ? null : r["EMP_NAME"].ToString(),
            PulledDate = Convert.ToDateTime(r["PULLED_DATE"]),
            ErrorMessage = r["ERROR_MESSAGE"] == DBNull.Value ? null : r["ERROR_MESSAGE"].ToString(),
        };
    }

    /// <summary>Latest attempt row this month for a PAN+type. Optionally restrict to
    /// one user's rows, failed rows (global cross-user freeze), or in-flight PENDING
    /// rows (global concurrency guard).</summary>
    private async Task<DataTable> FindAttemptRowAsync(string pan, string reportType, int? empCode,
        bool pendingOnly = false, bool failedOnly = false)
    {
        var where = "PAN_NO = @pan AND REPORT_TYPE = @type AND PULL_MONTH = @m";
        if (empCode.HasValue) where += " AND EMP_CODE = @e";
        if (pendingOnly) where += " AND STATUS = 'PENDING'";
        if (failedOnly) where += " AND STATUS = 'FAILED'";

        return await _db.QueryAsync($@"
            SELECT TOP 1 LOG_ID, PAN_NO, REPORT_TYPE, STATUS, SOURCE, EMP_NAME, PULLED_DATE, ERROR_MESSAGE
            FROM dbo.TBL_CIP_CIBIL_PULL_LOG
            WHERE {where}
            ORDER BY PULLED_DATE DESC, LOG_ID DESC",
            p =>
            {
                p.AddWithValue("@pan", pan);
                p.AddWithValue("@type", reportType ?? "cibil");
                p.AddWithValue("@m", CurrentMonthKey());
                if (empCode.HasValue) p.AddWithValue("@e", empCode.Value);
            });
    }

    private static CibilReportInfo MapRow(DataRow r) => new()
    {
        ReportId = Convert.ToInt32(r["REPORT_ID"]),
        PanNo = r["PAN_NO"].ToString()!,
        ApplicantName = r["APPLICANT_NAME"].ToString(),
        Mobile = r["MOBILE"].ToString(),
        ReportType = r["REPORT_TYPE"].ToString()!,
        FileName = r["FILE_NAME"].ToString()!,
        FileSize = r["FILE_SIZE"] == DBNull.Value ? null : Convert.ToInt64(r["FILE_SIZE"]),
        PulledByName = r["PULLED_BY_NAME"].ToString(),
        PulledDate = Convert.ToDateTime(r["PULLED_DATE"]),
        Source = r["SOURCE"].ToString()!,
        Status = r.Table.Columns.Contains("STATUS") ? r["STATUS"].ToString() ?? "OK" : "OK",
        // These two columns are absent from the narrower selects (FindByPan /
        // GetFileAsync), so read them defensively.
        LocationName = r.Table.Columns.Contains("LOCATION_NAME") ? NullIfBlank(r["LOCATION_NAME"]) : null,
        CenterManager = r.Table.Columns.Contains("CENTER_MANAGER") ? NullIfBlank(r["CENTER_MANAGER"]) : null
    };

    private static string? NullIfBlank(object? v)
    {
        var s = v?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private async Task<string?> GetFilePathAsync(int reportId)
    {
        var v = await _db.ScalarAsync(
            "SELECT FILE_PATH FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE REPORT_ID = @id",
            p => p.AddWithValue("@id", reportId));
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    private static void TryDeleteFile(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best effort — old file may be open in a viewer */ }
    }

    // ------------------------------------------------- employee limits (admin)

    /// <summary>Employees with their per-employee monthly pull limits (admins only).
    /// Returns employees matching the search; NULL limit = on the default.
    /// Connector-role/designation employees are hidden.</summary>
    public async Task<List<object>> QueryEmployeeLimitsAsync(string? search, string filter)
    {
        var dt = await _db.QueryAsync($@"
            SELECT TOP 200 e.EMP_CODE, e.EMP_NAME, e.ROLE_ID,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   l.MONTHLY_LIMIT, l.SET_ON
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT l ON l.EMP_CODE = e.EMP_CODE
            WHERE {EmployeeFilters.Active("e")}
              AND {EmployeeFilters.NotDeleted("e")}
              AND {EmployeeFilters.NotConnector("e")}
            {filter}
            ORDER BY CASE WHEN l.EMP_CODE IS NULL THEN 1 ELSE 0 END, e.EMP_NAME",
            p =>
            {
                if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            });

        var defaultLimit = await BaseMonthlyLimitAsync();
        return dt.AsEnumerable().Select(r => (object)new
        {
            empCode = r.Field<int>("EMP_CODE"),
            empName = r["EMP_NAME"].ToString(),
            roleId = r["ROLE_ID"] == DBNull.Value ? 0 : r.Field<int>("ROLE_ID"),
            roleName = r["ROLE_NAME"].ToString(),
            monthlyLimit = r["MONTHLY_LIMIT"] == DBNull.Value ? null : (int?)r.Field<int>("MONTHLY_LIMIT"),
            // Backend-driven: what this employee actually gets. An override replaces the
            // default outright (see GetQuotaAsync) so lowering a cap takes effect too.
            effectiveLimit = r["MONTHLY_LIMIT"] == DBNull.Value ? defaultLimit : r.Field<int>("MONTHLY_LIMIT"),
            setOn = r["SET_ON"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(r["SET_ON"])
        }).ToList();
    }

    public async Task<object> SetEmployeeLimitAsync(int empCode, int monthlyLimit, int setBy)
    {
        var eligible = await _db.ScalarAsync(
            "SELECT CASE WHEN " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e")
                + " AND " + EmployeeFilters.NotConnector("e") + " THEN 1 ELSE 0 END FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
        if (eligible == null || eligible == DBNull.Value || Convert.ToInt32(eligible) != 1)
            throw new InvalidOperationException("Active non-connector employee not found.");

        await _db.ExecuteAsync(@"
            MERGE dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT AS t
            USING (SELECT @e AS EMP_CODE) AS s ON t.EMP_CODE = s.EMP_CODE
            WHEN MATCHED THEN UPDATE SET MONTHLY_LIMIT = @l, SET_BY = @by, SET_ON = GETDATE()
            WHEN NOT MATCHED THEN INSERT (EMP_CODE, MONTHLY_LIMIT, SET_BY) VALUES (@e, @l, @by);",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@l", monthlyLimit); p.AddWithValue("@by", setBy); });

        InvalidatePullLimitCache();
        return new { empCode, monthlyLimit, saved = true };
    }

    public async Task ClearEmployeeLimitAsync(int empCode)
    {
        var eligible = await _db.ScalarAsync(
            "SELECT CASE WHEN " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e")
                + " AND " + EmployeeFilters.NotConnector("e") + " THEN 1 ELSE 0 END FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
        if (eligible == null || eligible == DBNull.Value || Convert.ToInt32(eligible) != 1)
            throw new InvalidOperationException("Active non-connector employee not found.");

        await _db.ExecuteAsync(
            "DELETE FROM dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT WHERE EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
        InvalidatePullLimitCache();
    }

    /// <summary>One-time legacy import: scan the storage folder tree for PDFs whose
    /// names follow  CIBIL[_&lt;name&gt;]_&lt;PAN&gt;_YYYY-MM-dd...  and register any PAN not
    /// already in TBL_CIP_CIBIL_REPORTS, so cache lookups serve them instead of
    /// re-pulling from the vendor. Files stay where they are.</summary>
    public Task<object> ImportLegacyFilesAsync()
    {
        var root = StorageFolderPath();
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Storage folder not found: {root}");

        // File names: CIBIL_deepak_parashar_ANPPP0898D_2026-09-21-...pdf
        var rx = new Regex(@"CIBIL.*?_([A-Za-z]{5}[0-9]{4}[A-Za-z])_(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = _db.QueryAsync(
            "SELECT PAN_NO FROM dbo.TBL_CIP_CIBIL_REPORTS",
            _ => { }).GetAwaiter().GetResult();
        foreach (DataRow r in existing.Rows) known.Add(r["PAN_NO"].ToString()!.ToUpperInvariant());

        int imported = 0, skippedNoPattern = 0, skippedKnown = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories))
        {
            var m = rx.Match(Path.GetFileName(file));
            if (!m.Success) { skippedNoPattern++; continue; }
            var pan = m.Groups[1].Value.ToUpperInvariant();
            if (known.Contains(pan)) { skippedKnown++; continue; }

            var pulled = DateTime.TryParse(m.Groups[2].Value, out var d) ? d : File.GetLastWriteTime(file);
            var fi = new FileInfo(file);
            var name = Path.GetFileNameWithoutExtension(file);
            // CIBIL_<name>_<PAN>_...  → extract a display name if present
            var withoutPrefix = name.StartsWith("CIBIL_", StringComparison.OrdinalIgnoreCase) ? name[6..] : name;
            var display = withoutPrefix.Substring(0, withoutPrefix.LastIndexOf('_') > 0 ? withoutPrefix.LastIndexOf('_') : withoutPrefix.Length)
                                     .Replace('_', ' ');

            _db.ExecuteAsync(@"
                INSERT INTO dbo.TBL_CIP_CIBIL_REPORTS
                    (PAN_NO, APPLICANT_NAME, REPORT_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE)
                VALUES
                    (@pan, @name, 'cibil', @file, @path, @size, 'legacy-import', @date, 'LEGACY');",
                p =>
                {
                    p.AddWithValue("@pan", pan);
                    p.AddWithValue("@name", (object?)display ?? DBNull.Value);
                    p.AddWithValue("@file", fi.Name);
                    p.AddWithValue("@path", file);
                    p.AddWithValue("@size", fi.Length);
                    p.AddWithValue("@date", pulled);
                }).GetAwaiter().GetResult();
            known.Add(pan);
            imported++;
        }
        return Task.FromResult<object>(new { imported, skippedNoPattern, skippedKnown,
            skipped = skippedNoPattern + skippedKnown, root });
    }

    // ------------------------------------------------------------------ quota

    /// <summary>Monthly pull quota for a user: base limit raised by any APPROVED
    /// limit-request for the current month; counts this month's pulls.</summary>
    public async Task<QuotaInfo> GetQuotaAsync(int empCode, string empName)
    {
        var baseLimit = await BaseMonthlyLimitAsync();
        var month = CurrentMonthKey();

        // Count completed pull events, not report rows. TBL_CIP_CIBIL_REPORTS is one
        // row per PAN per month, so counting it made cached pulls invisible and
        // attributed usage to whoever first pulled a PAN — the "0 / 10" bug. The ledger
        // has one row per request; both OK and FAILED vendor calls consume one quota
        // credit (the vendor charges for either outcome). PENDING and rejected requests
        // do not count because the vendor call has not completed / was never made.
        var usedV = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_PULL_LOG WHERE EMP_CODE = @e AND PULL_MONTH = @m AND STATUS IN ('OK','FAILED')",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@m", month); });
        var used = Convert.ToInt32(usedV ?? 0);

        var approvedV = await _db.ScalarAsync(@"
            SELECT MAX(APPROVED_LIMIT) FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            WHERE EMP_CODE = @e AND REQUEST_MONTH = @m AND STATUS = 'Approved' AND APPROVED_LIMIT IS NOT NULL",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@m", month); });
        var approved = approvedV == null || approvedV == DBNull.Value ? 0 : Convert.ToInt32(approvedV);

        // Admin-assigned per-employee limit (TBL_CIP_EMPLOYEE_CIBIL_LIMIT) wins over
        // the default; an approved limit request still raises it further if larger.
        var empLimitV = await _db.ScalarAsync(
            "SELECT MONTHLY_LIMIT FROM dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT WHERE EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
        var empLimit = empLimitV == null || empLimitV == DBNull.Value ? 0 : Convert.ToInt32(empLimitV);
        // An explicit per-employee override REPLACES the default, in both directions.
        // It used to be Math.Max(baseLimit, empLimit), which meant an admin could only
        // ever raise someone's cap — setting it BELOW the default silently did nothing
        // and the employee kept the default limit.
        var floorLimit = empLimit > 0 ? empLimit : baseLimit;

        var effective = Math.Max(floorLimit, approved);
        var pendingV = await _db.ScalarAsync(@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            WHERE EMP_CODE = @e AND REQUEST_MONTH = @m AND STATUS = 'Pending'",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@m", month); });

        return new QuotaInfo
        {
            Used = used,
            Limit = empLimit > 0 ? empLimit : baseLimit,
            EffectiveLimit = effective,
            ApprovedLimit = approved > floorLimit ? approved.ToString() : null,
            // The service doesn't know the caller's role, so this is the raw
            // "out of allowance" signal. The controller clears it for admins,
            // who are never quota-blocked.
            Blocked = used >= effective,
        };
    }

    private async Task<bool> HasPendingLimitRequestAsync(int empCode)
    {
        var v = await _db.ScalarAsync(@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            WHERE EMP_CODE = @e AND REQUEST_MONTH = @m AND STATUS = 'Pending'",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@m", CurrentMonthKey()); });
        return Convert.ToInt32(v ?? 0) > 0;
    }

    // ------------------------------------------------------- limit requests

    public async Task<LimitRequestInfo> SubmitLimitRequestAsync(int empCode, string empName, LimitRequestInput input)
    {
        if (input.RequestedLimit <= 0)
            throw new InvalidOperationException("Requested limit must be a positive number.");

        var quota = await GetQuotaAsync(empCode, empName);
        if (input.RequestedLimit <= quota.EffectiveLimit)
            throw new InvalidOperationException($"Your effective limit is already {quota.EffectiveLimit} pulls this month — request a higher number.");

        if (await HasPendingLimitRequestAsync(empCode))
            throw new InvalidOperationException("You already have a pending request for this month. Wait for the administrator's decision.");

        var newId = await _db.ScalarAsync(@"
            INSERT INTO dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS (EMP_CODE, EMP_NAME, REQUEST_MONTH, CURRENT_COUNT, REQUESTED_LIMIT, REASON)
            OUTPUT INSERTED.REQUEST_ID
            VALUES (@e, @n, @m, @cnt, @lim, @reason);",
            p =>
            {
                p.AddWithValue("@e", empCode);
                p.AddWithValue("@n", (object?)empName ?? DBNull.Value);
                p.AddWithValue("@m", CurrentMonthKey());
                p.AddWithValue("@cnt", quota.Used);
                p.AddWithValue("@lim", input.RequestedLimit);
                p.AddWithValue("@reason", string.IsNullOrWhiteSpace(input.Reason) ? DBNull.Value : input.Reason.Trim());
            });

        return new LimitRequestInfo
        {
            RequestId = Convert.ToInt32(newId),
            EmpCode = empCode,
            EmpName = empName,
            RequestMonth = CurrentMonthKey(),
            CurrentCount = quota.Used,
            RequestedLimit = input.RequestedLimit,
            Reason = input.Reason,
            Status = "Pending",
            CreatedDate = DateTime.Now
        };
    }

    public async Task<List<LimitRequestInfo>> GetMyLimitRequestsAsync(int empCode)
    {
        var dt = await _db.QueryAsync(
            "SELECT * FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS WHERE EMP_CODE = @e ORDER BY CREATED_DATE DESC",
            p => p.AddWithValue("@e", empCode));
        return MapRequests(dt);
    }

    public async Task<List<LimitRequestInfo>> GetAllLimitRequestsAsync(string? status = null)
    {
        var sql = "SELECT * FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS WHERE 1 = 1";
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            sql += " AND STATUS = @s";
        sql += " ORDER BY CASE WHEN STATUS = 'Pending' THEN 0 ELSE 1 END, CREATED_DATE DESC";
        var bind = new Action<SqlParameterCollection>(p =>
        {
            if (!string.IsNullOrWhiteSpace(status) && status != "All") p.AddWithValue("@s", status.Trim());
        });
        var dt = await _db.QueryAsync(sql, bind);
        return MapRequests(dt);
    }

    /// <summary>Limit requests filed by the caller's subtree (self + downline) — for TLs.</summary>
    public async Task<List<LimitRequestInfo>> GetSubtreeLimitRequestsAsync(int[] subtreeCodes, string? status = null)
    {
        if (subtreeCodes.Length == 0) return new List<LimitRequestInfo>();
        var sql = $@"SELECT * FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            WHERE EMP_CODE IN ({HierarchyService.InlineCodes(subtreeCodes)})";
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            sql += " AND STATUS = @s";
        sql += " ORDER BY CASE WHEN STATUS = 'Pending' THEN 0 ELSE 1 END, CREATED_DATE DESC";
        var bind = new Action<SqlParameterCollection>(p =>
        {
            if (!string.IsNullOrWhiteSpace(status) && status != "All") p.AddWithValue("@s", status.Trim());
        });
        var dt = await _db.QueryAsync(sql, bind);
        return MapRequests(dt);
    }

    /// <summary>Whether a limit request belongs to someone inside the given subtree.</summary>
    public async Task<bool> IsRequestInScopeAsync(int requestId, int[] subtreeCodes)
    {
        if (subtreeCodes.Length == 0) return false;
        var v = await _db.ScalarAsync($@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            WHERE REQUEST_ID = @id AND EMP_CODE IN ({HierarchyService.InlineCodes(subtreeCodes)})",
            p => p.AddWithValue("@id", requestId));
        return Convert.ToInt32(v) > 0;
    }

    public async Task<LimitRequestInfo> DecideLimitRequestAsync(int requestId, bool approve, int decidedBy, string decidedByName, int? approvedLimit)
    {
        var dt0 = await _db.QueryAsync(
            "SELECT REQUESTED_LIMIT, STATUS FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS WHERE REQUEST_ID = @id",
            p => p.AddWithValue("@id", requestId));
        if (dt0.Rows.Count == 0)
            throw new InvalidOperationException("Request not found.");
        if (dt0.Rows[0]["STATUS"].ToString() != "Pending")
            throw new InvalidOperationException("This request has already been decided.");

        // Approving without an override approves the requested limit.
        var effectiveApproved = approve ? approvedLimit ?? Convert.ToInt32(dt0.Rows[0]["REQUESTED_LIMIT"]) : 0;
        if (approve && effectiveApproved <= 0)
            throw new InvalidOperationException("Approved limit must be a positive number.");

        var n = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS
            SET STATUS = @st,
                APPROVED_LIMIT = @al,
                DECIDED_BY = @by,
                DECIDED_BY_NAME = @byname,
                DECIDED_DATE = GETDATE()
            WHERE REQUEST_ID = @id AND STATUS = 'Pending'",
            p =>
            {
                p.AddWithValue("@st", approve ? "Approved" : "Rejected");
                p.AddWithValue("@al", effectiveApproved);
                p.AddWithValue("@by", decidedBy);
                p.AddWithValue("@byname", (object?)decidedByName ?? DBNull.Value);
                p.AddWithValue("@id", requestId);
            });
        if (n == 0) throw new InvalidOperationException("Request not found or already decided.");

        var dt = await _db.QueryAsync("SELECT * FROM dbo.TBL_CIP_CIBIL_LIMIT_REQUESTS WHERE REQUEST_ID = @id",
            p => p.AddWithValue("@id", requestId));
        return MapRequests(dt)[0];
    }

    private static List<LimitRequestInfo> MapRequests(DataTable dt)
    {
        var list = new List<LimitRequestInfo>();
        foreach (DataRow r in dt.Rows)
        {
            list.Add(new LimitRequestInfo
            {
                RequestId = Convert.ToInt32(r["REQUEST_ID"]),
                EmpCode = Convert.ToInt32(r["EMP_CODE"]),
                EmpName = r["EMP_NAME"].ToString() ?? "",
                RequestMonth = r["REQUEST_MONTH"].ToString() ?? "",
                CurrentCount = Convert.ToInt32(r["CURRENT_COUNT"]),
                RequestedLimit = Convert.ToInt32(r["REQUESTED_LIMIT"]),
                Reason = r["REASON"].ToString(),
                Status = r["STATUS"].ToString() ?? "Pending",
                ApprovedLimit = r["APPROVED_LIMIT"] == DBNull.Value ? null : Convert.ToInt32(r["APPROVED_LIMIT"]),
                DecidedByName = r["DECIDED_BY_NAME"].ToString(),
                DecidedDate = r["DECIDED_DATE"] == DBNull.Value ? null : Convert.ToDateTime(r["DECIDED_DATE"]),
                CreatedDate = Convert.ToDateTime(r["CREATED_DATE"])
            });
        }
        return list;
    }

    // -------------------------------------------------------------- generate

    /// <summary>Work location of an employee, snapshotted onto the report row at pull
    /// time. Cached briefly because every pull and every quota check resolves it.</summary>
    private async Task<(int? id, string? name)> EmployeeLocationAsync(int empCode)
    {
        var dt = await _db.QueryAsync(@"
            SELECT e.LOCATION_ID, l.LOC_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER l ON l.LOC_ID = e.LOCATION_ID
            WHERE e.EMP_CODE = @e AND " + EmployeeFilters.Active("e")
                + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@e", empCode));

        if (dt.Rows.Count == 0) return (null, null);
        var id = dt.Rows[0]["LOCATION_ID"] == DBNull.Value
            ? (int?)null : Convert.ToInt32(dt.Rows[0]["LOCATION_ID"]);
        var name = dt.Rows[0]["LOC_NAME"].ToString();
        return (id, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>
    /// Records ONE pull event in TBL_CIP_CIBIL_PULL_LOG. This is the counting source of
    /// truth for every CIBIL usage figure on both tabs.
    ///
    /// Called on every GenerateAsync return path — fresh, cached, legacy and regenerated
    /// pulls all count, because the requirement is that a TL's monthly allowance is
    /// consumed whether or not the vendor was called. The alternative (the old
    /// behaviour, counting report rows) is what produced the "0 / 10" bug: a cached pull
    /// never inserted a report row, so it was invisible, and a TL whose PANs had all
    /// been pulled by colleagues legitimately showed 0.
    ///
    /// Best-effort by design: a failure here must never break a successful pull, so
    /// exceptions are logged and swallowed. The consequence is an undercount, which is
    /// far safer than failing a user's report.
    /// </summary>
    private async Task RecordPullAsync(int? reportId, string pan, string reportType,
        int empCode, string empName, string source, string status = "OK")
    {
        try
        {
            var (locId, locName) = await EmployeeLocationAsync(empCode);
            await _db.ExecuteAsync(@"
                INSERT INTO dbo.TBL_CIP_CIBIL_PULL_LOG
                    (REPORT_ID, PAN_NO, REPORT_TYPE, EMP_CODE, EMP_NAME,
                     LOCATION_ID, LOCATION_NAME, PULLED_BY_NAME, SOURCE, STATUS, PULLED_DATE)
                VALUES
                    (@reportId, @pan, @type, @emp, @empname,
                     @locId, @locName, @empname, @source, @status, GETDATE())",
                p =>
                {
                    p.AddWithValue("@reportId", (object?)reportId ?? DBNull.Value);
                    p.AddWithValue("@pan", pan);
                    p.AddWithValue("@type", reportType);
                    p.AddWithValue("@emp", empCode);
                    p.AddWithValue("@empname", (object?)empName ?? DBNull.Value);
                    p.AddWithValue("@locId", (object?)locId ?? DBNull.Value);
                    p.AddWithValue("@locName", (object?)locName ?? DBNull.Value);
                    p.AddWithValue("@source", source);
                    p.AddWithValue("@status", status);
                });

            // Keep the report row's snapshot columns in step with the ledger.
            if (reportId.HasValue)
                await UpdateReportSnapshotAsync(reportId.Value, locId, locName, empName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record CIBIL pull event for PAN {Pan} (emp {Emp})", pan, empCode);
        }
    }

    /// <summary>Snapshots the requester's location onto the report row so the grid keeps
    /// showing where a report came from after the employee is transferred. Throws —
    /// callers are the ledger writers, which wrap everything in their own
    /// best-effort catch so bookkeeping can never fail a pull.</summary>
    private async Task UpdateReportSnapshotAsync(int reportId, int? locId, string? locName, string? empName)
    {
        await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_CIBIL_REPORTS
            SET LOCATION_ID = @locId, LOCATION_NAME = @locName,
                CENTER_MANAGER = ISNULL(CENTER_MANAGER, @empname)
            WHERE REPORT_ID = @id",
            p =>
            {
                p.AddWithValue("@locId", (object?)locId ?? DBNull.Value);
                p.AddWithValue("@locName", (object?)locName ?? DBNull.Value);
                p.AddWithValue("@empname", (object?)empName ?? DBNull.Value);
                p.AddWithValue("@id", reportId);
            });
    }

    /// <summary>Opens the pull ledger row BEFORE the vendor is called, with
    /// STATUS='PENDING'. The unique filtered index UX_CIP_PULL_INFLIGHT turns this
    /// into a database-level guarantee that only one vendor call per PAN can be in
    /// flight, so two concurrent Generate clicks cannot both spend a credit.
    ///
    /// Returns the LOG_ID for FinalizePullAsync, or null when the ledger could not be
    /// written — best-effort by design, like every other ledger write here: a pull is
    /// never failed over bookkeeping. The one exception is losing the in-flight race
    /// (SQL 2601/2627), which is a real duplicate and is reported as such.</summary>
    private async Task<int?> BeginPullAsync(string pan, string reportType, int empCode, string empName, string source)
    {
        try
        {
            // Retire a PENDING row a crashed/restarted process left behind: it holds
            // the in-flight slot and would otherwise freeze the PAN for good. The
            // reason text is best-effort on its own so a database still missing
            // ERROR_MESSAGE (migration not yet run) degrades instead of failing.
            try
            {
                await _db.ExecuteAsync(@"
                    UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG
                       SET ERROR_MESSAGE = COALESCE(ERROR_MESSAGE, 'Pull attempt interrupted (server restart or timeout).')
                     WHERE PAN_NO = @pan AND REPORT_TYPE = @type AND STATUS = 'PENDING'
                       AND PULLED_DATE < DATEADD(MINUTE, -15, GETDATE())",
                    p => { p.AddWithValue("@pan", pan); p.AddWithValue("@type", reportType); });
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Could not record interruption reason for stale PENDING CIBIL pull on PAN {Pan}", pan);
            }
            await _db.ExecuteAsync(@"
                UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG
                   SET STATUS = 'FAILED'
                 WHERE PAN_NO = @pan AND REPORT_TYPE = @type AND STATUS = 'PENDING'
                   AND PULLED_DATE < DATEADD(MINUTE, -15, GETDATE())",
                p => { p.AddWithValue("@pan", pan); p.AddWithValue("@type", reportType); });

            var (locId, locName) = await EmployeeLocationAsync(empCode);
            var newId = await _db.ScalarAsync(@"
                INSERT INTO dbo.TBL_CIP_CIBIL_PULL_LOG
                    (REPORT_ID, PAN_NO, REPORT_TYPE, EMP_CODE, EMP_NAME,
                     LOCATION_ID, LOCATION_NAME, PULLED_BY_NAME, SOURCE, STATUS, PULLED_DATE)
                OUTPUT INSERTED.LOG_ID
                VALUES
                    (NULL, @pan, @type, @emp, @empname,
                     @locId, @locName, @empname, @source, 'PENDING', GETDATE())",
                p =>
                {
                    p.AddWithValue("@pan", pan);
                    p.AddWithValue("@type", reportType);
                    p.AddWithValue("@emp", empCode);
                    p.AddWithValue("@empname", (object?)empName ?? DBNull.Value);
                    p.AddWithValue("@locId", (object?)locId ?? DBNull.Value);
                    p.AddWithValue("@locName", (object?)locName ?? DBNull.Value);
                    p.AddWithValue("@source", source);
                });
            return Convert.ToInt32(newId);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // A concurrent request already owns the in-flight slot for this PAN.
            var attempt = await FindLastAttemptAsync(pan, reportType);
            throw new DuplicatePullBlockedException(
                $"A CIBIL pull for PAN {pan} is already in progress. Wait for it to finish before trying again.",
                attempt ?? new PullAttemptInfo
                {
                    PanNo = pan, ReportType = reportType, Status = "PENDING",
                    Source = source, RequestedBy = empName, PulledDate = DateTime.Now
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open CIBIL pull ledger row for PAN {Pan} (emp {Emp})", pan, empCode);
            return null;
        }
    }

    /// <summary>Closes the row BeginPullAsync opened: OK + the report served (plus the
    /// location snapshot), or FAILED + the vendor's error text. Both completed outcomes
    /// count against the requester's monthly allowance; rejected duplicates and pending
    /// calls do not. Best-effort.</summary>
    private async Task FinalizePullAsync(int? logId, int? reportId, int empCode, string empName, string status, string? error)
    {
        try
        {
            if (logId.HasValue)
            {
                await _db.ExecuteAsync(@"
                    UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG
                       SET STATUS = @status, REPORT_ID = @reportId
                     WHERE LOG_ID = @id",
                    p =>
                    {
                        p.AddWithValue("@status", status);
                        p.AddWithValue("@reportId", (object?)reportId ?? DBNull.Value);
                        p.AddWithValue("@id", logId.Value);
                    });

                if (!string.IsNullOrWhiteSpace(error))
                {
                    // Separate statement: a database that has not run
                    // CIBIL-DEDUPE-FAILED.sql yet loses the reason text, not the status.
                    try
                    {
                        await _db.ExecuteAsync(@"
                            UPDATE dbo.TBL_CIP_CIBIL_PULL_LOG
                               SET ERROR_MESSAGE = @err
                             WHERE LOG_ID = @id",
                            p =>
                            {
                                p.AddWithValue("@err", Truncate(error, 500));
                                p.AddWithValue("@id", logId.Value);
                            });
                    }
                    catch (SqlException ex)
                    {
                        _logger.LogWarning(ex, "Could not store CIBIL failure reason for log {LogId} (run CIBIL-DEDUPE-FAILED.sql)", logId.Value);
                    }
                }
            }

            if (reportId.HasValue)
            {
                var (locId, locName) = await EmployeeLocationAsync(empCode);
                await UpdateReportSnapshotAsync(reportId.Value, locId, locName, empName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize CIBIL pull ledger row {LogId}", logId);
        }
    }

    /// <summary>The user-facing block message for a duplicate pull. The vendor's error
    /// text and the "use Re-pull" hint are appended only for admins — the failure
    /// reason is admin-only. Public so the check endpoint reports the exact same
    /// wording the generate endpoint would throw.</summary>
    public static string DuplicateBlockMessage(PullAttemptInfo a, bool isAdmin)
    {
        var when = a.PulledDate.ToString("dd MMM yyyy 'at' HH:mm");
        var by = string.IsNullOrWhiteSpace(a.RequestedBy) ? "another user" : a.RequestedBy;
        var msg = a.Status switch
        {
            "PENDING" => $"A CIBIL pull for PAN {a.PanNo} is already in progress ({when}, requested by {by}). " +
                         "Wait for it to finish — duplicate attempts consume vendor credits.",
            "FAILED" => $"A CIBIL pull for PAN {a.PanNo} was already attempted on {when} by {by} and failed. " +
                        "Duplicate attempts consume vendor credits, so this PAN is frozen for the rest of the month — " +
                        "only an administrator can re-pull it.",
            _ => $"A CIBIL report for PAN {a.PanNo} was already pulled on {when} by {by}. " +
                 "This PAN is frozen for the rest of the month — only an administrator can re-pull it."
        };
        if (!isAdmin) return msg;
        var admin = " Use the Re-pull action to try again.";
        if (a.Status == "FAILED" && !string.IsNullOrWhiteSpace(a.ErrorMessage))
            admin += $" Last error: {a.ErrorMessage}";
        return msg + admin;
    }

    public async Task<CibilGenerateResponse> GenerateAsync(CibilRequest request, int pulledBy, string pulledByName, bool isAdmin, bool regenerate, CancellationToken ct = default)
    {
        // Strip spaces/dashes so "AAABP0002A " or "AAAB-P0-002A" still validates as 10 chars.
        var pan = Regex.Replace((request.PanCard ?? "").Trim().ToUpperInvariant(), @"[\s\-]", "");
        request.PanCard = pan;
        var reportType = string.IsNullOrWhiteSpace(request.ReportType) ? "cibil" : request.ReportType.Trim().ToLowerInvariant();

        if (pan.Length < 10 || !Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
            throw new InvalidOperationException("A valid 10-character PAN (e.g. AHAPN0789J) is required.");

        // ---------- RULE 0: a re-pull is an ADMIN-ONLY action, whatever the previous
        // attempt's status. The controller already strips regenerate for non-admins;
        // refusing loudly here means the service layer cannot be bypassed by a future
        // endpoint either, and the user is told why instead of silently getting cache.
        if (regenerate && !isAdmin)
            throw new RePullForbiddenException(
                "Only administrators can re-pull a CIBIL report for a PAN, irrespective of the previous pull's status.");

        // ---------- RULE 2b: LEGACY reports (standalone-era PDFs) serve from disk
        // forever, regardless of month. Only an admin Regenerate re-pulls these.
        var legacyReport = await FindLegacyByPanAsync(pan, reportType);
        if (legacyReport != null && !(isAdmin && regenerate))
        {
            _logger.LogInformation("CIBIL for PAN {Pan} is a legacy report ({Date}) — serving saved PDF (no API call).", pan, legacyReport.PulledDate);
            // A legacy serve is a pull event: it counts against the monthly limit.
            await RecordPullAsync(legacyReport.ReportId, pan, reportType, pulledBy, pulledByName, "LEGACY");
            return new CibilGenerateResponse
            {
                Source = "LEGACY",
                Report = legacyReport,
                DownloadUrl = $"/api/cibil/download/{legacyReport.ReportId}",
                Message = $"Saved report for PAN {pan} " +
                          $"({legacyReport.PulledDate:dd MMM yyyy} by {legacyReport.PulledByName}). Showing the stored report — admins can regenerate for fresh data.",
                Quota = await GetQuotaAsync(pulledBy, pulledByName)
            };
        }

        // ---------- RULE 3: a PAN can be pulled ONCE PER CALENDAR MONTH by ANY user.
        // Any subsequent request for that PAN in the same month is served the saved
        // PDF from the folder — the vendor API is not called. Admins can force a
        // fresh pull via regenerate=true.
        var existingReport = await FindByPanAsync(pan, reportType, currentMonthOnly: true);
        if (existingReport != null && !(isAdmin && regenerate))
        {
            _logger.LogInformation("CIBIL for PAN {Pan} already pulled {Date} this month — serving saved PDF (no API call).", pan, existingReport.PulledDate);
            // Served from cache, but still a pull: count it as 1 against the limit.
            // Logged BEFORE the quota snapshot below so the response reports the
            // post-pull count rather than the pre-pull one.
            await RecordPullAsync(existingReport.ReportId, pan, reportType, pulledBy, pulledByName, "CACHE");
            return new CibilGenerateResponse
            {
                Source = "CACHE",
                Report = existingReport,
                DownloadUrl = $"/api/cibil/download/{existingReport.ReportId}",
                Message = $"A CIBIL report for PAN {pan} was already generated this month " +
                          $"({existingReport.PulledDate:dd MMM yyyy} by {existingReport.PulledByName}). Showing the saved report.",
                Quota = await GetQuotaAsync(pulledBy, pulledByName)
            };
        }

        // ---------- RULE 4: do not make a paid vendor call for a PAN already attempted
        // this month. User-owned OK events keep the duplicate-popup behavior; FAILED
        // and PENDING attempts block everyone. An explicit admin re-pull bypasses this
        // freeze deliberately and its result is recorded/charged like every vendor call.
        var attempt = await FindLastAttemptAsync(pan, reportType, pulledBy);
        if (attempt != null && !(isAdmin && regenerate))
        {
            _logger.LogInformation(
                "Duplicate CIBIL pull for PAN {Pan} blocked (last attempt {Status} on {At} by {By}, requester admin: {Admin}).",
                pan, attempt.Status, attempt.PulledDate, attempt.RequestedBy, isAdmin);
            throw new DuplicatePullBlockedException(DuplicateBlockMessage(attempt, isAdmin), attempt);
        }

        // ---------- RULE 1: monthly pull quota for regular users.
        // Admins bypass the quota; each completed call still writes a ledger event.
        var quota = await GetQuotaAsync(pulledBy, pulledByName);
        if (!isAdmin && quota.Used >= quota.EffectiveLimit)
        {
            var existing = await FindByPanAsync(pan, reportType, currentMonthOnly: true);
            _logger.LogInformation("User {User} hit the monthly CIBIL limit ({Used}/{Limit}).", pulledByName, quota.Used, quota.EffectiveLimit);
            throw new QuotaExceededException(
                $"Monthly pull limit reached ({quota.Used}/{quota.EffectiveLimit}). Submit a request to the administrator to raise your limit.",
                quota, existing, await HasPendingLimitRequestAsync(pulledBy));
        }

        if (existingReport != null && isAdmin && regenerate)
        {
            // ---------- Admin regeneration — pull fresh, replace the existing row
            // (whatever month it came from), and delete the old file only after the
            // new pull has succeeded.
            _logger.LogInformation("Admin {User} regenerating CIBIL for PAN {Pan} (report {Id}).", pulledByName, pan, existingReport.ReportId);
            var oldPath = await GetFilePathAsync(existingReport.ReportId);

            // Open the ledger row first: STATUS='PENDING' holds the in-flight slot so a
            // concurrent request cannot double-spend, and a failure below is recorded
            // with the vendor's reason instead of vanishing unlogged.
            var regenLogId = await BeginPullAsync(pan, reportType, pulledBy, pulledByName, "REGEN");
            byte[] regenBytes;
            try
            {
                regenBytes = await PullFromApiAsync(request, ResolveApiToken(request), ct);
            }
            catch (Exception ex)
            {
                await FinalizePullAsync(regenLogId, null, pulledBy, pulledByName, "FAILED", ex.Message);
                throw;
            }
            var regenFileName = BuildFileName(request.Name, pan);
            var regenPath = Path.Combine(UserStorageFolderPath(pulledByName), regenFileName);
            await System.IO.File.WriteAllBytesAsync(regenPath, regenBytes, ct);

            await _db.ExecuteAsync(@"
                UPDATE dbo.TBL_CIP_CIBIL_REPORTS
                SET APPLICANT_NAME = @name, MOBILE = @mobile, GENDER = @gender,
                    FILE_NAME = @file, FILE_PATH = @path, FILE_SIZE = @size,
                    PULLED_BY = @by, PULLED_BY_NAME = @byname, PULLED_DATE = GETDATE(), SOURCE = 'REGEN'
                WHERE REPORT_ID = @id",
                p =>
                {
                    p.AddWithValue("@name", (object?)request.Name ?? DBNull.Value);
                    p.AddWithValue("@mobile", (object?)request.Mobile ?? DBNull.Value);
                    p.AddWithValue("@gender", (object?)request.Gender ?? DBNull.Value);
                    p.AddWithValue("@file", regenFileName);
                    p.AddWithValue("@path", regenPath);
                    p.AddWithValue("@size", regenBytes.Length);
                    p.AddWithValue("@by", pulledBy);
                    p.AddWithValue("@byname", (object?)pulledByName ?? DBNull.Value);
                    p.AddWithValue("@id", existingReport.ReportId);
                });
            TryDeleteFile(oldPath);

            _logger.LogInformation("Admin regeneration for PAN {Pan} saved as {File} (row {Id} replaced).", pan, regenFileName, existingReport.ReportId);
            await FinalizePullAsync(regenLogId, existingReport.ReportId, pulledBy, pulledByName, "OK", null);
            return new CibilGenerateResponse
            {
                Source = "REGEN",
                Report = new CibilReportInfo
                {
                    ReportId = existingReport.ReportId,
                    PanNo = pan,
                    ApplicantName = request.Name,
                    Mobile = request.Mobile,
                    ReportType = reportType,
                    FileName = regenFileName,
                    FileSize = regenBytes.Length,
                    PulledByName = pulledByName,
                    PulledDate = DateTime.Now,
                    Source = "REGEN"
                },
                DownloadUrl = $"/api/cibil/download/{existingReport.ReportId}",
                Message = "Report regenerated by administrator — the previous file for this month was replaced.",
                // Re-read: the pull event recorded above is not in the earlier snapshot.
                Quota = await GetQuotaAsync(pulledBy, pulledByName)
            };
        }

        // ---------- Fresh pull (first attempt for this PAN this month).
        // The ledger row opens BEFORE the vendor call: PENDING marks the in-flight
        // slot (UX_CIP_PULL_INFLIGHT), then is finalized to OK or FAILED below.
        var pullLogId = await BeginPullAsync(pan, reportType, pulledBy, pulledByName,
            isAdmin && regenerate ? "REGEN" : "API");
        byte[] bytes;
        try
        {
            bytes = await PullFromApiAsync(request, ResolveApiToken(request), ct);
        }
        catch (Exception ex)
        {
            // Ledger the failure with the vendor's error text — this globally freezes
            // the PAN for the rest of the month, and consumes one monthly quota credit
            // because the vendor charges for failed calls too.
            await FinalizePullAsync(pullLogId, null, pulledBy, pulledByName, "FAILED", ex.Message);
            throw;
        }

        var fileName = BuildFileName(request.Name, pan);
        var folder = UserStorageFolderPath(pulledByName);
        var filePath = Path.Combine(folder, fileName);
        await System.IO.File.WriteAllBytesAsync(filePath, bytes, ct);

        var (reportId, source) = await InsertReportAsync(request, pan, reportType, fileName, filePath, bytes.Length, pulledBy, pulledByName);

        // An admin re-pull of a PAN whose only history this month is a FAILED attempt
        // lands here — the REGEN branch above requires an existing report row, which a
        // failed pull never created. Label the artefact REGEN too, so the Reports grid
        // and the ledger agree on which pulls an administrator forced.
        if (source == "API" && isAdmin && regenerate)
        {
            await _db.ExecuteAsync(
                "UPDATE dbo.TBL_CIP_CIBIL_REPORTS SET SOURCE = 'REGEN' WHERE REPORT_ID = @id",
                p => p.AddWithValue("@id", reportId));
            source = "REGEN";
        }

        // source is CACHE when a concurrent request won the race for this PAN+month;
        // this request still called the vendor, so the PENDING row is finalized OK
        // (SOURCE stays as BeginPull recorded it) and counts exactly once.
        await FinalizePullAsync(pullLogId, reportId, pulledBy, pulledByName, "OK", null);

        _logger.LogInformation("CIBIL report for PAN {Pan} pulled from API and saved as {File}.", pan, fileName);

        return new CibilGenerateResponse
        {
            Source = source,
            Report = new CibilReportInfo
            {
                ReportId = reportId,
                PanNo = pan,
                ApplicantName = request.Name,
                Mobile = request.Mobile,
                ReportType = reportType,
                FileName = fileName,
                FileSize = bytes.Length,
                PulledByName = pulledByName,
                PulledDate = DateTime.Now,
                Source = source
            },
            DownloadUrl = $"/api/cibil/download/{reportId}",
            Message = source == "CACHE"
                ? $"A concurrent pull for PAN {pan} just saved this report — serving the saved file."
                : source == "REGEN"
                    ? "Report re-pulled by administrator."
                    : "Report generated successfully.",
            Quota = await GetQuotaAsync(pulledBy, pulledByName)
        };
    }

    private string ResolveApiToken(CibilRequest request) =>
        string.IsNullOrWhiteSpace(request.Token) ? _config["Cibil:ApiToken"] ?? "" : request.Token.Trim();

    private async Task<byte[]> PullFromApiAsync(CibilRequest request, string apiToken, CancellationToken ct)
    {
        if (apiToken.Length == 0)
            throw new InvalidOperationException("CIBIL API token is not configured (Cibil:ApiToken in appsettings.json).");

        var url = _config["Cibil:ApiUrl"] ?? "https://api.thefinrep.com/v1/tu-cibil/pdf";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("x-api-token", apiToken);
        req.Content = JsonContent.Create(new
        {
            name = request.Name,
            mobile = request.Mobile,
            pan_card = request.PanCard.Trim().ToUpperInvariant(),
            gender = request.Gender,
            report_type = request.ReportType,
            consent = request.Consent
        });

        using var res = await _http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsByteArrayAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(raw);
            throw new InvalidOperationException($"CIBIL API returned {(int)res.StatusCode}: {Truncate(body, 300)}");
        }

        // Case 1: raw PDF body
        if (raw.Length > 4 && raw[0] == 0x25 && raw[1] == 0x50 && raw[2] == 0x44 && raw[3] == 0x46) // "%PDF"
            return raw;

        var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("pdf"))
            return raw;

        // Case 2: JSON wrapping base64 or a URL
        var json = System.Text.Encoding.UTF8.GetString(raw);
        var (type, value) = ExtractPdfFromJson(json);
        switch (type)
        {
            case PdfExtractType.Base64:
                return Convert.FromBase64String(StripDataUri(value!));
            case PdfExtractType.Url:
                {
                    using var fileRes = await _http.GetAsync(value, ct);
                    fileRes.EnsureSuccessStatusCode();
                    return await fileRes.Content.ReadAsByteArrayAsync(ct);
                }
            default:
                throw new InvalidOperationException("No PDF data found in the CIBIL API response. Raw body: " + Truncate(json, 300));
        }
    }

    private enum PdfExtractType { None, Base64, Url }

    private static (PdfExtractType type, string? value) ExtractPdfFromJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return (PdfExtractType.None, null); }

        using (doc)
        {
            string[] keys = { "pdf", "data", "result", "file_url", "url", "download_url", "file", "content" };

            JsonElement? FindValue(JsonElement el, string key)
            {
                if (el.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in el.EnumerateObject())
                {
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        return p.Value;
                }
                return null;
            }

            foreach (var key in keys)
            {
                var v = FindValue(doc.RootElement, key);
                if (v == null) continue;
                var s = v.Value.GetString() ?? "";
                if (LooksLikeBase64(s)) return (PdfExtractType.Base64, s);
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (PdfExtractType.Url, s);
            }

            // nested { data: { pdf | base64 | content: "..." } }
            var dataEl = FindValue(doc.RootElement, "data");
            if (dataEl != null && dataEl.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var inner in new[] { "pdf", "base64", "content", "data" })
                {
                    var v = FindValue(dataEl.Value, inner);
                    if (v != null && LooksLikeBase64(v.Value.GetString() ?? ""))
                        return (PdfExtractType.Base64, v.Value.GetString());
                }
            }

            // last resort: any long base64-looking string
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString() ?? "";
                        if (s.Length > 500 && LooksLikeBase64(s)) return (PdfExtractType.Base64, s);
                    }
                }
            }
        }
        return (PdfExtractType.None, null);
    }

    private static bool LooksLikeBase64(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 100) return false;
        if (s.StartsWith("JVBERi")) return true; // "%PDF-" in base64
        return Regex.IsMatch(s, @"^[A-Za-z0-9+/=\s]{100,}$");
    }

    private static string StripDataUri(string s) => Regex.Replace(s, @"^data:application/pdf;base64,", "");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string BuildFileName(string name, string pan)
    {
        var safeName = Regex.Replace((name ?? "report").Trim(), @"\s+", "_");
        safeName = Regex.Replace(safeName, @"[^\w\-]", "");
        var ts = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        return $"CIBIL_{safeName}_{pan}_{ts}.pdf";
    }

    /// <summary>Inserts the pulled report. If a concurrent request inserts the same
    /// (PAN, type, month) first, the unique violation is swallowed and the winner's row is
    /// returned with SOURCE=CACHE so both callers end up serving one saved file.</summary>
    private async Task<(int reportId, string source)> InsertReportAsync(CibilRequest request, string pan, string reportType,
        string fileName, string filePath, long size, int pulledBy, string pulledByName)
    {
        try
        {
            var newId = await _db.ScalarAsync(@"
                INSERT INTO dbo.TBL_CIP_CIBIL_REPORTS
                    (PAN_NO, APPLICANT_NAME, MOBILE, GENDER, REPORT_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, PULLED_BY, PULLED_BY_NAME, PULLED_DATE, SOURCE)
                OUTPUT INSERTED.REPORT_ID
                VALUES
                    (@pan, @name, @mobile, @gender, @type, @file, @path, @size, @by, @byname, GETDATE(), 'API');",
                p =>
                {
                    p.AddWithValue("@pan", pan);
                    p.AddWithValue("@name", (object?)request.Name ?? DBNull.Value);
                    p.AddWithValue("@mobile", (object?)request.Mobile ?? DBNull.Value);
                    p.AddWithValue("@gender", (object?)request.Gender ?? DBNull.Value);
                    p.AddWithValue("@type", reportType);
                    p.AddWithValue("@file", fileName);
                    p.AddWithValue("@path", filePath);
                    p.AddWithValue("@size", size);
                    p.AddWithValue("@by", pulledBy);
                    p.AddWithValue("@byname", (object?)pulledByName ?? DBNull.Value);
                });
            return (Convert.ToInt32(newId), "API");
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Lost a race with a concurrent insert of the same PAN+type+month.
            var winner = await FindByPanAsync(pan, reportType, currentMonthOnly: true);
            if (winner == null) throw; // rethrow the original violation
            return (winner.ReportId, "CACHE");
        }
    }

    // ------------------------------------------------------------------ list

    public async Task<List<CibilReportInfo>> ListAsync(string? reportType = null, DateTime? from = null, DateTime? to = null, string? search = null, int[]? scopedCodes = null)
    {
        // LOCATION_* are snapshotted at pull time; the join is a fallback for rows
        // created before the columns existed. CENTER_MANAGER falls back to the
        // puller's name. Legacy rows have PULLED_BY = NULL and render as an em-dash.
        var sql = @"
            SELECT r.REPORT_ID, r.PAN_NO, r.APPLICANT_NAME, r.MOBILE, r.REPORT_TYPE,
                   r.FILE_NAME, r.FILE_SIZE, r.PULLED_BY_NAME, r.PULLED_DATE, r.SOURCE,
                   'OK' AS STATUS,
                   COALESCE(r.LOCATION_NAME, l.LOC_NAME) AS LOCATION_NAME,
                   COALESCE(r.CENTER_MANAGER, r.PULLED_BY_NAME, 'legacy-import') AS CENTER_MANAGER
            FROM dbo.TBL_CIP_CIBIL_REPORTS r
            LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = r.PULLED_BY
            LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER l ON l.LOC_ID = COALESCE(r.LOCATION_ID, e.LOCATION_ID)
            WHERE 1 = 1";
        // Hierarchy scope: only reports pulled by the viewer or their downline.
        // All filter fragments must be appended BEFORE the query call.
        if (scopedCodes is { Length: > 0 })
            sql += $" AND r.PULLED_BY IN ({HierarchyService.InlineCodes(scopedCodes)})";
        if (!string.IsNullOrWhiteSpace(reportType)) sql += " AND r.REPORT_TYPE = @type";
        if (from.HasValue) sql += " AND r.PULLED_DATE >= @from";
        if (to.HasValue) sql += " AND r.PULLED_DATE < @to";
        if (!string.IsNullOrWhiteSpace(search)) sql += " AND (r.APPLICANT_NAME LIKE @q OR r.PAN_NO LIKE @q OR COALESCE(r.MOBILE,'') LIKE @q OR r.FILE_NAME LIKE @q OR COALESCE(r.LOCATION_NAME,'') LIKE @q OR COALESCE(r.CENTER_MANAGER, r.PULLED_BY_NAME,'') LIKE @q)";
        sql += " ORDER BY r.PULLED_DATE DESC";
        var bind = new Action<SqlParameterCollection>(p =>
        {
            if (!string.IsNullOrWhiteSpace(reportType)) p.AddWithValue("@type", reportType);
            if (from.HasValue) p.AddWithValue("@from", from.Value.Date);
            if (to.HasValue) p.AddWithValue("@to", to.Value.Date.AddDays(1));
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
        });

        var dt = await _db.QueryAsync(sql, bind);
        var list = new List<CibilReportInfo>();
        foreach (DataRow r in dt.Rows) list.Add(MapRow(r));
        return list;
    }

    public async Task<(byte[] bytes, CibilReportInfo report)?> GetFileAsync(int reportId)
    {
        var dt = await _db.QueryAsync(@"
            SELECT REPORT_ID, PAN_NO, APPLICANT_NAME, MOBILE, REPORT_TYPE,
                   FILE_NAME, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE, 'OK' AS STATUS, FILE_PATH
            FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE REPORT_ID = @id",
            p => p.AddWithValue("@id", reportId));

        if (dt.Rows.Count == 0) return null;
        var info = MapRow(dt.Rows[0]);
        var path = dt.Rows[0]["FILE_PATH"].ToString()!;
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException($"Report file is missing on disk: {path}");
        return (await System.IO.File.ReadAllBytesAsync(path), info);
    }

    public async Task<int?> GetPulledByAsync(int reportId)
    {
        var v = await _db.ScalarAsync(
            "SELECT PULLED_BY FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE REPORT_ID = @id",
            p => p.AddWithValue("@id", reportId));
        return v == null || v == DBNull.Value ? null : Convert.ToInt32(v);
    }

    public async Task<bool> WasReportServedToAsync(int reportId, int empCode)
    {
        try
        {
            var v = await _db.ScalarAsync(@"
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.TBL_CIP_CIBIL_PULL_LOG
                    WHERE REPORT_ID = @id AND EMP_CODE = @e AND STATUS = 'OK')
                    THEN 1 ELSE 0 END",
                p => { p.AddWithValue("@id", reportId); p.AddWithValue("@e", empCode); });
            return v != null && v != DBNull.Value && Convert.ToInt32(v) == 1;
        }
        catch (Exception ex)
        {
            // Ledger missing (SQL scripts not run). Fail CLOSED — the download then
            // falls back to the hierarchy rule rather than 500ing or opening up.
            _logger.LogWarning(ex, "Pull ledger unavailable; cannot check whether report {Id} was served to {Emp}.", reportId, empCode);
            return false;
        }
    }

    // ------------------------------------------------------------ pull ledger

    /// <summary>The caller's own pull events (TL tab "My Reports"). Scoped to EMP_CODE
    /// with no hierarchy expansion, so a TL only ever sees their own pulls.
    ///
    /// FAILED and PENDING attempts are included: a failed vendor call leaves no row in
    /// TBL_CIP_CIBIL_REPORTS, so filtering on STATUS='OK' here made a failed pull
    /// vanish from every screen in the product while still freezing that PAN for the
    /// user. The status is returned so the UI can render it.</summary>
    public async Task<List<MyPullRow>> ListMyPullsAsync(int empCode, int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        var dt = await _db.QueryAsync($@"
            SELECT TOP {take} lg.LOG_ID, lg.REPORT_ID, lg.PAN_NO, lg.SOURCE, lg.STATUS,
                   lg.ERROR_MESSAGE, lg.PULLED_DATE, lg.LOCATION_NAME, lg.PULLED_BY_NAME,
                   rep.APPLICANT_NAME, COALESCE(rep.REPORT_TYPE, lg.REPORT_TYPE) AS REPORT_TYPE,
                   rep.FILE_NAME
            FROM dbo.TBL_CIP_CIBIL_PULL_LOG lg
            LEFT JOIN dbo.TBL_CIP_CIBIL_REPORTS rep ON rep.REPORT_ID = lg.REPORT_ID
            WHERE lg.EMP_CODE = @e
            ORDER BY lg.PULLED_DATE DESC, lg.LOG_ID DESC",
            p => p.AddWithValue("@e", empCode));

        return dt.AsEnumerable().Select(r => new MyPullRow
        {
            LogId = Convert.ToInt32(r["LOG_ID"]),
            ReportId = r["REPORT_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["REPORT_ID"]),
            PanNo = r["PAN_NO"].ToString() ?? "",
            ApplicantName = NullIfBlank(r["APPLICANT_NAME"]),
            ReportType = r["REPORT_TYPE"] == DBNull.Value ? "cibil" : r["REPORT_TYPE"].ToString()!,
            Source = r["SOURCE"].ToString() ?? "",
            Status = r["STATUS"].ToString() ?? "OK",
            ErrorMessage = NullIfBlank(r["ERROR_MESSAGE"]),
            LocationName = NullIfBlank(r["LOCATION_NAME"]),
            CenterManager = NullIfBlank(r["PULLED_BY_NAME"]),
            FileName = NullIfBlank(r["FILE_NAME"]),
            PullDate = Convert.ToDateTime(r["PULLED_DATE"]).ToString("yyyy-MM-dd"),
            PullTime = Convert.ToDateTime(r["PULLED_DATE"]).ToString("HH:mm:ss"),
        }).ToList();
    }

    /// <summary>Pull attempts with no report behind them — FAILED and in-flight/stuck
    /// PENDING. This is the ledger read behind GET /api/cibil/failed-pulls.
    ///
    /// Scoped like ListAsync, except the codes are EMP_CODEs (who ATTEMPTED the pull)
    /// rather than PULLED_BY. The viewer's own code is always OR'd in, so an employee
    /// whose subtree lookup excluded them still sees their own attempts. An empty
    /// (non-null) code list means "eligible to see nothing" and fails CLOSED: returning
    /// every row because the IN () list was empty would be an escalation.</summary>
    public async Task<List<FailedPullRow>> ListFailedPullsAsync(int[]? scopedCodes, int viewerEmpCode, string? search = null, int take = 200)
    {
        // Admins pass null and see every company-wide failure. A non-admin with an
        // empty resolved hierarchy still sees no rows (the guard fails closed).
        if (scopedCodes is { Length: 0 }) return new List<FailedPullRow>();
        take = Math.Clamp(take, 1, 500);

        var sql = $@"
            SELECT TOP ({take}) lg.LOG_ID, lg.REPORT_ID, lg.PAN_NO, lg.STATUS, lg.SOURCE,
                   lg.ERROR_MESSAGE, lg.EMP_NAME, lg.LOCATION_NAME, lg.PULLED_DATE,
                   lg.REPORT_TYPE, rep.APPLICANT_NAME, rep.FILE_NAME
            FROM dbo.TBL_CIP_CIBIL_PULL_LOG lg
            LEFT JOIN dbo.TBL_CIP_CIBIL_REPORTS rep ON rep.REPORT_ID = lg.REPORT_ID
            WHERE lg.STATUS <> 'OK'";

        if (scopedCodes != null)
            sql += $" AND (lg.EMP_CODE IN ({HierarchyService.InlineCodes(scopedCodes)}) OR lg.EMP_CODE = @me)";
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (lg.PAN_NO LIKE @q OR lg.EMP_NAME LIKE @q OR lg.SOURCE LIKE @q OR COALESCE(lg.ERROR_MESSAGE,'') LIKE @q)";
        sql += " ORDER BY lg.PULLED_DATE DESC, lg.LOG_ID DESC";

        var dt = await _db.QueryAsync(sql, p =>
        {
            if (scopedCodes != null) p.AddWithValue("@me", viewerEmpCode);
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
        });

        return dt.AsEnumerable().Select(r => new FailedPullRow
        {
            LogId = Convert.ToInt32(r["LOG_ID"]),
            ReportId = r["REPORT_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["REPORT_ID"]),
            PanNo = r["PAN_NO"].ToString() ?? "",
            ApplicantName = NullIfBlank(r["APPLICANT_NAME"]),
            ReportType = r["REPORT_TYPE"] == DBNull.Value ? "cibil" : r["REPORT_TYPE"].ToString()!,
            Status = r["STATUS"].ToString() ?? "",
            Source = r["SOURCE"].ToString() ?? "",
            RequestedBy = NullIfBlank(r["EMP_NAME"]),
            LocationName = NullIfBlank(r["LOCATION_NAME"]),
            FileName = NullIfBlank(r["FILE_NAME"]),
            ErrorMessage = NullIfBlank(r["ERROR_MESSAGE"]),
            PullDate = Convert.ToDateTime(r["PULLED_DATE"]).ToString("yyyy-MM-dd"),
            PullTime = Convert.ToDateTime(r["PULLED_DATE"]).ToString("HH:mm:ss"),
        }).ToList();
    }

    /// <summary>Admin: how many CIBIL credits this system has consumed, plus the vendor
    /// balance when the vendor exposes a balance endpoint.
    ///
    /// PortalPulls counts all completed calls to the vendor (API + REGEN), including
    /// failed calls, because the vendor charges for either outcome. It is NOT the
    /// vendor's remaining balance — the two are different numbers and must not be
    /// presented as the same figure.</summary>
    public async Task<PortalUsageInfo> GetPortalUsageAsync(DateTime? from = null, DateTime? to = null)
    {
        var fromDate = from?.Date ?? new DateTime(1900, 1, 1);
        var toDate = to?.Date.AddDays(1) ?? DateTime.Now.Date.AddDays(1);
        var month = CurrentMonthKey();

        async Task<int> Count(string extra)
        {
            // @m is only bound when the fragment uses it; extra parameters are harmless.
            var v = await _db.ScalarAsync($@"
                SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_PULL_LOG
                WHERE STATUS IN ('OK','FAILED') AND PULLED_DATE >= @from AND PULLED_DATE < @to {extra}",
                p =>
                {
                    p.AddWithValue("@from", fromDate);
                    p.AddWithValue("@to", toDate);
                    if (extra.Contains("@m")) p.AddWithValue("@m", month);
                });
            return Convert.ToInt32(v ?? 0);
        }

        return new PortalUsageInfo
        {
            // Count both successful and failed calls — the vendor charges for either.
            PortalPulls = await Count("AND SOURCE IN ('API','REGEN')"),
            MonthPulls = await Count("AND PULL_MONTH = @m"),
            MonthCaches = await Count("AND PULL_MONTH = @m AND SOURCE IN ('CACHE','LEGACY')"),
            TotalPullEvents = await Count(""),
            From = fromDate.ToString("yyyy-MM-dd"),
            To = toDate.AddDays(-1).ToString("yyyy-MM-dd"),
            Vendor = await GetVendorBalanceAsync()
        };
    }

    /// <summary>Probe the vendor balance endpoint. Never throws: any problem comes back
    /// as Available=false with a readable Message, so the Reports page still renders.
    /// Not cached — a stale balance is worse than a slow one.</summary>
    public async Task<VendorBalanceInfo> GetVendorBalanceAsync()
    {
        var result = new VendorBalanceInfo { CheckedOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        var url = _config["Cibil:BalanceUrl"];

        if (string.IsNullOrWhiteSpace(url))
        {
            result.Message = "Cibil:BalanceUrl is not configured. The vendor has not published a balance endpoint, so the exact credit balance is unavailable.";
            return result;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = _config["Cibil:ApiToken"];
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("x-api-token", token);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var res = await _http.SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);

            if (!res.IsSuccessStatusCode)
            {
                result.Message = $"Vendor balance endpoint returned HTTP {(int)res.StatusCode}.";
                return result;
            }

            using var doc = JsonDocument.Parse(body);
            if (TryReadDecimal(doc.RootElement, out var balance))
            {
                result.Available = true;
                result.Balance = balance;
                return result;
            }

            result.Message = "Vendor balance response did not contain a recognisable numeric balance.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CIBIL vendor balance probe failed");
            result.Message = "Vendor balance unavailable: " + ex.Message;
            return result;
        }
    }

    /// <summary>Pull the first plausible number out of a balance payload, whether it sits
    /// at the root or one level down under a conventional key.</summary>
    private static bool TryReadDecimal(JsonElement el, out decimal value)
    {
        value = 0;
        string[] keys = { "balance", "wallet_balance", "credit_balance", "remaining",
                          "available_balance", "credits", "amount" };

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in keys)
            {
                if (el.TryGetProperty(k, out var v) && TryScalarDecimal(v, out value)) return true;
            }
            // One level down: { "data": { "balance": 12 } } etc.
            foreach (var p in el.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object && TryReadDecimal(p.Value, out value)) return true;
            }
        }
        return false;
    }

    private static bool TryScalarDecimal(JsonElement v, out decimal value)
    {
        value = 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(v.GetString(), out value),
            _ => false
        };
    }
}
