namespace CIPAdmin.Backend.Services;

public interface IAccessControlService
{
    /// <summary>Whether the account belongs to an active, non-connector employee.</summary>
    Task<bool> IsEligibleEmployeeAsync(int empCode);
    /// <summary>Can this employee sign in to the website?</summary>
    Task<bool> HasWebAccessAsync(int empCode, int roleId);
}

/// <summary>
/// Gates website access at login. Rules:
///  • Only active, non-deleted, non-connector employees are eligible.
///  • Role 1 (SUPER ADMIN) has access by default.
///  • Everyone else — including ADMIN and TL — needs an explicit HAS_ACCESS = 1
///    row in TBL_CIP_WEB_ACCESS (granted on the Access Control page).
///  • Anyone can be denied with an explicit HAS_ACCESS = 0 row.
/// </summary>
public class AccessControlService : IAccessControlService
{
    public static readonly int[] DefaultAllowedRoles = { 1 };

    private readonly IDbContext _db;
    private readonly ILogger<AccessControlService> _logger;

    public AccessControlService(IDbContext db, ILogger<AccessControlService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> IsEligibleEmployeeAsync(int empCode)
    {
        var eligible = await _db.ScalarAsync(
            "SELECT CASE WHEN " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e")
                + " AND " + EmployeeFilters.NotConnector("e") + " THEN 1 ELSE 0 END FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE e.EMP_CODE = @code",
            p => p.AddWithValue("@code", empCode));

        // Missing, inactive, deleted and connector accounts all fail closed.
        return eligible != null && eligible != DBNull.Value && Convert.ToInt32(eligible) == 1;
    }

    public async Task<bool> HasWebAccessAsync(int empCode, int roleId)
    {
        // Database role/desig/date values are not trusted from the JWT or caller.
        if (!await IsEligibleEmployeeAsync(empCode)) return false;

        var roleDefault = Array.IndexOf(DefaultAllowedRoles, roleId) >= 0;
        try
        {
            var res = await _db.ScalarAsync(@"
                SELECT TOP 1
                    CASE
                        WHEN @roleDefault = 1 THEN
                            CASE WHEN ISNULL(HAS_ACCESS, 1) = 0 THEN 0 ELSE 1 END
                        ELSE
                            CASE WHEN HAS_ACCESS = 1 THEN 1 ELSE 0 END
                    END
                FROM dbo.TBL_CIP_WEB_ACCESS
                WHERE EMP_CODE = @code",
                p =>
                {
                    p.AddWithValue("@roleDefault", roleDefault ? 1 : 0);
                    p.AddWithValue("@code", empCode);
                });

            // No row → fall back to the role default (only SUPER ADMIN is in by default).
            if (res is null or DBNull) return roleDefault;
            return Convert.ToInt32(res) == 1;
        }
        catch (Exception ex)
        {
            // Never lock everyone out because of a transient DB hiccup: default-allowed
            // roles keep their default, everyone else fails closed.
            _logger.LogError(ex, "Web-access check failed for emp {Code}; using role default {Default}", empCode, roleDefault);
            return roleDefault;
        }
    }
}
