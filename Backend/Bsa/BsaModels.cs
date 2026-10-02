using System.Text.Json.Serialization;

namespace CIPAdmin.Backend.Bsa;

/// <summary>One parsed bank-statement transaction (output of the parser).</summary>
public class BsaTransaction
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public string Raw { get; set; } = "";
    public string? SourceType { get; set; }
    public string? SourceSheet { get; set; }
    public int? SourceRow { get; set; }
    public int? SourcePage { get; set; }
    public string? SourceExcerpt { get; set; }
    internal bool PendingBalance { get; set; }
    internal bool IsBalanceOnly { get; set; }
}

/// <summary>Metadata extracted from a transaction description (UTR, NACH refs, EMI cycle, ...).</summary>
public abstract class BsaMeta
{
    public string? ReferenceId { get; set; }
    public string? Utr { get; set; }
    public string? Rrn { get; set; }
    public string? NachRef { get; set; }
    public string? NachUmrn { get; set; }
    public string? MandateId { get; set; }
    public string? TxnId { get; set; }
    public string? ChequeNo { get; set; }
    public string? LoanAccountMasked { get; set; }
    public string? LenderRaw { get; set; }
    public string? EmiCycle { get; set; }
    public string? EmiDay { get; set; }
    public decimal? PrincipalAmount { get; set; }
    public decimal? InterestAmount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSheet { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceRow { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourcePage { get; set; }
}

public class BsaSalaryRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string Employer { get; set; } = "";
    public decimal Amount { get; set; }
    public string Mode { get; set; } = "";
    public string Month { get; set; } = "";
}

public class BsaEmiRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string Bank { get; set; } = "";
    public string BankCategory { get; set; } = "";
    public string LoanTypeCode { get; set; } = "";
    public string LoanTypeLabel { get; set; } = "";
    public string LoanTypeIcon { get; set; } = "";
    public string LoanTypeColor { get; set; } = "";
    public decimal EmiAmount { get; set; }
    public string Month { get; set; } = "";
    public string Mode { get; set; } = "";
    public string LoanAccountNumber { get; set; } = "";
}

public class BsaDisbursementRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string Lender { get; set; } = "";
    public string LenderCategory { get; set; } = "";
    public string LoanTypeCode { get; set; } = "";
    public string LoanTypeLabel { get; set; } = "";
    public string LoanTypeIcon { get; set; } = "";
    public string LoanTypeColor { get; set; } = "";
    public decimal Amount { get; set; }
    public string Mode { get; set; } = "";
    public string LoanAccountNumber { get; set; } = "";
    public decimal SanctionAmount { get; set; }
    public decimal? ProcessingFee { get; set; }
}

public class BsaPfRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";
}

public class BsaGovtSchemeRow
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string SchemeName { get; set; } = "";
    public string Type { get; set; } = "";
}

public class BsaStockMarketRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Platform { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}

public class BsaOtherIncomeRow
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Type { get; set; } = "other";
}

public class BsaBounceRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel { get; set; }
}

public class BsaApyRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
}

public class BsaInsuranceRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Provider { get; set; } = "";
    public string PolicyType { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}

public class BsaSipRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string FundName { get; set; } = "";
    public string Platform { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}

public class BsaAppLoanRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string AppName { get; set; } = "";
    public int MaxApr { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
}

public class BsaRentRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Payee { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string Mode { get; set; } = "";
}

public class BsaCreditCardRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcPlatform { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcChannel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcIssuerBank { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcPayee { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CcLast4 { get; set; }
    public string CcDisplay { get; set; } = "";
    public double? Confidence { get; set; }
}

public class BsaUtilityRow : BsaMeta
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
}

public class BsaOtherDebitRow
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
}

public class BsaLoanSummaryItem
{
    public string Bank { get; set; } = "";
    public string BankCategory { get; set; } = "";
    public string LoanTypeCode { get; set; } = "";
    public string LoanTypeLabel { get; set; } = "";
    public string LoanTypeIcon { get; set; } = "";
    public string LoanTypeColor { get; set; } = "";
    public decimal EmiAmount { get; set; }
    public int Count { get; set; }
    public decimal TotalPaid { get; set; }
}

public class BsaMonthlySummary
{
    public string Month { get; set; } = "";
    public decimal Credits { get; set; }
    public decimal Debits { get; set; }
    public decimal Salary { get; set; }
    public decimal Emi { get; set; }
    public decimal Pf { get; set; }
    public decimal Disbursements { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal TransfersOut { get; set; }
    public decimal TransfersIn { get; set; }
}

public class BsaAccountSummary
{
    public decimal TotalCredits { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal NetBalance { get; set; }
    public int TransactionCount { get; set; }
    public string Period { get; set; } = "";
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
}

public class BsaInsights
{
    public int ObligationToIncomeRatio { get; set; }
    public int SavingsRate { get; set; }
    public decimal AvgMonthlySalary { get; set; }
    public decimal TotalEmiMonthly { get; set; }
    public decimal TotalDisbursed { get; set; }
    public decimal EstimatedTotalDebt { get; set; }
    public List<string> RiskFlags { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public List<string> AdditionalFieldsNeeded { get; set; } = new();
}

public class BsaAnalysisResult
{
    public BsaAccountSummary AccountSummary { get; set; } = new();
    public List<BsaSalaryRow> Salary { get; set; } = new();
    public List<BsaPfRow> PfEpfo { get; set; } = new();
    public List<BsaDisbursementRow> LoanDisbursements { get; set; } = new();
    public List<BsaEmiRow> EmiPayments { get; set; } = new();
    public List<BsaOtherIncomeRow> OtherIncome { get; set; } = new();
    public List<BsaRentRow> Rent { get; set; } = new();
    public List<BsaSipRow> SipInvestments { get; set; } = new();
    public List<BsaInsuranceRow> Insurance { get; set; } = new();
    public List<BsaAppLoanRow> AppLoans { get; set; } = new();
    public List<BsaUtilityRow> Utilities { get; set; } = new();
    public List<BsaCreditCardRow> CreditCardPayments { get; set; } = new();
    public List<BsaBounceRow> BounceCharges { get; set; } = new();
    public List<BsaStockMarketRow> StockMarket { get; set; } = new();
    public List<BsaApyRow> ApyPension { get; set; } = new();
    public List<BsaGovtSchemeRow> GovtSchemeCredits { get; set; } = new();
    public List<BsaOtherDebitRow> OtherDebits { get; set; } = new();
    public List<BsaEmiRow> Loans { get; set; } = new();
    public List<BsaPfRow> PfCredits { get; set; } = new();
    public List<BsaMonthlySummary> MonthlySummary { get; set; } = new();
    public List<BsaLoanSummaryItem> LoanSummary { get; set; } = new();
    public BsaInsights Insights { get; set; } = new();
}

// ── CIBIL report models ─────────────────────────────────────────

public class BsaCibilPersonal
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dob { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Gender { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pan { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mobile { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Company { get; set; }
    public List<string> Addresses { get; set; } = new();
}

public class BsaCibilEnquiryDetail
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }
    public string Member { get; set; } = "";
    public string Purpose { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; set; }
    public string Raw { get; set; } = "";
}

public class BsaCibilAccount
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lender { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountNo { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountStatus { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ownership { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenedDate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClosedDate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastUpdate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Emi { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CurrentBalance { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SanctionedAmount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? HighCredit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CreditLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OverdueAmount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DpdMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AdverseFlags { get; set; }
}

public class BsaCibilReport
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Score { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportDate { get; set; }
    public BsaCibilPersonal Personal { get; set; } = new();
    public Dictionary<string, decimal> Enquiries { get; set; } = new();
    public List<BsaCibilEnquiryDetail> EnquiryDetails { get; set; } = new();
    public List<BsaCibilAccount> Accounts { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DpdMax { get; set; }
    public List<string> AdverseFlags { get; set; } = new();
    public int RawCharCount { get; set; }
}

public class BsaCrossVerifyMatch
{
    public string CibilLender { get; set; } = "";
    public string StatementLender { get; set; } = "";
}

public class BsaCrossVerifyResult
{
    public List<BsaCrossVerifyMatch> Matched { get; set; } = new();
    public List<string> MissingInStatement { get; set; } = new();
    public List<string> MissingInCibil { get; set; } = new();
}

public class BsaEligibility
{
    public string Decision { get; set; } = "Needs Review";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CibilScore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DpdMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AvgMonthlyIncome { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AvgMonthlyObligations { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Foir { get; set; }
    public int BounceCount { get; set; }
    public List<string> Reasons { get; set; } = new();
}

public class BsaProductEligibility
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Eligible { get; set; }
    public int MinScore { get; set; }
}

public class BsaUnderwriting
{
    public string Grade { get; set; } = "C";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CibilScore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DpdMax { get; set; }
    public int EnquiriesRecent { get; set; }
    public int AccountsTotal { get; set; }
    public int AccountsActive { get; set; }
    public int UnsecuredActive { get; set; }
    public int CreditCardsActive { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CreditCardUtilization { get; set; }
    public int BounceCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Foir { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AvgMonthlyIncome { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AvgMonthlyObligations { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxFoirPolicy { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AdditionalEmiCapacity { get; set; }
    public List<string> PolicyFlags { get; set; } = new();
    public List<BsaProductEligibility> ProductEligibility { get; set; } = new();
}
