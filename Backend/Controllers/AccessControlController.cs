using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class AccessRowDto
{
    public int EmpCode { get; set; }
    public string EmpName { get; set; } = "";
    public string Username { get; set; } = "";
    public int? RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public string AccessSource { get; set; } = "";   // "Role default" | "Granted" | "Denied"
    public bool HasAccess { get; set; }
    public string? GrantedByName { get; set; }
    public DateTime? GrantedAt { get; set; }
}

public class SetAccessRequest
{
    public bool HasAccess { get; set; }
}

/// <summary>
/// Access Control master — admins decide which employees may sign in to the website.
/// Admin/Super Admin/TL roles have access by default; every other role needs an explicit
/// grant in TBL_CIP_WEB_ACCESS. Gating is enforced at login by AccessControlService.
/// </summary>
[ApiController]
[Route("api/access-control")]
[Authorize(Policy = "Admin")]
public class AccessControlController : ControllerBase
{
    private const int TlRoleId = 4;

    private readonly IDbContext _db;
    private readonly ILogger<AccessControlController> _logger;

    public AccessControlController(IDbContext db, ILogger<AccessControlController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);

    /// <summary>Paged employee list with each employee's effective web-access state.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] string? search = null,
        [FromQuery] int? roleId = null,
        [FromQuery] string? access = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var where = @"
            WHERE ISNULL(e.IS_DELETED, 0) = 0
              AND ISNULL(e.ACTIVE, 0) = 1
              AND e.EMP_CODE <> @self
              AND ISNULL(e.ROLE_ID, 0) <> 2010
              AND ISNULL(e.DESIG_ID, 0) <> 1014";
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (e.EMP_NAME LIKE @q OR e.USERNAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q OR e.EMAIL_ID LIKE @q)";
        if (roleId is > 0) where += " AND e.ROLE_ID = @rid";
        if (access == "granted") where += " AND (a.HAS_ACCESS = 1 OR (e.ROLE_ID IN (1,2,4) AND ISNULL(a.HAS_ACCESS,1) = 1))";
        else if (access == "denied") where += " AND (a.HAS_ACCESS = 0 OR (e.ROLE_ID NOT IN (1,2,4) AND a.EMP_CODE IS NULL))";

        Action<SqlParameterCollection> bind = p =>
        {
            p.AddWithValue("@self", EmpCode);
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (roleId is > 0) p.AddWithValue("@rid", roleId.Value);
        };

        var total = Convert.ToInt32(await _db.ScalarAsync($@"
            SELECT COUNT(1)
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_WEB_ACCESS a ON a.EMP_CODE = e.EMP_CODE
            {where}", bind));

        var dt = await _db.QueryAsync($@"
            SELECT e.EMP_CODE, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME, e.USERNAME, e.ROLE_ID,
                   r.ROLE_NAME, a.HAS_ACCESS, a.GRANTED_AT,
                   (SELECT TOP 1 LTRIM(RTRIM(g.EMP_NAME)) FROM dbo.TBL_CIP_EMPLOYEE_MASTER g WHERE g.EMP_CODE = a.GRANTED_BY) AS GRANTED_BY_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_ROLE_MASTER r ON r.ROLE_ID = e.ROLE_ID
            LEFT JOIN dbo.TBL_CIP_WEB_ACCESS a ON a.EMP_CODE = e.EMP_CODE
            {where}
            ORDER BY e.EMP_NAME
            OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY", bind);

        var rows = dt.AsEnumerable().Select(r =>
        {
            int roleId_ = r["ROLE_ID"] == DBNull.Value ? 0 : r.Field<int>("ROLE_ID");
            bool roleDefault = roleId_ is 1 or 2 or TlRoleId;
            bool? overrideFlag = r["HAS_ACCESS"] == DBNull.Value ? null : Convert.ToBoolean(r["HAS_ACCESS"]);
            bool hasAccess = overrideFlag ?? roleDefault;
            string source = overrideFlag == null
                ? (roleDefault ? "Role default" : "Not granted")
                : (hasAccess ? "Granted" : "Denied");

            return new AccessRowDto
            {
                EmpCode = r.Field<int>("EMP_CODE"),
                EmpName = r["EMP_NAME"].ToString() ?? "",
                Username = r["USERNAME"].ToString() ?? "",
                RoleId = r["ROLE_ID"] == DBNull.Value ? null : roleId_,
                RoleName = r["ROLE_NAME"].ToString() ?? "—",
                HasAccess = hasAccess,
                AccessSource = source,
                GrantedByName = r["GRANTED_BY_NAME"] == DBNull.Value ? null : r["GRANTED_BY_NAME"].ToString(),
                GrantedAt = r["GRANTED_AT"] == DBNull.Value ? null : r.Field<DateTime>("GRANTED_AT")
            };
        }).ToList();

        return Ok(new { total, rows });
    }

    /// <summary>Grant or revoke web access for one employee.</summary>
    [HttpPut("{empCode:int}")]
    public async Task<IActionResult> SetAccess(int empCode, [FromBody] SetAccessRequest body)
    {
        var emp = await _db.QueryAsync(@"
            SELECT EMP_CODE, LTRIM(RTRIM(EMP_NAME)) AS EMP_NAME, ROLE_ID
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER
            WHERE EMP_CODE = @c AND ISNULL(IS_DELETED, 0) = 0",
            p => p.AddWithValue("@c", empCode));
        if (emp.Rows.Count == 0) return NotFound(new { error = "Employee not found." });

        await _db.ExecuteAsync(@"
            MERGE dbo.TBL_CIP_WEB_ACCESS AS t
            USING (SELECT @c AS EMP_CODE) AS s ON t.EMP_CODE = s.EMP_CODE
            WHEN MATCHED THEN UPDATE SET HAS_ACCESS = @a, GRANTED_BY = @by, GRANTED_AT = GETDATE()
            WHEN NOT MATCHED THEN INSERT (EMP_CODE, HAS_ACCESS, GRANTED_BY, GRANTED_AT)
                 VALUES (@c, @a, @by, GETDATE());",
            p =>
            {
                p.AddWithValue("@c", empCode);
                p.AddWithValue("@a", body.HasAccess ? 1 : 0);
                p.AddWithValue("@by", EmpCode);
            });

        _logger.LogInformation("Web access {Action} for emp {Code} by {User}",
            body.HasAccess ? "granted" : "revoked", empCode, EmpCode);
        return Ok(new { ok = true, hasAccess = body.HasAccess });
    }

    /// <summary>Remove the per-employee override — the role default applies again.</summary>
    [HttpDelete("{empCode:int}")]
    public async Task<IActionResult> ClearOverride(int empCode)
    {
        var affected = await _db.ExecuteAsync(
            "DELETE FROM dbo.TBL_CIP_WEB_ACCESS WHERE EMP_CODE = @c",
            p => p.AddWithValue("@c", empCode));
        return Ok(new { ok = true, removed = affected });
    }
}
