import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ApiError, isAdminUser } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
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
  lastWorkingDate: string | null;
}
interface Option { id: number; name: string }

/** Full record returned by GET /api/employees/{code}/detail for the edit modal.
 *  Note there is no `password` — the API never returns the stored value. */
interface Detail {
  empCode: number;
  empNo: number | null;
  employeeName: string | null;
  username: string | null;
  designationId: number | null;
  designation: string | null;
  roleId: number | null;
  roleName: string | null;
  locationId: number | null;
  location: string | null;
  email: string | null;
  doj: string | null;
  dob: string | null;
  salary: number | null;
  address: string | null;
  mobileNo1: string | null;
  mobileNo2: string | null;
  officeContact: string | null;
  bankAccountNo: string | null;
  ifscCode: string | null;
  bankName: string | null;
  bankAccName: string | null;
  panNo: string | null;
  aadharNo: string | null;
  employeeType: string | null;
  category: string | null;
  reportingManagerId: number | null;
  isActive: boolean;
}

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
  const [roles, setRoles] = useState<Option[]>([]);
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
    api
      .get<Option[]>('/api/employees/roles')
      .then(setRoles)
      .catch(() => setRoles([]));
  }, []);

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));

  const toggleStatus = async (r: Row) => {
    const verb = r.isActive ? 'Deactivate' : 'Activate';
    const name = r.employeeName || String(r.empCode);
    const ok = await confirmAction({
      title: `${verb} ${name}?`,
      text: r.isActive
        ? 'They will immediately lose access to every screen in the portal.'
        : 'They will be able to sign in again with their existing credentials.',
      confirmText: verb,
    });
    if (!ok) return;
    try {
      await api.put(`/api/employees/${r.empCode}/status`, { isActive: !r.isActive });
      toast.success(`${verb}d ${name}.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : `${verb} failed`);
    }
  };

  const remove = async (r: Row) => {
    const name = r.employeeName || String(r.empCode);
    const ok = await confirmAction({
      title: `Delete ${name}?`,
      text: 'The record is soft-deleted in the database and removed from the employee list.',
      confirmText: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await api.delete(`/api/employees/${r.empCode}`);
      toast.success(`Deleted ${name}.`);
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
                      {!r.isActive && r.lastWorkingDate && (
                        <div className="muted-note">Left {r.lastWorkingDate}</div>
                      )}
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
          roles={roles}
          onClose={() => setEditRow(null)}
          onSaved={() => { setEditRow(null); load(); }}
        />
      )}
    </div>
  );
}

/** Convert a legacy free-text date (DD/MM/YYYY, YYYY-MM-DD, ...) into the yyyy-mm-dd
 *  shape an <input type="date"> needs. Returns null when the value cannot be parsed. */
function toDateInput(v: string | null | undefined): string {
  if (!v) return '';
  const s = v.trim();
  const iso = s.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (iso) return `${iso[1]}-${iso[2]}-${iso[3]}`;
  const dmy = s.match(/^(\d{1,2})[\/-](\d{1,2})[\/-](\d{4})/);
  if (dmy) return `${dmy[3]}-${dmy[2].padStart(2, '0')}-${dmy[1].padStart(2, '0')}`;
  const parsed = new Date(s);
  return isNaN(parsed.getTime()) ? '' : parsed.toISOString().slice(0, 10);
}

/** Everything the modal edits. Held as one object so a partial save cannot drop a field. */
interface FormState {
  employeeName: string;
  empNo: string;
  username: string;
  password: string;
  designationId: string;
  roleId: string;
  locationId: string;
  doj: string;
  dob: string;
  salary: string;
  email: string;
  mobileNo1: string;
  mobileNo2: string;
  officeContact: string;
  address: string;
  bankAccountNo: string;
  ifscCode: string;
  bankName: string;
  bankAccName: string;
  panNo: string;
  aadharNo: string;
  employeeType: string;
  category: string;
}

function EditModal({
  row, designations, locations, roles, onClose, onSaved,
}: {
  row: Row; designations: Option[]; locations: Option[]; roles: Option[];
  onClose: () => void; onSaved: () => void;
}) {
  const [form, setForm] = useState<FormState | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  // The list query returns only the six summary columns, so the full record is fetched on
  // open. Until it lands the form renders nothing — editing a half-loaded record would
  // write NULLs over columns that were never displayed.
  useEffect(() => {
    let cancelled = false;
    setError('');
    setForm(null);
    api
      .get<Detail>(`/api/employees/${row.empCode}/detail`)
      .then((d) => {
        if (cancelled) return;
        setForm({
          employeeName: d.employeeName ?? '',
          empNo: d.empNo != null ? String(d.empNo) : '',
          username: d.username ?? '',
          password: '', // never prefilled — the API does not return it
          designationId: d.designationId ? String(d.designationId) : '',
          roleId: d.roleId ? String(d.roleId) : '',
          locationId: d.locationId ? String(d.locationId) : '',
          doj: toDateInput(d.doj),
          dob: toDateInput(d.dob),
          salary: d.salary != null ? String(d.salary) : '',
          email: d.email ?? '',
          mobileNo1: d.mobileNo1 ?? '',
          mobileNo2: d.mobileNo2 ?? '',
          officeContact: d.officeContact ?? '',
          address: d.address ?? '',
          bankAccountNo: d.bankAccountNo ?? '',
          ifscCode: d.ifscCode ?? '',
          bankName: d.bankName ?? '',
          bankAccName: d.bankAccName ?? '',
          panNo: d.panNo ?? '',
          aadharNo: d.aadharNo ?? '',
          employeeType: d.employeeType ?? '',
          category: d.category ?? '',
        });
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Failed to load employee details');
      });
    return () => { cancelled = true; };
  }, [row.empCode]);

  const set = (key: keyof FormState) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) =>
    setForm((f) => (f ? { ...f, [key]: e.target.value } : f));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!form) return;
    setSaving(true);
    setError('');
    try {
      await api.put(`/api/employees/${row.empCode}`, {
        employeeName: form.employeeName,
        empNo: form.empNo ? Number(form.empNo) : null,
        username: form.username,
        // Only sent when typed, so a blank box keeps the existing password.
        ...(form.password ? { password: form.password } : {}),
        designationId: form.designationId ? Number(form.designationId) : null,
        roleId: form.roleId ? Number(form.roleId) : null,
        locationId: form.locationId ? Number(form.locationId) : null,
        doj: form.doj,
        dob: form.dob,
        salary: form.salary ? Number(form.salary) : null,
        email: form.email,
        mobileNo1: form.mobileNo1,
        mobileNo2: form.mobileNo2,
        officeContact: form.officeContact,
        address: form.address,
        bankAccountNo: form.bankAccountNo,
        ifscCode: form.ifscCode,
        bankName: form.bankName,
        bankAccName: form.bankAccName,
        panNo: form.panNo,
        aadharNo: form.aadharNo,
        employeeType: form.employeeType,
        category: form.category,
      });
      onSaved();
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const title = `Edit — ${row.employeeName || row.empCode} (${row.employeeCodeText || row.empCode})`;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal wide" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{title}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>

        {!form ? (
          error ? <div className="alert error">{error}</div> : <div className="loading">Loading…</div>
        ) : (
          <form onSubmit={submit} className="emp-form-scroll">
            <div className="form-grid">
              <label className="field">
                <span className="field-label">EMPLOYEE NAME *</span>
                <input value={form.employeeName} onChange={set('employeeName')} required maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">EMP NO</span>
                <input type="number" value={form.empNo} onChange={set('empNo')} />
              </label>
              <label className="field">
                <span className="field-label">USERNAME</span>
                <input value={form.username} onChange={set('username')} maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">PASSWORD</span>
                <input
                  type="password"
                  value={form.password}
                  onChange={set('password')}
                  placeholder="Leave blank to keep current"
                  autoComplete="new-password"
                />
              </label>
              <label className="field">
                <span className="field-label">DESIGNATION</span>
                <select value={form.designationId} onChange={set('designationId')}>
                  <option value="">— None —</option>
                  {designations.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </label>
              <label className="field">
                <span className="field-label">ROLE</span>
                <select value={form.roleId} onChange={set('roleId')}>
                  <option value="">— None —</option>
                  {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                </select>
              </label>
              <label className="field">
                <span className="field-label">LOCATION</span>
                <select value={form.locationId} onChange={set('locationId')}>
                  <option value="">— None —</option>
                  {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                </select>
              </label>
              <label className="field">
                <span className="field-label">SALARY</span>
                <input type="number" step="0.01" min="0" value={form.salary} onChange={set('salary')} />
              </label>
              <label className="field">
                <span className="field-label">DATE OF JOINING</span>
                <input type="date" value={form.doj} onChange={set('doj')} />
              </label>
              <label className="field">
                <span className="field-label">DATE OF BIRTH</span>
                <input type="date" value={form.dob} onChange={set('dob')} />
              </label>
              <label className="field">
                <span className="field-label">EMAIL</span>
                <input type="email" value={form.email} onChange={set('email')} maxLength={50} />
              </label>
              <label className="field">
                <span className="field-label">MOBILE NO 1</span>
                <input value={form.mobileNo1} onChange={set('mobileNo1')} maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">MOBILE NO 2</span>
                <input value={form.mobileNo2} onChange={set('mobileNo2')} maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">OFFICE CONTACT</span>
                <input value={form.officeContact} onChange={set('officeContact')} maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">EMPLOYEE TYPE</span>
                <input value={form.employeeType} onChange={set('employeeType')} maxLength={10} />
              </label>
              <label className="field">
                <span className="field-label">CATEGORY</span>
                <input value={form.category} onChange={set('category')} maxLength={50} />
              </label>
              <label className="field">
                <span className="field-label">BANK ACCOUNT NO</span>
                <input value={form.bankAccountNo} onChange={set('bankAccountNo')} maxLength={100} />
              </label>
              <label className="field">
                <span className="field-label">IFSC CODE</span>
                <input value={form.ifscCode} onChange={set('ifscCode')} maxLength={20} />
              </label>
              <label className="field">
                <span className="field-label">BANK NAME</span>
                <input value={form.bankName} onChange={set('bankName')} maxLength={50} />
              </label>
              <label className="field">
                <span className="field-label">BANK ACCOUNT NAME</span>
                <input value={form.bankAccName} onChange={set('bankAccName')} maxLength={50} />
              </label>
              <label className="field">
                <span className="field-label">PAN NO</span>
                <input value={form.panNo} onChange={set('panNo')} maxLength={10} placeholder="ABCDE1234F" />
              </label>
              <label className="field">
                <span className="field-label">AADHAR NO</span>
                <input value={form.aadharNo} onChange={set('aadharNo')} maxLength={12} placeholder="12 digits" />
              </label>
              <label className="field span-2">
                <span className="field-label">ADDRESS</span>
                <textarea value={form.address} onChange={set('address')} maxLength={150} rows={2} />
              </label>
            </div>

            {error && <div className="alert error">{error}</div>}
            <div className="modal-actions">
              <button type="button" className="btn" onClick={onClose}>Cancel</button>
              <button className="btn primary" disabled={saving}>{saving ? 'Saving…' : 'Save Changes'}</button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
