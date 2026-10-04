import { useEffect, useState } from 'react';
import { api, ApiError, getUser } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
import { Pager, SortArrow, useTableView } from '../components/tableView';

interface EmpLimitRow {
  empCode: number;
  empName: string | null;
  roleId: number;
  roleName: string | null;
  monthlyLimit: number | null;
  effectiveLimit: number;
  setOn: string | null;
}

export default function SettingsPage() {
  const user = getUser();
  const isAdmin = user?.roleId === 1 || user?.roleId === 2;

  const [limit, setLimit] = useState<string>('');
  const [savedLimit, setSavedLimit] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);

  // Per-employee limits
  const [empSearch, setEmpSearch] = useState('');
  const [empRows, setEmpRows] = useState<EmpLimitRow[]>([]);
  const [empLoading, setEmpLoading] = useState(false);
  const [empEdits, setEmpEdits] = useState<Record<number, string>>({});
  const [empMsg, setEmpMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [empBusy, setEmpBusy] = useState<number | null>(null);

  // Pagination over the (client-side sorted) employee limit rows
  const empTable = useTableView<EmpLimitRow, 'empName' | 'roleId' | 'monthlyLimit' | 'empCode'>(
    empRows,
    'empName',
    (r, k) => k === 'monthlyLimit' ? (r.monthlyLimit ?? 0) : (r as any)[k]
  );

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    api
      .get<{ rows: { key: string; value: string }[] }>('/api/settings')
      .then((res) => {
        const row = res.rows.find((r) => r.key === 'Cibil.MonthlyPullLimit');
        if (row) {
          setLimit(row.value);
          setSavedLimit(Number(row.value));
        }
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [isAdmin]);

  const loadEmpLimits = (search: string) => {
    setEmpLoading(true);
    api
      .get<{ rows: EmpLimitRow[] }>('/api/cibil/employee-limits' + (search ? `?search=${encodeURIComponent(search)}` : ''))
      .then((res) => setEmpRows((res.rows || []).filter((r) => (r.roleName || '').toUpperCase() !== 'CONNECTOR')))
      .catch(() => setEmpRows([]))
      .finally(() => setEmpLoading(false));
  };

  useEffect(() => {
    if (!isAdmin) return;
    const t = setTimeout(() => loadEmpLimits(empSearch), 300);
    return () => clearTimeout(t);
  }, [empSearch, isAdmin]);

  const saveEmpLimit = async (empCode: number) => {
    const raw = empEdits[empCode];
    if (raw === undefined || raw === '') return;
    const n = Number(raw);
    if (!Number.isInteger(n) || n < 1 || n > 100000) {
      setEmpMsg({ kind: 'err', text: 'Limit must be a whole number between 1 and 100000.' });
      return;
    }
    setEmpBusy(empCode);
    setEmpMsg(null);
    try {
      await api.put(`/api/cibil/employee-limits/${empCode}`, { monthlyLimit: n });
      setEmpMsg({ kind: 'ok', text: `Limit ${n} saved for employee ${empCode}. This replaces their default outright — it can be lower as well as higher.` });
      setEmpEdits((e) => { const c = { ...e }; delete c[empCode]; return c; });
      loadEmpLimits(empSearch);
    } catch (err) {
      setEmpMsg({ kind: 'err', text: err instanceof ApiError ? err.message : 'Save failed.' });
    } finally {
      setEmpBusy(null);
    }
  };

  const clearEmpLimit = async (empCode: number) => {
    setEmpBusy(empCode);
    setEmpMsg(null);
    try {
      await api.delete(`/api/cibil/employee-limits/${empCode}`);
      setEmpMsg({ kind: 'ok', text: `Employee ${empCode} is back on the default limit.` });
      loadEmpLimits(empSearch);
    } catch (err) {
      setEmpMsg({ kind: 'err', text: err instanceof ApiError ? err.message : 'Failed to clear.' });
    } finally {
      setEmpBusy(null);
    }
  };

  const save = async () => {
    const n = Number(limit);
    if (!Number.isInteger(n) || n < 1 || n > 100000) {
      setMessage({ kind: 'err', text: 'Enter a whole number between 1 and 100000.' });
      return;
    }
    setSaving(true);
    setMessage(null);
    try {
      await api.put('/api/settings/monthly-pull-limit', { monthlyPullLimit: n });
      setSavedLimit(n);
      setMessage({ kind: 'ok', text: `Default monthly pull limit set to ${n}. It applies immediately — affected employees see the new number the next time they load the CIBIL page.` });
    } catch (err) {
      setMessage({
        kind: 'err',
        text: err instanceof ApiError
          ? err.message
          : 'Save failed — the server could not write to TBL_CIP_APP_SETTINGS. Run the CREATE TABLE script for TBL_CIP_APP_SETTINGS on the server database, then retry.',
      });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="page page-narrow">
      <div className="page-head">
        <div>
          <h1>Settings</h1>
          <p className="page-sub">Application configuration</p>
        </div>
      </div>

      {isAdmin && (
        <>
          <div className="card form-card">
            <label className="field">
              <span className="field-label">DEFAULT MONTHLY CIBIL PULL LIMIT</span>
              <input
                type="number"
                min={1}
                max={100000}
                placeholder="e.g. 50"
                value={limit}
                onChange={(e) => setLimit(e.target.value)}
                disabled={loading || saving}
              />
            </label>
            <p className="page-sub" style={{ margin: '0 0 12px' }}>
              Applies to every non-admin user unless an employee-specific limit (below) or an approved
              limit request overrides it.
              {savedLimit !== null && <b> Current value: {savedLimit}.</b>}
            </p>
            {message && <div className={`alert ${message.kind}`}>{message.text}</div>}
            <button className="btn primary" onClick={save} disabled={loading || saving}>
              <Icon name="check" /> {saving ? 'Saving…' : 'Save limit'}
            </button>
          </div>

          <div className="card form-card">
            <div className="card-head">
              <h3>Employee-wise monthly pull limits</h3>
            </div>
            <label className="field">
              <span className="field-label">SEARCH EMPLOYEE</span>
              <input
                placeholder="Name or employee code…"
                value={empSearch}
                onChange={(e) => setEmpSearch(e.target.value)}
              />
            </label>
            {empMsg && <div className={`alert ${empMsg.kind}`}>{empMsg.text}</div>}
            {empLoading ? (
              <p className="page-sub">Loading…</p>
            ) : empRows.length === 0 ? (
              <p className="page-sub">No employees match.</p>
            ) : (
              <table className="data-table">
                <thead>
                  <tr>
                    <th className="sortable-th" onClick={() => empTable.toggleSort('empCode')}>Code<SortArrow active={empTable.sort.key === 'empCode'} dir={empTable.sort.dir} /></th>
                    <th className="sortable-th" onClick={() => empTable.toggleSort('empName')}>Name<SortArrow active={empTable.sort.key === 'empName'} dir={empTable.sort.dir} /></th>
                    <th className="sortable-th" onClick={() => empTable.toggleSort('roleId')}>Role<SortArrow active={empTable.sort.key === 'roleId'} dir={empTable.sort.dir} /></th>
                    <th>Monthly limit</th>
                    <th>Set</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {empTable.pageRows.map((r) => (
                    <tr key={r.empCode}>
                      <td>{r.empCode}</td>
                      <td>{r.empName || '—'}</td>
                      <td>{r.roleName || '—'}</td>
                      <td>
                        {r.monthlyLimit
                          ? <span className="badge green">{r.monthlyLimit}</span>
                          : <span className="badge gray">Default ({r.effectiveLimit})</span>}
                      </td>
                      <td style={{ width: 110 }}>
                        <input
                          type="number"
                          min={1}
                          placeholder={String(r.effectiveLimit)}
                          value={empEdits[r.empCode] ?? ''}
                          onChange={(e) => setEmpEdits((m) => ({ ...m, [r.empCode]: e.target.value }))}
                          style={{ width: 100 }}
                        />
                      </td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        <button
                          className="btn primary small"
                          disabled={empBusy === r.empCode || !empEdits[r.empCode]}
                          onClick={() => saveEmpLimit(r.empCode)}
                        >
                          {empBusy === r.empCode ? '…' : 'Save'}
                        </button>{' '}
                        {r.monthlyLimit != null && (
                          <button
                            className="btn secondary small"
                            disabled={empBusy === r.empCode}
                            onClick={() => clearEmpLimit(r.empCode)}
                            title="Remove override — back to default"
                          >
                            Clear
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {!empLoading && empRows.length > 0 && (
              <Pager
                page={empTable.page}
                totalPages={empTable.totalPages}
                totalRows={empTable.totalRows}
                pageSize={empTable.pageSize}
                onPage={empTable.setPage}
                onPageSize={empTable.setPageSize}
              />
            )}
            <p className="page-sub" style={{ marginTop: 10 }}>
              An employee-specific limit replaces the default for that person; an approved limit
              request can still raise it further for the month.
            </p>
          </div>
        </>
      )}

      {isAdmin && (
        <div className="card form-card">
          <div className="card-head">
            <h3>Legacy report import</h3>
          </div>
          <p className="page-sub" style={{ marginBottom: 12 }}>
            Scan the backend <b>pdf files</b> folder and register PDFs from the old application so
            their PANs are served from disk instead of being re-pulled from the vendor. Safe to run
            multiple times — already-registered PANs are skipped.
          </p>
          <button
            className="btn warn"
            onClick={async () => {
              if (!(await confirmAction({
                title: 'Import legacy reports?',
                text: 'The pdf files folder is scanned and unregistered reports are registered against their PANs.\nSafe to run multiple times — already-registered PANs are skipped.',
                confirmText: 'Scan and import',
              }))) return;
              setSaving(true);
              setMessage(null);
              try {
                const res = await api.post<{ imported: number; skipped: number; skippedNoPattern?: number; skippedKnown?: number; root: string }>('/api/cibil/import-legacy', {});
                const detail = res.skippedNoPattern !== undefined
                  ? ` (${res.skippedKnown ?? 0} already registered, ${res.skippedNoPattern ?? 0} unrecognized name)`
                  : '';
                toast.success(`Imported ${res.imported} report(s); ${res.skipped} file(s) skipped${detail}.`, `Folder: ${res.root}`);
              } catch (err) {
                setMessage({ kind: 'err', text: err instanceof ApiError ? err.message : 'Import failed.' });
              } finally {
                setSaving(false);
              }
            }}
            disabled={saving}
          >
            <Icon name="upload" /> {saving ? 'Importing…' : 'Import legacy PDFs'}
          </button>
        </div>
      )}

      <div className="card form-card">
        <p className="page-sub" style={{ marginBottom: 12 }}>
          Verify the backend can reach the database.
        </p>
        <button
          className="btn secondary"
          onClick={async () => {
            setSaving(true);
            setMessage(null);
            try {
              await api.post('/api/settings/test', {});
              setMessage({ kind: 'ok', text: 'Backend connection OK.' });
            } catch {
              setMessage({ kind: 'err', text: 'Connection test failed.' });
            } finally {
              setSaving(false);
            }
          }}
          disabled={saving}
        >
          <Icon name="check" /> {saving ? 'Testing…' : 'Test backend connection'}
        </button>
      </div>
    </div>
  );
}
