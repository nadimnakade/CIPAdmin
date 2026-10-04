import { useMemo, useState } from 'react';
import Icon from '../components/Icon';
import { calculateEmi, formatInr, type EmiResult } from '../lib/emi';
import './LoansPage.css';

/**
 * EMI calculator. Replicates Views/EMI/Index.cshtml: same three inputs with the same
 * defaults (1,00,000 / 24 months / 14.5%), the same four summary figures, the same
 * principal-vs-interest pie and the same month-by-month schedule.
 *
 * The legacy sliders were commented out in the view, so only the text inputs are live —
 * they are kept rather than reintroducing sliders that were never in use.
 */
export default function EmiCalculatorPage() {
  const [loanAmount, setLoanAmount] = useState('100000');
  const [months, setMonths] = useState('24');
  const [roi, setRoi] = useState('14.5');

  const n = (v: string) => {
    const f = parseFloat(v);
    return isFinite(f) && f > 0 ? f : 0;
  };

  const result: EmiResult | null = useMemo(() => {
    const p = n(loanAmount);
    const t = Math.round(n(months));
    const r = n(roi);
    if (!p || !t) return null;
    return calculateEmi(p, t, r);
  }, [loanAmount, months, roi]);

  const loanPart = result ? Math.max(result.emi * Math.round(n(months)) - result.totalInterest, 0) : 0;
  const interestPart = result ? Math.max(result.totalInterest, 0) : 0;
  const donutTotal = loanPart + interestPart;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>EMI Calculator</h1>
          <p className="page-sub">Monthly instalment, total interest and the full repayment schedule.</p>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>Loan details</h3>
        </div>
        <div className="card-body">
          <div className="emi-inputs">
            <div className="field">
              <label className="field-label" htmlFor="emi-amount">Loan Amount</label>
              <input
                id="emi-amount"
                className="inp"
                inputMode="decimal"
                value={loanAmount}
                onChange={(e) => setLoanAmount(e.target.value)}
                placeholder="100000"
              />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="emi-months">No. of Months</label>
              <input
                id="emi-months"
                className="inp"
                inputMode="numeric"
                value={months}
                onChange={(e) => setMonths(e.target.value)}
                placeholder="24"
              />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="emi-roi">Rate of Interest (ROI)</label>
              <input
                id="emi-roi"
                className="inp"
                inputMode="decimal"
                value={roi}
                onChange={(e) => setRoi(e.target.value)}
                placeholder="14.5"
              />
            </div>
          </div>
        </div>
      </div>

      {!result ? (
        <div className="card">
          <div className="card-body">
            <p className="muted-note">Enter a loan amount and a tenure in months to see the instalment.</p>
          </div>
        </div>
      ) : (
        <>
          <div className="emi-tiles">
            <StatTile label="Monthly EMI" value={formatInr(result.emi)} accent="blue" icon="rupee" />
            <StatTile label="Total Interest" value={formatInr(result.totalInterest)} accent="amber" icon="percent" />
            <StatTile label="Payable Amount" value={formatInr(result.payable)} accent="green" icon="wallet" />
            <StatTile label="Interest in %" value={result.interestPercent.toFixed(2) + ' %'} accent="slate" icon="report" />
          </div>

          <div className="card">
            <div className="card-head">
              <h3>Principal vs interest</h3>
            </div>
            <div className="card-body emi-split">
              <Donut
                loan={loanPart}
                interest={interestPart}
                total={donutTotal}
                loanLabel={formatInr(loanPart)}
                interestLabel={formatInr(interestPart)}
              />
              <ul className="emi-legend">
                <li>
                  <span className="swatch swatch-loan" aria-hidden="true" />
                  <span className="legend-name">Loan</span>
                  <span className="legend-val">{formatInr(loanPart)}</span>
                  <span className="legend-pct">
                    {donutTotal ? ((loanPart / donutTotal) * 100).toFixed(1) : '0.0'} %
                  </span>
                </li>
                <li>
                  <span className="swatch swatch-interest" aria-hidden="true" />
                  <span className="legend-name">Interest</span>
                  <span className="legend-val">{formatInr(interestPart)}</span>
                  <span className="legend-pct">
                    {donutTotal ? ((interestPart / donutTotal) * 100).toFixed(1) : '0.0'} %
                  </span>
                </li>
              </ul>
            </div>
          </div>

          <div className="card">
            <div className="card-head">
              <h3>Repayment schedule</h3>
              <span className="card-head-note">{result.rows.length} instalments</span>
            </div>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Payment No.</th>
                    <th className="num">Beginning Balance</th>
                    <th className="num">EMI</th>
                    <th className="num">Principal</th>
                    <th className="num">Interest</th>
                    <th className="num">Ending Balance</th>
                  </tr>
                </thead>
                <tbody>
                  {result.rows.map((r) => (
                    <tr key={r.n}>
                      <td>{r.n}</td>
                      <td className="num">{formatInr(r.opening)}</td>
                      <td className="num">{formatInr(r.emi)}</td>
                      <td className="num">{formatInr(r.principal)}</td>
                      <td className="num">{formatInr(r.interest)}</td>
                      <td className="num">{formatInr(r.closing)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function StatTile({
  label, value, accent, icon,
}: { label: string; value: string; accent: string; icon: string }) {
  return (
    <div className={`emi-tile emi-tile-${accent}`}>
      <span className="emi-tile-icon"><Icon name={icon} /></span>
      <div className="emi-tile-body">
        <span className="emi-tile-label">{label}</span>
        <span className="emi-tile-value">{value}</span>
      </div>
    </div>
  );
}

/**
 * The legacy screen used a Highcharts pie. This is the same two-slap figure drawn as an
 * inline SVG so the page needs no charting dependency.
 */
function Donut({
  loan, interest, total, loanLabel, interestLabel,
}: { loan: number; interest: number; total: number; loanLabel: string; interestLabel: string }) {
  const R = 70;
  const C = 2 * Math.PI * R;
  const loanPct = total > 0 ? (loan / total) * 100 : 0;
  const interestPct = total > 0 ? 100 - loanPct : 0;

  return (
    <div className="donut-wrap">
      <svg
        className="donut"
        viewBox="0 0 200 200"
        role="img"
        aria-label={`Loan ${formatInr(loan)} (${loanPct.toFixed(1)}%), interest ${formatInr(interest)} (${interestPct.toFixed(1)}%)`}
      >
        <circle cx="100" cy="100" r={R} className="donut-track" />
        <circle
          cx="100" cy="100" r={R}
          className="donut-seg donut-seg-loan"
          strokeDasharray={`${(C * loanPct) / 100} ${C}`}
          transform="rotate(-90 100 100)"
        />
        <circle
          cx="100" cy="100" r={R}
          className="donut-seg donut-seg-interest"
          strokeDasharray={`${(C * interestPct) / 100} ${C}`}
          transform={`rotate(${-90 + (360 * loanPct) / 100} 100 100)`}
        />
        <text x="100" y="96" className="donut-center-label">Total payable</text>
        <text x="100" y="118" className="donut-center-value">{formatInr(loan + interest)}</text>
      </svg>
      <p className="muted-note donut-hint">{loanLabel} principal · {interestLabel} interest</p>
    </div>
  );
}