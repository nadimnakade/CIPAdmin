using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class SaveOcrGridRequest
{
    public string?[][] Grid { get; set; } = Array.Empty<string[]>();
}

public class OcrJobDto
{
    public int JobId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public long FileSize { get; set; }
    public string? ContentType { get; set; }
    public int RowCount { get; set; }
    public int ColCount { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// OCR to Excel: upload a photo/screenshot of a spreadsheet, get the extracted
/// grid back for preview/editing in the portal, and export it to .xlsx.
/// History is stored locally so any past conversion can be re-previewed or
/// re-exported. All signed-in roles may use the module.
/// </summary>
[ApiController]
[Route("api/ocr")]
[Authorize]
public class OcrController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IOcrService _ocr;
    private readonly ILogger<OcrController> _logger;

    private const long MaxBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly string[] AllowedExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public OcrController(IDbContext db, IOcrService ocr, ILogger<OcrController> logger)
    {
        _db = db; _ocr = ocr; _logger = logger;
    }

    private int GetEmpCode()
    {
        var v = User.FindFirst("empCode")?.Value;
        return int.TryParse(v, out var code) ? code : 0;
    }

    private static string UploadsRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ocr uploads");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Extract the spreadsheet grid from an image, store it and return it for preview.</summary>
    [HttpPost("extract")]
    [RequestSizeLimit(MaxBytes)]
    public async Task<IActionResult> Extract(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Select an image to convert." });
        if (file.Length > MaxBytes) return BadRequest(new { error = "File exceeds the 10 MB limit." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExt.Contains(ext))
            return BadRequest(new { error = $"Only image files are supported ({string.Join(", ", AllowedExt)})." });

        var grid = await _ocr.ExtractGridAsync(file.OpenReadStream());
        if (grid.RowCount == 0)
            return BadRequest(new { error = "No text could be read from the image. Try a sharper, well-lit photo taken straight from above." });

        // Persist the image + detected dimensions so the conversion stays re-openable.
        var stored = $"ocr_{GetEmpCode()}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        var relPath = Path.Combine("ocr uploads", stored);
        await using (var fs = System.IO.File.Create(Path.Combine(UploadsRoot(), stored)))
            await file.CopyToAsync(fs);

        // Store the extracted grid as JSON alongside the image so re-open needs no re-OCR.
        var json = System.Text.Json.JsonSerializer.Serialize(grid.Rows);
        await System.IO.File.WriteAllTextAsync(Path.Combine(UploadsRoot(), stored + ".json"), json);

        var jobId = Convert.ToInt32(await _db.ScalarAsync(@"
            INSERT INTO dbo.TBL_CIP_OCR_JOBS (EMP_CODE, ORIGINAL_FILE_NAME, STORED_FILE_NAME, FILE_PATH, FILE_SIZE, CONTENT_TYPE, ROW_COUNT, COL_COUNT)
            OUTPUT INSERTED.JOB_ID
            VALUES (@emp, @orig, @stored, @path, @size, @ct, @rows, @cols);",
            p =>
            {
                p.AddWithValue("@emp", GetEmpCode());
                p.AddWithValue("@orig", file.FileName);
                p.AddWithValue("@stored", stored);
                p.AddWithValue("@path", relPath);
                p.AddWithValue("@size", (int)file.Length);
                p.AddWithValue("@ct", file.ContentType ?? "");
                p.AddWithValue("@rows", grid.RowCount);
                p.AddWithValue("@cols", grid.ColCount);
            }));

        _logger.LogInformation("OCR job {JobId}: {Rows}x{Cols} grid from {File} ({Words} words)",
            jobId, grid.RowCount, grid.ColCount, file.FileName, grid.WordCount);

        return Ok(new
        {
            jobId,
            fileName = file.FileName,
            rows = grid.Rows,
            rowCount = grid.RowCount,
            colCount = grid.ColCount,
            wordCount = grid.WordCount,
            previewUrl = $"/api/ocr/jobs/{jobId}/image"
        });
    }

    /// <summary>Conversion history of the signed-in user (admins see everyone's).</summary>
    [HttpGet("jobs")]
    public async Task<ActionResult<List<OcrJobDto>>> Jobs()
    {
        var empCode = GetEmpCode();
        var isAdmin = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var role)
                      && Services.HierarchyService.IsAdminRole(role);

        var sql = @"SELECT JOB_ID, ORIGINAL_FILE_NAME, FILE_SIZE, CONTENT_TYPE, ROW_COUNT, COL_COUNT, CREATED_DATE
                    FROM dbo.TBL_CIP_OCR_JOBS WHERE IS_DELETED = 0";
        if (!isAdmin) sql += " AND EMP_CODE = @emp";
        sql += " ORDER BY CREATED_DATE DESC";

        var dt = await _db.QueryAsync(sql, p => p.AddWithValue("@emp", empCode));
        var list = new List<OcrJobDto>();
        foreach (DataRow r in dt.Rows)
        {
            list.Add(new OcrJobDto
            {
                JobId = r.Field<int>("JOB_ID"),
                OriginalFileName = r.Field<string>("ORIGINAL_FILE_NAME") ?? "",
                FileSize = r.Field<int>("FILE_SIZE"),
                ContentType = r.Field<string>("CONTENT_TYPE"),
                RowCount = r.Field<int>("ROW_COUNT"),
                ColCount = r.Field<int>("COL_COUNT"),
                CreatedDate = r.Field<DateTime>("CREATED_DATE")
            });
        }
        return Ok(list);
    }

    /// <summary>Re-open a saved conversion: the grid exactly as extracted (or last saved by the user).</summary>
    [HttpGet("jobs/{jobId:int}/grid")]
    public async Task<IActionResult> JobGrid(int jobId)
    {
        var (job, error) = await LoadOwnedJobAsync(jobId);
        if (error != null) return error;

        var jsonPath = Path.Combine(UploadsRoot(), job.StoredFileName + ".json");
        if (!System.IO.File.Exists(jsonPath))
            return NotFound(new { error = "The extracted data file is missing on disk." });

        var rows = System.Text.Json.JsonSerializer.Deserialize<string?[][]>(await System.IO.File.ReadAllTextAsync(jsonPath));
        return Ok(new { jobId, fileName = job.OriginalFileName, rows = rows ?? Array.Empty<string[]>() });
    }

    /// <summary>Save user edits made in the preview back over the stored grid.</summary>
    [HttpPut("jobs/{jobId:int}/grid")]
    public async Task<IActionResult> SaveJobGrid(int jobId, [FromBody] SaveOcrGridRequest request)
    {
        var (job, error) = await LoadOwnedJobAsync(jobId);
        if (error != null) return error;

        var grid = request.Grid ?? Array.Empty<string[]>();
        if (grid.Length == 0) return BadRequest(new { error = "The grid is empty." });

        var jsonPath = Path.Combine(UploadsRoot(), job.StoredFileName + ".json");
        await System.IO.File.WriteAllTextAsync(jsonPath,
            System.Text.Json.JsonSerializer.Serialize(grid));

        await _db.ExecuteAsync(
            "UPDATE dbo.TBL_CIP_OCR_JOBS SET ROW_COUNT = @r, COL_COUNT = @c WHERE JOB_ID = @id",
            p => { p.AddWithValue("@r", grid.Length); p.AddWithValue("@c", grid.Max(g => g.Length)); p.AddWithValue("@id", jobId); });

        return Ok(new { ok = true });
    }

    /// <summary>Preview the original uploaded image.</summary>
    [HttpGet("jobs/{jobId:int}/image")]
    public async Task<IActionResult> JobImage(int jobId)
    {
        var (job, error) = await LoadOwnedJobAsync(jobId);
        if (error != null) return error;

        var fullPath = Path.Combine(AppContext.BaseDirectory, job.FilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "Image is missing on disk." });

        var bytes = await System.IO.File.ReadAllBytesAsync(fullPath);
        var ct = string.IsNullOrWhiteSpace(job.ContentType) ? "application/octet-stream" : job.ContentType;
        return File(bytes, ct);
    }

    /// <summary>Export a grid (live edits included) to .xlsx — POST so big grids survive the request limit.</summary>
    [HttpPost("export")]
    public IActionResult Export([FromBody] SaveOcrGridRequest request)
    {
        var grid = request.Grid ?? Array.Empty<string[]>();
        if (grid.Length == 0) return BadRequest(new { error = "Nothing to export." });

        var bytes = Services.ExcelExporter.BuildFromArrays(grid, "Sheet1");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"OCR_Extract_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    /// <summary>Delete a conversion from history (and its stored files).</summary>
    [HttpDelete("jobs/{jobId:int}")]
    public async Task<IActionResult> DeleteJob(int jobId)
    {
        var (job, error) = await LoadOwnedJobAsync(jobId);
        if (error != null) return error;

        await _db.ExecuteAsync("UPDATE dbo.TBL_CIP_OCR_JOBS SET IS_DELETED = 1 WHERE JOB_ID = @id",
            p => p.AddWithValue("@id", jobId));

        TryDelete(Path.Combine(UploadsRoot(), job.StoredFileName + ".json"));
        return Ok(new { ok = true });
    }

    /// <summary>Load a job the caller is allowed to see (own job, or any when admin); 404-shaped error otherwise.</summary>
    private async Task<(OcrJobRow Job, IActionResult? Error)> LoadOwnedJobAsync(int jobId)
    {
        var empCode = GetEmpCode();
        var isAdmin = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var role)
                      && Services.HierarchyService.IsAdminRole(role);

        var sql = @"SELECT JOB_ID, EMP_CODE, ORIGINAL_FILE_NAME, STORED_FILE_NAME, FILE_PATH, CONTENT_TYPE
                    FROM dbo.TBL_CIP_OCR_JOBS WHERE JOB_ID = @id AND IS_DELETED = 0";
        if (!isAdmin) sql += " AND EMP_CODE = @emp";

        var dt = await _db.QueryAsync(sql, p => { p.AddWithValue("@id", jobId); p.AddWithValue("@emp", empCode); });
        if (dt.Rows.Count == 0)
            return (default, NotFound(new { error = "Conversion not found." }));

        var r = dt.Rows[0];
        return (new OcrJobRow(
            r.Field<int>("JOB_ID"),
            r.Field<string>("ORIGINAL_FILE_NAME") ?? "",
            r.Field<string>("STORED_FILE_NAME") ?? "",
            r.Field<string>("FILE_PATH") ?? "",
            r.Field<string>("CONTENT_TYPE")), null);
    }

    private record OcrJobRow(int JobId, string OriginalFileName, string StoredFileName, string FilePath, string ContentType);

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best effort */ }
    }
}
