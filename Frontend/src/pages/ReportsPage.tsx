import { useCallback, useEffect, useState } from 'react';
import { api, fetchReportPdf, isAdminUser } from '../api';
import Icon from '../components/Icon';
import { useTableView, Pager, SortArrow } from '../components/tableView';

interface ReportRow {
  reportId: number;
  panNo: string;
  applicantName: string | null;
  mobile: string | null;
  reportType: string;
  fileName: string;
  fileSize: number | null;
  pulledByName: string | null;
  pulledDate: string;
  source: string;
}

export default function ReportsPage() {
  const [rows, setRows] = useState<ReportRow[]>([]);
  const [scope, setScope] = useState<{ isAdmin: boolean; subtreeSize: number } | null>(null);
  const [search, setSearch] = useState('');
  const [type, setType] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  type SortKey = 'applicantName' | 'panNo' | 'reportType' | 'source' | 'pulledByName' | 'pulledDate';
  const view = useTableView<ReportRow, SortKey>(rows, 'pulledDate', (r, k) =>
    k === 'pulledDate' ? new Date(r.pulledDate).getTime() : ((r[k] as string | null) ?? '')
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (type) params.set('reportType', type);
      const data = await api.get<{ total: number; rows: ReportRow[]; scope?: { isAdmin: boolean; subtreeSize: number } }>('/api/cibil/reports?' + params.toString());
      setRows(data.rows);
      setScope(data.scope ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load reports');
    } finally {
      setLoading(false);
    }
  }, [search, type]);

  useEffect(() => {
    const t = setTimeout(load, search ? 300 : 0);
    return () => clearTimeout(t);
  }, [load]);

  const viewPdf = async (r: ReportRow) => {
    const blob = await fetchReportPdf(r.reportId);
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  };

  const downloadPdf = async (r: ReportRow) => {
    const blob = await fetchReportPdf(r.reportId);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = r.fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Reports</h1>
          <p className="page-sub">
            Pulled bureau reports with their stored PDFs
            <span className="scope-chip" title={isAdminUser() ? 'You can see every report in the company' : 'You see reports pulled by you and your downline'}>
              {scope && !scope.isAdmin ? `Your team · ${scope.subtreeSize} people` : isAdminUser() ? 'All employees' : 'Your team'}
            </span>
          </p>
        </div>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name or PAN..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={type} onChange={(e) => setType(e.target.value)} className="filter-select">
            <option value="">All Types</option>
            <option value="cibil">CIBIL</option>
            <option value="experian">Experian</option>
            <option value="equifax">Equifax</option>
            <option value="crif">CRIF</option>
          </select>
        </div>

        {loading ? (
          <div className="loading">Loading…</div>
        ) : error ? (
          <div className="alert error">{error}</div>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>#</th>
                {([
                  ['applicantName', 'Name'],
                  ['panNo', 'PAN'],
                  ['reportType', 'Type'],
                  ['source', 'Source'],
                  ['pulledByName', 'Pulled By'],
                  ['pulledDate', 'Pulled On'],
                ] as [SortKey, string][]).map(([key, label]) => (
                  <th key={key} className="sortable-th" onClick={() => view.toggleSort(key)}>
                    {label}
                    <SortArrow active={view.sort.key === key} dir={view.sort.dir} />
                  </th>
                ))}
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {view.pageRows.map((r, i) => (
                <tr key={r.reportId}>
                  <td>{view.page * view.pageSize + i + 1}</td>
                  <td>{r.applicantName || '—'}</td>
                  <td className="mono">{r.panNo}</td>
                  <td>
                    <span className="badge blue">{r.reportType}</span>
                  </td>
                  <td>
                    <span className={'badge ' + (r.source === 'CACHE' ? 'gray' : 'green')}>
                      {r.source}
                    </span>
                  </td>
                  <td>{r.pulledByName || '—'}</td>
                  <td>{new Date(r.pulledDate).toLocaleString('en-IN')}</td>
                  <td>
                    <div className="row-actions">
                      <button className="btn small" onClick={() => viewPdf(r)}>
                        View
                      </button>
                      <button className="btn small secondary" onClick={() => downloadPdf(r)}>
                        Download
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
              {view.pageRows.length === 0 && (
                <tr>
                  <td colSpan={8} className="empty">
                    No reports found.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}

        <Pager
          page={view.page}
          totalPages={view.totalPages}
          totalRows={view.totalRows}
          pageSize={view.pageSize}
          onPage={view.setPage}
          onPageSize={view.setPageSize}
        />
      </div>
    </div>
  );
}
