namespace CIPAdmin.Backend.Models;

public class CibilRequest
{
    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string PanCard { get; set; } = "";
    public string Gender { get; set; } = "male";
    public string ReportType { get; set; } = "cibil";
    public string Consent { get; set; } = "Y";
    /// <summary>Optional per-request API token override; falls back to Cibil:ApiToken config.</summary>
    public string? Token { get; set; }
}

public class CibilCheckRequest
{
    public string Pan { get; set; } = "";
    public string ReportType { get; set; } = "cibil";
}

public class CibilReportInfo
{
    public int ReportId { get; set; }
    public string PanNo { get; set; } = "";
    public string? ApplicantName { get; set; }
    public string? Mobile { get; set; }
    public string ReportType { get; set; } = "";
    public string FileName { get; set; } = "";
    public long? FileSize { get; set; }
    public string? PulledByName { get; set; }
    public DateTime PulledDate { get; set; }
    public string Source { get; set; } = "";

    /// <summary>Work location of the puller, snapshotted at pull time so the grid keeps
    /// showing where a report came from even after the employee is transferred.</summary>
    public string? LocationName { get; set; }

    /// <summary>Column shown as "Center Manager" — the employee who pulled the report.
    /// The 'Centre Manager' designation (DESIG_ID 4) and the 'CENTER_HEAD' role
    /// (ROLE_ID 1018) are both held by zero employees, so neither can resolve this.
    /// See docs/CIBIL-REPORTS-SPEC.md section 3.5.</summary>
    public string? CenterManager { get; set; }

    // PULLED_DATE split into separate date and time parts for the grid and the Excel
    // export (which needs a real date cell so Excel sorts chronologically).
    public string PullDate => PulledDate.ToString("yyyy-MM-dd");
    public string PullTime => PulledDate.ToString("HH:mm:ss");
}

/// <summary>One row of the TL tab's "My Reports" list — a pull event made by the caller.</summary>
public class MyPullRow
{
    public int LogId { get; set; }
    public int? ReportId { get; set; }
    public string PanNo { get; set; } = "";
    public string? ApplicantName { get; set; }
    public string ReportType { get; set; } = "";
    public string Source { get; set; } = "";
    public string? LocationName { get; set; }
    public string? CenterManager { get; set; }
    public string? FileName { get; set; }
    public string PullDate { get; set; } = "";
    public string PullTime { get; set; } = "";
}

/// <summary>Admin view of CIBIL portal consumption plus the vendor's remaining credit balance.</summary>
public class PortalUsageInfo
{
    /// <summary>Pulls that actually called the CIBIL vendor (API + REGEN). This is the
    /// number of CIBIL credits this system has consumed, NOT the vendor's balance.</summary>
    public int PortalPulls { get; set; }
    public int MonthPulls { get; set; }
    public int MonthCaches { get; set; }
    public int TotalPullEvents { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public VendorBalanceInfo Vendor { get; set; } = new();
}

public class VendorBalanceInfo
{
    public bool Available { get; set; }
    /// <summary>Remaining vendor credits, when the vendor exposes a balance endpoint.</summary>
    public decimal? Balance { get; set; }
    /// <summary>Why the balance is unavailable (missing config, HTTP error, bad payload).</summary>
    public string? Message { get; set; }
    public string CheckedOn { get; set; } = "";
}

public class CibilCheckResponse
{
    public bool Exists { get; set; }
    public CibilReportInfo? Report { get; set; }
    /// <summary>Message when regeneration was requested but a cached report exists.</summary>
    public string? Message { get; set; }
}

public class CibilGenerateResponse
{
    /// <summary>CACHE = served from the local folder without calling the API; API = freshly pulled; REGEN = admin regeneration.</summary>
    public string Source { get; set; } = "";
    public CibilReportInfo Report { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Monthly pull-quota snapshot (null on some error paths).</summary>
    public QuotaInfo? Quota { get; set; }
}

/// <summary>User's monthly CIBIL pull quota snapshot.</summary>
public class QuotaInfo
{
    public int Used { get; set; }
    public int Limit { get; set; }
    /// <summary>Remaining pulls against the effective (base or admin-approved) limit.</summary>
    public int Remaining => Math.Max(0, EffectiveLimit - Used);
    /// <summary>Effective cap: base limit or admin-approved limit for the month, whichever is larger.</summary>
    public int EffectiveLimit { get; set; }
    public string? ApprovedLimit { get; set; }
    /// <summary>True when the caller has used up their allowance and a new pull would be
    /// rejected with 403. Admins are never blocked (they can always regenerate).
    /// The UI uses this to disable the request form up front instead of letting the
    /// user fill it in only to be refused.</summary>
    public bool Blocked { get; set; }
}

/// <summary>Body for POST /api/cibil/limit-request.</summary>
public class LimitRequestInput
{
    public int RequestedLimit { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A user request to raise their monthly CIBIL pull limit.</summary>
public class LimitRequestInfo
{
    public int RequestId { get; set; }
    public int EmpCode { get; set; }
    public string EmpName { get; set; } = "";
    public string RequestMonth { get; set; } = "";
    public int CurrentCount { get; set; }
    public int RequestedLimit { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "Pending";
    public int? ApprovedLimit { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedDate { get; set; }
    public DateTime CreatedDate { get; set; }
}
