import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import { formatInr } from '../lib/emi';
import './LoansPage.css';

interface Row { bank: string; category: string; amt: string; slab: string }
interface CompanyOpt { id: number; name: string }
interface Obligation { key: number; type: string; amount: string }

/**
 * Existing-loan types offered on the obligation builder, with the weight the legacy view
 * applied when totalling. Values are the legacy labels — the stored procedure is handed
 * only the resulting total, so these strings never leave the browser.
 */
const PRODUCTS = [
  'Personal Loan',
  'Home Loan',
  'Business Loan',
  'Loan Against Property',
  'Car Loan',
  'OverDraft',
  'Credit Card (5%)',
  'Gold Loan (1.5%)',
  'Education Loan (1.5%)',
  'Auto Loan',
  'Other Loan',
];

/** Weight applied to the entered amount, exactly as in the legacy addrow handler. */
function obligationAmount(type: string, amount: number): number {
  if (type === 'Credit Card (5%)') return (amount * 5) / 100;
  if (type === 'Gold Loan (1.5%)' || type === 'Education Loan (1.5%)') return (amount * 1.5) / 100;
  return amount;
}

/**
 * Loan eligibility. Replicates Views/LoanEligiility/Index.cshtml: the same inputs, the
 * same obligations table with the same percentage weights, the same System/User
 * Recommended result table, and the same category re-check. The eligibility arithmetic
 * itself lives in USP_CIP_LOAN_ELIGIBILITY and is called unchanged from the backend.
 */
export default function LoanEligibilityPage() {
  const [foir, setFoir] = useState('');
  const [gross, setGross] = useState('');
  const [net, setNet] = useState('');
  const [company, setCompany] = useState<CompanyOpt | null>(null);
  const [companyText, setCompanyText] = useState('');
  const [roi, setRoi] = useState('0');
  const [tenure, setTenure] = useState('60');

  const [obligations, setObligations] = useState<Obligation[]>([]);
  const [newType, setNewType] = useState('Personal Loan');
  const [newAmount, setNewAmount] = useState('');

  const [rows, setRows] = useState<Row[]>([]);
  const [categories, setCategories] = useState<string[]>([]);
  const [picked, setPicked] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const nextKey = useRef(1);

  const obligationTotal = obligations.reduce(
    (sum, o) => sum + obligationAmount(o.type, parseFloat(o.amount) || 0),
    0,
  );

  useEffect(() => {
    api.get<string[]>('/api/loan/categories')
      .then(setCategories)
      .catch(() => setCategories([]));
  }, []);

  const [suggestions, setSuggestions] = useState<CompanyOpt[]>([]);

  /** Typeahead over companies that actually have an eligibility row; best-effort. */
  const suggestCompanies = useCallback(async (q: string) => {
    if (q.trim().length < 3) { setSuggestions([]); return; }
    try {
      const hits = await api.get<CompanyOpt[]>('/api/loan/companies?prefix=' + encodeURIComponent(q.trim()));
      setSuggestions(hits);
    } catch {
      setSuggestions([]);
    }
  }, []);

  const addObligation = () => {
    const amt = parseFloat(newAmount);
    if (!isFinite(amt) || amt <= 0) {
      toast.error('Enter an amount greater than zero.');
      return;
    }
    setObligations((o) => [...o, { key: nextKey.current++, type: newType, amount: newAmount }]);
    setNewAmount('');
  };

  const removeObligation = (key: number) => setObligations((o) => o.filter((x) => x.key !== key));

  const check = useCallback(
    async (category = '0') => {
      setError('');
      if (!parseFloat(net)) { setError('Please enter salary.'); return; }
      if (parseFloat(gross) > 0 && parseFloat(net) >= parseFloat(gross)) {
        setError('Net salary cannot be greater than or equal to gross salary.');
        return;
      }
      if (!parseFloat(tenure)) { setError('Please enter tenure.'); return; }

      setLoading(true);
      try {
        const res = await api.post<{ rows: Row[] }>('/api/loan/eligibility', {
          name: foir,
          gross: parseFloat(gross) || 0,
          net: parseFloat(net) || 0,
          // A blank company means "not listed", which is what the legacy form sent.
          companyKey: company ? company.id : -1,
          roi: parseFloat(roi) || 0,
          tenure: Math.round(parseFloat(tenure)) || 0,
          category,
          obligation: Math.round(obligationTotal * 100) / 100,
        });
        setRows(res.rows);
        if (category === '0') setPicked(new Array(res.rows.length).fill(''));
      } catch (e) {
        setRows([]);
        setError(e instanceof ApiError ? e.message : 'Eligibility check failed.');
      } finally {
        setLoading(false);
      }
    },
    [foir, gross, net, company, roi, tenure, obligationTotal],
  );

  /** Re-run for one bank under a user-chosen category and overwrite just that amount. */
  const recheck = async (index: number, category: string) => {
    if (!category) return;
    const next = [...picked];
    next[index] = category;
    setPicked(next);
    setLoading(true);
    try {
      const res = await api.post<{ rows: Row[] }>('/api/loan/eligibility', {
        name: foir,
        gross: parseFloat(gross) || 0,
        net: parseFloat(net) || 0,
        companyKey: company ? company.id : -1,
        roi: parseFloat(roi) || 0,
        tenure: Math.round(parseFloat(tenure)) || 0,
        category,
        obligation: Math.round(obligationTotal * 100) / 100,
      });
      setRows((prev) => prev.map((r, i) => (i === index ? { ...r, amt: res.rows[0]?.amt ?? r.amt } : r)));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not re-check that category.');
    } finally {
      setLoading(false);
    }
  };

  const exportCsv = () => {
    // The legacy export wrote Net Salary / Company / Tenure / ROI followed by one line
    // per obligation, as an Excel-readable HTML table.
    const esc = (s: string) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    let html = '<table>';
    html += `<tr><td>Net Salary</td><td>${esc(net)}</td></tr>`;
    html += `<tr><td>Company</td><td>${esc(company?.name ?? '')}</td></tr>`;
    html += `<tr><td>Tenure</td><td>${esc(tenure)}</td></tr>`;
    html += `<tr><td>ROI</td><td>${esc(roi)}</td></tr>`;
    obligations.forEach((o) => {
      const calc = obligationAmount(o.type, parseFloat(o.amount) || 0);
      html += `<tr><td>${esc(o.type)}</td><td>-${formatInr(calc, 0)}</td></tr>`;
    });
    html += '</table>';
    const url = 'data:application/vnd.ms-excel,' + encodeURIComponent(html);
    window.location.href = url;
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Loan Eligibility</h1>
          <p className="page-sub">
            Check eligibility across every configured bank, then re-check any bank under a different
            lending category.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Applicant &amp; loan</h3></div>
        <div className="card-body">
          <div className="form-grid">
            <div className="field">
              <label className="field-label" htmlFor="lg-gross">Gross Salary</label>
              <input id="lg-gross" className="inp" inputMode="decimal" value={gross}
                onChange={(e) => setGross(e.target.value)} placeholder="Enter gross salary" />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="lg-net">Net Salary</label>
              <input id="lg-net" className="inp" inputMode="decimal" value={net}
                onChange={(e) => setNet(e.target.value)} placeholder="Enter net salary" />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="lg-foir">FOIR</label>
              <input id="lg-foir" className="inp" inputMode="numeric" value={foir}
                onChange={(e) => setFoir(e.target.value)} placeholder="e.g. 50" />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="lg-company">Company</label>
              <input id="lg-company" className="inp" list="lg-companies" value={companyText}
                onChange={(e) => {
                  const v = e.target.value;
                  setCompanyText(v);
                  const hit = suggestions.find((s) => s.name === v);
                  setCompany(hit ?? null);
                  if (v.trim().length >= 3) suggestCompanies(v);
                  else setSuggestions([]);
                }}
                placeholder="Type at least 3 letters" />
              <datalist id="lg-companies">
                {suggestions.map((s) => <option key={s.id} value={s.name} />)}
              </datalist>
              <span className="field-hint">
                {company ? `Matched: ${company.name}` : 'Leave blank to check as an unlisted company.'}
              </span>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="lg-roi">ROI</label>
              <input id="lg-roi" className="inp" inputMode="decimal" value={roi}
                onChange={(e) => setRoi(e.target.value)} />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="lg-tenure">Tenure (in Months)</label>
              <input id="lg-tenure" className="inp" inputMode="numeric" value={tenure}
                onChange={(e) => setTenure(e.target.value)} />
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>Existing obligations</h3>
          <span className="card-head-note">Total {formatInr(obligationTotal)}</span>
        </div>
        <div className="card-body">
          {obligations.length > 0 && (
            <div className="table-wrap obs-wrap">
              <table className="data-table">
                <thead>
                  <tr><th>Type</th><th className="num">Entered</th><th className="num">Counted</th><th /></tr>
                </thead>
                <tbody>
                  {obligations.map((o) => {
                    const calc = obligationAmount(o.type, parseFloat(o.amount) || 0);
                    return (
                      <tr key={o.key}>
                        <td>{o.type}</td>
                        <td className="num">{formatInr(parseFloat(o.amount) || 0)}</td>
                        <td className="num">{formatInr(calc)}</td>
                        <td className="num">
                          <button className="icon-btn danger" onClick={() => removeObligation(o.key)}
                            aria-label={`Remove ${o.type}`} title="Remove">
                            <Icon name="trash" />
                          </button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
                <tfoot>
                  <tr className="total-row">
                    <td>Total</td><td className="num" /><td className="num">{formatInr(obligationTotal)}</td><td />
                  </tr>
                </tfoot>
              </table>
            </div>
          )}

          <div className="obs-add">
            <div className="field">
              <label className="field-label" htmlFor="obs-type">Type</label>
              <select id="obs-type" className="inp" value={newType} onChange={(e) => setNewType(e.target.value)}>
                {PRODUCTS.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="obs-amt">Amount</label>
              <input id="obs-amt" className="inp" inputMode="decimal" value={newAmount}
                onChange={(e) => setNewAmount(e.target.value)} placeholder="Monthly obligation"
                onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addObligation(); } }} />
            </div>
            <button className="btn secondary obs-add-btn" onClick={addObligation}>
              <Icon name="plus" /> Add
            </button>
          </div>
          <p className="muted-note">
            Credit card is counted at 5% and gold/education loans at 1.5% of the amount entered; all
            other types count in full. This is the legacy weighting and is applied to the total below.
          </p>
        </div>
      </div>

      {error && <div className="alert error" role="alert">{error}</div>}

      <div className="actions-row">
        <button className="btn primary" onClick={() => check('0')} disabled={loading}>
          <Icon name="search" /> {loading ? 'Checking…' : 'Check Eligibility'}
        </button>
        {rows.length > 0 && (
          <button className="btn secondary" onClick={exportCsv}><Icon name="download" /> Export</button>
        )}
      </div>

      {rows.length > 0 && (
        <div className="card">
          <div className="card-head"><h3>Result</h3></div>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr className="grp-head">
                  <th colSpan={3}>System Recommended</th>
                  <th colSpan={2}>User Recommended</th>
                </tr>
                <tr>
                  <th>Bank</th>
                  <th>Category</th>
                  <th className="num">Amount</th>
                  <th>Category</th>
                  <th className="num">Amount</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={i}>
                    <td><strong>{r.bank}</strong></td>
                    <td>{r.category}</td>
                    <td className="num">{r.amt}</td>
                    <td>
                      <select className="inp sm" value={picked[i] ?? ''} disabled={loading}
                        onChange={(e) => recheck(i, e.target.value)}>
                        <option value="">— select —</option>
                        {categories.map((c) => <option key={c} value={c}>{c}</option>)}
                      </select>
                    </td>
                    <td className="num">
                      {picked[i] ? <strong className="amt-user">{r.amt}</strong> : r.amt}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}