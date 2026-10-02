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
  locationName: string | null;
  centerManager: string | null;
  pullDate: string;
  pullTime: string;
}

interface PortalUsage {
  portalPulls: number;
  monthPulls: number;
  monthCaches: number;
  totalPullEvents: number;
  from: string;
  to: string;
  vendor: { available: boolean; balance: number | null; message: string | null; checkedOn: string };
}

const fmtDate = (iso: string) => {
  if (!iso) return '—';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' });
};

export default function ReportsPage() {
  const [rows, setRows] = useState<ReportRow[]>([]);
  const [scope, setScope] = useState<{ isAdmin: boolean; subtreeSize: number } | null>(null);
  const [search, setSearch] = useState('');
  const [type, setType] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [usage, setUsage] = useState<PortalUsage | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState('');

  const isAdmin = isAdminUser();

  type SortKey = 'applicantName' | 'panNo' | 'mobile' | 'reportType' | 'source' | 'centerManager' | 'locationName' | 'pullDate' | 'pullTime';
  const view = useTableView<ReportRow, SortKey>(rows, 'pullDate', (r, k) => {
    if (k === 'pullDate' || k === 'pullTime') return r[k] || '';
    return ((r[k] as string | null) ?? '');
  });

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

  // Portal usage tiles are admin-only; a failure here must not break the grid.
  const loadUsage = useCallback(() => {
    if (!isAdmin) return;
    api.get<PortalUsage>('/api/cibil/portal-usage').then(setUsage).catch(() => {});
  }, [isAdmin]);

  useEffect(() => { loadUsage(); }, [loadUsage]);

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

  const exportExcel = async () => {
    setExporting(true);
    setExportError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (type) params.set('reportType', type);
      const blob = await api.download('/api/cibil/reports/export?' + params.toString());
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `CIBIL_Reports_${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.xlsx`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (e) {
      setExportError(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExporting(false);
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Reports</h1>
          <p className="page-sub">
            Pulled bureau reports with their stored PDFs
            <span className="scope-chip" title={isAdmin ? 'You can see every report in the company' : 'You see reports pulled by you and your downline'}>
              {scope && !scope.isAdmin ? `Your team · ${scope.subtreeSize} people` : isAdmin ? 'All employees' : 'Your team'}
            </span>
          </p>
        </div>
        <button className="btn" onClick={exportExcel} disabled={exporting}>
          {exporting ? 'Preparing…' : 'Export to Excel'}
        </button>
      </div>

      {isAdmin && usage && (
        <div className="stats-grid">
          <div className="card stat-card">
            <div className="stat-icon blue"><Icon name="download" /></div>
            <div className="stat-body">
              <span className="stat-label">CIBIL Portal Pulls</span>
              <span className="stat-value">{usage.portalPulls.toLocaleString('en-IN')}</span>
              <span className="stat-delta" title="API + regeneration pulls — the CIBIL credits this system has consumed">
                credits consumed by this system
              </span>
            </div>
          </div>
          <div className="card stat-card">
            <div className="stat-icon violet"><Icon name="trendingUp" /></div>
            <div className="stat-body">
              <span className="stat-label">Pulls This Month</span>
              <span className="stat-value">{usage.monthPulls.toLocaleString('en-IN')}</span>
              <span className="stat-delta">{usage.monthCaches.toLocaleString('en-IN')} served from cache</span>
            </div>
          </div>
          <div className="card stat-card">
            <div className="stat-icon green"><Icon name="wallet" /></div>
            <div className="stat-body">
              <span className="stat-label">Vendor Credit Balance</span>
              <span className="stat-value">
                {usage.vendor.available ? Number(usage.vendor.balance).toLocaleString('en-IN') : 'Unavailable'}
              </span>
              <span className="stat-delta" title={usage.vendor.message ?? ''}>
                {usage.vendor.available ? `as of ${usage.vendor.checkedOn}` : 'balance endpoint not configured'}
              </span>
            </div>
          </div>
        </div>
      )}

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name, PAN, mobile, location or manager..."
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

        {exportError && <div className="alert error">{exportError}</div>}

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
                  ['mobile', 'Mobile No'],
                  ['reportType', 'Type'],
                  ['source', 'Source'],
                  ['locationName', 'Location'],
                  ['centerManager', 'Center Manager'],
                  ['pullDate', 'CIBIL Pull Date'],
                  ['pullTime', 'CIBIL Pull Time'],
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
                  <td className="mono">{r.mobile || '—'}</td>
                  <td>
                    <span className="badge blue">{r.reportType}</span>
                  </td>
                  <td>
                    <span className={'badge ' + (r.source === 'API' ? 'green' : 'gray')}>
                      {r.source}
                    </span>
                  </td>
                  <td>{r.locationName || '—'}</td>
                  <td title="Centre Manager — the employee who pulled this CIBIL report">
                    {r.centerManager || '—'}
                  </td>
                  <td>{fmtDate(r.pullDate)}</td>
                  <td className="mono">{r.pullTime || '—'}</td>
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
                  <td colSpan={11} className="empty">
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
