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
