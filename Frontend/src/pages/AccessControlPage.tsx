import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
import { Pager } from '../components/tableView';

interface AccessRow {
  empCode: number;
  empName: string;
  username: string;
  roleId: number | null;
  roleName: string;
  hasAccess: boolean;
  accessSource: string;
  grantedByName: string | null;
  grantedAt: string | null;
}
interface RoleOption { roleId: number; roleName: string }

export default function AccessControlPage() {
  const [rows, setRows] = useState<AccessRow[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [search, setSearch] = useState('');
  const [roleId, setRoleId] = useState('');
  const [access, setAccess] = useState('');
  const [roles, setRoles] = useState<RoleOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busyCode, setBusyCode] = useState<number | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (roleId) params.set('roleId', roleId);
      if (access) params.set('access', access);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const data = await api.get<{ total: number; rows: AccessRow[] }>('/api/access-control?' + params.toString());
      setRows(data.rows);
      setTotal(data.total);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load access list');
    } finally {
      setLoading(false);
    }
  }, [search, roleId, access, page, pageSize]);

  useEffect(() => { const t = setTimeout(load, search ? 350 : 0); return () => clearTimeout(t); }, [load]);
  useEffect(() => { setPage(1); }, [search, roleId, access, pageSize]);

  useEffect(() => {
    api
      .get<{ rows: RoleOption[] }>('/api/roles')
      .then((d) => setRoles(d.rows ?? []))
      .catch(() => setRoles([]));
  }, []);

  const setAccessFlag = async (r: AccessRow, hasAccess: boolean) => {
    const verb = hasAccess ? 'Grant' : 'Revoke';
    const name = r.empName || String(r.empCode);
    const ok = await confirmAction({
      title: `${verb} website access?`,
      text: `${name} will ${hasAccess ? 'be able to sign in to the portal' : 'immediately lose access to the portal'}.`,
      confirmText: verb,
      tone: hasAccess ? 'primary' : 'danger',
    });
    if (!ok) return;
    setBusyCode(r.empCode);
    try {
      await api.put(`/api/access-control/${r.empCode}`, { hasAccess });
      toast.success(`${verb === 'Grant' ? 'Granted' : 'Revoked'} access for ${name}.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : `${verb} failed`);
    } finally {
      setBusyCode(null);
    }
  };

  const clearOverride = async (r: AccessRow) => {
    const name = r.empName || String(r.empCode);
    const ok = await confirmAction({
      title: `Remove the override for ${name}?`,
      text: 'The role default will apply to this employee again.',
      confirmText: 'Remove override',
      tone: 'danger',
    });
    if (!ok) return;
    setBusyCode(r.empCode);
    try {
      await api.delete(`/api/access-control/${r.empCode}`);
      toast.success(`Override removed — role default applies for ${name}.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to remove override');
    } finally {
      setBusyCode(null);
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Access Control</h1>
          <p className="page-sub">
            Decide which employees can sign in to the website. Admin &amp; TL roles have access by default;
            everyone else needs an explicit grant.
          </p>
        </div>
      </div>

      {error && <div className="alert error">{error}</div>}

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name, username, code, or email..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={roleId} onChange={(e) => setRoleId(e.target.value)} className="filter-select">
            <option value="">All Roles</option>
            {roles.filter((r) => (r.roleName || '').toUpperCase() !== 'CONNECTOR').map((r) => (
              <option key={r.roleId} value={r.roleId}>{r.roleName}</option>
            ))}
          </select>
          <select value={access} onChange={(e) => setAccess(e.target.value)} className="filter-select">
            <option value="">All Access States</option>
            <option value="granted">Has access</option>
            <option value="denied">No access</option>
          </select>
        </div>

        {loading ? (
          <div className="loading">Loading…</div>
        ) : error && rows.length === 0 ? (
          <div className="alert error">{error}</div>
        ) : (
          <div className="table-scroll">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Code</th>
                  <th>Employee</th>
                  <th>Username</th>
                  <th>Role</th>
                  <th>Access</th>
                  <th>Source</th>
                  <th style={{ width: 200 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.empCode}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td className="mono">{r.empCode}</td>
                    <td>{r.empName || '—'}</td>
                    <td>{r.username || '—'}</td>
                    <td>{r.roleName || '—'}</td>
                    <td>
                      <span className={'badge ' + (r.hasAccess ? 'green' : 'gray')}>
                        {r.hasAccess ? 'Has Access' : 'No Access'}
                        </span>
                    </td>
                    <td>
                      {r.accessSource === 'Granted' && <span className="badge blue">{r.accessSource}</span>}
                      {r.accessSource === 'Denied' && <span className="badge pending">{r.accessSource}</span>}
                      {r.accessSource !== 'Granted' && r.accessSource !== 'Denied' && (
                        <span className="badge gray">{r.accessSource}</span>
                      )}
                      {r.accessSource === 'Not granted' && r.grantedByName && (
                        <div className="muted-note">
                          by {r.grantedByName}
                          {r.grantedAt ? ` · ${new Date(r.grantedAt).toLocaleDateString()}` : ''}
                        </div>
                      )}
                    </td>
                    <td>
                      <div className="row-actions">
                        {!r.hasAccess ? (
                          <button
                            className="btn sm ghost"
                            disabled={busyCode === r.empCode}
                            onClick={() => setAccessFlag(r, true)}
                          >
                            <Icon name="check" size={14} /> Grant
                          </button>
                        ) : (
                          <button
                            className="btn sm ghost"
                            disabled={busyCode === r.empCode}
                            onClick={() => setAccessFlag(r, false)}
                          >
                            <Icon name="lock" size={14} /> Revoke
                          </button>
                        )}
                        {(r.accessSource === 'Granted' || r.accessSource === 'Denied') && (
                          <button
                            className="btn sm ghost"
                            disabled={busyCode === r.empCode}
                            onClick={() => clearOverride(r)}
                            title="Remove override — role default applies again"
                          >
                            <Icon name="x" size={14} /> Reset
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
                {rows.length === 0 && !loading && (
                  <tr><td colSpan={8} className="empty">No employees match the current search or filters.</td></tr>
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
    </div>
  );
}
