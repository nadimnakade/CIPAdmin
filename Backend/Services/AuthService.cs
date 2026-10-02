using System.Data;
using CIPAdmin.Backend.Models;

namespace CIPAdmin.Backend.Services;

public interface IAuthService
{
    Task<LoginResponse> CheckCredentialAsync(LoginRequest request, string clientIp, string hostName);
    Task<LoginResponse> VerifyOtpAsync(string username, string code, bool rememberMe, string clientIp);
    Task<UserSessionDto?> GetSessionByEmpCodeAsync(int empCode);
    Task LogLogoutAsync(int empCode, string jti);
    /// <summary>Exchange a one-time SSO ticket (minted by the legacy CIPPortal) for a JWT session.</summary>
    Task<LoginResponse> ExchangeSsoTicketAsync(string ticket, string clientIp);
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
    private readonly ILogger<AuthService> _logger;

    public AuthService(IDbContext db, ITokenService tokens, ITotpService totp, IAccessControlService access, IConfiguration config, ILogger<AuthService> logger)
    {
        _db = db; _tokens = tokens; _totp = totp; _access = access; _config = config; _logger = logger;
    }

    /// <summary>Message shown when a valid user is not whitelisted for web access.</summary>
    public const string NoAccessMessage = "You don't have access to this website. Please contact administrator.";

    public async Task<LoginResponse> CheckCredentialAsync(LoginRequest request, string clientIp, string hostName)
    {
        var username = (request.Username ?? "").Trim();
        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 EMP_CODE, EMP_NAME, USERNAME, PASSWORD, DESIG_ID, ROLE_ID,
                   LOCATION_ID, EMAIL_ID, IS_GOOGLE_AUTHENTICATED, IS_BYPASS, GOOGLE_AUTH_SECRET_KEY,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE (USERNAME = @u OR EMAIL_ID = @u) AND ISNULL(IS_DELETED, 0) = 0",
            p => p.AddWithValue("@u", username));

        if (dt.Rows.Count == 0)
        {
            await LogLoginAsync(null, username, clientIp, hostName, "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID CREDENTIALS" };
        }

        var row = dt.Rows[0];
        var storedPassword = row["PASSWORD"].ToString() ?? "";

        // Supports hashed storage (SHA-256 / SHA-1 hex) and the legacy plaintext values.
        var passwordOk = PasswordMatches(request.Password ?? "", storedPassword);
        if (!passwordOk)
        {
            await LogLoginAsync(Convert.ToInt32(row["EMP_CODE"]), username, clientIp, hostName, "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID CREDENTIALS" };
        }

        int empCode = Convert.ToInt32(row["EMP_CODE"]);

        // Web-access gate: valid credentials are not enough — the employee must be
        // allowed by role (1/2/4) or hold an explicit grant.
        if (!await _access.HasWebAccessAsync(empCode, Convert.ToInt32(row["ROLE_ID"] == DBNull.Value ? 0 : row["ROLE_ID"])))
        {
            await LogLoginAsync(empCode, username, clientIp, hostName, "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        bool googleAuth = Convert.ToInt32(row["IS_GOOGLE_AUTHENTICATED"] == DBNull.Value ? 0 : row["IS_GOOGLE_AUTHENTICATED"]) == 1;
        bool bypass = Convert.ToInt32(row["IS_BYPASS"] == DBNull.Value ? 0 : row["IS_BYPASS"]) == 1;

        // Global OTP switch: when CipAuth:OtpRequired = false (or missing), the
        // authenticator step is skipped for everyone — password + access gate suffice.
        bool otpRequired;
        bool.TryParse(_config["CipAuth:OtpRequired"], out otpRequired);

        var session = MapSession(row);
        await LogLoginAsync(empCode, username, clientIp, hostName, "SUCCESS");

        if (!bypass && otpRequired)
        {
            // Production mode: require Google Authenticator OTP
            if (googleAuth)
                return new LoginResponse { Valid = true, NextStep = "require_otp", Message = "Credentials valid. Enter your authenticator code." };

            // Not yet enrolled: issue a secret and provisioning URI for the QR code
            var secret = _totp.GenerateSecret();
            var uri = $"otpauth://totp/CIPAdmin:{Uri.EscapeDataString(username)}?secret={secret}&issuer=CIPAdmin";
            return new LoginResponse
            { Valid = true, NextStep = "enroll_otp", OtpSecret = secret, OtpProvisioningUri = uri, Message = "Enroll your authenticator app to continue." };
        }

        // Bypass mode: straight to a JWT (admin tools / testing)
        var role = (row["ROLE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(row["ROLE_ID"]));
        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, jti) = _tokens.CreateToken(empCode, session.Username, session.EmpName, role, roleName);
        session.Token = token;
        session.TokenExpiresAt = expires;
        return new LoginResponse { Valid = true, NextStep = "done", User = session };
    }

    public async Task<LoginResponse> VerifyOtpAsync(string username, string code, bool rememberMe, string clientIp)
    {
        var dt = await _db.QueryAsync(@"
            SELECT TOP 1 EMP_CODE, EMP_NAME, USERNAME, DESIG_ID, ROLE_ID, LOCATION_ID, EMAIL_ID,
                   IS_GOOGLE_AUTHENTICATED, IS_BYPASS, GOOGLE_AUTH_SECRET_KEY,
                   (SELECT TOP 1 ROLE_NAME FROM dbo.TBL_CIP_ROLE_MASTER r WHERE r.ROLE_ID = e.ROLE_ID) AS ROLE_NAME,
                   (SELECT TOP 1 DESIG_NAME FROM dbo.TBL_CIP_DESIGNATION_MASTER d WHERE d.DESIG_ID = e.DESIG_ID) AS DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            WHERE USERNAME = @u AND ISNULL(IS_DELETED, 0) = 0",
            p => p.AddWithValue("@u", (username ?? "").Trim()));

        if (dt.Rows.Count == 0)
            return new LoginResponse { Valid = false, Message = "INVALID SESSION" };

        var row = dt.Rows[0];
        var secret = row["GOOGLE_AUTH_SECRET_KEY"].ToString() ?? "";

        if (secret.Length == 0 || !_totp.Verify(secret, code))
        {
            await LogLoginAsync(null, username, clientIp, "verify-otp", "FAILED");
            return new LoginResponse { Valid = false, Message = "INVALID OTP CODE" };
        }

        int empCode = Convert.ToInt32(row["EMP_CODE"]);

        // Same gate on the OTP step (role could have been revoked between steps).
        if (!await _access.HasWebAccessAsync(empCode, Convert.ToInt32(row["ROLE_ID"] == DBNull.Value ? 0 : row["ROLE_ID"])))
        {
            await LogLoginAsync(empCode, username, clientIp, "verify-otp", "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        var session = MapSession(row);

        // First successful verify => mark as enrolled
        if (Convert.ToInt32(row["IS_GOOGLE_AUTHENTICATED"] == DBNull.Value ? 0 : row["IS_GOOGLE_AUTHENTICATED"]) != 1)
        {
            await _db.ExecuteAsync(@"UPDATE dbo.TBL_CIP_EMPLOYEE_MASTER SET IS_GOOGLE_AUTHENTICATED = 1 WHERE EMP_CODE = @id",
                p => p.AddWithValue("@id", empCode));
            session.IsGoogleAuthenticated = true;
        }

        await LogLoginAsync(empCode, username, clientIp, "verify-otp", "SUCCESS");

        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, _) = _tokens.CreateToken(empCode, session.Username, session.EmpName, session.RoleId, roleName);
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
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e WHERE EMP_CODE = @id", p => p.AddWithValue("@id", empCode));

        return dt.Rows.Count == 0 ? null : MapSession(dt.Rows[0]);
    }

    /// <summary>One-time SSO ticket exchange. The legacy CIPPortal (MVC 4.5.2) inserts a
    /// random ticket row into TBL_CIP_SSO_TICKET after its own login (and its Google
    /// Authenticator step) completes, then redirects here. The ticket is single-use and
    /// short-lived; this method atomically consumes it and issues this app's own JWT.
    /// The web-access gate applies exactly as in an ordinary login.</summary>
    public async Task<LoginResponse> ExchangeSsoTicketAsync(string ticket, string clientIp)
    {
        var t = (ticket ?? "").Trim();
        if (t.Length == 0 || t.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9_-]+$"))
            return new LoginResponse { Valid = false, Message = "INVALID TICKET" };

        // Atomically consume: only the first claim wins (UPDATE guard on USED = 0).
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
            WHERE EMP_CODE = @id AND ISNULL(IS_DELETED, 0) = 0",
            p => p.AddWithValue("@id", empCode));
        if (dt.Rows.Count == 0)
            return new LoginResponse { Valid = false, Message = "EMPLOYEE NOT FOUND" };

        var row = dt.Rows[0];
        var roleId = Convert.ToInt32(row["ROLE_ID"] == DBNull.Value ? 0 : row["ROLE_ID"]);

        // Same web-access gate as a direct login.
        if (!await _access.HasWebAccessAsync(empCode, roleId))
        {
            await LogLoginAsync(empCode, row["USERNAME"].ToString() ?? "", clientIp, "sso-exchange", "NO_ACCESS");
            return new LoginResponse { Valid = false, Message = NoAccessMessage };
        }

        var session = MapSession(row);
        var roleName = row["ROLE_NAME"].ToString() ?? "";
        var (token, expires, jti) = _tokens.CreateToken(empCode, session.Username, session.EmpName, session.RoleId, roleName);
        session.Token = token;
        session.TokenExpiresAt = expires;
        await LogLoginAsync(empCode, session.Username, clientIp, "sso-exchange", "SUCCESS");
        return new LoginResponse { Valid = true, NextStep = "done", User = session };
    }

    public async Task LogLogoutAsync(int empCode, string jti)
    {
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

    /// <summary>Accepts SHA-256 hex/base64, SHA-1 hex/base64, or the legacy plaintext values.</summary>
    private static bool PasswordMatches(string input, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (string.Equals(input, stored, StringComparison.Ordinal)) return true; // legacy plaintext

        var inputBytes = System.Text.Encoding.UTF8.GetBytes(input);

        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            if (HashEqualsStored(sha256.ComputeHash(inputBytes), stored)) return true;
        }
        using (var sha1 = System.Security.Cryptography.SHA1.Create())
        {
            if (HashEqualsStored(sha1.ComputeHash(inputBytes), stored)) return true;
        }
        return false;
    }

    private static bool HashEqualsStored(byte[] hash, string stored)
    {
        var hex = Convert.ToHexString(hash);
        var b64 = Convert.ToBase64String(hash);
        return string.Equals(hex, stored, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(b64, stored, StringComparison.Ordinal);
    }
}
