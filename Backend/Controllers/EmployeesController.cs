using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Models;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class EmployeeDidDto
{
    public int DidId { get; set; }
    public int? EmpCode { get; set; }
    public string? EmployeeCodeText { get; set; }
    public string? EmployeeName { get; set; }
    public string? Designation { get; set; }
    public string DidNumber { get; set; } = "";
    public string? ExtensionNumber { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? AssignedDate { get; set; }

    // CloudConnect dialer config (merged into the DID row)
    public string? BaseUrl { get; set; }
    public string? Click2CallPath { get; set; }
    public string? TokenId { get; set; }
    public string? ExtensionPassword { get; set; }
    public string? SipServer { get; set; }
    public int? SipPort { get; set; }
    public string? SipPassword { get; set; }
    public string? SipRealm { get; set; }
}

public class AssignDidRequest
{
    public int? EmpCode { get; set; }
    public string DidNumber { get; set; } = "";

    // Optional CloudConnect config; defaults applied when omitted.
    public string? BaseUrl { get; set; }
    public string? Click2CallPath { get; set; }
    public string? TokenId { get; set; }
    public string? ExtensionPassword { get; set; }
    public string? SipServer { get; set; }
    public int? SipPort { get; set; }
    public string? SipPassword { get; set; }
    public string? SipRealm { get; set; }
}

public class ChangeDidRequest
{
    public int DidId { get; set; }
    public string NewDidNumber { get; set; } = "";
    public string? Status { get; set; }

    // Optional CloudConnect config; existing values kept when omitted.
    public string? BaseUrl { get; set; }
    public string? Click2CallPath { get; set; }
    public string? TokenId { get; set; }
    public string? ExtensionPassword { get; set; }
    public string? SipServer { get; set; }
    public int? SipPort { get; set; }
    public string? SipPassword { get; set; }
    public string? SipRealm { get; set; }
}

public class UpdateEmployeeRequest
{
    public string? EmployeeName { get; set; }
    public int? DesignationId { get; set; }
    public string? Email { get; set; }
    public int? LocationId { get; set; }
    public string? Doj { get; set; }
    public string? Contact { get; set; }
}

public class UpdateEmployeeStatusRequest
{
    public bool IsActive { get; set; }
}

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IHierarchyService _hierarchy;
    private readonly ILogger<EmployeesController> _logger;

    public EmployeesController(IDbContext db, IHierarchyService hierarchy, ILogger<EmployeesController> logger)
    {
        _db = db; _hierarchy = hierarchy; _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private int RoleId => int.TryParse(User.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : 0;
    private bool IsAdmin => HierarchyService.IsAdminRole(RoleId);

    /// <summary>Connector role / designation — excluded from employee listings (DID mgmt, pickers).</summary>
    private const int ConnectorRoleId = 2010;
    private const int ConnectorDesigId = 1014;
    /// <summary>Only these roles are dialer users: CALLER, TL, TL-OPS (item 2).</summary>
    private const string DialerRolesSql = "5,4,1007";

    /// <summary>SQL fragment restricting TBL_CIP_EMPLOYEE_MASTER (alias `e`) to the viewer's subtree.
    /// Empty string for admins (no restriction). SQL 2014-safe inline IN-list.</summary>
    private async Task<string> ScopeSqlAsync()
    {
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        if (scope.IsAdmin) return "";
        return $" AND (e.EMP_CODE IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)}) OR e.EMP_CODE IS NULL)";
    }

    /// <summary>Who-is-visibility payload for the frontend scope chips.</summary>
    private async Task<object> ScopePayloadAsync()
    {
        var s = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        return new { isAdmin = s.IsAdmin, subtreeSize = s.SubtreeSize };
    }

    /// <summary>DID assignments joined to TBL_CIP_EMPLOYEE_MASTER (generic/shared rows have EMP_CODE NULL).
    /// Non-admins see only their subtree's assignments (generic rows visible to all).</summary>
    [HttpGet("dids")]
    public async Task<ActionResult<List<EmployeeDidDto>>> GetDids([FromQuery] string? search = null, [FromQuery] string? status = null, [FromQuery] int? designationId = null)
    {
        var scopeSql = await ScopeSqlAsync();
        var sql = $@"
            SELECT d.DID_ID, d.EMP_CODE, e.EMP_CODE AS EMP_CODE_TEXT, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME,
                   dn.DESIG_NAME, d.DID_NUMBER, d.EXTENSION_NUMBER, d.DID_STATUS, d.ASSIGNED_DATE,
                   d.BASE_URL, d.CLICK2CALL_PATH, d.TOKEN_ID, d.EXTENSION_PASSWORD,
                   d.SIP_SERVER, d.SIP_PORT, d.SIP_PASSWORD, d.SIP_REALM
            FROM dbo.TBL_CIP_EMPLOYEE_DID d
            LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            WHERE ISNULL(d.IS_DELETED, 0) = 0{scopeSql}
              AND (d.EMP_CODE IS NULL OR (ISNULL(e.ROLE_ID, 0) IN (5, 4, 1007) AND ISNULL(e.ROLE_ID, 0) <> 2010 AND ISNULL(e.DESIG_ID, 0) <> 1014))";
        // NOTE: build the FULL SQL before the query call — the bind lambda must only
        // attach parameters (DbContext captures the command text before invoking it).
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q OR d.DID_NUMBER LIKE @q OR d.EXTENSION_NUMBER LIKE @q)";
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            sql += " AND d.DID_STATUS = @s";
        if (designationId is > 0)
            sql += " AND e.DESIG_ID = @dg";
        sql += " ORDER BY CASE WHEN d.EMP_CODE IS NULL THEN 1 ELSE 0 END, e.EMP_NAME, d.DID_ID";
        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (!string.IsNullOrWhiteSpace(status) && status != "All") p.AddWithValue("@s", status);
            if (designationId is > 0) p.AddWithValue("@dg", designationId.Value);
        };

        var dt = await _db.QueryAsync(sql, bind);
        var list = new List<EmployeeDidDto>();
        foreach (DataRow r in dt.Rows)
        {
            list.Add(new EmployeeDidDto
            {
                DidId = Convert.ToInt32(r["DID_ID"]),
                EmpCode = r["EMP_CODE"] == DBNull.Value ? null : Convert.ToInt32(r["EMP_CODE"]),
                EmployeeCodeText = r["EMP_CODE_TEXT"] == DBNull.Value ? null : r["EMP_CODE_TEXT"].ToString(),
                EmployeeName = r["EMP_CODE"] == DBNull.Value ? "Other (Generic)" : r["EMP_NAME"].ToString(),
                Designation = r["DESIG_NAME"].ToString(),
                DidNumber = r["DID_NUMBER"].ToString() ?? "",
                ExtensionNumber = r["EXTENSION_NUMBER"] == DBNull.Value ? null : r["EXTENSION_NUMBER"].ToString(),
                Status = r["DID_STATUS"].ToString() ?? "Active",
                AssignedDate = r["ASSIGNED_DATE"] == DBNull.Value ? null : Convert.ToDateTime(r["ASSIGNED_DATE"]),
                BaseUrl = r["BASE_URL"].ToString(),
                Click2CallPath = r["CLICK2CALL_PATH"].ToString(),
                TokenId = r["TOKEN_ID"].ToString(),
                ExtensionPassword = r["EXTENSION_PASSWORD"].ToString(),
                SipServer = r["SIP_SERVER"].ToString(),
                SipPort = r["SIP_PORT"] == DBNull.Value ? null : Convert.ToInt32(r["SIP_PORT"]),
                SipPassword = r["SIP_PASSWORD"].ToString(),
                SipRealm = r["SIP_REALM"].ToString()
            });
        }
        return Ok(list);
    }

    /// <summary>Designations for filter dropdowns.</summary>
    [HttpGet("designations")]
    public async Task<ActionResult<object>> Designations()
    {
        var dt = await _db.QueryAsync("SELECT DESIG_ID, DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER ORDER BY DESIG_NAME");
        return Ok(dt.AsEnumerable().Select(r => new { id = r.Field<int>("DESIG_ID"), name = r.Field<string>("DESIG_NAME") }));
    }

    /// <summary>Summary tiles for the DID page (screenshot layout) — scoped to the viewer's subtree.</summary>
    [HttpGet("dids/stats")]
    public async Task<ActionResult<object>> DidStats()
    {
        var scopeSql = await ScopeSqlAsync();
        var dt = await _db.QueryAsync($@"
            SELECT
              (SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE ISNULL(e.ACTIVE,0)=1 AND ISNULL(e.IS_DELETED,0)=0{scopeSql}
                 AND ISNULL(e.ROLE_ID,0) IN (5,4,1007)) AS TOTAL_EMP,
              (SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID d
                 JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
                 WHERE ISNULL(d.IS_DELETED,0)=0 AND d.EMP_CODE IS NOT NULL{scopeSql}
                   AND ISNULL(e.ROLE_ID,0) IN (5,4,1007)) AS ASSIGNED,
              (SELECT COUNT(DISTINCT d.EMP_CODE) FROM dbo.TBL_CIP_EMPLOYEE_DID d
                 JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
                 WHERE ISNULL(d.IS_DELETED,0)=0 AND d.EMP_CODE IS NOT NULL
                   AND ISNULL(e.ACTIVE,0)=1 AND ISNULL(e.IS_DELETED,0)=0{scopeSql}
                   AND ISNULL(e.ROLE_ID,0) IN (5,4,1007)) AS ACTIVE_EMP,
              (SELECT COUNT(DISTINCT d.EMP_CODE) FROM dbo.TBL_CIP_EMPLOYEE_DID d
                 JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
                 WHERE ISNULL(d.IS_DELETED,0)=0 AND d.EMP_CODE IS NOT NULL
                   AND ISNULL(e.ACTIVE,0)=1 AND ISNULL(e.IS_DELETED,0)=0
                   AND ISNULL(e.ROLE_ID,0) IN (5,4,1007)
                   AND ISNULL(d.DID_STATUS,'Active') <> 'Active'{scopeSql}) AS INACTIVE_EMP");
        var r = dt.Rows[0];
        return Ok(new
        {
            totalEmployees = Convert.ToInt32(r["TOTAL_EMP"]),
            assignedDids = Convert.ToInt32(r["ASSIGNED"]),
            activeEmployees = Convert.ToInt32(r["ACTIVE_EMP"]),
            inactiveEmployees = Convert.ToInt32(r["INACTIVE_EMP"]),
            scope = await ScopePayloadAsync()
        });
    }

    /// <summary>Excel export of the DID list (applies the same filters + hierarchy scope as the grid).</summary>
    [HttpGet("dids/export")]
    public async Task<IActionResult> ExportDids([FromQuery] string? search = null, [FromQuery] string? status = null, [FromQuery] int? designationId = null)
    {
        var scopeSql = await ScopeSqlAsync();
        var sql = $@"
            SELECT e.EMP_CODE AS [Employee Code], e.EMP_NAME AS [Employee Name],
                   dn.DESIG_NAME AS [Designation], d.DID_NUMBER AS [SIP Username],
                   d.EXTENSION_NUMBER AS [Extension Number],
                   d.SIP_SERVER AS [SIP Server], d.SIP_PORT AS [SIP Port],
                   d.BASE_URL AS [CRM Base URL], d.TOKEN_ID AS [Token ID],
                   d.DID_STATUS AS [Status], d.ASSIGNED_DATE AS [Assigned On]
            FROM dbo.TBL_CIP_EMPLOYEE_DID d
            LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER e ON e.EMP_CODE = d.EMP_CODE
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            WHERE ISNULL(d.IS_DELETED, 0) = 0{scopeSql}
              AND (d.EMP_CODE IS NULL OR (ISNULL(e.ROLE_ID, 0) IN (5, 4, 1007) AND ISNULL(e.ROLE_ID, 0) <> 2010 AND ISNULL(e.DESIG_ID, 0) <> 1014))";
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q OR d.DID_NUMBER LIKE @q OR d.EXTENSION_NUMBER LIKE @q)";
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            sql += " AND d.DID_STATUS = @s";
        if (designationId is > 0)
            sql += " AND e.DESIG_ID = @dg";
        sql += " ORDER BY CASE WHEN d.EMP_CODE IS NULL THEN 1 ELSE 0 END, e.EMP_NAME";
        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (!string.IsNullOrWhiteSpace(status) && status != "All") p.AddWithValue("@s", status);
            if (designationId is > 0) p.AddWithValue("@dg", designationId.Value);
        };

        var dt = await _db.QueryAsync(sql, bind);
        var bytes = Services.ExcelExporter.BuildXlsx(dt, "Employee DIDs");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"EmployeeDIDs_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    /// <summary>Server-paged Employee Master listing (10k+ rows — never load fully).</summary>
    [HttpGet("master")]
    public async Task<ActionResult<object>> Master(
        [FromQuery] string? search = null,
        [FromQuery] int? designationId = null,
        [FromQuery] string? active = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var scopeSql = await ScopeSqlAsync();
        var where = " WHERE ISNULL(e.IS_DELETED,0) = 0" + scopeSql;
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q OR ISNULL(e.EMAIL_ID,'') LIKE @q)";
        if (designationId is > 0)
            where += " AND e.DESIG_ID = @dg";
        if (active == "true") where += " AND ISNULL(e.ACTIVE,0) = 1";
        else if (active == "false") where += " AND ISNULL(e.ACTIVE,0) = 0";
        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (designationId is > 0) p.AddWithValue("@dg", designationId.Value);
        };

        var total = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER e" + where, bind));

        // Whitelisted sort columns (aliases match the page query joins).
        var dir = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderBy = (sortBy?.ToLowerInvariant()) switch
        {
            "code" => $"e.EMP_CODE {dir}",
            "doj" => $"e.DOJ {dir}",
            "designation" => $"dn.DESIG_NAME {dir}",
            "email" => $"e.EMAIL_ID {dir}",
            "location" => $"l.LOC_NAME {dir}",
            "status" => $"e.ACTIVE {dir}",
            _ => $"e.EMP_NAME {dir}"
        };

        var dt = await _db.QueryAsync($@"
            SELECT e.EMP_CODE, CAST(e.EMP_CODE AS VARCHAR(20)) AS EMP_CODE_TEXT, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME,
                   e.DESIG_ID, dn.DESIG_NAME, e.EMAIL_ID, l.LOC_NAME, e.LOCATION_ID, e.DOJ,
                   e.PERSONAL_CONTACT1, e.ACTIVE
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER l ON l.LOC_ID = e.LOCATION_ID
            {where}
            ORDER BY {orderBy}
            OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY", bind);

        var rows = dt.AsEnumerable().Select(r => new
        {
            empCode = r.Field<int>("EMP_CODE"),
            employeeCodeText = r["EMP_CODE_TEXT"].ToString(),
            employeeName = r["EMP_NAME"].ToString(),
            designationId = r["DESIG_ID"] == DBNull.Value ? (int?)null : r.Field<int>("DESIG_ID"),
            designation = r["DESIG_NAME"].ToString(),
            email = r["EMAIL_ID"].ToString(),
            locationId = r["LOCATION_ID"] == DBNull.Value ? (int?)null : r.Field<int>("LOCATION_ID"),
            location = r["LOC_NAME"].ToString(),
            doj = r["DOJ"].ToString(),
            contact = r["PERSONAL_CONTACT1"].ToString(),
            isActive = r["ACTIVE"] != DBNull.Value && Convert.ToBoolean(r["ACTIVE"])
        }).ToList();

        return Ok(new { total, rows, scope = await ScopePayloadAsync() });
    }

    /// <summary>Locations for the Employee Master edit modal dropdown.</summary>
    [HttpGet("locations")]
    public async Task<ActionResult<object>> GetLocations()
    {
        var dt = await _db.QueryAsync(
            "SELECT LOC_ID, LTRIM(RTRIM(LOC_NAME)) AS LOC_NAME FROM dbo.TBL_CIP_LOCATION_MASTER ORDER BY LOC_NAME");
        return Ok(dt.AsEnumerable().Select(r => new { id = r.Field<int>("LOC_ID"), name = r["LOC_NAME"].ToString() }));
    }

    /// <summary>Update an employee's master data (name, designation, email, location, DOJ, contact).</summary>
    [HttpPut("{empCode:int}")]
    public async Task<IActionResult> UpdateEmployee(int empCode, [FromBody] UpdateEmployeeRequest req)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });
        if (string.IsNullOrWhiteSpace(req.EmployeeName))
            return BadRequest(new { error = "Employee name is required." });
        if (!string.IsNullOrWhiteSpace(req.Email) && !System.Text.RegularExpressions.Regex.IsMatch(req.Email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return BadRequest(new { error = "Email format is invalid." });

        var exists = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p => p.AddWithValue("@e", empCode));
        if (Convert.ToInt32(exists) == 0) return NotFound(new { error = "Employee not found." });

        var affected = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER SET
                EMP_NAME = LTRIM(RTRIM(@n)),
                DESIG_ID = @dg,
                EMAIL_ID = NULLIF(LTRIM(RTRIM(@em)), ''),
                LOCATION_ID = @loc,
                DOJ = @doj,
                PERSONAL_CONTACT1 = NULLIF(LTRIM(RTRIM(@c)), ''),
                MODIFIED_BY = @by,
                MODIFIED_DATE = GETDATE()
            WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p =>
        {
            p.AddWithValue("@n", req.EmployeeName.Trim());
            p.AddWithValue("@dg", req.DesignationId ?? (object)DBNull.Value);
            p.AddWithValue("@em", (object?)req.Email?.Trim() ?? DBNull.Value);
            p.AddWithValue("@loc", req.LocationId ?? (object)DBNull.Value);
            p.AddWithValue("@doj", string.IsNullOrWhiteSpace(req.Doj) ? (object)DBNull.Value : req.Doj.Trim());
            p.AddWithValue("@c", (object?)req.Contact?.Trim() ?? DBNull.Value);
            p.AddWithValue("@by", GetEmpCode());
            p.AddWithValue("@e", empCode);
        });
        if (affected == 0) return NotFound(new { error = "Employee not found." });

        _logger.LogInformation("Employee {EmpCode} updated by {User}", empCode, GetEmpCode());
        return Ok(new { ok = true });
    }

    /// <summary>Activate / deactivate an employee (soft status flip, no delete).</summary>
    [HttpPut("{empCode:int}/status")]
    public async Task<IActionResult> SetEmployeeStatus(int empCode, [FromBody] UpdateEmployeeStatusRequest req)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });

        var exists = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p => p.AddWithValue("@e", empCode));
        if (Convert.ToInt32(exists) == 0) return NotFound(new { error = "Employee not found." });

        var affected = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
            SET ACTIVE = @a, MODIFIED_BY = @by, MODIFIED_DATE = GETDATE()
            WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p =>
        {
            p.AddWithValue("@a", req.IsActive ? 1 : 0);
            p.AddWithValue("@by", GetEmpCode());
            p.AddWithValue("@e", empCode);
        });
        if (affected == 0) return NotFound(new { error = "Employee not found." });

        _logger.LogInformation("Employee {EmpCode} status set to {Status} by {User}", empCode, req.IsActive ? "Active" : "Inactive", GetEmpCode());
        return Ok(new { ok = true });
    }

    /// <summary>Soft-delete an employee master row (IS_DELETED = 1; data stays in the DB).</summary>
    [HttpDelete("{empCode:int}")]
    public async Task<IActionResult> DeleteEmployee(int empCode)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });

        // Prevent deleting yourself.
        if (empCode == GetEmpCode())
            return BadRequest(new { error = "You cannot delete your own employee record." });

        // Block delete while any active DID is still assigned to this employee.
        var activeDid = await _db.ScalarAsync(@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID
            WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0 AND DID_STATUS = 'Active'",
            p => p.AddWithValue("@e", empCode));
        if (Convert.ToInt32(activeDid) > 0)
            return BadRequest(new { error = "Release the employee's active DID first, then delete." });

        var affected = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER
            SET IS_DELETED = 1, ACTIVE = 0, MODIFIED_BY = @by, MODIFIED_DATE = GETDATE()
            WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p =>
        {
            p.AddWithValue("@by", GetEmpCode());
            p.AddWithValue("@e", empCode);
        });
        if (affected == 0) return NotFound(new { error = "Employee not found." });

        _logger.LogInformation("Employee {EmpCode} soft-deleted by {User}", empCode, GetEmpCode());
        return Ok(new { ok = true });
    }

    /// <summary>Unassigned employees for the Add Employee dropdown — scoped to the viewer's subtree
    /// (admins see the whole company; a TL only assigns DIDs to their own downline).</summary>
    [HttpGet("without-did")]
    public async Task<ActionResult<object>> EmployeesWithoutDid([FromQuery] string? search = null)
    {
        var scopeSql = await ScopeSqlAsync();
        var sql = $@"
            SELECT TOP 50 e.EMP_CODE, e.EMP_CODE AS EMP_CODE_TEXT, e.EMP_NAME, dn.DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            WHERE ISNULL(e.ACTIVE, 0) = 1 AND ISNULL(e.IS_DELETED, 0) = 0{scopeSql}
              AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_DID d WHERE d.EMP_CODE = e.EMP_CODE AND ISNULL(d.IS_DELETED,0) = 0)
              AND ISNULL(e.ROLE_ID, 0) IN (5, 4, 1007)
              AND ISNULL(e.ROLE_ID, 0) <> 2010 AND ISNULL(e.DESIG_ID, 0) <> 1014";
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q)";
        sql += " ORDER BY e.EMP_NAME";
        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
        };

        var dt = await _db.QueryAsync(sql, bind);
        var list = new List<object>();
        foreach (DataRow r in dt.Rows)
        {
            list.Add(new
            {
                empCode = Convert.ToInt32(r["EMP_CODE"]),
                employeeCodeText = r["EMP_CODE_TEXT"].ToString(),
                employeeName = r["EMP_NAME"].ToString(),
                designation = r["DESIG_NAME"].ToString()
            });
        }
        return Ok(list);
    }

    /// <summary>Assign a new DID (DID_NUMBER holds the SIP username; its last 3
    /// digits are stored as the EXTENSION_NUMBER).
    /// Non-admins may only assign DIDs to employees inside their subtree.</summary>
    [HttpPost("dids")]
    public async Task<IActionResult> AssignDid([FromBody] AssignDidRequest request)
    {
        var did = (request.DidNumber ?? "").Trim();
        if (did.Length == 0) return BadRequest(new { error = "SIP username is required." });

        if (request.EmpCode.HasValue && !IsAdmin)
        {
            if (!await _hierarchy.IsInScopeAsync(EmpCode, RoleId, request.EmpCode.Value))
                return StatusCode(403, new { error = "You can only assign DIDs to employees in your team." });
        }

        var exists = await _db.ScalarAsync(@"
            SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID
            WHERE DID_NUMBER = @did AND ISNULL(IS_DELETED, 0) = 0",
            p => p.AddWithValue("@did", did));
        if (Convert.ToInt32(exists) > 0)
            return Conflict(new { error = $"DID {did} is already assigned." });

        var empCode = await _db.ScalarAsync(@"
            INSERT INTO dbo.TBL_CIP_EMPLOYEE_DID (EMP_CODE, DID_NUMBER, EXTENSION_NUMBER, DID_STATUS, CREATED_BY,
                                                  BASE_URL, CLICK2CALL_PATH, TOKEN_ID, EXTENSION_PASSWORD,
                                                  SIP_SERVER, SIP_PORT, SIP_PASSWORD, SIP_REALM)
            OUTPUT INSERTED.DID_ID
            VALUES (@emp, @did, RIGHT(RTRIM(@did), 3), 'Active', @by,
                    @baseUrl, @c2c, @token, @extPwd, @sipServer, @sipPort, @sipPwd, @sipRealm);",
            p =>
            {
                p.AddWithValue("@emp", request.EmpCode.HasValue ? request.EmpCode.Value : DBNull.Value);
                p.AddWithValue("@did", did);
                p.AddWithValue("@by", GetEmpCode());
                p.AddWithValue("@baseUrl", (object?)request.BaseUrl ?? "https://crm2.cloud-connect.in/ccpl_api/v1.4/");
                p.AddWithValue("@c2c", (object?)request.Click2CallPath ?? "api/info/click2call");
                p.AddWithValue("@token", (object?)request.TokenId ?? "6JU4KMMIO6B1xzLt");
                p.AddWithValue("@extPwd", (object?)request.ExtensionPassword ?? "15e8e7c72ea16ca290930fd7c4db760b");
                p.AddWithValue("@sipServer", (object?)request.SipServer ?? "sip2.cloud-connect.in");
                p.AddWithValue("@sipPort", request.SipPort ?? 7065);
                p.AddWithValue("@sipPwd", (object?)request.SipPassword ?? "Tejas@123");
                p.AddWithValue("@sipRealm", (object?)request.SipRealm ?? "sip2.cloud-connect.in");
            });
        return Ok(new { ok = true, didId = Convert.ToInt32(empCode) });
    }

    /// <summary>Change the SIP username for an assignment (old value kept in history).
    /// The extension is re-synced from the last 3 digits.</summary>
    [HttpPut("dids/{didId:int}")]
    public async Task<IActionResult> ChangeDid(int didId, [FromBody] ChangeDidRequest request)
    {
        var newDid = (request.NewDidNumber ?? "").Trim();
        if (newDid.Length == 0) return BadRequest(new { error = "New SIP username is required." });

        var dt = await _db.QueryAsync(@"SELECT EMP_CODE, DID_NUMBER FROM dbo.TBL_CIP_EMPLOYEE_DID WHERE DID_ID = @id AND ISNULL(IS_DELETED,0)=0",
            p => p.AddWithValue("@id", didId));
        if (dt.Rows.Count == 0) return NotFound(new { error = "DID assignment not found." });

        var oldDid = dt.Rows[0]["DID_NUMBER"].ToString() ?? "";
        var empCode = dt.Rows[0]["EMP_CODE"] == DBNull.Value ? (int?)null : Convert.ToInt32(dt.Rows[0]["EMP_CODE"]);

        if (!string.Equals(oldDid, newDid, StringComparison.OrdinalIgnoreCase))
        {
            var clash = await _db.ScalarAsync(@"
                SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_DID
                WHERE DID_NUMBER = @did AND DID_ID <> @id AND ISNULL(IS_DELETED, 0) = 0",
                p => { p.AddWithValue("@did", newDid); p.AddWithValue("@id", didId); });
            if (Convert.ToInt32(clash) > 0)
                return Conflict(new { error = $"DID {newDid} is already assigned to someone else." });
        }

        using (var conn = new Microsoft.Data.SqlClient.SqlConnection(_db.ConnectionString))
        {
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var update = new Microsoft.Data.SqlClient.SqlCommand(
                    @"UPDATE dbo.TBL_CIP_EMPLOYEE_DID
                      SET DID_NUMBER = @new, EXTENSION_NUMBER = RIGHT(RTRIM(@new), 3),
                          MODIFIED_BY = @by, MODIFIED_DATE = GETDATE(),
                          DID_STATUS = ISNULL(NULLIF(@status, ''), DID_STATUS),
                          BASE_URL = ISNULL(@baseUrl, BASE_URL),
                          CLICK2CALL_PATH = ISNULL(@c2c, CLICK2CALL_PATH),
                          TOKEN_ID = ISNULL(@token, TOKEN_ID),
                          EXTENSION_PASSWORD = ISNULL(@extPwd, EXTENSION_PASSWORD),
                          SIP_SERVER = ISNULL(@sipServer, SIP_SERVER),
                          SIP_PORT = ISNULL(@sipPort, SIP_PORT),
                          SIP_PASSWORD = ISNULL(@sipPwd, SIP_PASSWORD),
                          SIP_REALM = ISNULL(@sipRealm, SIP_REALM)
                      WHERE DID_ID = @id", conn, tx);
                update.Parameters.AddWithValue("@new", newDid);
                update.Parameters.AddWithValue("@status", (object?)request.Status ?? "");
                update.Parameters.AddWithValue("@by", GetEmpCode());
                update.Parameters.AddWithValue("@id", didId);
                update.Parameters.AddWithValue("@baseUrl", (object?)request.BaseUrl ?? DBNull.Value);
                update.Parameters.AddWithValue("@c2c", (object?)request.Click2CallPath ?? DBNull.Value);
                update.Parameters.AddWithValue("@token", (object?)request.TokenId ?? DBNull.Value);
                update.Parameters.AddWithValue("@extPwd", (object?)request.ExtensionPassword ?? DBNull.Value);
                update.Parameters.AddWithValue("@sipServer", (object?)request.SipServer ?? DBNull.Value);
                update.Parameters.AddWithValue("@sipPort", request.SipPort.HasValue ? request.SipPort.Value : DBNull.Value);
                update.Parameters.AddWithValue("@sipPwd", (object?)request.SipPassword ?? DBNull.Value);
                update.Parameters.AddWithValue("@sipRealm", (object?)request.SipRealm ?? DBNull.Value);
                await update.ExecuteNonQueryAsync();

                if (!string.Equals(oldDid, newDid, StringComparison.OrdinalIgnoreCase))
                {
                    var hist = new Microsoft.Data.SqlClient.SqlCommand(
                        @"INSERT INTO dbo.TBL_CIP_EMPLOYEE_DID_HISTORY (EMP_CODE, OLD_DID_NUMBER, NEW_DID_NUMBER, CHANGED_BY)
                          VALUES (@emp, @old, @new, @by)", conn, tx);
                    hist.Parameters.AddWithValue("@emp", empCode ?? throw new InvalidOperationException("Generic DID rows cannot be re-numbered; assign them to an employee instead."));
                    hist.Parameters.AddWithValue("@old", oldDid);
                    hist.Parameters.AddWithValue("@new", newDid);
                    hist.Parameters.AddWithValue("@by", GetEmpCode());
                    await hist.ExecuteNonQueryAsync();
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return Ok(new { ok = true });
    }

    /// <summary>Soft-delete a DID assignment (or just deactivate it with ?status=Inactive).</summary>
    [HttpDelete("dids/{didId:int}")]
    public async Task<IActionResult> ReleaseDid(int didId)
    {
        var n = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_EMPLOYEE_DID
            SET IS_DELETED = 1, DID_STATUS = 'Released', RELEASED_DATE = GETDATE(), MODIFIED_BY = @by, MODIFIED_DATE = GETDATE()
            WHERE DID_ID = @id",
            p => { p.AddWithValue("@id", didId); p.AddWithValue("@by", GetEmpCode()); });
        return n == 0 ? NotFound(new { error = "DID assignment not found." }) : Ok(new { ok = true });
    }

    // ------------------------------------------------------------------ documents

    private static readonly string[] AllowedDocTypes =
        { "PAN", "Aadhaar", "Offer Letter", "Appointment Letter", "Bank", "Photo", "Other" };
    private const long MaxDocBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly string[] AllowedDocExt =
        { ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".doc", ".docx" };

    /// <summary>Storage root for employee documents (per-employee subfolder).</summary>
    private static string DocsRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "employee documents");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>List documents of an employee (hierarchy-scoped). Offer letters come first, newest first.</summary>
    [HttpGet("{empCode:int}/documents")]
    public async Task<IActionResult> ListDocuments(int empCode)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });

        var dt = await _db.QueryAsync(@"
            SELECT DOC_ID, EMP_CODE, DOC_TYPE, FILE_NAME, FILE_SIZE, CONTENT_TYPE, SOURCE, UPLOADED_BY_NAME, CREATED_DATE
            FROM dbo.TBL_CIP_EMPLOYEE_DOCUMENTS
            WHERE EMP_CODE = @e AND IS_DELETED = 0
            ORDER BY CASE WHEN SOURCE = 'OFFER' THEN 0 ELSE 1 END, DOC_ID DESC",
            p => p.AddWithValue("@e", empCode));

        var rows = dt.AsEnumerable().Select(r => new
        {
            docId = r.Field<int>("DOC_ID"),
            empCode = r.Field<int>("EMP_CODE"),
            docType = r["DOC_TYPE"].ToString(),
            fileName = r["FILE_NAME"].ToString(),
            fileSize = r.Field<int>("FILE_SIZE"),
            contentType = r["CONTENT_TYPE"].ToString(),
            source = r["SOURCE"].ToString(),
            uploadedByName = r["UPLOADED_BY_NAME"].ToString(),
            createdDate = (DateTime)r["CREATED_DATE"],
        });
        return Ok(rows);
    }

    /// <summary>Upload a document (PAN / Aadhaar / etc.) for an employee — multipart form:
    /// file (required), docType (required, one of the allowed types).</summary>
    [HttpPost("{empCode:int}/documents")]
    public async Task<IActionResult> UploadDocument(int empCode, IFormFile file, [FromForm] string docType)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "Select a file to upload." });
        if (file.Length > MaxDocBytes) return BadRequest(new { error = "File exceeds the 10 MB limit." });
        if (string.IsNullOrWhiteSpace(docType) || !AllowedDocTypes.Contains(docType.Trim(), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"docType must be one of: {string.Join(", ", AllowedDocTypes)}." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedDocExt.Contains(ext))
            return BadRequest(new { error = $"Extension '{ext}' is not allowed ({string.Join(", ", AllowedDocExt)})." });

        var empExists = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE EMP_CODE = @e AND ISNULL(IS_DELETED,0) = 0",
            p => p.AddWithValue("@e", empCode));
        if (Convert.ToInt32(empExists) == 0) return NotFound(new { error = "Employee not found." });

        var folder = Path.Combine(DocsRoot(), empCode.ToString());
        Directory.CreateDirectory(folder);
        var stored = $"{docType.Trim().Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        var fullPath = Path.Combine(folder, stored);
        await using (var fs = System.IO.File.Create(fullPath))
            await file.CopyToAsync(fs);

        await _db.ExecuteAsync(@"
            INSERT INTO dbo.TBL_CIP_EMPLOYEE_DOCUMENTS
                (EMP_CODE, DOC_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, CONTENT_TYPE, SOURCE, UPLOADED_BY, UPLOADED_BY_NAME)
            VALUES (@e, @t, @fn, @fp, @sz, @ct, 'UPLOAD', @by, @bn)", p =>
        {
            p.AddWithValue("@e", empCode);
            p.AddWithValue("@t", docType.Trim());
            p.AddWithValue("@fn", file.FileName);
            p.AddWithValue("@fp", Path.Combine("employee documents", empCode.ToString(), stored));
            p.AddWithValue("@sz", (int)file.Length);
            p.AddWithValue("@ct", file.ContentType ?? "");
            p.AddWithValue("@by", GetEmpCode());
            p.AddWithValue("@bn", User.FindFirst("empName")?.Value ?? "");
        });

        return Ok(new { ok = true, docType = docType.Trim(), fileName = file.FileName });
    }

    /// <summary>Stream a document (inline preview when ?download is absent).</summary>
    [HttpGet("documents/{docId:int}")]
    public async Task<IActionResult> GetDocument(int docId, [FromQuery] bool download = false)
    {
        var dt = await _db.QueryAsync(
            "SELECT EMP_CODE, FILE_NAME, FILE_PATH, CONTENT_TYPE FROM dbo.TBL_CIP_EMPLOYEE_DOCUMENTS WHERE DOC_ID = @id AND IS_DELETED = 0",
            p => p.AddWithValue("@id", docId));
        if (dt.Rows.Count == 0) return NotFound(new { error = "Document not found." });

        var r = dt.Rows[0];
        var empCode = r.Field<int>("EMP_CODE");
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This document is outside your team's scope." });

        var rel = r["FILE_PATH"].ToString()!;
        var fullPath = Path.Combine(AppContext.BaseDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath)) return NotFound(new { error = "File is missing on disk." });

        var name = r["FILE_NAME"].ToString()!;
        var ct = string.IsNullOrWhiteSpace(r["CONTENT_TYPE"].ToString()) ? "application/octet-stream" : r["CONTENT_TYPE"].ToString();
        var bytes = await System.IO.File.ReadAllBytesAsync(fullPath);
        return File(bytes, ct, download ? name : null, enableRangeProcessing: true);
    }

    /// <summary>Soft-delete a document.</summary>
    [HttpDelete("documents/{docId:int}")]
    public async Task<IActionResult> DeleteDocument(int docId)
    {
        var dt = await _db.QueryAsync(
            "SELECT EMP_CODE, FILE_PATH FROM dbo.TBL_CIP_EMPLOYEE_DOCUMENTS WHERE DOC_ID = @id AND IS_DELETED = 0",
            p => p.AddWithValue("@id", docId));
        if (dt.Rows.Count == 0) return NotFound(new { error = "Document not found." });

        var empCode = dt.Rows[0].Field<int>("EMP_CODE");
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This document is outside your team's scope." });

        await _db.ExecuteAsync(
            "UPDATE dbo.TBL_CIP_EMPLOYEE_DOCUMENTS SET IS_DELETED = 1 WHERE DOC_ID = @id",
            p => p.AddWithValue("@id", docId));
        return Ok(new { ok = true });
    }

    /// <summary>Generate an offer letter PDF from the employee's master data
    /// (name, designation, DOJ, CTC = SALARY x 12 — mirrors the legacy
    /// cv.docx → OfferLetter flow), stores it as an OFFER document and streams it back.</summary>
    [HttpPost("{empCode:int}/offer-letter")]
    public async Task<IActionResult> GenerateOfferLetter(int empCode)
    {
        if (!await _hierarchy.IsInScopeAsync(GetEmpCode(), RoleId, empCode))
            return StatusCode(403, new { error = "This employee is outside your team's scope." });

        var dt = await _db.QueryAsync(@"
            SELECT e.EMP_NAME, dn.DESIG_NAME, e.DOJ, e.SALARY, l.LOC_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            LEFT JOIN dbo.TBL_CIP_LOCATION_MASTER l ON l.LOC_ID = e.LOCATION_ID
            WHERE e.EMP_CODE = @e AND ISNULL(e.IS_DELETED,0) = 0",
            p => p.AddWithValue("@e", empCode));
        if (dt.Rows.Count == 0) return NotFound(new { error = "Employee not found." });

        var r = dt.Rows[0];
        var name = r["EMP_NAME"].ToString()?.Trim() ?? "";
        var designation = r["DESIG_NAME"].ToString()?.Trim() ?? "";
        var location = r["LOC_NAME"].ToString()?.Trim() ?? "";
        var dojRaw = r["DOJ"].ToString() ?? "";
        var doj = DateTime.TryParse(dojRaw, out var d) ? d : DateTime.Today;
        var ctcAnnual = Convert.ToDecimal(r["SALARY"] == DBNull.Value ? 0m : r["SALARY"]) * 12m;

        var pdf = OfferLetterPdf.Build(new OfferLetterPdf.OfferData
        {
            CandidateName = name,
            Designation = designation,
            Location = location,
            Doj = doj,
            AnnualCtc = ctcAnnual,
            CompanyName = "1UP Pest Control Solutions",
        });

        var fileName = $"OfferLetter_{name.Replace(" ", "_")}_{doj:yyyyMMdd}.pdf";
        var folder = Path.Combine(DocsRoot(), empCode.ToString());
        Directory.CreateDirectory(folder);
        var stored = $"Offer_Letter_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, stored), pdf);

        await _db.ExecuteAsync(@"
            INSERT INTO dbo.TBL_CIP_EMPLOYEE_DOCUMENTS
                (EMP_CODE, DOC_TYPE, FILE_NAME, FILE_PATH, FILE_SIZE, CONTENT_TYPE, SOURCE, UPLOADED_BY, UPLOADED_BY_NAME)
            VALUES (@e, 'Offer Letter', @fn, @fp, @sz, 'application/pdf', 'OFFER', @by, @bn)", p =>
        {
            p.AddWithValue("@e", empCode);
            p.AddWithValue("@fn", fileName);
            p.AddWithValue("@fp", Path.Combine("employee documents", empCode.ToString(), stored));
            p.AddWithValue("@sz", pdf.Length);
            p.AddWithValue("@by", GetEmpCode());
            p.AddWithValue("@bn", User.FindFirst("empName")?.Value ?? "");
        });

        return File(pdf, "application/pdf", fileName);
    }

    private int GetEmpCode()
    {
        var v = User.FindFirst("empCode")?.Value;
        return int.TryParse(v, out var code) ? code : 0;
    }
}
