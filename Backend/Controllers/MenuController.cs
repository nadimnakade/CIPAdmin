using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class MenuNode
{
    public int MenuId { get; set; }
    public string Label { get; set; } = "";
    public string? Path { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public List<MenuNode> Children { get; set; } = new();
}

public class RoleMenuSave
{
    public List<int> MenuIds { get; set; } = new();
}

/// <summary>
/// Menu Master — drives which sidebar menus each role sees
/// (TBL_CIP_MENU_MASTER + TBL_CIP_ROLE_MENU, roles from TBL_CIP_ROLE_MASTER).
/// </summary>
[ApiController]
[Route("api/menus")]
[Authorize]
public class MenuController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IHierarchyService _hierarchy;

    public MenuController(IDbContext db, IHierarchyService hierarchy)
    {
        _db = db; _hierarchy = hierarchy;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private int RoleId => int.TryParse(User.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : 0;
    private bool IsAdmin => HierarchyService.IsAdminRole(RoleId);

    private const string BaseSelect = @"
        SELECT MENU_ID, MENU_LABEL, MENU_PATH, PARENT_ID, ICON, SORT_ORDER, IS_ACTIVE
        FROM dbo.TBL_CIP_MENU_MASTER";

    /// <summary>Sidebar menus allowed for the signed-in user: their role's grants
    /// adjusted by any per-employee overrides (TBL_CIP_EMPLOYEE_MENU), as a tree.</summary>
    [HttpGet]
    public async Task<ActionResult<List<MenuNode>>> ForMe()
    {
        var dt = await _db.QueryAsync($@"{BaseSelect}
            WHERE IS_ACTIVE = 1 AND MENU_ID IN (
                SELECT rm.MENU_ID FROM dbo.TBL_CIP_ROLE_MENU rm WHERE rm.ROLE_ID = @r
                UNION
                SELECT em.MENU_ID FROM dbo.TBL_CIP_EMPLOYEE_MENU em
                  WHERE em.EMP_CODE = @e AND em.GRANTED = 1
                  AND NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_EMPLOYEE_MENU x
                                  WHERE x.EMP_CODE = @e AND x.MENU_ID = em.MENU_ID AND x.GRANTED = 0))
            ORDER BY CASE WHEN PARENT_ID IS NULL THEN SORT_ORDER ELSE 99 END, MENU_ID",
            p =>
            {
                p.AddWithValue("@r", RoleId);
                p.AddWithValue("@e", EmpCode);
            });
        var tree = BuildTree(dt);

        // Per-employee revocations: drop menus the role grants but the override denies.
        var revDt = await _db.QueryAsync(
            "SELECT MENU_ID FROM dbo.TBL_CIP_EMPLOYEE_MENU WHERE EMP_CODE = @e AND GRANTED = 0",
            p => p.AddWithValue("@e", EmpCode));
        if (revDt.Rows.Count > 0)
        {
            var revoked = revDt.AsEnumerable().Select(r => r.Field<int>("MENU_ID")).ToHashSet();
            void Prune(List<MenuNode> nodes)
            {
                nodes.RemoveAll(n => revoked.Contains(n.MenuId));
                foreach (var n in nodes) Prune(n.Children);
            }
            Prune(tree);
        }
        return Ok(tree);
    }

    /// <summary>All menus (including inactive) — for the admin matrix screen.</summary>
    [HttpGet("all")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<List<MenuNode>>> All()
    {
        var dt = await _db.QueryAsync($@"{BaseSelect} ORDER BY CASE WHEN PARENT_ID IS NULL THEN SORT_ORDER ELSE 99 END, MENU_ID");
        return Ok(BuildTree(dt));
    }

    /// <summary>Active roles for the matrix row headers.</summary>
    [HttpGet("roles")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<object>> Roles()
    {
        var dt = await _db.QueryAsync(
            "SELECT ROLE_ID, ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER WHERE ISNULL(ROLE_ACTIVE,0) = 1 AND ROLE_ID <> @connector ORDER BY ROLE_ID",
            p => p.AddWithValue("@connector", EmployeeFilters.ConnectorRoleId));
        var roles = dt.AsEnumerable()
            .Select(r => new { roleId = r.Field<int>("ROLE_ID"), roleName = r["ROLE_NAME"].ToString() })
            .ToList();
        return Ok(roles);
    }

    /// <summary>Menu ids currently granted to a role.</summary>
    [HttpGet("roles/{roleId}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<object>> ForRole(int roleId)
    {
        if (roleId == EmployeeFilters.ConnectorRoleId)
            return NotFound(new { error = "Role not found." });
        var dt = await _db.QueryAsync(
            "SELECT MENU_ID FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r",
            p => p.AddWithValue("@r", roleId));
        return Ok(new { menuIds = dt.AsEnumerable().Select(r => r.Field<int>("MENU_ID")).ToList() });
    }

    /// <summary>Replace a role's menu grants atomically. The signed-in admin can
    /// never lock themselves out: their own role always keeps every menu.</summary>
    [HttpPut("roles/{roleId}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SaveForRole(int roleId, [FromBody] RoleMenuSave body)
    {
        if (roleId == EmployeeFilters.ConnectorRoleId)
            return NotFound(new { error = "Role not found." });
        var roleExists = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_ROLE_MASTER WHERE ROLE_ID = @r",
            p => p.AddWithValue("@r", roleId));
        if (Convert.ToInt32(roleExists) == 0) return NotFound(new { error = "Role not found." });

        var ids = (body.MenuIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        if (HierarchyService.IsAdminRole(roleId) && !ids.Any())
            return BadRequest(new { error = "Admin roles must keep at least one menu." });

        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var del = new Microsoft.Data.SqlClient.SqlCommand(
                "DELETE FROM dbo.TBL_CIP_ROLE_MENU WHERE ROLE_ID = @r", conn, tx))
            {
                del.Parameters.AddWithValue("@r", roleId);
                await del.ExecuteNonQueryAsync();
            }

            if (ids.Count > 0)
            {
                var values = string.Join(",", ids.Select((_, i) => $"(@r,@m{i})"));
                await using var ins = new Microsoft.Data.SqlClient.SqlCommand(
                    $"INSERT INTO dbo.TBL_CIP_ROLE_MENU (ROLE_ID, MENU_ID) VALUES {values}", conn, tx);
                ins.Parameters.AddWithValue("@r", roleId);
                for (var i = 0; i < ids.Count; i++)
                    ins.Parameters.AddWithValue($"@m{i}", ids[i]);
                await ins.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        return Ok(new { saved = ids.Count });
    }

    private static List<MenuNode> BuildTree(DataTable dt)
    {
        var all = dt.AsEnumerable().Select(r => new MenuNode
        {
            MenuId = r.Field<int>("MENU_ID"),
            Label = r["MENU_LABEL"].ToString() ?? "",
            Path = r["MENU_PATH"] == DBNull.Value ? null : r["MENU_PATH"].ToString(),
            Icon = r["ICON"] == DBNull.Value ? null : r["ICON"].ToString(),
            SortOrder = r.Field<int>("SORT_ORDER"),
            IsActive = r.Field<bool>("IS_ACTIVE"),
        }).ToDictionary(n => n.MenuId);

        var parentIdByMenu = dt.AsEnumerable().ToDictionary(
            r => r.Field<int>("MENU_ID"),
            r => r["PARENT_ID"] == DBNull.Value ? (int?)null : r.Field<int>("PARENT_ID"));

        var roots = new List<MenuNode>();
        foreach (var n in all.Values)
        {
            var parentId = parentIdByMenu.TryGetValue(n.MenuId, out var pid) ? pid : null;
            if (parentId is int p && all.ContainsKey(p)) all[p].Children.Add(n);
            else roots.Add(n);
        }
        foreach (var r in roots) SortTree(r);
        return roots.OrderBy(r => r.SortOrder).ThenBy(r => r.MenuId).ToList();
    }

    private static void SortTree(MenuNode node)
    {
        node.Children = node.Children.OrderBy(c => c.SortOrder).ThenBy(c => c.MenuId).ToList();
        foreach (var c in node.Children) SortTree(c);
    }
}
