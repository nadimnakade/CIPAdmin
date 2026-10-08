using System.Data;
using CIPAdmin.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CIPAdmin.Backend.Controllers;

[ApiController]
[Route("api/punchin")]
[Authorize]
public class PunchInController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ILogger<PunchInController> _logger;
    private const int MaxEmployeesPerPunch = 500;

    public PunchInController(IDbContext db, ILogger<PunchInController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);

    [HttpGet("locations")]
    public async Task<ActionResult<object>> Locations() => Ok(await DropdownAsync("FLocation"));

    [HttpPost("supervisors")]
    public async Task<ActionResult<object>> Supervisors([FromBody] BranchRequest body)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_SUPERVISOR @BRANCH_ID=@b, @USER_KEY=@u",
            p => { p.AddWithValue("@b", body.BranchId); p.AddWithValue("@u", EmpCode); });
        var raw = dt.Rows.Cast<DataRow>()
            .Select(r => new
            {
                Name = r[0]?.ToString()?.Trim() ?? "",
                Code = int.TryParse(r[1]?.ToString(), out var code) ? code : 0
            })
            .Where(row => row.Code > 0)
            .ToList();
        var eligible = await EmployeeFilters.FilterActiveCodesAsync(_db, raw.Select(row => row.Code));
        return Ok(raw.Where(row => eligible.Contains(row.Code)).Select(row => new Option(row.Code, row.Name)).ToList());
    }

    [HttpPost("callers")]
    public async Task<ActionResult<object>> Callers([FromBody] SupervisorRequest body)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_DATA_ENTRY @Action='FTelecaller', @SUPERVISOR_ID=@s, @BRANCH_ID=@b",
            p => { p.AddWithValue("@s", body.SupervisorId ?? -1); p.AddWithValue("@b", body.BranchId); },
            CancellationToken.None);
        var raw = dt.Rows.Cast<DataRow>()
            .Select(r => new
            {
                Code = int.TryParse(r[0]?.ToString(), out var code) ? code : 0,
                Name = r[1]?.ToString()?.Trim() ?? ""
            })
            .Where(row => row.Code > 0 && row.Name.Length > 0 && !row.Name.StartsWith("---", StringComparison.Ordinal))
            .ToList();
        var eligible = await EmployeeFilters.FilterActiveCodesAsync(_db, raw.Select(row => row.Code));
        return Ok(raw.Where(row => eligible.Contains(row.Code)).Select(row => new Option(row.Code, row.Name)).ToList());
    }

    private async Task<List<Option>> DropdownAsync(string flag, int? branchId = null, int? supervisorId = null)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_DROPDOWN @Action=@a, @PARAM=@branch, @PARAMNEW=@sup",
            p =>
            {
                p.AddWithValue("@a", flag);
                p.AddWithValue("@branch", branchId ?? 0);
                p.AddWithValue("@sup", supervisorId.HasValue ? (object)supervisorId.Value.ToString() : "");
            });
        var raw = dt.Rows.Cast<DataRow>()
            .Select(r => new Option(r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]), r[1]?.ToString() ?? ""))
            .Where(row => row.Id > 0 || (row.Name.Length > 0 && !row.Name.StartsWith("---", StringComparison.Ordinal)))
            .ToList();
        // FLocation returns location-master IDs, not employee codes. Leave those
        // options intact; employee filtering belongs on the employee-valued pickers.
        return raw;
    }

    public class PunchRequest
    {
        public DateTime Date { get; set; } = DateTime.Now;
        public string Type { get; set; } = "I";
        public List<int> EmpKeys { get; set; } = new();
    }

    public class BranchRequest { public int BranchId { get; set; } }
    public class SupervisorRequest { public int BranchId { get; set; } public int? SupervisorId { get; set; } }

    [HttpPost("punch")]
    public async Task<IActionResult> Punch([FromBody] PunchRequest body)
    {
        var submitted = body.EmpKeys.Where(code => code > 0).Distinct().ToList();
        var eligible = await EmployeeFilters.FilterActiveCodesAsync(_db, submitted);
        var employees = eligible.ToList();
        if (employees.Count == 0)
            return BadRequest(new { error = "Select at least one active, non-connector employee." });
        if (employees.Count != submitted.Count)
            return BadRequest(new { error = "One or more selected employees are inactive, deleted, or connectors." });
        if (employees.Count > MaxEmployeesPerPunch)
            return BadRequest(new { error = $"At most {MaxEmployeesPerPunch} employees can be punched at once." });

        var type = string.Equals(body.Type, "I", StringComparison.OrdinalIgnoreCase) ? "I" : "O";
        var stamp = body.Date.ToString("yyyy-MM-ddTHH:mm");
        try
        {
            var dt = await _db.QueryAsync(
                "EXEC dbo.USP_MANAGE_PUNCHINOUT_TEST @Date=@d, @Type=@t, @UserKey=@k",
                p =>
                {
                    p.AddWithValue("@d", stamp);
                    p.AddWithValue("@t", type);
                    p.AddWithValue("@k", string.Join(",", employees));
                });
            var recorded = dt.Rows.Count > 0 ? dt.Rows[0][0]?.ToString() ?? "" : "";
            _logger.LogInformation("Punch {Type} recorded for {Count} employee(s) at {Stamp} by {Emp}",
                type == "I" ? "in" : "out", employees.Count, recorded, User.FindFirst("empName")?.Value ?? "");
            return Ok(new { ok = true, type, count = employees.Count, recorded });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USP_MANAGE_PUNCHINOUT_TEST failed for {Count} employee(s)", employees.Count);
            return StatusCode(500, new { error = "Could not record the punch: " + ex.Message });
        }
    }

    [HttpGet("not-punched")]
    public async Task<ActionResult<object>> NotPunched()
    {
        // The legacy procedure only selects the display values and contains a stale ACTIVE
        // predicate. Rebuild its query with the canonical on-roll/connector rules instead.
        var dt = await _db.QueryAsync(@"
            SELECT vw.Caller, vw.TL, vw.[Location Name]
            FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
            INNER JOIN dbo.VW_EMPLOYEE_HIERARCHY vw ON vw.[Caller Code] = e.EMP_CODE
            LEFT JOIN dbo.TBL_CIP_ATTENDANCE_MASTER a
              ON e.EMP_CODE = a.EAM_EMP_CODE
             AND CAST(a.EAM_CREATED_ON AS DATE) = CAST(GETDATE() AS DATE)
            WHERE a.EAM_EMP_CODE IS NULL
              AND " + EmployeeFilters.Active("e") + " AND " + EmployeeFilters.NotDeleted("e") + " AND " + EmployeeFilters.NotConnector("e") + @"
              AND ISNULL(e.ROLE_ID, 0) NOT IN (1, 2, 2013, 2014, 2015)
            ORDER BY vw.[Location Name], vw.TL");
        var rows = dt.AsEnumerable().Select(r => new
        {
            employee = r["Caller"]?.ToString() ?? "",
            supervisor = r["TL"]?.ToString() ?? "",
            location = r["Location Name"]?.ToString() ?? "",
        }).ToList();
        return Ok(new { count = rows.Count, rows });
    }

    public record Option(int Id, string Name);
}
