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

    /// <summary>Check whether a report for a PAN already exists (optionally only this month's).
    /// Also reports the relevant latest attempt when no report exists: user-owned
    /// successes preserve the popup behavior; a FAILED or PENDING attempt freezes the
    /// PAN globally until next month, with admin-only explicit retry. Failure reasons
    /// are redacted for non-admins. A same-month successful report is still served from
    /// cache to another user without the prior-pull popup.</summary>
    [HttpPost("check")]
    public async Task<ActionResult<CibilCheckResponse>> Check([FromBody] CibilCheckRequest request)
    {
        var report = await _cibil.FindByPanAsync(request.Pan, request.ReportType, currentMonthOnly: true);
        var ownAttempt = await _cibil.FindLastAttemptAsync(request.Pan, request.ReportType, EmpCode);
        var attempt = report == null ? ownAttempt : null;
        if (attempt != null && !IsAdmin) attempt.ErrorMessage = null; // failure reason is admin-only
        return Ok(new CibilCheckResponse
        {
            Exists = report != null,
            Report = report,
            LastAttempt = attempt,
            Blocked = attempt != null,
            // The caller has their OWN successful pull for this PAN this month (direct
            // or cache-served) — only then may the UI offer the file back. Someone
            // else's successful pull never triggers the popup. Reports pulled before
            // the ledger existed have no OK row, so their owner (PULLED_BY) counts too.
            PulledByMe = ownAttempt?.Status == "OK"
                || (report != null && await _cibil.GetPulledByAsync(report.ReportId) == EmpCode),
            CanRePull = IsAdmin,
            Message = report != null
                ? $"A CIBIL report for PAN {request.Pan.ToUpperInvariant()} already exists this month ({report.FileName})."
                : attempt == null
                    ? null
                    : CibilService.DuplicateBlockMessage(attempt, IsAdmin)
        });
    }

    /// <summary>
    /// Generate (pull) a CIBIL report.
    /// Rules: one vendor call per PAN per month; existing PDFs return from cache, any
    /// FAILED/PENDING attempt blocks globally with 409 DUPLICATE_PULL unless an admin
    /// explicitly re-pulls; completed OK and FAILED calls each count against the
    /// regular user's monthly Cibil:MonthlyPullLimit (default 50);
    /// re-pulls (regenerate=true) are admin-only — non-admins get 403 RE-PULL_ADMIN_ONLY.
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<CibilGenerateResponse>> Generate([FromBody] CibilRequest request, [FromQuery] bool regenerate = false, CancellationToken ct = default)
    {
        try
        {
            // regenerate is passed through as-is: the SERVICE refuses it for non-admins,
            // so the rule cannot be bypassed by a future caller of the service.
            var res = await _cibil.GenerateAsync(request, EmpCode, EmpName, IsAdmin, regenerate, ct);
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
        catch (RePullForbiddenException ex)
        {
            return StatusCode(403, new { error = ex.Message, code = "RE-PULL_ADMIN_ONLY" });
        }
        catch (DuplicatePullBlockedException ex)
        {
            // The vendor's error text on the blocking attempt is admin-only.
            if (!IsAdmin) ex.Attempt.ErrorMessage = null;
            return StatusCode(409, new
            {
                error = ex.Message,
                code = "DUPLICATE_PULL",
                lastAttempt = ex.Attempt,
                canRePull = IsAdmin
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
        var q = await _cibil.GetQuotaAsync(EmpCode, EmpName);
        // Admins are exempt from the monthly cap, so never tell the UI to lock them out.
        if (IsAdmin) q.Blocked = false;
        return Ok(q);
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
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_CIBIL_LIMIT_REQ_PENDING rejected a second Pending request for the same
            // employee and month. This is a normal outcome of a double submit racing the
            // HasPendingLimitRequestAsync read, not a server fault - report it as the same
            // 400 the pre-check raises, so the user sees one consistent message.
            return BadRequest(new { error = "You already have a pending request for this month. Wait for the administrator's decision." });
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
    ///
    /// Authorisation here must AGREE with the rule that serves the report, otherwise a
    /// user is handed a report by Generate and then refused the file — after the serve
    /// already counted a pull against their monthly quota. Generate's RULE 3 serves the
    /// saved PDF for ANY PAN already pulled this month by ANYONE (SOURCE=CACHE) so a
    /// colleague's pull does not cost another vendor credit; the download of that very
    /// report used to be refused, because the hierarchy rule only ever walks the
    /// viewer's OWN downline — it can never contain the administrator (who sits in a
    /// separate tree) nor even the viewer's own manager. A client asked for by a TL and
    /// pulled by the admin was therefore unopenable.
    ///
    /// A report is openable when it is company-wide by one of these rules:
    ///   * the caller is an admin;
    ///   * SOURCE='LEGACY' — pre-migration PDFs have no owner;
    ///   * pulled THIS month — RULE 3 would serve it to any user who asks for the PAN;
    ///   * the ledger shows this report was already served to this caller.
    /// Anything else (an older report, reachable only through the Reports grid) keeps
    /// the hierarchy rule: yourself, or your downline.</summary>
    [HttpGet("download/{reportId:int}")]
    public async Task<IActionResult> Download(int reportId, [FromQuery] bool download = false)
    {
        try
        {
            var file = await _cibil.GetFileAsync(reportId);
            if (file == null) return NotFound(new { error = "Report not found." });
            var (bytes, report) = file.Value;

            var isLegacy = string.Equals(report.Source, "LEGACY", StringComparison.OrdinalIgnoreCase);
            var isCurrentMonth = report.PulledDate.ToString("yyyy-MM") == DateTime.Now.ToString("yyyy-MM");
            var mayOpen = IsAdmin
                || isLegacy
                || isCurrentMonth
                || await _cibil.WasReportServedToAsync(reportId, EmpCode);

            if (!mayOpen)
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

    /// <summary>Pull attempts that produced no report — FAILED and in-flight/stuck
    /// PENDING. A failed vendor call never inserts into TBL_CIP_CIBIL_REPORTS, so these
    /// rows are invisible to GET /reports: the pull simply "never happened" from every
    /// screen's point of view while the PAN stays frozen for the requester.
    ///
    /// Admins see all company failures; other users see only their own + downline
    /// (viewer included). The vendor's failure text is admin-only, matching Check and
    /// Generate. SQL scopes its empty-list case fail-closed.</summary>
    [HttpGet("failed-pulls")]
    public async Task<ActionResult<object>> FailedPulls([FromQuery] string? search = null, [FromQuery] int take = 200)
    {
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        var rows = await _cibil.ListFailedPullsAsync(
            scope.IsAdmin ? null : scope.SubtreeCodes, EmpCode, search, take);
        if (!IsAdmin)
            foreach (var r in rows) r.ErrorMessage = null;
        return Ok(new
        {
            total = rows.Count,
            rows,
            scope = new { isAdmin = scope.IsAdmin, subtreeSize = scope.SubtreeSize }
        });
    }

    /// <summary>Excel export of the reports grid. Applies the same filters and the same</summary>
    /// hierarchy scope as GET /api/cibil/reports, and exports the FULL filtered set
    /// rather than the page the user is looking at.
    ///
    /// CIBIL Pull Date is written as a real date cell and CIBIL Pull Time as a real time
    /// cell, so Excel's own Data &gt; Sort orders rows chronologically. Writing them as
    /// text would sort alphabetically and put 01-Apr-2026 after 31-Dec-2025.</summary>
    [HttpGet("reports/export")]
    public async Task<IActionResult> ExportReports([FromQuery] string? reportType = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] string? search = null)
    {
        const int maxRows = 50_000;
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        var rows = await _cibil.ListAsync(reportType, from, to, search, scope.IsAdmin ? null : scope.SubtreeCodes);

        if (rows.Count > maxRows)
            return StatusCode(413, new
            {
                error = $"This export would contain {rows.Count:N0} rows (limit {maxRows:N0}). Narrow the date range or filters and try again."
            });

        var table = new DataTable();
        table.Columns.Add("Report ID", typeof(int));
        table.Columns.Add("Applicant Name", typeof(string));
        table.Columns.Add("PAN", typeof(string));
        table.Columns.Add("Mobile", typeof(string));
        table.Columns.Add("Report Type", typeof(string));
        table.Columns.Add("Source", typeof(string));
        table.Columns.Add("Location", typeof(string));
        table.Columns.Add("Center Manager", typeof(string));
        table.Columns.Add("CIBIL Pull Date", typeof(DateTime));   // real date cell => sorts chronologically
        table.Columns.Add("CIBIL Pull Time", typeof(TimeSpan));   // real time cell
        table.Columns.Add("File Name", typeof(string));
        table.Columns.Add("File Size (bytes)", typeof(long));

        foreach (var r in rows)
        {
            table.Rows.Add(
                r.ReportId,
                (object?)r.ApplicantName ?? "",
                r.PanNo,
                (object?)r.Mobile ?? "",
                r.ReportType,
                r.Source,
                (object?)r.LocationName ?? "",
                (object?)r.CenterManager ?? "",
                r.PulledDate.Date,
                r.PulledDate.TimeOfDay,
                r.FileName,
                r.FileSize ?? 0L);
        }

        var bytes = ExcelExporter.BuildXlsx(table, "CIBIL Reports");
        var name = $"CIBIL_Reports_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx";
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            name);
    }

    /// <summary>Admin: how many CIBIL credits this system has consumed, plus the vendor's
    /// remaining balance when the vendor exposes a balance endpoint.</summary>
    [HttpGet("portal-usage")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<PortalUsageInfo>> PortalUsage([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        => Ok(await _cibil.GetPortalUsageAsync(from, to));

    /// <summary>Admin: probe the vendor balance endpoint on demand.</summary>
    [HttpGet("vendor-balance")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<VendorBalanceInfo>> VendorBalance()
        => Ok(await _cibil.GetVendorBalanceAsync());

    /// <summary>The caller's own CIBIL pull events — backs the TL tab's "My Reports"
    /// section. Scoped to the caller's own EMP_CODE with no hierarchy expansion, so a TL
    /// never sees another employee's pulls. Includes FAILED/PENDING attempts; the
    /// vendor's failure text is redacted for non-admins.</summary>
    [HttpGet("my-reports")]
    public async Task<ActionResult<object>> MyReports([FromQuery] int take = 100)
    {
        var rows = await _cibil.ListMyPullsAsync(EmpCode, take);
        if (!IsAdmin)
            foreach (var r in rows) r.ErrorMessage = null;
        return Ok(new { total = rows.Count, rows });
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
