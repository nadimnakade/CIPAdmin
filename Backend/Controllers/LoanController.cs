using System.Data;
using CIPAdmin.Backend.Models;
using CIPAdmin.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CIPAdmin.Backend.Controllers;

/// <summary>
/// Loan eligibility — a direct port of CIPPortal's LoanEligiilityController. The legacy
/// controller held no arithmetic of its own: it marshalled the posted header onto
/// <c>USP_CIP_LOAN_ELIGIBILITY</c> and returned the rows verbatim. This port keeps that
/// shape exactly, so eligibility results cannot drift from the legacy portal.
///
/// The EMI calculator that used to sit next to this screen ran entirely in the legacy
/// view's JavaScript and has no server side, so it lives in the React page too.
/// </summary>
[ApiController]
[Route("api/loan")]
[Authorize]
public class LoanController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ILogger<LoanController> _logger;

    public LoanController(IDbContext db, ILogger<LoanController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Run the eligibility check. <c>companyKey</c> of -1 means "company not listed",
    /// which makes the procedure fall back to the CAT D unlisted slab.
    /// </summary>
    [HttpPost("eligibility")]
    public async Task<ActionResult<object>> Eligibility([FromBody] EligibilityRequest req)
    {
        // The same guards the legacy view enforced client-side before it would call the controller.
        if (req.Net <= 0)
            return BadRequest(new { error = "Please enter salary." });
        if (req.Gross > 0 && req.Net >= req.Gross)
            return BadRequest(new { error = "Net salary cannot be greater than or equal to gross salary." });
        if (req.Tenure <= 0)
            return BadRequest(new { error = "Please enter tenure." });

        try
        {
            var ds = await _db.QuerySetAsync(
                "EXEC dbo.USP_CIP_LOAN_ELIGIBILITY @NAME=@name, @GROSS=@gross, @NET=@net, " +
                "@COMPANY_KEY=@companyKey, @ROI=@roi, @TENURE=@tenure, @CATEGORY=@category, @OBLIGATION=@obligation",
                p =>
                {
                    // @NAME is cast to int inside the procedure, so an empty FOIR must be sent as 0.
                    p.AddWithValue("@name", (object)Db_Null(req.Name));
                    p.AddWithValue("@gross", req.Gross);
                    p.AddWithValue("@net", req.Net);
                    p.AddWithValue("@companyKey", req.CompanyKey);
                    p.AddWithValue("@roi", req.Roi);
                    p.AddWithValue("@tenure", req.Tenure);
                    p.AddWithValue("@category", (req.Category ?? "0").Trim());
                    p.AddWithValue("@obligation", req.Obligation);
                },
                commandTimeout: 180);

            var rows = new List<EligibilityRow>();
            if (ds.Tables.Count > 0)
            {
                foreach (DataRow r in ds.Tables[0].Rows)
                {
                    // The legacy BO skipped rows with a blank Bank.
                    if (string.IsNullOrEmpty(r["Bank"]?.ToString())) continue;
                    rows.Add(new EligibilityRow
                    {
                        Bank = r["Bank"].ToString()!,
                        Category = r["Category"].ToString() ?? "",
                        Amt = r["Amt"].ToString() ?? "",
                        Slab = r["Slab"].ToString() ?? "",
                    });
                }
            }

            return Ok(new { rows });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USP_CIP_LOAN_ELIGIBILITY failed");
            return StatusCode(500, new { error = "Eligibility check failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Company typeahead for the eligibility form. Only companies that actually have an
    /// eligibility row for one of their banks are returned — anything else would produce
    /// an empty result set from the procedure and look like a broken search.
    /// </summary>
    [HttpGet("companies")]
    public async Task<ActionResult<object>> Companies([FromQuery] string? prefix, [FromQuery] int limit = 20)
    {
        prefix = (prefix ?? "").Trim();
        if (prefix.Length < 3) return Ok(Array.Empty<object>());

        var dt = await _db.QueryAsync(@"
            SELECT TOP (@lim) cc.COMPANY_KEY, cc.COMPANY_NAME
            FROM dbo.TBL_CIP_COMPANY_CATEGORY cc
            WHERE cc.COMPANY_NAME LIKE @q
              AND EXISTS (
                    SELECT 1 FROM dbo.TBL_CIP_ELIGIBILITY_CONFIG ec
                    WHERE ec.EG_BANK_KEY = cc.COMPANY_BANK_KEY
                      AND ec.EG_TENURE IN (12, 24, 36, 48, 60))
            GROUP BY cc.COMPANY_KEY, cc.COMPANY_NAME
            ORDER BY cc.COMPANY_NAME",
            p =>
            {
                p.AddWithValue("@q", prefix + "%");
                p.AddWithValue("@lim", Math.Clamp(limit, 1, 50));
            });

        return Ok(dt.AsEnumerable()
            .Select(r => new { id = r.Field<int>("COMPANY_KEY"), name = r["COMPANY_NAME"]?.ToString() ?? "" })
            .ToList());
    }

    /// <summary>
    /// The categories offered in the "User Recommended" column. These are the EG_CAT
    /// values USP_CIP_LOAN_ELIGIBILITY actually matches on, read from the slab config —
    /// the legacy screen populated the list from USP_CIP_ELIGIBILITY_COMPANY_CATEGORY,
    /// which also returned a "--Select--" placeholder and unrelated category spellings.
    /// </summary>
    [HttpGet("categories")]
    public async Task<ActionResult<object>> Categories()
    {
        var dt = await _db.QueryAsync(
            "SELECT DISTINCT LTRIM(RTRIM(EG_CAT)) AS CAT FROM dbo.TBL_CIP_ELIGIBILITY_CONFIG " +
            "WHERE EG_CAT IS NOT NULL AND LTRIM(RTRIM(EG_CAT)) <> '' ORDER BY CAT");
        return Ok(dt.AsEnumerable().Select(r => r["CAT"]?.ToString() ?? "").Where(c => c.Length > 0).ToList());
    }

    /// <summary>
    /// Read-only view of the slab configuration that drives eligibility, so the screen can
    /// explain a number instead of just showing it. (The legacy EligibilityConfigController
    /// was entirely commented out and never touched the database.)
    /// </summary>
    [HttpGet("config")]
    public async Task<ActionResult<object>> Config()
    {
        var dt = await _db.QueryAsync(@"
            SELECT ec.EG_BANK_KEY, b.BANK_NAME, ec.EG_CAT, ec.EG_TENURE,
                   ec.EG_25, ec.EG_35, ec.EG_50, ec.EG_75, ec.EG_100
            FROM dbo.TBL_CIP_ELIGIBILITY_CONFIG ec
            LEFT JOIN dbo.TBL_CIP_BANK_MASTER b ON b.BANK_ID = ec.EG_BANK_KEY
            ORDER BY b.BANK_NAME, ec.EG_CAT, ec.EG_TENURE");

        return Ok(dt.AsEnumerable().Select(r => new
        {
            bankId = r.Field<int>("EG_BANK_KEY"),
            bank = r["BANK_NAME"]?.ToString() ?? "",
            category = r["EG_CAT"]?.ToString() ?? "",
            tenure = r["EG_TENURE"]?.ToString() ?? "",
            slab25 = r["EG_25"]?.ToString() ?? "",
            slab35 = r["EG_35"]?.ToString() ?? "",
            slab50 = r["EG_50"]?.ToString() ?? "",
            slab75 = r["EG_75"]?.ToString() ?? "",
            slab100 = r["EG_100"]?.ToString() ?? "",
        }).ToList());
    }

    private static object Db_Null(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim()!;
}