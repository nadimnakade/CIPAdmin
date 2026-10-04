using System.Data;
using CIPAdmin.Backend.Models;
using CIPAdmin.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace CIPAdmin.Backend.Controllers;

/// <summary>
/// The Application section — search, single data-entry form, scan upload, bulk insert
/// and existing-loan details. A direct port of CIPPortal's CIP_SearchAndEditController,
/// DataEntry_CIPController, DataEntry_FileUpload_Controller, DataEntry_Bulk_CIPController
/// and CIP_ExistingLoanDetController.
///
/// Two deliberate departures from the legacy shape, neither of which changes stored data:
/// the ~14 dropdown round trips the old form made on every load are served by one
/// <c>GET api/applications/dropdowns</c>, and validation that the legacy code performed
/// (or commented out) is enforced server-side before a write.
/// </summary>
[ApiController]
[Route("api/applications")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private readonly IDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ApplicationsController> _logger;

    /// <summary>Hard ceiling on a bulk upload, so one request cannot exhaust the web process.</summary>
    private const int MaxBulkRows = 5_000;

    public ApplicationsController(
        IDbContext db,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ApplicationsController> logger)
    {
        _db = db;
        _config = config;
        _env = env;
        _logger = logger;
    }

    private int EmpCode => int.Parse(User.FindFirst("empCode")!.Value);
    private string EmpName => User.FindFirst("empName")?.Value ?? "";

    /// <summary>
    /// Every dropdown the single form needs, in one response. The action codes are the
    /// legacy <c>USP_CIP_FILL_DROPDOWN</c> flags.
    /// </summary>
    [HttpGet("dropdowns")]
    public async Task<ActionResult<object>> Dropdowns()
    {
        var tasks = new (string Key, string Flag)[]
        {
            ("banks", "FBank"),
            ("loanTypes", "FLoanType"),
            ("loanSchemes", "FLoanScheme"),
            ("loginChannels", "FLoginCh"),
            ("loginRegions", "FLoginRegion"),
            ("applicationStatuses", "FApplStatus"),
            ("residenceStatuses", "FResStatus"),
            ("educationQualifications", "FEduQual"),
            ("occupations", "FOccupation"),
            ("locations", "FLocation"),
            ("executives", "FExecutiveName"),
            ("designations", "FDesignation"),
            ("bankBranches", "FBankBranch"),
        };

        var results = await Task.WhenAll(tasks.Select(async t =>
            (t.Key, Options: await DropdownAsync(t.Flag))));

        // Supervisors and telecallers are branch-scoped and loaded on demand.
        var payload = results.ToDictionary(r => r.Key, r => r.Options);
        payload["genders"] = new List<Option> { new(0, ""), new(1, "Male"), new(2, "Female") };
        payload["maritalStatuses"] = new List<Option> { new(0, ""), new(1, "Single"), new(2, "Married"), new(3, "Widow") };
        return Ok(payload);
    }

    private async Task<List<Option>> DropdownAsync(string flag)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_DROPDOWN @Action=@a, @PARAM=0, @PARAMNEW=''",
            p => p.AddWithValue("@a", flag));
        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
            list.Add(new Option(ToInt(r[0]), r[1]?.ToString() ?? ""));
        return list;
    }

    [HttpPost("supervisors")]
    public async Task<ActionResult<object>> Supervisors([FromBody] IdRequest body)
        => Ok(await BranchDropdownAsync("FSupervisor", body.Id, null));

    /// <summary>
    /// Telecallers for a branch/supervisor. The legacy DataEntry_CIPController resolved these
    /// through GetFillDropDownTelecaller, which calls USP_CIP_DATA_ENTRY with
    /// @Action='FTelecaller' and @SUPERVISOR_ID / @BRANCH_ID — the FTelecaller branch of
    /// USP_CIP_FILL_DROPDOWN is a different query and is not what the old form used.
    /// </summary>
    [HttpPost("telecallers")]
    public async Task<ActionResult<object>> Telecallers([FromBody] SupervisorRequest body)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_DATA_ENTRY @Action='FTelecaller', @SUPERVISOR_ID=@s, @BRANCH_ID=@b",
            p =>
            {
                p.AddWithValue("@s", body.SupervisorId ?? -1);
                p.AddWithValue("@b", body.BranchId);
            });

        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
        {
            // Columns are EMP_CODE, EMP_NAME with no aliases; the leading row is the
            // procedure's own placeholder, which the page renders itself.
            if (!int.TryParse(r[0]?.ToString(), out var code) || code == 0) continue;
            var name = r[1]?.ToString()?.Trim() ?? "";
            if (name.StartsWith("---", StringComparison.Ordinal) || name.Length == 0) continue;
            list.Add(new Option(code, name));
        }
        return Ok(list);
    }

    private async Task<List<Option>> BranchDropdownAsync(string flag, int branchId, int? supervisorId)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_FILL_DROPDOWN @Action=@a, @PARAM=@branch, @PARAMNEW=@sup",
            p =>
            {
                p.AddWithValue("@a", flag);
                p.AddWithValue("@branch", branchId);
                p.AddWithValue("@sup", supervisorId.HasValue
                    ? (object)supervisorId.Value.ToString()
                    : "");
            });
        var list = new List<Option>();
        foreach (DataRow r in dt.Rows)
        {
            var id = ToInt(r[0]);
            var name = r[1]?.ToString() ?? "";
            if (id <= 0 && (name.Length == 0 || name.StartsWith("---", StringComparison.Ordinal))) continue;
            list.Add(new Option(id, name));
        }
        return list;
    }

    /// <summary>
    /// Paged application search. <paramref name="sortCol"/> is the legacy integer
    /// sort selector; 0 keeps the procedure's own ordering.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<object>> Search(
        [FromQuery] string? search = null,
        [FromQuery] string? loginRegion = null,
        [FromQuery] string? bank = null,
        [FromQuery] string? caller = null,
        [FromQuery] string? supervisor = null,
        [FromQuery] string? status = null,
        [FromQuery] string? appDate = null,
        [FromQuery] int sortCol = 0,
        [FromQuery] string? sortDir = "asc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var start = (page - 1) * pageSize;

        var dt = await _db.QueryAsync(@"
            EXEC dbo.USP_CIP_GET_CLIENT_DETAILS_CUSTOM_PAGING
                @DisplayLength=@len, @DisplayStart=@start, @SortCol=@sortCol, @SortDir=@sortDir,
                @SearchText=@search, @UserKey=@userKey,
                @FLoginRegion=@fRegion, @FBank=@fBank, @FCaller=@fCaller,
                @FSupervisor=@fSup, @FStatus=@fStatus, @FAppDate=@fDate",
            p =>
            {
                // The procedure pages with `RowNum >= @FirstRec AND RowNum < @LastRec` while RowNum
                // starts at 1, so asking for N rows returns N-1. Ask for one extra and trim.
                p.AddWithValue("@len", pageSize + 1);
                p.AddWithValue("@start", start);
                p.AddWithValue("@sortCol", sortCol);
                p.AddWithValue("@sortDir", string.IsNullOrWhiteSpace(sortDir) ? "asc" : sortDir);
                p.AddWithValue("@search", (object)(search ?? "").Trim());
                // Must be the signed-in employee: the procedure builds #tmpHierarchy from it
                // and drops it unconditionally, so any other value (0, -1) raises an error.
                p.AddWithValue("@userKey", EmpCode);
                p.AddWithValue("@fRegion", (object)(loginRegion ?? "").Trim());
                p.AddWithValue("@fBank", (object)(bank ?? "").Trim());
                p.AddWithValue("@fCaller", (object)(caller ?? "").Trim());
                p.AddWithValue("@fSup", (object)(supervisor ?? "").Trim());
                p.AddWithValue("@fStatus", (object)(status ?? "").Trim());
                p.AddWithValue("@fDate", (object)(appDate ?? "").Trim());
            });

        var rows = new List<ApplicationSearchRow>();
        foreach (DataRow r in dt.Rows)
        {
            rows.Add(new ApplicationSearchRow
            {
                Id = ToLong(r["id"]),
                AppNo = Str(r["app_no"]),
                BankName = Str(r["bank_name"]),
                LoanName = Str(r["loan_name"]),
                LoanSchemeName = Str(r["loan_scheme_name"]),
                LoanAmnt = Str(r["loan_amnt"]),
                PurposeOfLoan = Str(r["purpose_of_loan"]),
                LoginChannelName = Str(r["login_channel_name"]),
                LoginRegionName = Str(r["login_region_name"]),
                ClientName = Str(r["client_name"]),
                ClientMblNo = Str(r["client_mbl_no"]),
                ClientDob = Str(r["client_dob"]),
                ClientPan = Str(r["client_pan"]),
                ClientAadharNo = Str(r["client_aadhar_no"]),
                Gender = Str(r["gender"]),
                MaritatalStatus = Str(r["mariatal_status"]),
                DesigName = Str(r["desig_name"]),
                TelCallerName = Str(r["tel_caller_name"]),
                SupervisorName = Str(r["supervisor_name"]),
                AppStatusName = Str(r["app_status_name"]),
                LosNo = Str(r["los_no"]),
                CreditMonth = Str(r["creditmonth"]),
                AppDate = Str(r["app_date"]),
                CpvStatus = Str(r["cpv_status"]),
                Remarks = Str(r["remarks"]),
                StatusDesc = Str(r["statusdesc"]),
                UpdatedOn = Str(r["updatedon"]),
                HoldStatus = Str(r["HOLD_STATUS"]),
                AddDocReq = Str(r["add_doc_req"]),
                ImgRelRemarks = Str(r["img_rel_remarks"]),
                AmntDisbursed = Str(r["amnt_disbursed"]),
                DisbursalDate = Str(r["disbursal_date"]),
            });
            if (rows.Count > pageSize) rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        // The procedure carries the unpaged total on every row via COUNT(*) OVER().
        var total = dt.Rows.Count > 0 ? ToInt(dt.Rows[0]["TotalCount"]) : 0;
        return Ok(new { total, page, pageSize, rows });
    }

    /// <summary>Load one application for editing (the legacy "FE" fetch).</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<object>> Get(long id)
    {
        var dt = await _db.QueryAsync(
            "EXEC dbo.USP_CIP_GET_CLIENT_DETAILS @Action='FE', @SearchText=@id, @UserKey=@userKey",
            p =>
            {
                p.AddWithValue("@id", id.ToString());
                p.AddWithValue("@userKey", EmpCode);
            });

        if (dt.Rows.Count == 0) return NotFound(new { error = "Application not found." });

        var r = dt.Rows[0];
        return Ok(new Dictionary<string, object?>
        {
            ["id"] = ColInt(r, "Id") ?? ToLong(Col(r, "Id")),
            ["appNo"] = Col(r, "APP_NO"),
            // APP_DATE is selected twice by the procedure; the first is the formatted one.
            ["appDate"] = Col(r, "APP_DATE"),
            ["bankId"] = ColInt(r, "BANK_ID"),
            ["loanId"] = ColInt(r, "LOAN_ID"),
            ["loanSchemeId"] = ColInt(r, "LOAN_SCHEME_ID"),
            ["loanAmnt"] = Col(r, "LOAN_AMNT"),
            ["purposeOfLoan"] = Col(r, "PURPOSE_OF_LOAN"),
            ["lgChnlId"] = ColInt(r, "LG_CHNL_ID"),
            ["lgRgnId"] = ColInt(r, "LG_RGN_ID"),
            ["clientName"] = Col(r, "CLIENT_NAME"),
            ["clientMblNo"] = Col(r, "CLIENT_MBL_NO"),
            ["clientDob"] = Col(r, "CLIENT_DOB"),
            ["clientPan"] = Col(r, "CLIENT_PAN"),
            ["clientAadharNo"] = Col(r, "CLIENT_AADHAR_NO"),
            ["gender"] = Col(r, "GENDER"),
            ["maritatalStatus"] = Col(r, "MARIATAL_STATUS"),
            ["spouseName"] = Col(r, "SPOUSE_NAME"),
            ["spousOccupation"] = Col(r, "SPOUS_OCCUPATION"),
            ["noOfDependents"] = ColInt(r, "NO_OF_DEPENDENTS"),
            ["motherName"] = Col(r, "MOTHER_NAME"),
            ["eduQualId"] = ColInt(r, "EDU_QUAL_ID"),
            ["university"] = Col(r, "UNIVERSITY"),
            ["yrOfPassing"] = Col(r, "YR_OF_PASSING"),
            ["personalEmailId"] = Col(r, "PERSONAL_EMAIL_ID"),
            ["currAddress"] = Col(r, "CURR_ADDRESS"),
            ["permanentAddress"] = Col(r, "PERMANANT_ADDRESS"),
            ["residenceStatusId"] = ColInt(r, "RESIDENCE_STATUS_ID"),
            ["rentAmt"] = Col(r, "RENT_AMT"),
            ["noYrsCurrResidence"] = Col(r, "NO_YRS_CURR_RESIDENCE"),
            ["noYrsCurrCity"] = Col(r, "NO_YRS_CURR_CITY"),
            ["occupationId"] = ColInt(r, "OCCUPATION_ID"),
            ["companyName"] = Col(r, "COMPANY_NAME"),
            ["companyAddress"] = Col(r, "COMPANY_ADDRESS"),
            ["officeLandline"] = Col(r, "OFFICE_LANDLINE"),
            ["officialEmailId"] = Col(r, "OFFICIAL_EMAIL_ID"),
            ["designationId"] = Col(r, "DESIGNATION_ID"),
            ["noYrsCurrCompany"] = Col(r, "NO_YRS_CURR_COMPANY"),
            ["totalWorkExp"] = Col(r, "TOTAL_WORK_EXP"),
            ["salaryBankId"] = Col(r, "SALARY_BANK_ID"),
            ["bankAccNo"] = Col(r, "BANK_ACC_NO"),
            ["salaryBranch"] = Col(r, "SALARY_BRANCH"),
            ["ref1Name"] = Col(r, "REF1_NAME"),
            ["ref1Address"] = Col(r, "REF1_ADDRESS"),
            ["ref1Contact"] = Col(r, "REF1_CONTACT"),
            ["ref2Name"] = Col(r, "REF2_NAME"),
            ["ref2Address"] = Col(r, "REF2_ADDRESS"),
            ["ref2Contact"] = Col(r, "REF2_CONTACT"),
            ["grossSalary"] = Col(r, "GROSS_SALARY"),
            ["netSalary"] = Col(r, "NET_SALARY"),
            ["annualTurnover"] = Col(r, "ANNUAL_TURNOVER"),
            ["netProfit"] = Col(r, "NET_PROFIT"),
            ["exLoanId"] = ColInt(r, "EX_LOAN_ID"),
            ["exLoanBankId"] = ColInt(r, "EX_LOAN_BANK_ID"),
            ["existingLoanEmi"] = Col(r, "ExistingLoanEMI"),
            ["outstandingLoanAmnt"] = Col(r, "OUTSTANDING_LOAN_AMNT"),
            ["appStatusId"] = ColInt(r, "APP_STATUS_ID"),
            ["sentToBankDate"] = Col(r, "SENT_TO_BANK_DATE"),
            ["remarks"] = Col(r, "REMARKS"),
            ["amntDisbursed"] = Col(r, "AMNT_DISBURSED"),
            ["loanOfferId"] = Col(r, "LOAN_OFFER_ID"),
            ["disbursalDate"] = Col(r, "DISBURSAL_DATE"),
            ["telecallerId"] = ColInt(r, "TELECALLER_ID"),
            ["supervisorId"] = ColInt(r, "SUPERVISOR_ID"),
            ["branchId"] = ColInt(r, "BRANCH_ID"),
            ["cibilScore"] = Col(r, "CIBIL_SCORE"),
            ["comments"] = Col(r, "Comments"),
            ["tenure"] = Col(r, "Tenure"),
            ["executiveName"] = ColInt(r, "ExecutiveName"),
            ["pickUpDate"] = Col(r, "PickupDate"),
            ["losNo"] = Col(r, "LOS_NO"),
            ["creditMonth"] = Col(r, "CreditMonth"),
            ["cpvStatus"] = Col(r, "CPV_STATUS"),
            ["statusDesc"] = Col(r, "StatusDesc"),
            ["updatedOn"] = Col(r, "UpdatedOn"),
            ["amtByBank"] = Col(r, "ACTUAL_AMT_DISBURSED"),
        });
    }

    /// <summary>Insert or update one application.</summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] ApplicationUpsert body)
    {
        var errors = Validate(body);
        if (errors.Count > 0) return BadRequest(new { error = string.Join(" ", errors), fields = errors });

        // The procedure inserts on "I" and updates on "E"; the legacy controller chose
        // between them by whether the row already had an Id.
        var action = body.Id.HasValue && body.Id.Value > 0 ? "E" : "I";

        try
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("USP_CIP_DATA_ENTRY", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 180 };
            BindDataEntry(cmd, body, action);

            // The procedure catches its own errors, logs them to TBL_CIP_ERROR_LOG and still
            // returns normally, so a successful ExecuteNonQuery does NOT mean the row was
            // written. The 'E'/'I' paths answer with a result set whose first column is
            // 'Success'; anything else means the write did not happen and must be reported.
            var result = new DataTable();
            using (var da = new SqlDataAdapter(cmd)) da.Fill(result);
            var saved = result.Rows.Count > 0
                && result.Columns.Contains("SuccessFail")
                && string.Equals(result.Rows[0]["SuccessFail"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);

            if (!saved)
            {
                var detail = await LatestProcErrorAsync();
                _logger.LogError("USP_CIP_DATA_ENTRY did not confirm a write for app {AppNo}: {Detail}",
                    body.AppNo, detail);
                return StatusCode(500, new
                {
                    error = "The database rejected the save. Check the values and try again.",
                    detail = "See TBL_CIP_ERROR_LOG."
                });
            }

            return Ok(new { ok = true, action });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USP_CIP_DATA_ENTRY failed for app {AppNo}", body.AppNo);
            return StatusCode(500, new { error = "Could not save the application: " + ex.Message });
        }
    }

    private void BindDataEntry(SqlCommand cmd, ApplicationUpsert b, string action)
    {
        var P = cmd.Parameters;
        // Unset text must be sent as NULL, never "". USP_CIP_DATA_ENTRY assigns these straight
        // into the base table, and APP_DATE / CLIENT_DOB / SENT_TO_BANK_DATE / DISBURSAL_DATE /
        // PickUpDate / CreditMonth are date columns — SQL Server fails to convert an empty
        // string to a date, and the procedure swallows that in its own TRY/CATCH. Sending NULL
        // lets the columns fall back to their nullable default.
        void T(string n, object? v) =>
            P.AddWithValue(n, v is null || (v is string s && s.Trim().Length == 0) ? DBNull.Value : v);
        void NT(string n, int? v) => P.AddWithValue(n, v.HasValue ? v.Value : (object)DBNull.Value);
        void DT(string n, decimal? v) => P.AddWithValue(n, v.HasValue ? v.Value : (object)DBNull.Value);

        P.AddWithValue("@Action", action);
        T("@Id", b.Id.HasValue ? b.Id.Value.ToString() : "");
        T("@APP_DATE", b.AppDate);
        T("@APP_NO", b.AppNo);
        NT("@BANK_ID", b.BankId);
        NT("@LOAN_ID", b.LoanId);
        NT("@LOAN_SCHEME_ID", b.LoanSchemeId);
        DT("@LOAN_AMNT", b.LoanAmnt);
        T("@PURPOSE_OF_LOAN", b.PurposeOfLoan);
        NT("@LG_CHNL_ID", b.LgChnlId);
        NT("@LG_RGN_ID", b.LgRgnId);
        T("@CLIENT_NAME", b.ClientName);
        T("@CLIENT_MBL_NO", b.ClientMblNo);
        T("@CLIENT_DOB", b.ClientDob);
        T("@CLIENT_PAN", b.ClientPan);
        T("@CLIENT_AADHAR_NO", b.ClientAadharNo);
        T("@GENDER", b.Gender);
        T("@MARIATAL_STATUS", b.MaritatalStatus);
        T("@SPOUSE_NAME", b.SpouseName);
        T("@SPOUS_OCCUPATION", b.SpousOccupation);
        NT("@NO_OF_DEPENDENTS", b.NoOfDependents);
        T("@MOTHER_NAME", b.MotherName);
        NT("@EDU_QUAL_ID", b.EduQualId);
        T("@UNIVERSITY", b.University);
        T("@YR_OF_PASSING", b.YrOfPassing);
        T("@PERSONAL_EMAIL_ID", b.PersonalEmailId);
        T("@CURR_ADDRESS", b.CurrAddress);
        // The procedure's parameter is spelled PERMANENT_ADDRESS even though its result
        // column is PERMANENT_ADDRESS — a legacy typo that must be matched exactly.
        T("@PERMANANT_ADDRESS", b.PermanentAddress);
        NT("@RESIDENCE_STATUS_ID", b.ResidenceStatusId);
        DT("@RENT_AMNT", b.RentAmt);
        T("@NO_YRS_CURR_RESIDENCE", b.NoYrsCurrResidence);
        T("@NO_YRS_CURR_CITY", b.NoYrsCurrCity);
        NT("@OCCUPATION_ID", b.OccupationId);
        T("@COMPANY_NAME", b.CompanyName);
        T("@COMPANY_ADDRESS", b.CompanyAddress);
        T("@OFFICE_LANDLINE", b.OfficeLandline);
        T("@OFFICIAL_EMAIL_ID", b.OfficialEmailId);
        T("@DESIGNATION_ID", b.DesignationId);
        T("@NO_YRS_CURR_COMPANY", b.NoYrsCurrCompany);
        T("@TOTAL_WORK_EXP", b.TotalWorkExp);
        T("@SALARY_BANK_ID", b.SalaryBankId);
        T("@BANK_ACC_NO", b.BankAccNo);
        T("@SALARY_BRANCH", b.SalaryBranch);
        T("@REF1_NAME", b.Ref1Name);
        T("@REF1_ADDRESS", b.Ref1Address);
        T("@REF1_CONTACT", b.Ref1Contact);
        T("@REF2_NAME", b.Ref2Name);
        T("@REF2_ADDRESS", b.Ref2Address);
        T("@REF2_CONTACT", b.Ref2Contact);
        DT("@GROSS_SALARY", b.GrossSalary);
        DT("@NET_SALARY", b.NetSalary);
        DT("@ANNUAL_TURNOVER", b.AnnualTurnover);
        T("@NET_PROFIT", b.NetProfit);
        NT("@EX_LOAN_ID", b.ExLoanId);
        NT("@EX_LOAN_BANK_ID", b.ExLoanBankId);
        DT("@OUTSTANDING_LOAN_AMNT", b.OutstandingLoanAmnt);
        NT("@APP_STATUS_ID", b.AppStatusId);
        T("@SENT_TO_BANK_DATE", b.SentToBankDate);
        T("@REMARKS", b.Remarks);
        DT("@AMNT_DISBURSED", b.AmntDisbursed);
        T("@LOAN_OFFER_ID", b.LoanOfferId);
        T("@DISBURSAL_DATE", b.DisbursalDate);
        NT("@TELECALLER_ID", b.TelecallerId);
        NT("@SUPERVISOR_ID", b.SupervisorId);
        NT("@BRANCH_ID", b.BranchId);
        DT("@CIBIL_SCORE", b.CibilScore);
        P.AddWithValue("@CREATED_BY", EmpCode.ToString());
        DT("@ExistingLoanEMI", b.ExistingLoanEmi);
        T("@Comments", b.Comments);
        T("@Tenure", b.Tenure);
        NT("@ExecutiveName", b.ExecutiveName);
        T("@PickUpDate", b.PickUpDate);
        T("@Attachmentpath", b.AttachmentPath);
        NT("@LOS_NO", int.TryParse(b.LosNo, out var los) ? los : null);
        T("@CreditMonth", b.CreditMonth);
        DT("@AmtByBank", b.AmtByBank);

        // The single form has no rows to stage, so the TVP stays empty — the same thing
        // the legacy controller passed when a human filled the form in.
        var empty = new DataTable();
        foreach (var col in ApplicationBulk)
            empty.Columns.Add(col, typeof(string)).AllowDBNull = true;
        P.AddWithValue("@TBL_CIP_CLIENT_APPLICATION_DETAILS_Type", empty);
    }

    /// <summary>Most recent message the procedure logged, so a silent failure is explainable.</summary>
    private async Task<string?> LatestProcErrorAsync()
    {
        try
        {
            return await _db.ScalarAsync(
                "SELECT TOP 1 ERROR_DESC FROM dbo.TBL_CIP_ERROR_LOG WHERE ERROR_MODULE='A' ORDER BY ERROR_DATE DESC") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Server-side guards the legacy form left to the browser. The legacy validation
    /// blocks in the bulk controller were commented out, and the single form validated
    /// only that a bank was chosen.
    /// </summary>
    private static List<string> Validate(ApplicationUpsert b)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(b.ClientName)) e.Add("Client name is required.");
        if (b.ClientName.Length > 100) e.Add("Client name exceeds 100 characters.");
        if (!string.IsNullOrWhiteSpace(b.ClientMblNo) && b.ClientMblNo.Trim().Length is < 10 or > 12)
            e.Add("Mobile number must be 10 to 12 digits.");
        if (!string.IsNullOrWhiteSpace(b.ClientPan))
        {
            var pan = b.ClientPan.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(pan, @"^[A-Z]{5}[0-9]{4}[A-Z]$"))
                e.Add("PAN must look like ABCDE1234F.");
        }
        if (!string.IsNullOrWhiteSpace(b.ClientAadharNo) &&
            !System.Text.RegularExpressions.Regex.IsMatch(b.ClientAadharNo.Trim(), @"^\d{12}$"))
            e.Add("Aadhaar number must be 12 digits.");
        if (b.GrossSalary is > 0 && b.NetSalary > b.GrossSalary)
            e.Add("Net salary cannot be greater than gross salary.");
        if (b.BankId is null or 0) e.Add("Please select a bank.");
        if (b.LoanAmnt is < 0) e.Add("Loan amount cannot be negative.");
        return e;
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        try
        {
            var n = await _db.ExecuteAsync("EXEC dbo.USP_CIP_DELETE_APPLICATION @Key=@k, @UserKey=@u",
                p => { p.AddWithValue("@k", id); p.AddWithValue("@u", EmpCode); });
            return Ok(new { ok = true, deleted = n });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USP_CIP_DELETE_APPLICATION failed for {Id}", id);
            return StatusCode(500, new { error = "Could not delete the application: " + ex.Message });
        }
    }

    // --------------------------------------------------------------- scan upload

    /// <summary>
    /// Upload scan files against an application. The legacy controller wrote straight to
    /// <c>~/Uploads/{appNo}/{appNo}_{file}</c>; the same layout is reproduced so an
    /// application exported from the old portal still resolves its attachments.
    /// </summary>
    [HttpPost("scan")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> UploadScan([FromForm] IFormFileCollection? files, [FromForm] string appNo)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "Choose at least one file to upload." });
        appNo = SanitiseSegment(appNo);
        if (appNo.Length == 0)
            return BadRequest(new { error = "An application number is required." });

        var dir = ScanDir(appNo);
        Directory.CreateDirectory(dir);

        var saved = new List<string>();
        foreach (var f in files.Where(f => f.Length > 0))
        {
            var name = SanitiseSegment(f.FileName);
            if (name.Length == 0) continue;
            var target = Path.Combine(dir, $"{appNo}_{name}");
            await using var fs = System.IO.File.Create(target);
            await f.CopyToAsync(fs);
            saved.Add(name);
        }

        if (saved.Count == 0) return BadRequest(new { error = "No usable files in the upload." });
        return Ok(new { ok = true, saved });
    }

    [HttpGet("scan/{appNo}")]
    public IActionResult ListScan(string appNo)
    {
        var dir = ScanDir(SanitiseSegment(appNo));
        if (!Directory.Exists(dir)) return Ok(Array.Empty<ScanFile>());

        var files = Directory.GetFiles(dir)
            .Where(f => !string.Equals(Path.GetFileName(f), ".gitkeep", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ScanFile
            {
                Name = Path.GetFileName(f),
                Size = new FileInfo(f).Length.ToString("N0"),
                Modified = System.IO.File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm"),
                Url = $"api/applications/scan/{Uri.EscapeDataString(SanitiseSegment(appNo))}/{Uri.EscapeDataString(Path.GetFileName(f))}",
            })
            .OrderBy(f => f.Name)
            .ToList();
        return Ok(files);
    }

    [HttpGet("scan/{appNo}/{fileName}")]
    public IActionResult DownloadScan(string appNo, string fileName)
    {
        // Resolve inside the folder and then verify: blocks ".." traversal.
        var dir = ScanDir(SanitiseSegment(appNo));
        var file = Path.GetFullPath(Path.Combine(dir, SanitiseSegment(fileName)));
        if (!file.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(file))
            return NotFound(new { error = "File not found." });

        var bytes = System.IO.File.ReadAllBytes(file);
        return File(bytes, "application/octet-stream", Path.GetFileName(file));
    }

    private string ScanDir(string appNo) =>
        Path.Combine(StorageRoot(), SanitiseSegment(appNo));

    private string StorageRoot()
    {
        var folder = _config["Uploads:Folder"] ?? "uploads";
        if (Path.IsPathRooted(folder)) return folder;
        // Resolve against the application's content root, not Directory.GetCurrentDirectory():
        // the current directory is host-dependent (IIS, service, container) and is not
        // guaranteed to be the app folder, which would scatter scans outside it.
        var root = _env.ContentRootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = Directory.GetCurrentDirectory();
        return Path.Combine(root, folder);
    }

    private static string SanitiseSegment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var name = Path.GetFileName(s.Trim()); // strips any directory component
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ------------------------------------------------------------- bulk insert

    /// <summary>
    /// Bulk insert from a spreadsheet. The legacy controller used OleDb against .xls/.xlsx
    /// and mapped the sheet positionally onto the same 69-column TVP; .xlsx is accepted
    /// here and the columns are matched by header name so column order does not matter.
    /// </summary>
    [HttpPost("bulk")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> BulkInsert([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Choose a spreadsheet to upload." });
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .xlsx files are supported." });

        DataTable parsed;
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            parsed = ExcelReader.ReadFirstSheet(ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse bulk upload {File}", file.FileName);
            return BadRequest(new { error = "Could not read the workbook. Re-save it as .xlsx and try again." });
        }

        if (parsed.Rows.Count == 0) return BadRequest(new { error = "The sheet has no data rows." });

        // Match the sheet headers to the TVP columns by name (case/space insensitive),
        // falling back to positional order the way the legacy OleDb import did.
        var header = Enumerable.Range(0, parsed.Columns.Count)
            .Select(i => Cell(parsed.Rows[0], i)).Select(Normalise).ToArray();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < ApplicationBulk.Length; c++)
        {
            var target = Normalise(ApplicationBulk[c]);
            var idx = Array.FindIndex(header, h => h == target);
            if (idx < 0) idx = c < parsed.Columns.Count ? c : -1; // positional fallback
            map[ApplicationBulk[c]] = idx;
        }

        var tvp = NewApplicationTvp();
        var blank = new List<string>();
        var skipped = 0;
        foreach (DataRow r in parsed.Rows.Cast<DataRow>().Skip(1))
        {
            if (tvp.Rows.Count >= MaxBulkRows) { skipped++; continue; }

            var row = tvp.NewRow();
            var anyValue = false;
            foreach (var col in ApplicationBulk)
            {
                var v = Cell(r, map[col]).Trim();
                row[col] = v;
                if (v.Length > 0) anyValue = true;
            }
            if (!anyValue) { skipped++; continue; }

            // The application number is what every other lookup keys off, so a row
            // without one can only ever fail inside the procedure.
            if (string.IsNullOrWhiteSpace(Cell(r, map["APP_NO"])))
            {
                if (blank.Count < 50) blank.Add($"Row {tvp.Rows.Count + 2}: application number is blank.");
                skipped++;
                continue;
            }
            tvp.Rows.Add(row);
        }

        if (tvp.Rows.Count == 0)
            return BadRequest(new { error = "No usable rows found.", details = blank });

        try
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("USP_CIP_DATA_ENTRY", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 };
            cmd.Parameters.AddWithValue("@Action", "B");
            cmd.Parameters.AddWithValue("@CREATED_BY", EmpCode.ToString());
            // Every scalar is unused on the bulk path; the procedure reads the TVP.
            foreach (var name in BulkScalarNames)
                if (!cmd.Parameters.Contains(name))
                    cmd.Parameters.AddWithValue(name, DBNull.Value);
            var pTvp = cmd.Parameters.Add("@TBL_CIP_CLIENT_APPLICATION_DETAILS_Type", SqlDbType.Structured);
            pTvp.TypeName = "dbo.TBL_CIP_CLIENT_APPLICATION_DETAILS_Type";
            pTvp.Value = tvp;
            await cmd.ExecuteNonQueryAsync();

            return Ok(new { ok = true, inserted = tvp.Rows.Count, skipped, details = blank });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk insert failed");
            return StatusCode(500, new { error = "Bulk insert failed: " + ex.Message });
        }
    }

    /// <summary>Column order of TBL_CIP_CLIENT_APPLICATION_DETAILS_Type, and of the template sheet.</summary>
    public static readonly string[] ApplicationBulk =
    {
        "APP_DATE", "APP_NO", "LOAN_ID", "BANK_ID", "LG_CHNL_ID", "LG_RGN_ID", "LOAN_SCHEME_ID",
        "LOAN_AMNT", "PURPOSE_OF_LOAN", "CLIENT_NAME", "CLIENT_MBL_NO", "CLIENT_DOB", "CLIENT_PAN",
        "CLIENT_AADHAR_NO", "GENDER", "MARIATAL_STATUS", "SPOUSE_NAME", "SPOUS_OCCUPATION",
        "NO_OF_DEPENDENTS", "MOTHER_NAME", "EDU_QUAL_ID", "UNIVERSITY", "YR_OF_PASSING",
        "PERSONAL_EMAIL_ID", "CURR_ADDRESS", "PERMANENT_ADDRESS", "RESIDENCE_STATUS_ID", "RENT_AMNT",
        "NO_YRS_CURR_RESIDENCE", "NO_YRS_CURR_CITY", "OCCUPATION_ID", "COMPANY_NAME", "COMPANY_ADDRESS",
        "OFFICE_LANDLINE", "OFFICIAL_EMAIL_ID", "DESIGNATION_ID", "NO_YRS_CURR_COMPANY",
        "TOTAL_WORK_EXP", "SALARY_BANK_ID", "BANK_ACC_NO", "SALARY_BRANCH", "REF1_NAME",
        "REF1_ADDRESS", "REF1_CONTACT", "REF2_NAME", "REF2_ADDRESS", "REF2_CONTACT", "GROSS_SALARY",
        "NET_SALARY", "ANNUAL_TURNOVER", "NET_PROFIT", "EX_LOAN_ID", "EX_LOAN_BANK_ID",
        "ExistingLoanEMI", "OUTSTANDING_LOAN_AMNT", "APP_STATUS_ID", "SENT_TO_BANK_DATE", "REMARKS",
        "AMNT_DISBURSED", "LOAN_OFFER_ID", "DISBURSAL_DATE", "TELECALLER_ID", "SUPERVISOR_ID",
        "BRANCH_ID", "CIBIL_SCORE", "Comments", "Tenure", "ExecutiveName", "PickUpDate",
    };

    /// <summary>Scalar parameter names of USP_CIP_DATA_ENTRY that the bulk path must still pass.</summary>
    private static readonly string[] BulkScalarNames =
    {
        "@Id","@APP_DATE","@APP_NO","@BANK_ID","@LOAN_ID","@LOAN_SCHEME_ID","@LOAN_AMNT","@PURPOSE_OF_LOAN",
        "@LG_CHNL_ID","@LG_RGN_ID","@CLIENT_NAME","@CLIENT_MBL_NO","@CLIENT_DOB","@CLIENT_PAN",
        "@CLIENT_AADHAR_NO","@GENDER","@MARIATAL_STATUS","@SPOUSE_NAME","@SPOUS_OCCUPATION",
        "@NO_OF_DEPENDENTS","@MOTHER_NAME","@EDU_QUAL_ID","@UNIVERSITY","@YR_OF_PASSING",
        "@PERSONAL_EMAIL_ID","@CURR_ADDRESS","@PERMANENT_ADDRESS","@RESIDENCE_STATUS_ID","@RENT_AMNT",
        "@NO_YRS_CURR_RESIDENCE","@NO_YRS_CURR_CITY","@OCCUPATION_ID","@COMPANY_NAME","@COMPANY_ADDRESS",
        "@OFFICE_LANDLINE","@OFFICIAL_EMAIL_ID","@DESIGNATION_ID","@NO_YRS_CURR_COMPANY","@TOTAL_WORK_EXP",
        "@SALARY_BANK_ID","@BANK_ACC_NO","@SALARY_BRANCH","@REF1_NAME","@REF1_ADDRESS","@REF1_CONTACT",
        "@REF2_NAME","@REF2_ADDRESS","@REF2_CONTACT","@GROSS_SALARY","@NET_SALARY","@ANNUAL_TURNOVER",
        "@NET_PROFIT","@EX_LOAN_ID","@EX_LOAN_BANK_ID","@OUTSTANDING_LOAN_AMNT","@APP_STATUS_ID",
        "@SENT_TO_BANK_DATE","@REMARKS","@AMNT_DISBURSED","@LOAN_OFFER_ID","@DISBURSAL_DATE",
        "@TELECALLER_ID","@SUPERVISOR_ID","@BRANCH_ID","@CIBIL_SCORE","@CREATED_BY","@ExistingLoanEMI",
        "@Comments","@Tenure","@ExecutiveName","@PickUpDate","@Attachmentpath","@LOS_NO","@CreditMonth",
        "@AmtByBank",
    };

    private static DataTable NewApplicationTvp()
    {
        var tvp = new DataTable();
        foreach (var c in ApplicationBulk) tvp.Columns.Add(c, typeof(string));
        return tvp;
    }

    private static string Normalise(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Cell(DataRow r, int i) =>
        i >= 0 && i < r.Table.Columns.Count && r[i] != DBNull.Value ? r[i]?.ToString() ?? "" : "";

    // ---------------------------------------------------------- existing loans

    /// <summary>List existing-loan lines (the legacy "F" fetch).</summary>
    [HttpGet("existing-loans")]
    public async Task<ActionResult<object>> ExistingLoans()
    {
        var ds = await _db.QuerySetAsync(
            "EXEC dbo.USP_CIP_EXISTINGLOAN_DETAILS @Action='F', @CREATED_BY=@by, " +
            "@TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type=@tvp",
            p =>
            {
                p.AddWithValue("@by", EmpCode.ToString());
                var tvp = new DataTable();
                tvp.Columns.Add("App_No", typeof(string));
                tvp.Columns.Add("ExistingLoan", typeof(string));
                tvp.Columns.Add("ExistingLoanBank", typeof(string));
                tvp.Columns.Add("ExistingLoanEMI", typeof(string));
                tvp.Columns.Add("OutstandingLoanAmt", typeof(string));
                var pt = p.Add("@tvp", SqlDbType.Structured);
                pt.TypeName = "dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type";
                pt.Value = tvp;
            },
            commandTimeout: 180);

        var rows = new List<object>();
        if (ds.Tables.Count > 0)
            foreach (DataRow r in ds.Tables[0].Rows)
                rows.Add(new
                {
                    appNo = r.Table.Columns.Contains("App_No") ? Str(r["App_No"]) : "",
                    existingLoan = Str(r["ExistingLoan"]),
                    existingLoanBank = Str(r["ExistingLoanBank"]),
                    existingLoanEmi = Str(r["ExistingLoanEMI"]),
                    outstandingLoanAmt = Str(r["OutstandingLoanAmt"]),
                });

        return Ok(new { rows });
    }

    /// <summary>
    /// Upload existing-loan details from CSV. The legacy controller rejected the whole
    /// file if any row was blank or duplicated; both checks are kept, but the failures
    /// are returned as a report the user can act on instead of a downloaded file.
    /// </summary>
    [HttpPost("existing-loans")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadExistingLoans([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Choose a CSV file to upload." });
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .csv files are supported." });

        List<ExistingLoanRow> parsed;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            parsed = ParseExistingLoans(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse existing-loan upload {File}", file.FileName);
            return BadRequest(new { error = "Could not read the CSV. Check the file format." });
        }

        if (parsed.Count == 0) return BadRequest(new { error = "The CSV has no data rows." });
        if (parsed.Count > MaxBulkRows) return BadRequest(new { error = $"At most {MaxBulkRows} rows can be uploaded at once." });

        // Same two validations the legacy controller ran before calling the procedure.
        var problems = new List<string>();
        for (int i = 0; i < parsed.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(parsed[i].ExistingLoan))
                problems.Add($"Row {i + 2}: existing loan type cannot be blank.");
        }
        var dupes = parsed
            .GroupBy(r => new { AppNo = r.AppNo.Trim(), ExistingLoan = r.ExistingLoan.Trim(), ExistingLoanBank = r.ExistingLoanBank.Trim() })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var d in dupes)
            problems.Add($"Row group duplicated: application {d.AppNo}, {d.ExistingLoan}, {d.ExistingLoanBank}.");

        if (problems.Count > 0)
            return BadRequest(new { error = "The CSV has errors and was not uploaded.", details = problems.Take(100).ToList() });

        var tvp = new DataTable();
        tvp.Columns.Add("App_No", typeof(string));
        tvp.Columns.Add("ExistingLoan", typeof(string));
        tvp.Columns.Add("ExistingLoanBank", typeof(string));
        tvp.Columns.Add("ExistingLoanEMI", typeof(string));
        tvp.Columns.Add("OutstandingLoanAmt", typeof(string));
        foreach (var r in parsed)
            tvp.Rows.Add(r.AppNo.Trim(), r.ExistingLoan.Trim(), r.ExistingLoanBank.Trim(), r.ExistingLoanEMI.Trim(), r.OutstandingLoanAmt.Trim());

        try
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("USP_CIP_EXISTINGLOAN_DETAILS", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@Action", "I");
            cmd.Parameters.AddWithValue("@CREATED_BY", EmpCode.ToString());
            var pt = cmd.Parameters.Add("@TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type", SqlDbType.Structured);
            pt.TypeName = "dbo.TBL_CIP_CLIENT_EXISTINGLOAN_DETAILS_Type";
            pt.Value = tvp;
            await cmd.ExecuteNonQueryAsync();

            return Ok(new { ok = true, inserted = parsed.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Existing loan upload failed");
            return StatusCode(500, new { error = "Upload failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Parse the existing-loan CSV. Quoted fields are honoured, which the legacy
    /// comma-splitting regex got wrong for any value containing a quoted comma.
    /// </summary>
    internal static List<ExistingLoanRow> ParseExistingLoans(string csv)
    {
        var result = new List<ExistingLoanRow>();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return result;

        for (int i = 1; i < lines.Count; i++)
        {
            var c = SplitCsvLine(lines[i]);
            string At(int i) => i < c.Count ? c[i] : "";
            result.Add(new ExistingLoanRow
            {
                AppNo = At(0),
                ExistingLoan = At(1),
                ExistingLoanBank = At(2),
                ExistingLoanEMI = At(3),
                OutstandingLoanAmt = At(4),
            });
        }
        return result;
    }

    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    // ------------------------------------------------------------------- helpers

    private static string Str(object? v) => v == null || v == DBNull.Value ? "" : v.ToString() ?? "";

    /// <summary>
    /// Tolerant column read. USP_CIP_GET_CLIENT_DETAILS returns APP_DATE twice, so the
    /// DataTable renames the second copy; and several legacy columns are absent for
    /// older rows. A missing column must yield an empty field, never a 500.
    /// </summary>
    private static string Col(DataRow r, params string[] names)
    {
        foreach (var n in names)
        {
            if (r.Table.Columns.Contains(n)) return Str(r[n]);
        }
        foreach (var n in names)
        {
            var c = r.Table.Columns.Cast<DataColumn>()
                .FirstOrDefault(c => c.ColumnName.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (c != null) return Str(r[c]);
        }
        return "";
    }

    private static int? ColInt(DataRow r, params string[] names)
    {
        foreach (var n in names)
        {
            if (r.Table.Columns.Contains(n)) return ToNullableInt(r[n]);
        }
        foreach (var n in names)
        {
            var c = r.Table.Columns.Cast<DataColumn>()
                .FirstOrDefault(c => c.ColumnName.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (c != null) return ToNullableInt(r[c]);
        }
        return null;
    }
    private static int ToInt(object? v) => v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    private static long ToLong(object? v) => v == null || v == DBNull.Value ? 0L : Convert.ToInt64(v);
    private static int? ToNullableInt(object? v) => v == null || v == DBNull.Value || string.IsNullOrWhiteSpace(v.ToString())
        ? null : Convert.ToInt32(v);

    public record Option(int Id, string Name);
    public record IdRequest(int Id);
    public record SupervisorRequest(int BranchId, int? SupervisorId);
}