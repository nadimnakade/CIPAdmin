using System.Data;
using CIPAdmin.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CIPAdmin.Backend.Controllers;

/// <summary>
/// Punch In / Out — a port of CIPPortal's PunchInController and the PunchIn view.
///
/// The legacy screen offered three cascading selects (Location → TL → Caller, Caller being
/// multi-select), a datetime-local box and two buttons that posted to
/// <c>PunchIn/Manage</c>, which called <c>USP_MANAGE_PUNCHINOUT_TEST</c> with the chosen
/// employee codes as a comma-separated string. All of that is reproduced here; the one
/// addition is a "not punched today" list, because the legacy screen gave no feedback at
/// all after a punch.
/// </summary>
[ApiController]
[Route("api/punchin")]
[Authorize]
public class PunchInController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ILogger<PunchInController> _logger;

    /// <summary>The procedure splits this into rows; keep it well inside the limit.</summary>
    private const int MaxEmployeesPerPunch = 500;

    public PunchInController(IDbContext db, ILogger<PunchInController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);

    /// <summary>Locations (branch offices).</summary>
    [HttpGet("locations")]
    public async Task<ActionResult<object>> Locations()
        => Ok(await DropdownAsync("FLocation"));

    /// <summary>
    /// Team leaders for a location, scoped to the signed-in user's reporting line.
    /// The legacy PunchIn view called MonthlyAppReport/FillSupervisor, which runs
    /// USP_CIP_FILL_SUPERVISOR — not the unfiltered FSupervisor dropdown.
    /// </summary>
    [HttpPost("supervisors")]
    public async Task<ActionResult<object>> Supervisors([FromBody] BranchRequest body)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_SUPERVISOR @BRANCH_ID=@b, @USER_KEY=@u",
            p => { p.AddWithValue("@b", body.BranchId); p.AddWithValue("@u", EmpCode); });

        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
        {
            // Columns are EMP_NAME, EMP_CODE, ORD — unnamed aliases, so read positionally.
            var name = r[0]?.ToString()?.Trim() ?? "";
            if (!int.TryParse(r[1]?.ToString(), out var code) || code == 0) continue;
            list.Add(new Option(code, name));
        }
        return Ok(list);
    }

    /// <summary>
    /// Callers reporting to a team leader at a location: the TL plus their direct reports.
    /// The legacy view called MonthlyAppReport/FillTelecaller, which runs
    /// USP_CIP_DATA_ENTRY with @Action='FTelecaller' and @SUPERVISOR_ID / @BRANCH_ID —
    /// the FTelecaller branch of USP_CIP_FILL_DROPDOWN is a different (and wrong) query.
    /// </summary>
    [HttpPost("callers")]
    public async Task<ActionResult<object>> Callers([FromBody] SupervisorRequest body)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_DATA_ENTRY @Action='FTelecaller', @SUPERVISOR_ID=@s, @BRANCH_ID=@b",
            p => { p.AddWithValue("@s", body.SupervisorId ?? -1); p.AddWithValue("@b", body.BranchId); },
            CancellationToken.None);

        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
        {
            if (!int.TryParse(r[0]?.ToString(), out var code) || code == 0) continue;
            var name = r[1]?.ToString()?.Trim() ?? "";
            if (name.StartsWith("---", StringComparison.Ordinal) || name.Length == 0) continue;
            list.Add(new Option(code, name));
        }
        return Ok(list);
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
        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
        {
            var id = r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]);
            var name = r[1]?.ToString() ?? "";
            // The legacy dropdowns carried the procedure's own "---Select---" row. The page
            // already renders its own placeholder, so drop it rather than showing it twice.
            if (id <= 0 && (name.Length == 0 || name.StartsWith("---", StringComparison.Ordinal))) continue;
            list.Add(new Option(id, name));
        }
        return list;
    }

    public class PunchRequest
    {
        /// <summary>
        /// When the punch happened. Any ISO-8601 value the model binder can parse; it is
        /// normalised server-side before being handed to the procedure.
        /// </summary>
        public DateTime Date { get; set; } = DateTime.Now;

        /// <summary>"I" punches in, anything else punches out — the procedure's own test.</summary>
        public string Type { get; set; } = "I";

        public List<int> EmpKeys { get; set; } = new();
    }

    public class BranchRequest { public int BranchId { get; set; } }
    public class SupervisorRequest { public int BranchId { get; set; } public int? SupervisorId { get; set; } }

    /// <summary>Record a punch in or out for the selected employees.</summary>
    [HttpPost("punch")]
    public async Task<IActionResult> Punch([FromBody] PunchRequest body)
    {
        var employees = body.EmpKeys.Where(k => k > 0).Distinct().ToList();
        if (employees.Count == 0)
            return BadRequest(new { error = "Select at least one employee." });
        if (employees.Count > MaxEmployeesPerPunch)
            return BadRequest(new { error = $"At most {MaxEmployeesPerPunch} employees can be punched at once." });

        var type = string.Equals(body.Type, "I", StringComparison.OrdinalIgnoreCase) ? "I" : "O";

        // USP_MANAGE_PUNCHINOUT_TEST reads the timestamp with fixed SUBSTRING offsets
        // (year 1-4, month 6-7, day 9-10, time 12-16), which only line up with the
        // "yyyy-MM-ddTHH:mm" shape an <input type="datetime-local"> produces. Reformatting
        // here means the endpoint is safe for any client that sends a normal ISO timestamp.
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

            // The procedure answers with the string it recorded, so the user can see the
            // exact value that landed in EAM_START_TIME / EAM_END_TIME.
            var recorded = dt.Rows.Count > 0 ? dt.Rows[0][0]?.ToString() ?? "" : "";

            _logger.LogInformation(
                "Punch {Type} recorded for {Count} employee(s) at {Stamp} by {Emp}",
                type == "I" ? "in" : "out", employees.Count, recorded,
                User.FindFirst("empName")?.Value ?? "");

            return Ok(new { ok = true, type, count = employees.Count, recorded });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USP_MANAGE_PUNCHINOUT_TEST failed for {Count} employee(s)", employees.Count);
            return StatusCode(500, new { error = "Could not record the punch: " + ex.Message });
        }
    }

    /// <summary>
    /// Employees who have not punched in today, with their team leader and location.
    /// Backed by USP_EMPLOYEE_NOT_PUNCHED_TODAY; the columns are unnamed in that procedure,
    /// so they are read positionally.
    /// </summary>
    [HttpGet("not-punched")]
    public async Task<ActionResult<object>> NotPunched()
    {
        var dt = await _db.QueryAsync("EXEC dbo.USP_EMPLOYEE_NOT_PUNCHED_TODAY", null, CancellationToken.None);
        var rows = new List<object>();
        foreach (DataRow r in dt.Rows)
        {
            rows.Add(new
            {
                employee = r[0]?.ToString() ?? "",
                supervisor = r.Table.Columns.Count > 1 ? r[1]?.ToString() ?? "" : "",
                location = r.Table.Columns.Count > 2 ? r[2]?.ToString() ?? "" : "",
            });
        }
        return Ok(new { count = rows.Count, rows });
    }

    public record Option(int Id, string Name);
}