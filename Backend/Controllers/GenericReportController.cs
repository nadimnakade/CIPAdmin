using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class GenericReportDefDto
{
    public int Value { get; set; }
    public string Text { get; set; } = "";
}

public class GenericReportRunRequest
{
    public int ReportType { get; set; }
    public int Month { get; set; } = -1;
    public int Year { get; set; } = 0;
}

public class GenericReportResultDto
{
    public List<string[]> Rows { get; set; } = new();   // row[0] = header
    public int TotalRows => Math.Max(0, Rows.Count - 1);
    public int ReportType { get; set; }
    public string ReportName { get; set; } = "";
    public int Month { get; set; }
    public int Year { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Generic report module (port of CIPPortal GenericReportController):
/// runs USP_GENERIC_REPORT for a selected report type / month / year and
/// lets the user preview the grid or export it to Excel.
/// </summary>
[ApiController]
[Route("api/generic-reports")]
[Authorize]
public class GenericReportController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ILogger<GenericReportController> _logger;

    public GenericReportController(IDbContext db, ILogger<GenericReportController> logger)
    {
        _db = db; _logger = logger;
    }

    /// <summary>Report type dropdown — served from USP_CIP_FILL_DROPDOWN ('GenericReport').</summary>
    [HttpGet("types")]
    public async Task<ActionResult<List<GenericReportDefDto>>> Types()
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_DROPDOWN @Action = 'GenericReport', @PARAM = 0");

        var list = new List<GenericReportDefDto>();
        foreach (DataRow r in dt.Rows)
        {
            if (!int.TryParse(r[0].ToString(), out var value)) continue;
            var text = r[1].ToString() ?? "";
            if (value == 0) continue; // drop the "---Select---" row
            list.Add(new GenericReportDefDto { Value = value, Text = text });
        }
        return Ok(list);
    }

    /// <summary>
    /// Run a report. Returns the raw column headers + rows so the UI can render any shape.
    /// Month/Year are ignored by most report SPs but forwarded as in the legacy module.
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<GenericReportResultDto>> Run([FromBody] GenericReportRunRequest request)
    {
        var empCode = GetEmpCode();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var ds = await _db.QuerySetAsync(
                "EXEC dbo.USP_GENERIC_REPORT @Type = @type, @Month = @month, @Year = @year, @UserKey = @key",
                p =>
                {
                    p.AddWithValue("@type", request.ReportType.ToString());
                    p.AddWithValue("@month", request.Month);
                    p.AddWithValue("@year", request.Year == 0 ? DateTime.Now.Year : request.Year);
                    p.AddWithValue("@key", empCode);
                }, commandTimeout: 180);

            var result = new GenericReportResultDto
            {
                ReportType = request.ReportType,
                Month = request.Month,
                Year = request.Year == 0 ? DateTime.Now.Year : request.Year
            };

            var table = ds.Tables.Count > 0 ? ds.Tables[0] : null;
            if (table != null)
            {
                result.Rows.Add(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
                foreach (DataRow r in table.Rows)
                    result.Rows.Add(r.ItemArray.Select(v => v == DBNull.Value ? "" : v.ToString() ?? "").ToArray());
            }

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            _logger.LogInformation("Generic report {Type} for user {User} returned {Rows} rows in {Ms} ms",
                request.ReportType, empCode, result.TotalRows, sw.ElapsedMilliseconds);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generic report {Type} failed for user {User}", request.ReportType, empCode);
            return StatusCode(500, new { error = "Report failed: " + ex.Message });
        }
    }

    /// <summary>Export the current run to a real .xlsx file (OpenXML, no Interop).</summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] GenericReportRunRequest request)
    {
        var empCode = GetEmpCode();
        try
        {
            var ds = await _db.QuerySetAsync(
                "EXEC dbo.USP_GENERIC_REPORT @Type = @type, @Month = @month, @Year = @year, @UserKey = @key",
                p =>
                {
                    p.AddWithValue("@type", request.ReportType.ToString());
                    p.AddWithValue("@month", request.Month);
                    p.AddWithValue("@year", request.Year == 0 ? DateTime.Now.Year : request.Year);
                    p.AddWithValue("@key", empCode);
                }, commandTimeout: 180);

            var table = ds.Tables.Count > 0 ? ds.Tables[0] : null;
            if (table == null || table.Rows.Count == 0)
                return NotFound(new { error = "No data to export for this report." });

            var bytes = ExcelExporter.BuildXlsx(table,
                sheetName: SanitizeSheetName($"Report {request.ReportType}"),
                headerStyle: true);

            var fileName = $"Report_{request.ReportType}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generic report export {Type} failed for user {User}", request.ReportType, empCode);
            return StatusCode(500, new { error = "Export failed: " + ex.Message });
        }
    }

    private static string SanitizeSheetName(string s)
    {
        foreach (var c in new[] { ':', '\\', '/', '?', '*', '[', ']' }) s = s.Replace(c, '_');
        return s.Length > 31 ? s[..31] : s;
    }

    private int GetEmpCode()
    {
        var v = User.FindFirst("empCode")?.Value;
        return int.TryParse(v, out var code) ? code : 0;
    }
}
