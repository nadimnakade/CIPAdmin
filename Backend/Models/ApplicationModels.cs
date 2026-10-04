namespace CIPAdmin.Backend.Models;

/// <summary>
/// One row of the application search grid. Column names mirror
/// USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING exactly.
/// </summary>
public class ApplicationSearchRow
{
    public long Id { get; set; }
    public string AppNo { get; set; } = "";
    public string BankName { get; set; } = "";
    public string LoanName { get; set; } = "";
    public string LoanSchemeName { get; set; } = "";
    public string LoanAmnt { get; set; } = "";
    public string PurposeOfLoan { get; set; } = "";
    public string LoginChannelName { get; set; } = "";
    public string LoginRegionName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientMblNo { get; set; } = "";
    public string ClientDob { get; set; } = "";
    public string ClientPan { get; set; } = "";
    public string ClientAadharNo { get; set; } = "";
    public string Gender { get; set; } = "";
    public string MaritatalStatus { get; set; } = "";
    public string DesigName { get; set; } = "";
    public string TelCallerName { get; set; } = "";
    public string SupervisorName { get; set; } = "";
    public string AppStatusName { get; set; } = "";
    public string LosNo { get; set; } = "";
    public string CreditMonth { get; set; } = "";
    public string AppDate { get; set; } = "";
    public string CpvStatus { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string StatusDesc { get; set; } = "";
    public string UpdatedOn { get; set; } = "";
    public string HoldStatus { get; set; } = "";
    public string AddDocReq { get; set; } = "";
    public string ImgRelRemarks { get; set; } = "";
    public string AmntDisbursed { get; set; } = "";
    public string DisbursalDate { get; set; } = "";
}

/// <summary>
/// The single-form payload. Property names are the @-parameter names of
/// USP_CIP_DATA_ENTRY, so binding the body is a mechanical 1:1 mapping. Numeric
/// fields are nullable because the legacy form posts empty strings for untouched
/// dropdowns and the procedure coerces them itself.
/// </summary>
public class ApplicationUpsert
{
    /// <summary>Existing row id for an edit; null/0 for a new application.</summary>
    public long? Id { get; set; }

    public string AppDate { get; set; } = "";
    public string AppNo { get; set; } = "";
    public int? BankId { get; set; }
    public int? LoanId { get; set; }
    public int? LoanSchemeId { get; set; }
    public decimal? LoanAmnt { get; set; }
    public string PurposeOfLoan { get; set; } = "";
    public int? LgChnlId { get; set; }
    public int? LgRgnId { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientMblNo { get; set; } = "";
    public string ClientDob { get; set; } = "";
    public string ClientPan { get; set; } = "";
    public string ClientAadharNo { get; set; } = "";
    public string Gender { get; set; } = "";
    public string MaritatalStatus { get; set; } = "";
    public string SpouseName { get; set; } = "";
    public string SpousOccupation { get; set; } = "";
    public int? NoOfDependents { get; set; }
    public string MotherName { get; set; } = "";
    public int? EduQualId { get; set; }
    public string University { get; set; } = "";
    public string YrOfPassing { get; set; } = "";
    public string PersonalEmailId { get; set; } = "";
    public string CurrAddress { get; set; } = "";
    public string PermanentAddress { get; set; } = "";
    public int? ResidenceStatusId { get; set; }
    public decimal? RentAmt { get; set; }
    public string NoYrsCurrResidence { get; set; } = "";
    public string NoYrsCurrCity { get; set; } = "";
    public int? OccupationId { get; set; }
    public string CompanyName { get; set; } = "";
    public string CompanyAddress { get; set; } = "";
    public string OfficeLandline { get; set; } = "";
    public string OfficialEmailId { get; set; } = "";
    public string DesignationId { get; set; } = "";
    public string NoYrsCurrCompany { get; set; } = "";
    public string TotalWorkExp { get; set; } = "";
    public string SalaryBankId { get; set; } = "";
    public string BankAccNo { get; set; } = "";
    public string SalaryBranch { get; set; } = "";
    public string Ref1Name { get; set; } = "";
    public string Ref1Address { get; set; } = "";
    public string Ref1Contact { get; set; } = "";
    public string Ref2Name { get; set; } = "";
    public string Ref2Address { get; set; } = "";
    public string Ref2Contact { get; set; } = "";
    public decimal? GrossSalary { get; set; }
    public decimal? NetSalary { get; set; }
    public decimal? AnnualTurnover { get; set; }
    public string NetProfit { get; set; } = "";
    public int? ExLoanId { get; set; }
    public int? ExLoanBankId { get; set; }
    public decimal? ExistingLoanEmi { get; set; }
    public decimal? OutstandingLoanAmnt { get; set; }
    public int? AppStatusId { get; set; }
    public string SentToBankDate { get; set; } = "";
    public string Remarks { get; set; } = "";
    public decimal? AmntDisbursed { get; set; }
    public string LoanOfferId { get; set; } = "";
    public string DisbursalDate { get; set; } = "";
    public int? TelecallerId { get; set; }
    public int? SupervisorId { get; set; }
    public int? BranchId { get; set; }
    public decimal? CibilScore { get; set; }
    public string Comments { get; set; } = "";
    public string Tenure { get; set; } = "";
    public int? ExecutiveName { get; set; }
    public string PickUpDate { get; set; } = "";
    public string? LosNo { get; set; }
    public string? CreditMonth { get; set; }
    public decimal? AmtByBank { get; set; }

    /// <summary>Relative path of the scan file saved alongside this application, if any.</summary>
    public string? AttachmentPath { get; set; }
}

/// <summary>One existing-loan line from the CSV bulk upload.</summary>
public class ExistingLoanRow
{
    public string AppNo { get; set; } = "";
    public string ExistingLoan { get; set; } = "";
    public string ExistingLoanBank { get; set; } = "";
    public string ExistingLoanEMI { get; set; } = "";
    public string OutstandingLoanAmt { get; set; } = "";
}

/// <summary>A scan file stored against an application.</summary>
public class ScanFile
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string Modified { get; set; } = "";
    public string Url { get; set; } = "";
}