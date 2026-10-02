import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { isAdminUser } from '../api';
import { Pager, SortArrow } from '../components/tableView';
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell, Legend,
} from 'recharts';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';

interface AgentRow {
  caller: string;
  location: string;
  tl: string;
  dnd: number;
  lead: number;
  followUp: number;
  ni: number;
  notDoable: number;
  wrongNo: number;
  ringing: number;
  blank: number;
  sameDayFollowUp: number;
  auto: number;
  manual: number;
  totalCount: number;
  duration: string | null;
  disbursedAmt: number;
  disbursedCount: number;
  loginCount: number;
  rank: string;
  lastCalledDate: string | null;
}

interface LiveData {
  generatedAt: string;
  startDate: string;
  endDate: string;
  grandTotal: number;
  activeCallers: number;
  totalDisbursed: number;
  totalDisbursedCount: number;
  scope?: { isAdmin: boolean; subtreeSize: number };
  agents: AgentRow[];
  dispositions: { key: string; value: string }[];
  byLocation: { location: string; calls: number; callers: number }[];
  hourly: { hour: string; calls: number }[];
}

const DISPO_COLORS = ['#1f3a5f', '#b4531f', '#a16207', '#2e5d3a', '#b3341f', '#1f6f8b', '#5d3a6e', '#6e6753', '#96431a', '#4a4636', '#3d6b4f'];

type SortKey = keyof AgentRow;

const fmt = (n: number) => n.toLocaleString('en-IN');
const fmtAmt = (n: number) =>
  n >= 10000000 ? (n / 10000000).toFixed(2) + ' Cr' : n >= 100000 ? (n / 100000).toFixed(2) + ' L' : fmt(n);

export default function DialerLivePage() {
  const today = new Date().toISOString().slice(0, 10);

  // Live report is always for the current day
  const [data, setData] = useState<LiveData | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState('');
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const [search, setSearch] = useState('');
  const [locationFilter, setLocationFilter] = useState('');
  const [sortKey, setSortKey] = useState<SortKey>('totalCount');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');
  const [showAllColumns, setShowAllColumns] = useState(false);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);

  const load = useCallback(async (showSpinner: boolean) => {
    if (showSpinner) setRefreshing(true);
    try {
      // always today
      const d = new Date().toISOString().slice(0, 10);
      const res = await api.get<LiveData>(`/api/dialer/live?start=${d}&end=${d}`);
      setData(res);
      setLastUpdated(new Date());
      setError('');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load dialer data');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    setLoading(true);
    load(false);
  }, [load]);

  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    if (!autoRefresh) return;
    const t = setInterval(() => loadRef.current(true), 60_000);
    return () => clearInterval(t);
  }, [autoRefresh]);

  const locations = useMemo(
    () => Array.from(new Set((data?.agents ?? []).map((a) => a.location).filter(Boolean))).sort(),
    [data]
  );

  const filtered = useMemo(() => {
    let rows = data?.agents ?? [];
    if (search) {
      const q = search.toLowerCase();
      rows = rows.filter(
        (a) => a.caller.toLowerCase().includes(q) || a.tl.toLowerCase().includes(q)
      );
    }
    if (locationFilter) rows = rows.filter((a) => a.location === locationFilter);
    return rows;
  }, [data, search, locationFilter]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
  useEffect(() => {
    if (page > totalPages - 1) setPage(totalPages - 1);
  }, [totalPages, page]);
  const pageRows = useMemo(
    () => filtered.slice(page * pageSize, page * pageSize + pageSize),
    [filtered, page, pageSize]
  );

  const toggleSort = (k: SortKey) => {
    if (k === sortKey) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else {
      setSortKey(k);
      setSortDir('desc');
    }
  };

  const kpis = [
    { label: 'Total Calls', value: fmt(data?.grandTotal ?? 0), icon: 'phoneCall', color: '#1f3a5f', bg: '#e7ebf2' },
    { label: 'Active Callers', value: fmt(data?.activeCallers ?? 0), icon: 'users', color: '#059669', bg: '#d1fae5' },
    { label: 'Calls Connected', value: fmt((data?.dispositions ?? []).filter(d => !['Blank', 'Ringing'].includes(d.key)).reduce((s, d) => s + Number(d.value), 0)), icon: 'check', color: '#b4531f', bg: '#f7ecd7' },
    { label: 'Total Disbursed', value: '₹' + fmtAmt(data?.totalDisbursed ?? 0), icon: 'fileText', color: '#ea580c', bg: '#ffedd5' },
  ];

  const dispoChart = (data?.dispositions ?? []).map((d) => ({ name: d.key, value: Number(d.value) }));
  const locChart = (data?.byLocation ?? []).slice(0, 8).map((l) => ({ name: l.location, calls: l.calls }));

  const columns: { key: SortKey; label: string; hideable?: boolean; numeric?: boolean }[] = [
    { key: 'caller', label: 'Caller' },
    { key: 'location', label: 'Location' },
    { key: 'tl', label: 'Team Lead' },
    { key: 'lead', label: 'Lead', numeric: true },
    { key: 'followUp', label: 'FollowUp', numeric: true },
    { key: 'sameDayFollowUp', label: 'SameDay FU', numeric: true },
    { key: 'dnd', label: 'DND', numeric: true },
    { key: 'ni', label: 'NI', numeric: true },
    { key: 'notDoable', label: 'Not Doable', numeric: true, hideable: true },
    { key: 'wrongNo', label: 'Wrong No', numeric: true, hideable: true },
    { key: 'ringing', label: 'Ringing', numeric: true, hideable: true },
    { key: 'blank', label: 'Blank', numeric: true },
    { key: 'auto', label: 'Auto', numeric: true, hideable: true },
    { key: 'manual', label: 'Manual', numeric: true, hideable: true },
    { key: 'totalCount', label: 'Total', numeric: true },
    { key: 'disbursedAmt', label: 'Disbursed ₹', numeric: true, hideable: true },
    { key: 'disbursedCount', label: 'Disb. Count', numeric: true, hideable: true },
    { key: 'lastCalledDate', label: 'Last Called', hideable: true },
  ];

  const visibleColumns = showAllColumns ? columns : columns.filter((c) => !c.hideable);

  if (loading) return <div className="loading" style={{ paddingTop: 80 }}>Loading live dialer data…</div>;

  return (
    <div className="dialer-page">
      <div className="page-head">
        <div>
          <h1>
            Dialer Live Report
            <span className={'live-dot' + (autoRefresh ? ' on' : '')} title={autoRefresh ? 'Auto-refreshing every 60s' : 'Auto-refresh paused'} />
          </h1>
          <p className="page-sub">
            Today · {data?.startDate}
            {lastUpdated && <> · updated {lastUpdated.toLocaleTimeString('en-IN')}</>}
            <span className="scope-chip" title={isAdminUser() ? 'You can see every caller in the company' : 'You see calls made by you and your downline'}>
              {data?.scope && !data.scope.isAdmin ? `Your team · ${data.scope.subtreeSize} people` : isAdminUser() ? 'All callers' : 'Your team'}
            </span>
          </p>
        </div>
        <div className="dialer-controls">
          <button className="btn" onClick={() => load(true)} disabled={refreshing}>
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </button>
          <label className="check">
            <input type="checkbox" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
            Auto (60s)
          </label>
        </div>
      </div>

      {error && <div className="alert error">{error}</div>}

      <div className="stats-grid">
        {kpis.map((c) => (
          <div className="card stat-card" key={c.label}>
            <div className="stat-icon" style={{ background: c.bg, color: c.color }}>
              <Icon name={c.icon} />
            </div>
            <div className="stat-body">
              <span className="stat-label">{c.label}</span>
              <span className="stat-value">{c.value}</span>
            </div>
          </div>
        ))}
      </div>

      <div className="charts-row dialer-charts">
        <div className="card chart-card">
          <div className="card-head">
            <h3>Calls by Location</h3>
          </div>
          {locChart.length === 0 ? (
            <div className="loading">No data for this range.</div>
          ) : (
            <ResponsiveContainer width="100%" height={280}>
              <BarChart data={locChart} margin={{ bottom: 30 }}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e5e7eb" />
                <XAxis dataKey="name" tickLine={false} axisLine={false} angle={-25} textAnchor="end" tick={{ fontSize: 11, fill: '#6b7280' }} />
                <YAxis tickLine={false} axisLine={false} tick={{ fontSize: 12, fill: '#6b7280' }} />
                <Tooltip />
                <Bar dataKey="calls" fill="#1f3a5f" radius={[6, 6, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          )}
        </div>

        <div className="card chart-card">
          <div className="card-head">
            <h3>Dispositions</h3>
          </div>
          {dispoChart.length === 0 ? (
            <div className="loading">No data for this range.</div>
          ) : (
            <ResponsiveContainer width="100%" height={280}>
              <PieChart>
                <Pie data={dispoChart} dataKey="value" nameKey="name" cx="38%" cy="50%" innerRadius={62} outerRadius={95} paddingAngle={2}>
                  {dispoChart.map((_, i) => (
                    <Cell key={i} fill={DISPO_COLORS[i % DISPO_COLORS.length]} />
                  ))}
                </Pie>
                <Legend verticalAlign="middle" align="right" layout="vertical" wrapperStyle={{ fontSize: 12 }} />
                <Tooltip />
              </PieChart>
            </ResponsiveContainer>
          )}
        </div>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search caller or team lead..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={locationFilter} onChange={(e) => setLocationFilter(e.target.value)} className="filter-select">
            <option value="">All Locations</option>
            {locations.map((l) => (
              <option key={l} value={l}>{l}</option>
            ))}
          </select>
          <label className="check">
            <input type="checkbox" checked={showAllColumns} onChange={(e) => setShowAllColumns(e.target.checked)} />
            All columns
          </label>
        </div>

        <table className="data-table dialer-table">
          <thead>
            <tr>
              {visibleColumns.map((c) => (
                <th
                  key={c.key}
                  onClick={() => toggleSort(c.key)}
                  className={'sortable-th' + (c.numeric ? ' num' : '')}
                >
                  {c.label}
                  <SortArrow active={sortKey === c.key} dir={sortDir} />
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {pageRows.map((a, i) => (
              <tr key={i}>
                {visibleColumns.map((c) => {
                  const v = a[c.key];
                  if (c.key === 'disbursedAmt') return <td key={c.key} className="num">{fmtAmt(Number(v))}</td>;
                  if (c.numeric) return <td key={c.key} className="num">{fmt(Number(v))}</td>;
                  return <td key={c.key}>{(v as string) || '—'}</td>;
                })}
              </tr>
            ))}
            {filtered.length === 0 && (
              <tr>
                <td colSpan={visibleColumns.length} className="empty">No callers match the filters.</td>
              </tr>
            )}
          </tbody>
          {filtered.length > 0 && (
            <tfoot>
              <tr>
                {visibleColumns.map((c, idx) => {
                  if (idx === 0) return <td key={c.key}>TOTAL ({filtered.length.toLocaleString('en-IN')})</td>;
                  if (!c.numeric) return <td key={c.key} />;
                  const sum = filtered.reduce((s, a) => s + Number(a[c.key] ?? 0), 0);
                  return <td key={c.key} className="num">{c.key === 'disbursedAmt' ? fmtAmt(sum) : fmt(sum)}</td>;
                })}
              </tr>
            </tfoot>
          )}
        </table>

        <Pager
          page={page}
          totalPages={totalPages}
          totalRows={filtered.length}
          pageSize={pageSize}
          onPage={setPage}
          onPageSize={(n) => {
            setPageSize(n);
            setPage(0);
          }}
        />
      </div>
    </div>
  );
}
