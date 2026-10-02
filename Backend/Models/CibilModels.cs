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
