/**
 * EMI arithmetic, ported verbatim from the legacy CIPPortal EMI view
 * (Views/EMI/Index.cshtml → calculateEMI). The legacy page computed this in the browser
 * with no server round trip, and its rounding quirks are visible to users comparing old
 * and new figures, so the sequence of operations is reproduced exactly rather than
 * "improved" — rounding the EMI, then deriving interest from the rounded EMI, is what
 * produces the same total interest and interest-percentage the old screen showed.
 */

export interface EmiRow {
  /** 1-based payment number. */
  n: number;
  /** Balance at the start of the period. */
  opening: number;
  /** Constant EMI for the loan. */
  emi: number;
  /** Principal repaid this period. */
  principal: number;
  /** Interest charged this period. */
  interest: number;
  /** Balance after the payment. */
  closing: number;
}

export interface EmiResult {
  emi: number;
  /** months × EMI. */
  payable: number;
  /** payable − principal. */
  totalInterest: number;
  /** totalInterest as a percentage of payable. */
  interestPercent: number;
  rows: EmiRow[];
}

/** Two-decimal fixed string, the legacy `.toFixed(2)` step inside the amortisation loop. */
const f2 = (n: number) => n.toFixed(2);

/**
 * EMI = P × r × (1+r)^N / ((1+r)^N − 1), with r = (roi/100)/12.
 *
 * The legacy loop deliberately rounds the EMI and the interest to two decimals and then
 * subtracts those *strings*; JavaScript coerces them back to numbers, which is why the
 * arithmetic below uses Number(f2(...)) rather than the raw floats.
 */
export function calculateEmi(loanAmount: number, months: number, roi: number): EmiResult {
  const monthlyInterestRatio = roi / 100 / 12;

  const top = Math.pow(1 + monthlyInterestRatio, months);
  const bottom = top - 1;
  const sp = top / bottom;
  const emi = loanAmount * monthlyInterestRatio * sp;

  const payable = months * emi;
  const totalInterest = payable - loanAmount;
  const interestPercent = payable === 0 ? 0 : (totalInterest / payable) * 100;

  // Legacy used parseInt on the loan amount, so a fractional principal is truncated.
  let balance = parseInt(String(loanAmount), 10);
  const rows: EmiRow[] = [];
  for (let n = 1; n <= months; n++) {
    const interest = balance * monthlyInterestRatio;
    const principal = Number(f2(emi)) - Number(f2(interest));
    const closing = balance - principal;
    rows.push({ n, opening: balance, emi, principal, interest, closing });
    balance = balance - principal;
  }

  return { emi, payable, totalInterest, interestPercent, rows };
}

/** Indian digit grouping (1,23,456.78) — used across all the rupee figures on these pages. */
export function formatInr(n: number, decimals = 2): string {
  if (!isFinite(n)) return '0';
  const fixed = Math.abs(n).toFixed(decimals);
  const [int, frac] = fixed.split('.');
  let out = '';
  if (int.length > 3) {
    const last3 = int.slice(-3);
    const rest = int.slice(0, -3);
    out = rest.replace(/\B(?=(\d{2})+(?!\d))/g, ',') + ',' + last3;
  } else {
    out = int;
  }
  return (n < 0 ? '-' : '') + out + (frac ? '.' + frac : '');
}