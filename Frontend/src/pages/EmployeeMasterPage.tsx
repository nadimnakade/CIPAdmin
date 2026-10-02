import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ApiError, isAdminUser } from '../api';
import Icon from '../components/Icon';
import { Pager, SortArrow } from '../components/tableView';
import DocumentsDrawer from '../components/DocumentsDrawer';

interface Row {
  empCode: number;
  employeeCodeText: string | null;
  employeeName: string | null;
  designationId: number | null;
  designation: string | null;
  email: string | null;
  locationId: number | null;
  location: string | null;
  doj: string | null;
  contact: string | null;
  isActive: boolean;
}
interface Option { id: number; name: string }

type SortKey = 'code' | 'name' | 'designation' | 'email' | 'location' | 'doj' | 'status';

export default function EmployeeMasterPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [total, setTotal] = useState(0);
  const [scope, setScope] = useState<{ isAdmin: boolean; subtreeSize: number } | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [sort, setSort] = useState<{ key: SortKey; dir: 'asc' | 'desc' }>({ key: 'name', dir: 'asc' });

  const [search, setSearch] = useState('');
  const [designationId, setDesignationId] = useState('');
  const [active, setActive] = useState('true');

  const [designations, setDesignations] = useState<Option[]>([]);
  const [locations, setLocations] = useState<Option[]>([]);
  const [docsEmp, setDocsEmp] = useState<{ code: number; name: string } | null>(null);
  const [editRow, setEditRow] = useState<Row | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (designationId) params.set('designationId', designationId);
      if (active) params.set('active', active);
      params.set('sortBy', sort.key);
      params.set('sortDir', sort.dir);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const data = await api.get<{ total: number; rows: Row[]; scope?: { isAdmin: boolean; subtreeSize: number } }>('/api/employees/master?' + params.toString());
      setRows(data.rows);
      setTotal(data.total);
      setScope(data.scope ?? null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load employees');
    } finally {
      setLoading(false);
    }
  }, [search, designationId, active, sort, page, pageSize]);

  useEffect(() => { const t = setTimeout(load, search ? 350 : 0); return () => clearTimeout(t); }, [load]);
  useEffect(() => { setPage(1); }, [search, designationId, active, pageSize]);

  useEffect(() => {
    api
      .get<Option[]>('/api/employees/designations')
      .then(setDesignations)
      .catch(() => setDesignations([]));
    api
      .get<Option[]>('/api/employees/locations')
      .then(setLocations)
      .catch(() => setLocations([]));
  }, []);

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));

  const toggleStatus = async (r: Row) => {
    const verb = r.isActive ? 'deactivate' : 'activate';
    if (!confirm(`Are you sure you want to ${verb} "${r.employeeName || r.empCode}"?`)) return;
    try {
      await api.put(`/api/employees/${r.empCode}/status`, { isActive: !r.isActive });
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : `${verb[0].toUpperCase()}${verb.slice(1)} failed`);
    }
  };

  const remove = async (r: Row) => {
    if (!confirm(`Delete "${r.employeeName || r.empCode}" permanently? The record is removed from the list (soft-deleted in the database).`)) return;
    try {
      await api.delete(`/api/employees/${r.empCode}`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Employee Master</h1>
          <p className="page-sub">
            Employees from TBL_CIP_EMPLOYEE_MASTER
            <span className="scope-chip" title={isAdminUser() ? 'You can see the whole company' : 'You see yourself and everyone below you in the reporting hierarchy'}>
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
              placeholder="Search by name, code, or email..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={designationId} onChange={(e) => setDesignationId(e.target.value)} className="filter-select">
            <option value="">All Designations</option>
            {designations.map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
          <select value={active} onChange={(e) => setActive(e.target.value)} className="filter-select">
            <option value="">All</option>
            <option value="true">Active only</option>
            <option value="false">Inactive only</option>
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
                  <th className="sortable-th" onClick={() => toggleSort('code')}>
                    Code <SortArrow active={sort.key === 'code'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('name')}>
                    Employee Name <SortArrow active={sort.key === 'name'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('designation')}>
                    Designation <SortArrow active={sort.key === 'designation'} dir={sort.dir} />
                  </th>
                  <th>Email</th>
                  <th>Location</th>
                  <th className="sortable-th" onClick={() => toggleSort('doj')}>
                    DOJ <SortArrow active={sort.key === 'doj'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('status')}>
                    Status <SortArrow active={sort.key === 'status'} dir={sort.dir} />
                  </th>
                  <th style={{ width: 150 }}>Documents</th>
                  <th style={{ width: 120 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.empCode}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td className="mono">{r.employeeCodeText || r.empCode}</td>
                    <td>{r.employeeName}</td>
                    <td>{r.designation || '—'}</td>
                    <td>{r.email || '—'}</td>
                    <td>{r.location || '—'}</td>
                    <td>{r.doj || '—'}</td>
                    <td>
                      <span className={'badge ' + (r.isActive ? 'green' : 'gray')}>
                        {r.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td>
                      <div className="row-actions">
                        <button className="btn sm ghost" onClick={() => setDocsEmp({ code: r.empCode, name: r.employeeName || String(r.empCode) })}>
                          <Icon name="fileText" size={14} /> Documents
                        </button>
                        <button
                          className="btn sm ghost"
                          title="Generate offer letter PDF"
                          onClick={async () => {
                            try {
                              const blob = await api.download(`/api/employees/${r.empCode}/offer-letter`, { method: 'POST' });
                              const url = URL.createObjectURL(blob);
                              window.open(url, '_blank');
                              setTimeout(() => URL.revokeObjectURL(url), 60000);
                            } catch (e) {
                              setError(e instanceof ApiError ? e.message : 'Offer letter generation failed');
                            }
                          }}
                        >
                          <Icon name="mail" size={14} /> Offer Letter
                        </button>
                      </div>
                    </td>
                    <td>
                      <div className="row-actions">
                        <button className="icon-btn" title="Edit" onClick={() => setEditRow(r)}>
                          <Icon name="edit" size={16} />
                        </button>
                        <button
                          className="icon-btn"
                          title={r.isActive ? 'Deactivate' : 'Activate'}
                          onClick={() => toggleStatus(r)}
                        >
                          <Icon name={r.isActive ? 'lock' : 'check'} size={16} />
                        </button>
                        <button className="icon-btn danger" title="Delete" onClick={() => remove(r)}>
                          <Icon name="trash" size={16} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {rows.length === 0 && (
                  <tr>
                    <td colSpan={10} className="empty">No employees match the current search or filters.</td>
                  </tr>
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

      {docsEmp && (
        <DocumentsDrawer
          empCode={docsEmp.code}
          empName={docsEmp.name}
          onClose={() => setDocsEmp(null)}
        />
      )}

      {editRow && (
        <EditModal
          row={editRow}
          designations={designations}
          locations={locations}
          onClose={() => setEditRow(null)}
          onSaved={() => { setEditRow(null); load(); }}
        />
      )}
    </div>
  );
}

function EditModal({
  row, designations, locations, onClose, onSaved,
}: {
  row: Row; designations: Option[]; locations: Option[]; onClose: () => void; onSaved: () => void;
}) {
  const [employeeName, setEmployeeName] = useState(row.employeeName ?? '');
  const [designationId, setDesignationId] = useState(row.designationId ? String(row.designationId) : '');
  const [email, setEmail] = useState(row.email ?? '');
  const [locationId, setLocationId] = useState(row.locationId ? String(row.locationId) : '');
  // DOJ may arrive in any legacy format — prefill the date input when parseable;
  // if not parseable, keep the raw value on save so nothing is silently wiped.
  const parsedDoj = row.doj && !isNaN(new Date(row.doj).getTime()) ? new Date(row.doj).toISOString().slice(0, 10) : null;
  const [doj, setDoj] = useState(parsedDoj ?? '');
  const [contact, setContact] = useState(row.contact ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await api.put(`/api/employees/${row.empCode}`, {
        employeeName,
        designationId: designationId ? Number(designationId) : null,
        email,
        locationId: locationId ? Number(locationId) : null,
        doj: doj || (parsedDoj === null ? row.doj ?? '' : ''),
        contact,
      });
      onSaved();
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
          <h3>Edit — {row.employeeName || row.empCode} ({row.employeeCodeText || row.empCode})</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <label className="field">
            <span className="field-label">EMPLOYEE NAME *</span>
            <input value={employeeName} onChange={(e) => setEmployeeName(e.target.value)} required maxLength={100} />
          </label>
          <label className="field">
            <span className="field-label">DESIGNATION</span>
            <select value={designationId} onChange={(e) => setDesignationId(e.target.value)}>
              <option value="">— None —</option>
              {designations.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">EMAIL</span>
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={150} />
          </label>
          <label className="field">
            <span className="field-label">LOCATION</span>
            <select value={locationId} onChange={(e) => setLocationId(e.target.value)}>
              <option value="">— None —</option>
              {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">DATE OF JOINING</span>
            <input type="date" value={doj} onChange={(e) => setDoj(e.target.value)} />
          </label>
          <label className="field">
            <span className="field-label">CONTACT</span>
            <input value={contact} onChange={(e) => setContact(e.target.value)} maxLength={20} />
          </label>
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving}>{saving ? 'Saving…' : 'Save Changes'}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
