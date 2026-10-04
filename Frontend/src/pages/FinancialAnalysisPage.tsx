import { useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';

// ── Types (mirror the backend DTOs, camelCase JSON) ──────────────────────────

interface BsaMeta {
  referenceId?: string | null;
  utr?: string | null;
  rrn?: string | null;
  nachRef?: string | null;
  loanAccountMasked?: string | null;
  lenderRaw?: string | null;
  emiCycle?: string | null;
  emiDay?: string | null;
  principalAmount?: number | null;
  interestAmount?: number | null;
  sourceType?: string | null;
  sourceSheet?: string | null;
  sourceRow?: number | null;
}

interface BsaAnalysis {
  transactions?: (BsaMeta & { date: string; description: string; debit: number; credit: number; balance: number; category: string; counterparty: string; instrument: string; activity: string; needsReview: boolean; classificationReason: string })[];
  accountSummary: {
    totalCredits: number; totalDebits: number; netBalance: number;
    transactionCount: number; period: string; openingBalance: number; closingBalance: number;
  };
  salary: (BsaMeta & { date: string; description: string; employer: string; amount: number; mode: string; month: string })[];
  pfEpfo: (BsaMeta & { date: string; description: string; amount: number; type: string; source: string })[];
  loanDisbursements: (BsaMeta & { date: string; description: string; lender: string; lenderCategory: string; loanTypeLabel: string; amount: number; mode: string; loanAccountNumber: string; sanctionAmount: number; processingFee?: number | null })[];
  emiPayments: (BsaMeta & { date: string; description: string; bank: string; bankCategory: string; loanTypeLabel: string; emiAmount: number; month: string; mode: string; loanAccountNumber: string })[];
  otherIncome: { date: string; description: string; amount: number; type: string }[];
  rent: (BsaMeta & { date: string; payee: string; amount: number; description: string; mode: string })[];
  sipInvestments: (BsaMeta & { date: string; fundName: string; platform: string; amount: number; description: string; investmentType?: string })[];
  insurance: (BsaMeta & { date: string; provider: string; policyType: string; amount: number; description: string })[];
  appLoans: (BsaMeta & { date: string; appName: string; maxApr: number; amount: number; type: string; description: string })[];
  utilities: (BsaMeta & { date: string; description: string; amount: number; category: string })[];
  creditCardPayments: (BsaMeta & { date: string; description: string; amount: number; ccDisplay: string; ccIssuerBank?: string | null; ccChannel?: string | null })[];
  bounceCharges: (BsaMeta & { date: string; description: string; amount: number; category: string; channel?: string | null })[];
  stockMarket: (BsaMeta & { date: string; direction: string; platform: string; amount: number; description: string; segment?: string; activity?: string; needsReview?: boolean; classificationReason?: string })[];
  securities: (BsaMeta & {
    date: string; description: string; direction: string;
    platform: string; instrument: string; amount: number; activity?: string;
  })[];
  apyPension: (BsaMeta & { date: string; description: string; amount: number })[];
  govtSchemeCredits: { date: string; description: string; amount: number; schemeName: string; type: string }[];
  otherDebits: { date: string; description: string; amount: number }[];
  monthlySummary: { month: string; credits: number; debits: number; salary: number; emi: number; pf: number; disbursements: number; closingBalance: number }[];
  loanSummary: { bank: string; bankCategory: string; loanTypeLabel: string; emiAmount: number; count: number; totalPaid: number }[];
  insights: {
    obligationToIncomeRatio: number; savingsRate: number; avgMonthlySalary: number;
    totalEmiMonthly: number; totalDisbursed: number; estimatedTotalDebt: number;
    riskFlags: string[]; recommendations: string[]; additionalFieldsNeeded: string[];
  };
}

interface BsaCibil {
  score?: number | null;
  reportDate?: string | null;
  personal: { name?: string | null; dob?: string | null; gender?: string | null; pan?: string | null; mobile?: string | null; email?: string | null; company?: string | null; addresses: string[] };
  enquiries: Record<string, number>;
  enquiryDetails: { date?: string | null; member: string; purpose: string; amount?: number | null; raw: string }[];
  accounts: { lender?: string | null; accountType?: string | null; accountNo?: string | null; accountStatus?: string | null; ownership?: string | null; openedDate?: string | null; closedDate?: string | null; emi?: number | null; currentBalance?: number | null; sanctionedAmount?: number | null; creditLimit?: number | null; overdueAmount?: number | null; dpdMax?: number | null; adverseFlags?: string[] | null }[];
  dpdMax?: number | null;
  adverseFlags: string[];
}

interface BsaResponse {
  success: boolean;
  filename: string;
  transactionCount: number;
  /** Present on /api/bsa/analyze-flex. False means a CIBIL-only run. */
  statementProvided?: boolean;
  cibilProvided?: boolean;
  analysis: BsaAnalysis;
  cibil?: BsaCibil | null;
  crossVerify?: { matched: { cibilLender: string; statementLender: string }[]; missingInStatement: string[]; missingInCibil: string[] } | null;
  eligibility?: { decision: string; cibilScore?: number | null; dpdMax?: number | null; avgMonthlyIncome?: number | null; avgMonthlyObligations?: number | null; foir?: number | null; bounceCount: number; reasons: string[] } | null;
  underwriting?: {
    grade: string; cibilScore?: number | null; dpdMax?: number | null; enquiriesRecent: number;
    accountsTotal: number; accountsActive: number; unsecuredActive: number; creditCardsActive: number;
    creditCardUtilization?: number | null; bounceCount: number; foir?: number | null;
    avgMonthlyIncome?: number | null; avgMonthlyObligations?: number | null;
    maxFoirPolicy?: number | null; additionalEmiCapacity?: number | null;
    policyFlags: string[]; productEligibility: { code: string; label: string; eligible: boolean; minScore: number }[];
  } | null;
}

interface BsaHistoryEntry {
  id: string;
  savedAt: string;
  filename: string;
  txnCount: number;
  hasCibil: boolean;
  result: BsaResponse;
}

// ── Helpers ──────────────────────────────────────────────────────────────────

const money = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 });
const fmtMoney = (n: number | null | undefined) => (n == null ? '' : `₹${money.format(n)}`);
const fmtNum = (n: number | null | undefined) => (n == null ? '' : money.format(n));

// Deliberately a DIFFERENT storage key from the CIBIL Analyzer's
// 'cip_bsa_history' so the two screens never read or overwrite each other's
// saved analyses.
const HISTORY_KEY = 'cip_financial_analysis_history';

function loadHistory(): BsaHistoryEntry[] {
  try {
    const raw = localStorage.getItem(HISTORY_KEY);
    const parsed = raw ? (JSON.parse(raw) as BsaHistoryEntry[]) : [];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function saveHistory(entries: BsaHistoryEntry[]) {
  try {
    localStorage.setItem(HISTORY_KEY, JSON.stringify(entries));
  } catch {
    // Storage full — drop the oldest half and retry once.
    try {
      localStorage.setItem(HISTORY_KEY, JSON.stringify(entries.slice(0, Math.max(1, Math.floor(entries.length / 2)))));
    } catch { /* give up silently */ }
  }
}

interface Column { key: string; label: string; money?: boolean; num?: boolean }

function DataTable({ columns, rows }: { columns: Column[]; rows: Record<string, unknown>[] }) {
  if (!rows.length) return <p className="muted">No entries detected in this statement.</p>;
  return (
    <div className="table-scroll">
      <table className="data-table">
        <thead>
          <tr>{columns.map((c) => <th key={c.key}>{c.label}</th>)}</tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              {columns.map((c) => (
                <td key={c.key} style={c.money || c.num ? { textAlign: 'right', whiteSpace: 'nowrap' } : undefined}>
                  {c.money ? fmtMoney(r[c.key] as number) : c.num ? fmtNum(r[c.key] as number) : typeof r[c.key] === 'boolean' ? (r[c.key] ? 'Yes' : 'No') : String(r[c.key] ?? '')}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── Tabs ─────────────────────────────────────────────────────────────────────

const MAIN_TABS = [
  'Summary', 'Transactions', 'Monthly', 'Salary', 'Loans (EMI)', 'Disbursements', 'Credit Card',
  'App Loans', 'Bounces', 'Rent', 'SIP / MF', 'Securities', 'Insurance', 'Stock Market', 'Utilities',
] as const;
const MORE_TABS = [
  'APY Pension', 'Other Income', 'Other Debits', 'Insights', 'CIBIL', 'Underwriting',
] as const;
type Tab = (typeof MAIN_TABS)[number] | (typeof MORE_TABS)[number];

// ── Page ─────────────────────────────────────────────────────────────────────

export default function BsaAnalyzerPage() {
  const [statement, setStatement] = useState<File | null>(null);
  const [cibil, setCibil] = useState<File | null>(null);
  const [cibilPassword, setCibilPassword] = useState('');
  const [showPwd, setShowPwd] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [result, setResult] = useState<BsaResponse | null>(null);
  const [tab, setTab] = useState<Tab>('Summary');
  const [openMore, setOpenMore] = useState(false);
  const [history, setHistory] = useState<BsaHistoryEntry[]>(() => loadHistory());
  const [showHistory, setShowHistory] = useState(false);
  const [dragOver, setDragOver] = useState<'stmt' | 'cibil' | null>(null);
  const stmtRef = useRef<HTMLInputElement>(null);
  const cibilRef = useRef<HTMLInputElement>(null);

  const analyze = async () => {
    if (!statement && !cibil) {
      setError('Choose a bank statement, a CIBIL report, or both.');
      return;
    }
    setBusy(true); setError(''); setNotice(''); setResult(null);
    try {
      const form = new FormData();
      if (statement) form.append('statement', statement);
      if (cibil) form.append('cibil', cibil);
      if (cibilPassword.trim()) {
        // One field serves both PDFs: whichever upload is encrypted, the same
        // password is tried against it. Sent under both names so the backend
        // applies it to the right file.
        form.append('cibilPassword', cibilPassword.trim());
        form.append('statementPassword', cibilPassword.trim());
      }
      // analyze-flex, not analyze: this screen allows a CIBIL-only upload,
      // which the original endpoint deliberately does not.
      const res = await api.upload<BsaResponse>('/api/bsa/analyze-flex', form);
      setResult(res);
      setTab('Summary');
      setNotice(
        (statement
          ? `Analyzed ${res.transactionCount} transactions from ${res.filename}`
          : 'No bank statement supplied — showing CIBIL-only analysis') +
        (res.cibil ? ' with CIBIL report' : ''),
      );
      const entry: BsaHistoryEntry = {
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        savedAt: new Date().toISOString(),
        filename: res.filename,
        txnCount: res.transactionCount,
        hasCibil: !!res.cibil,
        result: res,
      };
      const next = [entry, ...loadHistory()].slice(0, 8);
      setHistory(next);
      saveHistory(next);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Analysis failed.');
    } finally {
      setBusy(false);
    }
  };

  const clearAll = () => {
    setStatement(null);
    setCibil(null);
    setCibilPassword('');
    setResult(null);
    setError('');
    setNotice('');
    if (stmtRef.current) stmtRef.current.value = '';
    if (cibilRef.current) cibilRef.current.value = '';
  };

  const openHistoryEntry = (entry: BsaHistoryEntry) => {
    setResult(entry.result);
    setTab('Summary');
    setShowHistory(false);
    setError('');
    setNotice(`Loaded saved analysis of ${entry.filename} (${new Date(entry.savedAt).toLocaleString()}).`);
  };

  const deleteHistoryEntry = (id: string) => {
    const next = loadHistory().filter((h) => h.id !== id);
    setHistory(next);
    saveHistory(next);
  };

  const a = result?.analysis;

  // Section table configs (used by tabs + export)
  const tables = useMemo(() => {
    if (!a) return [] as { id: Tab; columns: Column[]; rows: Record<string, unknown>[] }[];
    return [
      { id: 'Transactions' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'description', label: 'Narration' },
        { key: 'debit', label: 'Debit', money: true }, { key: 'credit', label: 'Credit', money: true },
        { key: 'balance', label: 'Balance', money: true }, { key: 'category', label: 'Nature' },
        { key: 'counterparty', label: 'Counterparty' }, { key: 'instrument', label: 'Instrument / Segment' },
        { key: 'activity', label: 'Activity' }, { key: 'needsReview', label: 'Review needed' },
        { key: 'classificationReason', label: 'Basis / Limitation' },
      ], rows: (a.transactions ?? []) as unknown as Record<string, unknown>[] },
      { id: 'Monthly' as const, columns: [
        { key: 'month', label: 'Month' }, { key: 'credits', label: 'Credits', money: true },
        { key: 'debits', label: 'Debits', money: true }, { key: 'salary', label: 'Salary', money: true },
        { key: 'emi', label: 'EMI', money: true }, { key: 'pf', label: 'PF', money: true },
        { key: 'disbursements', label: 'Disbursed', money: true }, { key: 'closingBalance', label: 'Closing Bal', money: true },
      ], rows: a.monthlySummary as unknown as Record<string, unknown>[] },
      { id: 'Salary' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'employer', label: 'Employer' },
        { key: 'description', label: 'Description' }, { key: 'mode', label: 'Mode' },
        { key: 'month', label: 'Month' }, { key: 'amount', label: 'Amount', money: true },
      ], rows: a.salary as unknown as Record<string, unknown>[] },
      { id: 'Loans (EMI)' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'bank', label: 'Lender' },
        { key: 'bankCategory', label: 'Category' }, { key: 'loanTypeLabel', label: 'Loan Type' },
        { key: 'loanAccountNumber', label: 'Loan A/c' }, { key: 'mode', label: 'Mode' },
        { key: 'description', label: 'Description' },
        { key: 'emiAmount', label: 'EMI', money: true },
      ], rows: a.emiPayments as unknown as Record<string, unknown>[] },
      { id: 'Disbursements' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'lender', label: 'Lender' },
        { key: 'lenderCategory', label: 'Category' }, { key: 'loanTypeLabel', label: 'Loan Type' },
        { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', money: true },
        { key: 'processingFee', label: 'Proc. Fee', money: true },
      ], rows: a.loanDisbursements as unknown as Record<string, unknown>[] },
      { id: 'Credit Card' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'ccDisplay', label: 'Card' },
        { key: 'ccIssuerBank', label: 'Issuer' }, { key: 'ccChannel', label: 'Channel' },
        { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', money: true },
      ], rows: a.creditCardPayments as unknown as Record<string, unknown>[] },
      { id: 'App Loans' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'appName', label: 'App' },
        { key: 'description', label: 'Description' }, { key: 'maxApr', label: 'Max APR %', num: true },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.appLoans as unknown as Record<string, unknown>[] },
      { id: 'Bounces' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'category', label: 'Category' },
        { key: 'channel', label: 'Channel' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.bounceCharges as unknown as Record<string, unknown>[] },
      { id: 'Rent' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'payee', label: 'Payee' },
        { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', money: true },
      ], rows: a.rent as unknown as Record<string, unknown>[] },
      { id: 'SIP / MF' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'platform', label: 'Platform' },
        { key: 'fundName', label: 'Fund' }, { key: 'investmentType', label: 'Investment type' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.sipInvestments as unknown as Record<string, unknown>[] },
      { id: 'Securities' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'direction', label: 'Direction' },
        { key: 'instrument', label: 'Instrument' }, { key: 'platform', label: 'Rail' },
        { key: 'activity', label: 'Activity' },
        { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', money: true },
      ], rows: (a.securities ?? []) as unknown as Record<string, unknown>[] },
      { id: 'Insurance' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'provider', label: 'Provider' },
        { key: 'policyType', label: 'Type' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.insurance as unknown as Record<string, unknown>[] },
      { id: 'Utilities' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'category', label: 'Category' },
        { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', money: true },
      ], rows: a.utilities as unknown as Record<string, unknown>[] },
      { id: 'Stock Market' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'platform', label: 'Platform' },
        { key: 'direction', label: 'Direction' }, { key: 'segment', label: 'Segment' },
        { key: 'activity', label: 'Activity' }, { key: 'needsReview', label: 'Review needed' },
        { key: 'classificationReason', label: 'Basis / Limitation' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.stockMarket as unknown as Record<string, unknown>[] },
      { id: 'APY Pension' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.apyPension as unknown as Record<string, unknown>[] },
      { id: 'Other Income' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.otherIncome as unknown as Record<string, unknown>[] },
      { id: 'Other Debits' as const, columns: [
        { key: 'date', label: 'Date' }, { key: 'description', label: 'Description' },
        { key: 'amount', label: 'Amount', money: true },
      ], rows: a.otherDebits as unknown as Record<string, unknown>[] },
    ];
  }, [a]);

  const c = result?.cibil;
  const u = result?.underwriting;
  const el = result?.eligibility;
  const cv = result?.crossVerify;
  const moreActive = (MORE_TABS as readonly string[]).includes(tab);

  // ── Export to Excel ─────────────────────────────────────────────
  const exportExcel = async () => {
    if (!result || !a) return;
    const rows: string[][] = [];
    rows.push(['Bank Statement Analysis']);
    rows.push(['Classification is based on bank narration. Broker funding/payout is not proof of a buy, sell, trade profit, or F&O segment. Verify with contract notes or broker ledger.']);
    rows.push([`File: ${result.filename}`]);
    rows.push([`Period: ${a.accountSummary.period}`]);
    rows.push([`Generated: ${new Date().toLocaleString()}`]);
    rows.push([]);

    rows.push(['Account Summary']);
    rows.push(['Total Credits', String(a.accountSummary.totalCredits)]);
    rows.push(['Total Debits', String(a.accountSummary.totalDebits)]);
    rows.push(['Net Balance', String(a.accountSummary.netBalance)]);
    rows.push(['Transactions', String(a.accountSummary.transactionCount)]);
    rows.push(['Avg Monthly Salary', String(a.insights.avgMonthlySalary)]);
    rows.push(['Total Monthly EMI', String(a.insights.totalEmiMonthly)]);
    rows.push(['Total Disbursed', String(a.insights.totalDisbursed)]);
    rows.push(['Estimated Total Debt (EMI x 36)', String(a.insights.estimatedTotalDebt)]);
    rows.push(['EMI Obligation Ratio %', String(a.insights.obligationToIncomeRatio)]);
    rows.push(['Savings Rate %', String(a.insights.savingsRate)]);
    rows.push([]);

    for (const t of tables) {
      if (!t.rows.length) continue;
      rows.push([t.id]);
      rows.push(t.columns.map((c) => c.label));
      for (const r of t.rows) rows.push(t.columns.map((c) => typeof r[c.key] === 'boolean' ? (r[c.key] ? 'Yes' : 'No') : String(r[c.key] ?? '')));
      rows.push([]);
    }

    if (c) {
      rows.push(['CIBIL Report']);
      rows.push(['Score', String(c.score ?? 'Not detected')]);
      rows.push(['Report Date', c.reportDate ?? '']);
      rows.push(['Name', c.personal.name ?? '']);
      rows.push(['PAN', c.personal.pan ?? '']);
      rows.push(['Mobile', c.personal.mobile ?? '']);
      rows.push(['Email', c.personal.email ?? '']);
      rows.push(['DPD Max', String(c.dpdMax ?? 0)]);
      rows.push([]);
      if (c.accounts.length) {
        rows.push(['CIBIL Accounts']);
        rows.push(['Lender', 'Type', 'Account No', 'Status', 'Ownership', 'Opened', 'Balance', 'Sanctioned', 'DPD']);
        for (const acc of c.accounts) {
          rows.push([acc.lender ?? '', acc.accountType ?? '', acc.accountNo ?? '', acc.accountStatus ?? '',
            acc.ownership ?? '', acc.openedDate ?? '', String(acc.currentBalance ?? ''), String(acc.sanctionedAmount ?? ''), String(acc.dpdMax ?? 0)]);
        }
        rows.push([]);
      }
    }
    if (el) {
      rows.push(['Eligibility']);
      rows.push(['Decision', el.decision]);
      for (const r of el.reasons) rows.push([r]);
      rows.push([]);
    }
    if (u) {
      rows.push(['Underwriting']);
      rows.push(['Grade', u.grade]);
      rows.push(['Policy Flags', u.policyFlags.join('; ')]);
      rows.push(['Additional EMI Capacity', String(u.additionalEmiCapacity ?? '')]);
      rows.push(['Product', 'Min Score', 'Eligible']);
      for (const p of u.productEligibility) rows.push([p.label, String(p.minScore), p.eligible ? 'Yes' : 'No']);
      rows.push([]);
    }
    if (a.insights.riskFlags.length) {
      rows.push(['Risk Flags']);
      for (const r of a.insights.riskFlags) rows.push([r]);
      rows.push([]);
    }
    if (a.insights.recommendations.length) {
      rows.push(['Recommendations']);
      for (const r of a.insights.recommendations) rows.push([r]);
      rows.push([]);
    }

    try {
      const blob = await api.download('/api/bsa/export', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileName: result.filename, rows }),
      });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `BSA_${result.filename.replace(/\.[^.]+$/, '')}_${new Date().toISOString().slice(0, 10)}.xlsx`;
      link.click();
      URL.revokeObjectURL(url);
      setNotice('Excel report downloaded.');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Export failed.');
    }
  };

  // Summary stat cards (mirrors the reference design)
  const cards: { icon: string; tone: string; label: string; value: string; sub?: string; danger?: boolean }[] = a ? [
    { icon: 'fileText', tone: 'blue', label: 'Transactions', value: String(a.accountSummary.transactionCount), sub: a.accountSummary.period },
    { icon: 'trendingUp', tone: 'green', label: 'Total Credits', value: fmtMoney(a.accountSummary.totalCredits) },
    { icon: 'trendingDown', tone: 'red', label: 'Total Debits', value: fmtMoney(a.accountSummary.totalDebits) },
    { icon: 'download', tone: 'purple', label: 'Net Balance', value: fmtMoney(a.accountSummary.netBalance), danger: a.accountSummary.netBalance < 0 },
    { icon: 'rupee', tone: 'amber', label: 'Avg Monthly Salary', value: fmtMoney(a.insights.avgMonthlySalary) },
    { icon: 'calendar', tone: 'blue', label: 'Monthly EMI', value: fmtMoney(a.insights.totalEmiMonthly) },
    { icon: 'wallet', tone: 'green', label: 'Disbursed', value: fmtMoney(a.insights.totalDisbursed) },
    { icon: 'percent', tone: 'green', label: 'EMI / Income', value: `${a.insights.obligationToIncomeRatio}%` },
    { icon: 'percent', tone: a.insights.savingsRate < 0 ? 'red' : 'green', label: 'Savings Rate', value: `${a.insights.savingsRate}%`, danger: a.insights.savingsRate < 0 },
    ...(el ? [{ icon: 'shield', tone: 'blue', label: 'Eligibility', value: el.decision, sub: el.foir != null ? `FOIR ${(el.foir * 100).toFixed(0)}%` : undefined }] : []),
    ...(u ? [{ icon: 'check', tone: 'purple', label: 'Underwriting Grade', value: u.grade, sub: u.cibilScore != null ? `Score ${u.cibilScore}` : undefined }] : []),
  ] : [];

  const pickFile = (which: 'stmt' | 'cibil') => (files: FileList | null) => {
    const f = files?.[0];
    if (!f) return;
    if (which === 'stmt') setStatement(f);
    else setCibil(f);
  };

  return (
    <div className="">
      <div className="bsa-head">
        <div className="bsa-head-left">
          <div className="bsa-head-icon"><Icon name="fileText" size={26} /></div>
          <div>
            <h1>Financial Analysis</h1>
            <p className="muted">Upload a bank statement, a CIBIL report, or both — rule-based transaction classification: salary, EMIs, mutual funds, insurance, investments, equity/F&O activity and credit-card payments.</p>
          </div>
        </div>
        <button className="btn bsa-history-btn" onClick={() => setShowHistory((v) => !v)}>
          <Icon name="clock" size={16} /> Analysis History
        </button>
      </div>

      {error && <div className="alert error">{error}</div>}
      {notice && <div className="alert success">{notice}</div>}
      {result && <p className="muted">Classification uses the bank narration. Broker funding/payout does not prove a share buy/sell or trading profit; equity and F&amp;O are labelled only when supported. Verify uncertain entries with contract notes, broker ledgers, payslips or loan statements. Re-analyze saved reports to see the new transaction details.</p>}

      {showHistory && (
        <div className="panel bsa-history-panel">
          <div className="table-toolbar">
            <h3 className="section-title">Analysis History <span className="muted">(last 8, stored on this device)</span></h3>
            {history.length > 0 && (
              <button className="btn small" onClick={() => { setHistory([]); saveHistory([]); }}>Clear All</button>
            )}
          </div>
          {history.length === 0 ? (
            <p className="muted">No saved analyses yet — run one and it will appear here.</p>
          ) : (
            <div className="table-scroll">
              <table className="data-table">
                <thead>
                  <tr><th>File</th><th>Transactions</th><th>CIBIL</th><th>Analyzed</th><th></th></tr>
                </thead>
                <tbody>
                  {history.map((h) => (
                    <tr key={h.id}>
                      <td><Icon name="fileText" size={14} /> {h.filename}</td>
                      <td>{h.txnCount}</td>
                      <td>{h.hasCibil ? <span className="badge green">yes</span> : <span className="muted">—</span>}</td>
                      <td>{new Date(h.savedAt).toLocaleString()}</td>
                      <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                        <button className="btn small primary" onClick={() => openHistoryEntry(h)}>Open</button>{' '}
                        <button className="icon-btn" title="Delete" onClick={() => deleteHistoryEntry(h.id)}><Icon name="trash" size={14} /></button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      <div className="panel bsa-upload-panel">
        <div className="bsa-panel-head">
          <div className="bsa-panel-title"><span className="bsa-title-icon"><Icon name="upload" size={13} /></span> Upload Files</div>
          {result && (
            <button className="btn small" onClick={exportExcel}><Icon name="download" size={14} /> Export to Excel</button>
          )}
        </div>
        <div className="bsa-upload-grid">
          <div className="field">
            <label>Bank statement (optional — PDF / CSV / XLSX / TXT, ≤ 25 MB)</label>
            <div
              className={'bsa-drop' + (dragOver === 'stmt' ? ' over' : '')}
              onClick={() => stmtRef.current?.click()}
              onDragOver={(e) => { e.preventDefault(); setDragOver('stmt'); }}
              onDragLeave={() => setDragOver(null)}
              onDrop={(e) => { e.preventDefault(); setDragOver(null); pickFile('stmt')(e.dataTransfer.files); }}
            >
              <div className="bsa-drop-icon"><Icon name="fileText" size={16} /></div>
              <div className="bsa-drop-text">
                <span className="bsa-drop-hint"><b>Choose File</b> or drag &amp; drop</span>
                {statement && <span className="bsa-drop-file" title={statement.name}>{statement.name}</span>}
              </div>
              {statement && (
                <button className="icon-btn bsa-drop-x" title="Remove" onClick={(e) => { e.stopPropagation(); setStatement(null); if (stmtRef.current) stmtRef.current.value = ''; }}>
                  <Icon name="close" size={12} />
                </button>
              )}
              <input ref={stmtRef} type="file" accept=".pdf,.csv,.xlsx,.xls,.txt" hidden
                onChange={(e) => pickFile('stmt')(e.target.files)} />
            </div>
          </div>
          <div className="field">
            <label>CIBIL report (optional — PDF / TXT)</label>
            <div
              className={'bsa-drop' + (dragOver === 'cibil' ? ' over' : '')}
              onClick={() => cibilRef.current?.click()}
              onDragOver={(e) => { e.preventDefault(); setDragOver('cibil'); }}
              onDragLeave={() => setDragOver(null)}
              onDrop={(e) => { e.preventDefault(); setDragOver(null); pickFile('cibil')(e.dataTransfer.files); }}
            >
              <div className="bsa-drop-icon"><Icon name="fileText" size={16} /></div>
              <div className="bsa-drop-text">
                <span className="bsa-drop-hint"><b>Choose File</b> or drag &amp; drop</span>
                {cibil && <span className="bsa-drop-file" title={cibil.name}>{cibil.name}</span>}
              </div>
              {cibil && (
                <button className="icon-btn bsa-drop-x" title="Remove" onClick={(e) => { e.stopPropagation(); setCibil(null); if (cibilRef.current) cibilRef.current.value = ''; }}>
                  <Icon name="close" size={12} />
                </button>
              )}
              <input ref={cibilRef} type="file" accept=".pdf,.txt" hidden
                onChange={(e) => pickFile('cibil')(e.target.files)} />
            </div>
          </div>
          <div className="field">
            <label>PDF password (used for any password-protected upload)</label>
            <div className="bsa-pwd">
              <input type={showPwd ? 'text' : 'password'} placeholder="Password" value={cibilPassword}
                onChange={(e) => setCibilPassword(e.target.value)} />
              <button className="icon-btn" title={showPwd ? 'Hide password' : 'Show password'} onClick={() => setShowPwd((v) => !v)}>
                <Icon name="eye" size={15} />
              </button>
            </div>
          </div>
          <div className="bsa-upload-actions">
            <button className="btn primary bsa-analyze-btn" disabled={busy || (!statement && !cibil)} onClick={analyze}>
              <Icon name="barChart" size={16} /> {busy ? 'Analyzing…' : 'Analyze'}
            </button>
            <button className="btn bsa-clear-btn" onClick={clearAll} disabled={busy}>
              <Icon name="refresh" size={14} /> Clear
            </button>
          </div>
        </div>

        {result && (
          <div className="bsa-tabs-row">
            <div className="bsa-tabs">
              {MAIN_TABS.map((t) => (
                <button key={t} className={`bsa-tab${tab === t ? ' active' : ''}`} onClick={() => setTab(t)}>
                  {t}
                </button>
              ))}
              <div className="bsa-more">
                <button className={`bsa-tab${moreActive ? ' active' : ''}`} onClick={() => setOpenMore((v) => !v)}>
                  {(moreActive ? tab : 'More')} <Icon name="chevronDown" size={12} />
                </button>
                {openMore && (
                  <div className="bsa-more-menu">
                    {MORE_TABS.map((t) => (
                      <button key={t} className={tab === t ? 'active' : ''} onClick={() => { setTab(t); setOpenMore(false); }}>
                        {t}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </div>
        )}
      </div>

      {result && a && (
        <div className="panel">
          {tab === 'Summary' && (
            <div className="bsa-summary">
              <div className="bsa-cards">
                {cards.map((card, i) => (
                  <div key={i} className="bsa-card">
                    <div className={`bsa-card-icon tone-${card.tone}`}><Icon name={card.icon} size={16} /></div>
                    <div className="bsa-card-body">
                      <span className="bsa-card-label">{card.label}</span>
                      <strong className={card.danger ? 'bsa-danger' : ''}>{card.value}</strong>
                      {card.sub && <span className="muted bsa-card-sub">{card.sub}</span>}
                    </div>
                  </div>
                ))}
              </div>
              {(a.insights.riskFlags.length > 0 || a.insights.recommendations.length > 0) && (
                <div className="bsa-flags-row">
                  {a.insights.riskFlags.length > 0 && (
                    <div className="bsa-flag-panel red">
                      <div className="bsa-flag-head"><Icon name="alert" size={16} /> Risk Flags</div>
                      <ul className="bsa-list">{a.insights.riskFlags.map((r, i) => <li key={i}>{r}</li>)}</ul>
                    </div>
                  )}
                  {a.insights.recommendations.length > 0 && (
                    <div className="bsa-flag-panel green">
                      <div className="bsa-flag-head"><Icon name="bulb" size={16} /> Recommendations</div>
                      <ul className="bsa-list">{a.insights.recommendations.map((r, i) => <li key={i}>{r}</li>)}</ul>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {tab === 'Insights' && (
            <div className="bsa-summary">
              <div className="bsa-cards">
                <div className="bsa-card">
                  <div className="bsa-card-icon tone-purple"><Icon name="wallet" size={16} /></div>
                  <div className="bsa-card-body">
                    <span className="bsa-card-label">Estimated Total Debt</span>
                    <strong>{fmtMoney(a.insights.estimatedTotalDebt)}</strong>
                    <span className="muted bsa-card-sub">EMI × 36</span>
                  </div>
                </div>
                <div className="bsa-card">
                  <div className="bsa-card-icon tone-blue"><Icon name="layers" size={16} /></div>
                  <div className="bsa-card-body">
                    <span className="bsa-card-label">Active Loans</span>
                    <strong>{a.loanSummary.length}</strong>
                  </div>
                </div>
                <div className="bsa-card">
                  <div className="bsa-card-icon tone-red"><Icon name="phone" size={16} /></div>
                  <div className="bsa-card-body">
                    <span className="bsa-card-label">App Loan Repayments</span>
                    <strong>{a.appLoans.length}</strong>
                  </div>
                </div>
                <div className="bsa-card">
                  <div className="bsa-card-icon tone-amber"><Icon name="alert" size={16} /></div>
                  <div className="bsa-card-body">
                    <span className="bsa-card-label">Bounce Charges</span>
                    <strong>{a.bounceCharges.length}</strong>
                  </div>
                </div>
              </div>
              <h3>Loan Summary</h3>
              <DataTable columns={[
                { key: 'bank', label: 'Lender' }, { key: 'bankCategory', label: 'Category' },
                { key: 'loanTypeLabel', label: 'Loan Type' }, { key: 'emiAmount', label: 'EMI', money: true },
                { key: 'count', label: 'EMIs Paid', num: true }, { key: 'totalPaid', label: 'Total Paid', money: true },
              ]} rows={a.loanSummary as unknown as Record<string, unknown>[]} />
              <h3>Additional Fields Needed (from customer)</h3>
              <ul className="bsa-list">{a.insights.additionalFieldsNeeded.map((r, i) => <li key={i}>{r}</li>)}</ul>
            </div>
          )}

          {tab === 'CIBIL' && (
            <div className="bsa-summary">
              {!c ? <p className="muted">No CIBIL report was uploaded. Re-analyze with a CIBIL PDF/TXT to see this tab.</p> : (
                <>
                  <div className="bsa-cards">
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-blue"><Icon name="shield" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">CIBIL Score</span>
                        <strong>{c.score ?? 'Not detected'}</strong>
                        {c.reportDate && <span className="muted bsa-card-sub">as on {c.reportDate}</span>}
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-red"><Icon name="alert" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">DPD Max</span>
                        <strong>{c.dpdMax ?? 0}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-purple"><Icon name="database" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Accounts</span>
                        <strong>{c.accounts.length}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-amber"><Icon name="search" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Enquiries</span>
                        <strong>{c.enquiries.total ?? c.enquiryDetails.length}</strong>
                      </div>
                    </div>
                  </div>
                  <div className="bsa-personal">
                    <div><span className="muted">Name:</span> {c.personal.name ?? '—'}</div>
                    <div><span className="muted">PAN:</span> {c.personal.pan ?? '—'}</div>
                    <div><span className="muted">DOB:</span> {c.personal.dob ?? '—'}</div>
                    <div><span className="muted">Mobile:</span> {c.personal.mobile ?? '—'}</div>
                    <div><span className="muted">Email:</span> {c.personal.email ?? '—'}</div>
                    <div><span className="muted">Company:</span> {c.personal.company ?? '—'}</div>
                  </div>
                  <h3>Enquiry Window Counts</h3>
                  <div className="bsa-personal">
                    {['last_30d', 'last_60d', 'last_90d', 'last_180d', 'last_365d', 'total'].map((k) => (
                      <div key={k}><span className="muted">{k.replace('last_', 'Last ').replace('d', ' days')}:</span> {c.enquiries[k] ?? '—'}</div>
                    ))}
                  </div>
                  <h3>CIBIL Accounts</h3>
                  <DataTable columns={[
                    { key: 'lender', label: 'Lender' }, { key: 'accountType', label: 'Type' },
                    { key: 'accountNo', label: 'Account No' }, { key: 'accountStatus', label: 'Status' },
                    { key: 'ownership', label: 'Ownership' }, { key: 'openedDate', label: 'Opened' },
                    { key: 'currentBalance', label: 'Balance', money: true },
                    { key: 'sanctionedAmount', label: 'Sanctioned', money: true },
                    { key: 'overdueAmount', label: 'Overdue', money: true },
                    { key: 'dpdMax', label: 'DPD', num: true },
                  ]} rows={c.accounts as unknown as Record<string, unknown>[]} />
                  <h3>Enquiry Details</h3>
                  <DataTable columns={[
                    { key: 'date', label: 'Date' }, { key: 'member', label: 'Member' },
                    { key: 'purpose', label: 'Purpose' }, { key: 'amount', label: 'Amount', money: true },
                  ]} rows={c.enquiryDetails as unknown as Record<string, unknown>[]} />
                  {c.adverseFlags.length > 0 && (<><h3>Adverse Flags</h3><p>{c.adverseFlags.join(', ')}</p></>)}
                  {cv && (
                    <>
                      <h3>Cross-Verification (CIBIL vs Statement)</h3>
                      {cv.matched.length > 0 && <p><span className="muted">Matched lenders:</span> {cv.matched.map((m) => `${m.cibilLender} ↔ ${m.statementLender}`).join(', ')}</p>}
                      {cv.missingInStatement.length > 0 && <p><span className="muted">In CIBIL but not in statement:</span> {cv.missingInStatement.join(', ')}</p>}
                      {cv.missingInCibil.length > 0 && <p><span className="muted">In statement but not in CIBIL:</span> {cv.missingInCibil.join(', ')}</p>}
                    </>
                  )}
                </>
              )}
            </div>
          )}

          {tab === 'Underwriting' && (
            <div className="bsa-summary">
              {!u ? <p className="muted">Upload both a statement and a CIBIL report to see underwriting results.</p> : (
                <>
                  <div className="bsa-cards">
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-purple"><Icon name="check" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Grade</span>
                        <strong>{u.grade}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-blue"><Icon name="shield" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">CIBIL Score</span>
                        <strong>{u.cibilScore ?? '—'}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-green"><Icon name="percent" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">FOIR</span>
                        <strong>{u.foir != null ? `${(u.foir * 100).toFixed(0)}%` : '—'}</strong>
                        {u.maxFoirPolicy != null && <span className="muted bsa-card-sub">policy max {(u.maxFoirPolicy * 100).toFixed(0)}%</span>}
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-green"><Icon name="wallet" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Additional EMI Capacity</span>
                        <strong>{u.additionalEmiCapacity != null ? fmtMoney(u.additionalEmiCapacity) : '—'}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-amber"><Icon name="percent" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">CC Utilization</span>
                        <strong>{u.creditCardUtilization != null ? `${(u.creditCardUtilization * 100).toFixed(0)}%` : '—'}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-blue"><Icon name="database" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Accounts (active/total)</span>
                        <strong>{u.accountsActive}/{u.accountsTotal}</strong>
                      </div>
                    </div>
                    <div className="bsa-card">
                      <div className="bsa-card-icon tone-red"><Icon name="search" size={16} /></div>
                      <div className="bsa-card-body">
                        <span className="bsa-card-label">Recent Enquiries</span>
                        <strong>{u.enquiriesRecent}</strong>
                      </div>
                    </div>
                  </div>
                  {u.policyFlags.length > 0 && (
                    <div className="bsa-flags-row">
                      <div className="bsa-flag-panel red">
                        <div className="bsa-flag-head"><Icon name="alert" size={16} /> Policy Flags</div>
                        <ul className="bsa-list">{u.policyFlags.map((f, i) => <li key={i}>{f}</li>)}</ul>
                      </div>
                    </div>
                  )}
                  <h3>Product Eligibility</h3>
                  <DataTable columns={[
                    { key: 'label', label: 'Product' }, { key: 'minScore', label: 'Min Score', num: true },
                    { key: 'eligible', label: 'Eligible' },
                  ]} rows={u.productEligibility.map((p) => ({ label: p.label, minScore: p.minScore, eligible: p.eligible ? 'Yes ✓' : 'No ✗' }))} />
                </>
              )}
            </div>
          )}

          {/* Transaction-derived tabs have nothing to show when the user supplied
              only a CIBIL report, so say that plainly instead of rendering a
              grid of zeroes. */}
          {result.statementProvided === false ? (
            <p className="muted">
              This tab is built from bank statement transactions, and no bank statement was
              uploaded. Re-run the analysis with a statement to populate it — the CIBIL,
              Eligibility and Underwriting tabs are already available.
            </p>
          ) : (
            tables.filter((t) => t.id === tab).map((t) => (
              <DataTable key={t.id} columns={t.columns} rows={t.rows} />
            ))
          )}
        </div>
      )}
    </div>
  );
}
