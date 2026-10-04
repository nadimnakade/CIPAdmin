using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Models;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly ITotpService _totp;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ITotpService totp, ILogger<AuthController> logger)
    {
        _auth = auth; _totp = totp; _logger = logger;
    }

    /// <summary>Step 1: validate username/password (improved port of Chk_Credential).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var host = Request.Headers["Host"].ToString();
            var res = await _auth.CheckCredentialAsync(request, ip, host, Request.Headers["User-Agent"].ToString());
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {User}", request.Username);
            return StatusCode(500, new LoginResponse { Valid = false, Message = "Login error: " + ex.Message });
        }
    }

    /// <summary>Step 2a: verify a Google Authenticator code for an enrolled user.</summary>
    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> VerifyOtp([FromBody] OtpRequest request, [FromQuery] bool rememberMe = false)
    {        var res = await _auth.VerifyOtpAsync(request.Username, request.Code, rememberMe, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", Request.Headers["User-Agent"].ToString());
        if (!res.Valid) return Unauthorized(res);
        return Ok(res);
    }

    /// <summary>Step 2b: enroll a new authenticator secret; the URI encodes into a QR code on the client.</summary>
    [HttpPost("enroll-otp")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> EnrollOtp([FromBody] OtpRequest request)
    {
        // EnrollOtp returns the provisioning URI; VerifyOtp confirms the first code and marks enrollment.
        var res = await _auth.VerifyOtpAsync(request.Username, request.Code, false, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", Request.Headers["User-Agent"].ToString());
        if (!res.Valid) return Unauthorized(res);
        return Ok(res);
    }

    /// <summary>SSO bridge for the legacy CIPPortal (MVC 4.5.2): exchange a one-time ticket
    /// from TBL_CIP_SSO_TICKET for this app's JWT. The portal mints the ticket after its own
    /// login + Google Authenticator step and redirects to  /?sso={ticket} .</summary>
    [HttpGet("sso")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Sso([FromQuery] string ticket)
    {
        var res = await _auth.ExchangeSsoTicketAsync(ticket, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", Request.Headers["User-Agent"].ToString());
        if (!res.Valid) return Unauthorized(res);
        return Ok(res);
    }

    /// <summary>Current session info from the JWT.</summary>
    [HttpGet("session")]
    [Authorize]
    public async Task<ActionResult<UserSessionDto>> Session()
    {
        var empCode = int.Parse(User.FindFirst("empCode")!.Value);
        var session = await _auth.GetSessionByEmpCodeAsync(empCode);
        if (session == null) return Unauthorized();
        return Ok(session);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var empCode = int.Parse(User.FindFirst("empCode")!.Value);
        var jti = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        await _auth.LogLogoutAsync(empCode, jti ?? "");
        return Ok(new { ok = true });
    }
}
