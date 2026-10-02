using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CIPAdmin.Backend.Services;

namespace CIPAdmin.Backend.Controllers;

public class DialerAgentRow
{
    public string Caller { get; set; } = "";
    public string Location { get; set; } = "";
    public string Tl { get; set; } = "";
    public long Dnd { get; set; }
    public long Lead { get; set; }
    public long FollowUp { get; set; }
    public long Ni { get; set; }
    public long NotDoable { get; set; }
    public long WrongNo { get; set; }
    public long Ringing { get; set; }
    public long Blank { get; set; }
    public long SameDayFollowUp { get; set; }
    public long Auto { get; set; }
    public long Manual { get; set; }
    public long TotalCount { get; set; }
    public string? Duration { get; set; }
    public decimal DisbursedAmt { get; set; }
    public long DisbursedCount { get; set; }
    public long LoginCount { get; set; }
    public string Rank { get; set; } = "";
    public string? LastCalledDate { get; set; }
    public int? EmployeeCode { get; set; }
}

public class LocationSlice
{
    public string Location { get; set; } = "";
    public long Calls { get; set; }
    public int Callers { get; set; }
}

public class HourSlice
{
    public string Hour { get; set; } = "";
    public long Calls { get; set; }
}

public class DialerLiveResponse
{
    public string GeneratedAt { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public long GrandTotal { get; set; }
    public int ActiveCallers { get; set; }
    public decimal TotalDisbursed { get; set; }
    public long TotalDisbursedCount { get; set; }
    public object? Scope { get; set; }
    public List<DialerAgentRow> Agents { get; set; } = new();
    public List<KeyValuePair<string, long>> Dispositions { get; set; } = new();
    public List<LocationSlice> ByLocation { get; set; } = new();
    public List<HourSlice> Hourly { get; set; } = new();
}

/// <summary>
/// Live dialer MIS dashboard — a direct port of the legacy DialerCount module.
/// Calls the same hierarchy-aware SP the legacy portal used
/// (USP_DIALER_LIVE_COUNT_REPORT @UserKey): the SP itself restricts rows to the
/// reporting subtree of the caller (admins/managers see everything they manage),
/// so no additional filtering is applied here.
/// </summary>
[ApiController]
[Route("api/dialer")]
[Authorize]
public class DialerLiveController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IHierarchyService _hierarchy;
    private readonly ILogger<DialerLiveController> _logger;

    public DialerLiveController(IDbContext db, IHierarchyService hierarchy, ILogger<DialerLiveController> logger)
    {
        _db = db; _hierarchy = hierarchy; _logger = logger;
    }

    [HttpGet("live")]
    public async Task<ActionResult<DialerLiveResponse>> Live()
    {
        var empCode = int.Parse(User.FindFirst("empCode")!.Value);
        var roleId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var rr) ? rr : 0;
        var scope = await _hierarchy.GetScopeAsync(empCode, roleId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Legacy CallLogLive() equivalent: hierarchy is resolved inside the SP.
            var ds = await _db.QuerySetAsync(
                "EXEC dbo.USP_DIALER_LIVE_COUNT_REPORT @UserKey = @u",
                p => p.AddWithValue("@u", empCode),
                commandTimeout: 180);

            var res = new DialerLiveResponse
            {
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartDate = DateTime.Today.ToString("yyyy-MM-dd"),
                EndDate = DateTime.Today.ToString("yyyy-MM-dd"),
                Scope = new { isAdmin = scope.IsAdmin, subtreeSize = scope.SubtreeSize },
            };

            var table = ds.Tables.Count > 0 ? ds.Tables[0] : null;
            if (table != null)
            {
                foreach (DataRow r in table.Rows)
                {
                    var row = new DialerAgentRow
                    {
                        Caller = r["Caller"].ToString() ?? "",
                        Location = r["LOCATION"].ToString() ?? "",
                        Tl = r["TL"].ToString() ?? "",
                        Dnd = Lng(r["DND"]),
                        Lead = Lng(r["Lead"]),
                        FollowUp = Lng(r["FollowUp"]),
                        Ni = Lng(r["NI"]),
                        NotDoable = Lng(r["Not Doable"]),
                        WrongNo = Lng(r["WrongNo"]),
                        Ringing = Lng(r["Ringing"]),
                        Blank = Lng(r["Blank"]),
                        SameDayFollowUp = Lng(r["SameDayFollowUp"]),
                        Auto = Lng(r["Auto"]),
                        Manual = Lng(r["Manual"]),
                        TotalCount = Lng(r["TotalCount"]),
                        Duration = NullStr(r["Duration"]),
                        DisbursedAmt = Dec(r["DisbursedAmt"]),
                        DisbursedCount = Lng(r["DisbursedCount"]),
                        LoginCount = Lng(r["LoginCount"]),
                        Rank = r["Rank"].ToString() ?? "",
                        LastCalledDate = NullStr(r["LastCalledDate"]),
                        EmployeeCode = r.Table.Columns.Contains("EmployeeCode") && r["EmployeeCode"] != DBNull.Value
                            ? Convert.ToInt32(r["EmployeeCode"]) : null,
                    };

                    res.Agents.Add(row);

                    res.TotalDisbursed += row.DisbursedAmt;
                    res.TotalDisbursedCount += row.DisbursedCount;
                    if (row.TotalCount > 0 || row.Lead > 0 || row.FollowUp > 0 || row.Dnd > 0 || row.Ni > 0
                        || row.NotDoable > 0 || row.WrongNo > 0 || row.Ringing > 0 || row.Blank > 0
                        || row.SameDayFollowUp > 0 || row.Auto > 0 || row.Manual > 0)
                        res.ActiveCallers++;
                }

                res.Dispositions = new (string Key, Func<DialerAgentRow, long> Pick)[]
                {
                    ("Lead", a => a.Lead),
                    ("FollowUp", a => a.FollowUp),
                    ("SameDay FollowUp", a => a.SameDayFollowUp),
                    ("DND", a => a.Dnd),
                    ("NI", a => a.Ni),
                    ("Not Doable", a => a.NotDoable),
                    ("Wrong No", a => a.WrongNo),
                    ("Ringing", a => a.Ringing),
                    ("Blank", a => a.Blank),
                    ("Auto", a => a.Auto),
                    ("Manual", a => a.Manual),
                }
                .Select(k => new KeyValuePair<string, long>(k.Key, res.Agents.Sum(k.Pick)))
                .Where(k => k.Value > 0)
                .OrderByDescending(k => k.Value)
                .ToList();

                // The SP's TotalCount excludes some dispositions (e.g. Blank),
                // so the headline number is the larger of the two views.
                var dispoSum = res.Dispositions.Sum(k => k.Value);
                res.GrandTotal = Math.Max(res.Agents.Sum(a => a.TotalCount), dispoSum);

                res.ByLocation = res.Agents
                    .GroupBy(a => string.IsNullOrWhiteSpace(a.Location) ? "Unassigned" : a.Location)
                    .Select(g => new LocationSlice
                    {
                        Location = g.Key,
                        Calls = g.Sum(x => x.Lead + x.FollowUp + x.SameDayFollowUp + x.Dnd + x.Ni + x.NotDoable
                                          + x.WrongNo + x.Ringing + x.Blank + x.Auto + x.Manual + x.TotalCount),
                        Callers = g.Count()
                    })
                    .OrderByDescending(x => x.Calls)
                    .ToList();
            }

            // Hourly view — only meaningful for admins: the hourly SP has no user
            // parameter, so for scoped users it would leak other teams' volumes.
            if (scope.IsAdmin)
            {
                try
                {
                    var dsH = await _db.QuerySetAsync(
                        "EXEC dbo.USP_DIALER_LIVE_COUNT_REPORT_MIS_FILTER_BY_HOUR @StartDate = @s, @EndDate = @e",
                        p =>
                        {
                            p.AddWithValue("@s", DateTime.Today);
                            p.AddWithValue("@e", DateTime.Today);
                        }, commandTimeout: 120);

                    var tH = dsH.Tables.Count > 0 ? dsH.Tables[0] : null;
                    if (tH != null && tH.Rows.Count > 0 && tH.Columns.Count >= 2)
                    {
                        foreach (DataRow r in tH.Rows)
                        {
                            var label = r[0].ToString() ?? "";
                            long calls = 0;
                            for (int c = 1; c < tH.Columns.Count; c++)
                            {
                                var v = Lng(r[c]);
                                if (v > 0) { calls = v; break; }
                                if (v > calls) calls = v;
                            }
                            res.Hourly.Add(new HourSlice { Hour = label, Calls = calls });
                        }
                    }
                }
                catch (Exception exH)
                {
                    _logger.LogWarning(exH, "Hourly dialer SP failed (non-fatal)");
                }
            }

            sw.Stop();
            _logger.LogInformation("Dialer live (legacy SP, user {User}): {Agents} agents, {Calls} calls in {Ms} ms",
                empCode, res.Agents.Count, res.GrandTotal, sw.ElapsedMilliseconds);
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dialer live report failed for user {User}", empCode);
            return StatusCode(500, new { error = "Dialer live report failed: " + ex.Message });
        }
    }

    private static long Lng(object o) => o == DBNull.Value ? 0 : long.TryParse(o.ToString(), out var v) ? v : 0;
    private static decimal Dec(object o) => o == DBNull.Value ? 0 : decimal.TryParse(o.ToString(), out var v) ? v : 0;
    private static string? NullStr(object o) => o == DBNull.Value ? null : o.ToString();
}
