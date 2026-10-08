using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;

namespace CIPAdmin.Backend.Services;

/// <summary>
/// Writes application and database errors to dbo.TBL_CIP_API_ERROR_LOG so that an
/// admin can read them on the Error Log screen instead of digging through IIS logs.
///
/// THE ONE RULE: nothing in here may throw into the request path. This code runs
/// while we are already handling an exception, and the most common reason it runs
/// at all is that the DATABASE IS BROKEN. An INSERT that throws would turn a clean
/// 500-with-a-message into an unhandled crash with no response body, which is the
/// exact failure mode this whole feature exists to fix. So every path here is
/// wrapped, and the write is best-effort by design.
/// </summary>
public interface IErrorLogService
{
    /// <summary>Record an exception against the current HTTP request. Never throws.</summary>
    Task LogAsync(Exception ex, HttpContext? ctx = null, int? statusCode = null, string? source = null);

    /// <summary>Record an error with no exception object (a caught failure summarised
    /// by the caller). Never throws.</summary>
    Task LogAsync(string message, HttpContext? ctx = null, int? statusCode = null,
                  string source = "Application", string? exceptionType = null);

    /// <summary>True when the log table exists, so the UI can say "run ERROR-LOG.sql"
    /// instead of showing an empty table that looks like "no errors".</summary>
    bool TableExists();
}

public class ErrorLogService : IErrorLogService
{
    private readonly IDbContext _db;
    private readonly ILogger<ErrorLogService> _logger;

    /// <summary>
    /// Cached table-existence probe. Error logging must never re-query the catalogue
    /// on every failure, and the answer cannot change while the process is up in any
    /// way that matters — an admin either ran ERROR-LOG.sql or did not.
    /// </summary>
    private static int _tableExists = -1;

    public ErrorLogService(IDbContext db, ILogger<ErrorLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool TableExists()
    {
        if (_tableExists >= 0) return _tableExists == 1;
        try
        {
            var v = _db.Scalar("SELECT OBJECT_ID('dbo.TBL_CIP_API_ERROR_LOG', 'U')");
            _tableExists = v != null && v != DBNull.Value && Convert.ToInt32(v) > 0 ? 1 : 0;
        }
        catch
        {
            // The catalogue query itself failed, so the database is in trouble. Report
            // "missing" rather than guessing: the UI then tells the operator to run the
            // script, which is more useful than pretending logging is working.
            _tableExists = 0;
        }
        return _tableExists == 1;
    }

    public async Task LogAsync(Exception ex, HttpContext? ctx = null, int? statusCode = null, string? source = null)
    {
        await WriteAsync(new ErrorRecord
        {
            HttpMethod = ctx?.Request.Method,
            RequestPath = Trunc(ctx?.Request.Path.Value, 400),
            QueryString = Trunc(ctx?.Request.QueryString.Value, 1000),
            Source = source ?? Classify(ex),
            ExceptionType = Trunc(ex.GetType().FullName, 300),
            ErrorMessage = Trunc(ex.Message, 2000),
            SqlErrorNumber = (ex as SqlException)?.Number,
            SqlErrorState = (ex as SqlException)?.State,
            SqlErrorClass = (ex as SqlException)?.Class,
            DbObject = Trunc(ExtractDbObject(ex.Message), 300),
            StackTrace = ex.ToString(), // nvarchar(max), not truncated
            EmpCode = EmpCode(ctx),
            EmpName = Trunc(ctx?.User.FindFirst("empName")?.Value, 200),
            Username = Trunc(ctx?.User.FindFirst(ClaimTypes.Name)?.Value
                             ?? ctx?.User.FindFirst("username")?.Value, 200),
            RoleId = RoleId(ctx),
            IpAddress = Trunc(ctx?.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Trunc(ctx?.Request.Headers["User-Agent"], 400),
            StatusCode = statusCode ?? 500,
        });
    }

    public async Task LogAsync(string message, HttpContext? ctx = null, int? statusCode = null,
                              string source = "Application", string? exceptionType = null)
    {
        await WriteAsync(new ErrorRecord
        {
            HttpMethod = ctx?.Request.Method,
            RequestPath = Trunc(ctx?.Request.Path.Value, 400),
            QueryString = Trunc(ctx?.Request.QueryString.Value, 1000),
            Source = source,
            ExceptionType = Trunc(exceptionType, 300),
            ErrorMessage = Trunc(message, 2000),
            DbObject = Trunc(ExtractDbObject(message), 300),
            EmpCode = EmpCode(ctx),
            EmpName = Trunc(ctx?.User.FindFirst("empName")?.Value, 200),
            Username = Trunc(ctx?.User.FindFirst(ClaimTypes.Name)?.Value
                             ?? ctx?.User.FindFirst("username")?.Value, 200),
            RoleId = RoleId(ctx),
            IpAddress = Trunc(ctx?.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Trunc(ctx?.Request.Headers["User-Agent"], 400),
            StatusCode = statusCode ?? 500,
        });
    }

    private async Task WriteAsync(ErrorRecord rec)
    {
        try
        {
            if (!TableExists())
            {
                // Fall back to the normal logger so the error is not lost entirely —
                // just not visible on the screen until the script is run.
                _logger.LogError(
                    "Error logged but TBL_CIP_API_ERROR_LOG is missing, so it was NOT recorded. " +
                    "Run Backend/sql/ERROR-LOG.sql on this server. {Method} {Path}: {Message}",
                    rec.HttpMethod, rec.RequestPath, rec.ErrorMessage);
                return;
            }

            await _db.ExecuteAsync(@"
                INSERT INTO dbo.TBL_CIP_API_ERROR_LOG
                    (HTTP_METHOD, REQUEST_PATH, QUERY_STRING, SOURCE, EXCEPTION_TYPE, ERROR_MESSAGE,
                     SQL_ERROR_NUMBER, SQL_ERROR_STATE, SQL_ERROR_CLASS, DB_OBJECT, STACK_TRACE,
                     EMP_CODE, EMP_NAME, USERNAME, ROLE_ID, IP_ADDRESS, USER_AGENT, STATUS_CODE)
                VALUES
                    (@m, @path, @qs, @src, @et, @msg,
                     @num, @st, @cls, @obj, @stack,
                     @emp, @empName, @user, @role, @ip, @ua, @status);",
                p =>
                {
                    p.AddWithValue("@m", (object)rec.HttpMethod ?? DBNull.Value);
                    p.AddWithValue("@path", (object)rec.RequestPath ?? DBNull.Value);
                    p.AddWithValue("@qs", (object)rec.QueryString ?? DBNull.Value);
                    p.AddWithValue("@src", rec.Source);
                    p.AddWithValue("@et", (object)rec.ExceptionType ?? DBNull.Value);
                    p.AddWithValue("@msg", (object)rec.ErrorMessage ?? DBNull.Value);
                    p.AddWithValue("@num", (object)rec.SqlErrorNumber ?? DBNull.Value);
                    p.AddWithValue("@st", (object)rec.SqlErrorState ?? DBNull.Value);
                    p.AddWithValue("@cls", (object)rec.SqlErrorClass ?? DBNull.Value);
                    p.AddWithValue("@obj", (object)rec.DbObject ?? DBNull.Value);
                    p.AddWithValue("@stack", (object)rec.StackTrace ?? DBNull.Value);
                    p.AddWithValue("@emp", (object)rec.EmpCode ?? DBNull.Value);
                    p.AddWithValue("@empName", (object)rec.EmpName ?? DBNull.Value);
                    p.AddWithValue("@user", (object)rec.Username ?? DBNull.Value);
                    p.AddWithValue("@role", (object)rec.RoleId ?? DBNull.Value);
                    p.AddWithValue("@ip", (object)rec.IpAddress ?? DBNull.Value);
                    p.AddWithValue("@ua", (object)rec.UserAgent ?? DBNull.Value);
                    p.AddWithValue("@status", (object)rec.StatusCode ?? DBNull.Value);
                });
        }
        catch (Exception writeEx)
        {
            // Deliberately swallowed: see the class comment. The user's error is still
            // logged through ILogger even when the database refuses the INSERT.
            _logger.LogError(writeEx,
                "Could not write to TBL_CIP_API_ERROR_LOG. Original error on {Method} {Path}: {Message}",
                rec.HttpMethod, rec.RequestPath, rec.ErrorMessage);
        }
    }

    /// <summary>"Database" for anything SQL-shaped, "Application" otherwise. Lets the
    /// screen answer "is the database unhappy?" with one filter.</summary>
    private static string Classify(Exception ex) => ex switch
    {
        SqlException => "Database",
        TimeoutException => "Database",
        InvalidOperationException when ex.InnerException is SqlException => "Database",
        _ => "Application",
    };

    /// <summary>
    /// Pulls the offending object name out of the message so admins can search for it.
    /// SQL Server words it several ways, so all of the common shapes are matched:
    ///   "Invalid object name 'dbo.X'."            (208)
    ///   "Could not find stored procedure 'X'."    (2812)
    ///   "Cannot find data type 'X'."              (2715)
    ///   "The column 'X' does not belong to table 'Y'." (207)
    /// Best-effort: returns null when nothing matches, which is fine.
    /// </summary>
    internal static string? ExtractDbObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] markers =
        {
            "Invalid object name", "Could not find stored procedure",
            "Cannot find data type", "does not belong to table",
            "Permission denied", "invalid object name",
        };

        foreach (var marker in markers)
        {
            int i = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;

            // Take the first quoted token after the marker.
            int q = text.IndexOf('\'', i);
            if (q < 0) continue;
            int q2 = text.IndexOf('\'', q + 1);
            if (q2 <= q) continue;

            var candidate = text.Substring(q + 1, q2 - q - 1).Trim();
            if (candidate.Length is > 0 and <= 300) return candidate;
        }
        return null;
    }

    private static int? EmpCode(HttpContext? ctx)
        => int.TryParse(ctx?.User.FindFirst("empCode")?.Value, out var v) ? v : null;

    private static int? RoleId(HttpContext? ctx)
        => int.TryParse(ctx?.User.FindFirst(ClaimTypes.Role)?.Value, out var v) ? v : null;

    private static string? Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max];
    }

    private sealed class ErrorRecord
    {
        public string? HttpMethod, RequestPath, QueryString, Source, ExceptionType, ErrorMessage;
        public string? DbObject, StackTrace, EmpName, Username, IpAddress, UserAgent;
        public int? SqlErrorNumber, SqlErrorState, SqlErrorClass, EmpCode, RoleId, StatusCode;
    }
}