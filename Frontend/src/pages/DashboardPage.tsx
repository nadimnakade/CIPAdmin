import { useEffect, useState } from 'react';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell, Legend,
} from 'recharts';
import { api, ApiError, isAdminUser } from '../api';
import Icon from '../components/Icon';

interface DashboardData {
  cibilPulledToday: number;
  cibilPulledTodayDelta: number;
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

interface DialerUserRow {
  empCode: number;
  employeeCodeText: string | null;
  empName: string | null;
  designation: string | null;
  dialerVersion: string | null;
  dialerUuid: string | null;
  isNewDialer: boolean;
}

const PIE_COLORS = ['#1f3a5f', '#b4531f', '#2e5d3a', '#a16207'];

export default function DashboardPage() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState('');
  const [dialerModal, setDialerModal] = useState<'old' | 'new' | 'all' | null>(null);

  useEffect(() => {
    api.get<DashboardData>('/api/dashboard').then(setData).catch((e) => setError(e.message));
  }, []);

  if (error) return <div className="alert error">{error}</div>;
  if (!data) return <div className="loading">Loading dashboard…</div>;

  const totalByType = data.reportsByType.reduce((s, t) => s + t.count, 0);

  const cards = [
    {
      label: 'CIBIL Pulled Today',
      value: data.cibilPulledToday,
      delta: data.cibilPulledTodayDelta as number | null,
      vs: 'vs yesterday',
      icon: 'fileText',
      color: '#1f3a5f',
      bg: '#e7ebf2',
    },
    {
      label: 'Total CIBIL Reports',
      value: data.totalCibilReports,
      delta: data.totalCibilDelta as number | null,
      vs: 'vs last month',
      icon: 'layers',
      color: '#b4531f',
      bg: '#f7ecd7',
    },
    {
      label: 'Active Employees',
      value: data.activeEmployees,
      delta: null,
      vs: 'on roll',
      icon: 'users',
      color: '#059669',
      bg: '#d1fae5',
    },
    {
      label: 'Active DIDs',
      value: data.activeDids,
      delta: null,
      vs: `${data.unassignedDids} unassigned`,
      icon: 'phoneCall',
      color: '#ea580c',
      bg: '#ffedd5',
    },
  ];

  const dialerCards: { group: 'old' | 'new' | 'all'; label: string; value: number; icon: string; color: string; bg: string; vs: string }[] = [
    {
      group: 'old',
      label: 'Old Dialer Users',
      value: data.oldDialerUsers,
      icon: 'phoneCall',
      color: '#6b7280',
      bg: '#f3f4f6',
      vs: 'default for everyone',
    },
    {
      group: 'new',
      label: 'New Dialer Users',
      value: data.newDialerUsers,
      icon: 'phoneOutgoing',
      color: '#1f3a5f',
      bg: '#e7ebf2',
      vs: 'moved to the new dialer app',
    },
    {
      group: 'all',
      label: 'All Dialer Users',
      value: data.oldDialerUsers + data.newDialerUsers,
      icon: 'users',
      color: '#b4531f',
      bg: '#f7ecd7',
      vs: 'old + new combined',
    },
  ];

  return (
    <div className="page">
      {dialerModal && <DialerUsersModal group={dialerModal} onClose={() => setDialerModal(null)} />}
      <div className="page-head">
        <div>
          <h1>Dashboard</h1>
          <p className="page-sub">Welcome back, Admin! Here's what's happening today.</p>
        </div>
        <span className="page-date">
          {new Date().toLocaleDateString('en-IN', {
            weekday: 'short',
            day: 'numeric',
            month: 'short',
            year: 'numeric',
          })}
        </span>
      </div>

      <div className="stats-grid">
        {cards.map((c) => (
          <div className="card stat-card" key={c.label}>
            <div className="stat-icon" style={{ background: c.bg, color: c.color }}>
              <Icon name={c.icon} />
            </div>
            <div className="stat-body">
              <span className="stat-label">{c.label}</span>
              <span className="stat-value">{c.value.toLocaleString('en-IN')}</span>
              <span className="stat-delta">
                {c.delta !== null && (
                  <span className={c.delta >= 0 ? 'delta up' : 'delta down'}>
                    {c.delta >= 0 ? '↑' : '↓'} {Math.abs(c.delta)}%
                  </span>
                )}{' '}
                {c.vs}
              </span>
            </div>
          </div>
        ))}
      </div>

      <div className="stats-grid dialer-grid">
        {dialerCards.map((c) => (
          <div
            className="card stat-card clickable"
            key={c.label}
            role="button"
            tabIndex={0}
            onClick={() => setDialerModal(c.group)}
            onKeyDown={(e) => e.key === 'Enter' && setDialerModal(c.group)}
            title="View user list"
          >
            <div className="stat-icon" style={{ background: c.bg, color: c.color }}>
              <Icon name={c.icon} />
            </div>
            <div className="stat-body">
              <span className="stat-label">{c.label}</span>
              <span className="stat-value">{c.value.toLocaleString('en-IN')}</span>
              <span className="stat-delta">{c.vs}</span>
            </div>
          </div>
        ))}
      </div>

      <div className="charts-row">
        <div className="card chart-card">
          <div className="card-head">
            <h3>CIBIL Reports Trend</h3>
            <span className="select-like">Last 7 Days</span>
          </div>
          <ResponsiveContainer width="100%" height={260}>
            <LineChart data={data.trend}>
              <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e5e7eb" />
              <XAxis dataKey="label" tickLine={false} axisLine={false} tick={{ fontSize: 12, fill: '#6b7280' }} />
              <YAxis tickLine={false} axisLine={false} tick={{ fontSize: 12, fill: '#6b7280' }} allowDecimals={false} />
              <Tooltip />
              <Line type="monotone" dataKey="count" stroke="#1f3a5f" strokeWidth={2.5} dot={{ r: 3 }} />
            </LineChart>
          </ResponsiveContainer>
        </div>

        <div className="card chart-card">
          <div className="card-head">
            <h3>Reports by Type</h3>
          </div>
          {totalByType === 0 ? (
            <div className="loading">No reports yet.</div>
          ) : (
            <ResponsiveContainer width="100%" height={260}>
              <PieChart>
                <Pie
                  data={data.reportsByType}
                  dataKey="count"
                  nameKey="reportType"
                  cx="40%"
                  cy="50%"
                  innerRadius={60}
                  outerRadius={90}
                  paddingAngle={3}
                >
                  {data.reportsByType.map((_, i) => (
                    <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Legend verticalAlign="middle" align="right" layout="vertical" />
                <Tooltip />
              </PieChart>
            </ResponsiveContainer>
          )}
        </div>
      </div>
    </div>
  );
}

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
    const verb = r.isNewDialer ? 'move back to the OLD dialer' : 'move to the NEW dialer';
    if (!confirm(`${verb}: ${r.empName || r.empCode}?`)) return;
    setBusy(r.empCode);
    try {
      await api.put(`/api/dashboard/dialer-users/${r.empCode}`, {
        dialerVersion: r.isNewDialer ? null : '0.0.23',
      });
      // Refresh the current page.
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
          <h3>{title} <span className="scope-chip">{total.toLocaleString('en-IN')} users</span></h3>
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
                        <button
                          className="btn sm ghost"
                          disabled={busy === r.empCode}
                          onClick={() => move(r)}
                        >
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
