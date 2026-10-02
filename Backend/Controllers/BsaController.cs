using System.Text.RegularExpressions;
using CIPAdmin.Backend.Bsa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CIPAdmin.Backend.Controllers;

/// <summary>
/// Bank Statement Analyzer (BSA / CIBIL analyzer) — C# port of the rule-based
/// engine. Upload a bank statement (PDF/CSV/XLSX/TXT) and optionally a CIBIL
/// report (PDF/TXT); returns the full categorized analysis, cross-verification,
/// eligibility and underwriting results. No persistence — stateless per request.
/// </summary>
[ApiController]
[Route("api/bsa")]
[Authorize]
public class BsaController : ControllerBase
{
    private const long MaxBytes = 25 * 1024 * 1024; // 25 MB (matches Node server limit)
    private static readonly string[] StatementExt = { ".pdf", ".csv", ".xlsx", ".xls", ".txt" };

    [HttpPost("analyze")]
    [RequestSizeLimit(MaxBytes)]
    public IActionResult Analyze(IFormFile statement, IFormFile? cibil = null, string? cibilPassword = null)
    {
        if (statement == null || statement.Length == 0)
            return BadRequest(new { error = "No statement file uploaded" });
        if (statement.Length > MaxBytes)
            return BadRequest(new { error = "Statement file exceeds the 25 MB limit." });

        var ext = Path.GetExtension(statement.FileName).ToLowerInvariant();
        if (!StatementExt.Contains(ext))
            return BadRequest(new { error = $"Only {string.Join(", ", StatementExt)} files are allowed" });

        try
        {
            using var ms = new MemoryStream();
            statement.CopyTo(ms);
            var bytes = ms.ToArray();

            var transactions = BsaStatementParser.ParseFile(bytes, statement.FileName);
            if (transactions.Count == 0)
                return BadRequest(new
                {
                    error = "No transactions found. If this is a scanned/image PDF, export CSV/XLSX from netbanking and try again.",
                });

            var analysis = BsaAnalyzer.AnalyzeTransactions(transactions);

            object response = new
            {
                success = true,
                filename = statement.FileName,
                transactionCount = transactions.Count,
                analysis,
            };

            if (cibil != null && cibil.Length > 0)
            {
                var cibilExt = Path.GetExtension(cibil.FileName).ToLowerInvariant();
                if (cibilExt != ".pdf" && cibilExt != ".txt")
                    return BadRequest(new { error = "CIBIL report must be a PDF or TXT file" });
                if (cibil.Length > MaxBytes)
                    return BadRequest(new { error = "CIBIL file exceeds the 25 MB limit." });

                using var cms = new MemoryStream();
                cibil.CopyTo(cms);
                var cibilBytes = cms.ToArray();

                var cibilText = cibilExt == ".txt"
                    ? System.Text.Encoding.UTF8.GetString(cibilBytes)
                    : BsaStatementParser.ExtractPdfText(cibilBytes, cibilPassword, isCibil: true);

                var report = BsaAnalyzer.ParseCibilText(cibilText);
                var crossVerify = BsaAnalyzer.CrossVerifyCibil(analysis, report);
                var eligibility = BsaAnalyzer.AssessEligibility(analysis, report);
                var underwriting = BsaAnalyzer.AssessUnderwriting(analysis, report);

                return Ok(new
                {
                    success = true,
                    filename = statement.FileName,
                    transactionCount = transactions.Count,
                    analysis,
                    cibil = report,
                    crossVerify,
                    eligibility,
                    underwriting,
                });
            }

            return Ok(response);
        }
        catch (BsaStatementParser.BsaException bex)
        {
            return BadRequest(new { error = bex.Message, errorCode = bex.Code });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Analysis failed: " + ex.Message });
        }
    }

    public sealed class BsaExportRequest
    {
        public string? FileName { get; set; }
        public List<string[]>? Rows { get; set; }
    }

    /// <summary>Export a flat grid (already assembled by the frontend) to .xlsx.</summary>
    [HttpPost("export")]
    public IActionResult Export([FromBody] BsaExportRequest request)
    {
        var rows = request.Rows ?? new List<string[]>();
        if (rows.Count == 0) return BadRequest(new { error = "Nothing to export." });

        var bytes = Services.ExcelExporter.BuildFromArrays(rows.ToArray(),
            string.IsNullOrEmpty(request.FileName) ? "BSA Report" : TruncateSheet(request.FileName));
        var name = $"BSA_{(string.IsNullOrEmpty(request.FileName) ? "Report" : Sanitize(request.FileName))}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    private static string Sanitize(string s) =>
        Regex.Replace(s, @"[^A-Za-z0-9 _-]", "").Trim().Trunc(40);

    private static string TruncateSheet(string s)
    {
        foreach (var c in new[] { ':', '\\', '/', '?', '*', '[', ']' }) s = s.Replace(c, '_');
        return s.Length > 31 ? s[..31] : s;
    }
}

internal static class BsaStringExtensions
{
    public static string Trunc(this string s, int len) => s.Length <= len ? s : s[..len];
}
