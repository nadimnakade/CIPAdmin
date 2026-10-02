using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Models;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

[ApiController]
[Route("api/cibil")]
[Authorize]
public class CibilController : ControllerBase
{
    private readonly ICibilService _cibil;
    private readonly IHierarchyService _hierarchy;
    private readonly ILogger<CibilController> _logger;

    public CibilController(ICibilService cibil, IHierarchyService hierarchy, ILogger<CibilController> logger)
    {
        _cibil = cibil; _hierarchy = hierarchy; _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private string EmpName => User.FindFirst("empName")?.Value ?? "";
    private int RoleId => int.TryParse(User.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : 0;
    /// <summary>Role 1 = SUPER ADMIN, 2 = ADMIN (see TBL_CIP_ROLE_MASTER).</summary>
    private bool IsAdmin => RoleId is 1 or 2;

    /// <summary>Check whether a report for a PAN already exists (optionally only this month's).</summary>
    [HttpPost("check")]
    public async Task<ActionResult<CibilCheckResponse>> Check([FromBody] CibilCheckRequest request)
    {
        var report = await _cibil.FindByPanAsync(request.Pan, request.ReportType, currentMonthOnly: true);
        return Ok(new CibilCheckResponse
        {
            Exists = report != null,
            Report = report,
            Message = report == null
                ? null
                : $"A CIBIL report for PAN {request.Pan.ToUpperInvariant()} already exists this month ({report.FileName})."
        });
    }

    /// <summary>
    /// Generate (pull) a CIBIL report.
    /// Rules: one report per PAN per month for everyone (existing PDF is returned);
    /// regular users max Cibil:MonthlyPullLimit (default 50) pulls per month (403 beyond);
    /// admins may pass regenerate=true to replace this month's report with a fresh pull.
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<CibilGenerateResponse>> Generate([FromBody] CibilRequest request, [FromQuery] bool regenerate = false, CancellationToken ct = default)
    {
        try
        {
            var res = await _cibil.GenerateAsync(request, EmpCode, EmpName, IsAdmin, regenerate && IsAdmin, ct);
            return Ok(res);
        }
        catch (QuotaExceededException ex)
        {
            return StatusCode(403, new
            {
                error = ex.Message,
                quota = ex.Quota,
                existingReport = ex.ExistingReport,
                hasPendingRequest = ex.HasPendingRequest,
                code = "QUOTA_EXCEEDED"
            });
        }
        catch (RegenerateBlockedException ex)
        {
            return StatusCode(409, new { error = ex.Message, report = ex.Report, code = "FILE_MISSING" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CIBIL generate failed for PAN {Pan}", request.PanCard);
            return StatusCode(500, new { error = "Report generation failed: " + ex.Message });
        }
    }

    /// <summary>The current user's monthly pull quota.</summary>
    [HttpGet("quota")]
    public async Task<ActionResult<QuotaInfo>> Quota()
    {
        return Ok(await _cibil.GetQuotaAsync(EmpCode, EmpName));
    }

    /// <summary>Submit a request to raise the current user's monthly pull limit.</summary>
    [HttpPost("limit-request")]
    public async Task<ActionResult<LimitRequestInfo>> SubmitLimitRequest([FromBody] LimitRequestInput input)
    {
        try
        {
            return Ok(await _cibil.SubmitLimitRequestAsync(EmpCode, EmpName, input));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>The current user's limit requests (status of their own submissions).</summary>
    [HttpGet("my-limit-requests")]
    public async Task<ActionResult<List<LimitRequestInfo>>> MyLimitRequests()
    {
        return Ok(await _cibil.GetMyLimitRequestsAsync(EmpCode));
    }

    /// <summary>All limit requests visible to the caller — admins see everyone's,
    /// TLs see their own subtree's (own requests included).</summary>
    [HttpGet("limit-requests")]
    public async Task<ActionResult<List<LimitRequestInfo>>> AllLimitRequests([FromQuery] string? status = null)
    {
        if (IsAdmin) return Ok(await _cibil.GetAllLimitRequestsAsync(status));
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        return Ok(await _cibil.GetSubtreeLimitRequestsAsync(scope.SubtreeCodes, status));
    }

    /// <summary>Approve or reject a limit request. Admins decide any; TLs only their subtree's.
    /// On approve, approvedLimit becomes the user's effective cap for the month (defaults to the requested limit).</summary>
    [HttpPost("limit-requests/{requestId:int}/decide")]
    public async Task<ActionResult<LimitRequestInfo>> DecideLimitRequest(int requestId, [FromBody] LimitDecisionInput body)
    {
        try
        {
            if (!IsAdmin)
            {
                // Non-admin: the request must belong to someone in the caller's subtree.
                var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
                if (!await _cibil.IsRequestInScopeAsync(requestId, scope.SubtreeCodes))
                    return StatusCode(403, new { error = "This limit request is outside your team's scope." });
            }
            // On approve, fall back to the requested limit when no override is supplied.
            int? approvedLimit = body.ApprovedLimit;
            return Ok(await _cibil.DecideLimitRequestAsync(requestId, body.Approve, EmpCode, EmpName, approvedLimit));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Stream a saved report PDF inline (for the viewer iframe).
    /// Non-admins may only open reports pulled by themselves or their downline.
    /// Legacy reports (SOURCE='LEGACY', registered from the standalone CIBIL checker)
    /// have no owner and are company-wide — anyone can open them, since the checker
    /// deliberately serves them to whoever requests that PAN.</summary>
    [HttpGet("download/{reportId:int}")]
    public async Task<IActionResult> Download(int reportId, [FromQuery] bool download = false)
    {
        try
        {
            var file = await _cibil.GetFileAsync(reportId);
            if (file == null) return NotFound(new { error = "Report not found." });
            var (bytes, report) = file.Value;

            var isLegacy = string.Equals(report.Source, "LEGACY", StringComparison.OrdinalIgnoreCase);
            if (!IsAdmin && !isLegacy)
            {
                var pulledBy = await _cibil.GetPulledByAsync(reportId);
                if (pulledBy == null || !await _hierarchy.IsInScopeAsync(EmpCode, RoleId, pulledBy.Value))
                    return StatusCode(403, new { error = "This report is outside your team's scope." });
            }

            return File(bytes, "application/pdf",
                download ? report.FileName : null, // null => render inline
                enableRangeProcessing: true);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Admin, one-time: register existing PDFs already on disk so their PANs
    /// are served from the folder instead of triggering fresh vendor pulls.</summary>
    [HttpPost("import-legacy")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<object>> ImportLegacy()
    {
        try
        {
            return Ok(await _cibil.ImportLegacyFilesAsync());
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy CIBIL import failed");
            return StatusCode(500, new { error = "Import failed: " + ex.Message });
        }
    }

    // ------------------------------------------------- employee limits (admin)

    /// <summary>Admin: search employees and their per-employee monthly pull limits.</summary>
    [HttpGet("employee-limits")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<object>> EmployeeLimits([FromQuery] string? search = null)
    {
        var filter = string.IsNullOrWhiteSpace(search) ? "" :
            "AND (e.EMP_NAME LIKE @q OR CONVERT(varchar(20), e.EMP_CODE) LIKE @q) ";
        var dt = await _cibil.QueryEmployeeLimitsAsync(search, filter);
        return Ok(new { rows = dt });
    }

    /// <summary>Admin: set (or raise/lower) a specific employee's monthly pull limit.
    /// Takes effect within 60s (quota caching).</summary>
    [HttpPut("employee-limits/{empCode:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SetEmployeeLimit(int empCode, [FromBody] EmployeeLimitInput body)
    {
        if (body.MonthlyLimit is < 1 or > 100000)
            return BadRequest(new { error = "Limit must be between 1 and 100000." });
        try
        {
            return Ok(await _cibil.SetEmployeeLimitAsync(empCode, body.MonthlyLimit!.Value, EmpCode));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Admin: remove a per-employee limit — the employee falls back to the default.</summary>
    [HttpDelete("employee-limits/{empCode:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> ClearEmployeeLimit(int empCode)
    {
        await _cibil.ClearEmployeeLimitAsync(empCode);
        return Ok(new { cleared = empCode });
    }

    /// <summary>List saved reports — hierarchy-scoped: admins see all, everyone else
    /// sees reports pulled by themselves and their downline.</summary>
    [HttpGet("reports")]
    public async Task<ActionResult<List<CibilReportInfo>>> Reports([FromQuery] string? reportType = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] string? search = null)
    {
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        var rows = await _cibil.ListAsync(reportType, from, to, search, scope.IsAdmin ? null : scope.SubtreeCodes);
        return Ok(new
        {
            total = rows.Count,
            rows,
            scope = new { isAdmin = scope.IsAdmin, subtreeSize = scope.SubtreeSize }
        });
    }
}

public class LimitDecisionInput
{
    public bool Approve { get; set; }
    public int? ApprovedLimit { get; set; }
}

public class EmployeeLimitInput
{
    public int? MonthlyLimit { get; set; }
}
