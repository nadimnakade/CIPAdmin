import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { Pager } from '../components/tableView';
import { toast } from '../lib/toast';

interface ErrorRow {
  id: number;
  errorOn: string;
  httpMethod: string;
  requestPath: string;
  queryString: string;
  source: string;
  exceptionType: string;
  message: string;
  sqlErrorNumber: number | null;
  dbObject: string | null;
  empCode: number | null;
  empName: string;
  username: string;
  ipAddress: string;
  statusCode: number | null;
  isResolved: boolean;
  resolutionNote: string;
}

interface ErrorDetail extends ErrorRow {
  stackTrace: string;
  userAgent: string;
  roleId: number | null;
  sqlErrorState: number | null;
  sqlErrorClass: number | null;
  resolvedBy: number | null;
  resolvedOn: string | null;
}

/**
 * Admin view over dbo.TBL_CIP_API_ERROR_LOG - every database and application error
 * the API hit, so a failure is found here instead of being reported second-hand.
 *
 * Pagination is SERVER-side on purpose. The other tables in this app are small enough
 * to page in the browser, but an error log is the one table that grows without bound
 * and is scanned exactly when something is already broken - pulling 100,000 rows to
 * show 25 would make the screen slowest at the moment it is most needed.
 *
 * Only ADMIN (roles 1-2) can reach this. The endpoint enforces the same rule, so a
 * non-admin following a pasted URL gets a 401 rather than a blank screen.
 */
export default function ErrorLogsPage() {
  const [rows, setRows] = useState<ErrorRow[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(25);
  const [search, setSearch] = useState('');
  const [source, setSource] = useState('');
  const [resolved, setResolved] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  // null = the table has never been created on the server. Distinguished from "no
  // errors" because an empty table and a missing one look identical in a response,
  // and they mean completely different things to whoever is reading this screen.
  const [tableMissing, setTableMissing] = useState(false);

  const [detail, setDetail] = useState<ErrorDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);

  // Changing a filter must return to page 0. Staying on page 7 of a result set that
  // now has two pages shows an empty grid and reads like "the filter found nothing".
  const resetPage = <T,>(setter: (v: T) => void) => (v: T) => {
    setter(v);
    setPage(0);
  };

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const q = new URLSearchParams();
      // The API takes 1-based pages; Pager is 0-based.
      q.set('page', String(page + 1));
      q.set('pageSize', String(pageSize));
      if (search.trim()) q.set('q', search.trim());
      if (source) q.set('source', source);
      if (resolved) q.set('resolved', resolved);

      const d = await api.get<{
        total: number;
        rows: ErrorRow[];
        logTablePresent: boolean;
      }>('/api/error-logs?' + q.toString());

      setRows(d.rows);
      setTotal(d.total);
      setTableMissing(!d.logTablePresent);
    } catch (err) {
      // A missing table produces a 500 whose body says so; surface that text rather
      // than the generic "Request failed (500)".
      if (err instanceof ApiError && /ERROR-LOG\.sql/i.test(err.message)) {
        setTableMissing(true);
        setError(err.message);
      } else {
        setError(err instanceof ApiError ? err.message : 'Could not load the error log');
      }
    } finally {
      setLoading(false);
    }
  }, [page, pageSize, search, source, resolved]);

  useEffect(() => {
    load();
  }, [load]);

  const openDetail = async (row: ErrorRow) => {
    setDetailLoading(true);
    setNote(row.resolutionNote);
    try {
      const d = await api.get<ErrorDetail>(`/api/error-logs/${row.id}`);
      setDetail(d);
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not load the details');
    } finally {
      setDetailLoading(false);
    }
  };

  const toggleResolved = async (row: ErrorRow, next: boolean, resolutionNote: string) => {
    try {
      await api.post(`/api/error-logs/${row.id}/resolve`, {
        resolved: next,
        note: next ? resolutionNote : '',
      });
      toast.success(next ? 'Marked resolved' : 'Reopened');
      if (detail?.id === row.id) setDetail(null);
      load();
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Update failed');
    }
  };

  const saveDetail = async () => {
    if (!detail) return;
    setSaving(true);
    try {
      await api.post(`/api/error-logs/${detail.id}/resolve`, {
        resolved: detail.isResolved,
        note: note.trim(),
      });
      toast.success('Saved');
      load();
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const purge = async () => {
    const days = 30;
    const ok = window.confirm(
      `Delete every error older than ${days} days?\n\nThis cannot be undone. Rows for errors you have not marked resolved will be deleted too.`,
    );
    if (!ok) return;
    try {
      const r = await api.post<{ deleted: number }>('/api/error-logs/purge', {
        olderThanDays: days,
      });
      toast.success(`${r.deleted} old ${r.deleted === 1 ? 'entry' : 'entries'} deleted`);
      setPage(0);
      load();
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Delete failed');
    }
  };

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1 className="page-title">Error Log</h1>
          <p className="page-sub">
            Every database and application error the API hit. Unresolved errors are
            listed first; open one to see the full message and stack trace.
          </p>
        </div>
      </header>

      {tableMissing && (
        <div className="alert error" role="alert">
          <span className="alert-ico"><Icon name="alert" size={15} /></span>
          <span>
            The error log table is missing on this server. Run{' '}
            <b>Backend/sql/ERROR-LOG.sql</b> against the CIP database, then restart the
            API. Until then, errors are still logged to the application log but cannot
            be shown here.
          </span>
        </div>
      )}

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" size={15} />
            <input
              placeholder="Search path, message, object, user or SQL number"
              value={search}
              onChange={(e) => resetPage(setSearch)(e.target.value)}
              aria-label="Search errors"
            />
          </div>

          <select
            className="pager-size"
            aria-label="Filter by source"
            value={source}
            onChange={(e) => resetPage(setSource)(e.target.value)}
          >
            <option value="">All sources</option>
            <option value="Database">Database</option>
            <option value="Application">Application</option>
          </select>

          <select
            className="pager-size"
            aria-label="Filter by status"
            value={resolved}
            onChange={(e) => resetPage(setResolved)(e.target.value)}
          >
            <option value="">All</option>
            <option value="false">Unresolved</option>
            <option value="true">Resolved</option>
          </select>

          <button className="btn small ghost" onClick={load} disabled={loading}>
            <Icon name="refresh" size={14} />
            {loading ? ' Loading…' : 'Refresh'}
          </button>

          <button className="btn small ghost" onClick={purge} disabled={loading}>
            Delete 30-day-old
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
                <th>When</th>
                <th>Source</th>
                <th>Request</th>
                <th>Error</th>
                <th>User</th>
                <th>State</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <tr><td colSpan={7} className="loading">Loading…</td></tr>
              )}
              {!loading && rows.length === 0 && (
                <tr>
                  <td colSpan={7} className="empty">
                    {tableMissing
                      ? 'The error log is not set up on this server yet.'
                      : search || source || resolved
                        ? 'No errors matched these filters.'
                        : 'No errors recorded. That is the good outcome.'}
                  </td>
                </tr>
              )}
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>
                    <div>{formatWhen(r.errorOn)}</div>
                    <div className="muted-note">{r.statusCode ?? '—'}</div>
                  </td>
                  <td>
                    <span className={`badge ${r.source === 'Database' ? 'blue' : 'gray'}`}>
                      {r.source}
                    </span>
                  </td>
                  <td>
                    <div className="muted-note">{r.httpMethod}</div>
                    <div>{r.requestPath}</div>
                  </td>
                  <td>
                    <div className="muted-note">
                      {r.exceptionType}
                      {r.sqlErrorNumber != null ? ` · SQL ${r.sqlErrorNumber}` : ''}
                    </div>
                    <div title={r.message}>{truncate(r.message, 90)}</div>
                    {r.dbObject && <div className="muted-note">{r.dbObject}</div>}
                  </td>
                  <td>
                    <div>{r.empName || '—'}</div>
                    <div className="muted-note">{r.username || r.ipAddress || ''}</div>
                  </td>
                  <td>
                    <span className={`badge ${r.isResolved ? 'green' : 'pending'}`}>
                      {r.isResolved ? 'Resolved' : 'Open'}
                    </span>
                  </td>
                  <td>
                    <button
                      className="btn small ghost"
                      onClick={() => openDetail(r)}
                      disabled={detailLoading}
                    >
                      Details
                    </button>{' '}
                    {r.isResolved ? (
                      <button
                        className="btn small ghost"
                        onClick={() => toggleResolved(r, false, '')}
                      >
                        Reopen
                      </button>
                    ) : (
                      <button
                        className="btn small ghost"
                        onClick={() => {
                          const text = window.prompt(
                            'How was this fixed? (required to mark resolved)',
                          );
                          if (text && text.trim()) toggleResolved(r, true, text.trim());
                        }}
                      >
                        Resolve
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <Pager
          page={page}
          pageSize={pageSize}
          totalRows={total}
          totalPages={totalPages}
          onPage={setPage}
          onPageSize={(s) => {
            setPageSize(s);
            setPage(0);
          }}
        />
      </div>

      {detail && (
        <div className="card">
          <div className="card-head">
            <h3>Error #{detail.id}</h3>
            <button className="btn small ghost" onClick={() => setDetail(null)}>Close</button>
          </div>

          <dl className="detail-list">
            <dt>When</dt><dd>{formatWhen(detail.errorOn)}</dd>
            <dt>Request</dt>
            <dd>{detail.httpMethod} {detail.requestPath}</dd>
            {detail.queryString && <><dt>Query</dt><dd>{detail.queryString}</dd></>}
            <dt>Source</dt><dd>{detail.source}</dd>
            <dt>Exception</dt><dd>{detail.exceptionType || '—'}</dd>
            <dt>SQL number</dt>
            <dd>{detail.sqlErrorNumber ?? '—'}</dd>
            <dt>Object</dt><dd>{detail.dbObject || '—'}</dd>
            <dt>User</dt>
            <dd>
              {detail.empName || '—'}
              {detail.username ? ` (${detail.username})` : ''}
              {detail.empCode != null ? ` · ${detail.empCode}` : ''}
            </dd>
            <dt>IP</dt><dd>{detail.ipAddress || '—'}</dd>
            <dt>Message</dt><dd>{detail.message}</dd>
          </dl>

          {detail.stackTrace && (
            <>
              <h4 className="detail-heading">Stack trace</h4>
              {/* A <pre> keeps the trace readable; collapsing whitespace would join the
                  frames together and destroy the very structure being read. */}
              <pre className="trace-block">{detail.stackTrace}</pre>
            </>
          )}

          <div className="table-toolbar">
            <input
              className="search-box"
              placeholder="Resolution note"
              value={note}
              onChange={(e) => setNote(e.target.value)}
              aria-label="Resolution note"
            />
            <button className="btn small primary" onClick={saveDetail} disabled={saving}>
              {saving ? 'Saving…' : 'Save note'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

/** SQL returns UTC (the column default is SYSUTCDATETIME), so render it as such.
 *  Showing a raw UTC timestamp next to "happened 2 minutes ago" invites the reader
 *  to distrust the whole screen. */
function formatWhen(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString('en-IN', {
    day: '2-digit', month: 'short', year: 'numeric',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }) + ' UTC';
}

function truncate(s: string, max: number): string {
  if (!s) return '—';
  return s.length <= max ? s : s.slice(0, max - 1) + '…';
}