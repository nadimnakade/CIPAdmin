using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class SaveLeadRequest
{
    public string? LeadName { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? Remarks { get; set; }
    public int? AssignedTo { get; set; }
    public string? Status { get; set; }
    public int? Product { get; set; }
}

public class LeadStatusRequest
{
    public string? Status { get; set; }
}

/// <summary>
/// Lead Master — CRUD over the real TBL_CIP_LEAD_MASTER (LM_* columns).
/// Statuses come from the live data (Ringing, Call Back, Pickup, Login, Disbursed,
/// Not Contactable, Not Interested, Not Doable, Sales Followup, Applied, ...).
/// "Delete" sets LM_STATUS = '-1' (the legacy convention); rows are never physically removed.
/// </summary>
[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IHierarchyService _hierarchy;
    private readonly ILogger<LeadsController> _logger;

    /// <summary>Statuses offered for manual assignment in this admin tool.</summary>
    public static readonly string[] AllowedStatuses =
        { "Ringing", "Call Back", "Sales Followup", "Pickup", "Docs pending for log", "Login", "Disbursed", "Applied", "Not Contactable", "Not Interested", "Not Doable", "Already Applied in A" };

    public LeadsController(IDbContext db, IHierarchyService hierarchy, ILogger<LeadsController> logger)
    {
        _db = db; _hierarchy = hierarchy; _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private int RoleId => int.TryParse(User.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : 0;

    /// <summary>SQL fragment restricting alias `l` to the viewer's subtree (empty for admins).</summary>
    private async Task<string> ScopeSqlAsync()
    {
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        if (scope.IsAdmin) return "";
        return $" AND (l.LM_ASSIGNED_TO IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)}) OR l.LM_ASSIGNED_TO IS NULL OR l.LM_CREATED_BY = {EmpCode})";
    }

    /// <summary>Server-paged, scoped, sortable lead listing over the legacy LM_* schema.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int? assignedTo = null,
        [FromQuery] int? product = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var scopeSql = await ScopeSqlAsync();
        // '-1' is the legacy deleted marker; never show it in the grid.
        var where = " WHERE ISNULL(l.LM_STATUS, '') <> '-1'" + scopeSql;
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (l.LM_NAME LIKE @q OR ISNULL(l.LM_CONTACT_NO,'') LIKE @q OR ISNULL(l.LM_EMAIL_ID,'') LIKE @q)";
        if (!string.IsNullOrWhiteSpace(status) && status != "All")
            where += " AND " + (status == "Unassigned" ? "ISNULL(l.LM_STATUS,'') = ''" : "l.LM_STATUS = @st");
        if (assignedTo is > 0)
            where += " AND l.LM_ASSIGNED_TO = @at";
        if (product is > 0)
            where += " AND l.LM_PRODUCT = @pr";

        Action<SqlParameterCollection> bind = p =>
        {
            if (!string.IsNullOrWhiteSpace(search)) p.AddWithValue("@q", "%" + search.Trim() + "%");
            if (!string.IsNullOrWhiteSpace(status) && status != "All" && status != "Unassigned") p.AddWithValue("@st", status);
            if (assignedTo is > 0) p.AddWithValue("@at", assignedTo.Value);
            if (product is > 0) p.AddWithValue("@pr", product.Value);
        };

        var total = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_LEAD_MASTER l" + where, bind));

        var dir = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderBy = (sortBy?.ToLowerInvariant()) switch
        {
            "name" => $"l.LM_NAME {dir}",
            "mobile" => $"l.LM_CONTACT_NO {dir}",
            "status" => $"l.LM_STATUS {dir}",
            "assigned" => $"am.EMP_NAME {dir}",
            "created" => $"l.LM_CREATED_ON {dir}",
            _ => $"l.LM_KEY {dir}"
        };

        var dt = await _db.QueryAsync($@"
            SELECT l.LM_KEY, l.LM_NAME, l.LM_CONTACT_NO, l.LM_EMAIL_ID, l.LM_ADDRESS, l.LM_REMARKS,
                   l.LM_CALLER_REMARKS, l.LM_PRODUCT, p.PRO_NAME AS PRODUCT_NAME,
                   l.LM_ASSIGNED_TO, am.EMP_NAME AS ASSIGNED_TO_NAME, l.LM_STATUS,
                   l.LM_CREATED_BY, l.LM_CREATED_ON, l.LM_ASSIGNED_ON
            FROM dbo.TBL_CIP_LEAD_MASTER l
            LEFT JOIN dbo.TBL_CIP_EMPLOYEE_MASTER am ON am.EMP_CODE = l.LM_ASSIGNED_TO
            LEFT JOIN dbo.TBL_CIP_PRODUCTS p ON p.PRO_KEY = l.LM_PRODUCT
            {where}
            ORDER BY {orderBy}
            OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY", bind);

        var rows = dt.AsEnumerable().Select(r => new
        {
            leadId = r.Field<int>("LM_KEY"),
            leadName = r["LM_NAME"].ToString(),
            mobile = r["LM_CONTACT_NO"].ToString(),
            email = r["LM_EMAIL_ID"].ToString(),
            address = r["LM_ADDRESS"].ToString(),
            remarks = r["LM_REMARKS"].ToString(),
            callerRemarks = r["LM_CALLER_REMARKS"].ToString(),
            product = r["LM_PRODUCT"] == DBNull.Value ? (int?)null : r.Field<int>("LM_PRODUCT"),
            productName = r["PRODUCT_NAME"].ToString(),
            assignedTo = r["LM_ASSIGNED_TO"] == DBNull.Value ? (int?)null : r.Field<int>("LM_ASSIGNED_TO"),
            assignedToName = r["ASSIGNED_TO_NAME"].ToString(),
            status = string.IsNullOrWhiteSpace(r["LM_STATUS"].ToString()) ? "Unassigned" : r["LM_STATUS"].ToString(),
            createdByName = (string?)null,
            createdDate = r["LM_CREATED_ON"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(r["LM_CREATED_ON"]),
            assignedOn = r["LM_ASSIGNED_ON"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["LM_ASSIGNED_ON"])
        }).ToList();

        // Distinct statuses in the data (for the filter dropdown).
        var statusDt = await _db.QueryAsync($@"
            SELECT DISTINCT LM_STATUS FROM dbo.TBL_CIP_LEAD_MASTER
            WHERE LM_STATUS IS NOT NULL AND LTRIM(RTRIM(LM_STATUS)) <> '' AND LM_STATUS <> '-1'
            ORDER BY LM_STATUS");
        var statuses = statusDt.AsEnumerable().Select(r => r["LM_STATUS"].ToString()!).ToList();

        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        return Ok(new
        {
            total,
            rows,
            scope = new { isAdmin = scope.IsAdmin, subtreeSize = scope.SubtreeSize },
            statuses,
            allowedStatuses = AllowedStatuses
        });
    }

    /// <summary>Team members available for assignment (viewer's subtree; admins: everyone).
    /// Connectors (role 2010 / designation 1014) are excluded.</summary>
    [HttpGet("assignees")]
    public async Task<ActionResult<object>> Assignees([FromQuery] string? search = null)
    {
        var scope = await _hierarchy.GetScopeAsync(EmpCode, RoleId);
        var scopeSql = scope.IsAdmin ? "" :
            $" WHERE e.EMP_CODE IN ({HierarchyService.InlineCodes(scope.SubtreeCodes)})";
        var where = (scopeSql == "" ? " WHERE" : scopeSql + " AND") +
                    " ISNULL(e.ACTIVE,0) = 1" +
                    " AND ISNULL(e.ROLE_ID,0) <> 2010 AND ISNULL(e.DESIG_ID,0) <> 1014";
        var sql = $@"
            SELECT TOP 50 e.EMP_CODE, e.EMP_NAME, dn.DESIG_NAME
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            LEFT JOIN dbo.TBL_CIP_DESIGNATION_MASTER dn ON dn.DESIG_ID = e.DESIG_ID
            {where}";
        var bind = default(Action<SqlParameterCollection>);
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND (e.EMP_NAME LIKE @q OR CAST(e.EMP_CODE AS VARCHAR(20)) LIKE @q)";
            bind = p => p.AddWithValue("@q", "%" + search.Trim() + "%");
        }
        sql += " ORDER BY e.EMP_NAME";

        var dt = await _db.QueryAsync(sql, bind);
        return Ok(dt.AsEnumerable().Select(r => new
        {
            empCode = r.Field<int>("EMP_CODE"),
            employeeName = r["EMP_NAME"].ToString(),
            designation = r["DESIG_NAME"].ToString()
        }).ToList());
    }

    /// <summary>Create a lead (legacy-schema columns; caller remarks left to the dialer).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveLeadRequest req)
    {
        var (ok, error) = Validate(req);
        if (!ok) return BadRequest(new { error });

        if (req.AssignedTo is > 0 && !await _hierarchy.IsInScopeAsync(EmpCode, RoleId, req.AssignedTo.Value))
            return StatusCode(403, new { error = "Assignee is outside your team's scope." });

        var dt = await _db.QueryAsync($@"
            INSERT INTO dbo.TBL_CIP_LEAD_MASTER
                (LM_NAME, LM_CONTACT_NO, LM_EMAIL_ID, LM_REMARKS, LM_PRODUCT,
                 LM_ASSIGNED_TO, LM_STATUS, LM_CREATED_BY, LM_CREATED_ON, LM_DATE)
            OUTPUT INSERTED.LM_KEY
            VALUES (@n, @m, @e, @rem, @pr, @at, @st, @by, GETDATE(), CAST(GETDATE() AS DATE));",
            p =>
        {
            BindLead(p, req);
            p.AddWithValue("@by", EmpCode);
        });
        var leadId = dt.Rows.Count > 0 ? Convert.ToInt32(dt.Rows[0][0]) : 0;

        _logger.LogInformation("Lead {LeadId} created by {User}", leadId, EmpCode);
        return Ok(new { ok = true, leadId });
    }

    /// <summary>Update a lead's fields and status.</summary>
    [HttpPut("{leadId:int}")]
    public async Task<IActionResult> Update(int leadId, [FromBody] SaveLeadRequest req)
    {
        var (ok, error) = Validate(req);
        if (!ok) return BadRequest(new { error });

        var existing = await _db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.TBL_CIP_LEAD_MASTER WHERE LM_KEY = @id",
            p => p.AddWithValue("@id", leadId));
        if (Convert.ToInt32(existing) == 0) return NotFound(new { error = "Lead not found." });

        if (req.AssignedTo is > 0 && !await _hierarchy.IsInScopeAsync(EmpCode, RoleId, req.AssignedTo.Value))
            return StatusCode(403, new { error = "Assignee is outside your team's scope." });

        var affected = await _db.ExecuteAsync($@"
            UPDATE dbo.TBL_CIP_LEAD_MASTER SET
                LM_NAME = LTRIM(RTRIM(@n)),
                LM_CONTACT_NO = NULLIF(LTRIM(RTRIM(@m)), ''),
                LM_EMAIL_ID = NULLIF(LTRIM(RTRIM(@e)), ''),
                LM_REMARKS = NULLIF(LTRIM(RTRIM(@rem)), ''),
                LM_PRODUCT = @pr,
                LM_ASSIGNED_TO = @at,
                LM_STATUS = @st,
                LM_MODIFIED_BY = @by,
                LM_MODIFIED_ON = GETDATE()
            WHERE LM_KEY = @id",
            p =>
        {
            BindLead(p, req);
            p.AddWithValue("@by", EmpCode);
            p.AddWithValue("@id", leadId);
        });
        if (affected == 0) return NotFound(new { error = "Lead not found." });

        _logger.LogInformation("Lead {LeadId} updated by {User}", leadId, EmpCode);
        return Ok(new { ok = true });
    }

    /// <summary>Change a lead's status (legacy workflow statuses).</summary>
    [HttpPut("{leadId:int}/status")]
    public async Task<IActionResult> SetStatus(int leadId, [FromBody] LeadStatusRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Status))
            return BadRequest(new { error = "Status is required." });

        var affected = await _db.ExecuteAsync($@"
            UPDATE dbo.TBL_CIP_LEAD_MASTER
            SET LM_STATUS = @st, LM_MODIFIED_BY = @by, LM_MODIFIED_ON = GETDATE()
            WHERE LM_KEY = @id",
            p =>
        {
            p.AddWithValue("@st", req.Status.Trim());
            p.AddWithValue("@by", EmpCode);
            p.AddWithValue("@id", leadId);
        });
        if (affected == 0) return NotFound(new { error = "Lead not found." });
        return Ok(new { ok = true });
    }

    /// <summary>Soft-delete a lead — legacy convention: LM_STATUS = '-1'.</summary>
    [HttpDelete("{leadId:int}")]
    public async Task<IActionResult> Delete(int leadId)
    {
        var affected = await _db.ExecuteAsync($@"
            UPDATE dbo.TBL_CIP_LEAD_MASTER
            SET LM_STATUS = '-1', LM_MODIFIED_BY = @by, LM_MODIFIED_ON = GETDATE()
            WHERE LM_KEY = @id",
            p =>
        {
            p.AddWithValue("@by", EmpCode);
            p.AddWithValue("@id", leadId);
        });
        if (affected == 0) return NotFound(new { error = "Lead not found." });

        _logger.LogInformation("Lead {LeadId} deleted by {User}", leadId, EmpCode);
        return Ok(new { ok = true });
    }

    private static (bool ok, string? error) Validate(SaveLeadRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LeadName)) return (false, "Lead name is required.");
        if (req.LeadName.Trim().Length > 150) return (false, "Lead name exceeds 150 characters.");
        if (!string.IsNullOrWhiteSpace(req.Mobile) && !System.Text.RegularExpressions.Regex.IsMatch(req.Mobile.Trim(), @"^[0-9+\-\s]{5,15}$"))
            return (false, "Mobile format is invalid.");
        if (!string.IsNullOrWhiteSpace(req.Email) && !System.Text.RegularExpressions.Regex.IsMatch(req.Email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return (false, "Email format is invalid.");
        return (true, null);
    }

    private static void BindLead(SqlParameterCollection p, SaveLeadRequest req)
    {
        p.AddWithValue("@n", req.LeadName!.Trim());
        p.AddWithValue("@m", (object?)req.Mobile?.Trim() ?? DBNull.Value);
        p.AddWithValue("@e", (object?)req.Email?.Trim() ?? DBNull.Value);
        p.AddWithValue("@rem", (object?)req.Remarks?.Trim() ?? DBNull.Value);
        p.AddWithValue("@pr", req.Product ?? 0);
        p.AddWithValue("@at", req.AssignedTo ?? (object)DBNull.Value);
        p.AddWithValue("@st", string.IsNullOrWhiteSpace(req.Status) ? "Ringing" : req.Status.Trim());
    }
}
