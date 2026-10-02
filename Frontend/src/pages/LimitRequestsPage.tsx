import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { Pager, SortArrow, useTableView } from '../components/tableView';

interface LimitRequestInfo {
  requestId: number;
  empCode: number;
  empName: string;
  requestMonth: string;
  currentCount: number;
  requestedLimit: number;
  reason: string | null;
  status: 'Pending' | 'Approved' | 'Rejected';
  approvedLimit: number | null;
  decidedByName: string | null;
  decidedDate: string | null;
  createdDate: string;
}

type StatusFilter = '' | 'Pending' | 'Approved' | 'Rejected';

export default function LimitRequestsPage() {
  const [rows, setRows] = useState<LimitRequestInfo[]>([]);
  const [status, setStatus] = useState<StatusFilter>('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [deciding, setDeciding] = useState<number | null>(null);
  const [overrideLimit, setOverrideLimit] = useState('');

  type SortKey = 'empName' | 'requestMonth' | 'currentCount' | 'requestedLimit' | 'status' | 'createdDate';
  const view = useTableView<LimitRequestInfo, SortKey>(rows, 'createdDate', (r, k) => {
    if (k === 'createdDate') return new Date(r.createdDate).getTime();
    const v = r[k];
    return typeof v === 'number' ? v : (v as string | null) ?? '';
  });

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (status) params.set('status', status);
      setRows(await api.get<LimitRequestInfo[]>('/api/cibil/limit-requests?' + params.toString()));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load requests');
    } finally {
      setLoading(false);
    }
  }, [status]);

  useEffect(() => { load(); }, [load]);

  const decide = async (r: LimitRequestInfo, approve: boolean) => {
    const limit = overrideLimit.trim() ? Number(overrideLimit) : r.requestedLimit;
    const msg = approve
      ? `Approve ${r.empName}'s request for ${r.requestedLimit} pulls this month${limit !== r.requestedLimit ? ` (overridden to ${limit})` : ''}?`
      : `Reject ${r.empName}'s request for ${r.requestedLimit} pulls?`;
    if (!confirm(msg)) return;
    setDeciding(r.requestId);
    setError('');
    try {
      await api.post(`/api/cibil/limit-requests/${r.requestId}/decide`, {
        approve,
        approvedLimit: approve ? limit : null,
      });
      setNotice(`Request ${approve ? 'approved' : 'rejected'} for ${r.empName}.`);
      setOverrideLimit('');
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to save decision');
    } finally {
      setDeciding(null);
    }
  };

  const pendingCount = rows.filter((r) => r.status === 'Pending').length;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>CIBIL Limit Requests</h1>
          <p className="page-sub">Approve or reject users' requests to raise their monthly CIBIL pull limit</p>
        </div>
        {pendingCount > 0 && <span className="badge pending">{pendingCount} pending</span>}
      </div>

      {notice && <div className="alert success">{notice}</div>}

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box" style={{ visibility: 'hidden' }} aria-hidden="true">
            <input tabIndex={-1} />
          </div>
          <select value={status} onChange={(e) => setStatus(e.target.value as StatusFilter)} className="filter-select">
            <option value="">All Status</option>
            <option value="Pending">Pending</option>
            <option value="Approved">Approved</option>
            <option value="Rejected">Rejected</option>
          </select>
        </div>

        {loading ? (
          <div className="loading">Loading…</div>
        ) : error ? (
          <div className="alert error">{error}</div>
        ) : (
          <div className="table-scroll">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  {([
                    ['empName', 'Employee'],
                    ['requestMonth', 'Month'],
                    ['currentCount', 'Used'],
                    ['requestedLimit', 'Requested'],
                    'reason',
                    ['status', 'Status'],
                    ['createdDate', 'Submitted'],
                  ] as (SortKey | string)[]).map((col) =>
                    typeof col === 'string' ? (
                      <th key={col}>{col === 'reason' ? 'Reason' : col}</th>
                    ) : (
                      <th key={col} className="sortable-th" onClick={() => view.toggleSort(col)}>
                        {{ empName: 'Employee', requestMonth: 'Month', currentCount: 'Used', requestedLimit: 'Requested', status: 'Status', createdDate: 'Submitted' }[col]}
                        <SortArrow active={view.sort.key === col} dir={view.sort.dir} />
                      </th>
                    )
                  )}
                  <th>Decision</th>
                </tr>
              </thead>
              <tbody>
                {view.pageRows.map((r, i) => (
                  <tr key={r.requestId} className={r.status === 'Pending' ? 'row-pending' : ''}>
                    <td>{view.page * view.pageSize + i + 1}</td>
                    <td>{r.empName} <span className="mono muted">#{r.empCode}</span></td>
                    <td className="mono">{r.requestMonth}</td>
                    <td>{r.currentCount}</td>
                    <td><b>{r.requestedLimit}</b></td>
                    <td className="reason-cell" title={r.reason || ''}>{r.reason || '—'}</td>
                    <td>
                      <span className={'badge ' + (r.status === 'Approved' ? 'green' : r.status === 'Rejected' ? 'gray' : 'pending')}>
                        {r.status}{r.status === 'Approved' && r.approvedLimit ? ` · ${r.approvedLimit}` : ''}
                      </span>
                    </td>
                    <td>{new Date(r.createdDate).toLocaleString()}</td>
                    <td>
                      {r.status === 'Pending' ? (
                        <div className="row-actions">
                          <input
                            className="override-input"
                            type="number"
                            min={1}
                            placeholder={String(r.requestedLimit)}
                            title="Optional: override the approved limit"
                            value={overrideLimit}
                            onChange={(e) => setOverrideLimit(e.target.value)}
                          />
                          <button className="btn small primary" disabled={deciding === r.requestId} onClick={() => decide(r, true)}>
                            Approve
                          </button>
                          <button className="btn small danger" disabled={deciding === r.requestId} onClick={() => decide(r, false)}>
                            Reject
                          </button>
                        </div>
                      ) : (
                        <span className="muted">
                          {r.decidedByName || '—'}{r.decidedDate ? ` · ${new Date(r.decidedDate).toLocaleDateString()}` : ''}
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
                {view.pageRows.length === 0 && (
                  <tr><td colSpan={9} className="empty">No limit requests found.</td></tr>
                )}
              </tbody>
            </table>
          </div>
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
