import { useEffect, useState } from 'react';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell,
} from 'recharts';
import { api, ApiError, getUser } from '../api';
import Icon from '../components/Icon';
import './DashboardPage.css';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';

interface DashboardData {
  cibilPulledToday: number;
  cibilPulledTodayDelta: number;
  cibilPulledYesterday: number;
  cibilPulledThisWeek: number;
  cibilPulledThisMonth: number;
  totalCibilReports: number;
  totalCibilDelta: number;
  activeEmployees: number;
  activeEmployeesDelta: number;
  activeDids: number;
  unassignedDids: number;
  oldDialerUsers: number;
  newDialerUsers: number;
  trend: { label: string; count: number }[];
  reportsByType: { reportType: string; count: number }[];
}

interface RecentReport {
  reportId: number;
  applicantName: string | null;
  panNo: string;
  reportType: string;
  pulledDate: string;
  status?: string | null;
}

/** Tonal pairs, verified >= 4.5:1 by npm run check:contrast. */
const TONE = {
  blue: { bg: '#e8f1ff', fg: '#1257a8' },
  orange: { bg: '#fdf0e6', fg: '#8a4310' },
  green: { bg: '#e4f6ed', fg: '#0f6b3d' },
  teal: { bg: '#e2f4f2', fg: '#0d6b62' },
  violet: { bg: '#f1ebff', fg: '#5b32a8' },
  amber: { bg: '#fff5d8', fg: '#7a5406' },
} as const;
type ToneName = keyof typeof TONE;

const PIE_COLORS = ['#1257a8', '#0f6b3d', '#7a5406', '#5b32a8'];

const fmt = (n: number) => n.toLocaleString('en-IN');

export default function DashboardPage() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [recent, setRecent] = useState<RecentReport[]>([]);
  const [error, setError] = useState('');
  const [dialerModal, setDialerModal] = useState<'old' | 'new' | 'all' | null>(null);
  const [period, setPeriod] = useState<'7' | '30'>('7');

  useEffect(() => {
    api.get<DashboardData>('/api/dashboard').then(setData).catch((e) => setError(e.message));
  }, []);

  // Recent CIBIL reports reuses the existing reports feed; a failure here must not
  // take the whole dashboard down, so it is swallowed into an empty list.
  useEffect(() => {
    api
      .get<{ rows: RecentReport[] }>('/api/cibil/reports')
      .then((d) => setRecent((d.rows || []).slice(0, 5)))
      .catch(() => setRecent([]));
  }, []);

  if (error) return <div className="alert error">{error}</div>;
  if (!data) return <div className="loading">Loading dashboard…</div>;

  const totalByType = data.reportsByType.reduce((s, t) => s + t.count, 0);
  const who = getUser();
  // Username, not empName: an employee's display name can be a full legal name
  // ("Administrator"), which reads oddly in a one-word greeting.
  const firstName = (who?.username || who?.empName || 'there').split(' ')[0];

  // The 7-day window is what the API returns; "Last 30 Days" is a cumulative total,
  // labelled as such, rather than a chart redrawn with points that do not exist.
  const trend = period === '7' ? data.trend : [];

  const kpis = [
    {
      label: 'CIBIL Pulled Today',
      value: data.cibilPulledToday,
      foot: (
        <>
          <span className={`dash-delta ${data.cibilPulledTodayDelta >= 0 ? 'up' : 'down'}`}>
            {data.cibilPulledTodayDelta >= 0 ? '↑' : '↓'} {Math.abs(data.cibilPulledTodayDelta)}%
          </span>{' '}
          <span className="dash-foot">vs yesterday</span>
        </>
      ),
      icon: 'fileText',
      tone: 'blue' as ToneName,
    },
    {
      label: 'Total CIBIL Reports',
      value: data.totalCibilReports,
      foot: (
        <>
          <span className={`dash-delta ${data.totalCibilDelta >= 0 ? 'up' : 'down'}`}>
            {data.totalCibilDelta >= 0 ? '↑' : '↓'} {Math.abs(data.totalCibilDelta)}%
          </span>{' '}
          <span className="dash-foot">vs last month</span>
        </>
      ),
      icon: 'layers',
      tone: 'orange' as ToneName,
    },
    {
      label: 'Active Employees',
      value: data.activeEmployees,
      foot: <span className="dash-foot">on roll</span>,
      icon: 'users',
      tone: 'green' as ToneName,
    },
    {
      label: 'Active DIDs',
      value: data.activeDids,
      foot: <span className="dash-foot">{data.unassignedDids} unassigned</span>,
      icon: 'phoneCall',
      tone: 'orange' as ToneName,
    },
  ];

  const dialerCards = [
    {
      group: 'old' as const,
      label: 'Old Dialer Users',
      value: data.oldDialerUsers,
      icon: 'phoneCall',
      tone: 'violet' as ToneName,
      foot: 'default for everyone',
    },
    {
      group: 'new' as const,
      label: 'New Dialer Users',
      value: data.newDialerUsers,
      icon: 'phoneOutgoing',
      tone: 'blue' as ToneName,
      foot: 'moved to the new dialer app',
    },
    {
      group: 'all' as const,
      label: 'All Dialer Users',
      value: data.oldDialerUsers + data.newDialerUsers,
      icon: 'users',
      tone: 'orange' as ToneName,
      foot: 'old + new combined',
    },
  ];

  const periodStats = [
    { k: 'Today', v: data.cibilPulledToday, tone: 'blue' as ToneName },
    { k: 'Yesterday', v: data.cibilPulledYesterday, tone: 'orange' as ToneName },
    { k: 'This Week', v: data.cibilPulledThisWeek, tone: 'green' as ToneName },
    { k: 'This Month', v: data.cibilPulledThisMonth, tone: 'violet' as ToneName },
  ];

  return (
    <div className="page dash-page">
      {dialerModal && <DialerUsersModal group={dialerModal} onClose={() => setDialerModal(null)} />}

      <div className="dash-head">
        <div className="dash-head-text">
          <span className="dash-eyebrow">Dashboard</span>
          <h1>Dashboard</h1>
          <p className="dash-sub">Welcome back, {firstName}! Here's what's happening today.</p>
        </div>
        <span className="dash-date">
          <Icon name="calendar" size={14} />
          {new Date().toLocaleDateString('en-IN', {
            weekday: 'short',
            day: 'numeric',
            month: 'short',
            year: 'numeric',
          })}
        </span>
      </div>

      <div className="dash-kpis">
        {kpis.map((c) => (
          <article className="dash-card" key={c.label}>
            <div className="dash-card-top">
              <span className="dash-card-icon" style={{ background: TONE[c.tone].bg, color: TONE[c.tone].fg }}>
                <Icon name={c.icon} />
              </span>
              <button className="dash-more" aria-label={`More options for ${c.label}`}>
                <Icon name="more" size={16} />
              </button>
            </div>
            <span className="dash-card-label">{c.label}</span>
            <strong className="dash-card-value">{fmt(c.value)}</strong>
            <span className="dash-card-foot">{c.foot}</span>
          </article>
        ))}
      </div>

      <div className="dash-kpis dash-kpis-3">
        {dialerCards.map((c) => (
          <article
            className="dash-card dash-card-click"
            key={c.label}
            role="button"
            tabIndex={0}
            onClick={() => setDialerModal(c.group)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                setDialerModal(c.group);
              }
            }}
          >
            <div className="dash-card-top">
              <span className="dash-card-icon" style={{ background: TONE[c.tone].bg, color: TONE[c.tone].fg }}>
                <Icon name={c.icon} />
              </span>
              <button
                className="dash-more"
                aria-label={`More options for ${c.label}`}
                onClick={(e) => e.stopPropagation()}
              >
                <Icon name="more" size={16} />
              </button>
            </div>
            <span className="dash-card-label">{c.label}</span>
            <strong className="dash-card-value">{fmt(c.value)}</strong>
            <span className="dash-card-foot">{c.foot}</span>
          </article>
        ))}
      </div>

      <div className="dash-split">
        <section className="dash-panel">
          <div className="dash-panel-head">
            <h2>
              <span className="dash-panel-icon" style={{ background: TONE.blue.bg, color: TONE.blue.fg }}>
                <Icon name="barChart" size={15} />
              </span>
              CIBIL Reports Trend
            </h2>
            <label className="dash-select">
              <Icon name="calendar" size={13} />
              <select value={period} onChange={(e) => setPeriod(e.target.value as '7' | '30')} aria-label="Trend period">
                <option value="7">Last 7 Days</option>
                <option value="30">Last 30 Days</option>
              </select>
            </label>
          </div>

          {period === '7' ? (
            <ResponsiveContainer width="100%" height={200}>
              <LineChart data={trend} margin={{ top: 8, right: 12, bottom: 0, left: -18 }}>
                <CartesianGrid stroke="#eef2f7" vertical={false} />
                <XAxis dataKey="label" tickLine={false} axisLine={false} tick={{ fontSize: 11, fill: '#7b89a0' }} />
                <YAxis tickLine={false} axisLine={false} tick={{ fontSize: 11, fill: '#7b89a0' }} allowDecimals={false} width={38} />
                <Tooltip
                  contentStyle={{ borderRadius: 8, border: '1px solid #e3eaf3', fontSize: 12 }}
                  labelStyle={{ color: '#15294f', fontWeight: 600 }}
                />
                <Line
                  type="monotone"
                  dataKey="count"
                  name="Reports"
                  stroke={TONE.blue.fg}
                  strokeWidth={2}
                  dot={{ r: 3, fill: TONE.blue.fg, strokeWidth: 0 }}
                  activeDot={{ r: 5 }}
                />
              </LineChart>
            </ResponsiveContainer>
          ) : (
            <div className="dash-panel-empty">
              <strong>{fmt(data.totalCibilReports)}</strong>
              <span>reports in total. The daily chart covers the last 7 days; totals below cover the month.</span>
            </div>
          )}

          <div className="dash-period">
            {periodStats.map((s) => (
              <div className="dash-period-item" key={s.k}>
                <strong style={{ color: TONE[s.tone].fg }}>{fmt(s.v)}</strong>
                <span>{s.k}</span>
              </div>
            ))}
          </div>
        </section>

        <section className="dash-panel">
          <div className="dash-panel-head">
            <h2>
              <span className="dash-panel-icon" style={{ background: TONE.violet.bg, color: TONE.violet.fg }}>
                <Icon name="layers" size={15} />
              </span>
              Reports by Type
            </h2>
          </div>
          {totalByType === 0 ? (
            <div className="dash-empty">
              <span className="dash-empty-icon">
                <Icon name="fileText" size={24} />
              </span>
              <strong>No reports yet.</strong>
              <span>Reports by type will be shown here once data is available.</span>
            </div>
          ) : (
            <ResponsiveContainer width="100%" height={200}>
              <PieChart>
                <Pie data={data.reportsByType} dataKey="count" nameKey="reportType" innerRadius={52} outerRadius={78} paddingAngle={3}>
                  {data.reportsByType.map((_, i) => (
                    <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Tooltip
                  contentStyle={{ borderRadius: 8, border: '1px solid #e3eaf3', fontSize: 12 }}
                  labelStyle={{ color: '#15294f', fontWeight: 600 }}
                />
              </PieChart>
            </ResponsiveContainer>
          )}
        </section>
      </div>

      <div className="dash-split">
        <section className="dash-panel">
          <div className="dash-panel-head">
            <h2>
              <span className="dash-panel-icon" style={{ background: TONE.blue.bg, color: TONE.blue.fg }}>
                <Icon name="report" size={15} />
              </span>
              Recent CIBIL Reports
            </h2>
            <a className="dash-link" href="#/cibil">View All</a>
          </div>
          {recent.length === 0 ? (
            <div className="dash-empty">
              <span className="dash-empty-icon">
                <Icon name="search" size={24} />
              </span>
              <strong>No CIBIL reports found</strong>
              <span>Pulled reports will appear here.</span>
            </div>
          ) : (
            <div className="dash-table-scroll">
              <table className="dash-table">
                <thead>
                  <tr>
                    <th scope="col">#</th>
                    <th scope="col">Customer Name</th>
                    <th scope="col">Report Type</th>
                    <th scope="col">Pulled On</th>
                    <th scope="col">Status</th>
                  </tr>
                </thead>
                <tbody>
                  {recent.map((r, i) => (
                    <tr key={r.reportId}>
                      <td>{i + 1}</td>
                      <td className="dash-strong">{r.applicantName || r.panNo}</td>
                      <td>{r.reportType}</td>
                      <td className="dash-nowrap">
                        {r.pulledDate ? new Date(r.pulledDate).toLocaleDateString('en-IN') : '—'}
                      </td>
                      <td>
                        <span className={`dash-status ${r.status === 'Pending' ? 'pending' : 'done'}`}>
                          {r.status || 'Ready'}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="dash-panel">
          <div className="dash-panel-head">
            <h2>
              <span className="dash-panel-icon" style={{ background: TONE.teal.bg, color: TONE.teal.fg }}>
                <Icon name="clock" size={15} />
              </span>
              Recent Activities
            </h2>
            <a className="dash-link" href="#/reports">View All</a>
          </div>
          {/*
            No activity-log feed exists on the API — the session registry keeps only the
            current session per employee, not history. Until one does, this shows the
            dashboard's own load time rather than inventing login/audit rows.
          */}
          <ul className="dash-activity">
            <li className="dash-activity-item">
              <span className="dash-dot blue" />
              <div>
                <strong>System</strong>
                <span>Dashboard loaded at {new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}</span>
              </div>
            </li>
            <li className="dash-activity-item">
              <span className="dash-dot green" />
              <div>
                <strong>Session</strong>
                <span>{firstName} is signed in</span>
              </div>
            </li>
            <li className="dash-activity-item">
              <span className="dash-dot amber" />
              <div>
                <strong>Reports</strong>
                <span>
                  {fmt(totalByType)} report{plural(totalByType)} recorded so far
                </span>
              </div>
            </li>
          </ul>
        </section>
      </div>
    </div>
  );
}

const plural = (n: number) => (n === 1 ? '' : 's');

/* ── Old / New / All dialer user list ─────────────────────────────── */

function DialerUsersModal({ group, onClose }: { group: 'old' | 'new' | 'all'; onClose: () => void }) {
  const title = group === 'old' ? 'Old Dialer Users' : group === 'new' ? 'New Dialer Users' : 'All Dialer Users';
  const [rows, setRows] = useState<DialerUserRow[]>([]);
  const [total, setTotal] = useState(0);
  const [isAdmin, setIsAdmin] = useState(false);
  const [page, setPage] = useState(1);
  const pageSize = 100;
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    const t = setTimeout(() => {
      setLoading(true);
      const params = new URLSearchParams({ group, page: String(page), pageSize: String(pageSize) });
      if (search) params.set('search', search);
      api
        .get<{ total: number; rows: DialerUserRow[]; isAdmin: boolean }>('/api/dashboard/dialer-users?' + params.toString())
        .then((d) => {
          if (cancelled) return;
          setRows(d.rows);
          setTotal(d.total);
          setIsAdmin(d.isAdmin);
          setError('');
        })
        .catch((e) => !cancelled && setError(e instanceof ApiError ? e.message : 'Failed to load users'))
        .finally(() => !cancelled && setLoading(false));
    }, search ? 300 : 0);
    return () => { cancelled = true; clearTimeout(t); };
  }, [group, search, page]);

  const move = async (r: DialerUserRow) => {
    const toNew = !r.isNewDialer;
    const name = r.empName || String(r.empCode);
    const ok = await confirmAction({
      title: `Move ${name}?`,
      text: `${name} will be moved to the ${toNew ? 'NEW' : 'OLD'} dialer.`,
      confirmText: 'Move',
    });
    if (!ok) return;
    setBusy(r.empCode);
    try {
      await api.put(`/api/dashboard/dialer-users/${r.empCode}`, {
        dialerVersion: toNew ? '0.0.23' : null,
      });
      toast.success(`${name} moved to the ${toNew ? 'NEW' : 'OLD'} dialer.`);
      const params = new URLSearchParams({ group, page: String(page), pageSize: String(pageSize) });
      if (search) params.set('search', search);
      const d = await api.get<{ total: number; rows: DialerUserRow[] }>('/api/dashboard/dialer-users?' + params.toString());
      setRows(d.rows);
      setTotal(d.total);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Update failed');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal wide" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{title} <span className="scope-chip">{fmt(total)} users</span></h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>

        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by name or code..."
              value={search}
              onChange={(e) => { setPage(1); setSearch(e.target.value); }}
            />
          </div>
        </div>

        {error && <div className="alert error">{error}</div>}
        {loading ? (
          <div className="loading">Loading…</div>
        ) : (
          <div className="table-scroll modal-table">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Emp Code</th>
                  <th>Name</th>
                  <th>Designation</th>
                  <th>Dialer</th>
                  {isAdmin && <th style={{ width: 140 }}>Action</th>}
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.empCode}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td className="mono">{r.employeeCodeText || r.empCode}</td>
                    <td>{r.empName}</td>
                    <td>{r.designation || '—'}</td>
                    <td>
                      {r.isNewDialer ? (
                        <span className="badge green" title={r.dialerVersion || ''}>New {r.dialerVersion ? `· v${r.dialerVersion}` : ''}</span>
                      ) : (
                        <span className="badge gray">Old</span>
                      )}
                    </td>
                    {isAdmin && (
                      <td>
                        <button className="btn sm ghost" disabled={busy === r.empCode} onClick={() => move(r)}>
                          {busy === r.empCode ? '…' : r.isNewDialer ? 'Set Old' : 'Set New'}
                        </button>
                      </td>
                    )}
                  </tr>
                ))}
                {rows.length === 0 && (
                  <tr><td colSpan={isAdmin ? 6 : 5} className="empty">No users found.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}

        <div className="modal-actions">
          <button className="btn" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1}>← Prev</button>
          <span className="pager-count">Page {page} of {Math.max(1, Math.ceil(total / pageSize))}</span>
          <button className="btn" onClick={() => setPage((p) => p + 1)} disabled={page >= Math.ceil(total / pageSize)}>Next →</button>
          <button className="btn primary" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

interface DialerUserRow {
  empCode: number;
  employeeCodeText: string | null;
  empName: string | null;
  designation: string | null;
  dialerVersion: string | null;
  dialerUuid: string | null;
  isNewDialer: boolean;
}
