import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';

interface AuthRow {
  empCode: number;
  username: string;
  empName: string;
  roleId: number;
  roleName: string;
  isBypass: boolean;
  isEnrolled: boolean;
  locationName: string | null;
}

/**
 * Admin view over the Google Authenticator second factor.
 *
 * The rule this screen manages is the legacy one: IS_BYPASS = 1 means the
 * authenticator is skipped and the user lands on the dashboard; IS_BYPASS = 0
 * means the authenticator is mandatory and the user must have enrolled.
 *
 * "Reset" clears the stored secret so the user re-scans a QR at next login - the
 * lost-phone path. It deliberately does NOT touch IS_BYPASS, because flipping
 * that would silently switch the second factor off, which is a different decision.
 */
export default function AuthenticatorAdminPage() {
  const [rows, setRows] = useState<AuthRow[]>([]);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busyCode, setBusyCode] = useState<number | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search.trim()) params.set('q', search.trim());
      params.set('take', '100');
      const data = await api.get<AuthRow[]>('/api/auth/admin/auth-status?' + params.toString());
      setRows(data);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not load authenticator status');
    } finally {
      setLoading(false);
    }
  }, [search]);

  useEffect(() => {
    load();
  }, [load]);

  // Lost phone: wipe the secret so the next login shows a fresh QR code.
  const resetAuth = async (row: AuthRow) => {
    const ok = await confirmAction({
      title: 'Reset authenticator?',
      text: `Clear the stored authenticator for ${row.empName} (${row.username}). They will have to scan a new QR code at their next login.`,
      confirmText: 'Reset',
      tone: 'danger',
    });
    if (!ok) return;

    setBusyCode(row.empCode);
    try {
      const res = await api.post<{ message: string }>('/api/auth/admin/reset-otp', {
        empCode: row.empCode,
        clearSecret: true,
      });
      toast.success(res.message || 'Authenticator reset');
      load();
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Reset failed');
    } finally {
      setBusyCode(null);
    }
  };

  // Turn the second factor on (0) or off (1) for one employee.
  const setBypass = async (row: AuthRow, nextBypass: boolean) => {
    const ok = await confirmAction({
      title: nextBypass ? 'Stop requiring the authenticator?' : 'Require the authenticator?',
      text: nextBypass
        ? `${row.empName} will sign in with username and password only.`
        : `${row.empName} will have to scan a QR code and enter a 6-digit code at every login.`,
      confirmText: nextBypass ? 'Stop requiring' : 'Require it',
      tone: nextBypass ? 'danger' : 'primary',
    });
    if (!ok) return;

    setBusyCode(row.empCode);
    try {
      await api.post<{ message: string }>('/api/auth/admin/reset-otp', {
        empCode: row.empCode,
        clearSecret: false,
        setBypass: nextBypass ? 1 : 0,
      });
      toast.success(nextBypass ? 'Authenticator disabled' : 'Authenticator enabled');
      load();
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Update failed');
    } finally {
      setBusyCode(null);
    }
  };

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1 className="page-title">Authenticator Setup</h1>
          <p className="page-sub">
            Google Authenticator is required when <b>Authenticator = off</b> (IS_BYPASS = 0).
            Users who have never enrolled are shown a QR code at login.
          </p>
        </div>
      </header>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" size={15} />
            <input
              placeholder="Search name, username or code"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              aria-label="Search employees"
            />
          </div>
          <button className="btn sm ghost" onClick={load} disabled={loading}>
            Refresh
          </button>
        </div>

        {error && (
          <div className="alert error" role="alert">
            <span className="alert-ico"><Icon name="alert" size={15} /></span>
            <span>{error}</span>
          </div>
        )}

        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>Employee</th>
                <th>Role</th>
                <th>Location</th>
                <th>Authenticator</th>
                <th>Enrolled</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <tr><td colSpan={6} className="loading">Loading…</td></tr>
              )}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={6} className="empty">No employees matched.</td></tr>
              )}
              {rows.map((r) => (
                <tr key={r.empCode}>
                  <td>
                    <div>{r.empName}</div>
                    <div className="muted-note">{r.username} · {r.empCode}</div>
                  </td>
                  <td>{r.roleName || '—'}</td>
                  <td>{r.locationName || '—'}</td>
                  <td>
                    <span className={`badge ${r.isBypass ? 'gray' : 'blue'}`}>
                      {r.isBypass ? 'Not required' : 'Required'}
                    </span>
                  </td>
                  <td>
                    {r.isBypass ? (
                      <span className="muted-note">n/a</span>
                    ) : r.isEnrolled ? (
                      <span className="badge green">Yes</span>
                    ) : (
                      <span className="badge pending">Not yet</span>
                    )}
                  </td>
                  <td>
                    <div className="row-actions">
                      {r.isEnrolled && (
                        <button
                          className="btn sm ghost"
                          disabled={busyCode === r.empCode}
                          onClick={() => resetAuth(r)}
                        >
                          Reset
                        </button>
                      )}
                      <button
                        className="btn sm ghost"
                        disabled={busyCode === r.empCode}
                        onClick={() => setBypass(r, !r.isBypass)}
                      >
                        {r.isBypass ? 'Require it' : 'Stop requiring'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="table-foot">
          <span className="muted-note">Showing up to 100 rows — search to narrow it down.</span>
        </div>
      </div>
    </div>
  );
}
