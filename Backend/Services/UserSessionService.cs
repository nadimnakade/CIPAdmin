using System.Data;
using CIPAdmin.Backend.Models;

namespace CIPAdmin.Backend.Services;

/// <summary>Why a token was refused. Distinct values let the client explain itself.</summary>
public enum SessionCheck
{
    /// <summary>Token matches the employee's one active session and is within the idle window.</summary>
    Ok,
    /// <summary>This employee has no active session row (logged out, idle-expired, or app restarted).</summary>
    NoSession,
    /// <summary>The employee signed in again elsewhere; this token was superseded.</summary>
    Superseded,
    /// <summary>No activity within the idle window.</summary>
    IdleTimeout
}

public interface IUserSessionService
{
    /// <summary>Register a fresh login, replacing (and thereby invalidating) any previous session.</summary>
    Task StartSessionAsync(int empCode, string jti, string? ip, string? userAgent);

    /// <summary>Validate the presented token and refresh LAST_SEEN. Called on every authenticated request.</summary>
    Task<SessionCheck> ValidateAndTouchAsync(int empCode, string jti);

    /// <summary>End the session. No-op when the token is not the active one (a stale tab signing out
    /// must not be able to kill the session that replaced it).</summary>
    Task EndSessionAsync(int empCode, string jti);
}

/// <summary>
/// Single-active-session registry backed by dbo.TBL_CIP_ACTIVE_SESSION.
/// See Backend/sql/USER-SINGLE-SESSION.sql for the schema and the rationale.
/// </summary>
public class UserSessionService : IUserSessionService
{
    private readonly IDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<UserSessionService> _logger;

    public UserSessionService(IDbContext db, IConfiguration config, ILogger<UserSessionService> logger)
    {
        _db = db; _config = config; _logger = logger;
    }

    /// <summary>Minutes of inactivity before a session is dropped. Jwt:IdleTimeoutMinutes, default 15.</summary>
    public int IdleTimeoutMinutes
    {
        get
        {
            var v = _config.GetValue<int?>("Jwt:IdleTimeoutMinutes");
            return v is > 0 and <= 1440 ? v.Value : 15;
        }
    }

    public async Task StartSessionAsync(int empCode, string jti, string? ip, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(jti)) return;

        if (!await TableExistsAsync())
        {
            _logger.LogWarning(
                "dbo.TBL_CIP_ACTIVE_SESSION is missing — single-login and idle logout are disabled. " +
                "Run Backend/sql/USER-SINGLE-SESSION.sql.");
            return;
        }

        // A plain upsert. The previous row is replaced rather than deleted-then-inserted so there
        // is no window in which the employee has no row and an old token would look unclaimed.
        await _db.ExecuteAsync(@"
            MERGE dbo.TBL_CIP_ACTIVE_SESSION AS t
            USING (SELECT @e AS EMP_CODE) AS s ON t.EMP_CODE = s.EMP_CODE
            WHEN MATCHED THEN UPDATE SET
                JTI = @j, LOGIN_TIME = GETDATE(), LAST_SEEN = GETDATE(),
                IP_ADDRESS = @ip, USER_AGENT = @ua
            WHEN NOT MATCHED THEN INSERT (EMP_CODE, JTI, LOGIN_TIME, LAST_SEEN, IP_ADDRESS, USER_AGENT)
                VALUES (@e, @j, GETDATE(), GETDATE(), @ip, @ua);",
            p =>
            {
                p.AddWithValue("@e", empCode);
                p.AddWithValue("@j", jti);
                p.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
                p.AddWithValue("@ua", Trunc(userAgent, 400));
            }).ConfigureAwait(false);

        _logger.LogInformation("Session started for employee {EmpCode}.", empCode);
    }

    public async Task<SessionCheck> ValidateAndTouchAsync(int empCode, string jti)
    {
        if (string.IsNullOrWhiteSpace(jti)) return SessionCheck.NoSession;
        if (!await TableExistsAsync()) return SessionCheck.Ok; // fail open pre-deploy

        // Read the row without locking: a concurrent second login for the same employee is
        // exactly the race this feature is about, and the check is repeated on the next
        // request anyway. Touch LAST_SEEN only when it actually moved, so a burst of requests
        // from one screen does not turn into a burst of UPDATEs.
        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 JTI, LAST_SEEN,
                   DATEDIFF(SECOND, LAST_SEEN, GETDATE()) AS IDLE_SECONDS
            FROM dbo.TBL_CIP_ACTIVE_SESSION
            WHERE EMP_CODE = @e", p => p.AddWithValue("@e", empCode)).ConfigureAwait(false);

        if (dt.Rows.Count == 0) return SessionCheck.NoSession;
        var row = dt.Rows[0];

        if (!string.Equals(row["JTI"].ToString(), jti, StringComparison.Ordinal))
            return SessionCheck.Superseded;

        var idleSeconds = Convert.ToInt32(row["IDLE_SECONDS"] == DBNull.Value ? 0 : row["IDLE_SECONDS"]);
        if (idleSeconds > IdleTimeoutMinutes * 60)
        {
            // Drop the row so the employee is not left with a session that only looks dead.
            await EndSessionAsync(empCode, jti).ConfigureAwait(false);
            _logger.LogInformation("Session for employee {EmpCode} expired after {Idle}s idle.", empCode, idleSeconds);
            return SessionCheck.IdleTimeout;
        }

        if (idleSeconds >= 15)
        {
            await _db.ExecuteAsync(
                "UPDATE dbo.TBL_CIP_ACTIVE_SESSION SET LAST_SEEN = GETDATE() WHERE EMP_CODE = @e AND JTI = @j",
                p => { p.AddWithValue("@e", empCode); p.AddWithValue("@j", jti); }).ConfigureAwait(false);
        }

        return SessionCheck.Ok;
    }

    public async Task EndSessionAsync(int empCode, string jti)
    {
        if (!await TableExistsAsync()) return;

        // The JTI guard matters: an old tab hitting "sign out" must not terminate the
        // session that superseded it.
        await _db.ExecuteAsync(
            "DELETE FROM dbo.TBL_CIP_ACTIVE_SESSION WHERE EMP_CODE = @e AND JTI = @j",
            p => { p.AddWithValue("@e", empCode); p.AddWithValue("@j", jti ?? ""); }).ConfigureAwait(false);
    }

    /// <summary>Small cache so the existence probe does not hit the catalog on every request.</summary>
    private bool? _tableExists;

    private async Task<bool> TableExistsAsync()
    {
        if (_tableExists.HasValue) return _tableExists.Value;
        try
        {
            var v = await _db.ScalarAsync(
                "SELECT COUNT(1) FROM sys.tables WHERE name = 'TBL_CIP_ACTIVE_SESSION' AND schema_id = SCHEMA_ID('dbo')")
                .ConfigureAwait(false);
            _tableExists = Convert.ToInt32(v) > 0;
        }
        catch
        {
            _tableExists = false;
        }
        return _tableExists.Value;
    }

    private static string? Trunc(string? s, int max) =>
        s == null ? null : (s.Length <= max ? s : s[..max]);
}
