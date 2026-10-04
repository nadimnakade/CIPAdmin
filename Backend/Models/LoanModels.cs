namespace CIPAdmin.Backend.Models;

/// <summary>
/// Loan eligibility input, mirroring the legacy LoanEligibility.Header model that
/// CIPPortal posted to LoanEligiilityController.LoanEligibility. Field names keep
/// the legacy EH_* semantics so the stored procedure call stays a 1:1 port.
/// </summary>
public class EligibilityRequest
{
    /// <summary>EH_NAME — labelled "FOIR" on the legacy form. The procedure casts it to int and multiplies by 1000.</summary>
    public string Name { get; set; } = "";

    /// <summary>EH_NET_GROSS. The procedure overwrites this with @NET, so gross only drives client-side validation.</summary>
    public decimal Gross { get; set; }

    /// <summary>EH_NET_SALARY. This is the value the procedure actually uses as the salary base.</summary>
    public decimal Net { get; set; }

    /// <summary>EH_COMPANY_NAME_KEY — a COMPANY_KEY from TBL_CIP_COMPANY_CATEGORY, or -1 for "company not listed".</summary>
    public int CompanyKey { get; set; } = -1;

    /// <summary>EH_ROI. When > 0 the procedure also returns a principal range instead of a flat slab amount.</summary>
    public decimal Roi { get; set; }

    /// <summary>EH_TENURE in months.</summary>
    public int Tenure { get; set; } = 60;

    /// <summary>EH_CATEGORY — an EG_CAT value, or ""/-1/0 for "all categories" (which the procedure maps to CAT D).</summary>
    public string Category { get; set; } = "0";

    /// <summary>EH_OBLIGATION — the summed monthly obligation of existing loans.</summary>
    public decimal Obligation { get; set; }
}

/// <summary>One row of USP_CIP_LOAN_ELIGIBILITY output.</summary>
public class EligibilityRow
{
    public string Bank { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>
    /// With ROI 0 this is the flat slab amount; with ROI &gt; 0 the procedure rewrites
    /// it to a range string ("Minimum 100000-400000") or a listing advisory.
    /// </summary>
    public string Amt { get; set; } = "";
    /// <summary>The slab percentage the amount was derived from.</summary>
    public string Slab { get; set; } = "";
}

/// <summary>An existing-loan obligation line as entered on the eligibility form.</summary>
public class ObligationInput
{
    public string Type { get; set; } = "";
    public decimal Amount { get; set; }
}