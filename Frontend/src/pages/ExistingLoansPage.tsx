import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

interface Row {
  appNo: string; existingLoan: string; existingLoanBank: string;
  existingLoanEmi: string; outstandingLoanAmt: string;
}

const TEMPLATE = 'Application number,Existing Loan Type,Existing Loan Bank,Existing Loan EMI,Outstanding Loan Amount\n';

/**
 * Existing Loan Details. Replicates CIP_ExistingLoanDetController: the same five-column
 * CSV, the same two pre-checks (blank loan type, duplicate application+type+bank) and the
 * same insert into USP_CIP_EXISTINGLOAN_DETAILS.
 *
 * The legacy controller answered a failed upload by streaming an Error.csv download from
 * inside the response; the same information is returned as a readable list instead.
 */
export default function ExistingLoansPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [details, setDetails] = useState<string[]>([]);
  const [file, setFile] = useState<File | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const d = await api.get<{ rows: Row[] }>('/api/applications/existing-loans');
      setRows(d.rows ?? []);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not load existing loans.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const upload = async () => {
    if (!file) { toast.error('Choose a CSV first.'); return; }
    setBusy(true);
    setError('');
    setDetails([]);
    try {
      const fd = new FormData();
      fd.append('file', file);
      const res = await api.upload<{ inserted: number }>('/api/applications/existing-loans', fd);
      toast.success(`${res.inserted} row${res.inserted === 1 ? '' : 's'} inserted.`);
      setFile(null);
      load();
    } catch (e) {
      if (e instanceof ApiError) {
        setError(e.message);
        // The API returns the offending rows alongside the message.
        try {
          const parsed = JSON.parse((e as unknown as { body?: string }).body ?? '');
          if (Array.isArray(parsed?.details)) setDetails(parsed.details);
        } catch { /* message only */ }
      } else {
        setError('Upload failed.');
      }
      toast.error('Upload failed.');
    } finally {
      setBusy(false);
    }
  };

  const downloadTemplate = () => {
    window.location.href = 'data:text/csv;charset=utf-8,' + encodeURIComponent(TEMPLATE);
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Existing Loan Details</h1>
          <p className="page-sub">
            Record every existing loan against an application. The whole file is rejected if any row
            is blank or duplicated, so nothing is half-imported.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Upload CSV</h3></div>
        <div className="card-body">
          <div className="filter-grid">
            <div className="field">
              <label className="field-label" htmlFor="el-file">CSV file</label>
              <input id="el-file" className="inp" type="file" accept=".csv"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              <span className="field-hint">{file ? file.name : 'Five columns, header row required.'}</span>
            </div>
            <div className="filter-actions">
              <button className="btn primary" onClick={upload} disabled={busy || !file}>
                <Icon name="upload" /> {busy ? 'Uploading…' : 'Upload and insert'}
              </button>
              <button className="btn secondary" onClick={downloadTemplate}>
                <Icon name="download" /> Template
              </button>
            </div>
          </div>
          <p className="muted-note">
            Columns: Application number, Existing Loan Type, Existing Loan Bank, Existing Loan EMI,
            Outstanding Loan Amount.
          </p>
        </div>
      </div>

      {error && <div className="alert error" role="alert">{error}</div>}
      {details.length > 0 && (
        <div className="card">
          <div className="card-head"><h3>Rows to fix</h3><span className="card-head-note">{details.length}</span></div>
          <div className="card-body">
            <ul className="issue-list">{details.map((d, i) => <li key={i}>{d}</li>)}</ul>
          </div>
        </div>
      )}

      <div className="card">
        <div className="card-head">
          <h3>Stored details</h3>
          <span className="card-head-note">{rows.length} row{rows.length === 1 ? '' : 's'}</span>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th>Application number</th>
                <th>Existing Loan Type</th>
                <th>Existing Loan Bank</th>
                <th className="num">Existing Loan EMI</th>
                <th className="num">Outstanding Loan Amount</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i}>
                  <td>{r.appNo}</td>
                  <td>{r.existingLoan}</td>
                  <td>{r.existingLoanBank}</td>
                  <td className="num">{r.existingLoanEmi}</td>
                  <td className="num">{r.outstandingLoanAmt}</td>
                </tr>
              ))}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={5} className="empty-cell">No existing-loan details recorded yet.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}