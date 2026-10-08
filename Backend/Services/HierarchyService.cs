using System.Data;
using Microsoft.Extensions.Caching.Memory;

namespace CIPAdmin.Backend.Services;

/// <summary>Data-visibility scope for the current user.
/// Admins (role 1/2) see everything; everyone else sees their reporting
/// subtree only (themselves + everyone below them via REPORTING_MANAGER_ID).</summary>
public class ScopeInfo
{
    public bool IsAdmin { get; init; }
    /// <summary>Subtree employee codes including self. Empty when IsAdmin (no filter needed).</summary>
    public int[] SubtreeCodes { get; init; } = Array.Empty<int>();
    /// <summary>Self + downline count (1 when the user has no downline).</summary>
    public int SubtreeSize { get; init; }
}

public interface IHierarchyService
{
    /// <summary>Resolve the visibility scope for a user. Admins get IsAdmin=true (unfiltered).</summary>
    Task<ScopeInfo> GetScopeAsync(int empCode, int roleId);
    /// <summary>True when the given employee is inside the user's subtree (or user is admin).</summary>
    Task<bool> IsInScopeAsync(int viewerEmpCode, int viewerRoleId, int targetEmpCode, bool allowInactiveTarget = false);
}

public class HierarchyService : IHierarchyService
{
    private readonly IDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HierarchyService> _logger;

    /// <summary>SQL 2014-safe IN-list of subtree codes (all values are server-parsed ints —
    /// no injection surface). Use inside "EMP_CODE IN (...)", or "IN (-1)" when empty.</summary>
    public static string InlineCodes(int[] codes) =>
        codes is { Length: > 0 } ? string.Join(",", codes.Select(c => c.ToString())) : "-1";

    /// <summary>Subtree cache lifetime — the org chart rarely changes intraday.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public HierarchyService(IDbContext db, IMemoryCache cache, ILogger<HierarchyService> logger)
    {
        _db = db; _cache = cache; _logger = logger;
    }

    public static bool IsAdminRole(int roleId) => roleId is 1 or 2;

    public async Task<ScopeInfo> GetScopeAsync(int empCode, int roleId)
    {
        if (IsAdminRole(roleId))
            return new ScopeInfo { IsAdmin = true, SubtreeCodes = Array.Empty<int>(), SubtreeSize = 0 };

        var eligibleViewer = await _db.ScalarAsync(
            "SELECT CASE WHEN " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + " THEN 1 ELSE 0 END FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.EMP_CODE = @code",
            p => p.AddWithValue("@code", empCode));
        if (eligibleViewer == null || eligibleViewer == DBNull.Value || Convert.ToInt32(eligibleViewer) != 1)
            return new ScopeInfo { IsAdmin = false, SubtreeCodes = Array.Empty<int>(), SubtreeSize = 0 };

        var codes = await ResolveSubtreeAsync(empCode);
        return new ScopeInfo { IsAdmin = false, SubtreeCodes = codes, SubtreeSize = codes.Length };
    }

    public async Task<bool> IsInScopeAsync(int viewerEmpCode, int viewerRoleId, int targetEmpCode, bool allowInactiveTarget = false)
    {
        // This is also the final guard for direct employee-ID routes. Do not let an
        // administrator's unbounded scope reintroduce connector/deleted records.
        var targetFilter = allowInactiveTarget
            ? EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e")
            : EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e");
        var eligibleTarget = await _db.ScalarAsync(
            "SELECT CASE WHEN " + targetFilter + " THEN 1 ELSE 0 END FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.EMP_CODE = @target",
            p => p.AddWithValue("@target", targetEmpCode));
        if (eligibleTarget == null || eligibleTarget == DBNull.Value || Convert.ToInt32(eligibleTarget) != 1)
            return false;

        if (IsAdminRole(viewerRoleId)) return true;
        if (viewerEmpCode == targetEmpCode) return true;
        var codes = await ResolveSubtreeAsync(viewerEmpCode, includeInactive: allowInactiveTarget);
        return Array.IndexOf(codes, targetEmpCode) >= 0;
    }

    /// <summary>Recursive CTE over REPORTING_MANAGER_ID. Roots use 0 / -1 / self; a user
    /// whose manager is missing simply becomes their own root, so they always see at
    /// least themselves. Cached per employee.</summary>
    private async Task<int[]> ResolveSubtreeAsync(int empCode, bool includeInactive = false)
    {
        var key = $"hier-subtree-{empCode}-{(includeInactive ? "all" : "active")}";
        if (_cache.TryGetValue(key, out int[]? cached) && cached != null)
            return cached;

        var dt = await _db.QueryAsync(@"
            WITH tree AS (
                SELECT e.EMP_CODE, e.REPORTING_MANAGER_ID, 0 AS lvl
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                WHERE e.EMP_CODE = @code
                  AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @"
                UNION ALL
                SELECT d.EMP_CODE, d.REPORTING_MANAGER_ID, t.lvl + 1
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER d
                JOIN tree t ON d.REPORTING_MANAGER_ID = t.EMP_CODE
                            AND d.EMP_CODE <> t.EMP_CODE      -- stop on self-loops
                            AND t.lvl < 20                     -- hard depth guard
                WHERE " + EmployeeFilters.NotDeleted("d") + " AND " + EmployeeFilters.NotConnector("d") + @"
            )
            SELECT DISTINCT tree.EMP_CODE FROM tree
            JOIN dbo.TBL_CIP_EMPLOYEE_MASTER eligible ON eligible.EMP_CODE = tree.EMP_CODE
            WHERE tree.EMP_CODE NOT IN (0, -1)
              AND " + EmployeeFilters.NotDeleted("eligible") + " AND " + EmployeeFilters.NotConnector("eligible")
              + (includeInactive ? "" : " AND " + EmployeeFilters.Active("eligible")) + " ORDER BY tree.EMP_CODE OPTION (MAXRECURSION 100)",
            p => p.AddWithValue("@code", empCode));

        var codes = dt.Rows.Cast<DataRow>()
            .Select(r => Convert.ToInt32(r["EMP_CODE"]))
            .Distinct()
            .ToArray();

        // The seed row is filtered by active/non-deleted/non-connector criteria. Do not
        // re-add it when absent: that would make an invalid employee a viewer scope.

        _cache.Set(key, codes, CacheTtl);
        _logger.LogDebug("Subtree of {Code} = {Count} employees (cached {Minutes}m)", empCode, codes.Length, CacheTtl.TotalMinutes);
        return codes;
    }
}
