using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class DashboardResponse
{
    public int CibilPulledToday { get; set; }
    public int CibilPulledTodayDelta { get; set; }      // % vs yesterday
    public int CibilPulledYesterday { get; set; }
    public int CibilPulledThisWeek { get; set; }       // trailing 7 days, matches Trend
    public int CibilPulledThisMonth { get; set; }      // calendar month to date
    public int TotalCibilReports { get; set; }
    public int TotalCibilDelta { get; set; }            // % vs previous month
    public int ActiveEmployees { get; set; }
    public int ActiveEmployeesDelta { get; set; }       // % vs last month
    public int ActiveDids { get; set; }
    public int UnassignedDids { get; set; }
    public int OldDialerUsers { get; set; }     // DIALER_VERSION IS NULL (default)
    public int NewDialerUsers { get; set; }     // DIALER_VERSION set (new dialer app)
    public List<TrendPoint> Trend { get; set; } = new();
    public List<TypeSlice> ReportsByType { get; set; } = new();
}

public class TrendPoint
{
    public string Label { get; set; } = "";   // e.g. "10 Sep"
    public int Count { get; set; }
}

public class TypeSlice
{
    public string ReportType { get; set; } = "";
    public int Count { get; set; }
}

public class DialerVersionRequest
{
    public string? DialerVersion { get; set; }   // null/empty => old dialer
}

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IHierarchyService _hierarchy;

    public DashboardController(IDbContext db, IHierarchyService hierarchy)
    {
        _db = db; _hierarchy = hierarchy;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get()
    {
        var res = new DashboardResponse();

        // Hierarchy scope: admins aggregate the whole company; everyone else
        // aggregates only their own subtree (self + downline).
        var empCode = int.Parse(User.FindFirst("empCode")!.Value);
        var roleId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var rr) ? rr : 0;
        var scope = await _hierarchy.GetScopeAsync(empCode, roleId);

        // CIBIL counts are per PULLED_BY (the puller); employee/DID counts are per EMP_CODE.
        string pullFilter = "";
        if (!scope.IsAdmin)
            pullFilter = $" AND PULLED_BY IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})";

        string empFilter = "";
        if (!scope.IsAdmin)
            empFilter = $" AND EMP_CODE IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})";

        res.CibilPulledToday = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= CAST(GETDATE() AS DATE)" + pullFilter));
        res.CibilPulledTodayDelta = await PctChangeAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= CAST(GETDATE() AS DATE)" + pullFilter,
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(DAY, -1, CAST(GETDATE() AS DATE)) AND PULLED_DATE < CAST(GETDATE() AS DATE)" + pullFilter,
            scope.IsAdmin ? null : scope.SubtreeCodes);

        // Period strip under the trend chart. These are counted here rather than summed from
        // Trend in the UI: "this month" is a calendar month, which the 7-day Trend window
        // cannot answer, and deriving it client-side would silently show the week instead.
        res.CibilPulledYesterday = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(DAY, -1, CAST(GETDATE() AS DATE)) AND PULLED_DATE < CAST(GETDATE() AS DATE)" + pullFilter));
        res.CibilPulledThisWeek = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(DAY, -6, CAST(GETDATE() AS DATE))" + pullFilter));
        res.CibilPulledThisMonth = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(MONTH, DATEDIFF(MONTH, 0, GETDATE()), 0)" + pullFilter));

        res.TotalCibilReports = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE 1=1" + pullFilter));
        res.TotalCibilDelta = await PctChangeAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(MONTH, -1, GETDATE())" + pullFilter,
            "SELECT COUNT(1) FROM dbo.TBL_CIP_CIBIL_REPORTS WHERE PULLED_DATE >= DATEADD(MONTH, -2, GETDATE()) AND PULLED_DATE < DATEADD(MONTH, -1, GETDATE())" + pullFilter,
            scope.IsAdmin ? null : scope.SubtreeCodes);

        res.ActiveEmployees = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE ISNULL(ACTIVE, 0) = 1 AND ISNULL(IS_DELETED, 0) = 0" + empFilter));
        res.ActiveEmployeesDelta = 0;

        res.ActiveDids = Convert.ToInt32(await _db.ScalarAsync($@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID d
            JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
            WHERE ISNULL(d.IS_DELETED, 0) = 0 AND d.DID_STATUS = 'Active'{(scope.IsAdmin ? "" : $" AND e.EMP_CODE IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})")}"));
        res.UnassignedDids = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID WHERE ISNULL(IS_DELETED, 0) = 0 AND EMP_CODE IS NULL"));

        // Dialer split: employees default to the OLD dialer (DIALER_VERSION NULL) until
        // an admin marks them as using the new dialer app (version recorded).
        res.OldDialerUsers = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE ISNULL(ACTIVE, 0) = 1 AND ISNULL(IS_DELETED, 0) = 0 AND DIALER_VERSION IS NULL" + empFilter));
        res.NewDialerUsers = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE ISNULL(ACTIVE, 0) = 1 AND ISNULL(IS_DELETED, 0) = 0 AND DIALER_VERSION IS NOT NULL" + empFilter));

        // 7-day trend (0 for days with no pulls)
        var trendSql = $@"
            WITH days AS (
                SELECT TOP 7 CAST(DATEADD(DAY, -n, CAST(GETDATE() AS DATE)) AS DATE) AS d
                FROM (VALUES (0),(1),(2),(3),(4),(5),(6)) v(n)
            )
            SELECT CAST(days.d AS VARCHAR(20)) AS d, ISNULL(COUNT(r.REPORT_ID), 0) AS cnt
            FROM days
            LEFT JOIN dbo.TBL_CIP_CIBIL_REPORTS r ON CAST(r.PULLED_DATE AS DATE) = days.d{(scope.IsAdmin ? "" : $" AND r.PULLED_BY IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})")}
            GROUP BY days.d ORDER BY days.d;";
        var trend = await _db.QueryAsync(trendSql);
        foreach (DataRow r in trend.Rows)
        {
            res.Trend.Add(new TrendPoint
            {
                Label = Convert.ToDateTime(r["d"]).ToString("dd MMM"),
                Count = Convert.ToInt32(r["cnt"])
            });
        }

        // Reports by type
        var byType = await _db.QueryAsync($@"
            SELECT REPORT_TYPE, COUNT(1) AS cnt
            FROM dbo.TBL_CIP_CIBIL_REPORTS
            WHERE 1 = 1{pullFilter}
            GROUP BY REPORT_TYPE ORDER BY COUNT(1) DESC");
        foreach (DataRow r in byType.Rows)
        {
            res.ReportsByType.Add(new TypeSlice
            {
                ReportType = r["REPORT_TYPE"].ToString() ?? "",
                Count = Convert.ToInt32(r["cnt"])
            });
        }

        return Ok(res);
    }

    /// <summary>Name list behind the old / new / all dialer tiles. Paged + searchable;
    /// admins can move an employee between old and new dialer.</summary>
    [HttpGet("dialer-users")]
    public async Task<ActionResult<object>> DialerUsers(
        [FromQuery] string group = "all",   // old | new | all
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var scope = await _hierarchy.GetScopeAsync(int.Parse(User.FindFirst("empCode")!.Value),
            int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var rr) ? rr : 0);
        var empFilter = scope.IsAdmin ? "" : $" AND e.EMP_CODE IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})";

        var where = " WHERE ISNULL(e.IS_DELETED, 0) = 0 AND ISNULL(e.ACTIVE, 0) = 1" + empFilter;
        if (group == "old") where += " AND e.DIALER_VERSION IS NULL";
        else if (group == "new") where += " AND e.DIALER_VERSION IS NOT NULL";
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q)";

        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
        };

        var total = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER e" + where, bind));

        var dt = await _db.QueryAsync($@"
            SELECT e.EMP_CODE, CAST(e.EMP_CODE AS VARCHAR(20)) AS EMP_CODE_TEXT, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME,
                   e.DIALER_VERSION, e.DIALER_UUID, dn.DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            {where}
            ORDER BY e.EMP_NAME
            OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY", bind);

        var rows = dt.AsEnumerable().Select(r => new
        {
            empCode = r.Field<int>("EMP_CODE"),
            employeeCodeText = r["EMP_CODE_TEXT"].ToString(),
            empName = r["EMP_NAME"].ToString(),
            designation = r["DESIG_NAME"].ToString(),
            dialerVersion = r["DIALER_VERSION"] == DBNull.Value ? null : r["DIALER_VERSION"].ToString(),
            dialerUuid = r["DIALER_UUID"] == DBNull.Value ? null : r["DIALER_UUID"].ToString(),
            isNewDialer = r["DIALER_VERSION"] != DBNull.Value
        }).ToList();

        return Ok(new { total, rows, isAdmin = scope.IsAdmin });
    }

    /// <summary>Move an employee between the old dialer (default) and the new dialer.
    /// Admins only. version=null moves them back to old; otherwise records e.g. "0.0.23".</summary>
    [HttpPut("dialer-users/{empCode:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SetDialerVersion(int empCode, [FromBody] DialerVersionRequest body)
    {
        var affected = await _db.ExecuteAsync(
            "UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER SET DIALER_VERSION = @v, MODIFIED_BY = @by, MODIFIED_DATE = GETDATE() " +
            "WHERE EMP_CODE = @e AND ISNULL(IS_DELETED, 0) = 0",
            p =>
            {
                p.AddWithValue("@v", string.IsNullOrWhiteSpace(body.DialerVersion) ? (object)DBNull.Value : body.DialerVersion.Trim());
                p.AddWithValue("@by", int.Parse(User.FindFirst("empCode")!.Value));
                p.AddWithValue("@e", empCode);
            });
        return affected == 0 ? NotFound(new { error = "Employee not found." }) : Ok(new { ok = true });
    }

    private async Task<int> PctChangeAsync(string currentSql, string previousSql, int[]? scopeCodes = null)
    {
        Action<SqlParameterCollection> bind = scopeCodes == null
            ? _ => { }
            : p => p.AddWithValue("@scopeCodes", string.Join(",", scopeCodes));
        var cur = Convert.ToInt32(await _db.ScalarAsync(currentSql, bind));
        var prev = Convert.ToInt32(await _db.ScalarAsync(previousSql, bind));
        if (prev == 0) return cur == 0 ? 0 : 100;
        return (int)Math.Round((cur - prev) * 100.0 / prev);
    }
}
