using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ICibilService _cibil;

    public SettingsController(IDbContext db, ICibilService cibil)
    {
        _db = db;
        _cibil = cibil;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private int RoleId => int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var r) ? r : 0;
    private bool IsAdmin => RoleId is 1 or 2;

    /// <summary>Current app settings (admins only).</summary>
    [HttpGet]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<object>> Get()
    {
        var dt = await _db.QueryAsync(
            "SELECT SETTING_KEY, SETTING_VALUE, UPDATED_ON FROM dbo.TBL_CIP_APP_SETTINGS ORDER BY SETTING_KEY");
        var rows = dt.AsEnumerable().Select(r => new
        {
            key = r["SETTING_KEY"].ToString(),
            value = r["SETTING_VALUE"].ToString(),
            updatedOn = r["UPDATED_ON"]
        }).ToList();
        return Ok(new { rows });
    }

    /// <summary>Update the default monthly CIBIL pull limit (admins only).
    /// The cached value is evicted on save, so the new limit applies to the very next
    /// quota read rather than up to 60 seconds later.</summary>
    [HttpPut("monthly-pull-limit")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SetMonthlyPullLimit([FromBody] PullLimitInput input)
    {
        if (input.MonthlyPullLimit is < 1 or > 100000)
            return BadRequest(new { error = "Monthly pull limit must be between 1 and 100000." });

        try
        {
            await _db.ExecuteAsync(@"
            MERGE dbo.TBL_CIP_APP_SETTINGS AS t
            USING (SELECT 'Cibil.MonthlyPullLimit' AS K) AS s
            ON t.SETTING_KEY = s.K
            WHEN MATCHED THEN
                UPDATE SET SETTING_VALUE = @v, UPDATED_BY = @by, UPDATED_ON = GETDATE()
            WHEN NOT MATCHED THEN
                INSERT (SETTING_KEY, SETTING_VALUE, UPDATED_BY, UPDATED_ON)
                VALUES (s.K, @v, @by, GETDATE());",
            p =>
            {
                p.AddWithValue("@v", input.MonthlyPullLimit.Value.ToString());
                p.AddWithValue("@by", EmpCode);
            });
        }
        catch (Exception ex)
        {
            // Most common cause: TBL_CIP_APP_SETTINGS (or its UPDATED_BY/UPDATED_ON
            // columns) missing on the server DB — script: Backend/sql/TBL_CIP_APP_SETTINGS.sql
            return StatusCode(500, new
            {
                error = "Could not save the setting. Check that TBL_CIP_APP_SETTINGS exists on the server DB (run Backend/sql/TBL_CIP_APP_SETTINGS.sql), then retry.",
                detail = ex.Message,
            });
        }

        // Evict the cached default so employees see the new number immediately.
        _cibil.InvalidatePullLimitCache();
        return Ok(new { saved = input.MonthlyPullLimit });
    }

    /// <summary>Verifies DB connectivity — used by the Settings page "Test connection" button.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test()
    {
        try
        {
            var v = await _db.ScalarAsync("SELECT 1");
            return Ok(new { ok = Convert.ToInt32(v) == 1 });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }
}

public class PullLimitInput
{
    public int? MonthlyPullLimit { get; set; }
}
