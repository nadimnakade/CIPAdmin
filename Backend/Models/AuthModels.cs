namespace CIPAdmin.Backend.Models;

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Remember me extends the JWT lifetime.</summary>
    public bool RememberMe { get; set; }
}

public class OtpRequest
{
    public string Username { get; set; } = "" ;
    public string Code { get; set; } = "";
}

public class UserSessionDto
{
    public int EmpCode { get; set; }
    public string Username { get; set; } = "";
    public string EmpName { get; set; } = "";
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public int DesignationId { get; set; }
    public string Designation { get; set; } = "";
    public int LocationId { get; set; }
    public string? Email { get; set; }
    public bool IsGoogleAuthenticated { get; set; }
    public bool IsBypass { get; set; }
    public string? Token { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
}

public class LoginResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = "";
    /// <summary>require_otp = Google Authenticator step needed before a JWT is issued.</summary>
    public string NextStep { get; set; } = "";   // "done" | "require_otp" | "enroll_otp"
    public UserSessionDto? User { get; set; }
    /// <summary>Only populated when enrolling a new authenticator.</summary>
    public string? OtpProvisioningUri { get; set; }
    public string? OtpSecret { get; set; }
}

public class ResetOtpRequest
{
    public int EmpCode { get; set; }
    /// <summary>Clear the stored secret and mark the user un-enrolled (lost phone).</summary>
    public bool ClearSecret { get; set; } = true;
    /// <summary>Optional: also set IS_BYPASS. 1 = skip the authenticator, 0 = require it.</summary>
    public int? SetBypass { get; set; }
}

/// <summary>One row of the admin authenticator-status screen.</summary>
public class AuthenticatorStatusDto
{
    public int EmpCode { get; set; }
    public string Username { get; set; } = "";
    public string EmpName { get; set; } = "";
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public bool IsBypass { get; set; }
    public bool IsEnrolled { get; set; }
    public string? LocationName { get; set; }
}

/// <summary>Result of an admin reset/flip.</summary>
public class ResetOtpResult
{
    public bool Found { get; set; }
    public int EmpCode { get; set; }
    public string Username { get; set; } = "";
    public bool IsBypass { get; set; }
    public bool IsEnrolled { get; set; }
    public string Message { get; set; } = "";
}
