import { useMemo, useState } from 'react';
import { Bar, BarChart, CartesianGrid, Cell, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import Icon from './Icon';

export interface ClassifiedBankTransaction {
  date: string;
  description: string;
  debit: number;
  credit: number;
  balance: number;
  category: string;
  counterparty: string;
  instrument: string;
  activity: string;
  needsReview: boolean;
  classificationReason: string;
  referenceId?: string | null;
  utr?: string | null;
  rrn?: string | null;
  sourceType?: string | null;
  sourceSheet?: string | null;
  sourceRow?: number | null;
}

const money = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 });
const rupees = (value: number) => `₹${money.format(value)}`;
const shortMoney = (value: number) => Math.abs(value) >= 10000000 ? `₹${(value / 10000000).toFixed(1)}Cr` :
  Math.abs(value) >= 100000 ? `₹${(value / 100000).toFixed(1)}L` : Math.abs(value) >= 1000 ? `₹${(value / 1000).toFixed(0)}K` : `₹${value}`;
const monthLabel = (value: string) => /^\d{4}-\d{2}$/.test(value)
  ? new Date(`${value}-01T00:00:00`).toLocaleDateString('en-IN', { month: 'short', year: '2-digit' }) : value;

export function bankCategoryStyle(category: string): { label: string; tone: string; color: string; icon: string } {
  switch (category) {
    case 'Salary': return { label: 'Salary', tone: 'green', color: '#16a36b', icon: 'briefcase' };
    case 'EMI / loan repayment': return { label: 'EMI / Loan', tone: 'red', color: '#fb7185', icon: 'wallet' };
    case 'Mutual fund': return { label: 'Mutual Fund / SIP', tone: 'purple', color: '#9b7be4', icon: 'trendingUp' };
    case 'Investment': return { label: 'Investment', tone: 'purple', color: '#6366f1', icon: 'layers' };
    case 'Credit card payment': return { label: 'Credit Card', tone: 'amber', color: '#f5bd38', icon: 'wallet' };
    case 'Insurance': return { label: 'Insurance', tone: 'blue', color: '#38a2e8', icon: 'shield' };
    case 'Stock market': return { label: 'Stock Market', tone: 'purple', color: '#7c5ce7', icon: 'barChart' };
    case 'Rent': return { label: 'Rent', tone: 'blue', color: '#5897ec', icon: 'briefcase' };
    case 'Utility / expense': return { label: 'Utility / Expense', tone: 'blue', color: '#45b6bf', icon: 'rupee' };
    case 'Charges / returns': return { label: 'Charges / Returns', tone: 'red', color: '#ed9370', icon: 'alert' };
    case 'App loan': return { label: 'App Loan', tone: 'red', color: '#e75b71', icon: 'phone' };
    case 'Loan disbursement': return { label: 'Disbursement', tone: 'purple', color: '#8a68da', icon: 'download' };
    case 'Pension': case 'PF / EPFO': return { label: category, tone: 'green', color: '#56b78b', icon: 'shield' };
    default: return { label: category === 'Other' ? 'Others' : category, tone: 'neutral', color: '#94a3b8', icon: 'layers' };
  }
}

type SortKey = 'date' | 'debit' | 'credit' | 'balance';

export default function BsaTransactionExplorer({ transactions, monthlySummary, totalDebits }: {
  transactions: ClassifiedBankTransaction[];
  monthlySummary: { month: string; credits: number; debits: number }[];
  totalDebits: number;
}) {
  const [search, setSearch] = useState('');
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [category, setCategory] = useState('all');
  const [direction, setDirection] = useState('all');
  const [reviewOnly, setReviewOnly] = useState(false);
  const [sort, setSort] = useState<{ key: SortKey; descending: boolean }>({ key: 'date', descending: false });
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(10);
  const [expanded, setExpanded] = useState<number | null>(null);
  const categories = useMemo(() => [...new Set(transactions.map(t => t.category))].sort(), [transactions]);
  const reviewCount = transactions.filter(t => t.needsReview).length;
  const activeFilters = Number(category !== 'all') + Number(direction !== 'all') + Number(reviewOnly);

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    return transactions.map((transaction, index) => ({ transaction, index })).filter(({ transaction: t }) =>
      (category === 'all' || t.category === category) &&
      (direction === 'all' || (direction === 'credit' ? t.credit > 0 : t.debit > 0)) &&
      (!reviewOnly || t.needsReview) &&
      (!query || [t.description, t.counterparty, t.category, t.instrument, t.activity, t.date,
        String(t.debit), String(t.credit), money.format(t.debit), money.format(t.credit),
        t.referenceId, t.utr, t.rrn].join(' ').toLowerCase().includes(query))
    ).sort((a, b) => {
      const av = a.transaction[sort.key], bv = b.transaction[sort.key];
      const compared = typeof av === 'number' && typeof bv === 'number' ? av - bv : String(av).localeCompare(String(bv));
      return (sort.descending ? -compared : compared) || a.index - b.index;
    });
  }, [transactions, search, category, direction, reviewOnly, sort]);
  const lastPage = Math.max(0, Math.ceil(filtered.length / pageSize) - 1);
  const currentPage = Math.min(page, lastPage);
  const visible = filtered.slice(currentPage * pageSize, (currentPage + 1) * pageSize);

  const spending = useMemo(() => {
    const sums = new Map<string, number>();
    for (const t of transactions) if (t.debit > 0) sums.set(t.category, (sums.get(t.category) ?? 0) + t.debit);
    return [...sums].map(([category, value]) => ({ category, value, ...bankCategoryStyle(category) })).sort((a, b) => b.value - a.value);
  }, [transactions]);
  const chartTotal = spending.reduce((sum, item) => sum + item.value, 0);
  const sortBy = (key: SortKey) => {
    setSort(previous => ({ key, descending: previous.key === key ? !previous.descending : key !== 'date' }));
    setPage(0);
  };
  const resetFilters = () => { setCategory('all'); setDirection('all'); setReviewOnly(false); setSearch(''); setPage(0); };

  return (
    <div className="bsa-explorer-grid">
      <section className="bsa-register" aria-label="Bank transactions">
        <div className="bsa-register-head">
          <div>
            <h2>Transactions <span>({transactions.length})</span></h2>
            <p>{reviewCount ? `${reviewCount} ${reviewCount === 1 ? 'entry needs' : 'entries need'} review · ` : ''}Based on statement narration</p>
          </div>
          <div className="bsa-register-tools">
            <label className="bsa-search"><Icon name="search" size={15} />
              <input aria-label="Search transactions" placeholder="Search narration, counterparty, amount…" value={search}
                onChange={e => { setSearch(e.target.value); setPage(0); }} />
              {search && <button className="icon-btn" aria-label="Clear transaction search" onClick={() => { setSearch(''); setPage(0); }}><Icon name="close" size={12} /></button>}
            </label>
            <button className={`btn small bsa-filter-toggle${activeFilters ? ' is-filtered' : ''}`} aria-expanded={filtersOpen} aria-controls="bsa-transaction-filters" onClick={() => setFiltersOpen(v => !v)}>
              <Icon name="settings" size={14} /> Filters{activeFilters > 0 && <span>{activeFilters}</span>}
            </button>
          </div>
        </div>
        {filtersOpen && <div className="bsa-filter-row" id="bsa-transaction-filters">
          <label>Category<select value={category} onChange={e => { setCategory(e.target.value); setPage(0); }}>
            <option value="all">All categories</option>{categories.map(c => <option key={c} value={c}>{bankCategoryStyle(c).label}</option>)}
          </select></label>
          <label>Direction<select value={direction} onChange={e => { setDirection(e.target.value); setPage(0); }}>
            <option value="all">Credits &amp; debits</option><option value="credit">Credits only</option><option value="debit">Debits only</option>
          </select></label>
          <label className="bsa-review-filter"><input type="checkbox" checked={reviewOnly} onChange={e => { setReviewOnly(e.target.checked); setPage(0); }} />Needs review only</label>
          <button className="btn small" onClick={resetFilters}>Reset filters</button>
        </div>}
        <div className="bsa-register-scroll">
          <table className="bsa-register-table">
            <thead><tr>
              <th scope="col">#</th>
              <th scope="col" aria-sort={sort.key === 'date' ? (sort.descending ? 'descending' : 'ascending') : 'none'}><button onClick={() => sortBy('date')}>Date <Icon name={sort.key === 'date' && sort.descending ? 'chevronDown' : 'chevronUp'} size={11} /></button></th>
              <th scope="col">Narration</th>
              {(['debit', 'credit', 'balance'] as const).map(key => <th scope="col" className="numeric" key={key} aria-sort={sort.key === key ? (sort.descending ? 'descending' : 'ascending') : 'none'}>
                <button onClick={() => sortBy(key)}>{key === 'debit' ? 'Debit' : key === 'credit' ? 'Credit' : 'Balance'} (₹)<Icon name={sort.key === key && sort.descending ? 'chevronDown' : 'chevronUp'} size={11} /></button>
              </th>)}
              <th scope="col">Nature</th><th scope="col">Counterparty</th><th scope="col">Category</th><th scope="col"><span className="bsa-sr-only">Details</span></th>
            </tr></thead>
            <tbody>{visible.length ? visible.map(({ transaction: t, index }) => {
              const badge = bankCategoryStyle(t.category);
              const isExpanded = expanded === index;
              return <TransactionRows key={index} transaction={t} index={index} badge={badge} expanded={isExpanded} toggle={() => setExpanded(isExpanded ? null : index)} />;
            }) : <tr><td colSpan={10}><div className="bsa-table-empty"><Icon name="search" size={25} /><strong>No matching transactions</strong><p>Try another search or reset your filters.</p><button className="btn small" onClick={resetFilters}>Reset filters</button></div></td></tr>}</tbody>
          </table>
        </div>
        <div className="bsa-table-footer">
          <span>{filtered.length ? `${currentPage * pageSize + 1}–${Math.min((currentPage + 1) * pageSize, filtered.length)}` : '0'} of {filtered.length} transactions{filtered.length !== transactions.length ? ` (filtered from ${transactions.length})` : ''}</span>
          <div><label>Rows <select aria-label="Rows per page" value={pageSize} onChange={e => { setPageSize(Number(e.target.value)); setPage(0); }}><option value={10}>10</option><option value={25}>25</option><option value={50}>50</option></select></label>
            <button className="icon-btn" aria-label="Previous transaction page" disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}><Icon name="chevronLeft" size={16} /></button>
            <span>{currentPage + 1} / {lastPage + 1}</span>
            <button className="icon-btn" aria-label="Next transaction page" disabled={currentPage >= lastPage} onClick={() => setPage(currentPage + 1)}><Icon name="chevronRight" size={16} /></button>
          </div>
        </div>
      </section>
      <aside className="bsa-chart-stack" aria-label="Statement charts">
        <section className="bsa-chart-panel">
          <h2>Spending by Category <span>(Debits)</span></h2>
          {chartTotal > 0 ? <>
            <div className="bsa-spending-chart">
              <div className="bsa-donut" role="img" aria-label={`Spending by category, total debits ${rupees(chartTotal)}`}>
                <ResponsiveContainer width="100%" height="100%"><PieChart><Pie data={spending} dataKey="value" nameKey="label" cx="50%" cy="50%" innerRadius="64%" outerRadius="92%" paddingAngle={2} stroke="none" isAnimationActive={false}>
                  {spending.map(item => <Cell key={item.category} fill={item.color} />)}
                </Pie><Tooltip formatter={value => rupees(Number(value))} /></PieChart></ResponsiveContainer>
                <div className="bsa-donut-center"><strong>{rupees(chartTotal)}</strong><span>Total Debits</span></div>
              </div>
              <ul className="bsa-chart-legend">{spending.map(item => <li key={item.category}>
                <span className="bsa-legend-dot" style={{ background: item.color }} /><span>{item.label}</span><strong>{(item.value / chartTotal * 100).toFixed(1)}%</strong>
              </li>)}</ul>
            </div>
            {Math.abs(chartTotal - totalDebits) > 0.01 && <p className="bsa-chart-note">Chart covers classified rows only; statement total is {rupees(totalDebits)}.</p>}
          </> : <div className="bsa-chart-empty">No debit transactions in this statement.</div>}
        </section>
        <section className="bsa-chart-panel">
          <h2>Monthly Cash Flow</h2>
          <div className="bsa-flow-legend"><span><i className="credit" />Credits</span><span><i className="debit" />Debits</span></div>
          {monthlySummary.length > 0 ? <div className="bsa-flow-chart" role="img" aria-label="Monthly credits and debits">
            <ResponsiveContainer width="100%" height="100%"><BarChart data={monthlySummary} margin={{ top: 8, right: 6, left: 0, bottom: 4 }} barGap={5}>
              <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#eaf0f6" />
              <XAxis dataKey="month" tickFormatter={monthLabel} tick={{ fontSize: 10, fill: '#71809b' }} axisLine={false} tickLine={false} />
              <YAxis tickFormatter={shortMoney} tick={{ fontSize: 10, fill: '#71809b' }} width={58} axisLine={false} tickLine={false} />
              <Tooltip formatter={value => rupees(Number(value))} labelFormatter={value => monthLabel(String(value))} cursor={{ fill: '#f5f8fc' }} />
              <Bar dataKey="credits" name="Credits" fill="#5bc791" radius={[3, 3, 0, 0]} maxBarSize={44} isAnimationActive={false} />
              <Bar dataKey="debits" name="Debits" fill="#fb7883" radius={[3, 3, 0, 0]} maxBarSize={44} isAnimationActive={false} />
            </BarChart></ResponsiveContainer>
          </div> : <div className="bsa-chart-empty">No monthly cash-flow data available.</div>}
          <details className="bsa-flow-data"><summary>View monthly amounts</summary><table><thead><tr><th>Month</th><th>Credits</th><th>Debits</th></tr></thead><tbody>{monthlySummary.map(m => <tr key={m.month}><td>{monthLabel(m.month)}</td><td>{rupees(m.credits)}</td><td>{rupees(m.debits)}</td></tr>)}</tbody></table></details>
        </section>
        <p className="bsa-chart-footnote"><Icon name="help" size={13} />Charts show the full statement, unaffected by table filters.</p>
      </aside>
    </div>
  );
}

function TransactionRows({ transaction: t, index, badge, expanded, toggle }: {
  transaction: ClassifiedBankTransaction; index: number; badge: ReturnType<typeof bankCategoryStyle>; expanded: boolean; toggle: () => void;
}) {
  return <>
    <tr className={expanded ? 'is-expanded' : ''}>
      <td className="bsa-row-number">{index + 1}</td><td className="bsa-date-cell">{t.date}</td>
      <td className="bsa-narration" title={t.description}><span>{t.description}</span></td>
      <td className="numeric">{money.format(t.debit)}</td><td className="numeric">{money.format(t.credit)}</td><td className="numeric">{money.format(t.balance)}</td>
      <td><span className={`bsa-direction ${t.credit > 0 && !t.debit ? 'credit' : t.debit > 0 && !t.credit ? 'debit' : ''}`}>{t.credit > 0 && !t.debit ? 'Credit' : t.debit > 0 && !t.credit ? 'Debit' : 'Review'}</span></td>
      <td className="bsa-counterparty" title={t.counterparty}>{t.counterparty || '—'}</td>
      <td><span className={`bsa-category-badge ${badge.tone}`}><Icon name={badge.icon} size={13} />{badge.label}</span>{t.needsReview && <span className="bsa-review-mark" title="Needs review" aria-label="Needs review">•</span>}</td>
      <td><button className="icon-btn bsa-detail-toggle" aria-label={`${expanded ? 'Hide' : 'Show'} details for transaction ${index + 1}`} aria-expanded={expanded} aria-controls={`bsa-transaction-${index}`} onClick={toggle}>{expanded ? <Icon name="chevronUp" size={15} /> : <span aria-hidden="true">•••</span>}</button></td>
    </tr>
    {expanded && <tr className="bsa-detail-row"><td colSpan={10}><div id={`bsa-transaction-${index}`}>
      <div className="bsa-detail-heading"><strong>Transaction {index + 1} · Classification details</strong><span className={`bsa-category-badge ${t.needsReview ? 'amber' : 'green'}`}>{t.needsReview ? 'Needs review' : 'Narration-based match'}</span></div>
      <p>{t.description}</p><dl>
        <div><dt>Category</dt><dd>{t.category}</dd></div><div><dt>Instrument / Segment</dt><dd>{t.instrument || 'Not specified'}</dd></div>
        <div><dt>Activity</dt><dd>{t.activity || 'Not specified'}</dd></div><div><dt>Reference</dt><dd>{t.utr || t.rrn || t.referenceId || 'Not available'}</dd></div>
        <div><dt>Source</dt><dd>{[t.sourceType?.toUpperCase(), t.sourceSheet, t.sourceRow != null ? `row ${t.sourceRow}` : null].filter(Boolean).join(' · ') || 'Statement'}</dd></div>
      </dl><div className="bsa-detail-basis"><Icon name="help" size={15} /><span>{t.classificationReason || 'No reliable category signal in the narration.'}</span></div>
    </div></td></tr>}
  </>;
}
