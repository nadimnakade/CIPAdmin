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
    Task<CibilGenerateResponse> GenerateAsync(CibilRequest request, int pulledBy, string pulledByName, bool isAdmin, bool regenerate, CancellationToken ct = default);
    Task<List<object>> QueryEmployeeLimitsAsync(string? search, string filter);
    Task<object> SetEmployeeLimitAsync(int empCode, int monthlyLimit, int setBy);
    Task ClearEmployeeLimitAsync(int empCode);
    Task<object> ImportLegacyFilesAsync();
    Task<List<CibilReportInfo>> ListAsync(string? reportType = null, DateTime? from = null, DateTime? to = null, string? search = null, int[]? scopedCodes = null);
    Task<(byte[] bytes, CibilReportInfo report)?> GetFileAsync(int reportId);
    Task<int?> GetPulledByAsync(int reportId);
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
        Source = r["SOURCE"].ToString()!
    };

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

    /// <summary>Connector role / designation — excluded from employee listings.</summary>
    private const int ConnectorRoleId = 2010;
    private const int ConnectorDesigId = 1014;

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
            WHERE ISNULL(e.ROLE_ID, 0) <> @connectorRole
              AND ISNULL(e.DESIG_ID, 0) <> @connectorDesig
            {filter}
            ORDER BY CASE WHEN l.EMP_CODE IS NULL THEN 1 ELSE 0 END, e.EMP_NAME",
            p =>
            {
                p.AddWithValue("@connectorRole", ConnectorRoleId);
                p.AddWithValue("@connectorDesig", ConnectorDesigId);
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
            // Backend-driven: what this employee actually gets (override or current default).
            effectiveLimit = r["MONTHLY_LIMIT"] == DBNull.Value ? defaultLimit : Math.Max(defaultLimit, r.Field<int>("MONTHLY_LIMIT")),
            setOn = r["SET_ON"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(r["SET_ON"])
        }).ToList();
    }

    public async Task<object> SetEmployeeLimitAsync(int empCode, int monthlyLimit, int setBy)
    {
        var exists = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
        if (Convert.ToInt32(exists) == 0)
            throw new InvalidOperationException("Employee not found.");

        await _db.ExecuteAsync(@"
            MERGE dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT AS t
            USING (SELECT @e AS EMP_CODE) AS s ON t.EMP_CODE = s.EMP_CODE
            WHEN MATCHED THEN UPDATE SET MONTHLY_LIMIT = @l, SET_BY = @by, SET_ON = GETDATE()
            WHEN NOT MATCHED THEN INSERT (EMP_CODE, MONTHLY_LIMIT, SET_BY) VALUES (@e, @l, @by);",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@l", monthlyLimit); p.AddWithValue("@by", setBy); });

        return new { empCode, monthlyLimit, saved = true };
    }

    public async Task ClearEmployeeLimitAsync(int empCode)
    {
        await _db.ExecuteAsync(
            "DELETE FROM dbo.TBL_CIP_EMPLOYEE_CIBIL_LIMIT WHERE EMP_CODE = @e",
            p => p.AddWithValue("@e", empCode));
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

        var usedV = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_BY = @e AND PULL_MONTH = @m",
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
        var floorLimit = empLimit > 0 ? Math.Max(baseLimit, empLimit) : baseLimit;

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

    public async Task<CibilGenerateResponse> GenerateAsync(CibilRequest request, int pulledBy, string pulledByName, bool isAdmin, bool regenerate, CancellationToken ct = default)
    {
        // Strip spaces/dashes so "AAABP0002A " or "AAAB-P0-002A" still validates as 10 chars.
        var pan = Regex.Replace((request.PanCard ?? "").Trim().ToUpperInvariant(), @"[\s\-]", "");
        request.PanCard = pan;
        var reportType = string.IsNullOrWhiteSpace(request.ReportType) ? "cibil" : request.ReportType.Trim().ToLowerInvariant();

        if (pan.Length < 10 || !Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
            throw new InvalidOperationException("A valid 10-character PAN (e.g. AHAPN0789J) is required.");

        // ---------- RULE 2b: LEGACY reports (standalone-era PDFs) serve from disk
        // forever, regardless of month. Only an admin Regenerate re-pulls these.
        var legacyReport = await FindLegacyByPanAsync(pan, reportType);
        if (legacyReport != null && !(isAdmin && regenerate))
        {
            _logger.LogInformation("CIBIL for PAN {Pan} is a legacy report ({Date}) — serving saved PDF (no API call).", pan, legacyReport.PulledDate);
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

        // ---------- RULE 1: monthly pull quota for regular users.
        // Admins pulling a regeneration bypass the quota (RULE 2) and quota is not consumed.
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

            var regenBytes = await PullFromApiAsync(request, ResolveApiToken(request), ct);
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
                Quota = quota
            };
        }

        // ---------- Fresh pull (new PAN this month).
        var bytes = await PullFromApiAsync(request, ResolveApiToken(request), ct);

        var fileName = BuildFileName(request.Name, pan);
        var folder = UserStorageFolderPath(pulledByName);
        var filePath = Path.Combine(folder, fileName);
        await System.IO.File.WriteAllBytesAsync(filePath, bytes, ct);

        var (reportId, source) = await InsertReportAsync(request, pan, reportType, fileName, filePath, bytes.Length, pulledBy, pulledByName);

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
        var sql = @"
            SELECT REPORT_ID, PAN_NO, APPLICANT_NAME, MOBILE, REPORT_TYPE,
                   FILE_NAME, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE
            FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE 1 = 1";
        // Hierarchy scope: only reports pulled by the viewer or their downline.
        // All filter fragments must be appended BEFORE the query call.
        if (scopedCodes is { Length: > 0 })
            sql += $" AND PULLED_BY IN ({HierarchyService.InlineCodes(scopedCodes)})";
        if (!string.IsNullOrWhiteSpace(reportType)) sql += " AND REPORT_TYPE = @type";
        if (from.HasValue) sql += " AND PULLED_DATE >= @from";
        if (to.HasValue) sql += " AND PULLED_DATE < @to";
        if (!string.IsNullOrWhiteSpace(search)) sql += " AND (APPLICANT_NAME LIKE @q OR PAN_NO LIKE @q OR FILE_NAME LIKE @q)";
        sql += " ORDER BY PULLED_DATE DESC";
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
                   FILE_NAME, FILE_SIZE, PULLED_BY_NAME, PULLED_DATE, SOURCE, FILE_PATH
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
}
