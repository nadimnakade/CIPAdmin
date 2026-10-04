import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ApiError, getToken, isAdminUser, apiUrl } from '../api';
import Icon from '../components/Icon';
import { useTableView, Pager, SortArrow } from '../components/tableView';
import { trackedFetch } from '../lib/loading';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';

interface DidRow {
  didId: number;
  empCode: number | null;
  employeeCodeText: string | null;
  employeeName: string | null;
  designation: string | null;
  didNumber: string;
  extensionNumber: string | null;
  status: string;
  assignedDate: string | null;
  baseUrl: string | null;
  click2CallPath: string | null;
  tokenId: string | null;
  extensionPassword: string | null;
  sipServer: string | null;
  sipPort: number | null;
  sipPassword: string | null;
  sipRealm: string | null;
}

/** CloudConnect defaults applied when a field is left blank. */
const DialerDefaults = {
  baseUrl: 'https://crm2.cloud-connect.in/ccpl_api/v1.4/',
  click2CallPath: 'api/info/click2call',
  tokenId: '6JU4KMMIO6B1xzLt',
  extensionPassword: '15e8e7c72ea16ca290930fd7c4db760b',
  sipServer: 'sip2.cloud-connect.in',
  sipPort: '7065',
  sipPassword: 'Tejas@123',
  sipRealm: 'sip2.cloud-connect.in',
};

interface DidStats {
  totalEmployees: number;
  assignedDids: number;
  activeEmployees: number;
  inactiveEmployees: number;
  scope?: { isAdmin: boolean; subtreeSize: number };
}

interface EmployeeOption {
  empCode: number;
  employeeCodeText: string;
  employeeName: string;
  designation: string | null;
}

interface DesignationOption { id: number; name: string }

export default function EmployeesPage() {
  const [rows, setRows] = useState<DidRow[]>([]);
  const [stats, setStats] = useState<DidStats | null>(null);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [designationId, setDesignationId] = useState('');
  const [designations, setDesignations] = useState<DesignationOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  

  // Inline add panel (screenshot layout) instead of a modal
  const [showAdd, setShowAdd] = useState(false);
  const [changeRow, setChangeRow] = useState<DidRow | null>(null);

  type SortKey = 'employeeCodeText' | 'employeeName' | 'designation' | 'didNumber' | 'extensionNumber' | 'sipServer' | 'status' | 'assignedDate';
  const view = useTableView<DidRow, SortKey>(rows, 'employeeName', (r, k) => {
    const v = r[k];
    if (k === 'assignedDate') return v ? new Date(v as string).getTime() : 0;
    return (v as string | null) ?? '';
  });

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (status) params.set('status', status);
      if (designationId) params.set('designationId', designationId);
      const data = await api.get<DidRow[]>('/api/employees/dids?' + params.toString());
      setRows(data);
      api.get<DidStats>('/api/employees/dids/stats').then(setStats).catch(() => {});
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load');
    } finally {
      setLoading(false);
    }
  }, [search, status, designationId]);

  useEffect(() => {
    const t = setTimeout(load, search ? 300 : 0);
    return () => clearTimeout(t);
  }, [load]);

  useEffect(() => {
    api.get<DesignationOption[]>('/api/employees/designations').then(setDesignations).catch(() => {});
  }, []);

  const exportExcel = () => {
    const params = new URLSearchParams();
    if (search) params.set('search', search);
    if (status) params.set('status', status);
    if (designationId) params.set('designationId', designationId);
    trackedFetch(apiUrl('/api/employees/dids/export?' + params.toString()), {
      headers: { Authorization: `Bearer ${getToken()}` },
    })
      .then(async (res) => {
        if (!res.ok) throw new Error('Export failed');
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `EmployeeDIDs_${new Date().toISOString().slice(0, 10)}.xlsx`;
        a.click();
        URL.revokeObjectURL(url);
      })
      .catch((e) => setError(e.message || 'Export failed'));
  };

  const assignedPct = stats && stats.totalEmployees > 0
    ? Math.round((stats.assignedDids / stats.totalEmployees) * 100)
    : 0;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Employee DID Management</h1>
          <p className="page-sub">
            View and manage employee DID assignments
            <span className="scope-chip" title={isAdminUser() ? 'You can see the whole company' : 'You see your own assignments and your downline\'s'}>
              {stats?.scope && !stats.scope.isAdmin ? `Your team · ${stats.scope.subtreeSize} people` : isAdminUser() ? 'All employees' : 'Your team'}
            </span>
          </p>
        </div>
        <button className={'btn primary' + (showAdd ? ' subtle' : '')} onClick={() => setShowAdd((s) => !s)}>
          <Icon name={showAdd ? 'close' : 'plus'} /> {showAdd ? 'Close Panel' : 'Add Employee'}
        </button>
      </div>

      <div className="stats-grid did-stats">
        <div className="card stat-card">
          <div className="stat-icon blue"><Icon name="users" size={22} /></div>
          <div className="stat-body">
            <span className="stat-label">Total Employees</span>
            <span className="stat-value">{(stats?.totalEmployees ?? 0).toLocaleString()}</span>
            <span className="stat-delta">in master data</span>
          </div>
        </div>
        <div className="card stat-card">
          <div className="stat-icon green"><Icon name="phoneCall" size={22} /></div>
          <div className="stat-body">
            <span className="stat-label">Assigned DIDs</span>
            <span className="stat-value">{(stats?.assignedDids ?? 0).toLocaleString()}</span>
            <span className="stat-delta"><span className="delta up">{assignedPct}%</span> of total employees</span>
          </div>
        </div>
        <div className="card stat-card">
          <div className="stat-icon violet"><Icon name="user" size={22} /></div>
          <div className="stat-body">
            <span className="stat-label">Active Employees</span>
            <span className="stat-value">{(stats?.activeEmployees ?? 0).toLocaleString()}</span>
            <span className="stat-delta">currently active</span>
          </div>
        </div>
        <div className="card stat-card">
          <div className="stat-icon red"><Icon name="users" size={22} /></div>
          <div className="stat-body">
            <span className="stat-label">Inactive Employees</span>
            <span className="stat-value">{(stats?.inactiveEmployees ?? 0).toLocaleString()}</span>
            <span className="stat-delta">with inactive DIDs</span>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name, code, SIP username, or extension..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={status} onChange={(e) => setStatus(e.target.value)} className="filter-select">
            <option value="">All Status</option>
            <option value="Active">Active</option>
            <option value="Inactive">Inactive</option>
          </select>
          <select value={designationId} onChange={(e) => setDesignationId(e.target.value)} className="filter-select">
            <option value="">All Designations</option>
            {designations.map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
          <button className="btn" onClick={exportExcel} title="Export the filtered list to Excel">
            <Icon name="download" /> Export
          </button>
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
                    ['employeeCodeText', 'Employee Code'],
                    ['employeeName', 'Employee Name'],
                    ['designation', 'Designation'],
                    ['didNumber', 'SIP Username'],
                    ['extensionNumber', 'Extension'],
                    ['sipServer', 'SIP Server'],
                    ['status', 'Status'],
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
                  <tr key={r.didId}>
                    <td>{view.page * view.pageSize + i + 1}</td>
                    <td className="mono">{r.employeeCodeText || 'OTHER'}</td>
                    <td>{r.employeeName || 'Other (Generic)'}</td>
                    <td>{r.designation || '—'}</td>
                    <td className="mono">{r.didNumber}</td>
                    <td className="mono">{r.extensionNumber || '—'}</td>
                    <td className="mono" title={r.tokenId ? `Token: ${r.tokenId}` : undefined}>
                      {r.sipServer || '—'}
                      {r.sipPort ? `:${r.sipPort}` : ''}
                    </td>
                    <td>
                      <span className={'badge ' + (r.status === 'Active' ? 'green' : 'gray')}>
                        {r.status}
                      </span>
                    </td>
                    <td>
                      <div className="row-actions">
                        <button className="btn small" onClick={() => setChangeRow(r)}>
                          Change DID
                        </button>
                        <button
                          className="icon-btn danger"
                          title="Release DID"
                          onClick={async () => {
                            const ok = await confirmAction({
                              title: 'Release this SIP username?',
                              text: `SIP username ${r.didNumber} (extension ${r.extensionNumber || '—'}) is freed for reassignment.`,
                              confirmText: 'Release',
                              tone: 'danger',
                            });
                            if (!ok) return;
                            try {
                              await api.delete('/api/employees/dids/' + r.didId);
                              toast.success(`SIP username ${r.didNumber} released.`);
                              load();
                            } catch (e) {
                              setError(e instanceof ApiError ? e.message : 'Release failed');
                            }
                          }}
                        >
                          <Icon name="trash" size={16} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {view.pageRows.length === 0 && (
                  <tr>
                    <td colSpan={8} className="empty">
                      No DID assignments yet — assign a DID to an employee to see it here.
                    </td>
                  </tr>
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

      {showAdd && (
        <AddEmployeePanel
          onClose={() => setShowAdd(false)}
          onSaved={(msg) => {
            setShowAdd(false);
            toast.success(msg);
            load();
          }}
        />
      )}
      {changeRow && (
        <ChangeDidModal
          row={changeRow}
          onClose={() => setChangeRow(null)}
          onSaved={(msg) => {
            setChangeRow(null);
            toast.success(msg);
            load();
          }}
        />
      )}
    </div>
  );
}

/** Inline add panel matching the reference design — employee picker auto-fills the rest. */
function AddEmployeePanel({
  onClose,
  onSaved,
}: {
  onClose: () => void;
  onSaved: (msg: string) => void;
}) {
  const [options, setOptions] = useState<EmployeeOption[]>([]);
  const [optionSearch, setOptionSearch] = useState('');
  const [empCode, setEmpCode] = useState('');
  const [assignGeneric, setAssignGeneric] = useState(false);
  const [did, setDid] = useState('');
  const [showConfig, setShowConfig] = useState(false);
  const [cfg, setCfg] = useState({ ...DialerDefaults });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    const t = setTimeout(() => {
      api
        .get<EmployeeOption[]>('/api/employees/without-did?search=' + encodeURIComponent(optionSearch))
        .then(setOptions)
        .catch(() => setOptions([]));
    }, 250);
    return () => clearTimeout(t);
  }, [optionSearch]);

  const selected = options.find((o) => String(o.empCode) === empCode);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await api.post('/api/employees/dids', {
        empCode: assignGeneric ? null : Number(empCode),
        didNumber: did,
        baseUrl: cfg.baseUrl,
        click2CallPath: cfg.click2CallPath,
        tokenId: cfg.tokenId,
        extensionPassword: cfg.extensionPassword,
        sipServer: cfg.sipServer,
        sipPort: Number(cfg.sipPort) || null,
        sipPassword: cfg.sipPassword,
        sipRealm: cfg.sipRealm,
      });
      onSaved(
        assignGeneric
          ? `Generic SIP username ${did} assigned.`
          : `SIP username ${did} assigned to ${selected?.employeeName ?? 'employee'}.`
      );
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="card inline-panel">
      <div className="inline-panel-head">
        <div className="inline-panel-title">
          <span className="inline-panel-icon blue"><Icon name="user" size={18} /></span>
          <div>
            <h3>Assign SIP Username / DID</h3>
            <p>Select an employee from master data and assign a SIP username</p>
          </div>
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="Close panel">
          <Icon name="close" size={16} />
        </button>
      </div>

      <form onSubmit={submit} className="inline-panel-form">
        <label className="field">
          <span className="field-label">EMPLOYEE *</span>
          {assignGeneric ? (
            <input value="Other (Generic / shared DID)" disabled />
          ) : (
            <>
              <input
                placeholder="Type to search employees…"
                value={optionSearch}
                onChange={(e) => setOptionSearch(e.target.value)}
              />
              <select
                value={empCode}
                onChange={(e) => setEmpCode(e.target.value)}
                required
                disabled={assignGeneric}
                size={1}
              >
                <option value="">Select employee…</option>
                {options.map((o) => (
                  <option key={o.empCode} value={o.empCode}>
                    {o.employeeName} ({o.employeeCodeText || o.empCode}){o.designation ? ` — ${o.designation}` : ''}
                  </option>
                ))}
              </select>
            </>
          )}
        </label>
        <label className="field">
          <span className="field-label">EMPLOYEE CODE</span>
          <input value={selected?.employeeCodeText || ''} placeholder="Auto-filled" disabled />
        </label>
        <label className="field">
          <span className="field-label">EMPLOYEE NAME</span>
          <input value={selected?.employeeName || ''} placeholder="Auto-filled" disabled />
        </label>
        <label className="field">
          <span className="field-label">DESIGNATION</span>
          <input value={selected?.designation || ''} placeholder="Auto-filled" disabled />
        </label>
        <label className="field">
          <span className="field-label">SIP USERNAME *</span>
          <input
            placeholder="Enter SIP username"
            value={did}
            onChange={(e) => setDid(e.target.value)}
            required
          />
        </label>
        <label className="field">
          <span className="field-label">EXTENSION NUMBER</span>
          <input value={did.trim() ? did.trim().slice(-3) : ''} placeholder="Auto-filled (last 3 of SIP username)" disabled />
        </label>
        <div className="config-toggle">
          <button type="button" className="link-btn" onClick={() => setShowConfig((s) => !s)}>
            <Icon name={showConfig ? 'chevronDown' : 'chevronRight'} size={12} /> Dialer config (CloudConnect)
          </button>
        </div>
        {showConfig && (
          <>
            <label className="field">
              <span className="field-label">CRM BASE URL</span>
              <input value={cfg.baseUrl} onChange={(e) => setCfg({ ...cfg, baseUrl: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">CLICK2CALL ENDPOINT</span>
              <input value={cfg.click2CallPath} onChange={(e) => setCfg({ ...cfg, click2CallPath: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">TOKEN ID</span>
              <input value={cfg.tokenId} onChange={(e) => setCfg({ ...cfg, tokenId: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">EXTENSION PASSWORD</span>
              <input value={cfg.extensionPassword} onChange={(e) => setCfg({ ...cfg, extensionPassword: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">SIP SERVER</span>
              <input value={cfg.sipServer} onChange={(e) => setCfg({ ...cfg, sipServer: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">SIP PORT</span>
              <input value={cfg.sipPort} onChange={(e) => setCfg({ ...cfg, sipPort: e.target.value })} inputMode="numeric" />
            </label>
            <label className="field">
              <span className="field-label">SIP PASSWORD</span>
              <input value={cfg.sipPassword} onChange={(e) => setCfg({ ...cfg, sipPassword: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">SIP REALM</span>
              <input value={cfg.sipRealm} onChange={(e) => setCfg({ ...cfg, sipRealm: e.target.value })} />
            </label>
          </>
        )}
        <div className="inline-panel-note">
          Employee code, name and designation are auto-filled from Employee Master.
          The extension number is the last 3 digits of the SIP username. The dialer
          config defaults to CloudConnect and is stored on the DID row.
        </div>
        {error && <div className="alert error">{error}</div>}
        <div className="inline-panel-actions">
          <label className="check">
            <input
              type="checkbox"
              checked={assignGeneric}
              onChange={(e) => {
                setAssignGeneric(e.target.checked);
                setEmpCode('');
              }}
            />
            Assign as generic/shared DID
          </label>
          <div className="spacer" />
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button className="btn primary" disabled={saving || (!assignGeneric && !empCode) || !did}>
            {saving ? 'Saving…' : 'Save Employee'}
          </button>
        </div>
      </form>
    </div>
  );
}

function ChangeDidModal({
  row,
  onClose,
  onSaved,
}: {
  row: DidRow;
  onClose: () => void;
  onSaved: (msg: string) => void;
}) {
  const [did, setDid] = useState(row.didNumber);
  const [status, setStatus] = useState(row.status);
  const [showConfig, setShowConfig] = useState(false);
  const [cfg, setCfg] = useState({
    baseUrl: row.baseUrl ?? '',
    click2CallPath: row.click2CallPath ?? '',
    tokenId: row.tokenId ?? '',
    extensionPassword: row.extensionPassword ?? '',
    sipServer: row.sipServer ?? '',
    sipPort: row.sipPort != null ? String(row.sipPort) : '',
    sipPassword: row.sipPassword ?? '',
    sipRealm: row.sipRealm ?? '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await api.put('/api/employees/dids/' + row.didId, {
        didId: row.didId,
        newDidNumber: did,
        status,
        baseUrl: cfg.baseUrl || null,
        click2CallPath: cfg.click2CallPath || null,
        tokenId: cfg.tokenId || null,
        extensionPassword: cfg.extensionPassword || null,
        sipServer: cfg.sipServer || null,
        sipPort: cfg.sipPort ? Number(cfg.sipPort) : null,
        sipPassword: cfg.sipPassword || null,
        sipRealm: cfg.sipRealm || null,
      });
      onSaved(`SIP username updated for ${row.employeeName || 'Generic'} — now ${did} (extension ${did.trim().slice(-3)}).`);
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
          <h3>Change SIP Username — {row.employeeName || 'Generic'}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <label className="field">
            <span className="field-label">SIP USERNAME</span>
            <input value={did} onChange={(e) => setDid(e.target.value)} required />
          </label>
          <label className="field">
            <span className="field-label">EXTENSION NUMBER</span>
            <input value={did.trim() ? did.trim().slice(-3) : ''} placeholder="Auto-filled (last 3 of SIP username)" disabled />
          </label>
          <label className="field">
            <span className="field-label">STATUS</span>
            <select value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="Active">Active</option>
              <option value="Inactive">Inactive</option>
            </select>
          </label>
          <div className="config-toggle">
            <button type="button" className="link-btn" onClick={() => setShowConfig((s) => !s)}>
              <Icon name={showConfig ? 'chevronDown' : 'chevronRight'} size={12} /> Dialer config (CloudConnect)
            </button>
          </div>
          {showConfig && (
            <>
              <label className="field">
                <span className="field-label">CRM BASE URL</span>
                <input value={cfg.baseUrl} onChange={(e) => setCfg({ ...cfg, baseUrl: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">CLICK2CALL ENDPOINT</span>
                <input value={cfg.click2CallPath} onChange={(e) => setCfg({ ...cfg, click2CallPath: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">TOKEN ID</span>
                <input value={cfg.tokenId} onChange={(e) => setCfg({ ...cfg, tokenId: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">EXTENSION PASSWORD</span>
                <input value={cfg.extensionPassword} onChange={(e) => setCfg({ ...cfg, extensionPassword: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">SIP SERVER</span>
                <input value={cfg.sipServer} onChange={(e) => setCfg({ ...cfg, sipServer: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">SIP PORT</span>
                <input value={cfg.sipPort} onChange={(e) => setCfg({ ...cfg, sipPort: e.target.value })} inputMode="numeric" />
              </label>
              <label className="field">
                <span className="field-label">SIP PASSWORD</span>
                <input value={cfg.sipPassword} onChange={(e) => setCfg({ ...cfg, sipPassword: e.target.value })} />
              </label>
              <label className="field">
                <span className="field-label">SIP REALM</span>
                <input value={cfg.sipRealm} onChange={(e) => setCfg({ ...cfg, sipRealm: e.target.value })} />
              </label>
            </>
          )}
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving}>
              {saving ? 'Saving…' : 'Save Changes'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
