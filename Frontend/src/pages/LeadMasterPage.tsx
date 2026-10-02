import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { Pager, SortArrow } from '../components/tableView';

interface Row {
  leadId: number;
  leadName: string | null;
  mobile: string | null;
  email: string | null;
  address: string | null;
  remarks: string | null;
  callerRemarks: string | null;
  product: number | null;
  productName: string | null;
  assignedTo: number | null;
  assignedToName: string | null;
  status: string;
  createdDate: string;
  assignedOn: string | null;
}
interface Assignee { empCode: number; employeeName: string | null; designation: string | null }

type SortKey = 'id' | 'name' | 'mobile' | 'status' | 'assigned' | 'created';

/** Badge color per legacy workflow status. */
const statusBadge = (s: string): string => {
  if (['Disbursed', 'Login', 'Pickup', 'Applied'].includes(s)) return 'green';
  if (['Ringing', 'Call Back', 'Sales Followup', 'Docs pending for log'].includes(s)) return 'orange';
  if (['Not Interested', 'Not Doable', 'Not Contactable', 'Already Applied in A'].includes(s)) return 'gray';
  return 'blue';
};

export default function LeadMasterPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [sort, setSort] = useState<{ key: SortKey; dir: 'asc' | 'desc' }>({ key: 'id', dir: 'desc' });

  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');

  const [statuses, setStatuses] = useState<string[]>([]);
  const [assignees, setAssignees] = useState<Assignee[]>([]);
  const [editRow, setEditRow] = useState<Row | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (status) params.set('status', status);
      params.set('sortBy', sort.key);
      params.set('sortDir', sort.dir);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const data = await api.get<{ total: number; rows: Row[]; statuses: string[] }>('/api/leads?' + params.toString());
      setRows(data.rows);
      setTotal(data.total);
      setStatuses(data.statuses);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load leads');
    } finally {
      setLoading(false);
    }
  }, [search, status, sort, page, pageSize]);

  useEffect(() => { const t = setTimeout(load, search ? 350 : 0); return () => clearTimeout(t); }, [load]);
  useEffect(() => { setPage(1); }, [search, status, pageSize]);

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));

  const remove = async (r: Row) => {
    if (!confirm(`Delete lead "${r.leadName}"? It is closed out of the list (status set to deleted) and never shows here again.`)) return;
    try {
      await api.delete('/api/leads/' + r.leadId);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Lead Master</h1>
          <p className="page-sub">Sales leads — track, assign, and convert</p>
        </div>
        <button className="btn primary" onClick={() => setShowAdd(true)}>
          <Icon name="plus" /> Add Lead
        </button>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name, mobile, or email..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={status} onChange={(e) => setStatus(e.target.value)} className="filter-select">
            <option value="">All Statuses</option>
            <option value="Unassigned">Unassigned status</option>
            {statuses.map((s) => <option key={s} value={s}>{s}</option>)}
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
                  <th className="sortable-th" onClick={() => toggleSort('id')}>
                    Lead ID <SortArrow active={sort.key === 'id'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('name')}>
                    Lead Name <SortArrow active={sort.key === 'name'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('mobile')}>
                    Mobile <SortArrow active={sort.key === 'mobile'} dir={sort.dir} />
                  </th>
                  <th>Product</th>
                  <th className="sortable-th" onClick={() => toggleSort('status')}>
                    Status <SortArrow active={sort.key === 'status'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('assigned')}>
                    Assigned To <SortArrow active={sort.key === 'assigned'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('created')}>
                    Created <SortArrow active={sort.key === 'created'} dir={sort.dir} />
                  </th>
                  <th style={{ width: 100 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.leadId}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td className="mono">{r.leadId}</td>
                    <td title={[r.remarks, r.callerRemarks].filter(Boolean).join(' | ') || undefined}>{r.leadName}</td>
                    <td className="mono">{r.mobile || '—'}</td>
                    <td>{r.productName || (r.product ? `#${r.product}` : '—')}</td>
                    <td>
                      <span className={'badge ' + statusBadge(r.status)}>{r.status}</span>
                    </td>
                    <td>{r.assignedToName || '—'}</td>
                    <td>{r.createdDate ? new Date(r.createdDate).toLocaleDateString() : '—'}</td>
                    <td>
                      <div className="row-actions">
                        <button className="icon-btn" title="Edit" onClick={() => setEditRow(r)}>
                          <Icon name="edit" size={16} />
                        </button>
                        <button className="icon-btn danger" title="Delete" onClick={() => remove(r)}>
                          <Icon name="trash" size={16} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {rows.length === 0 && (
                  <tr><td colSpan={9} className="empty">No leads match the filters.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}

        <Pager
          page={page}
          totalPages={Math.max(1, Math.ceil(total / pageSize))}
          totalRows={total}
          pageSize={pageSize}
          onPage={setPage}
          onPageSize={setPageSize}
        />
      </div>

      {(showAdd || editRow) && (
        <LeadModal
          row={editRow}
          onClose={() => { setShowAdd(false); setEditRow(null); }}
          onSaved={(msg) => { setShowAdd(false); setEditRow(null); setError(''); load(); }}
        />
      )}
    </div>
  );
}

function LeadModal({
  row, onClose, onSaved,
}: {
  row: Row | null; onClose: () => void; onSaved: (msg: string) => void;
}) {
  const [assignees, setAssignees] = useState<Assignee[]>([]);
  const [statuses, setStatuses] = useState<string[]>([]);
  const [leadName, setLeadName] = useState(row?.leadName ?? '');
  const [mobile, setMobile] = useState(row?.mobile ?? '');
  const [email, setEmail] = useState(row?.email ?? '');
  const [remarks, setRemarks] = useState(row?.remarks ?? '');
  const [assignedTo, setAssignedTo] = useState(row?.assignedTo ? String(row.assignedTo) : '');
  const [status, setStatus] = useState(row?.status ?? 'Ringing');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    api.get<Assignee[]>('/api/leads/assignees').then(setAssignees).catch(() => setAssignees([]));
    api.get<{ statuses: string[]; allowedStatuses: string[] }>('/api/leads?pageSize=1')
      .then((d) => setStatuses(d.allowedStatuses))
      .catch(() => setStatuses([]));
  }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      const body = {
        leadName,
        mobile,
        email,
        remarks,
        assignedTo: assignedTo ? Number(assignedTo) : null,
        status,
      };
      if (row) {
        await api.put(`/api/leads/${row.leadId}`, body);
        onSaved(`Lead "${leadName}" updated.`);
      } else {
        await api.post('/api/leads', body);
        onSaved(`Lead "${leadName}" created.`);
      }
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{row ? `Edit — ${row.leadName}` : 'Add Lead'}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <label className="field">
            <span className="field-label">LEAD NAME *</span>
            <input value={leadName} onChange={(e) => setLeadName(e.target.value)} required maxLength={150} />
          </label>
          <div className="field-row">
            <label className="field">
              <span className="field-label">MOBILE</span>
              <input value={mobile} onChange={(e) => setMobile(e.target.value)} maxLength={15} />
            </label>
            <label className="field">
              <span className="field-label">EMAIL</span>
              <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={150} />
            </label>
          </div>
          <label className="field">
            <span className="field-label">ASSIGN TO</span>
            <select value={assignedTo} onChange={(e) => setAssignedTo(e.target.value)}>
              <option value="">— Unassigned —</option>
              {assignees.map((a) => (
                <option key={a.empCode} value={a.empCode}>
                  {a.employeeName} ({a.empCode}){a.designation ? ` — ${a.designation}` : ''}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span className="field-label">STATUS</span>
            <select value={status} onChange={(e) => setStatus(e.target.value)}>
              {statuses.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">REMARKS</span>
            <textarea value={remarks} onChange={(e) => setRemarks(e.target.value)} maxLength={500} rows={3} />
          </label>
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving}>{saving ? 'Saving…' : row ? 'Save Changes' : 'Create Lead'}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
