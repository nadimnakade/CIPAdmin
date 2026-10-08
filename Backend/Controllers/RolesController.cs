using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class RoleDto
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public bool IsActive { get; set; }
    public int EmployeeCount { get; set; }
}

public class SaveRoleRequest
{
    public string RoleName { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class RoleStatusRequest
{
    public bool IsActive { get; set; }
}

public class SaveRoleWithMenus : SaveRoleRequest
{
    public List<int>? MenuIds { get; set; }
}

[ApiController]
[Route("api/roles")]
[Authorize(Policy = "Admin")]
public class RolesController : ControllerBase
{
    private const int ConnectorRoleId = EmployeeFilters.ConnectorRoleId;

    private readonly IDbContext _db;
    private readonly ILogger<RolesController> _logger;

    public RolesController(IDbContext db, ILogger<RolesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);

    [HttpGet]
    public async Task<ActionResult<object>> List([FromQuery] string? search = null, [FromQuery] string? active = null)
    {
        var where = " WHERE r.ROLE_ID <> " + ConnectorRoleId;
        if (!string.IsNullOrWhiteSpace(search)) where += " AND r.ROLE_NAME LIKE @q";
        if (active == "true") where += " AND ISNULL(r.ROLE_ACTIVE,0) = 1";
        else if (active == "false") where += " AND ISNULL(r.ROLE_ACTIVE,0) = 0";

        Action<SqlParameterCollection>? bind = null;
        if (!string.IsNullOrWhiteSpace(search)) bind = p => p.AddWithValue("@q", "%" + search.Trim() + "%");

        var dt = await _db.QueryAsync($@"
            SELECT r.ROLE_ID, r.ROLE_NAME, r.ROLE_ACTIVE,
                   (SELECT COUNT(1)
                    FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                    WHERE e.ROLE_ID = r.ROLE_ID
                      AND {EmployeeFilters.Active("e")}
                      AND {EmployeeFilters.NotDeleted("e")}
                      AND {EmployeeFilters.NotConnector("e")}) AS EMP_COUNT
            FROM dbo.TBL_CIP_ROLE_MASTER r{where}
            ORDER BY r.ROLE_ID", bind);

        var rows = dt.AsEnumerable().Select(r => new RoleDto
        {
            RoleId = r.Field<int>("ROLE_ID"),
            RoleName = r["ROLE_NAME"].ToString() ?? "",
            IsActive = r["ROLE_ACTIVE"] != DBNull.Value && Convert.ToBoolean(r["ROLE_ACTIVE"]),
            EmployeeCount = r["EMP_COUNT"] == DBNull.Value ? 0 : Convert.ToInt32(r["EMP_COUNT"])
        }).ToList();
        return Ok(new { rows });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveRoleWithMenus body)
    {
        if (string.Equals(body.RoleName?.Trim(), "CONNECTOR", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "The CONNECTOR role is reserved and excluded from CIPAdmin." });
        if (string.IsNullOrWhiteSpace(body.RoleName)) return BadRequest(new { error = "Role name is required." });
        var name = body.RoleName.Trim();
        if (name.Length > 100) return BadRequest(new { error = "Role name exceeds 100 characters." });

        var dup = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MASTER WHERE LTRIM(RTRIM(ROLE_NAME)) = @n",
            p => p.AddWithValue("@n", name));
        if (Convert.ToInt32(dup) > 0) return Conflict(new { error = $"A role named \"{name}\" already exists." });

        var dt = await _db.QueryAsync(@"
            INSERT INTO dbo.TBL_CIP_ROLE_MASTER (ROLE_NAME, ROLE_ACTIVE)
            OUTPUT INSERTED.ROLE_ID
            VALUES (@n, @a);",
            p =>
            {
                p.AddWithValue("@n", name);
                p.AddWithValue("@a", body.IsActive ? 1 : 0);
            });
        var roleId = dt.Rows.Count > 0 ? Convert.ToInt32(dt.Rows[0][0]) : 0;
        var menuIds = (body.MenuIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        if (roleId > 0 && menuIds.Count > 0) await ReplaceMenusAsync(roleId, menuIds);

        _logger.LogInformation("Role {RoleId} ({Name}) created by {User}", roleId, name, EmpCode);
        return Ok(new { ok = true, roleId });
    }

    [HttpPut("{roleId:int}")]
    public async Task<IActionResult> Update(int roleId, [FromBody] SaveRoleRequest body)
    {
        if (roleId == ConnectorRoleId) return NotFound(new { error = "Role not found." });
        if (string.Equals(body.RoleName?.Trim(), "CONNECTOR", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "The CONNECTOR role is reserved and excluded from CIPAdmin." });
        if (string.IsNullOrWhiteSpace(body.RoleName)) return BadRequest(new { error = "Role name is required." });
        var name = body.RoleName.Trim();

        var dt = await _db.QueryAsync("SELECT ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @r", p => p.AddWithValue("@r", roleId));
        if (dt.Rows.Count == 0) return NotFound(new { error = "Role not found." });
        var existingName = dt.Rows[0]["ROLE_NAME"].ToString() ?? "";
        if (roleId is 1 or 2 && !string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Built-in admin roles (Super Admin / Admin) cannot be renamed." });

        var dup = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MASTER WHERE LTRIM(RTRIM(ROLE_NAME)) = @n AND ROLE_ID <> @r",
            p => { p.AddWithValue("@n", name); p.AddWithValue("@r", roleId); });
        if (Convert.ToInt32(dup) > 0) return Conflict(new { error = $"A role named \"{name}\" already exists." });

        var affected = await _db.ExecuteAsync(
            "UPDATE dbo.TBL_CIP_ROLE_MASTER SET ROLE_NAME = @n, ROLE_ACTIVE = @a WHERE ROLE_ID = @r",
            p => { p.AddWithValue("@n", name); p.AddWithValue("@a", body.IsActive ? 1 : 0); p.AddWithValue("@r", roleId); });
        if (affected == 0) return NotFound(new { error = "Role not found." });
        _logger.LogInformation("Role {RoleId} updated by {User}", roleId, EmpCode);
        return Ok(new { ok = true });
    }

    [HttpPut("{roleId:int}/status")]
    public async Task<IActionResult> SetStatus(int roleId, [FromBody] RoleStatusRequest body)
    {
        if (roleId == ConnectorRoleId) return NotFound(new { error = "Role not found." });
        if (roleId is 1 or 2 && !body.IsActive) return BadRequest(new { error = "Built-in admin roles cannot be deactivated." });
        if (roleId == EmpCodeRole() && !body.IsActive) return BadRequest(new { error = "You cannot deactivate your own role." });

        var affected = await _db.ExecuteAsync(
            "UPDATE dbo.TBL_CIP_ROLE_MASTER SET ROLE_ACTIVE = @a WHERE ROLE_ID = @r",
            p => { p.AddWithValue("@a", body.IsActive ? 1 : 0); p.AddWithValue("@r", roleId); });
        if (affected == 0) return NotFound(new { error = "Role not found." });
        _logger.LogInformation("Role {RoleId} status set to {Status} by {User}", roleId, body.IsActive ? "Active" : "Inactive", EmpCode);
        return Ok(new { ok = true });
    }

    [HttpDelete("{roleId:int}")]
    public async Task<IActionResult> Delete(int roleId)
    {
        if (roleId == ConnectorRoleId) return NotFound(new { error = "Role not found." });
        if (roleId is 1 or 2) return BadRequest(new { error = "Built-in admin roles cannot be deleted." });
        if (roleId == EmpCodeRole()) return BadRequest(new { error = "You cannot delete your own role." });

        // Do not remove a role merely because its remaining holders are inactive,
        // deleted, or connectors: those rows still reference the role in master data.
        var inUse = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.ROLE_ID = @r",
            p => p.AddWithValue("@r", roleId));
        if (Convert.ToInt32(inUse) > 0)
            return BadRequest(new { error = "This role is assigned to employees. Move them to another role first." });

        await _db.ExecuteAsync(@"
            DELETE FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r;
            DELETE FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @r;",
            p => p.AddWithValue("@r", roleId));
        var still = await _db.ScalarAsync("SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @r", p => p.AddWithValue("@r", roleId));
        if (Convert.ToInt32(still) > 0) return NotFound(new { error = "Role not found." });
        _logger.LogInformation("Role {RoleId} deleted by {User}", roleId, EmpCode);
        return Ok(new { ok = true });
    }

    [HttpGet("privileges")]
    public async Task<ActionResult<object>> Privileges()
    {
        var menus = await _db.QueryAsync(@"
            SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE
            FROM dbo.TBL_CIP_MENU_MASTER
            ORDER BY CASE WHEN PARENT_ID IS NULL THEN SORT_ORDER ELSE 99 END, MENU_ID");
        var grants = await _db.QueryAsync(
            "SELECT ROLE_ID, MENU_ID FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID <> @connector",
            p => p.AddWithValue("@connector", ConnectorRoleId));

        var menuList = menus.AsEnumerable().Select(r => new
        {
            menuId = r.Field<int>("MENU_ID"),
            label = r["MENU_LABEL"].ToString(),
            path = r["MENU_PATH"] == DBNull.Value ? null : r["MENU_PATH"].ToString(),
            parentId = r["PARENT_ID"] == DBNull.Value ? (int?)null : r.Field<int>("PARENT_ID"),
            isActive = r["IS_ACTIVE"] != DBNull.Value && Convert.ToBoolean(r["IS_ACTIVE"])
        }).ToList();
        var grantSet = grants.AsEnumerable().Select(r => new { roleId = r.Field<int>("ROLE_ID"), menuId = r.Field<int>("MENU_ID") })
            .GroupBy(x => x.roleId).ToDictionary(g => g.Key, g => g.Select(x => x.menuId).ToList());
        return Ok(new { menus = menuList, grants = grantSet });
    }

    [HttpGet("employee-privileges")]
    public async Task<ActionResult<object>> EmployeePrivileges(
        [FromQuery] string? search = null, [FromQuery] int? roleId = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var where = @"WHERE " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e");
        if (!string.IsNullOrWhiteSpace(search)) where += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q)";
        if (roleId is > 0 && roleId != ConnectorRoleId) where += " AND e.ROLE_ID = @rid";
        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (roleId is > 0 && roleId != ConnectorRoleId) p.AddWithValue("@rid", roleId.Value);
        };

        var total = Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(1) FROM dbo.TBL_CIP_EMPLOYEE_MASTER e {where}", bind));
        var dt = await _db.QueryAsync($@"
            SELECT e.EMP_CODE, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME, e.ROLE_ID, r.ROLE_NAME,
                   (SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MENU rm WHERE rm.ROLE_ID = e.ROLE_ID) AS MENU_COUNT
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_ROLE_MASTER r ON r.ROLE_ID = e.ROLE_ID
            {where}
            ORDER BY e.EMP_NAME OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY", bind);
        var rows = dt.AsEnumerable().Select(r => new
        {
            empCode = r.Field<int>("EMP_CODE"), empName = r["EMP_NAME"].ToString(),
            roleId = r["ROLE_ID"] == DBNull.Value ? (int?)null : r.Field<int>("ROLE_ID"),
            roleName = r["ROLE_NAME"].ToString() ?? "—", menuCount = Convert.ToInt32(r["MENU_COUNT"])
        }).ToList();
        return Ok(new { total, rows });
    }

    [HttpGet("employee-privileges/{empCode:int}")]
    public async Task<ActionResult<object>> EmployeePrivilegeDetail(int empCode)
    {
        var emp = await _db.QueryAsync(@"
            SELECT e.EMP_CODE, LTRIM(RTRIM(e.EMP_NAME)) AS EMP_NAME, e.ROLE_ID, r.ROLE_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_ROLE_MASTER r ON r.ROLE_ID = e.ROLE_ID
            WHERE e.EMP_CODE = @e AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@e", empCode));
        if (emp.Rows.Count == 0) return NotFound(new { error = "Employee not found (inactive employees and connectors are excluded)." });

        var er = emp.Rows[0];
        int? roleId = er["ROLE_ID"] == DBNull.Value ? null : er.Field<int>("ROLE_ID");
        var menusDt = await _db.QueryAsync(@"
            SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID
            FROM dbo.TBL_CIP_MENU_MASTER WHERE IS_ACTIVE = 1
            ORDER BY CASE WHEN PARENT_ID IS NULL THEN SORT_ORDER ELSE 99 END, MENU_ID");
        var roleGrants = new HashSet<int>();
        if (roleId.HasValue)
        {
            var g = await _db.QueryAsync("SELECT MENU_ID FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r", p => p.AddWithValue("@r", roleId.Value));
            foreach (DataRow row in g.Rows) roleGrants.Add(row.Field<int>("MENU_ID"));
        }
        var overrides = new Dictionary<int, bool>();
        var o = await _db.QueryAsync("SELECT MENU_ID, GRANTED FROM dbo.TBL_CIP_EMPLOYEE_MENU WHERE EMP_CODE = @e", p => p.AddWithValue("@e", empCode));
        foreach (DataRow row in o.Rows) overrides[row.Field<int>("MENU_ID")] = Convert.ToBoolean(row["GRANTED"]);

        var menus = menusDt.AsEnumerable().Select(row =>
        {
            int menuId = row.Field<int>("MENU_ID");
            bool roleGranted = roleGrants.Contains(menuId);
            bool? ov = overrides.TryGetValue(menuId, out var b) ? b : null;
            return new
            {
                menuId, label = row["MENU_LABEL"].ToString(),
                path = row["MENU_PATH"] == DBNull.Value ? null : row["MENU_PATH"].ToString(),
                parentId = row["PARENT_ID"] == DBNull.Value ? (int?)null : row.Field<int>("PARENT_ID"),
                roleGranted, overrideGranted = ov, granted = ov ?? roleGranted
            };
        }).ToList();
        return Ok(new { empCode, empName = er["EMP_NAME"].ToString(), roleId, roleName = er["ROLE_NAME"].ToString() ?? "—", menus });
    }

    [HttpPut("employee-privileges/{empCode:int}")]
    public async Task<IActionResult> SaveEmployeePrivileges(int empCode, [FromBody] RoleMenuSave body)
    {
        var emp = await _db.QueryAsync(@"
            SELECT e.ROLE_ID FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE e.EMP_CODE = @e AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@e", empCode));
        if (emp.Rows.Count == 0) return NotFound(new { error = "Employee not found (inactive employees and connectors are excluded)." });

        int roleId = emp.Rows[0]["ROLE_ID"] == DBNull.Value ? 0 : emp.Rows[0].Field<int>("ROLE_ID");
        if (HierarchyService.IsAdminRole(roleId)) return BadRequest(new { error = "Admin roles already have all menus — per-employee overrides do not apply." });
        if (empCode == EmpCode) return BadRequest(new { error = "You cannot change your own privileges." });

        var roleGrants = new HashSet<int>();
        var g = await _db.QueryAsync("SELECT MENU_ID FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r", p => p.AddWithValue("@r", roleId));
        foreach (DataRow row in g.Rows) roleGrants.Add(row.Field<int>("MENU_ID"));
        var desired = new HashSet<int>((body.MenuIds ?? new List<int>()).Where(i => i > 0));

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var del = new SqlCommand("DELETE FROM dbo.TBL_CIP_EMPLOYEE_MENU WHERE EMP_CODE = @e", conn, tx))
            {
                del.Parameters.AddWithValue("@e", empCode);
                await del.ExecuteNonQueryAsync();
            }
            var allMenus = await _db.QueryAsync("SELECT MENU_ID FROM dbo.TBL_CIP_MENU_MASTER WHERE IS_ACTIVE = 1");
            var inserts = new List<(int menuId, bool granted)>();
            foreach (DataRow row in allMenus.Rows)
            {
                int menuId = row.Field<int>("MENU_ID");
                bool roleHas = roleGrants.Contains(menuId);
                bool want = desired.Contains(menuId);
                if (want != roleHas) inserts.Add((menuId, want));
            }
            if (inserts.Count > 0)
            {
                var values = string.Join(",", inserts.Select((_, i) => $"(@e,@m{i},@g{i},@by)"));
                await using var ins = new SqlCommand($"INSERT INTO dbo.TBL_CIP_EMPLOYEE_MENU (EMP_CODE, MENU_ID, GRANTED, GRANTED_BY) VALUES {values}", conn, tx);
                ins.Parameters.AddWithValue("@e", empCode);
                for (int i = 0; i < inserts.Count; i++)
                {
                    ins.Parameters.AddWithValue($"@m{i}", inserts[i].menuId);
                    ins.Parameters.AddWithValue($"@g{i}", inserts[i].granted ? 1 : 0);
                }
                ins.Parameters.AddWithValue("@by", EmpCode);
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        _logger.LogInformation("Employee privileges for {EmpCode} saved by {User} ({Count} menus)", empCode, EmpCode, desired.Count);
        return Ok(new { saved = desired.Count });
    }

    [HttpPut("{roleId:int}/privileges")]
    public async Task<IActionResult> SavePrivileges(int roleId, [FromBody] RoleMenuSave body)
    {
        if (roleId == ConnectorRoleId) return NotFound(new { error = "Role not found." });
        var roleExists = await _db.ScalarAsync("SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @r", p => p.AddWithValue("@r", roleId));
        if (Convert.ToInt32(roleExists) == 0) return NotFound(new { error = "Role not found." });
        var ids = (body.MenuIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        if (HierarchyService.IsAdminRole(roleId) && !ids.Any()) return BadRequest(new { error = "Admin roles must keep at least one menu." });
        await ReplaceMenusAsync(roleId, ids);
        _logger.LogInformation("Privileges for role {RoleId} saved by {User} ({Count} menus)", roleId, EmpCode, ids.Count);
        return Ok(new { saved = ids.Count });
    }

    private async Task ReplaceMenusAsync(int roleId, List<int> menuIds)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var del = new SqlCommand("DELETE FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r", conn, tx))
            {
                del.Parameters.AddWithValue("@r", roleId);
                await del.ExecuteNonQueryAsync();
            }
            if (menuIds.Count > 0)
            {
                var values = string.Join(",", menuIds.Select((_, i) => $"(@r,@m{i})"));
                await using var ins = new SqlCommand($"INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID) VALUES {values}", conn, tx);
                ins.Parameters.AddWithValue("@r", roleId);
                for (int i = 0; i < menuIds.Count; i++) ins.Parameters.AddWithValue($"@m{i}", menuIds[i]);
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private int EmpCodeRole() => int.TryParse(User.FindFirst(ClaimTypes.Role)?.Value, out var role) ? role : 0;
}
