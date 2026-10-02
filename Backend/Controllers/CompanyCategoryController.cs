using System.Data;
using System.Text;
using CIPAdmin.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace CIPAdmin.Backend.Controllers;

public class CompanyRow
{
    public int Key { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int ProductId { get; set; }
    public string Product { get; set; } = "";
    public int BankId { get; set; }
    public string Bank { get; set; } = "";
    public DateTime? CreatedOn { get; set; }
}

public class CompanyUpsertRequest
{
    public int Key { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int ProductId { get; set; }
    public int BankId { get; set; }
}

/// <summary>
/// Company Category master — port of CIPPortal's CompanyCategoryController.
/// The underlying table (TBL_CIP_COMPANY_CATEGORY, 1.7M+ rows) is append-mostly:
/// the legacy module bulk-copied rows from Excel/PDF, so the same key can exist
/// for several Product/Bank combinations. Listing/edit here targets the latest
/// row per company name to keep the grid stable.
/// </summary>
[ApiController]
[Route("api/company-categories")]
[Authorize]
public class CompanyCategoryController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly ILogger<CompanyCategoryController> _logger;
    private const int MaxUploadRows = 100_000;

    public CompanyCategoryController(IDbContext db, ILogger<CompanyCategoryController> logger)
    {
        _db = db; _logger = logger;
    }

    /// <summary>Banks for the upload/inline-edit dropdowns.</summary>
    [HttpGet("banks")]
    public async Task<ActionResult<object>> Banks()
    {
        var dt = await _db.QueryAsync("SELECT BANK_ID, BANK_NAME FROM dbo.TBL_CIP_BANK_MASTER WHERE BANK_ACTIVE = 1 ORDER BY BANK_NAME");
        return Ok(dt.AsEnumerable().Select(r => new { id = r.Field<int>("BANK_ID"), name = r.Field<string>("BANK_NAME") }));
    }

    /// <summary>Products (loan types) for the dropdowns.</summary>
    [HttpGet("products")]
    public async Task<ActionResult<object>> Products()
    {
        var dt = await _db.QueryAsync("SELECT LOAN_ID, LOAN_NAME FROM dbo.TBL_CIP_LOAN_MASTER WHERE LOAN_ACTIVE = 1 ORDER BY LOAN_NAME");
        return Ok(dt.AsEnumerable().Select(r => new { id = r.Field<int>("LOAN_ID"), name = r.Field<string>("LOAN_NAME") }));
    }

    /// <summary>Distinct category values (top by usage) for the filter dropdown.</summary>
    [HttpGet("categories")]
    public async Task<ActionResult<object>> Categories()
    {
        var dt = await _db.QueryAsync(
            "SELECT TOP 100 COMPANY_CATEGORY, COUNT(1) AS CNT FROM dbo.TBL_CIP_COMPANY_CATEGORY GROUP BY COMPANY_CATEGORY ORDER BY CNT DESC");
        return Ok(dt.AsEnumerable().Select(r => new { name = r.Field<string>("COMPANY_CATEGORY"), count = r.Field<int>("CNT") }));
    }

    /// <summary>
    /// Paged, filtered listing. Because COMPANY_KEY repeats across Product/Bank
    /// variations, one row is returned per company name — the latest entry.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] int? productId = null,
        [FromQuery] int? bankId = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var where = new StringBuilder("WHERE 1=1"); // predicates alias the inner table as cc
        var p = new CombinedParams();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Append(" AND c.COMPANY_NAME LIKE @q");
            p.Add("@q", "%" + search.Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            where.Append(" AND c.COMPANY_CATEGORY = @cat");
            p.Add("@cat", category.Trim());
        }
        if (productId is > 0)
        {
            where.Append(" AND c.COMPANY_PRODUCT_ID = @prod");
            p.Add("@prod", productId.Value);
        }
        if (bankId is > 0)
        {
            where.Append(" AND c.COMPANY_BANK_ID = @bank");
            p.Add("@bank", bankId.Value);
        }

        var w = where.ToString().Replace("c.", "cc.");
        var count = await _db.ScalarAsync($@"
            SELECT COUNT(DISTINCT cc.COMPANY_NAME)
            FROM dbo.TBL_CIP_COMPANY_CATEGORY cc {w}", p.Bind());

        // COMPANY_KEY is identity → the max key per company name is its latest row.
        var dir = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderInner = sortBy switch
        {
            "category" => $"MAX(cc.COMPANY_CATEGORY) {dir}",
            "createdOn" => $"MAX(cc.CREATED_ON) {dir}",
            _ => $"cc.COMPANY_NAME {dir}"
        };
        var orderOuter = sortBy switch
        {
            "category" => $"c.COMPANY_CATEGORY {dir}",
            "createdOn" => $"c.CREATED_ON {dir}",
            _ => $"c.COMPANY_NAME {dir}"
        };
        var offset = (page - 1) * pageSize;
        var dt = await _db.QueryAsync($@"
            SELECT c.COMPANY_KEY AS KEY_, LTRIM(RTRIM(c.COMPANY_NAME)) AS NAME, c.COMPANY_CATEGORY AS CATEGORY,
                   c.COMPANY_PRODUCT_ID AS PRODUCT_ID, l.LOAN_NAME, c.COMPANY_BANK_ID AS BANK_ID,
                   b.BANK_NAME, c.CREATED_ON AS LAST_CREATED
            FROM (
                SELECT cc.COMPANY_NAME, MAX(cc.COMPANY_KEY) AS MK
                FROM dbo.TBL_CIP_COMPANY_CATEGORY cc {w}
                GROUP BY cc.COMPANY_NAME
                ORDER BY {orderInner}
                OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY
            ) m
            JOIN dbo.TBL_CIP_COMPANY_CATEGORY c ON c.COMPANY_KEY = m.MK
            JOIN dbo.TBL_CIP_LOAN_MASTER l ON l.LOAN_ID = c.COMPANY_PRODUCT_ID
            JOIN dbo.TBL_CIP_BANK_MASTER b ON b.BANK_ID = c.COMPANY_BANK_ID
            ORDER BY {orderOuter}", p.AddOff(offset, pageSize).Bind());

        var list = dt.AsEnumerable().Select(r => new CompanyRow
        {
            Key = r.Field<int>("KEY_"),
            Name = r.Field<string>("NAME"),
            Category = r.Field<string>("CATEGORY"),
            ProductId = r.Field<int>("PRODUCT_ID"),
            Product = r.Field<string>("LOAN_NAME"),
            BankId = r.Field<int>("BANK_ID"),
            Bank = r.Field<string>("BANK_NAME"),
            CreatedOn = r["LAST_CREATED"] == DBNull.Value ? null : r.Field<DateTime>("LAST_CREATED")
        }).ToList();

        return Ok(new { total = Convert.ToInt32(count), rows = list });
    }

    /// <summary>Update category (and optionally product/bank) on the latest row for a company.</summary>
    [HttpPut("{key:int}")]
    public async Task<IActionResult> Update(int key, [FromBody] CompanyUpsertRequest request)
    {
        var name = (request.Name ?? "").Trim();
        var cat = (request.Category ?? "").Trim();
        if (name.Length == 0 || cat.Length == 0)
            return BadRequest(new { error = "Company name and category are required." });
        if (name.Length > 250) return BadRequest(new { error = "Company name exceeds 250 characters." });
        if (cat.Length > 100) return BadRequest(new { error = "Category exceeds 100 characters." });

        var n = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_COMPANY_CATEGORY
            SET COMPANY_CATEGORY = @cat, COMPANY_PRODUCT_ID = @prod, COMPANY_BANK_ID = @bank, CREATED_ON = GETDATE()
            WHERE COMPANY_KEY = @key",
            p =>
            {
                p.AddWithValue("@cat", cat);
                p.AddWithValue("@prod", request.ProductId);
                p.AddWithValue("@bank", request.BankId);
                p.AddWithValue("@key", key);
            });
        return n == 0 ? NotFound(new { error = "Company row not found." }) : Ok(new { ok = true });
    }

    /// <summary>Delete one row by COMPANY_KEY.</summary>
    [HttpDelete("{key:int}")]
    public async Task<IActionResult> Delete(int key)
    {
        var n = await _db.ExecuteAsync("DELETE FROM dbo.TBL_CIP_COMPANY_CATEGORY WHERE COMPANY_KEY = @key", p => p.AddWithValue("@key", key));
        return n == 0 ? NotFound(new { error = "Company row not found." }) : Ok(new { ok = true });
    }

    /// <summary>
    /// Bulk upload: xlsx with columns "Company Name" / "Final Category" (first two
    /// non-empty columns are accepted too), tied to the chosen Product + Bank —
    /// exactly like the legacy CIPPortal upload.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] int productId, [FromForm] int bankId)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Choose an .xlsx file to upload." });
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .xlsx files are supported." });
        if (productId <= 0 || bankId <= 0)
            return BadRequest(new { error = "Product and Bank are required." });

        DataTable parsed;
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            parsed = ExcelReader.ReadFirstSheet(ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded xlsx {File}", file.FileName);
            return BadRequest(new { error = "Could not read the workbook. Re-save it as .xlsx and try again." });
        }

        if (parsed.Rows.Count == 0) return BadRequest(new { error = "The sheet has no data rows." });

        // Column detection: look for 'Company Name' / 'Final Category' (legacy headers)
        // in the first row; otherwise fall back to the first two columns as data.
        var r0 = parsed.Rows[0];
        var headers = Enumerable.Range(0, parsed.Columns.Count).Select(i => CellStr(r0, i).Trim()).ToArray();
        int nameIdx = Array.FindIndex(headers, h => h.Equals("company name", StringComparison.OrdinalIgnoreCase) || h.Equals("name", StringComparison.OrdinalIgnoreCase));
        int catIdx = Array.FindIndex(headers, h => h.Equals("final category", StringComparison.OrdinalIgnoreCase) || h.Equals("category", StringComparison.OrdinalIgnoreCase));
        bool hasHeader = nameIdx >= 0 && catIdx >= 0 && nameIdx != catIdx;
        if (!hasHeader) { nameIdx = 0; catIdx = Math.Min(1, parsed.Columns.Count - 1); }

        var rowsToLoad = parsed.Rows.Cast<DataRow>().Skip(hasHeader ? 1 : 0).ToList();

        var staged = new DataTable();
        staged.Columns.Add("COMPANY_NAME", typeof(string));
        staged.Columns.Add("COMPANY_CATEGORY", typeof(string));
        staged.Columns.Add("COMPANY_PRODUCT_ID", typeof(int));
        staged.Columns.Add("COMPANY_BANK_ID", typeof(int));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (var r in rowsToLoad)
        {
            var name = CellStr(r, nameIdx).Trim();
            var cat = CellStr(r, catIdx).Trim();
            if (name.Length == 0 || cat.Length == 0) { skipped++; continue; }
            if (name.Length > 250 || cat.Length > 100) { skipped++; continue; }
            if (!seen.Add(name + "|" + cat)) { skipped++; continue; }
            staged.Rows.Add(name, cat, productId, bankId);
            if (staged.Rows.Count >= MaxUploadRows) break;
        }

        if (staged.Rows.Count == 0)
            return BadRequest(new { error = "No usable rows found. Expected columns: 'Company Name' and 'Final Category'." });

        try
        {
            var (inserted, existing) = await BulkStageAndMergeAsync(staged);
            return Ok(new { ok = true, inserted, duplicatesSkipped = skipped, existingInDb = existing });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Company category upload failed");
            return StatusCode(500, new { error = "Upload failed while saving: " + ex.Message });
        }
    }

    /// <summary>Stage rows in tempdb and insert only the ones not already present for this product+bank.</summary>
    private async Task<(int inserted, int existing)> BulkStageAndMergeAsync(DataTable staged)
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        var tmp = "#compupload_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var create = new Microsoft.Data.SqlClient.SqlCommand($@"
                CREATE TABLE {tmp} (
                    COMPANY_NAME VARCHAR(250) NOT NULL,
                    COMPANY_CATEGORY VARCHAR(100) NOT NULL
                )", conn, tx))
                create.ExecuteNonQuery();

            using (var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(conn, Microsoft.Data.SqlClient.SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = tmp,
                BatchSize = 10_000,
                BulkCopyTimeout = 300
            })
            {
                bulk.ColumnMappings.Add("COMPANY_NAME", "COMPANY_NAME");
                bulk.ColumnMappings.Add("COMPANY_CATEGORY", "COMPANY_CATEGORY");
                await bulk.WriteToServerAsync(staged);
            }

            // Pre-compute how many already exist for this product+bank (informational).
            var existing = Convert.ToInt32(new Microsoft.Data.SqlClient.SqlCommand($@"
                SELECT COUNT(1) FROM {tmp} t
                WHERE EXISTS (SELECT 1 FROM dbo.TBL_CIP_COMPANY_CATEGORY c
                              WHERE c.COMPANY_NAME = t.COMPANY_NAME
                                AND c.COMPANY_CATEGORY = t.COMPANY_CATEGORY
                                AND c.COMPANY_PRODUCT_ID = @prod
                                AND c.COMPANY_BANK_ID = @bank)", conn, tx)
            {
                Parameters = { new Microsoft.Data.SqlClient.SqlParameter("@prod", staged.Rows[0]["COMPANY_PRODUCT_ID"]), new Microsoft.Data.SqlClient.SqlParameter("@bank", staged.Rows[0]["COMPANY_BANK_ID"]) }
            }.ExecuteScalarAsync().GetAwaiter().GetResult());

            var inserted = new Microsoft.Data.SqlClient.SqlCommand($@"
                INSERT INTO dbo.TBL_CIP_COMPANY_CATEGORY (COMPANY_NAME, COMPANY_CATEGORY, COMPANY_PRODUCT_ID, COMPANY_BANK_ID, CREATED_ON)
                SELECT t.COMPANY_NAME, t.COMPANY_CATEGORY, @prod, @bank, GETDATE()
                FROM {tmp} t
                WHERE NOT EXISTS (SELECT 1 FROM dbo.TBL_CIP_COMPANY_CATEGORY c
                                  WHERE c.COMPANY_NAME = t.COMPANY_NAME
                                    AND c.COMPANY_CATEGORY = t.COMPANY_CATEGORY
                                    AND c.COMPANY_PRODUCT_ID = @prod
                                    AND c.COMPANY_BANK_ID = @bank);
                SELECT @@ROWCOUNT;", conn, tx)
            {
                Parameters = { new Microsoft.Data.SqlClient.SqlParameter("@prod", staged.Rows[0]["COMPANY_PRODUCT_ID"]), new Microsoft.Data.SqlClient.SqlParameter("@bank", staged.Rows[0]["COMPANY_BANK_ID"]) }
            };

            var n = Convert.ToInt32(await inserted.ExecuteScalarAsync());
            tx.Commit();
            return (n, existing);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            // temp table dies with the session
        }
    }

    private static string CellStr(DataRow r, int i) =>
        i >= 0 && i < r.Table.Columns.Count && r[i] != DBNull.Value ? r[i]?.ToString() ?? "" : "";

    /// <summary>Small helper bundling SQL parameters built conditionally. Clones per Bind — a SqlParameter can't join two collections.</summary>
    private sealed class CombinedParams
    {
        private readonly List<(string name, object value)> _list = new();
        public void Add(string name, object value) => _list.Add((name, value));
        public Action<SqlParameterCollection> Bind() => pc =>
        {
            foreach (var (name, value) in _list) pc.Add(new SqlParameter(name, value));
        };
        public CombinedParams AddOff(int off, int ps) { Add("@off", off); Add("@ps", ps); return this; }
    }
}
