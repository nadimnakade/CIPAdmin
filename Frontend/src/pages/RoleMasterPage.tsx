import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
import { Pager } from '../components/tableView';

interface RoleRow {
  roleId: number;
  roleName: string;
  isActive: boolean;
  employeeCount: number;
}
interface MenuNode {
  menuId: number;
  label: string | null;
  path: string | null;
  parentId: number | null;
  isActive: boolean;
  children?: MenuNode[];
}
type Grants = Record<string, number[]>;

/** Flatten menu rows to display order with depth + full-path labels. */
interface FlatMenu { menuId: number; label: string; depth: number; isGroup: boolean }
const flatten = (nodes: MenuNode[], depth = 0, prefix = ''): FlatMenu[] =>
  nodes.flatMap((n) => {
    const label = prefix ? `${prefix} › ${n.label}` : n.label || '';
    const row: FlatMenu = { menuId: n.menuId, label, depth, isGroup: n.path == null };
    return [row, ...flatten(n.children || [], depth + 1, label)];
  });

/** One employee row in the employee-wise privilege list. */
interface EmpPrivRow {
  empCode: number;
  empName: string | null;
  roleId: number | null;
  roleName: string;
  menuCount: number;
}
/** One menu row in the editable employee-privilege matrix. */
interface EmpPrivMenu {
  menuId: number;
  label: string | null;
  path: string | null;
  parentId: number | null;
  roleGranted: boolean;
  overrideGranted: boolean | null;
  granted: boolean;
}

export default function RoleMasterPage() {
  const [tab, setTab] = useState<'roles' | 'employees'>('roles');
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [menus, setMenus] = useState<MenuNode[]>([]);
  const [grants, setGrants] = useState<Grants>({});
  const [search, setSearch] = useState('');
  const [activeFilter, setActiveFilter] = useState('');
  const [editRole, setEditRole] = useState<RoleRow | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  const [privRole, setPrivRole] = useState<RoleRow | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (activeFilter) params.set('active', activeFilter);
      const data = await api.get<{ rows: RoleRow[] }>('/api/roles?' + params.toString());
      setRoles(data.rows);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load roles');
    } finally {
      setLoading(false);
    }
  }, [search, activeFilter]);

  useEffect(() => { const t = setTimeout(load, search ? 300 : 0); return () => clearTimeout(t); }, [load]);

  const loadPrivileges = useCallback(() => {
    api
      .get<{ menus: MenuNode[]; grants: Grants }>('/api/roles/privileges')
      .then((d) => { setMenus(d.menus); setGrants(d.grants ?? {}); })
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Failed to load privileges'));
  }, []);

  useEffect(() => { loadPrivileges(); }, [loadPrivileges]);

  const toggleStatus = async (r: RoleRow) => {
    const verb = r.isActive ? 'Deactivate' : 'Activate';
    const ok = await confirmAction({
      title: `${verb} role "${r.roleName}"?`,
      text: r.isActive
        ? `Members of this role (${r.employeeCount} employee${r.employeeCount === 1 ? '' : 's'}) lose the menus and privileges it grants.`
        : 'Members of this role regain the menus and privileges it grants.',
      confirmText: verb,
    });
    if (!ok) return;
    try {
      await api.put(`/api/roles/${r.roleId}/status`, { isActive: !r.isActive });
      toast.success(`Role "${r.roleName}" ${r.isActive ? 'deactivated' : 'activated'}.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : `${verb} failed`);
    }
  };

  const remove = async (r: RoleRow) => {
    const ok = await confirmAction({
      title: `Delete role "${r.roleName}"?`,
      text: 'This also removes every privilege grant attached to the role.',
      confirmText: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await api.delete('/api/roles/' + r.roleId);
      toast.success(`Role "${r.roleName}" deleted.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  // Build the tree from the flat list once.
  const menuTree = useMemo(() => {
    const byId = new Map<number, MenuNode>();
    menus.forEach((m) => byId.set(m.menuId, { ...m, children: [] }));
    const roots: MenuNode[] = [];
    menus.forEach((m) => {
      const node = byId.get(m.menuId)!;
      if (m.parentId != null && byId.has(m.parentId)) byId.get(m.parentId)!.children!.push(node);
      else roots.push(node);
    });
    return roots;
  }, [menus]);

  const flatMenus = useMemo(() => flatten(menuTree), [menuTree]);

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Role &amp; Privilege Master</h1>
          <p className="page-sub">Manage roles, the sidebar menus each role can see, and per-employee privileges</p>
        </div>
        {tab === 'roles' && (
          <button className="btn primary" onClick={() => setShowAdd(true)}>
            <Icon name="plus" /> Add Role
          </button>
        )}
      </div>

      <div className="subtabs">
        <button className={'subtab' + (tab === 'roles' ? ' active' : '')} onClick={() => setTab('roles')}>
          Role-wise
        </button>
        <button className={'subtab' + (tab === 'employees' ? ' active' : '')} onClick={() => setTab('employees')}>
          Employee-wise
        </button>
      </div>

      

      {tab === 'employees' ? (
        <EmployeePrivilegesTab />
      ) : (
      <>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by role name..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={activeFilter} onChange={(e) => setActiveFilter(e.target.value)} className="filter-select">
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
                  <th>Role ID</th>
                  <th>Role Name</th>
                  <th>Status</th>
                  <th>Employees</th>
                  <th style={{ width: 150 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {roles.map((r, i) => (
                  <tr key={r.roleId} style={{ opacity: r.isActive ? 1 : 0.55 }}>
                    <td>{i + 1}</td>
                    <td className="mono">{r.roleId}</td>
                    <td>{r.roleName}</td>
                    <td>
                      <span className={'badge ' + (r.isActive ? 'green' : 'gray')}>
                        {r.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td>{r.employeeCount.toLocaleString()}</td>
                    <td>
                      <div className="row-actions">
                        <button className="btn sm ghost" onClick={() => setPrivRole(r)}>
                          <Icon name="shield" size={14} /> Privileges
                        </button>
                        <button className="icon-btn" title="Edit" onClick={() => setEditRole(r)}>
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
                {roles.length === 0 && (
                  <tr><td colSpan={6} className="empty">No roles match the filters.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {(showAdd || editRole) && (
        <RoleModal
          role={editRole}
          onClose={() => { setShowAdd(false); setEditRole(null); }}
          onSaved={(msg) => { setShowAdd(false); setEditRole(null); toast.success(msg); load(); loadPrivileges(); }}
        />
      )}

      {privRole && (
        <PrivilegeModal
          role={privRole}
          menus={flatMenus}
          granted={new Set(grants[String(privRole.roleId)] ?? [])}
          onClose={() => setPrivRole(null)}
          onSaved={() => { setPrivRole(null); loadPrivileges(); }}
        />
      )}
      </>
      )}
    </div>
  );
}

/* ── Employee-wise privileges tab ─────────────────────────────────── */

function EmployeePrivilegesTab() {
  const [rows, setRows] = useState<EmpPrivRow[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(50);
  const [search, setSearch] = useState('');
  const [roleId, setRoleId] = useState('');
  const [roles, setRoles] = useState<{ roleId: number; roleName: string }[]>([]);
  const [detail, setDetail] = useState<EmpPrivRow | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (roleId) params.set('roleId', roleId);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const data = await api.get<{ total: number; rows: EmpPrivRow[] }>('/api/roles/employee-privileges?' + params.toString());
      setRows(data.rows);
      setTotal(data.total);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load employee privileges');
    } finally {
      setLoading(false);
    }
  }, [search, roleId, page, pageSize]);

  useEffect(() => { const t = setTimeout(load, search ? 300 : 0); return () => clearTimeout(t); }, [load]);
  useEffect(() => { setPage(1); }, [search, roleId]);

  useEffect(() => {
    api.get<{ rows: { roleId: number; roleName: string }[] }>('/api/roles')
      .then((d) => setRoles(d.rows.filter((r) => r.roleId !== 2010)))
      .catch(() => setRoles([]));
  }, []);

  return (
    <div className="card">
      
      <div className="table-toolbar">
        <div className="search-box">
          <Icon name="search" />
          <input
            placeholder="Search by employee name or code..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <select value={roleId} onChange={(e) => setRoleId(e.target.value)} className="filter-select">
          <option value="">All roles</option>
          {roles.map((r) => (
            <option key={r.roleId} value={r.roleId}>{r.roleName}</option>
          ))}
        </select>
      </div>

      {loading ? (
        <div className="loading">Loading…</div>
      ) : error ? (
        <div className="alert error">{error}</div>
      ) : (
        <>
          <div className="table-scroll">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Emp Code</th>
                  <th>Employee Name</th>
                  <th>Role</th>
                  <th>Menus Granted</th>
                  <th style={{ width: 120 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.empCode}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td className="mono">{r.empCode}</td>
                    <td>{r.empName}</td>
                    <td><span className="badge blue">{r.roleName}</span></td>
                    <td>{r.menuCount}</td>
                    <td>
                      <div className="row-actions">
                        <button className="btn sm ghost" onClick={() => setDetail(r)}>
                          <Icon name="shield" size={14} /> View Privileges
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {rows.length === 0 && (
                  <tr><td colSpan={6} className="empty">No employees match. (Connectors are excluded.)</td></tr>
                )}
              </tbody>
            </table>
          </div>
          <Pager
            page={page}
            totalPages={Math.max(1, Math.ceil(total / pageSize))}
            totalRows={total}
            pageSize={pageSize}
            onPage={setPage}
            onPageSize={() => {}}
          />
        </>
      )}

      {detail && (
        <EmployeePrivilegeModal
          emp={detail}
          onClose={() => setDetail(null)}
          onSaved={(msg) => { toast.success(msg); load(); }}
        />
      )}
    </div>
  );
}

/** Editable matrix of one employee's effective menu privileges.
 *  Shows every active menu: checked = effectively granted (role grant + per-employee
 *  override). Saving diffs against the role default on the server. */
function EmployeePrivilegeModal({ emp, onClose, onSaved }: { emp: EmpPrivRow; onClose: () => void; onSaved: (msg: string) => void }) {
  const [menus, setMenus] = useState<EmpPrivMenu[] | null>(null);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    api
      .get<{ menus: EmpPrivMenu[] }>(`/api/roles/employee-privileges/${emp.empCode}`)
      .then((d) => setMenus(d.menus))
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Failed to load privileges'));
  }, [emp.empCode]);

  const toggle = (menuId: number) =>
    setMenus((ms) => ms && ms.map((m) => (m.menuId === menuId ? { ...m, granted: !m.granted } : m)));

  const save = async () => {
    if (!menus) return;
    setSaving(true);
    setError('');
    try {
      const grantedIds = menus.filter((m) => m.granted).map((m) => m.menuId);
      await api.put(`/api/roles/employee-privileges/${emp.empCode}`, { menuIds: grantedIds });
      onSaved(`Privileges updated for ${emp.empName || emp.empCode}. They see the new menus on next sign-in or reload.`);
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>Privileges — {emp.empName} ({emp.roleName})</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <p className="modal-sub">
          Checked menus are what this employee can open. Unchecked extras override their role default;
          changes apply the next time they load the app.
        </p>
        {error && <div className="alert error">{error}</div>}
        {menus === null && !error && <div className="loading">Loading…</div>}
        {menus !== null && (
          <div className="menu-matrix-scroll">
            <table className="menu-matrix">
              <tbody>
                {menus.map((m) => (
                  <tr key={m.menuId}>
                    <td>
                      <label className="menu-matrix-row">
                        <input type="checkbox" checked={m.granted} onChange={() => toggle(m.menuId)} />
                        <span style={{ fontWeight: m.path == null ? 600 : 400 }}>
                          {m.label}
                        </span>
                        {m.roleGranted && !m.granted && <em className="override-note">removed from role default</em>}
                        {!m.roleGranted && m.granted && <em className="override-note granted">extra grant</em>}
                      </label>
                    </td>
                  </tr>
                ))}
                {menus.length === 0 && (
                  <tr><td className="empty">No menus defined in the menu master.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={save} disabled={saving || menus === null}>
            {saving ? 'Saving…' : 'Save Privileges'}
          </button>
        </div>
      </div>
    </div>
  );
}

function RoleModal({
  role, onClose, onSaved,
}: {
  role: RoleRow | null; onClose: () => void; onSaved: (msg: string) => void;
}) {
  const [name, setName] = useState(role?.roleName ?? '');
  const [isActive, setIsActive] = useState(role?.isActive ?? true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      if (role) {
        await api.put(`/api/roles/${role.roleId}`, { roleName: name, isActive });
        onSaved(`Role "${name}" updated.`);
      } else {
        await api.post('/api/roles', { roleName: name, isActive });
        onSaved(`Role "${name}" created.`);
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
          <h3>{role ? `Edit — ${role.roleName}` : 'Add Role'}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <label className="field">
            <span className="field-label">ROLE NAME *</span>
            <input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          </label>
          <label className="check">
            <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
            Active (visible to assign to employees)
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

function PrivilegeModal({
  role, menus, granted, onClose, onSaved,
}: {
  role: RoleRow; menus: FlatMenu[]; granted: Set<number>; onClose: () => void; onSaved: () => void;
}) {
  const [selected, setSelected] = useState<Set<number>>(new Set(granted));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const toggle = (menuId: number) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(menuId)) next.delete(menuId);
      else next.add(menuId);
      return next;
    });

  const allOn = menus.length > 0 && menus.every((m) => selected.has(m.menuId));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await api.put(`/api/roles/${role.roleId}/privileges`, { menuIds: [...selected] });
      onSaved();
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Failed to save privileges');
    } finally {
      setSaving(false);
    }
  };

  const isAdminRole = role.roleId === 1 || role.roleId === 2;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>Privileges — {role.roleName}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <div className="menu-matrix-toolbar">
            <label className="check">
              <input
                type="checkbox"
                checked={allOn}
                onChange={(e) => setSelected(e.target.checked ? new Set(menus.map((m) => m.menuId)) : new Set())}
              />
              Select all
            </label>
            {isAdminRole && <span className="scope-chip">Built-in admin role — cannot be emptied</span>}
          </div>
          <div className="menu-matrix-scroll">
            <table className="menu-matrix">
              <tbody>
                {menus.map((m) => (
                  <tr key={m.menuId}>
                    <td>
                      <label className="menu-matrix-row" style={{ paddingLeft: m.depth * 18 }}>
                        <input
                          type="checkbox"
                          checked={selected.has(m.menuId)}
                          onChange={() => toggle(m.menuId)}
                        />
                        <span style={{ fontWeight: m.isGroup ? 600 : 400 }}>{m.label}</span>
                      </label>
                    </td>
                  </tr>
                ))}
                {menus.length === 0 && (
                  <tr><td className="empty">No menus defined.</td></tr>
                )}
              </tbody>
            </table>
          </div>
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving}>{saving ? 'Saving…' : 'Save Privileges'}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
