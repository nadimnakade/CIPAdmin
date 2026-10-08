using System.Data;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

/// <summary>
/// Admin-only read/triage access to dbo.TBL_CIP_API_ERROR_LOG.
///
/// EVERY action requires the "Admin" policy (roles 1 and 2). That is not decoration:
/// the rows carry stack traces, SQL messages and client IP addresses, which is
/// operational and personal data. A non-admin calling this gets 401/403 from the
/// policy before any query runs, not an empty result set.
/// </summary>
[ApiController]
[Route("api/error-logs")]
[Authorize(Policy = "Admin")]
public class ErrorLogsController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IErrorLogService _errorLog;

    public ErrorLogsController(IDbContext db, IErrorLogService errorLog)
    {
        _db = db;
        _errorLog = errorLog;
    }

    private int EmpCode => int.TryParse(User.FindFirst("empCode")?.Value, out var v) ? v : 0;

    /// <summary>
    /// One page of errors, newest first, with the filters the screen offers.
    ///
    /// Paging uses OFFSET/FETCH with a total from COUNT(*) OVER() so the page count and
    /// the rows come from a single round trip and cannot disagree - which they do when
    /// two separate queries race with new errors arriving.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? q = null,
        [FromQuery] string? source = null,
        [FromQuery] bool? resolved = null)
    {
        // Clamped server-side: pageSize is what drives OFFSET and an unbounded value
        // would let one request ask for the whole table.
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var where = new StringBuilder(" WHERE 1=1");
        var filters = new List<Action<Microsoft.Data.SqlClient.SqlParameterCollection>>();

        if (!string.IsNullOrWhiteSpace(q))
        {
            where.Append(" AND ("
                       + "REQUEST_PATH LIKE @q OR ERROR_MESSAGE LIKE @q OR DB_OBJECT LIKE @q"
                       + " OR USERNAME LIKE @q OR EMP_NAME LIKE @q OR EXCEPTION_TYPE LIKE @q"
                       + " OR CAST(SQL_ERROR_NUMBER AS varchar(20)) LIKE @q)");
            var pattern = "%" + q.Trim() + "%";
            filters.Add(p => p.AddWithValue("@q", pattern));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            where.Append(" AND SOURCE = @src");
            var src = source.Trim();
            filters.Add(p => p.AddWithValue("@src", src));
        }

        if (resolved.HasValue)
        {
            where.Append(" AND IS_RESOLVED = @res");
            filters.Add(p => p.AddWithValue("@res", resolved.Value));
        }

        // Unresolved first, then newest. An open error is the one being worked on, so
        // it must not sit below a page of already-resolved history.
        var sql = $@"
            SELECT * FROM (
                SELECT
                    ERROR_ID, ERROR_ON, HTTP_METHOD, REQUEST_PATH, QUERY_STRING, SOURCE,
                    EXCEPTION_TYPE, ERROR_MESSAGE, SQL_ERROR_NUMBER, DB_OBJECT, EMP_CODE,
                    EMP_NAME, USERNAME, ROLE_ID, IP_ADDRESS, STATUS_CODE, IS_RESOLVED,
                    RESOLVED_BY, RESOLVED_ON, RESOLUTION_NOTE,
                    COUNT(*) OVER () AS TotalCount
                FROM dbo.TBL_CIP_API_ERROR_LOG
                {where}
            ) t
            ORDER BY IS_RESOLVED ASC, ERROR_ON DESC, ERROR_ID DESC
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";

        var offset = (page - 1) * pageSize;

        DataTable dt;
        try
        {
            dt = await _db.QueryAsync(sql, p =>
            {
                foreach (var f in filters) f(p);
                p.AddWithValue("@offset", offset);
                p.AddWithValue("@take", pageSize);
            });
        }
        catch (Exception ex)
        {
            // Almost always "ERROR-LOG.sql has not been run on this server". Say that
            // instead of surfacing an empty table that looks like "no errors recorded",
            // which is the more dangerous misreading.
            return StatusCode(500, new
            {
                error = _errorLog.TableExists()
                    ? "Could not read the error log."
                    : "The error log table is missing on this server. Run Backend/sql/ERROR-LOG.sql.",
                detail = ex.Message,
            });
        }

        var rows = dt.AsEnumerable().Select(r => new
        {
            id = Convert.ToInt64(r["ERROR_ID"]),
            errorOn = r["ERROR_ON"],
            httpMethod = Str(r["HTTP_METHOD"]),
            requestPath = Str(r["REQUEST_PATH"]),
            queryString = Str(r["QUERY_STRING"]),
            source = Str(r["SOURCE"]),
            exceptionType = Str(r["EXCEPTION_TYPE"]),
            message = Str(r["ERROR_MESSAGE"]),
            sqlErrorNumber = IntOrNull(r, "SQL_ERROR_NUMBER"),
            dbObject = Str(r["DB_OBJECT"]),
            // STACK_TRACE is deliberately NOT selected here and must not be read below.
            // A trace is kilobytes; shipping one per row would bloat every page load for
            // data the screen only ever shows one row at a time. The detail endpoint
            // (GET /api/error-logs/{id}, SELECT *) returns it on demand.
            empCode = IntOrNull(r, "EMP_CODE"),
            empName = Str(r["EMP_NAME"]),
            username = Str(r["USERNAME"]),
            ipAddress = Str(r["IP_ADDRESS"]),
            statusCode = IntOrNull(r, "STATUS_CODE"),
            isResolved = !r["IS_RESOLVED"].Equals(DBNull.Value) && Convert.ToBoolean(r["IS_RESOLVED"]),
            resolutionNote = Str(r["RESOLUTION_NOTE"]),
        }).ToList();

        var total = dt.Rows.Count > 0 ? Convert.ToInt32(dt.Rows[0]["TotalCount"]) : 0;
        return Ok(new { total, page, pageSize, logTablePresent = _errorLog.TableExists(), rows });
    }

    /// <summary>
    /// The full stack trace for one row. Split out of the list endpoint on purpose:
    /// a stack trace is large, and including one per row would bloat every page load
    /// for data that is only ever read one row at a time.
    /// </summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<object>> Detail(long id)
    {
        var dt = await _db.QueryAsync(
            "SELECT TOP 1 * FROM dbo.TBL_CIP_API_ERROR_LOG WHERE ERROR_ID = @id",
            p => p.AddWithValue("@id", id));

        if (dt.Rows.Count == 0) return NotFound(new { error = "That error is not in the log." });

        var r = dt.Rows[0];
        return Ok(new
        {
            id = Convert.ToInt64(r["ERROR_ID"]),
            errorOn = r["ERROR_ON"],
            httpMethod = Str(r["HTTP_METHOD"]),
            requestPath = Str(r["REQUEST_PATH"]),
            queryString = Str(r["QUERY_STRING"]),
            source = Str(r["SOURCE"]),
            exceptionType = Str(r["EXCEPTION_TYPE"]),
            message = Str(r["ERROR_MESSAGE"]),
            sqlErrorNumber = IntOrNull(r, "SQL_ERROR_NUMBER"),
            sqlErrorState = IntOrNull(r, "SQL_ERROR_STATE"),
            sqlErrorClass = IntOrNull(r, "SQL_ERROR_CLASS"),
            dbObject = Str(r["DB_OBJECT"]),
            stackTrace = Str(r["STACK_TRACE"]),
            empCode = IntOrNull(r, "EMP_CODE"),
            empName = Str(r["EMP_NAME"]),
            username = Str(r["USERNAME"]),
            roleId = IntOrNull(r, "ROLE_ID"),
            ipAddress = Str(r["IP_ADDRESS"]),
            userAgent = Str(r["USER_AGENT"]),
            statusCode = IntOrNull(r, "STATUS_CODE"),
            isResolved = !r["IS_RESOLVED"].Equals(DBNull.Value) && Convert.ToBoolean(r["IS_RESOLVED"]),
            resolutionNote = Str(r["RESOLUTION_NOTE"]),
        });
    }

    /// <summary>Mark one error resolved/unresolved with an optional note, so the same
    /// failure is not re-investigated by the next person who sees it.</summary>
    [HttpPost("{id:long}/resolve")]
    public async Task<ActionResult<object>> Resolve(long id, [FromBody] ResolveInput input)
    {
        if (input.Resolved && string.IsNullOrWhiteSpace(input.Note))
            return BadRequest(new { error = "Add a short note describing the fix before marking an error resolved." });

        var affected = await _db.ExecuteAsync(@"
            UPDATE dbo.TBL_CIP_API_ERROR_LOG
               SET IS_RESOLVED = @res,
                   RESOLVED_BY = CASE WHEN @res = 1 THEN @by ELSE NULL END,
                   RESOLVED_ON = CASE WHEN @res = 1 THEN GETDATE() ELSE NULL END,
                   RESOLUTION_NOTE = @note
             WHERE ERROR_ID = @id;",
            p =>
            {
                p.AddWithValue("@id", id);
                p.AddWithValue("@res", input.Resolved);
                p.AddWithValue("@by", EmpCode);
                p.AddWithValue("@note", (object?)input.Note?.Trim() ?? DBNull.Value);
            });

        if (affected == 0) return NotFound(new { error = "That error is not in the log." });
        return Ok(new { id, isResolved = input.Resolved });
    }

    /// <summary>Delete entries older than a cut-off. An error log that is never pruned
    /// grows without bound, and it holds stack traces and IP addresses. Defaults to 30
    /// days so it cannot empty the table by accident.</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<object>> Purge([FromBody] PurgeInput input)
    {
        var days = Math.Clamp(input.OlderThanDays, 1, 3650);

        var deleted = await _db.ExecuteAsync(
            "DELETE FROM dbo.TBL_CIP_API_ERROR_LOG WHERE ERROR_ON < DATEADD(day, @neg, SYSUTCDATETIME())",
            p => p.AddWithValue("@neg", -days));

        return Ok(new { deleted, olderThanDays = days });
    }

    private static string Str(object? v) => v is DBNull or null ? "" : v!.ToString() ?? "";

    /// <summary>Nullable int column -> null when SQL NULL. Typed explicitly so the
    /// compiler picks int? rather than failing to infer a common type for the ternary.</summary>
    private static int? IntOrNull(DataRow r, string column)
    {
        var v = r[column];
        return v is DBNull or null ? (int?)null : Convert.ToInt32(v);
    }
}

public class ResolveInput
{
    public bool Resolved { get; set; }
    public string? Note { get; set; }
}

public class PurgeInput
{
    public int OlderThanDays { get; set; } = 30;
}