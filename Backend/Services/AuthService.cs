using System.Data;
using CIPAdmin.Backend.Models;

namespace CIPAdmin.Backend.Services;

public interface IAuthService
{
    Task<LoginResponse> CheckCredentialAsync(LoginRequest request, string clientIp, string hostName, string? userAgent = null);
    Task<LoginResponse> VerifyOtpAsync(string username, string code, bool rememberMe, string clientIp, string? userAgent = null);
    Task<UserSessionDto?> GetSessionByEmpCodeAsync(int empCode);
    Task LogLogoutAsync(int empCode, string jti);
    /// <summary>Exchange a one-time SSO ticket (minted by the legacy CIPPortal) for a JWT session.</summary>
    Task<LoginResponse> ExchangeSsoTicketAsync(string ticket, string clientIp, string? userAgent = null);
    /// <summary>Admin: clear an employee's authenticator enrollment and/or set their bypass flag.</summary>
    Task<ResetOtpResult> ResetAuthenticatorAsync(int empCode, bool clearSecret, int? setBypass, string clientIp);
    /// <summary>Admin: authenticator/bypass state for the admin screen.</summary>
    Task<List<AuthenticatorStatusDto>> GetAuthenticatorStatusAsync(string? q, int take);
}

/// <summary>Rejected because the employee is not whitelisted for web access.</summary>
public class AccessDeniedException : Exception
{
    public AccessDeniedException(string message) : base(message) { }
}

public class AuthService : IAuthService
{
    private readonly IDbContext _db;
    private readonly ITokenService _tokens;
    private readonly ITotpService _totp;
    private readonly IAccessControlService _access;
    private readonly IConfiguration _config;
    private readonly IUserSessionService _sessions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IDbContext db, ITokenService tokens, ITotpService totp, IAccessControlService access, IConfiguration config, IUserSessionService sessions, ILogger<AuthService> logger)
    {
        _db = db; _tokens = tokens; _totp = totp; _access = access; _config = config; _sessions = sessions; _logger = logger;
    }

    /// <summary>Message shown when a valid user is not whitelisted for web access.</summary>
    public const string NoAccessMessage = "You don't have access to this website. Please contact administrator.";
    /// <summary>Invalid second-factor codes tolerated per account before the OTP step locks.</summary>
    public const int OtpMaxFailures = 5;
    private const int OtpFailureWindowMinutes = 15;

    public async Task<LoginResponse> CheckCredentialAsync(LoginRequest request, string clientIp, string hostName, string? userAgent = null)
    {
        var username = (request.Username ?? "").Trim();
        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 EMP_CODE, EMP_NAME, USERNAME, PASSWORD, DESIG_ID, ROLE_ID,
                   LOCATION_ID, EMAIL_ID, IS_GOOGLE_AUTHENTICATED, IS_BYPASS, GOOGLE_AUTH_SECRET_KEY,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE (USERNAME = @u OR EMAIL_ID = @u) AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@u", username));

        if (dt.Rows.Count == 0)
        {
            await LogLoginAsync(null, username, clientIp, hostName, "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID CREDENTIALS" };
        }

        var row = dt.Rows[0];
        var storedPassword = row["PASSWORD"].ToString() ?? "";
        var passwordOk = PasswordMatches(request.Password ?? "", storedPassword);
        if (!passwordOk)
        {
            await LogLoginAsync(Convert.ToInt32(row["EMP_CODE"]), username, clientIp, hostName, "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID CREDENTIALS" };
        }

        int empCode = Convert.ToInt32(row["EMP_CODE"]);
        int roleId = Convert.ToInt32(row["ROLE_ID"] == DBNull.Value ? 0 : row["ROLE_ID"]);
        if (!await _access.HasWebAccessAsync(empCode, roleId))
        {
            await LogLoginAsync(empCode, username, clientIp, hostName, "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        bool googleAuth = Convert.ToInt32(row["IS_GOOGLE_AUTHENTICATED"] == DBNull.Value ? 0 : row["IS_GOOGLE_AUTHENTICATED"]) == 1;
        bool bypass = Convert.ToInt32(row["IS_BYPASS"] == DBNull.Value ? 0 : row["IS_BYPASS"]) == 1;
        bool otpRequired;
        bool.TryParse(_config["CipAuth:OtpRequired"], out otpRequired);
        var session = MapSession(row);
        await LogLoginAsync(empCode, username, clientIp, hostName, "SUCCESS");

        // Preserve the configured global OTP switch and per-employee bypass behavior.
        if (!bypass && otpRequired)
        {
            if (googleAuth)
                return new LoginResponse { Valid = true, NextStep = "require_otp", Message = "Credentials valid. Enter your authenticator code." };

            var pending = row["GOOGLE_AUTH_SECRET_KEY"].ToString() ?? "";
            if (pending.Length == 0)
            {
                pending = _totp.GenerateSecret();
                await _db.ExecuteAsync(@"
                    UPDATE e SET GOOGLE_AUTH_SECRET_KEY = @secret
                    FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                    WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @"
                      AND ISNULL(IS_GOOGLE_AUTHENTICATED, 0) = 0",
                    p => { p.AddWithValue("@secret", pending); p.AddWithValue("@id", empCode); });
            }

            var uri = $"otpauth://totp/CIPAdmin:{Uri.EscapeDataString(username)}?secret={pending}&issuer=CIPAdmin&algorithm=SHA1&digits=6&period=30";
            return new LoginResponse { Valid = true, NextStep = "enroll_otp", OtpSecret = pending, OtpProvisioningUri = uri, Message = "Scan the QR code with Google Authenticator to continue." };
        }

        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, jti) = _tokens.CreateToken(empCode, session.Username, session.EmpName, roleId, roleName);
        await _sessions.StartSessionAsync(empCode, jti, clientIp, userAgent);
        session.Token = token;
        session.TokenExpiresAt = expires;
        return new LoginResponse { Valid = true, NextStep = "done", User = session };
    }

    public async Task<LoginResponse> VerifyOtpAsync(string username, string code, bool rememberMe, string clientIp, string? userAgent = null)
    {
        var uname = (username ?? "").Trim();
        if (await CountRecentOtpFailuresAsync(uname) >= OtpMaxFailures)
            return new LoginResponse { Valid = false, Message = "TOO MANY INVALID CODES. Try again in a few minutes." };

        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 EMP_CODE, EMP_NAME, USERNAME, DESIG_ID, ROLE_ID, LOCATION_ID, EMAIL_ID,
                   IS_GOOGLE_AUTHENTICATED, IS_BYPASS, GOOGLE_AUTH_SECRET_KEY,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE USERNAME = @u AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@u", uname));
        if (dt.Rows.Count == 0) return new LoginResponse { Valid = false, Message = "INVALID SESSION" };

        var row = dt.Rows[0];
        var secret = row["GOOGLE_AUTH_SECRET_KEY"].ToString() ?? "";
        if (secret.Length == 0 || !_totp.Verify(secret, code))
        {
            await LogLoginAsync(null, uname, clientIp, "verify-otp", "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID OTP CODE" };
        }

        int empCode = Convert.ToInt32(row["EMP_CODE"]);
        int currentRoleId = row["ROLE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(row["ROLE_ID"]);
        if (!await _access.HasWebAccessAsync(empCode, currentRoleId))
        {
            await LogLoginAsync(empCode, uname, clientIp, "verify-otp", "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        var session = MapSession(row);
        if (Convert.ToInt32(row["IS_GOOGLE_AUTHENTICATED"] == DBNull.Value ? 0 : row["IS_GOOGLE_AUTHENTICATED"]) != 1)
        {
            await _db.ExecuteAsync(@"
                UPDATE e SET IS_GOOGLE_AUTHENTICATED = 1
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
                p => p.AddWithValue("@id", empCode));
            session.IsGoogleAuthenticated = true;
        }

        await LogLoginAsync(empCode, uname, clientIp, "verify-otp", "SUCCESS");
        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, jti) = _tokens.CreateToken(empCode, session.Username, session.EmpName, session.RoleId, roleName);
        await _sessions.StartSessionAsync(empCode, jti, clientIp, userAgent);
        session.Token = token;
        session.TokenExpiresAt = expires;
        return new LoginResponse { Valid = true, NextStep = "done", User = session };
    }

    public async Task<UserSessionDto?> GetSessionByEmpCodeAsync(int empCode)
    {
        var dt = await _db.QueryAsync(@"
            SELECT EMP_CODE, EMP_NAME, USERNAME, DESIG_ID, ROLE_ID, LOCATION_ID, EMAIL_ID,
                   IS_GOOGLE_AUTHENTICATED, IS_BYPASS,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@id", empCode));
        return dt.Rows.Count == 0 ? null : MapSession(dt.Rows[0]);
    }

    /// <summary>One-time SSO ticket exchange. The legacy CIPPortal (MVC 4.5.2) inserts a random ticket after its own login/OTP step.</summary>
    public async Task<LoginResponse> ExchangeSsoTicketAsync(string ticket, string clientIp, string? userAgent = null)
    {
        var t = (ticket ?? "").Trim();
        if (t.Length == 0 || t.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9_-]+$"))
            return new LoginResponse { Valid = false, Message = "INVALID TICKET" };

        var consumed = await _db.ScalarAsync(@"
            UPDATE dbo.TBL_CIP_SSO_TICKET
            SET USED = 1, USED_ON = GETDATE()
            OUTPUT INSERTED.EMP_CODE
            WHERE TICKET = @t AND USED = 0 AND EXPIRES_ON > GETDATE()",
            p => p.AddWithValue("@t", t));
        if (consumed == null || consumed == DBNull.Value)
            return new LoginResponse { Valid = false, Message = "TICKET EXPIRED OR ALREADY USED" };

        var empCode = Convert.ToInt32(consumed);
        var dt = await _db.QueryAsync(@"
            SELECT EMP_CODE, EMP_NAME, USERNAME, DESIG_ID, ROLE_ID, LOCATION_ID, EMAIL_ID,
                   IS_GOOGLE_AUTHENTICATED, IS_BYPASS,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@id", empCode));
        if (dt.Rows.Count == 0) return new LoginResponse { Valid = false, Message = "EMPLOYEE NOT FOUND" };

        var row = dt.Rows[0];
        int currentRoleId = row["ROLE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(row["ROLE_ID"]);
        if (!await _access.HasWebAccessAsync(empCode, currentRoleId))
        {
            await LogLoginAsync(empCode, row["USERNAME"].ToString() ?? "", clientIp, "sso-exchange", "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        var session = MapSession(row);
        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, jti) = _tokens.CreateToken(empCode, session.Username, session.EmpName, currentRoleId, roleName);
        await _sessions.StartSessionAsync(empCode, jti, clientIp, userAgent);
        session.Token = token;
        session.TokenExpiresAt = expires;
        await LogLoginAsync(empCode, session.Username, clientIp, "sso-exchange", "SUCCESS");
        return new LoginResponse { Valid = true, NextStep = "done", User = session };
    }

    public async Task LogLogoutAsync(int empCode, string jti)
    {
        // End the session FIRST: a stale tab signing out must not remove a newer session,
        // and EndSessionAsync guards on the JTI so it cannot.
        await _sessions.EndSessionAsync(empCode, jti);
        await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_LOGIN_LOGS SET STATUS = 'LOGOUT', LOGOUT_TIME = GETDATE()
            WHERE EMP_CODE = @id AND TOKEN_JTI = @jti AND STATUS = 'SUCCESS'",
            p => { p.AddWithValue("@id", empCode); p.AddWithValue("@jti", (object?)jti ?? DBNull.Value); });
    }

    private static UserSessionDto MapSession(DataRow r) => new()
    {
        EmpCode = Convert.ToInt32(r["EMP_CODE"]),
        Username = r["USERNAME"].ToString() ?? "",
        EmpName = r["EMP_NAME"].ToString() ?? "",
        RoleId = r["ROLE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(r["ROLE_ID"]),
        RoleName = r["ROLE_NAME"].ToString() ?? "",
        DesignationId = r["DESIG_ID"] == DBNull.Value ? 0 : Convert.ToInt32(r["DESIG_ID"]),
        LocationId = r["LOCATION_ID"] == DBNull.Value ? 0 : Convert.ToInt32(r["LOCATION_ID"]),
        Email = r["EMAIL_ID"].ToString(),
        IsGoogleAuthenticated = r["IS_GOOGLE_AUTHENTICATED"] != DBNull.Value && Convert.ToInt32(r["IS_GOOGLE_AUTHENTICATED"]) == 1,
        IsBypass = r["IS_BYPASS"] != DBNull.Value && Convert.ToInt32(r["IS_BYPASS"]) == 1
    };

    private async Task LogLoginAsync(int? empCode, string username, string ip, string host, string status)
    {
        try
        {
            await _db.ExecuteAsync(@"
                INSERT INTO dbo.TBL_CIP_LOGIN_LOGS (EMP_CODE, USERNAME, LOGIN_TIME, IP_ADDRESS, HOST_NAME, STATUS)
                VALUES (@id, @u, GETDATE(), @ip, @host, @s)",
                p =>
                {
                    p.AddWithValue("@id", empCode.HasValue ? empCode.Value : DBNull.Value);
                    p.AddWithValue("@u", username);
                    p.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
                    p.AddWithValue("@host", (object?)host ?? DBNull.Value);
                    p.AddWithValue("@s", status);
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write login log");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Admin reset/flip. IS_BYPASS is only written when explicitly supplied.</summary>
    public async Task<ResetOtpResult> ResetAuthenticatorAsync(int empCode, bool clearSecret, int? setBypass, string clientIp)
    {
        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 EMP_CODE, USERNAME, IS_BYPASS, IS_GOOGLE_AUTHENTICATED, GOOGLE_AUTH_SECRET_KEY
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e"),
            p => p.AddWithValue("@id", empCode));
        if (dt.Rows.Count == 0) return new ResetOtpResult { Found = false };

        var before = dt.Rows[0];
        var username = before["USERNAME"].ToString() ?? "";
        if (clearSecret)
        {
            var n = await _db.ExecuteAsync(@"
                UPDATE e SET GOOGLE_AUTH_SECRET_KEY = NULL, IS_GOOGLE_AUTHENTICATED = 0
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @"
                  AND ISNULL(IS_GOOGLE_AUTHENTICATED, 0) = @wasAuth
                  AND ISNULL(CAST(GOOGLE_AUTH_SECRET_KEY AS VARCHAR(100)), '') = @wasSecret",
                p =>
                {
                    p.AddWithValue("@id", empCode);
                    p.AddWithValue("@wasAuth", Convert.ToInt32(before["IS_GOOGLE_AUTHENTICATED"] == DBNull.Value ? 0 : before["IS_GOOGLE_AUTHENTICATED"]));
                    p.AddWithValue("@wasSecret", before["GOOGLE_AUTH_SECRET_KEY"] == DBNull.Value ? "" : before["GOOGLE_AUTH_SECRET_KEY"].ToString());
                });
            if (n == 0)
                return new ResetOtpResult { Found = true, EmpCode = empCode, Username = username, Message = "CHANGED ELSEWHERE - reload and retry" };
        }
        if (setBypass.HasValue)
        {
            await _db.ExecuteAsync(@"
                UPDATE e SET IS_BYPASS = @b
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                WHERE e.EMP_CODE = @id AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @" AND ISNULL(IS_BYPASS, 0) = @was",
                p => { p.AddWithValue("@b", setBypass.Value == 1 ? 1 : 0); p.AddWithValue("@id", empCode); p.AddWithValue("@was", Convert.ToInt32(before["IS_BYPASS"] == DBNull.Value ? 0 : before["IS_BYPASS"])); });
        }

        var after = await _db.QueryAsync(@"
            SELECT IS_BYPASS, IS_GOOGLE_AUTHENTICATED FROM dbo.TBL_CIP_EMPLOYEE_MASTER WHERE EMP_CODE = @id",
            p => p.AddWithValue("@id", empCode));
        var a = after.Rows[0];
        await LogLoginAsync(empCode, username, clientIp, "admin-reset-otp", "SUCCESS");
        return new ResetOtpResult
        {
            Found = true, EmpCode = empCode, Username = username,
            IsBypass = a["IS_BYPASS"] != DBNull.Value && Convert.ToInt32(a["IS_BYPASS"]) == 1,
            IsEnrolled = a["IS_GOOGLE_AUTHENTICATED"] != DBNull.Value && Convert.ToInt32(a["IS_GOOGLE_AUTHENTICATED"]) == 1,
            Message = setBypass.HasValue ? "Updated" : "Authenticator reset - the user must scan a new QR code next login"
        };
    }

    public async Task<List<AuthenticatorStatusDto>> GetAuthenticatorStatusAsync(string? q, int take)
    {
        var pattern = (q ?? "").Trim();
        var dt = await _db.QueryAsync(@"
            SELECT TOP (@take) e.EMP_CODE, e.USERNAME, e.EMP_NAME, e.ROLE_ID,
                   ISNULL(e.IS_BYPASS, 0) AS IS_BYPASS,
                   ISNULL(e.IS_GOOGLE_AUTHENTICATED, 0) AS IS_GOOGLE_AUTHENTICATED,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 LOC_NAME FROM dbo.TBL_CIP_LOCATION_MASTER l WHERE l.LOC_ID = e.LOCATION_ID) AS LOC_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @"
              AND (@q = '' OR e.USERNAME LIKE @like OR e.EMP_NAME LIKE @like OR CAST(e.EMP_CODE AS VARCHAR(20)) = @q)
            ORDER BY e.EMP_NAME",
            p => { p.AddWithValue("@take", take); p.AddWithValue("@q", pattern); p.AddWithValue("@like", "%" + pattern + "%"); });

        var list = new List<AuthenticatorStatusDto>();
        foreach (DataRow r in dt.Rows)
        {
            list.Add(new AuthenticatorStatusDto
            {
                EmpCode = Convert.ToInt32(r["EMP_CODE"]), Username = r["USERNAME"].ToString() ?? "",
                EmpName = r["EMP_NAME"].ToString() ?? "", RoleId = r["ROLE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(r["ROLE_ID"]),
                RoleName = r["ROLE_NAME"] == DBNull.Value ? "" : r["ROLE_NAME"].ToString() ?? "",
                IsBypass = Convert.ToInt32(r["IS_BYPASS"]) == 1,
                IsEnrolled = Convert.ToInt32(r["IS_GOOGLE_AUTHENTICATED"]) == 1,
                LocationName = r["LOC_NAME"] == DBNull.Value ? null : r["LOC_NAME"].ToString()
            });
        }
        return list;
    }

    /// <summary>Recent invalid-OTP attempts for an account, used to throttle guessing.</summary>
    private async Task<int> CountRecentOtpFailuresAsync(string username)
    {
        try
        {
            var v = await _db.ScalarAsync(@"
                SELECT COUNT(*) FROM dbo.TBL_CIP_LOGIN_LOGS
                WHERE UPPER(USERNAME) = UPPER(@u)
                  AND HOST_NAME = 'verify-otp'
                  AND STATUS = 'FAILED'
                  AND LOGIN_TIME > DATEADD(MINUTE, -@mins, GETDATE())",
                p => { p.AddWithValue("@u", (object?)username ?? DBNull.Value); p.AddWithValue("@mins", OtpFailureWindowMinutes); });
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
        catch (Exception ex)
        {
            // A broken counter must not become an open door: fail closed.
            _logger.LogWarning(ex, "OTP failure count failed for {User}", username);
            return OtpMaxFailures;
        }
    }

    /// <summary>Accepts SHA-256 hex/base64, SHA-1 hex/base64, or the legacy plaintext values.</summary>
    private static bool PasswordMatches(string input, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (string.Equals(input, stored, StringComparison.Ordinal)) return true;
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
            if (HashEqualsStored(sha256.ComputeHash(inputBytes), stored)) return true;
        using (var sha1 = System.Security.Cryptography.SHA1.Create())
            return HashEqualsStored(sha1.ComputeHash(inputBytes), stored);
    }

    private static bool HashEqualsStored(byte[] hash, string stored)
    {
        var hex = Convert.ToHexString(hash);
        var b64 = Convert.ToBase64String(hash);
        return string.Equals(hex, stored, StringComparison.OrdinalIgnoreCase) || string.Equals(b64, stored, StringComparison.Ordinal);
    }
}
