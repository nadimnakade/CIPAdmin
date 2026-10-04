using System.Text;
using System.Text.Json;
using CIPAdmin.Backend.Bsa;
using CIPAdmin.Backend.Controllers;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}
BsaTransaction Tx(string desc, bool credit = false, decimal amount = 6000) => new()
{
    Date = "2026-09-01", Description = desc, Debit = credit ? 0 : amount,
    Credit = credit ? amount : 0, Balance = 100000, SourceType = "csv", SourceRow = 2
};
void Category(string desc, string expected, bool credit = false, decimal amount = 6000)
{
    var a = BsaAnalyzer.AnalyzeTransactions(new() { Tx(desc, credit, amount) });
    Check(a.Transactions.Single().Category == expected, desc + " => " + expected);
    Check(a.AccountSummary.TotalDebits == (credit ? 0 : amount) &&
          a.AccountSummary.TotalCredits == (credit ? amount : 0), "totals: " + desc);
    Check(a.Transactions.Single().SourceRow == 2, "source: " + desc);
}
Category("NEFT/SALARY/INFOSYS SEPTEMBER", "Salary", true, 80000);
Category("NEFT/BAJAJ FINANCE SALARY", "Salary", true, 80000);
Category("NEFT/SAL/ACME", "Salary", true, 3000);
Category("NACH/123/456789/ACME TRANSFER", "Other", true);
Category("IMPS/EARLYSALARY LOAN DISBURSEMENT", "Loan disbursement", true, 50000);
Category("NEFT/SALARY REFUND", "Other", true);
Category("NACH/HDFC HOME LOAN EMI", "EMI / loan repayment");
Category("NEFT/ICICI PERSONAL LOAN EMI", "EMI / loan repayment", amount: 300);
Category("UPI/LOAN REPAYMENT", "EMI / loan repayment");
Category("NACH/UNKNOWN SUBSCRIPTION", "Other");
Category("NACH/HDFC LIFE PREMIUM", "Insurance");
Category("ECS/LIC/PREMIUM", "Insurance");
Category("NACH/ICICI LOMBARD PREMIUM", "Insurance");
Category("NACH/LIC HOUSING LOAN EMI", "EMI / loan repayment");
Category("NACH/HDFC CREDIT CARD PAYMENT XX1234", "Credit card payment");
Category("UPI/CRED/CREDIT CARD BILL/HDFC", "Credit card payment");
Category("UPI/PAID VIA CRED/HDFC BANK/XX1234", "Credit card payment");
Category("NEFT/SBICARD", "Credit card payment");
Category("BILLDESK/HDFCCC", "Credit card payment");
Category("AMEX BILL PAYMENT", "Credit card payment");
Category("UPI/CREDPAY/GROCERY", "Other");
Category("POS/DEBIT CARD PURCHASE", "Other");
Category("NACH/CAMS/MF SIP", "Mutual fund");
Category("NACH/LIC MF SIP", "Mutual fund");
Category("UPI/EQUITY SHARES BUY", "Stock market");
Category("NEFT/HDFC CARD PAYMENT", "Credit card payment");
Category("POS/CREDIT CARD PURCHASE", "Other");
Category("UPI/DEBIT CARD BILL PAYMENT", "Other");
Category("ACH/BD- FTMF/1234", "Mutual fund");
Category("ACH/BD- Sundaram MF/1234", "Mutual fund");
Category("NACH/CTIDFCMUTF/1234", "Mutual fund");
Category("NACH/CTUNKNOWN/1234", "Other");
Category("NEFT/KFIN/MUTUAL FUND REDEMPTION", "Investment", true);
Category("NEFT/ICICI PRU MF REDEMPTION", "Investment", true, 75000);
Category("NACH/MOTILAL OSWAL MF SIP", "Mutual fund");
Category("UPI/ZERODHA COIN MF", "Mutual fund");
Category("NEFT/NFO MUTUAL FUND", "Mutual fund");
Category("NEFT/FIXED DEPOSIT PURCHASE", "Investment");
Category("FIXED DEPOSIT MATURITY", "Investment", true);
Category("NACH/RECURRING DEPOSIT INSTALMENT", "Investment");
Category("NACH/NPS CONTRIBUTION", "Investment");
Category("PPF CONTRIBUTION", "Investment");
Category("G-SEC BOND PURCHASE", "Investment");
Category("UPI/cp.zepto13a87@a", "Other");
Category("UPI/FINO GROCERY BILLDESK", "Other");
Category("UPI/FIRST TIME CUSTOMER", "Other");
Category("UPI/CALL CENTRE", "Other");
Category("UPI/PUT GROCERIES", "Other");
Category("NEFT/OPTION SERVICE", "Other");
Category("UPI/DIGIT MOBILE", "Other");
Category("UPI/SUNDARAM SHOP", "Other");
Category("UPI/COIN SHOP", "Other");
Category("NEFT/NSE TRANSFER", "Other");
Category("NEFT/ZERODHA", "Stock market");
Category("ZERODHA DP CHARGES", "Stock market");
Category("ZERODHA RETURN CHARGES", "Charges / returns");
Category("NEFT/GROWW", "Stock market");
Category("NEFT/MOTILAL OSWAL PAYOUT", "Stock market", true, 100000);
Category("NEFT/BAJAJ ALLIANZ CLAIM", "Other", true, 100000);
Category("NACH EMI RETURN CHARGES", "Charges / returns");
Category("NACH/APY", "Pension");
Category("ECS/JIO MOBILE BILL", "Utility / expense");
var stocks = BsaAnalyzer.AnalyzeTransactions(new()
{
    Tx("NEFT/ZERODHA EQUITY SHARE PURCHASE"), Tx("NEFT/UPSTOX SHARES SALE", true),
    Tx("NEFT/ANGEL ONE F&O MARGIN"), Tx("NEFT/FYERS FUTURES SETTLEMENT", true),
    Tx("NEFT/NIFTY OPTIONS PREMIUM"), Tx("NEFT/ZERODHA"), Tx("NEFT/ZERODHA PAYOUT", true)
});
Check(stocks.StockMarket.Count == 7, "stock count");
Check(stocks.StockMarket[0].Segment == "Equity" && stocks.StockMarket[0].Activity == "Share purchase", "equity buy");
Check(stocks.StockMarket[1].Activity == "Share sale proceeds", "equity sale");
Check(stocks.StockMarket.Skip(2).Take(3).All(x => x.Segment == "F&O"), "derivatives segments");
Check(stocks.StockMarket.Skip(5).All(x => x.NeedsReview && x.Segment == "Unspecified"), "broker ambiguity");
var duplicate = BsaAnalyzer.AnalyzeTransactions(new() { Tx("NACH/HDFC EMI REF111111"), Tx("NACH/HDFC EMI REF222222") });
Check(duplicate.EmiPayments.Count == 2 && duplicate.MonthlySummary.Single().Emi == 12000, "same-day payments retained");
var dual = Tx("SALARY", true); dual.Debit = 200;
var dualResult = BsaAnalyzer.AnalyzeTransactions(new() { dual });
Check(dualResult.Transactions.Single().NeedsReview && dualResult.OtherIncome.Count == 1 && dualResult.OtherDebits.Count == 1, "dual sided review");
Check(BsaAnalyzer.AnalyzeTransactions(new()).Transactions.Count == 0, "empty input");
var sample = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "sample-transactions.csv"));
IFormFile Upload() => new FormFile(new MemoryStream(sample), 0, sample.Length, "statement", "sample-transactions.csv");
foreach (var response in new[] { new BsaController().Analyze(Upload()), new BsaController().AnalyzeEither(Upload()) })
{
    Check(response is OkObjectResult, "upload succeeds");
    var json = JsonSerializer.SerializeToElement(((OkObjectResult)response).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(json.GetProperty("success").GetBoolean(), "success flag");
    var analysis = json.GetProperty("analysis");
    var rows = analysis.GetProperty("transactions");
    Check(rows.GetArrayLength() == 16 && json.GetProperty("transactionCount").GetInt32() == 16, "all uploaded transactions");
    Check(rows[0].GetProperty("sourceRow").GetInt32() == 2, "correct CSV source line");
    Check(analysis.GetProperty("salary").GetArrayLength() == 1, "uploaded salary");
    Check(analysis.GetProperty("emiPayments").GetArrayLength() == 2, "uploaded EMI");
    Check(analysis.GetProperty("insurance").GetArrayLength() == 1, "uploaded insurance");
    Check(analysis.GetProperty("creditCardPayments").GetArrayLength() == 2, "uploaded cards");
    Check(analysis.GetProperty("sipInvestments").GetArrayLength() == 2, "uploaded funds");
    Check(analysis.GetProperty("stockMarket").GetArrayLength() == 4, "uploaded stock activity");
    var summary = analysis.GetProperty("accountSummary");
    Check(rows.EnumerateArray().Sum(x => x.GetProperty("debit").GetDecimal()) == summary.GetProperty("totalDebits").GetDecimal(), "debit conservation");
    Check(rows.EnumerateArray().Sum(x => x.GetProperty("credit").GetDecimal()) == summary.GetProperty("totalCredits").GetDecimal(), "credit conservation");
}
var export = new BsaController().Export(new BsaController.BsaExportRequest
{
    FileName = "Classification regression", Rows = new() { new[] { "Segment", "Activity" }, new[] { "F&O", "F&O margin" } }
});
Check(export is FileContentResult, "Excel export response");
using (var workbook = new XLWorkbook(new MemoryStream(((FileContentResult)export).FileContents)))
    Check(workbook.Worksheet(1).Cell(2, 1).GetString() == "F&O", "Excel preserves segment");
using (var workbook = new XLWorkbook())
{
    var sheet = workbook.AddWorksheet("Statement");
    var lines = Encoding.UTF8.GetString(sample).Trim().Split('\n');
    for (var r = 0; r < lines.Length; r++)
    {
        var cells = lines[r].TrimEnd('\r').Split(',');
        for (var c = 0; c < cells.Length; c++) sheet.Cell(r + 1, c + 1).Value = cells[c];
    }
    using var stream = new MemoryStream();
    workbook.SaveAs(stream);
    var parsed = BsaStatementParser.ParseFile(stream.ToArray(), "statement.xlsx");
    Check(parsed.Count == 16 && BsaAnalyzer.AnalyzeTransactions(parsed).StockMarket.Count == 4, "XLSX parsing and classification");
}
var text = "Date Description Debit Credit Balance\n2026-09-01 SALARY INFOSYS 0.00 80000.00 180000.00\n2026-09-02 HDFC EMI 12000.00 0.00 168000.00\n";
var parsedText = BsaStatementParser.ParseFile(Encoding.UTF8.GetBytes(text), "statement.txt");
Check(parsedText.Count == 2 && BsaAnalyzer.AnalyzeTransactions(parsedText).Salary.Count == 1, "TXT parsing and classification");
if (args.Length > 0)
{
    var parsedPdf = BsaStatementParser.ParseFile(File.ReadAllBytes(args[0]), "statement.pdf");
    Check(parsedPdf.Count == 2 && BsaAnalyzer.AnalyzeTransactions(parsedPdf).EmiPayments.Count == 1, "text PDF parsing and classification");
}
Console.WriteLine($"PASS: {checks} assertions, including both upload endpoints and XLSX export.");
