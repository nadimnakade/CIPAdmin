using System.Globalization;
using System.Text.RegularExpressions;

namespace CIPAdmin.Backend.Bsa;

/// <summary>
/// C# port of the BSA (Bank Statement Analyzer) v4.0 rule engine (analyzer.js).
/// Rule-based, no AI: salary/EMI/loan/rent/SIP/insurance/credit-card detection,
/// monthly insights, CIBIL report text parsing, cross-verification and underwriting.
/// </summary>
public static partial class BsaAnalyzer
{
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── LOAN TYPE MAP (RBI classification) ──────────────────────────
    private sealed record LoanType(Regex Regex, string Code, string Label, string Color, string Icon);
    private static readonly LoanType[] LoanTypeMap =
    {
        new(Rx(@"HOME.*LOAN|HOUSING.*LOAN|MORTGAGE|HFL|HOMELOAN|\bHL\b|GRUH|NHB|LICHFL|LIC.*HFL"), "HL", "Home Loan", "blue", "🏠"),
        new(Rx(@"LAP\b|LOAN.*AGAINST.*PROP|PROP.*LOAN|MORTGAGE.*LOAN"), "LAP", "Loan Against Property", "blue", "🏢"),
        new(Rx(@"CAR.*LOAN|AUTO.*LOAN|VEHICLE.*LOAN|TWO.*WHEEL|\bVL\b|CARLOAN|BIKE.*LOAN"), "VL", "Vehicle Loan", "amber", "🚗"),
        new(Rx(@"EDUCATION.*LOAN|EDU.*LOAN|STUDENT.*LOAN|\bEL\b|VIDYA|SCHOLAR"), "EL", "Education Loan", "teal", "🎓"),
        new(Rx(@"BUSINESS.*LOAN|\bBL\b|MSME|SME.*LOAN|WORKING.*CAP|TERM.*LOAN|MUDRA|COMMERCIAL"), "BL", "Business Loan", "purple", "💼"),
        new(Rx(@"GOLD.*LOAN|\bGL\b|MUTHOOT|MANAPPURAM|PLEDGED.*GOLD"), "GL", "Gold Loan", "amber", "🥇"),
        new(Rx(@"CONSUMER.*DUR|\bCDL\b|DURABLE.*LOAN|NO.*COST.*EMI"), "CDL", "Consumer Durable Loan", "teal", "📱"),
        new(Rx(@"AGRI.*LOAN|KISAN|CROP.*LOAN|\bKCC\b|FARM.*LOAN|\bAGRI\b"), "AL", "Agriculture Loan", "green", "🌾"),
        new(Rx(@"OVERDRAFT|\bOD\b|CREDIT.*LINE|CASH.*CREDIT"), "OD", "Overdraft / Credit Line", "red", "🔄"),
        new(Rx(@"PERSONAL.*LOAN|\bPL\b|PERSLOAN|CONSUMER.*LOAN|UNSECURED"), "PL", "Personal Loan", "red", "👤"),
    };

    // ── LENDER REGISTRY (100+ Indian banks/NBFCs) ───────────────────
    private sealed record Lender(Regex Regex, string Name, string Category);
    private static readonly Lender[] LenderPatterns =
    {
        new(Rx(@"\bSBIN\b|STATE.*BANK.*INDIA|\bSBI\b"), "State Bank of India (SBI)", "PSB"),
        new(Rx(@"\bUBIN\b|UNION.*BANK"), "Union Bank of India", "PSB"),
        new(Rx(@"\bPUNB\b|PUNJAB.*NATIONAL|\bPNB\b"), "Punjab National Bank (PNB)", "PSB"),
        new(Rx(@"\bBARB\b|BANK.*BARODA|\bBOB\b"), "Bank of Baroda (BOB)", "PSB"),
        new(Rx(@"\bCNRB\b|CANARA.*BANK"), "Canara Bank", "PSB"),
        new(Rx(@"\bIOBA\b|INDIAN.*OVERSEAS"), "Indian Overseas Bank (IOB)", "PSB"),
        new(Rx(@"\bBKID\b|BANK.*INDIA\b"), "Bank of India (BOI)", "PSB"),
        new(Rx(@"\bMAHB\b|BANK.*MAHARASHTRA"), "Bank of Maharashtra", "PSB"),
        new(Rx(@"CENTRAL.*BANK.*INDIA|\bCBIN\b"), "Central Bank of India", "PSB"),
        new(Rx(@"\bUCO\b|UCO.*BANK"), "UCO Bank", "PSB"),
        new(Rx(@"\bHDFC\b|HDFCBANK"), "HDFC Bank", "Pvt"),
        new(Rx(@"\bICIC\b|\bICICI\b"), "ICICI Bank", "Pvt"),
        new(Rx(@"\bUTIB\b|AXIS.*BANK"), "Axis Bank", "Pvt"),
        new(Rx(@"\bKKBK\b|KOTAK"), "Kotak Mahindra Bank", "Pvt"),
        new(Rx(@"\bYESB\b|YES.*BANK"), "Yes Bank", "Pvt"),
        new(Rx(@"\bINDB\b|INDUS.*IND"), "IndusInd Bank", "Pvt"),
        new(Rx(@"\bFDRL\b|FEDERAL.*BANK"), "Federal Bank", "Pvt"),
        new(Rx(@"\bIBKL\b|\bIDFC\b"), "IDFC First Bank", "Pvt"),
        new(Rx(@"\bIDIB\b|\bIDBI\b"), "IDBI Bank", "Pvt"),
        new(Rx(@"\bRBLB\b|\bRBL\b"), "RBL Bank", "Pvt"),
        new(Rx(@"\bBAND\b|BANDHAN"), "Bandhan Bank", "Pvt"),
        new(Rx(@"\bAUBK\b|AU.*SMALL|AU.*FINANCE"), "AU Small Finance Bank", "SFB"),
        new(Rx(@"\bSRCB\b|SARASWAT"), "Saraswat Bank", "Coop"),
        new(Rx(@"\bDNSB\b|DOMBIVLI"), "Dombivli Nagari Sahakari Bank", "Coop"),
        new(Rx(@"\bKJSB\b|KALYAN.*JANATA"), "Kalyan Janata Sahakari Bank", "Coop"),
        new(Rx(@"\bTDCB\b"), "Thane District Central Bank", "Coop"),
        new(Rx(@"SMFG|SMFGINDIACR|SMFG.*INDIA"), "SMFG India Credit", "NBFC"),
        new(Rx(@"BAJAJ.*FIN|BAJAJFIN"), "Bajaj Finance Ltd", "NBFC"),
        new(Rx(@"TATA.*CAP|TATACAP"), "Tata Capital", "NBFC"),
        new(Rx(@"ADITYA.*BIRLA|ABFL"), "Aditya Birla Finance", "NBFC"),
        new(Rx(@"FULLERTON|FULLERTONIND"), "Fullerton India Credit", "NBFC"),
        new(Rx(@"L&T.*FIN|LNTFIN"), "L&T Finance", "NBFC"),
        new(Rx(@"MUTHOOT"), "Muthoot Finance", "NBFC"),
        new(Rx(@"MANAPPURAM"), "Manappuram Finance", "NBFC"),
        new(Rx(@"SHRIRAM.*FIN|SHRIRAM.*TRANS"), "Shriram Finance", "NBFC"),
        new(Rx(@"MAHINDRA.*FIN|MAHFIN"), "Mahindra Finance", "NBFC"),
        new(Rx(@"\bCHOLA\b|CHOLAMANDALAM"), "Cholamandalam Finance", "NBFC"),
        new(Rx(@"HERO.*FINCORP|HEROFIN"), "Hero FinCorp", "NBFC"),
        new(Rx(@"FINAGLE"), "Finagle (Kotak)", "Fintech"),
        new(Rx(@"\bIIFL\b|INDIA.*INFOLINE"), "IIFL Finance", "NBFC"),
        new(Rx(@"PIRAMAL|PIRAMALMORT|PIRAMAL.*CAP"), "Piramal Capital & Housing Finance", "HFC"),
        new(Rx(@"LICHFL|LIC.*HFL|LIC.*HOUSING"), "LIC Housing Finance", "HFC"),
        new(Rx(@"INDIABULL.*HOUSING|IBHFL"), "Indiabulls Housing Finance", "HFC"),
        new(Rx(@"\bPPIW\b|PAYTM.*PAYMENTS"), "Paytm Payments Bank", "PB"),
        new(Rx(@"\bFINO\b"), "Fino Payments Bank", "PB"),
    };

    // ── SALARY KEYWORDS ─────────────────────────────────────────────
    private static readonly string[] SalaryKeywords =
    {
        "SALARY", "PAYROLL", "WAGES", "STIPEND", "BONUS", "INCENTIVE",
        "SAL/", @"SAL ", @"NEFT.*SALARY", @"IMPS.*SALARY", @"RTGS.*SALARY",
        @"NACH.*SALARY", @"NACH.*PAYROLL",
    };

    private static readonly Dictionary<string, string> SalarySourceMap = new()
    {
        ["AI AIRPORT"] = "AI Airport Services Ltd", ["AIAIRPORT"] = "AI Airport Services Ltd",
        ["INFOSYS"] = "Infosys BPO Ltd", ["WIPRO"] = "Wipro Ltd",
        ["TCS"] = "Tata Consultancy Services", ["HCL"] = "HCL Technologies",
        ["COGNIZANT"] = "Cognizant Technology", ["ACCENTURE"] = "Accenture",
    };

    // ── NACH / ECS PATTERNS ─────────────────────────────────────────
    private static readonly Regex[] NachPatterns =
    {
        Rx(@"^NACH/"), Rx(@"^NACH\s"), Rx(@"^ECS/"), Rx(@"^ECS\s"),
        Rx(@"^ACH/"), Rx(@"^EMANCH/"), Rx(@"^SI/"), Rx(@"^SI\s"),
        Rx(@"E-?MANDATE"), Rx(@"\bMANDATE\b"), Rx(@"AUTO\s*DEBIT"),
        Rx(@"NACH.*DEBIT"), Rx(@"ECS.*DEBIT"),
    };

    // ── APP LOAN PATTERNS ───────────────────────────────────────────
    private sealed record AppLoan(Regex Regex, string Name, int MaxApr);
    private static readonly AppLoan[] AppLoanPatterns =
    {
        new(Rx(@"KREDITBEE|KREDIT.*BEE"), "KreditBee", 36),
        new(Rx(@"LAZYPAY|LAZY.*PAY"), "LazyPay", 36),
        new(Rx(@"MONEYTAP|MONEY.*TAP"), "MoneyTap", 36),
        new(Rx(@"\bNAVI\b"), "Navi", 36),
        new(Rx(@"MPOKKET|M.*POKKET"), "mPokket", 48),
        new(Rx(@"PAYSENSE|PAY.*SENSE"), "PaySense", 36),
        new(Rx(@"\bCASHE\b"), "CASHe", 36),
        new(Rx(@"EARLYSALARY|EARLY.*SALARY|\bFIBE\b"), "Fibe", 36),
        new(Rx(@"STASHFIN"), "StashFin", 36),
        new(Rx(@"\bSLICE\b"), "Slice", 30),
        new(Rx(@"\bJUPITER\b"), "Jupiter", 30),
        new(Rx(@"\bNIRA\b"), "Nira", 36),
        new(Rx(@"SMARTCOIN"), "SmartCoin", 48),
        new(Rx(@"KISSHT|RING.*LOAN"), "Kissht/Ring", 36),
        new(Rx(@"\bFREO\b|FREOPAY"), "Freo", 30),
        new(Rx(@"ZESTMONEY|ZEST.*MONEY"), "ZestMoney", 36),
        new(Rx(@"AXIO|CAPITALFLOAT"), "Axio", 36),
        new(Rx(@"\bDHANI\b"), "Dhani", 42),
        new(Rx(@"INDIALENDS"), "IndiaLends", 36),
        new(Rx(@"LOANTAP"), "LoanTap", 36),
        new(Rx(@"KRAZYBEE"), "KrazyBee", 48),
        new(Rx(@"RUPEEK"), "Rupeek", 24),
        new(Rx(@"BHARATPE.*LOAN"), "BharatPe Loan", 36),
        new(Rx(@"LENDINGKART"), "Lendingkart", 36),
        new(Rx(@"FINNABLE"), "Finnable", 36),
        new(Rx(@"OXYZO"), "Oxyzo", 24),
        new(Rx(@"INDIFI"), "Indifi", 36),
        new(Rx(@"FAIRCENT"), "Faircent", 36),
        new(Rx(@"MONEYVIEW"), "MoneyView", 36),
        new(Rx(@"\bPREFR\b"), "Prefr", 36),
        new(Rx(@"FINAGLE"), "Finagle", 40),
    };

    // ── SIP / MF PLATFORMS ──────────────────────────────────────────
    private sealed record Sip(Regex Regex, string Platform);
    private static readonly Sip[] SipPatterns =
    {
        new(Rx(@"GROWW"), "Groww"),
        new(Rx(@"ZERODHA.*COIN|ZERODHA.*MF"), "Zerodha Coin"),
        new(Rx(@"PAYTM.*MONEY|PAYTMMONEY"), "Paytm Money"),
        new(Rx(@"\bCAMS\b|CAMSONLINE"), "CAMS"),
        new(Rx(@"BSE.*STAR|BSESTAR"), "BSE Star MF"),
        new(Rx(@"MF.*UTIL|MFUTILITY"), "MF Utility"),
        new(Rx(@"MIRAE"), "Mirae Asset MF"),
        new(Rx(@"NIPPON.*MF|NIPPON.*INDIA"), "Nippon India MF"),
        new(Rx(@"AXIS.*MF"), "Axis MF"),
        new(Rx(@"HDFC.*MF"), "HDFC MF"),
        new(Rx(@"SBI.*MF"), "SBI MF"),
        new(Rx(@"ICICI.*MF|ICICI.*PRU.*MF"), "ICICI Prudential MF"),
        new(Rx(@"KOTAK.*MF"), "Kotak MF"),
        new(Rx(@"DSP\b.*MF|DSP.*MUTUAL"), "DSP MF"),
        new(Rx(@"CANARA.*ROB|CANROB"), "Canara Robeco MF"),
        new(Rx(@"FRANKLIN.*TEMP|FRANKLINTEMP"), "Franklin Templeton MF"),
        new(Rx(@"MOTILAL.*OSWAL|MOTILAL"), "Motilal Oswal MF"),
        new(Rx(@"INVESCO"), "Invesco MF"),
        new(Rx(@"EDELWEISS.*MF"), "Edelweiss MF"),
        new(Rx(@"MUTUAL.*FUND|MF.*SIP|SIP.*MF"), "Mutual Fund"),
    };

    // ── STOCK MARKET / BROKING PLATFORMS ────────────────────────────
    private static readonly Sip[] StockMarketPatterns =
    {
        new(Rx(@"ZERODHA|KITE\b|KITEAPP|\bCOIN\b"), "Zerodha"),
        new(Rx(@"UPSTOX|UPSTX"), "Upstox"),
        new(Rx(@"ANGEL\s*ONE|ANGEL\s*BROKING|ANGELBRK"), "Angel One"),
        new(Rx(@"FYERS"), "Fyers"),
        new(Rx(@"5PAISA|FIVE\s*PAISA"), "5paisa"),
        new(Rx(@"\bDHAN\b|RAISE\s*FIN"), "Dhan"),
        new(Rx(@"ALICE\s*BLUE"), "Alice Blue"),
        new(Rx(@"SHOONYA|FINVASIA"), "Shoonya"),
        new(Rx(@"PROSTOCKS"), "ProStocks"),
        new(Rx(@"\bSAMCO\b"), "Samco"),
        new(Rx(@"INDMONEY"), "INDmoney"),
        new(Rx(@"ICICI\s*DIRECT|ICICIDIRECT"), "ICICI Direct"),
        new(Rx(@"HDFC\s*SEC|HDFCSEC|HDFC\s*SKY"), "HDFC Securities"),
        new(Rx(@"KOTAK\s*SEC|KOTAKSEC|KOTAK\s*NEO"), "Kotak Securities"),
        new(Rx(@"AXIS\s*DIRECT|AXISDIRECT"), "Axis Direct"),
        new(Rx(@"SBI\s*SEC|SBI\s*SECURITIES"), "SBI Securities"),
        new(Rx(@"NUVAMA|EDELWEISS\s*BROKING|EDELWEISS\s*SEC"), "Nuvama"),
        new(Rx(@"SHAREKHAN"), "Sharekhan"),
        new(Rx(@"MOTILAL\s*OSWAL|MOSL"), "Motilal Oswal"),
        new(Rx(@"VENTURA"), "Ventura Wealth"),
        new(Rx(@"\bGEOJIT\b"), "Geojit"),
        new(Rx(@"ANAND\s*RATHI"), "Anand Rathi"),
        new(Rx(@"NIRMAL\s*BANG"), "Nirmal Bang"),
        new(Rx(@"RELIGARE"), "Religare"),
        new(Rx(@"ARIHANT\s*CAP"), "Arihant Capital"),
        new(Rx(@"BONANZA"), "Bonanza"),
        new(Rx(@"CHOICE\s*FINX|CHOICE\s*BROKING"), "Choice FinX"),
    };

    // ── INSURANCE PATTERNS ──────────────────────────────────────────
    private sealed record Insurance(Regex Regex, string Provider, string Type);
    private static readonly Insurance[] InsurancePatterns =
    {
        new(Rx(@"\bLIC\b|LICINDIA|LIC.*PREM"), "LIC", "life"),
        new(Rx(@"HDFC.*LIFE|HDFCLIFE"), "HDFC Life", "life"),
        new(Rx(@"ICICI.*PRU.*LIFE|ICICIPRULIFE"), "ICICI Prudential Life", "life"),
        new(Rx(@"SBI.*LIFE|SBILIFE"), "SBI Life", "life"),
        new(Rx(@"MAX.*LIFE|MAXLIFE"), "Max Life", "life"),
        new(Rx(@"BAJAJ.*ALLIANZ|BAJAJALLIANZ"), "Bajaj Allianz", "life"),
        new(Rx(@"KOTAK.*LIFE|KOTAKLIFE"), "Kotak Life", "life"),
        new(Rx(@"TATA.*AIA|TATAAIA"), "Tata AIA Life", "life"),
        new(Rx(@"STAR.*HEALTH|STARHEALTH"), "Star Health", "health"),
        new(Rx(@"NIVA.*BUPA|NIVABUPA"), "Niva Bupa", "health"),
        new(Rx(@"CARE.*HEALTH|CAREHEALTH"), "Care Health", "health"),
        new(Rx(@"NEW.*INDIA.*ASSU|NEWINDIAASSUR"), "New India Assurance", "health"),
        new(Rx(@"NATIONAL.*INSURANCE"), "National Insurance", "health"),
        new(Rx(@"DIGIT.*INSURANCE|\bDIGIT\b"), "Digit Insurance", "general"),
        new(Rx(@"RELIANCE.*GENERAL"), "Reliance General Insurance", "general"),
        new(Rx(@"PMSBY"), "PMSBY (Govt)", "accidental"),
        new(Rx(@"PMJJBY"), "PMJJBY (Govt)", "life"),
        new(Rx(@"INSURANCE.*PREM|INS.*PREM|PREM.*INS"), "Insurance Premium", "general"),
    };

    // ── UTILITY PATTERNS ────────────────────────────────────────────
    private sealed record Utility(Regex Regex, string Category);
    private static readonly Utility[] UtilityPatterns =
    {
        new(Rx(@"MSEDCL|MSEB\b|MAHAVITARAN|MAHAVITRAN"), "electricity"),
        new(Rx(@"BSES\b|BESCOM|TPDDL|TNEB|WBSEDCL|CESC\b|APEPDCL"), "electricity"),
        new(Rx(@"JIO\b|JIOFIBER|JIONET"), "internet/mobile"),
        new(Rx(@"AIRTEL\b|BHARTI.*AIRTEL"), "mobile"),
        new(Rx(@"VODAFONE|VODA\b"), "mobile"),
        new(Rx(@"\bBSNL\b"), "mobile"),
        new(Rx(@"GOOGLE.*UTIL|gpay.*utility|GOOGLE.*PAY.*UTIL"), "utility"),
        new(Rx(@"MAHANAGAR.*GAS|MGL\b|IGL\b|ADANI.*GAS|GAIL\b"), "gas"),
        new(Rx(@"NETFLIX|AMAZON.*PRIME|HOTSTAR|DISNEY\+|SONY.*LIV|ZEE5"), "OTT"),
        new(Rx(@"SWIGGY|ZOMATO"), "food delivery"),
        new(Rx(@"IBIBO|MAKEMYTRIP|GOIBIBO|YATRA\b|IXIGO"), "travel"),
        new(Rx(@"BBMP\b|MCGM\b|\bBMC\b|PROPERTY.*TAX"), "municipal tax"),
    };

    private static readonly string[] RentKeywords =
    {
        "RENT", "HOUSE RENT", "H RENT", "FLAT RENT", "ROOM RENT",
        "LANDLORD", "OWNER RENT", "MAKAN", "PG RENT", "HOSTEL RENT",
        "ACCOMMODATION", "LEASE RENT",
    };

    private static readonly Regex[] PfPatterns =
    {
        Rx(@"EPFO"), Rx(@"EMPLOYEE.*PROVIDENT"), Rx(@"PROVIDENT.*FUND"),
        Rx(@"\bEPF\b"), Rx(@"PF.*CREDIT"), Rx(@"PF.*SETTLE"), Rx(@"PROVIDENT"),
    };

    private sealed record GovtScheme(Regex Regex, string Name, string Type);
    private static readonly GovtScheme[] GovtSchemePatterns =
    {
        new(Rx(@"LADKI.*BAH|LADKI BAHIN"), "Ladki Bahin Yojana", "govt_scheme"),
        new(Rx(@"PM.*KISAN"), "PM Kisan", "govt_scheme"),
        new(Rx(@"JANDHAN"), "Jan Dhan Scheme", "govt_scheme"),
        new(Rx(@"\bDBT\b"), "DBT Credit", "govt_scheme"),
        new(Rx(@"APB/"), "Aadhaar Payment Bridge", "govt_scheme"),
    };

    private static readonly Regex[] DisbursementKeywords =
    {
        Rx(@"DISBURSE"), Rx(@"DISBUR"), Rx(@"DISBURSAL"),
        Rx(@"LOAN.*CREDIT"), Rx(@"LOAN.*PROCEED"), Rx(@"SANCTION.*AMT"),
        Rx(@"TERM.*LOAN.*CR"), Rx(@"PL.*DISBUR"),
    };

    // ════════════════════════════════════════════════════════════════
    // SHARED HELPERS (also used by the parser)
    // ════════════════════════════════════════════════════════════════

    public static decimal ParseAmount(object? value)
    {
        if (value == null) return 0;
        var s = value.ToString();
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Replace("₹", "").Trim();
        if (s.StartsWith("(") && s.EndsWith(")")) s = "-" + s[1..^1];
        s = Regex.Replace(s, @"[,\s]", "");
        s = Regex.Replace(s, @"(dr|cr)$", "", RegexOptions.IgnoreCase);
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public static string ParseDate(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return "";
        var s = Regex.Replace(str.ToString().Trim(), "[.,]", "");

        var iso = Regex.Match(s, @"^(\d{4})-(\d{2})-(\d{2})");
        if (iso.Success) return $"{iso.Groups[1].Value}-{iso.Groups[2].Value}-{iso.Groups[3].Value}";

        var dmyDash4 = Regex.Match(s, @"^(\d{2})-(\d{2})-(\d{4})");
        if (dmyDash4.Success) return $"{dmyDash4.Groups[3].Value}-{dmyDash4.Groups[2].Value}-{dmyDash4.Groups[1].Value}";

        var dmySlash4 = Regex.Match(s, @"^(\d{2})/(\d{2})/(\d{4})");
        if (dmySlash4.Success) return $"{dmySlash4.Groups[3].Value}-{dmySlash4.Groups[2].Value}-{dmySlash4.Groups[1].Value}";

        var dmyDash2 = Regex.Match(s, @"^(\d{2})-(\d{2})-(\d{2})");
        if (dmyDash2.Success) return $"20{dmyDash2.Groups[3].Value}-{dmyDash2.Groups[2].Value}-{dmyDash2.Groups[1].Value}";

        var dmySlash2 = Regex.Match(s, @"^(\d{2})/(\d{2})/(\d{2})");
        if (dmySlash2.Success) return $"20{dmySlash2.Groups[3].Value}-{dmySlash2.Groups[2].Value}-{dmySlash2.Groups[1].Value}";

        var dMonY = Regex.Match(s, @"^(\d{2})[-/\s]([A-Za-z]{3})[-/\s](\d{2,4})");
        if (dMonY.Success)
        {
            var mm = MonthNumber(dMonY.Groups[2].Value);
            var yyyy = dMonY.Groups[3].Value.Length == 2 ? "20" + dMonY.Groups[3].Value : dMonY.Groups[3].Value;
            if (mm != null) return $"{yyyy}-{mm}-{dMonY.Groups[1].Value}";
        }

        var dMonYCompact = Regex.Match(s, @"^(\d{2})([A-Za-z]{3})(\d{2,4})");
        if (dMonYCompact.Success)
        {
            var mm = MonthNumber(dMonYCompact.Groups[2].Value);
            var yyyy = dMonYCompact.Groups[3].Value.Length == 2 ? "20" + dMonYCompact.Groups[3].Value : dMonYCompact.Groups[3].Value;
            if (mm != null) return $"{yyyy}-{mm}-{dMonYCompact.Groups[1].Value}";
        }

        return s.Split(' ')[0];
    }

    private static string? MonthNumber(string mon) => mon.ToLowerInvariant() switch
    {
        "jan" => "01", "feb" => "02", "mar" => "03", "apr" => "04",
        "may" => "05", "jun" => "06", "jul" => "07", "aug" => "08",
        "sep" => "09", "oct" => "10", "nov" => "11", "dec" => "12",
        _ => null,
    };

    /// <summary>Display month key, e.g. "Jan 2026" (mirrors toLocaleDateString('en-IN')).</summary>
    public static string MonthKey(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return "Unknown";
        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        return dateStr.Length >= 7 ? dateStr[..7] : dateStr;
    }

    public static string Fmt(decimal n)
    {
        if (n == 0) return "0";
        var d = (double)n;
        if (Math.Abs(d) >= 10000000) return (d / 10000000).ToString("0.00") + "Cr";
        if (Math.Abs(d) >= 100000) return (d / 100000).ToString("0.00") + "L";
        if (Math.Abs(d) >= 1000) return (d / 1000).ToString("0.0") + "K";
        return d.ToString("0");
    }

    private static string Trunc(string s, int len) => s.Length <= len ? s : s[..len];

    private static string NormalizeDesc(string? desc) =>
        Regex.Replace(desc ?? "", @"\s+", " ").Trim();

    private static string NormUpper(string? desc) => NormalizeDesc(desc).ToUpperInvariant();

    private static bool AnyMatch(string desc, string[] patterns)
    {
        foreach (var p in patterns)
            if (Regex.IsMatch(desc, p, RegexOptions.IgnoreCase)) return true;
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    // DETECTION FUNCTIONS
    // ════════════════════════════════════════════════════════════════

    private static bool DetectNach(string desc) => NachPatterns.Any(p => p.IsMatch(desc));

    private static LoanType DetectLoanType(string desc)
    {
        foreach (var p in LoanTypeMap)
            if (p.Regex.IsMatch(desc)) return p;
        return new LoanType(Rx("PL"), "PL", "Personal Loan", "red", "👤");
    }

    private static Lender? DetectLender(string desc)
    {
        foreach (var p in LenderPatterns)
            if (p.Regex.IsMatch(desc)) return p;
        return null;
    }

    private static string InferAutoDebitLender(string desc)
    {
        var m = Regex.Match(desc ?? "", @"(?:^|\b)(?:NACH|ACH|ECS|SI|EMANCH)/([^/\s]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var token = Regex.Replace(m.Groups[1].Value, "[^A-Za-z0-9]", "");
        if (token.Length > 24) token = token[..24];
        return token.Length >= 3 ? token.ToUpperInvariant() : "";
    }

    private static string InferSalaryEmployer(string desc)
    {
        var upper = (desc ?? "").ToUpperInvariant();
        foreach (var (key, name) in SalarySourceMap)
            if (upper.Contains(key)) return name;

        var salaryPay = Regex.Match(desc, @"\bSALARY(?:\s*PAYMENT)?\s*/\s*([A-Z0-9 &.'-]{3,})", RegexOptions.IgnoreCase);
        if (salaryPay.Success) return Trunc(Regex.Replace(salaryPay.Groups[1].Value, @"\s+", " ").Trim(), 50);

        var nachMatch = Regex.Match(desc, @"NACH/\d+/\d+/(.+)", RegexOptions.IgnoreCase);
        if (nachMatch.Success) return Trunc(Regex.Replace(nachMatch.Groups[1].Value, "/", " ").Trim(), 50);

        var channelMatch = Regex.Match(desc, @"\b(NEFT|IMPS|RTGS)\b[:\s\-/]+(.+)", RegexOptions.IgnoreCase);
        if (channelMatch.Success)
        {
            var s = channelMatch.Groups[2].Value;
            s = Regex.Replace(s, @"\bUTR[:\s\-]*[A-Z0-9]{10,25}\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bRRN[:\s\-]*[0-9]{10,18}\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[A-Z]{4}0[A-Z0-9]{6}\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:CR|DR|CREDIT|DEBIT)\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:SALARY|PAYROLL|WAGES|STIPEND|BONUS|INCENTIVE)\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "[/-]+", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length > 0) return Trunc(s, 50);
        }

        var autoToken = InferAutoDebitLender(desc);
        if (autoToken.Length > 0) return autoToken;
        return "Salary Credit";
    }

    private static BsaMeta ExtractTxnMeta(string desc, BsaTransaction tx)
    {
        var text = desc ?? "";
        string Find(string pattern)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        var utr = Find(@"\bUTR[:\s\-]*([A-Z0-9]{10,25})\b");
        var rrn = Find(@"\bRRN[:\s\-]*([0-9]{10,18})\b");
        var nachRef = Find(@"\bNACH/\d{1,4}/\d{6,20}\b");
        var referenceId = Find(@"\bREF(?:ERENCE)?[:\s\-]*([A-Z0-9\-]{6,30})\b");
        if (referenceId.Length == 0) referenceId = nachRef;
        var txnId = Find(@"\bTRAN(?:SACTION)?\s*ID[:\s\-]*([A-Z0-9\-]{6,30})\b");
        if (txnId.Length == 0) txnId = Find(@"\bT\d{7,12}\b");
        var chequeNo = Find(@"\bCHQ(?:UE)?\s*(?:NO|NUMBER)?[:\s\-]*([0-9]{4,10})\b");
        var nachUmrn = Find(@"\bUMRN[:\s\-]*([A-Z0-9]{10,30})\b");
        var mandateId = Find(@"\bMANDATE[:\s\-]*([A-Z0-9\-]{6,30})\b");

        var loanAccountRaw = Find(@"\bLOAN\s*(?:A/C|AC|ACCOUNT)\s*(?:NO|NUMBER)?[:\s\-]*([Xx*]*\d{3,10})\b");
        if (loanAccountRaw.Length == 0) loanAccountRaw = Find(@"\bA/C\s*(?:NO|NUMBER)?[:\s\-]*([Xx*]*\d{3,10})\b");
        var digitsOnly = Regex.Replace(loanAccountRaw, "[^0-9Xx*]", "");
        var loanAccountMasked = loanAccountRaw.Length > 0 && digitsOnly.Length > 0
            ? (digitsOnly.Length > 10 ? digitsOnly[^10..] : digitsOnly)
            : "";

        var principalRaw = Find(@"\bPRIN(?:CIPAL)?[:\s\-]*([\d,]+\.\d{2})\b");
        var interestRaw = Find(@"\bINT(?:EREST)?[:\s\-]*([\d,]+\.\d{2})\b");
        var principalAmount = principalRaw.Length > 0 ? ParseAmount(principalRaw) : 0;
        var interestAmount = interestRaw.Length > 0 ? ParseAmount(interestRaw) : 0;

        var emiCycleM = Regex.Match(text.ToUpperInvariant(), @"\b(MONTHLY|MTHLY|QUARTERLY|WEEKLY)\b");
        var emiDayM = Regex.Match(text.ToUpperInvariant(), @"\bEMI(?:\s*ON)?\s*(\d{1,2})(?:ST|ND|RD|TH)?\b");

        return new BsaMetaCarrier
        {
            ReferenceId = referenceId.Length > 0 ? referenceId : null,
            Utr = utr.Length > 0 ? utr : null,
            Rrn = rrn.Length > 0 ? rrn : null,
            NachRef = nachRef.Length > 0 ? nachRef : null,
            NachUmrn = nachUmrn.Length > 0 ? nachUmrn : null,
            MandateId = mandateId.Length > 0 ? mandateId : null,
            TxnId = txnId.Length > 0 ? txnId : null,
            ChequeNo = chequeNo.Length > 0 ? chequeNo : null,
            LoanAccountMasked = loanAccountMasked.Length > 0 ? loanAccountMasked : null,
            LenderRaw = InferAutoDebitLender(text).Length > 0 ? InferAutoDebitLender(text) : null,
            EmiCycle = emiCycleM.Success ? emiCycleM.Groups[1].Value : null,
            EmiDay = emiDayM.Success ? emiDayM.Groups[1].Value : null,
            PrincipalAmount = principalAmount > 0 ? principalAmount : null,
            InterestAmount = interestAmount > 0 ? interestAmount : null,
            SourceType = tx.SourceType,
            SourceSheet = tx.SourceSheet,
            SourceRow = tx.SourceRow,
            SourcePage = tx.SourcePage,
        };
    }

    /// <summary>Internal carrier so ExtractTxnMeta can return a base BsaMeta to copy onto rows.</summary>
    private sealed class BsaMetaCarrier : BsaMeta { }

    private static void CopyMeta(BsaMeta source, BsaMeta target)
    {
        target.ReferenceId = source.ReferenceId;
        target.Utr = source.Utr;
        target.Rrn = source.Rrn;
        target.NachRef = source.NachRef;
        target.NachUmrn = source.NachUmrn;
        target.MandateId = source.MandateId;
        target.TxnId = source.TxnId;
        target.ChequeNo = source.ChequeNo;
        target.LoanAccountMasked = source.LoanAccountMasked;
        target.LenderRaw = source.LenderRaw;
        target.EmiCycle = source.EmiCycle;
        target.EmiDay = source.EmiDay;
        target.PrincipalAmount = source.PrincipalAmount;
        target.InterestAmount = source.InterestAmount;
        target.SourceType = source.SourceType;
        target.SourceSheet = source.SourceSheet;
        target.SourceRow = source.SourceRow;
        target.SourcePage = source.SourcePage;
    }

    private static string? DetectSalary(string desc, decimal amount, bool isCredit)
    {
        var d = NormalizeDesc(desc);
        if (!isCredit) return null;
        if (AnyMatch(d, SalaryKeywords)) return InferSalaryEmployer(d);

        if (DetectNach(d))
        {
            var negative = Rx(@"\b(EMI|INSTAL+MENT|SALARY|LOAN|REPAY|APY|PENSION|INSURANCE|PREM|SIP|MUTUAL|MF\b|CARD|CC\b|BILLDESK|DEMAT|DP\s*CHARGES|CDSL|NSDL|PAYOUT|PAYIN)\b");
            if (!negative.IsMatch(d) && amount >= 5000) return InferSalaryEmployer(d);
        }

        var channel = Rx(@"\b(NEFT|IMPS|RTGS)\b");
        if (channel.IsMatch(d) && amount >= 5000)
        {
            var salaryLike = Rx(@"\b(SALARY|PAYROLL|WAGES|STIPEND|BONUS|INCENTIVE|SAL/|\bSAL\b)\b");
            var negative = Rx(@"\b(LOAN|DISB|DISBURSE|SANCTION|OD\b|OVERDRAFT|CREDIT\s*LIMIT)\b");
            if (!negative.IsMatch(d) && salaryLike.IsMatch(d)) return InferSalaryEmployer(d);
        }

        return null;
    }

    private static (string Bank, string BankCategory, LoanType LoanType)? DetectLoanEmi(string desc, decimal amount)
    {
        if (Rx(@"\b(RETURN|INSUFFICIENT|SIGNATURE|ERRORS|DISCREPANCY|DAMAGE|OVERWRITING|CLOSED|FROZEN|BOUNCE|BOUNCED|FAILED|FAILURE|UNPAID|REJECT|REJECTED|CHARGE|CHARGES|PENALTY)\b").IsMatch(desc))
            return null;
        foreach (var p in LenderPatterns)
        {
            if (p.Regex.IsMatch(desc) && DetectNach(desc))
                return (p.Name, p.Category, DetectLoanType(desc));
        }
        var isAutoDebit = DetectNach(desc);
        if (!isAutoDebit && !Rx(@"EMI|LOAN\s*INST|INSTALMENT|REPAY").IsMatch(desc)) return null;
        if (isAutoDebit && amount >= 500)
        {
            var lender = DetectLender(desc);
            return (
                lender?.Name ?? (InferAutoDebitLender(desc).Length > 0 ? InferAutoDebitLender(desc) : "Unknown Lender (NACH)"),
                lender?.Category ?? "Unknown",
                DetectLoanType(desc));
        }
        return null;
    }

    private static (string Lender, string LenderCategory, LoanType LoanType)? DetectLoanDisbursement(string desc, decimal amount, bool isCredit)
    {
        if (!isCredit || amount < 5000) return null;
        foreach (var p in DisbursementKeywords)
        {
            if (p.IsMatch(desc))
            {
                var lender = DetectLender(desc);
                return (lender?.Name ?? "Unknown Lender", lender?.Category ?? "Unknown", DetectLoanType(desc));
            }
        }
        foreach (var p in LenderPatterns)
        {
            if (p.Regex.IsMatch(desc) && (p.Category == "NBFC" || p.Category == "HFC") && amount >= 50000)
                return (p.Name, p.Category, DetectLoanType(desc));
        }
        return null;
    }

    private static bool DetectPf(string desc) => PfPatterns.Any(p => p.IsMatch(desc));

    private static (string Name, int MaxApr)? DetectAppLoan(string desc)
    {
        foreach (var p in AppLoanPatterns)
            if (p.Regex.IsMatch(desc)) return (p.Name, p.MaxApr);
        return null;
    }

    private static string? DetectSip(string desc)
    {
        var d = (desc ?? "").ToUpperInvariant();
        var isMf = Rx(@"\bMF\b|MUTUAL|SIP|FOLIO|AMC\b|\bCAMS\b|KFIN|KARVY").IsMatch(d);
        var isStock = Rx(@"\bNSE\b|\bBSE\b|\bEQUITY\b|\bSTOCK\b|\bSHARES?\b|\bBROK").IsMatch(d);
        if (isStock && !isMf) return null;
        foreach (var p in SipPatterns)
        {
            if (p.Regex.IsMatch(desc))
            {
                if (!isMf && Rx(@"GROWW|ZERODHA|PAYTM|MOTILAL").IsMatch(p.Platform)) continue;
                return p.Platform;
            }
        }
        return null;
    }

    private static string? DetectStockMarket(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return null;
        var d = NormUpper(desc);
        var isMf = Rx(@"\bMF\b|MUTUAL|SIP|FOLIO|AMC\b|\bCAMS\b|KFIN|KARVY").IsMatch(d);
        var isStock = Rx(@"\bNSE\b|\bBSE\b|\bEQUITY\b|\bSTOCK\b|\bSHARES?\b|\bBROK(ING)?\b|DP\s*CHARGES|\bDEMAT\b|CDSL|NSDL|\bPAYOUT\b|\bPAYIN\b|\bMARGIN\b|\bFNO\b|F&O|\bFUTURES\b|\bOPTIONS\b|\bINTRADAY\b|\bIPO\b").IsMatch(d);
        var isUpi = Rx(@"\bUPI\b|\bUPIAR\b|\bUPI/").IsMatch(d);
        foreach (var p in StockMarketPatterns)
        {
            if (!p.Regex.IsMatch(desc)) continue;
            if (isMf && !isStock) return null;
            return p.Platform;
        }
        if (isStock && !isMf)
        {
            if (isUpi)
            {
                var strong = Rx(@"DP\s*CHARGES|\bDEMAT\b|CDSL|NSDL|\bPAYOUT\b|\bPAYIN\b|\bFNO\b|F&O|\bFUTURES\b|\bOPTIONS\b|\bINTRADAY\b");
                if (!strong.IsMatch(d)) return null;
            }
            return "Stock Market";
        }
        return null;
    }

    private static (string Provider, string Type)? DetectInsurance(string desc)
    {
        foreach (var p in InsurancePatterns)
            if (p.Regex.IsMatch(desc)) return (p.Provider, p.Type);
        return null;
    }

    private static bool DetectRent(string desc)
    {
        var d = desc.ToUpperInvariant();
        return RentKeywords.Any(kw => d.Contains(kw));
    }

    private static string? DetectUtility(string desc)
    {
        foreach (var p in UtilityPatterns)
            if (p.Regex.IsMatch(desc)) return p.Category;
        return null;
    }

    private static bool DetectApy(string desc) => Rx(@"\bAPY\b|ATAL.*PENSION|TRTR.*APY").IsMatch(desc);

    private static (string Category, string? Channel)? DetectBounceOrCharges(string desc)
    {
        var d = NormUpper(desc);

        var isBounce =
            Regex.IsMatch(d, @"\bBOUNCE(D)?\b") ||
            Regex.IsMatch(d, @"\bRETURN\b") ||
            Regex.IsMatch(d, @"\bUNPAID\b") ||
            Regex.IsMatch(d, @"\bREJECT(ED)?\b") ||
            Regex.IsMatch(d, @"\bFAILED\b|\bFAILURE\b") ||
            Regex.IsMatch(d, @"\bINSUFFICIENT\s*FUNDS\b") ||
            Regex.IsMatch(d, @"\bRETURN\s*MEMO\b");

        var isCharge =
            Regex.IsMatch(d, @"\bCHARGES?\b") ||
            Regex.IsMatch(d, @"\bCHG\b") ||
            Regex.IsMatch(d, @"\bPENALTY\b") ||
            Regex.IsMatch(d, @"\bBOUNCE\s*CHARGES?\b") ||
            Regex.IsMatch(d, @"\bRETURN\s*CHARGES?\b");

        if (!isBounce && !isCharge) return null;

        var category = "Charges/Returns";
        if (Regex.IsMatch(d, @"\b(EMI|INSTAL+MENT)\b")) category = "EMI Bounce/Charges";
        else if (Regex.IsMatch(d, @"\b(NACH|ECS|ACH|SI|E-?MANDATE|MANDATE)\b")) category = "Auto-debit Return/Charges";
        else if (Regex.IsMatch(d, @"\b(CHQ|CHEQUE)\b")) category = "Cheque Bounce/Charges";

        string? channel = null;
        if (Regex.IsMatch(d, @"\bNACH\b")) channel = "NACH";
        else if (Regex.IsMatch(d, @"\bECS\b")) channel = "ECS";
        else if (Regex.IsMatch(d, @"\bACH\b")) channel = "ACH";
        else if (Regex.IsMatch(d, @"\bSI\b") || Regex.IsMatch(d, @"\bE-?MANDATE\b") || Regex.IsMatch(d, @"\bMANDATE\b")) channel = "SI/Mandate";
        else if (Regex.IsMatch(d, @"\bCHQ\b") || Regex.IsMatch(d, @"\bCHEQUE\b")) channel = "Cheque";

        return (category, channel);
    }

    private static (string? Platform, string? Channel, string? IssuerBank, string? Payee, string? Last4, string Display, double? Confidence)? ClassifyCreditCardPayment(string desc)
    {
        var text = NormalizeDesc(desc);
        var d = text.ToUpperInvariant();

        var signalsStrong = new[]
        {
            @"\bCREDIT\s*CARD\b", @"\bCRDT\s*CARD\b", @"\bCARD\s*(?:BILL|DUE)\b",
            @"\bCC\s*(?:PAYMENT|PAY|PMT)\b", @"\bCREDITCARD\b",
        };
        var signalsIssuer = new[] { @"\bCARD\b.*\bPAYMENT\b", @"\bVISA\b", @"\bMASTERCARD\b", @"\bRUPAY\b" };
        var negatives = new[] { @"\bUPI\b", @"\bUPIAR\b", @"\bUPI/" };

        var score = 0;
        if (AnyMatch(d, signalsStrong)) score += 6;
        if (Regex.IsMatch(d, @"\bBILLDESK\b") && Regex.IsMatch(d, @"\b(CARD|CC|CREDIT)\b")) score += 3;
        if (AnyMatch(d, signalsIssuer) && Regex.IsMatch(d, @"\b(CARD|CC)\b")) score += 2;

        if (Regex.IsMatch(d, @"\bPAID\s+VIA\s+CRED\b") || Regex.IsMatch(d, @"\bCREDPAY\b")) return null;

        var isUpi = AnyMatch(text, negatives);
        if (isUpi && score < 5) score -= 4;

        if (score < 4) return null;

        var meta = ParseCreditCardPaymentMeta(text);
        var confidence = Math.Min(1.0, Math.Max(0.4, score / 10.0));
        return (meta.Platform, meta.Channel, meta.IssuerBank, meta.Payee, meta.Last4, meta.Display, (double?)confidence);
    }

    private static (string? Platform, string? Channel, string? IssuerBank, string? Payee, string? Last4, string Display, double? Confidence) ParseCreditCardPaymentMeta(string desc)
    {
        var text = desc ?? "";
        var upper = text.ToUpperInvariant();

        string? platform = null;
        if (Regex.IsMatch(upper, @"\bPAID\s+VIA\s+CRED\b") || Regex.IsMatch(upper, @"\bCREDPAY\b")) platform = "CRED";
        else if (Regex.IsMatch(upper, @"\bBILLDESK\b")) platform = "BillDesk";

        string? channel = null;
        if (Regex.IsMatch(upper, @"\bUPI\b") || Regex.IsMatch(upper, @"\bUPIAR\b") || Regex.IsMatch(text, @"\bUPI/", RegexOptions.IgnoreCase)) channel = "UPI";
        else if (Regex.IsMatch(upper, @"\bNEFT\b")) channel = "NEFT";
        else if (Regex.IsMatch(upper, @"\bIMPS\b")) channel = "IMPS";
        else if (Regex.IsMatch(upper, @"\bRTGS\b")) channel = "RTGS";
        else if (Regex.IsMatch(upper, @"\bNETBANK\b")) channel = "NETBANKING";

        string? issuerBank = null;
        var credBank = Regex.Match(text, @"PAID\s+VIA\s+CRED/([^/]{2,60})", RegexOptions.IgnoreCase);
        if (credBank.Success) issuerBank = Regex.Replace(credBank.Groups[1].Value, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(issuerBank))
        {
            var m = Regex.Match(upper, @"\b(HDFC|ICICI|SBI|AXIS|KOTAK|INDUSIND|YESB|YES\s*BANK|IDFC|RBL|AU\s*BANK|FEDERAL|PNB|BOB|CANARA|UNION\s*BANK)\b");
            if (m.Success) issuerBank = Regex.Replace(m.Value, @"\s+", " ").Trim();
        }

        string? payee = null;
        var upi = Regex.Match(text, @"UPI/([^/\s]{2,80})", RegexOptions.IgnoreCase);
        if (upi.Success) payee = upi.Groups[1].Value.Trim();

        string? last4 = null;
        var m4 = Regex.Match(upper, @"\b(?:CARD|CC)\b[^0-9X]*(?:XX|X{2,}|\*{2,})\s*(\d{4})\b");
        if (m4.Success) last4 = m4.Groups[1].Value;

        var display =
            platform == "CRED" ? $"CRED → {issuerBank ?? "Credit Card"}" :
            platform == "BillDesk" ? $"BillDesk → {issuerBank ?? "Credit Card"}" :
            !string.IsNullOrEmpty(issuerBank) ? $"Card Payment → {issuerBank}" :
            "Card Payment";

        return (platform, channel, issuerBank, payee, last4, display, null);
    }

    private static (string Name, string Type)? DetectGovtScheme(string desc)
    {
        foreach (var p in GovtSchemePatterns)
            if (p.Regex.IsMatch(desc)) return (p.Name, p.Type);
        return null;
    }

    // ════════════════════════════════════════════════════════════════
    // MAIN ANALYZER
    // ════════════════════════════════════════════════════════════════

    public static BsaAnalysisResult AnalyzeTransactions(List<BsaTransaction> transactions)
    {
        var result = new BsaAnalysisResult();
        if (transactions.Count == 0) return result;

        // Sort by date (stable, mirroring new Date(a) - new Date(b))
        var ordered = transactions
            .Select((t, i) => (t, i))
            .OrderBy(x => DateTime.TryParse(x.t.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Ticks : 0L)
            .ThenBy(x => x.i)
            .Select(x => x.t)
            .ToList();
        transactions = ordered;

        result.AccountSummary.TransactionCount = transactions.Count;
        result.AccountSummary.Period = $"{transactions[0].Date} to {transactions[^1].Date}";

        var emiSeen = new HashSet<string>();
        var processingFees = new List<(string Date, decimal Amount, string Description, BsaMeta Meta)>();
        var monthlyIndex = new Dictionary<string, BsaMonthlySummary>();
        var loanIndex = new Dictionary<string, BsaLoanSummaryItem>();

        foreach (var tx in transactions)
        {
            var desc = (tx.Description ?? "").Trim();
            var debit = ParseAmount(tx.Debit);
            var credit = ParseAmount(tx.Credit);
            var isCredit = credit > 0;
            var isDebit = debit > 0;
            var date = tx.Date ?? "";
            var balance = ParseAmount(tx.Balance);
            var month = MonthKey(date);
            var meta = ExtractTxnMeta(desc, tx);

            if (!monthlyIndex.TryGetValue(month, out var ms))
            {
                ms = new BsaMonthlySummary { Month = month };
                monthlyIndex[month] = ms;
                result.MonthlySummary.Add(ms);
            }
            if (isCredit) { ms.Credits += credit; result.AccountSummary.TotalCredits += credit; }
            if (isDebit) { ms.Debits += debit; result.AccountSummary.TotalDebits += debit; }
            if (balance > 0) ms.ClosingBalance = balance;

            // ══════════════ CREDIT SIDE ══════════════
            if (isCredit)
            {
                // 1. LOAN DISBURSEMENT
                var disb = DetectLoanDisbursement(desc, credit, true);
                if (disb != null)
                {
                    var row = new BsaDisbursementRow
                    {
                        Date = date, Description = desc,
                        Lender = disb.Value.Lender, LenderCategory = disb.Value.LenderCategory,
                        Amount = credit,
                        Mode = Regex.IsMatch(desc, "NEFT", RegexOptions.IgnoreCase) ? "NEFT"
                             : Regex.IsMatch(desc, "IMPS", RegexOptions.IgnoreCase) ? "IMPS"
                             : Regex.IsMatch(desc, "RTGS", RegexOptions.IgnoreCase) ? "RTGS" : "NEFT",
                        LoanAccountNumber = meta.LoanAccountMasked ?? "",
                        SanctionAmount = credit,
                    };
                    CopyMeta(meta, row);
                    result.LoanDisbursements.Add(row);
                    ms.Disbursements += credit;
                    continue;
                }

                // 2. SALARY
                var employer = DetectSalary(desc, credit, true);
                if (employer != null)
                {
                    var row = new BsaSalaryRow
                    {
                        Date = date, Description = desc, Employer = employer, Amount = credit,
                        Mode = Regex.IsMatch(desc, "NACH", RegexOptions.IgnoreCase) ? "NACH"
                             : Regex.IsMatch(desc, "NEFT", RegexOptions.IgnoreCase) ? "NEFT"
                             : Regex.IsMatch(desc, "IMPS", RegexOptions.IgnoreCase) ? "IMPS" : "Other",
                        Month = month,
                    };
                    CopyMeta(meta, row);
                    result.Salary.Add(row);
                    ms.Salary += credit;
                    continue;
                }

                // 3. PF / EPFO
                if (DetectPf(desc))
                {
                    var row = new BsaPfRow
                    {
                        Date = date, Description = desc, Amount = credit, Type = "credit",
                        Source = Regex.IsMatch(desc, "EPFO", RegexOptions.IgnoreCase) ? "EPFO" : "Employer PF",
                    };
                    CopyMeta(meta, row);
                    result.PfEpfo.Add(row);
                    result.PfCredits.Add(row);
                    ms.Pf += credit;
                    continue;
                }

                // 4. GOVT SCHEME
                var govt = DetectGovtScheme(desc);
                if (govt != null)
                {
                    result.GovtSchemeCredits.Add(new BsaGovtSchemeRow
                    { Date = date, Description = desc, Amount = credit, SchemeName = govt.Value.Name, Type = govt.Value.Type });
                    continue;
                }

                // 5. STOCK MARKET (credit = sale / payout)
                var smCredit = DetectStockMarket(desc);
                if (smCredit != null)
                {
                    var row = new BsaStockMarketRow
                    { Date = date, Direction = "credit", Platform = smCredit, Amount = credit, Description = desc };
                    CopyMeta(meta, row);
                    result.StockMarket.Add(row);
                    continue;
                }

                // 6. OTHER INCOME
                result.OtherIncome.Add(new BsaOtherIncomeRow
                { Date = date, Description = Trunc(desc, 80), Amount = credit, Type = "other" });
            }

            // ══════════════ DEBIT SIDE ══════════════
            if (isDebit)
            {
                if (Rx(@"PROCESSING\s*FEE|DOC(?:UMENTATION)?\s*CHARGES?|STAMP\s*DUTY|DISBURSAL\s*CHARGES?|LOAN\s*CHARGES?").IsMatch(desc))
                    processingFees.Add((date, debit, desc, meta));

                var bounce = DetectBounceOrCharges(desc);
                if (bounce != null)
                {
                    var row = new BsaBounceRow
                    { Date = date, Description = desc, Amount = debit, Category = bounce.Value.Category, Channel = bounce.Value.Channel };
                    CopyMeta(meta, row);
                    result.BounceCharges.Add(row);
                    continue;
                }

                // 1. APY PENSION
                if (DetectApy(desc))
                {
                    var row = new BsaApyRow { Date = date, Description = desc, Amount = debit };
                    CopyMeta(meta, row);
                    result.ApyPension.Add(row);
                    continue;
                }

                // 2. LOAN EMI
                var emiInfo = DetectLoanEmi(desc, debit);
                if (emiInfo != null)
                {
                    var key = $"{date}|{emiInfo.Value.Bank}|{emiInfo.Value.LoanType.Code}|{debit}";
                    if (!emiSeen.Contains(key))
                    {
                        emiSeen.Add(key);
                        var row = new BsaEmiRow
                        {
                            Date = date, Description = desc,
                            Bank = emiInfo.Value.Bank, BankCategory = emiInfo.Value.BankCategory,
                            LoanTypeCode = emiInfo.Value.LoanType.Code,
                            LoanTypeLabel = emiInfo.Value.LoanType.Label,
                            LoanTypeIcon = emiInfo.Value.LoanType.Icon,
                            LoanTypeColor = emiInfo.Value.LoanType.Color,
                            EmiAmount = debit, Month = month,
                            Mode = DetectNach(desc) ? "NACH/ECS" : "Manual",
                            LoanAccountNumber = meta.LoanAccountMasked ?? "",
                        };
                        CopyMeta(meta, row);
                        result.EmiPayments.Add(row);
                        result.Loans.Add(row);

                        if (!loanIndex.TryGetValue(emiInfo.Value.Bank, out var ls))
                        {
                            ls = new BsaLoanSummaryItem
                            {
                                Bank = emiInfo.Value.Bank, BankCategory = emiInfo.Value.BankCategory,
                                LoanTypeCode = emiInfo.Value.LoanType.Code,
                                LoanTypeLabel = emiInfo.Value.LoanType.Label,
                                LoanTypeIcon = emiInfo.Value.LoanType.Icon,
                                LoanTypeColor = emiInfo.Value.LoanType.Color,
                                EmiAmount = debit,
                            };
                            loanIndex[emiInfo.Value.Bank] = ls;
                            result.LoanSummary.Add(ls);
                        }
                        ls.Count++;
                        ls.TotalPaid += debit;
                        ms.Emi += debit;
                    }
                    continue;
                }

                // 3. INSURANCE
                var ins = DetectInsurance(desc);
                if (ins != null)
                {
                    var row = new BsaInsuranceRow
                    { Date = date, Provider = ins.Value.Provider, PolicyType = ins.Value.Type, Amount = debit, Description = desc };
                    CopyMeta(meta, row);
                    result.Insurance.Add(row);
                    continue;
                }

                // 4. STOCK MARKET
                var smDebit = DetectStockMarket(desc);
                if (smDebit != null)
                {
                    var row = new BsaStockMarketRow
                    { Date = date, Direction = "debit", Platform = smDebit, Amount = debit, Description = desc };
                    CopyMeta(meta, row);
                    result.StockMarket.Add(row);
                    continue;
                }

                // 5. SIP
                var sip = DetectSip(desc);
                if (sip != null)
                {
                    var row = new BsaSipRow
                    { Date = date, FundName = Trunc(desc, 60), Platform = sip, Amount = debit, Description = desc };
                    CopyMeta(meta, row);
                    result.SipInvestments.Add(row);
                    continue;
                }

                // 6. APP LOANS
                var app = DetectAppLoan(desc);
                if (app != null)
                {
                    var row = new BsaAppLoanRow
                    { Date = date, AppName = app.Value.Name, MaxApr = app.Value.MaxApr, Amount = debit, Type = "repayment", Description = desc };
                    CopyMeta(meta, row);
                    result.AppLoans.Add(row);
                    continue;
                }

                // 7. RENT
                if (DetectRent(desc))
                {
                    var row = new BsaRentRow
                    { Date = date, Payee = Trunc(desc, 50), Amount = debit, Description = desc, Mode = "UPI" };
                    CopyMeta(meta, row);
                    result.Rent.Add(row);
                    continue;
                }

                // 8. CREDIT CARD
                var cc = ClassifyCreditCardPayment(desc);
                if (cc != null)
                {
                    var row = new BsaCreditCardRow
                    {
                        Date = date, Description = desc, Amount = debit,
                        CcPlatform = cc.Value.Platform, CcChannel = cc.Value.Channel,
                        CcIssuerBank = cc.Value.IssuerBank, CcPayee = cc.Value.Payee,
                        CcLast4 = cc.Value.Last4, CcDisplay = cc.Value.Display,
                        Confidence = cc.Value.Confidence,
                    };
                    CopyMeta(meta, row);
                    result.CreditCardPayments.Add(row);
                    continue;
                }

                // 9. PF DEBIT (employee contribution)
                if (DetectPf(desc))
                {
                    var row = new BsaPfRow
                    { Date = date, Description = desc, Amount = debit, Type = "debit", Source = "Employee PF contribution" };
                    CopyMeta(meta, row);
                    result.PfEpfo.Add(row);
                    ms.Pf += debit;
                    continue;
                }

                // 10. UTILITIES
                var util = DetectUtility(desc);
                if (util != null)
                {
                    var row = new BsaUtilityRow
                    { Date = date, Description = Trunc(desc, 60), Amount = debit, Category = util };
                    CopyMeta(meta, row);
                    result.Utilities.Add(row);
                    continue;
                }

                result.OtherDebits.Add(new BsaOtherDebitRow { Date = date, Description = Trunc(desc, 80), Amount = debit });
            }
        }

        // ── Link processing fees to disbursements ────────────────────
        if (processingFees.Count > 0 && result.LoanDisbursements.Count > 0)
        {
            var byMonth = new Dictionary<string, List<BsaDisbursementRow>>();
            foreach (var d in result.LoanDisbursements)
            {
                var m = MonthKey(d.Date);
                if (!byMonth.TryGetValue(m, out var list)) { list = new(); byMonth[m] = list; }
                list.Add(d);
            }
            foreach (var fee in processingFees)
            {
                if (!byMonth.TryGetValue(MonthKey(fee.Date), out var candidates) || candidates.Count == 0) continue;
                BsaDisbursementRow? picked = candidates.Count == 1 ? candidates[0] : null;
                if (picked == null)
                {
                    if (DateTime.TryParse(fee.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd))
                    {
                        foreach (var d in candidates)
                        {
                            if (DateTime.TryParse(d.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) &&
                                Math.Abs((fd - dd).TotalDays) <= 7) { picked = d; break; }
                        }
                    }
                }
                if (picked != null) picked.ProcessingFee = (picked.ProcessingFee ?? 0) + fee.Amount;
            }
        }

        // ── COMPUTE INSIGHTS ─────────────────────────────────────────
        var salaryMonths = result.Salary.Count > 0
            ? result.Salary.Select(s => MonthKey(s.Date)).Distinct().Count()
            : 1;
        if (salaryMonths == 0) salaryMonths = 1;

        result.Insights.AvgMonthlySalary = result.Salary.Sum(x => x.Amount) / salaryMonths;

        var emiByBank = new Dictionary<string, decimal>();
        foreach (var e in result.EmiPayments)
        {
            if (!emiByBank.TryGetValue(e.Bank, out var cur) || e.EmiAmount > cur)
                emiByBank[e.Bank] = e.EmiAmount;
        }
        result.Insights.TotalEmiMonthly = emiByBank.Values.Sum();
        result.Insights.TotalDisbursed = result.LoanDisbursements.Sum(x => x.Amount);
        result.Insights.EstimatedTotalDebt = result.Insights.TotalEmiMonthly * 36;

        var apyMonthly = result.ApyPension.Count > 0 ? result.ApyPension.Sum(x => x.Amount) / salaryMonths : 0;

        result.Insights.ObligationToIncomeRatio = result.Insights.AvgMonthlySalary > 0
            ? (int)Math.Round((result.Insights.TotalEmiMonthly + apyMonthly) / result.Insights.AvgMonthlySalary * 100)
            : 0;

        result.AccountSummary.NetBalance = result.AccountSummary.TotalCredits - result.AccountSummary.TotalDebits;

        var totalMonths = Math.Max(result.MonthlySummary.Count, 1);
        var avgCredit = result.AccountSummary.TotalCredits / totalMonths;
        var avgDebit = result.AccountSummary.TotalDebits / totalMonths;
        result.Insights.SavingsRate = avgCredit > 0
            ? (int)Math.Round((avgCredit - avgDebit) / avgCredit * 100) : 0;

        // ── RISK FLAGS ───────────────────────────────────────────────
        var R = result.Insights.RiskFlags;
        var ratio = result.Insights.ObligationToIncomeRatio;
        if (ratio > 60) R.Add($"EMI obligation ratio is {ratio}% — critical (RBI safe limit: 40%)");
        else if (ratio > 40) R.Add($"EMI obligation ratio is {ratio}% — moderate risk");

        var allBals = transactions.Where(t => ParseAmount(t.Balance) != 0).Select(t => ParseAmount(t.Balance)).ToList();
        if (allBals.Count > 0)
        {
            var minBal = allBals.Min();
            if (minBal < 500) R.Add($"Balance dropped to ₹{minBal.ToString("0.00", CultureInfo.InvariantCulture)} — near-zero liquidity");
        }
        if (result.AppLoans.Count > 0)
            R.Add($"{result.AppLoans.Count} app loan repayment(s) — high-interest debt (24–48% APR)");
        if (result.LoanDisbursements.Count > 0)
            R.Add($"{result.LoanDisbursements.Count} fresh loan disbursement(s) totalling ₹{Fmt(result.Insights.TotalDisbursed)}");
        if (result.SipInvestments.Count == 0)
            R.Add("No SIP/mutual fund investment detected — wealth creation gap");
        if (result.Insurance.Count(i => i.PolicyType == "life") == 0)
            R.Add("No life insurance premium detected — financial protection gap");
        if (result.LoanSummary.Count > 2)
            R.Add($"{result.LoanSummary.Count} active loans — debt consolidation recommended");
        var ccTotal = result.CreditCardPayments.Sum(x => x.Amount);
        if (ccTotal > 0) R.Add($"Credit card payments: ₹{Fmt(ccTotal)} — monitor revolving debt");

        // ── RECOMMENDATIONS ──────────────────────────────────────────
        var Rec = result.Insights.Recommendations;
        if (result.SipInvestments.Count == 0)
            Rec.Add("Start SIP: ₹1,000/month in index funds builds wealth through compounding");
        if (result.Insurance.Count == 0)
            Rec.Add("Get term life (₹1Cr cover ~₹700/month) + health insurance (₹5L cover ~₹500/month)");
        if (ratio > 50)
            Rec.Add("Loan consolidation or part-prepayment recommended to bring EMI ratio below 40%");
        if (result.AppLoans.Count > 0)
            Rec.Add("Clear high-interest app loans first — they charge 24–48% APR vs 10–18% bank loans");
        if (result.PfEpfo.Count(p => p.Type == "credit") > 0)
            Rec.Add("PF withdrawal detected — consider VPF top-up for tax-free retirement returns");
        if (result.Insights.SavingsRate < 10)
            Rec.Add("Savings rate below 10% — budget review needed to identify discretionary spending");

        result.Insights.AdditionalFieldsNeeded = new List<string>
        {
            "Loan Account Number (LAN) — links disbursement to EMI repayment",
            "Principal vs Interest split — for tax benefit (Sec 24b / 80C)",
            "Outstanding Loan Balance — from loan statement or net banking",
            "Loan Tenure (months) — to project future cash outflows",
            "Interest Rate (ROI %) — fixed or floating (MCLR/EBLR based)",
            "Processing Fee — one-time charge at disbursement",
            "Bounce / Penal Charges — indicates missed EMI",
            "Co-applicant Name — for joint loan products",
            "Security / Collateral Type — for HL, LAP, GL",
            "Moratorium Period — for EL or restructured loans",
            "Employer Name on Payslip — to verify salary consistency",
            "UAN Number — to link EPFO account with salary account",
            "TDS Certificate (Form 16) — for gross vs net salary reconciliation",
            "GST / Business Turnover — for BL underwriting",
        };

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // CIBIL REPORT TEXT PARSER
    // ════════════════════════════════════════════════════════════════

    private static decimal? ParseNum(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var cleaned = Regex.Replace(s, "[,\\s]", "");
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static BsaCibilReport ParseCibilText(string text)
    {
        var t = text ?? "";
        var lines = t.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var joined = string.Join("\n", lines);
        var joinedFlat = Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();

        var report = new BsaCibilReport { RawCharCount = t.Length };

        // Score
        int? score = null;
        var sm = Regex.Match(joined, @"CIBIL\s*SCORE[^0-9]{0,60}([3-9]\d{2})\b", RegexOptions.IgnoreCase);
        if (!sm.Success) sm = Regex.Match(joined, @"\bSCORE[^0-9]{0,60}([3-9]\d{2})\b", RegexOptions.IgnoreCase);
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out var sn) && sn >= 300 && sn <= 900)
            score = sn;
        if (score == null)
        {
            var fm = Regex.Match(joinedFlat, @"\b([3-9]\d{2})\b\s+(EXCELLENT|VERY\s*GOOD|GOOD|FAIR|POOR)\b", RegexOptions.IgnoreCase);
            if (fm.Success && int.TryParse(fm.Groups[1].Value, out var fn) && fn >= 300 && fn <= 900)
                score = fn;
        }
        report.Score = score;

        var dm = Regex.Match(joined, @"\bREPORT\s*DATE[^0-9]{0,20}(\d{2}[-/]\d{2}[-/]\d{4})", RegexOptions.IgnoreCase);
        if (!dm.Success) dm = Regex.Match(joined, @"\bAS\s*ON[^0-9]{0,20}(\d{2}[-/]\d{2}[-/]\d{4})", RegexOptions.IgnoreCase);
        if (dm.Success) report.ReportDate = dm.Groups[1].Value;

        // Enquiries summary
        var enquiries = new Dictionary<string, decimal>();
        var lastDaysHits = Regex.Matches(joined, @"\bENQUIR(?:Y|IES)\b[\s\S]{0,200}?\bLAST\s*(30|60|90|180|365)\s*DAYS\b[^0-9]{0,20}(\d{1,3})", RegexOptions.IgnoreCase);
        foreach (Match hit in lastDaysHits)
        {
            var m = Regex.Match(hit.Value, @"\bLAST\s*(30|60|90|180|365)\s*DAYS\b[^0-9]{0,20}(\d{1,3})", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var v = ParseNum(m.Groups[2].Value);
            if (v != null) enquiries[$"last_{m.Groups[1].Value}d"] = v.Value;
        }
        var totalEnq = Regex.Match(joined, @"\bTOTAL\s*ENQUIR(?:Y|IES)\b[^0-9]{0,20}(\d{1,4})", RegexOptions.IgnoreCase);
        if (!totalEnq.Success) totalEnq = Regex.Match(joined, @"\bENQUIR(?:Y|IES)\b[^0-9]{0,20}(\d{1,4})\b", RegexOptions.IgnoreCase);
        if (totalEnq.Success)
        {
            var v = ParseNum(totalEnq.Groups[1].Value);
            if (v != null) enquiries["total"] = v.Value;
        }

        // Personal details
        var personal = new BsaCibilPersonal();
        var addresses = new List<string>();
        bool LabelLine(string s) => Regex.IsMatch(s ?? "", @"^[A-Z][A-Z ]{2,30}\s*[:\-]");
        bool StopAddress(string s) =>
            Regex.IsMatch(s ?? "", @"\b(ACCOUNT|ACCT|ENQUIR(?:Y|IES)|INQUIR(?:Y|IES)|SCORE|CIBIL|DPD|CREDIT\s*FACILITY|PAYMENT\s*HISTORY)\b", RegexOptions.IgnoreCase) ||
            LabelLine(s);

        var addrBuf = new List<string>();
        var inAddr = false;
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^([A-Z][A-Z \/_]{2,40})\s*[:\-]\s*(.*)$");
            if (m.Success)
            {
                var k = Regex.Replace(m.Groups[1].Value.Trim().ToUpperInvariant(), @"\s+", " ");
                var v = m.Groups[2].Value.Trim();

                if (Regex.IsMatch(k, @"^(FULL\s*)?NAME$|^NAME$") && personal.Name == null) personal.Name = v;
                else if (Regex.IsMatch(k, @"^DATE OF BIRTH$|^DOB$") && personal.Dob == null) personal.Dob = v;
                else if (Regex.IsMatch(k, @"^GENDER$|^SEX$") && personal.Gender == null) personal.Gender = v;
                else if (Regex.IsMatch(k, @"^PAN$|^PERMANENT ACCOUNT NUMBER$") && personal.Pan == null) personal.Pan = v;
                else if (Regex.IsMatch(k, @"^MOBILE$|^MOBILE NUMBER$|^PHONE$|^PHONE NUMBER$") && personal.Mobile == null) personal.Mobile = v;
                else if (Regex.IsMatch(k, @"^EMAIL$|^EMAIL ID$") && personal.Email == null) personal.Email = v;
                else if (Regex.IsMatch(k, @"^COMPANY$|^EMPLOYER$|^ORGANIZATION$|^ORGANISATION$") && personal.Company == null) personal.Company = v;

                if (Regex.IsMatch(k, "ADDRESS"))
                {
                    if (addrBuf.Count > 0)
                    {
                        addresses.Add(Regex.Replace(string.Join(" ", addrBuf), @"\s+", " ").Trim());
                        addrBuf = new List<string>();
                    }
                    if (v.Length > 0) addrBuf.Add(v);
                    inAddr = true;
                }
                else if (inAddr && v.Length == 0)
                {
                    inAddr = false;
                }
                continue;
            }

            if (!inAddr) continue;
            if (StopAddress(line))
            {
                if (addrBuf.Count > 0) addresses.Add(Regex.Replace(string.Join(" ", addrBuf), @"\s+", " ").Trim());
                addrBuf = new List<string>();
                inAddr = false;
                continue;
            }
            addrBuf.Add(line);
        }
        if (addrBuf.Count > 0) addresses.Add(Regex.Replace(string.Join(" ", addrBuf), @"\s+", " ").Trim());
        if (addresses.Count > 0) personal.Addresses = addresses.Distinct().Where(x => x.Length > 0).ToList();

        if (personal.Name == null)
        {
            var m = Regex.Match(joined, @"\bHey\s+([A-Z][A-Za-z]+)\b", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(joined, @"\b([A-Z][A-Za-z]+)'s\s+Credit\s+Report\b", RegexOptions.IgnoreCase);
            if (m.Success) personal.Name = m.Groups[1].Value.Trim();
        }

        if (personal.Mobile == null)
        {
            var m = Regex.Match(joinedFlat, @"\bMobile\s*Phone\s+(\d{10})\b", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(joinedFlat, @"\bPhone\s*Number\b[\s\S]{0,120}?\b(\d{10})\b", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(joinedFlat, @"\b(\d{10})\b");
            if (m.Success) personal.Mobile = m.Groups[1].Value;
        }

        if (personal.Email == null)
        {
            var m = Regex.Match(joinedFlat, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
            if (m.Success) personal.Email = m.Value;
        }

        if (personal.Addresses.Count == 0)
        {
            var upper = joinedFlat.ToUpperInvariant();
            var idx = upper.LastIndexOf("ADDRESS DETAILS", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var seg = joinedFlat.Substring(idx, Math.Min(8000, joinedFlat.Length - idx));
                var addrRe = new Regex(@"([A-Z0-9:#,\-/ .]{15,}?)\s*,\s*(\d{6})\s*,\s*([A-Za-z]{2,})\s+(Residence\s+Address|O\s*ce\s+Address|Office\s+Address)\b", RegexOptions.IgnoreCase);
                var found = new List<string>();
                foreach (Match mm in addrRe.Matches(seg))
                {
                    var a = Regex.Replace($"{mm.Groups[1].Value}, {mm.Groups[2].Value}, {mm.Groups[3].Value}", @"\s+", " ").Trim();
                    a = Regex.Replace(a, @"^Address Details\s+Address\s+Category\s+Date\s+Reported\s+", "", RegexOptions.IgnoreCase);
                    if (a.Length >= 10) found.Add(a);
                }
                if (found.Count > 0) personal.Addresses = found.Distinct().ToList();
            }
        }

        // Enquiry details (line-wise)
        var enquiryDetails = new List<BsaCibilEnquiryDetail>();
        var enqDateRe = new Regex(@"\b(\d{2}[-/]\d{2}[-/]\d{4})\b");
        var inEnq = false;
        foreach (var line in lines)
        {
            if (!inEnq && Regex.IsMatch(line, @"\bENQUIR(?:Y|IES)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(line, @"\bDETAIL|HISTORY|INFORMATION\b", RegexOptions.IgnoreCase))
            {
                inEnq = true;
                continue;
            }
            if (!inEnq && (Regex.IsMatch(line, @"\bENQUIR(?:Y|IES)\s+PURPOSE\b", RegexOptions.IgnoreCase) ||
                           Regex.IsMatch(line, @"\bENQUIRE[D|D]\s+ON\b", RegexOptions.IgnoreCase) ||
                           Regex.IsMatch(line, @"\bENQUIR(?:IED|ED)\s+ON\b", RegexOptions.IgnoreCase)))
            {
                inEnq = true;
                continue;
            }
            if (!inEnq) continue;
            if (Regex.IsMatch(line, @"\bACCOUNT\b", RegexOptions.IgnoreCase) && Regex.IsMatch(line, @"\bTYPE\b", RegexOptions.IgnoreCase)) break;
            var dmx = enqDateRe.Match(line);
            if (!dmx.Success) continue;
            if (Regex.IsMatch(line, @"\bREPORT\s+NUMBER\b", RegexOptions.IgnoreCase) || Regex.IsMatch(line, @"\bTABLE\s+OF\s+CONTENTS\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(line, @"\bLAST\s*(30|60|90|180|365)\s*DAYS\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(line, @"\bTOTAL\s*ENQUIR", RegexOptions.IgnoreCase)) continue;

            var date = ParseDate(dmx.Groups[1].Value);
            var member = "";
            var purpose = "";
            decimal? amount = null;
            var rest = Regex.Replace(line, enqDateRe.ToString(), "").Trim();
            var parts = Regex.Split(rest, @"\s{2,}").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (parts.Count >= 2)
            {
                member = parts[0] ?? "";
                purpose = parts[1] ?? "";
                if (parts.Count >= 3) amount = ParseNum(parts[2]);
            }
            else
            {
                var amt = Regex.Match(rest, @"\b(\d[\d,]{3,})\b(?!.*\b\d[\d,]{3,}\b)");
                if (amt.Success) amount = ParseNum(amt.Groups[1].Value);
                purpose = rest;
            }
            enquiryDetails.Add(new BsaCibilEnquiryDetail { Date = date, Member = member, Purpose = purpose, Amount = amount, Raw = line });
        }

        if (enquiryDetails.Count == 0)
        {
            var upper = joinedFlat.ToUpperInvariant();
            var start = upper.IndexOf("CREDIT ENQUIRIES", StringComparison.Ordinal);
            if (start >= 0)
            {
                var seg = joinedFlat.Substring(start, Math.Min(25000, joinedFlat.Length - start));
                var hdr = seg.ToUpperInvariant().IndexOf("ENQUIRED ON", StringComparison.Ordinal);
                if (hdr >= 0) seg = seg[(hdr + "ENQUIRED ON".Length)..];
                var purposeRe = @"(?:PERSONAL\s+LOAN|EDUCATION\s+LOAN|HOME\s+LOAN|VEHICLE\s+LOAN|TWO\s+WHEELER\s+LOAN|GOLD\s+LOAN|BUSINESS\s+LOAN(?:\s+GENERAL)?|CONSUMER\s+LOAN|LOAN\s+AGAINST\s+PROPERTY|CREDIT\s+CARD|OVERDRAFT|OTHER)";
                var rowRe = new Regex($@"\b(\d{{1,3}})\s+({purposeRe})\s+([A-Z][A-Z0-9 &]{{2,80}})\s+(\d{{2}}-\d{{2}}-\d{{4}})\b", RegexOptions.IgnoreCase);
                foreach (Match m in rowRe.Matches(seg))
                {
                    if (!int.TryParse(m.Groups[1].Value, out var sr) || sr < 1 || sr > 300) continue;
                    var member = m.Groups[3].Value.Trim();
                    if (member.Length == 0 || Regex.IsMatch(member, @"\bREPORT\b|\bCONTENTS\b", RegexOptions.IgnoreCase)) continue;
                    var purpose = m.Groups[2].Value.Trim();
                    if (purpose.Length < 3) continue;
                    enquiryDetails.Add(new BsaCibilEnquiryDetail
                    { Date = ParseDate(m.Groups[4].Value), Member = member, Purpose = purpose, Amount = null, Raw = m.Value });
                }
            }
        }

        if (enquiryDetails.Count > 0 && !enquiries.ContainsKey("total")) enquiries["total"] = enquiryDetails.Count;

        // Accounts (line-wise state machine)
        var accounts = new List<BsaCibilAccount>();
        AccBuilder? cur = null;
        void Push()
        {
            if (cur != null && cur.SetCount > 0) accounts.Add(cur.ToAccount());
            cur = null;
        }

        var startRe = new Regex(@"^(ACCOUNT|ACCT)\s*TYPE\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase);
        var memberRe = new Regex(@"^(?:MEMBER\s*NAME|LENDER|BANK\s*NAME|FINANCIER|INSTITUTION)\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase);
        foreach (var line in lines)
        {
            var start = startRe.Match(line);
            if (start.Success)
            {
                Push();
                cur = new AccBuilder { AccountType = start.Groups[2].Value.Trim() };
                continue;
            }
            var memberStart = memberRe.Match(line);
            if (memberStart.Success && cur == null)
            {
                cur = new AccBuilder { Lender = memberStart.Groups[1].Value.Trim() };
                continue;
            }
            if (memberStart.Success && cur != null && !string.IsNullOrEmpty(cur.Lender) && cur.SetCount >= 2)
            {
                Push();
                cur = new AccBuilder { Lender = memberStart.Groups[1].Value.Trim() };
                continue;
            }
            if (cur == null) continue;

            cur.Set(ref cur.Lender, memberRe, line);
            cur.Set(ref cur.AccountStatus, Rx2(@"^(?:ACCOUNT\s*STATUS|STATUS)\s*[:\-]\s*(.+)$"), line);
            cur.Set(ref cur.Ownership, Rx2(@"^(?:OWNERSHIP|ACCOUNT\s*HOLDER\s*TYPE)\s*[:\-]\s*(.+)$"), line);
            cur.Set(ref cur.OpenedDate, Rx2(@"^(?:DATE\s*OPENED|OPENED\s*DATE|DATE\s*OPEN)\s*[:\-]\s*(.+)$"), line);
            cur.Set(ref cur.ClosedDate, Rx2(@"^(?:DATE\s*CLOSED|CLOSED\s*DATE)\s*[:\-]\s*(.+)$"), line);

            cur.SetMoney(ref cur.Emi, @"\bEMI\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);
            cur.SetMoney(ref cur.CurrentBalance, @"\bCURRENT\s*(?:BALANCE|BAL|CURR\.?\s*BAL)\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);
            cur.SetMoney(ref cur.SanctionedAmount, @"\bSANCTION(?:ED)?\s*(?:AMOUNT)?\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);
            cur.SetMoney(ref cur.HighCredit, @"\bHIGH\s*(?:CREDIT|CR)\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);
            cur.SetMoney(ref cur.CreditLimit, @"\b(?:CREDIT\s*LIMIT|LIMIT)\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);
            cur.SetMoney(ref cur.OverdueAmount, @"\b(?:OVERDUE|OD\s*AMOUNT|OVER\s*DUE)\b[^0-9]{0,20}([\d,]+(?:\.\d{1,2})?)\b", line);

            if (Regex.IsMatch(line, @"\b(WRITE\s*OFF|WRITTEN\s*OFF|SETTLED|SETTLEMENT|SUIT\s*FILED|WILFUL\s*DEFAULT|LOSS|SPECIAL\s*MENTION)\b", RegexOptions.IgnoreCase))
            {
                cur.AdverseFlags ??= new List<string>();
                var f = line.ToUpperInvariant();
                void Add(string flag) { if (!cur.AdverseFlags.Contains(flag)) cur.AdverseFlags.Add(flag); }
                if (Regex.IsMatch(f, @"WRITE\s*OFF|WRITTEN\s*OFF", RegexOptions.IgnoreCase)) Add("WRITTEN_OFF");
                if (Regex.IsMatch(f, @"SETTLED|SETTLEMENT", RegexOptions.IgnoreCase)) Add("SETTLED");
                if (Regex.IsMatch(f, @"SUIT\s*FILED", RegexOptions.IgnoreCase)) Add("SUIT_FILED");
                if (Regex.IsMatch(f, @"WILFUL\s*DEFAULT", RegexOptions.IgnoreCase)) Add("WILFUL_DEFAULT");
                if (Regex.IsMatch(f, @"\bLOSS\b", RegexOptions.IgnoreCase)) Add("LOSS");
                if (Regex.IsMatch(f, @"SPECIAL\s*MENTION", RegexOptions.IgnoreCase)) Add("SMA");
            }

            if (Regex.IsMatch(line, @"\bDPD\b", RegexOptions.IgnoreCase))
            {
                var nums = Regex.Matches(line, @"\b\d{2,3}\b").Select(mx => int.TryParse(mx.Value, out var n) ? n : 0).ToList();
                var nonZero = nums.Where(n => n > 0).ToList();
                var max = nonZero.Count > 0 ? nonZero.Max() : 0;
                cur.DpdMax = Math.Max(cur.DpdMax ?? 0, max);
            }
        }
        Push();

        if (!accounts.Any(a => !string.IsNullOrEmpty(a.AccountNo)))
        {
            var upper = joinedFlat.ToUpperInvariant();
            var idx = upper.IndexOf("SUMMARY: LOAN ACCOUNTS", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var seg = joinedFlat.Substring(idx, Math.Min(40000, joinedFlat.Length - idx));
                var hdr = seg.ToUpperInvariant().IndexOf("OUTSTANDING BALANCE", StringComparison.Ordinal);
                if (hdr >= 0) seg = seg[(hdr + "OUTSTANDING BALANCE".Length)..];
                var typeRe = @"(?:Personal\s+Loan|Education\s+Loan|Home\s+Loan|Vehicle\s+Loan|Two\s+Wheeler\s+Loan|Gold\s+Loan|Business\s+Loan(?:\s+General)?|Consumer\s+Loan|Loan\s+Against\s+Property|Overdraft|Credit\s+Card|Other)";
                var rowRe = new Regex(@"([A-Z][A-Z0-9 &.'-]{2,80}?)\s+(" + typeRe + @")\s+(X{3,}\d{3,})\s+(Individual|Guarantor|Joint|Co-Applicant|Co Applicant|CoApplicant|Authorized User|Authorised User)\s+(\d{2}-\d{2}-\d{4})\s+(Active\*?\*?|Closed|Settled|Written\s*Off|Suit\s*Filed|Wilful\s*Default|Loss|Special\s*Mention|SMA)\s+(\d{2}-\d{2}-\d{4})\s+([\d,]+)\s+([\d,]+)", RegexOptions.IgnoreCase);
                foreach (Match m in rowRe.Matches(seg))
                {
                    var lender = m.Groups[1].Value.Trim();
                    if (lender.Length == 0 || Regex.IsMatch(lender, @"\bDATE\b|\bACCOUNT\b|\bSTATUS\b|\bUPDATE\b", RegexOptions.IgnoreCase)) continue;
                    accounts.Add(new BsaCibilAccount
                    {
                        Lender = lender,
                        AccountType = m.Groups[2].Value.Trim(),
                        AccountNo = m.Groups[3].Value.Trim(),
                        Ownership = m.Groups[4].Value.Trim(),
                        OpenedDate = ParseDate(m.Groups[5].Value),
                        AccountStatus = Regex.Replace(m.Groups[6].Value, @"\s+", " ").Trim(),
                        LastUpdate = ParseDate(m.Groups[7].Value),
                        SanctionedAmount = ParseNum(m.Groups[8].Value),
                        CurrentBalance = ParseNum(m.Groups[9].Value),
                    });
                }
            }
        }

        if (accounts.Count > 0)
        {
            var seen = new HashSet<string>();
            var uniq = new List<BsaCibilAccount>();
            foreach (var a in accounts)
            {
                var key = $"{(a.AccountNo ?? "").Trim()}|{(a.Lender ?? "").Trim()}|{(a.AccountType ?? "").Trim()}";
                if (string.IsNullOrEmpty(a.AccountNo) || seen.Contains(key)) continue;
                seen.Add(key);
                uniq.Add(a);
            }
            accounts = uniq;
        }

        var dpdMaxAll = accounts.Count > 0 ? accounts.Max(a => a.DpdMax ?? 0) : 0;
        var adverseAll = accounts
            .SelectMany(a => a.AdverseFlags ?? new List<string>())
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        return new BsaCibilReport
        {
            Score = score,
            ReportDate = report.ReportDate,
            Personal = personal,
            Enquiries = enquiries,
            EnquiryDetails = enquiryDetails,
            Accounts = accounts,
            DpdMax = dpdMaxAll > 0 ? dpdMaxAll : null,
            AdverseFlags = adverseAll,
            RawCharCount = t.Length,
        };
    }

    private static Regex Rx2(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class AccBuilder
    {
        public string? Lender; public string? AccountType; public string? AccountNo; public string? AccountStatus;
        public string? Ownership; public string? OpenedDate; public string? ClosedDate; public string? LastUpdate;
        public decimal? Emi; public decimal? CurrentBalance; public decimal? SanctionedAmount; public decimal? HighCredit;
        public decimal? CreditLimit; public decimal? OverdueAmount; public int? DpdMax; public List<string>? AdverseFlags;

        public int SetCount =>
            (Lender != null ? 1 : 0) + (AccountType != null ? 1 : 0) + (AccountNo != null ? 1 : 0) +
            (AccountStatus != null ? 1 : 0) + (Ownership != null ? 1 : 0) + (OpenedDate != null ? 1 : 0) +
            (ClosedDate != null ? 1 : 0) + (LastUpdate != null ? 1 : 0) + (Emi != null ? 1 : 0) +
            (CurrentBalance != null ? 1 : 0) + (SanctionedAmount != null ? 1 : 0) + (HighCredit != null ? 1 : 0) +
            (CreditLimit != null ? 1 : 0) + (OverdueAmount != null ? 1 : 0) + (DpdMax != null ? 1 : 0) +
            (AdverseFlags != null ? 1 : 0);

        public void Set(ref string? field, Regex re, string line)
        {
            if (field != null) return;
            var m = re.Match(line);
            if (m.Success && m.Groups[1].Value.Length > 0) field = m.Groups[1].Value.Trim();
        }

        public void SetMoney(ref decimal? field, string pattern, string line)
        {
            if (field != null) return;
            var m = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 0) field = ParseNum(m.Groups[1].Value);
        }

        public BsaCibilAccount ToAccount() => new()
        {
            Lender = Lender, AccountType = AccountType, AccountNo = AccountNo, AccountStatus = AccountStatus,
            Ownership = Ownership, OpenedDate = OpenedDate, ClosedDate = ClosedDate, LastUpdate = LastUpdate,
            Emi = Emi, CurrentBalance = CurrentBalance, SanctionedAmount = SanctionedAmount, HighCredit = HighCredit,
            CreditLimit = CreditLimit, OverdueAmount = OverdueAmount, DpdMax = DpdMax, AdverseFlags = AdverseFlags,
        };
    }

    // ════════════════════════════════════════════════════════════════
    // CROSS-VERIFY / ELIGIBILITY / UNDERWRITING
    // ════════════════════════════════════════════════════════════════

    public static BsaCrossVerifyResult? CrossVerifyCibil(BsaAnalysisResult? bankAnalysis, BsaCibilReport? cibil)
    {
        if (bankAnalysis == null || cibil == null) return null;

        string NormName(string? s) => Regex.Replace(
            Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9 ]", " "), @"\s+", " ").Trim();
        List<string> Tokens(string s) => NormName(s).Split(' ').Where(x => x.Length >= 4).ToList();

        var stmt = new List<string>();
        foreach (var e in bankAnalysis.EmiPayments) if (!string.IsNullOrEmpty(e.Bank)) stmt.Add(e.Bank);
        foreach (var k in bankAnalysis.LoanSummary) stmt.Add(k.Bank);

        var stmtNames = stmt.Select(NormName).Where(x => x.Length > 0 && !Regex.IsMatch(x, "UNKNOWN")).Distinct().ToList();
        var cibilNames = cibil.Accounts.Select(a => NormName(a.Lender)).Where(x => x.Length > 0).Distinct().ToList();

        bool IsMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a.Contains(b) || b.Contains(a)) return true;
            var ta = Tokens(a);
            var tb = Tokens(b);
            if (ta.Count == 0 || tb.Count == 0) return false;
            var setB = new HashSet<string>(tb);
            return ta.Any(t => setB.Contains(t));
        }

        var matched = new List<BsaCrossVerifyMatch>();
        var missingInStatement = new List<string>();
        foreach (var cn in cibilNames)
        {
            var found = stmtNames.FirstOrDefault(sn => IsMatch(cn, sn));
            if (found != null) matched.Add(new BsaCrossVerifyMatch { CibilLender = cn, StatementLender = found });
            else missingInStatement.Add(cn);
        }

        var missingInCibil = stmtNames.Where(sn => cibilNames.All(cn => !IsMatch(cn, sn))).ToList();

        return new BsaCrossVerifyResult { Matched = matched, MissingInStatement = missingInStatement, MissingInCibil = missingInCibil };
    }

    public static BsaEligibility AssessEligibility(BsaAnalysisResult bankAnalysis, BsaCibilReport cibil)
    {
        int? score = cibil.Score;
        var dpdMax = cibil.DpdMax ?? 0;
        var bounceCount = bankAnalysis.BounceCharges.Count;

        static string MonthKeyIso(string? dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return "";
            if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return dateStr.Length >= 7 ? dateStr[..7] : dateStr;
        }

        static Dictionary<string, decimal> SumByMonth(IEnumerable<(string? Date, decimal Amount)> items)
        {
            var outp = new Dictionary<string, decimal>();
            foreach (var (date, amount) in items)
            {
                var m = MonthKeyIso(date);
                if (m.Length == 0) continue;
                outp[m] = (outp.TryGetValue(m, out var v) ? v : 0) + amount;
            }
            return outp;
        }

        var salaryByMonth = SumByMonth(bankAnalysis.Salary.Select(s => (s.Date, s.Amount)));
        var emiByMonth = SumByMonth(bankAnalysis.EmiPayments.Select(e => (e.Date, e.EmiAmount)));
        var rentByMonth = SumByMonth(bankAnalysis.Rent.Select(r => (r.Date, r.Amount)));
        var apyByMonth = SumByMonth(bankAnalysis.ApyPension.Select(a => (a.Date, a.Amount)));
        var appRepayByMonth = SumByMonth(bankAnalysis.AppLoans.Where(x => string.Equals(x.Type, "repayment", StringComparison.OrdinalIgnoreCase)).Select(a => (a.Date, a.Amount)));
        var ccByMonth = SumByMonth(bankAnalysis.CreditCardPayments.Select(c => (c.Date, c.Amount)));

        var months = salaryByMonth.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var salaryMonths = months.Where(m => salaryByMonth[m] > 0).ToList();
        var avgSalary = salaryMonths.Count > 0 ? salaryMonths.Sum(m => salaryByMonth[m]) / salaryMonths.Count : 0;

        decimal AvgOverMonths(Dictionary<string, decimal> byMonth)
        {
            var ms = salaryMonths.Count > 0 ? salaryMonths : byMonth.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (ms.Count == 0) return 0;
            return ms.Sum(m => byMonth.TryGetValue(m, out var v) ? v : 0) / ms.Count;
        }

        var monthlyEmi = AvgOverMonths(emiByMonth);
        var monthlyRent = AvgOverMonths(rentByMonth);
        var monthlyApy = AvgOverMonths(apyByMonth);
        var monthlyApp = AvgOverMonths(appRepayByMonth);
        var monthlyCc = AvgOverMonths(ccByMonth);
        var obligations = monthlyEmi + monthlyRent + monthlyApy + monthlyApp + monthlyCc;
        double? foir = avgSalary > 0 ? (double)(obligations / avgSalary) : null;

        var reasons = new List<string>();
        var decision = "Needs Review";

        if (score != null)
        {
            if (score < 650) reasons.Add($"Low CIBIL score ({score})");
            else if (score >= 750) reasons.Add($"Strong CIBIL score ({score})");
            else reasons.Add($"Moderate CIBIL score ({score})");
        }
        else reasons.Add("CIBIL score not detected in report text");

        if (dpdMax >= 30) reasons.Add($"Delinquency detected (DPD max {dpdMax})");
        if (bounceCount > 0) reasons.Add($"{bounceCount} bounce/return charge(s) in statement");
        if (foir != null) reasons.Add($"FOIR (fixed obligations / income) ~ {(foir.Value * 100).ToString("0", CultureInfo.InvariantCulture)}%");

        if (score != null && score < 650) decision = "Unlikely";
        else if (dpdMax >= 30) decision = "Unlikely";
        else if (foir != null && foir > 0.6) decision = "Unlikely";
        else if (score != null && score >= 750 && foir != null && foir <= 0.5 && dpdMax < 30) decision = "Likely Eligible";

        return new BsaEligibility
        {
            Decision = decision,
            CibilScore = score,
            DpdMax = dpdMax > 0 ? dpdMax : null,
            AvgMonthlyIncome = avgSalary > 0 ? avgSalary : null,
            AvgMonthlyObligations = obligations > 0 ? obligations : null,
            Foir = foir != null ? Math.Round(foir.Value, 4) : null,
            BounceCount = bounceCount,
            Reasons = reasons,
        };
    }

    public static BsaUnderwriting AssessUnderwriting(BsaAnalysisResult bankAnalysis, BsaCibilReport cibil)
    {
        var score = cibil.Score;
        var dpdMax = cibil.DpdMax ?? 0;

        var accounts = cibil.Accounts;
        var enquiries = cibil.Enquiries;
        decimal Recent(string key) => enquiries.TryGetValue(key, out var v) ? v : 0;
        var recentEnq = (int)(Recent("last_30d") != 0 ? Recent("last_30d") : Recent("last_60d") != 0 ? Recent("last_60d") : Recent("last_90d"));

        string Norm(string? s) => Regex.Replace(
            Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9 ]", " "), @"\s+", " ").Trim();
        bool IsActive(BsaCibilAccount a)
        {
            var st = Norm(a.AccountStatus);
            if (st.Length == 0) return true;
            return !Regex.IsMatch(st, @"\bCLOSED\b");
        }
        string Classify(BsaCibilAccount a)
        {
            var t = Norm(a.AccountType);
            if (t.Length == 0) return "UNKNOWN";
            if (Regex.IsMatch(t, @"\bCREDIT\s*CARD\b|\bCARD\b")) return "CREDIT_CARD";
            if (Regex.IsMatch(t, @"\bHOME\b|\bHOUSING\b|\bMORTGAGE\b|\bHFL\b")) return "SECURED";
            if (Regex.IsMatch(t, @"\bAUTO\b|\bCAR\b|\bVEHICLE\b|\bTWO\b")) return "SECURED";
            if (Regex.IsMatch(t, @"\bGOLD\b")) return "SECURED";
            if (Regex.IsMatch(t, @"\bPERSONAL\b|\bCONSUMER\b|\bUNSECURED\b")) return "UNSECURED";
            if (Regex.IsMatch(t, @"\bBUSINESS\b|\bMSME\b|\bSME\b|\bTERM\b|\bWORKING\b")) return "BUSINESS";
            return "OTHER";
        }

        var active = accounts.Where(IsActive).ToList();
        var unsecured = active.Where(a => Classify(a) == "UNSECURED").ToList();
        var creditCards = active.Where(a => Classify(a) == "CREDIT_CARD").ToList();

        var hasSevere = accounts.Any(a => (a.AdverseFlags ?? new List<string>()).Any(f =>
            f is "WRITTEN_OFF" or "SETTLED" or "SUIT_FILED" or "WILFUL_DEFAULT" or "LOSS"));

        double? ccUtil = null;
        {
            decimal bal = 0, lim = 0;
            foreach (var a in creditCards) { bal += a.CurrentBalance ?? 0; lim += a.CreditLimit ?? 0; }
            if (lim > 0) ccUtil = Math.Round((double)(bal / lim), 4);
        }

        var bounceCount = bankAnalysis.BounceCharges.Count;
        var eligibility = AssessEligibility(bankAnalysis, cibil);

        var foir = eligibility.Foir;
        var avgIncome = eligibility.AvgMonthlyIncome;
        var avgObl = eligibility.AvgMonthlyObligations;

        var policyFlags = new List<string>();
        if (score != null && score < 650) policyFlags.Add("LOW_SCORE");
        if (score != null && score >= 800) policyFlags.Add("EXCELLENT_SCORE");
        if (dpdMax >= 30) policyFlags.Add("DPD_30_PLUS");
        if (hasSevere) policyFlags.Add("SEVERE_DEROGATORY");
        if (recentEnq >= 6) policyFlags.Add("HIGH_ENQUIRIES");
        if (ccUtil != null && ccUtil > 0.8) policyFlags.Add("HIGH_CC_UTILIZATION");
        if (bounceCount >= 2) policyFlags.Add("MULTIPLE_BOUNCES");
        if (foir != null && foir > 0.6) policyFlags.Add("HIGH_FOIR");

        double? maxFoir = score == null ? null : score >= 780 ? 0.6 : score >= 720 ? 0.55 : score >= 680 ? 0.5 : 0.45;
        decimal? maxEmi = avgIncome != null && maxFoir != null
            ? Math.Max(0, (decimal)(avgIncome.Value * (decimal)maxFoir.Value) - (avgObl ?? 0))
            : null;

        var grade = "C";
        if (hasSevere) grade = "D";
        else if (dpdMax >= 30) grade = "D";
        else if (score == null) grade = "C";
        else if (score >= 780 && (ccUtil == null || ccUtil <= 0.6) && recentEnq <= 3) grade = "A";
        else if (score >= 720) grade = "B";
        else if (score >= 650) grade = "C";
        else grade = "D";

        BsaProductEligibility Product(string code, string label, int minScore, bool allowDpd)
        {
            var okScore = score != null && score >= minScore;
            var okDpd = allowDpd ? dpdMax < 60 : dpdMax < 30;
            var okDerog = !hasSevere;
            return new BsaProductEligibility { Code = code, Label = label, Eligible = okScore && okDpd && okDerog, MinScore = minScore };
        }

        var productEligibility = new List<BsaProductEligibility>
        {
            Product("PL", "Personal Loan", 720, false),
            Product("HL", "Home Loan", 700, false),
            Product("BL", "Business Loan", 700, false),
            Product("VL", "Vehicle Loan", 680, false),
            Product("CC", "Credit Card", 720, false),
        };

        return new BsaUnderwriting
        {
            Grade = grade,
            CibilScore = score,
            DpdMax = dpdMax > 0 ? dpdMax : null,
            EnquiriesRecent = recentEnq,
            AccountsTotal = accounts.Count,
            AccountsActive = active.Count,
            UnsecuredActive = unsecured.Count,
            CreditCardsActive = creditCards.Count,
            CreditCardUtilization = ccUtil,
            BounceCount = bounceCount,
            Foir = foir,
            AvgMonthlyIncome = avgIncome,
            AvgMonthlyObligations = avgObl,
            MaxFoirPolicy = maxFoir,
            AdditionalEmiCapacity = maxEmi != null ? Math.Round(maxEmi.Value, 2) : null,
            PolicyFlags = policyFlags,
            ProductEligibility = productEligibility,
        };
    }
}
