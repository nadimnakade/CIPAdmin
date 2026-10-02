using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CIPAdmin.Backend.Bsa;

/// <summary>
/// C# port of the BSA universal bank statement parser (parser.js).
/// Supports PDF (PdfPig text extraction), CSV, XLSX (ClosedXML) and TXT,
/// with the same generic text heuristics + Union Bank special-case parser.
/// </summary>
public static class BsaStatementParser
{
    private static readonly Regex MoneyRe = new(@"(?:\d{1,3}(?:,\s?\d{2,3})+|\d+)\.\d{2}", RegexOptions.Compiled);

    public static List<BsaTransaction> ParseFile(byte[] bytes, string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => ParsePdf(bytes),
            ".csv" => ParseCsv(bytes),
            ".xlsx" or ".xls" => ParseXlsx(bytes),
            ".txt" => ParseText(Encoding.UTF8.GetString(bytes)),
            _ => throw new BsaException($"Unsupported file type: {ext}", "UNSUPPORTED_TYPE"),
        };
    }

    public sealed class BsaException : Exception
    {
        public string Code { get; }
        public BsaException(string message, string code) : base(message) => Code = code;
    }

    // ── Amount helpers ──────────────────────────────────────────────

    private sealed record MoneyHit(decimal Value, int Sign, int Idx, string Raw);

    private static List<MoneyHit> ExtractAmounts(string line)
    {
        var outp = new List<MoneyHit>();
        foreach (Match m in MoneyRe.Matches(line))
        {
            var idx = m.Index;
            var j = idx - 1;
            while (j >= 0 && char.IsWhiteSpace(line[j])) j--;
            var sign = 0;
            if (j >= 0 && (line[j] == '+' || line[j] == '-')) sign = line[j] == '+' ? 1 : -1;

            var afterLen = Math.Min(8, line.Length - (idx + m.Length));
            var after = afterLen > 0 ? line.Substring(idx + m.Length, afterLen).ToUpperInvariant() : "";
            if (sign == 0 && Regex.IsMatch(after, @"\bCR\b")) sign = 1;
            if (sign == 0 && Regex.IsMatch(after, @"\bDR\b")) sign = -1;

            outp.Add(new MoneyHit(BsaAnalyzer.ParseAmount(m.Value), sign, idx, m.Value));
        }
        return outp;
    }

    // ── PDF ─────────────────────────────────────────────────────────

    private static List<BsaTransaction> ParsePdf(byte[] bytes)
    {
        var raw = ExtractPdfText(bytes);
        if (raw.Trim().Length < 50)
            throw new BsaException(
                "No extractable text found in this PDF. This looks like a scanned/image-only statement. Please export CSV/XLSX from netbanking or download a text-based PDF.",
                "PDF_SCANNED");

        var best = ParseText(raw);
        var union = ParseUnionBankText(raw);
        if (union.Count > best.Count) best = union;

        if (best.Count >= 1) return best;

        throw new BsaException(
            "We could extract text from this PDF, but couldn't detect transaction rows reliably. Please export CSV/XLSX from netbanking (recommended) or share a text-based PDF statement.",
            "PDF_PARSE_NO_ROWS");
    }

    /// <summary>Extract raw text from a PDF. Used for statements and CIBIL reports (with password).</summary>
    public static string ExtractPdfText(byte[] bytes, string? password = null, bool isCibil = false)
    {
        try
        {
            var sb = new StringBuilder();
            using var doc = string.IsNullOrWhiteSpace(password)
                ? PdfDocument.Open(bytes)
                : PdfDocument.Open(bytes, new ParsingOptions { Password = password.Trim() });
            foreach (var page in doc.GetPages())
            {
                foreach (var word in page.GetWords())
                    sb.Append(word.Text).Append(' ');
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            var msg = (e.Message ?? "").ToString();
            if (Regex.IsMatch(msg, "password", RegexOptions.IgnoreCase) || Regex.IsMatch(msg, "encrypted", RegexOptions.IgnoreCase))
            {
                if (isCibil)
                    throw new BsaException(
                        !string.IsNullOrWhiteSpace(password)
                            ? "Incorrect CIBIL PDF password. Please re-check and try again."
                            : "CIBIL PDF is password-protected. Please enter the PDF password and re-upload.",
                        "PDF_PASSWORD");
                throw new BsaException("This PDF is password-protected. Please upload an unlocked statement PDF or export CSV/XLSX.", "PDF_PASSWORD");
            }
            throw new BsaException($"PDF parsing failed: {msg}", "PDF_FAILED");
        }
    }

    // ── TEXT PARSER (generic) ───────────────────────────────────────

    private static readonly string[] HeaderPatterns =
    {
        @"date", @"tran.*id", @"utr", @"withdrawal|debit",
        @"deposit|credit", @"balance", @"narr|remark|desc",
    };

    private static int HeaderHits(string line) =>
        HeaderPatterns.Count(p => Regex.IsMatch(line, p, RegexOptions.IgnoreCase));

    private static readonly Regex DateRegex =
        new(@"\b(\d{4}-\d{2}-\d{2}|\d{2}[-/]\d{2}[-/]\d{2,4}|\d{2}[-/][A-Za-z]{3}[-/]\d{2,4}|\d{2}\s+[A-Za-z]{3}\s+\d{2,4}|\d{2}[A-Za-z]{3}\d{2,4})\b", RegexOptions.Compiled);

    public static List<BsaTransaction> ParseText(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var transactions = new List<BsaTransaction>();

        var headerLineIdx = -1;
        for (var i = 0; i < Math.Min(lines.Count, 30); i++)
        {
            if (HeaderHits(lines[i]) >= 3) { headerLineIdx = i; break; }
        }

        BsaTransaction? currentTx = null;

        for (var i = headerLineIdx >= 0 ? headerLineIdx + 1 : 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (Regex.IsMatch(line, @"^(?:page\s*(?:no)?\b|statement of account|account statement|customer id|account no|account type|branch|micr|ifsc|for any queries|savings account transactions)\b", RegexOptions.IgnoreCase))
                continue;

            if (HeaderHits(line) >= 3) continue;

            var dateMatch = DateRegex.Match(line);

            if (dateMatch.Success)
            {
                if (Regex.IsMatch(line, @"\b\d{2}\s+[A-Za-z]{3}\s+\d{4}\s*(?:to|-)\s*\d{2}\s+[A-Za-z]{3}\s+\d{4}\b", RegexOptions.IgnoreCase)) continue;

                var amounts = ExtractAmounts(line);
                var desc = Regex.Replace(line, DateRegex.ToString(), "").Trim();
                var descNoRow = Regex.Replace(desc, @"^[#\s]*\d+\s*", "").Trim();

                // Date-only row markers (Kotak / ICICI layouts)
                if (amounts.Count == 0 && descNoRow.Length < 3)
                {
                    var rowMarker = Regex.IsMatch(line, $@"^\s*\d+\s+{Regex.Escape(dateMatch.Groups[1].Value)}\b", RegexOptions.IgnoreCase);
                    if (rowMarker) continue;

                    var next = i + 1 < lines.Count ? lines[i + 1] : "";
                    var next2 = i + 2 < lines.Count ? lines[i + 2] : "";
                    var timeLike = Regex.IsMatch(next, @"^\d{1,2}:\d{2}(?::\d{2})?\s*(?:AM|PM)?$", RegexOptions.IgnoreCase);
                    var repeatsDate = next2.Length > 0 && DateRegex.IsMatch(next2) && next2.Contains(dateMatch.Groups[1].Value);
                    if (timeLike && repeatsDate) continue;
                }

                if (currentTx != null) transactions.Add(currentTx);

                decimal debit = 0, credit = 0, balance = 0;
                var pendingBalance = false;

                if (amounts.Count >= 2)
                {
                    balance = amounts[^1].Value;
                    var txAmt = amounts[^2];
                    if (Regex.IsMatch(line, @"withdrawal|debit|dr\b", RegexOptions.IgnoreCase)) debit = txAmt.Value;
                    else if (Regex.IsMatch(line, @"deposit|credit|cr\b", RegexOptions.IgnoreCase)) credit = txAmt.Value;
                    else if (txAmt.Sign > 0) credit = txAmt.Value;
                    else if (txAmt.Sign < 0) debit = txAmt.Value;
                    else pendingBalance = true;
                }
                else if (amounts.Count == 1)
                {
                    var txAmt = amounts[0];
                    if (Regex.IsMatch(line, @"withdrawal|debit|dr\b", RegexOptions.IgnoreCase) || txAmt.Sign < 0) debit = txAmt.Value;
                    else if (Regex.IsMatch(line, @"deposit|credit|cr\b", RegexOptions.IgnoreCase) || txAmt.Sign > 0) credit = txAmt.Value;
                    else pendingBalance = true;
                    balance = 0;
                    pendingBalance = true;
                }
                else
                {
                    pendingBalance = true;
                }

                var isBalanceOnly = Regex.IsMatch(descNoRow, @"\bB/F\b|OPENING\s*BAL", RegexOptions.IgnoreCase) && debit == 0 && credit == 0;

                currentTx = new BsaTransaction
                {
                    Date = BsaAnalyzer.ParseDate(dateMatch.Groups[1].Value),
                    Description = descNoRow.Length > 0 ? descNoRow : desc,
                    Debit = debit,
                    Credit = credit,
                    Balance = balance,
                    Raw = line,
                    SourceType = "text",
                    SourceRow = i + 1,
                    SourceExcerpt = line,
                    PendingBalance = pendingBalance,
                    IsBalanceOnly = isBalanceOnly,
                };
            }
            else if (currentTx != null)
            {
                var amounts = ExtractAmounts(line);
                if (amounts.Count > 0)
                {
                    if (amounts.Count >= 2)
                    {
                        if (currentTx.Balance == 0 || currentTx.PendingBalance)
                            currentTx.Balance = amounts[^1].Value != 0 ? amounts[^1].Value : currentTx.Balance;
                        var txAmt = amounts[^2];
                        if (currentTx.Debit == 0 && currentTx.Credit == 0 && txAmt.Value != 0)
                        {
                            if (Regex.IsMatch(line, @"deposit|credit|cr\b", RegexOptions.IgnoreCase) || txAmt.Sign > 0) currentTx.Credit = txAmt.Value;
                            else if (Regex.IsMatch(line, @"withdrawal|debit|dr\b", RegexOptions.IgnoreCase) || txAmt.Sign < 0) currentTx.Debit = txAmt.Value;
                        }
                        currentTx.PendingBalance = false;
                    }
                    else
                    {
                        var one = amounts[0];
                        if (currentTx.Debit == 0 && currentTx.Credit == 0 && one.Value != 0)
                        {
                            if (Regex.IsMatch(line, @"deposit|credit|cr\b", RegexOptions.IgnoreCase) || one.Sign > 0) currentTx.Credit = one.Value;
                            else if (Regex.IsMatch(line, @"withdrawal|debit|dr\b", RegexOptions.IgnoreCase) || one.Sign < 0) currentTx.Debit = one.Value;
                            else currentTx.Debit = one.Value;
                            currentTx.PendingBalance = true;
                        }
                        else if ((currentTx.Balance == 0 || currentTx.PendingBalance) && one.Value != 0)
                        {
                            currentTx.Balance = one.Value;
                            currentTx.PendingBalance = false;
                        }
                    }
                }
                // Continuation line (description, UTR, remarks)
                if (!Regex.IsMatch(line, @"^\d+$") && line.Length > 2)
                {
                    currentTx.Description = (currentTx.Description + " " + line).Trim();
                    currentTx.SourceRow = i + 1;
                    currentTx.SourceExcerpt = (currentTx.SourceExcerpt + "\n" + line).Trim();
                }
            }
        }
        if (currentTx != null) transactions.Add(currentTx);

        return FinishTransactions(transactions);
    }

    private static List<BsaTransaction> FinishTransactions(List<BsaTransaction> transactions)
    {
        // Post-process: fill debit/credit using balance deltas
        decimal? lastBalance = null;
        foreach (var tx in transactions)
        {
            var prevBalance = lastBalance;
            if (prevBalance != null && tx.Balance != 0 && tx.Debit == 0 && tx.Credit == 0)
            {
                var delta = tx.Balance - prevBalance.Value;
                if (delta > 0) { tx.Credit = delta; tx.Debit = 0; }
                else if (delta < 0) { tx.Debit = Math.Abs(delta); tx.Credit = 0; }
            }
            if (tx.Balance != 0) lastBalance = tx.Balance;
            tx.Description = CleanDesc(tx.Description.Length > 0 ? tx.Description : tx.Raw);
        }

        var cleaned = transactions
            .Where(t => t.Date.Length > 0 && !t.IsBalanceOnly && (t.Debit > 0 || t.Credit > 0 || t.Balance > 0))
            .ToList();

        var seen = new HashSet<string>();
        var uniq = new List<BsaTransaction>();
        foreach (var t in cleaned)
        {
            var key = $"{t.Date}|{t.Debit.ToString("0.00", CultureInfo.InvariantCulture)}|{t.Credit.ToString("0.00", CultureInfo.InvariantCulture)}|{t.Balance.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (!seen.Add(key)) continue;
            uniq.Add(t);
        }
        return uniq;
    }

    // ── UNION BANK SPECIFIC PARSER ──────────────────────────────────

    public static List<BsaTransaction> ParseUnionBankText(string text)
    {
        var transactions = new List<BsaTransaction>();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var lineDateRe = new Regex(@"^(\d{2}[-/](?:\d{2}|[A-Za-z]{3})[-/]\d{2,4})");

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var dateMatch = lineDateRe.Match(line);
            if (!dateMatch.Success) { i++; continue; }

            var date = BsaAnalyzer.ParseDate(dateMatch.Groups[1].Value);
            var descLines = new List<string>();
            decimal debit = 0, credit = 0, balance = 0;
            var sourceStart = i + 1;
            var sourceEnd = i + 1;
            i++;

            while (i < lines.Count)
            {
                var l = lines[i];
                if (lineDateRe.IsMatch(l)) break;

                if (Regex.IsMatch(l, @"^\d{2}:\d{2}(:\d{2})?$")) { sourceEnd = i + 1; i++; continue; }

                var amounts = MoneyRe.Matches(l).Select(m => BsaAnalyzer.ParseAmount(m.Value)).ToList();

                if (amounts.Count >= 2)
                {
                    balance = amounts[^1];
                    var txAmt = amounts[^2];
                    if (Regex.IsMatch(l, @"cr\b|credit", RegexOptions.IgnoreCase)) credit = txAmt;
                    else debit = txAmt;
                    descLines.Add(Regex.Replace(l, @"[\d][\d,\s]*\.\d{2}", "").Trim());
                    sourceEnd = i + 1;
                    i++;
                    break;
                }

                if (l.Length > 0 && !Regex.IsMatch(l, @"^page no\d*", RegexOptions.IgnoreCase)) descLines.Add(l);
                sourceEnd = i + 1;
                i++;
            }

            var description = Regex.Replace(string.Join(" ", descLines), @"\s+", " ").Trim();
            if (date.Length > 0 && description.Length > 0)
            {
                transactions.Add(new BsaTransaction
                {
                    Date = date,
                    Description = CleanDesc(description),
                    Debit = debit,
                    Credit = credit,
                    Balance = balance,
                    Raw = description,
                    SourceType = "unionbank-text",
                    SourceRow = sourceStart,
                    SourceExcerpt = string.Join("\n", lines.Skip(Math.Max(0, sourceStart - 1)).Take(sourceEnd - sourceStart + 1)),
                });
            }
        }

        // Fix debit/credit using balance deltas
        for (var j = 0; j < transactions.Count; j++)
        {
            var tx = transactions[j];
            var prev = j > 0 ? transactions[j - 1].Balance : 0;
            if (j > 0 && tx.Balance > 0)
            {
                var delta = tx.Balance - prev;
                if (delta > 0) { tx.Credit = delta; tx.Debit = 0; }
                else if (delta < 0) { tx.Debit = Math.Abs(delta); tx.Credit = 0; }
            }
        }

        var cleaned = transactions.Where(t => t.Date.Length > 0 && (t.Debit > 0 || t.Credit > 0)).ToList();
        var seen = new HashSet<string>();
        var uniq = new List<BsaTransaction>();
        foreach (var t in cleaned)
        {
            var key = $"{t.Date}|{t.Debit.ToString("0.00", CultureInfo.InvariantCulture)}|{t.Credit.ToString("0.00", CultureInfo.InvariantCulture)}|{t.Balance.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (!seen.Add(key)) continue;
            uniq.Add(t);
        }
        return uniq;
    }

    // ── CSV PARSER ──────────────────────────────────────────────────

    private static List<BsaTransaction> ParseCsv(byte[] bytes)
    {
        var content = Encoding.UTF8.GetString(bytes);
        var lines = SplitCsvLines(content);
        if (lines.Count == 0) return new List<BsaTransaction>();

        // Detect delimiter (comma vs semicolon vs tab)
        var delimiter = ",";
        foreach (var candidate in new[] { ",", ";", "\t" })
        {
            if (lines[0].CountOccurrences(candidate) > lines[0].CountOccurrences(delimiter)) delimiter = candidate;
        }

        var header = SplitCsvLine(lines[0], delimiter);
        var rows = new List<BsaTransaction>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var cells = SplitCsvLine(lines[i], delimiter);
            var row = new Dictionary<string, string>();
            for (var c = 0; c < header.Count; c++)
                row[header[c]] = c < cells.Count ? cells[c] : "";
            var tx = NormalizeRow(row);
            if (tx.Date.Length > 0 && (tx.Debit > 0 || tx.Credit > 0))
            {
                tx.SourceType = "csv";
                tx.SourceRow = i + 2;
                tx.SourceExcerpt = string.Join("|", cells);
                rows.Add(tx);
            }
        }
        return rows;
    }

    private static List<string> SplitCsvLines(string content) =>
        content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();

    private static List<string> SplitCsvLine(string line, string delimiter)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes && delimiter == ",") { result.Add(sb.ToString()); sb.Clear(); }
            else if (!inQuotes && ((delimiter == ";" && ch == ';') || (delimiter == "\t" && ch == '\t')))
            { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ── XLSX PARSER (ClosedXML) ─────────────────────────────────────

    private static List<BsaTransaction> ParseXlsx(byte[] bytes)
    {
        var allTx = new List<BsaTransaction>();
        using var ms = new MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(ms);
        foreach (var ws in workbook.Worksheets)
        {
            var range = ws.RangeUsed();
            if (range == null) continue;
            var rows = range.RowsUsed().ToList();
            if (rows.Count < 2) continue;

            var grid = rows.Select(r => r.Cells().Select(c => c.GetString() ?? "").ToList()).ToList();

            var headerIdx = 0;
            for (var i = 0; i < Math.Min(grid.Count, 10); i++)
            {
                var row = grid[i].Select(c => c.ToLowerInvariant()).ToList();
                if (row.Any(c => c.Contains("date")) && row.Any(c => c.Contains("amount") || c.Contains("debit") || c.Contains("credit")))
                { headerIdx = i; break; }
            }

            var headers = grid[headerIdx].Select(c => c.ToLowerInvariant().Trim()).ToList();

            for (var i = headerIdx + 1; i < grid.Count; i++)
            {
                var row = new Dictionary<string, string>();
                for (var j = 0; j < headers.Count; j++)
                    row[headers[j]] = j < grid[i].Count ? grid[i][j] : "";
                var tx = NormalizeRow(row);
                if (tx.Date.Length > 0 && (tx.Debit > 0 || tx.Credit > 0))
                {
                    tx.SourceType = "xlsx";
                    tx.SourceSheet = ws.Name;
                    tx.SourceRow = i + 1;
                    tx.SourceExcerpt = string.Join("|", grid[i]);
                    allTx.Add(tx);
                }
            }
        }
        return allTx;
    }

    // ── ROW NORMALIZER ──────────────────────────────────────────────

    private static BsaTransaction NormalizeRow(Dictionary<string, string> row)
    {
        var keys = row.Keys.ToList();

        string Find(params string[] patterns)
        {
            foreach (var p in patterns)
            {
                var k = keys.FirstOrDefault(key => Regex.IsMatch(key, p, RegexOptions.IgnoreCase));
                if (k != null && row.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        var dateRaw = Find("date", "txn.*date", "tran.*date", "value.*date", "posting.*date");
        var desc = Find("narr", "remark", "desc", "particular", "detail", "transac.*desc", "tran.*desc", "memo");
        var debitRaw = Find("withdrawal", "debit", "^dr$", "dr", "debit.*amount", "dr.*amount", "amount.*dr");
        var creditRaw = Find("deposit", "credit", "^cr$", "cr", "credit.*amount", "cr.*amount", "amount.*cr");
        var balanceRaw = Find("balance", "closing.*bal", "avail.*bal", "running.*bal");
        var amountRaw = Find("^amount$", "^amt$", "amount", "amt", "transaction.*amount", "txn.*amount");

        var debit = BsaAnalyzer.ParseAmount(debitRaw);
        var credit = BsaAnalyzer.ParseAmount(creditRaw);

        if (debit == 0 && credit == 0 && amountRaw.Length > 0)
        {
            var typeCol = Find("type", "dr.*cr", "cr.*dr", "tran.*type");
            var typeVal = typeCol.ToUpperInvariant();
            var amt = BsaAnalyzer.ParseAmount(amountRaw);
            if (typeVal.Contains("DR") || typeVal.Contains("DEBIT") || typeVal.Contains("WD")) debit = Math.Abs(amt);
            else if (typeVal.Contains("CR") || typeVal.Contains("CREDIT") || typeVal.Contains("DEP")) credit = Math.Abs(amt);
            else if (amt < 0) debit = Math.Abs(amt);
            else credit = Math.Abs(amt);
        }

        return new BsaTransaction
        {
            Date = BsaAnalyzer.ParseDate(dateRaw),
            Description = CleanDesc(desc),
            Debit = debit,
            Credit = credit,
            Balance = BsaAnalyzer.ParseAmount(balanceRaw),
            Raw = string.Join("|", row.Select(kv => $"{kv.Key}={kv.Value}")),
        };
    }

    private static string CleanDesc(string desc)
    {
        var cleaned = Regex.Replace(desc ?? "", @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"[^\x20-\x7E\u0900-\u097F]", "");
        return cleaned.Trim().TruncTo(200);
    }

    private static string TruncTo(this string s, int len) => s.Length <= len ? s : s[..len];

    private static int CountOccurrences(this string s, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = s.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
