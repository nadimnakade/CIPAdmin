import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { Pager } from '../components/tableView';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

interface Row {
  id: number; appNo: string; bankName: string; loanName: string; loanSchemeName: string;
  loanAmnt: string; purposeOfLoan: string; loginChannelName: string; loginRegionName: string;
  clientName: string; clientMblNo: string; clientPan: string; gender: string;
  maritatalStatus: string; telCallerName: string; supervisorName: string;
  appStatusName: string; losNo: string; appDate: string; statusDesc: string;
  updatedOn: string; remarks: string; addDocReq: string; amntDisbursed: string;
}
interface Option { id: number; name: string }

/**
 * Search Application. Replicates CIP_SearchAndEditController.get_ClientDetails —
 * the same paged procedure, the same six filters, and the same row actions (edit,
 * attachments, delete). The legacy screen called this "Incomplete File Client Details".
 */
export default function ApplicationsPage() {
  const navigate = useNavigate();
  const [rows, setRows] = useState<Row[]>([]);
  const [total, setTotal] = useState(0);
  // Zero-based: the shared <Pager> is zero-based (its "First" button emits 0), while the
  // API takes a 1-based page. page + 1 is what goes on the wire.
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(25);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [search, setSearch] = useState('');
  const [bank, setBank] = useState('');
  const [region, setRegion] = useState('');
  const [status, setStatus] = useState('');
  const [appDate, setAppDate] = useState('');

  const [banks, setBanks] = useState<Option[]>([]);
  const [regions, setRegions] = useState<Option[]>([]);
  const [statuses, setStatuses] = useState<Option[]>([]);

  useEffect(() => {
    api.get<Record<string, Option[]>>('/api/applications/dropdowns')
      .then((d) => {
        setBanks(d.banks ?? []);
        setRegions(d.loginRegions ?? []);
        setStatuses(d.applicationStatuses ?? []);
      })
      .catch(() => { /* filters degrade to free text */ });
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const q = new URLSearchParams();
      if (search.trim()) q.set('search', search.trim());
      if (bank) q.set('bank', bank);
      if (region) q.set('loginRegion', region);
      if (status) q.set('status', status);
      if (appDate) q.set('appDate', appDate);
      q.set('page', String(page + 1));
      q.set('pageSize', String(pageSize));
      const d = await api.get<{ total: number; rows: Row[] }>('/api/applications?' + q.toString());
      setRows(d.rows);
      setTotal(d.total);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not load applications.');
      setRows([]);
    } finally {
      setLoading(false);
    }
  }, [search, bank, region, status, appDate, page, pageSize]);

  useEffect(() => { load(); }, [load]);

  const resetPage = <T,>(setter: (v: T) => void) => (v: T) => { setter(v); setPage(0); };

  const remove = async (row: Row) => {
    const ok = await confirmAction({
      title: 'Delete application',
      text: `Application ${row.appNo} for ${row.clientName} will be removed. This cannot be undone.`,
      confirmText: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await api.delete(`/api/applications/${row.id}`);
      toast.success('Application deleted.');
      load();
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : 'Delete failed.');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Search Application</h1>
          <p className="page-sub">
            {loading ? 'Searching…' : `${total.toLocaleString('en-IN')} application${total === 1 ? '' : 's'} found`}
          </p>
        </div>
        <div className="head-actions">
          <Link className="btn secondary" to="/applications/new"><Icon name="plus" /> New Application</Link>
          <Link className="btn secondary" to="/applications/scans"><Icon name="upload" /> Scan Upload</Link>
          <Link className="btn secondary" to="/applications/bulk"><Icon name="upload" /> Bulk Insert</Link>
          <Link className="btn secondary" to="/applications/existing-loans"><Icon name="wallet" /> Existing Loans</Link>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Filters</h3></div>
        <div className="card-body">
          <div className="filter-grid">
            <div className="field">
              <label className="field-label" htmlFor="f-search">Search</label>
              <input id="f-search" className="inp" value={search}
                onChange={(e) => resetPage(setSearch)(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') load(); }}
                placeholder="Application no, client, mobile, PAN…" />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="f-bank">Bank</label>
              <select id="f-bank" className="inp" value={bank} onChange={(e) => resetPage(setBank)(e.target.value)}>
                <option value="">All banks</option>
                {banks.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
              </select>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="f-region">Login Region</label>
              <select id="f-region" className="inp" value={region} onChange={(e) => resetPage(setRegion)(e.target.value)}>
                <option value="">All regions</option>
                {regions.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
              </select>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="f-status">Status</label>
              <select id="f-status" className="inp" value={status} onChange={(e) => resetPage(setStatus)(e.target.value)}>
                <option value="">All statuses</option>
                {statuses.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="f-date">Application Date</label>
              <input id="f-date" className="inp" value={appDate}
                onChange={(e) => resetPage(setAppDate)(e.target.value)}
                placeholder="e.g. 2018-07-22" />
            </div>
            <div className="filter-actions">
              <button className="btn primary" onClick={load} disabled={loading}><Icon name="search" /> Search</button>
              <button className="btn secondary" onClick={() => {
                setSearch(''); setBank(''); setRegion(''); setStatus(''); setAppDate(''); setPage(0);
              }}><Icon name="refresh" /> Reset</button>
            </div>
          </div>
        </div>
      </div>

      {error && <div className="alert error" role="alert">{error}</div>}

      <div className="card">
        <div className="card-head">
          <h3>Applications</h3>
          <span className="card-head-note">{rows.length} on this page</span>
        </div>
        <div className="table-wrap">
          <table className="data-table app-grid">
            <thead>
              <tr>
                <th>Application No.</th>
                <th>Client</th>
                <th>Mobile</th>
                <th>Bank / Loan</th>
                <th className="num">Amount</th>
                <th>Status</th>
                <th>Applied</th>
                <th>Updated</th>
                <th className="num">Actions</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td className="nowrap">{r.appNo}</td>
                  <td>
                    <div className="cell-strong">{r.clientName}</div>
                    {r.clientPan && <div className="cell-sub">{r.clientPan}</div>}
                  </td>
                  <td className="nowrap">{r.clientMblNo}</td>
                  <td>
                    <div className="cell-strong">{r.bankName}</div>
                    <div className="cell-sub">{r.loanName}{r.loanSchemeName ? ' · ' + r.loanSchemeName : ''}</div>
                  </td>
                  <td className="num nowrap">{r.loanAmnt ? Number(r.loanAmnt).toLocaleString('en-IN') : '—'}</td>
                  <td><span className="badge blue">{r.appStatusName || '—'}</span></td>
                  <td className="nowrap">{r.appDate}</td>
                  <td className="nowrap">{r.updatedOn}</td>
                  <td className="num nowrap">
                    <button className="icon-btn" title="Edit" aria-label={`Edit ${r.appNo}`}
                      onClick={() => navigate(`/applications/${r.id}`)}><Icon name="edit" /></button>
                    <button className="icon-btn" title="Attachments" aria-label={`Attachments for ${r.appNo}`}
                      onClick={() => navigate(`/applications/${r.id}?tab=attachments`)}><Icon name="eye" /></button>
                    <button className="icon-btn danger" title="Delete" aria-label={`Delete ${r.appNo}`}
                      onClick={() => remove(r)}><Icon name="trash" /></button>
                  </td>
                </tr>
              ))}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={9} className="empty-cell">No applications match these filters.</td></tr>
              )}
            </tbody>
          </table>
        </div>
        <div className="card-foot">
          <Pager page={page} pageSize={pageSize}
            totalRows={total} totalPages={Math.max(1, Math.ceil(total / pageSize))}
            onPage={setPage} onPageSize={(s) => { setPageSize(s); setPage(0); }} />
        </div>
      </div>
    </div>
  );
}