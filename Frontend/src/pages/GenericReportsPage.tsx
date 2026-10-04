import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import { api, ApiError, apiUrl } from '../api';
import Icon from '../components/Icon';
import { Pager, SortArrow } from '../components/tableView';
import { trackedFetch } from '../lib/loading';

interface ReportTypeDef {
  value: number;
  text: string;
}

interface RunResult {
  rows: string[][];
  totalRows: number;
  reportType: number;
  reportName: string;
  month: number;
  year: number;
  elapsed: string;
}

const MONTHS = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
];

// Report types that actually use the month/year filters (attendance & disbursal SPs)
const USES_MONTH_YEAR = new Set([1, 3, 4, 5, 6, 7, 8]);

export default function GenericReportsPage() {
  const [types, setTypes] = useState<ReportTypeDef[]>([]);
  const [reportType, setReportType] = useState('');
  const [month, setMonth] = useState('-1');
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<RunResult | null>(null);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);
  const [exporting, setExporting] = useState(false);
  const [rowFilter, setRowFilter] = useState('');
  const [sortCol, setSortCol] = useState<number | null>(null);
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  useEffect(() => {
    api
      .get<ReportTypeDef[]>('/api/generic-reports/types')
      .then(setTypes)
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Failed to load report types'));
  }, []);

  const run = useCallback(async () => {
    if (!reportType) {
      setError('Select a report type first.');
      return;
    }
    setRunning(true);
    setError('');
    setResult(null);
    try {
      const res = await api.post<RunResult>('/api/generic-reports/run', {
        reportType: Number(reportType),
        month: Number(month),
        year: Number(year) || 0,
      });
      const def = types.find((t) => t.value === Number(reportType));
      setResult({ ...res, reportName: def?.text || `Report ${reportType}` });
      setPage(0);
      setRowFilter('');
      setSortCol(null);
      setSortDir('asc');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Report failed');
    } finally {
      setRunning(false);
    }
  }, [reportType, month, year, types]);

  const exportXlsx = async () => {
    if (!reportType) return;
    setExporting(true);
    setError('');
    try {
      const res = await trackedFetch(apiUrl('/api/generic-reports/export'), {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: 'Bearer ' + (localStorage.getItem('cip_token') || ''),
        },
        body: JSON.stringify({
          reportType: Number(reportType),
          month: Number(month),
          year: Number(year) || 0,
        }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => null);
        throw new Error(data?.error || 'Export failed');
      }
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${result?.reportName || 'Report'}_${new Date().toISOString().slice(0, 10)}.xlsx`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExporting(false);
    }
  };

  const header = useMemo(() => result?.rows[0] ?? [], [result]);
  const body = useMemo(() => result?.rows.slice(1) ?? [], [result]);

  // Row text filter across all columns
  const filteredRows = useMemo(() => {
    if (!rowFilter) return body;
    const q = rowFilter.toLowerCase();
    return body.filter((row) => row.some((cell) => (cell ?? '').toLowerCase().includes(q)));
  }, [body, rowFilter]);

  // Column sort (numeric-aware: sort as number when every non-empty value is numeric)
  const sortedRows = useMemo(() => {
    if (sortCol === null) return filteredRows;
    const dir = sortDir === 'asc' ? 1 : -1;
    return [...filteredRows].sort((a, b) => {
      const av = a[sortCol] ?? '';
      const bv = b[sortCol] ?? '';
      const an = av !== '' && !isNaN(Number(av));
      const bn = bv !== '' && !isNaN(Number(bv));
      if (an && bn) return (Number(av) - Number(bv)) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  }, [filteredRows, sortCol, sortDir]);

  const totalPages = Math.max(1, Math.ceil(sortedRows.length / pageSize));
  const pageRows = useMemo(
    () => sortedRows.slice(page * pageSize, page * pageSize + pageSize),
    [sortedRows, page, pageSize]
  );

  const toggleColSort = (idx: number) => {
    if (sortCol === idx) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else {
      setSortCol(idx);
      setSortDir('asc');
    }
  };

  const years = useMemo(() => {
    const now = new Date().getFullYear();
    return Array.from({ length: 5 }, (_, i) => now - i);
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    run();
  };

  return (
    <div className="">
      <div className="page-head">
        <div>
          <h1>Generic Reports</h1>
          <p className="page-sub">Run any configured MIS report and export to Excel</p>
        </div>
      </div>

      <form className="card form-card" onSubmit={submit}>
        <div className="field-row filters-row">
          <label className="field">
            <span className="field-label">REPORT TYPE</span>
            <select value={reportType} onChange={(e) => { setReportType(e.target.value); setResult(null); setError(''); }}>
              <option value="">---Select---</option>
              {types.map((t) => (
                <option key={t.value} value={t.value}>{t.text}</option>
              ))}
            </select>
          </label>

          {USES_MONTH_YEAR.has(Number(reportType)) && (
            <>
              <label className="field">
                <span className="field-label">MONTH</span>
                <select value={month} onChange={(e) => setMonth(e.target.value)}>
                  <option value="-1">--All--</option>
                  {MONTHS.map((m, i) => (
                    <option key={m} value={i + 1}>{m}</option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span className="field-label">YEAR</span>
                <select value={year} onChange={(e) => setYear(e.target.value)}>
                  {years.map((y) => (
                    <option key={y} value={y}>{y}</option>
                  ))}
                </select>
              </label>
            </>
          )}

          <div className="run-actions">
            <button className="btn primary" disabled={running || !reportType}>
              <Icon name="search" size={16} /> {running ? 'Running…' : 'Run Report'}
            </button>
            {result && result.totalRows > 0 && (
              <button
                type="button"
                className="btn secondary"
                onClick={exportXlsx}
                disabled={exporting}
              >
                {exporting ? 'Exporting…' : 'Export to Excel'}
              </button>
            )}
          </div>
        </div>

        {error && <div className="alert error">{error}</div>}
      </form>        {result && (
          <div className="card report-grid-card">
            <div className="card-head report-meta">
              <h3>
                {result.reportName}
                <span className="meta-chip">{result.totalRows.toLocaleString('en-IN')} rows</span>
                {rowFilter && (
                  <span className="meta-chip">
                    {filteredRows.length.toLocaleString('en-IN')} match“{rowFilter}”
                  </span>
                )}
                <span className="meta-chip">{(parseFloat(result.elapsed) * 1000 || 0).toFixed(0)} ms</span>
              </h3>
            </div>

            {body.length > 0 && (
              <div className="table-toolbar">
                <div className="search-box">
                  <Icon name="search" />
                  <input
                    placeholder="Filter rows…"
                    value={rowFilter}
                    onChange={(e) => {
                      setRowFilter(e.target.value);
                      setPage(0);
                    }}
                  />
                </div>
              </div>
            )}

            {sortedRows.length === 0 ? (
              <div className="empty">
                {body.length === 0
                  ? 'The report ran successfully but returned no rows.'
                  : 'No rows match the filter.'}
              </div>
            ) : (
              <>
                <table className="data-table report-grid">
                  <thead>
                    <tr>
                      {header.map((h, i) => (
                        <th key={i} className="sortable-th" onClick={() => toggleColSort(i)}>
                          {h}
                          <SortArrow active={sortCol === i} dir={sortDir} />
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {pageRows.map((row, ri) => (
                      <tr key={ri}>
                        {row.map((cell, ci) => (
                          <td key={ci}>{cell}</td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
                <Pager
                  page={page}
                  totalPages={totalPages}
                  totalRows={sortedRows.length}
                  pageSize={pageSize}
                  onPage={setPage}
                  onPageSize={setPageSize}
                  sizes={[25, 50, 200, 100000]}
                />
              </>
            )}
          </div>
        )}
    </div>
  );
}
