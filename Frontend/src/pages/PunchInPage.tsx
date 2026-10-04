import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

interface Option { id: number; name: string }
interface NotPunched { employee: string; supervisor: string; location: string }

/** `datetime-local` wants exactly `YYYY-MM-DDTHH:mm` in the viewer's own timezone. */
function localStamp(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

/**
 * Punch In / Out. Replicates the legacy PunchIn screen: Location → TL → Caller (multi-select)
 * then a timestamp and the two punch buttons. The employees not punched today are listed
 * underneath, because the legacy screen showed nothing after a successful punch.
 */
export default function PunchInPage() {
  const [locations, setLocations] = useState<Option[]>([]);
  const [branchId, setBranchId] = useState('');
  const [supervisors, setSupervisors] = useState<Option[]>([]);
  const [supervisorId, setSupervisorId] = useState('');
  const [callers, setCallers] = useState<Option[]>([]);
  const [selected, setSelected] = useState<number[]>([]);

  const [stamp, setStamp] = useState(() => localStamp(new Date()));
  const [busy, setBusy] = useState<'I' | 'O' | ''>('');

  const [missing, setMissing] = useState<NotPunched[]>([]);
  const [missingLoading, setMissingLoading] = useState(true);

  useEffect(() => {
    api.get<Option[]>('/api/punchin/locations')
      .then(setLocations)
      .catch(() => toast.error('Could not load locations.'));
  }, []);

  const loadMissing = useCallback(() => {
    setMissingLoading(true);
    api.get<{ rows: NotPunched[] }>('/api/punchin/not-punched')
      .then((d) => setMissing(d.rows ?? []))
      .catch(() => setMissing([]))
      .finally(() => setMissingLoading(false));
  }, []);

  useEffect(() => { loadMissing(); }, [loadMissing]);

  const loadSupervisors = useCallback((branch: string) => {
    if (!branch) { setSupervisors([]); setSupervisorId(''); return; }
    api.post<Option[]>('/api/punchin/supervisors', { branchId: Number(branch) })
      .then(setSupervisors)
      .catch(() => setSupervisors([]));
  }, []);

  const loadCallers = useCallback((branch: string, sup: string, selectAll = false) => {
    if (!branch || !sup) { setCallers([]); setSelected([]); return; }
    api.post<Option[]>('/api/punchin/callers', {
      branchId: Number(branch),
      // The procedure treats -1 as "everyone at this location".
      supervisorId: sup === '-1' ? -1 : Number(sup),
    })
      .then((list) => {
        setCallers(list);
        setSelected(selectAll ? list.map((c) => c.id) : []);
      })
      .catch(() => { setCallers([]); setSelected([]); });
  }, []);

  const toggle = (id: number) =>
    setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));

  const punch = async (type: 'I' | 'O') => {
    if (!stamp) { toast.error('Enter the date and time.'); return; }
    if (selected.length === 0) { toast.error('Select at least one caller.'); return; }
    setBusy(type);
    try {
      // No timezone suffix: the value is already the local wall-clock the user typed, and
      // appending an offset would shift the stamp the procedure records.
      const res = await api.post<{ ok: boolean; count: number; recorded: string }>('/api/punchin/punch', {
        date: stamp,
        type,
        empKeys: selected,
      });
      toast.success(
        `Punched ${type === 'I' ? 'in' : 'out'} ${res.count} employee${res.count === 1 ? '' : 's'}` +
        (res.recorded ? ` at ${res.recorded.trim()}` : '') + '.');
      loadMissing();
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : 'Could not record the punch.');
    } finally {
      setBusy('');
    }
  };

  const selectedNames = callers.filter((c) => selected.includes(c.id)).map((c) => c.name);

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Punch In / Out</h1>
          <p className="page-sub">
            Record attendance for the callers under a team leader. Pick a location and TL, then
            choose everyone you want to punch at once.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Attendance</h3></div>
        <div className="card-body">
          <div className="filter-grid">
            <div className="field">
              <label className="field-label" htmlFor="punch-loc">Location</label>
              <select id="punch-loc" className="inp" value={branchId}
                onChange={(e) => {
                  setBranchId(e.target.value);
                  setSupervisorId('');
                  setSelected([]);
                  loadSupervisors(e.target.value);
                }}>
                <option value="">— select —</option>
                {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </select>
            </div>

            <div className="field">
              <label className="field-label" htmlFor="punch-tl">TL</label>
              <select id="punch-tl" className="inp" value={supervisorId} disabled={!branchId}
                onChange={(e) => {
                  const v = e.target.value;
                  setSupervisorId(v);
                  setSelected([]);
                  // "--SELECT ALL--" is the -1 sentinel the hierarchy procedure returns;
                  // it means everyone under this location, so select the whole list.
                  if (v === '-1') {
                    loadCallers(branchId, '-1', true);
                  } else {
                    loadCallers(branchId, v);
                  }
                }}>
                <option value="">— select —</option>
                {supervisors.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </div>

            <div className="field">
              <label className="field-label" htmlFor="punch-date">Date &amp; time</label>
              <input id="punch-date" className="inp" type="datetime-local" value={stamp}
                onChange={(e) => setStamp(e.target.value)} />
            </div>
          </div>

          <div className="field" style={{ marginTop: 16 }}>
            <label className="field-label" htmlFor="punch-callers">
              Callers
              <span className="muted-note" style={{ margin: 0, fontWeight: 400 }}>
                {' '}({selected.length} selected)
              </span>
            </label>

            {!supervisorId ? (
              <p className="muted-note">Choose a location and TL to list the callers.</p>
            ) : callers.length === 0 ? (
              <p className="muted-note">No callers found for this TL.</p>
            ) : (
              <>
                <div className="caller-toolbar">
                  <button className="btn small secondary" type="button"
                    onClick={() => setSelected(callers.map((c) => c.id))}>Select all</button>
                  <button className="btn small secondary" type="button"
                    onClick={() => setSelected([])}>Clear</button>
                </div>
                <ul className="caller-list" id="punch-callers">
                  {callers.map((c) => (
                    <li key={c.id}>
                      <label>
                        <input type="checkbox" checked={selected.includes(c.id)}
                          onChange={() => toggle(c.id)} />
                        <span>{c.name}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </div>

          {selected.length > 0 && (
            <p className="muted-note">Selected: {selectedNames.join(', ')}</p>
          )}

          <div className="actions-row" style={{ marginTop: 16 }}>
            <button className="btn primary" onClick={() => punch('I')}
              disabled={busy !== '' || selected.length === 0}>
              <Icon name="check" /> {busy === 'I' ? 'Recording…' : 'Punch In'}
            </button>
            <button className="btn secondary" onClick={() => punch('O')}
              disabled={busy !== '' || selected.length === 0}>
              <Icon name="logout" /> {busy === 'O' ? 'Recording…' : 'Punch Out'}
            </button>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>Not punched in today</h3>
          <span className="card-head-note">
            {missingLoading ? 'Loading…' : `${missing.length} employee${missing.length === 1 ? '' : 's'}`}
          </span>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr><th>Employee</th><th>TL</th><th>Location</th></tr>
            </thead>
            <tbody>
              {missing.map((m, i) => (
                <tr key={i}>
                  <td>{m.employee}</td>
                  <td>{m.supervisor}</td>
                  <td>{m.location}</td>
                </tr>
              ))}
              {!missingLoading && missing.length === 0 && (
                <tr><td colSpan={3} className="empty-cell">Everyone has punched in today.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}