import { useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

/** The 69 spreadsheet headers, in the order TBL_CIP_CLIENT_APPLICATION_DETAILS_Type expects. */
const COLUMNS = [
  'Date of Application', 'Application number', 'Type Of Loan Applied', 'Bank',
  'Login Channel', 'Login Region', 'Loan Scheme', 'Loan Amount', 'Purpose of Loan',
  'Client Name', 'Client Mobile', 'Client DOB', 'PAN', 'Aadhar Number', 'Gender',
  'Maratial Status', 'Spouse Name', 'Spouse Occupation', 'Number of Dependents',
  'Mothers Name', 'Education Qualification', 'University', 'Year of Passing',
  'Personal Email ID', 'Current Address', 'Permanant Address', 'Residence Status',
  'Rent Amount if Rented', 'No of years at current residence', 'No of years in City',
  'Occupation Details', 'Company Name', 'Company Address', 'Office Landline Number',
  'Official Email ID', 'Designation', 'Number of years in Current Company',
  'Total Work Experience', 'Salary Bank', 'Bank Account number', 'Salary Branch',
  'Reference 1 Name', 'Reference 1 Address', 'Reference 1 Contact', 'Reference 2 Name',
  'Reference 2 Address', 'Reference 2 Contact', 'Gross Salary', 'Net Salary',
  'Annual Turnover', 'Net Profit', 'Existing Loan Type', 'Existing Loan Bank',
  'Existing Loan EMI', 'Outstanding Loan Amount', 'Status', 'Sent to Bank Date',
  'Remarks', 'Amount Disbursed', 'Loan offer', 'Disbursal Date', 'Telecaller Name',
  'Supervisor Name', 'Branch', 'CIBIL Score', 'Comments', 'Tenure', 'ExecutiveName',
  'PickUpDate',
];

/**
 * Bulk Insert. Replicates DataEntry_Bulk_CIPController, which loaded the first sheet of a
 * workbook with OleDb and handed it to USP_CIP_DATA_ENTRY. The backend matches headers by
 * name, so column order no longer has to match — but the header names must.
 *
 * The legacy controller's validation block was entirely commented out; rows without an
 * application number are now reported rather than failing silently inside the procedure.
 */
export default function BulkInsertPage() {
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ inserted: number; skipped: number; details: string[] } | null>(null);
  const [error, setError] = useState('');

  const upload = async () => {
    if (!file) { toast.error('Choose a workbook first.'); return; }
    setBusy(true);
    setError('');
    setResult(null);
    try {
      const fd = new FormData();
      fd.append('file', file);
      const res = await api.upload<{ inserted: number; skipped: number; details: string[] }>(
        '/api/applications/bulk', fd);
      setResult(res);
      toast.success(`${res.inserted} row${res.inserted === 1 ? '' : 's'} inserted.`);
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : 'Bulk insert failed.';
      setError(msg);
      toast.error(msg);
    } finally {
      setBusy(false);
    }
  };

  const downloadTemplate = () => {
    // A minimal .xlsx is awkward to hand-roll; CSV opens in Excel and carries the headers.
    const csv = COLUMNS.join(',') + '\n';
    const url = 'data:text/csv;charset=utf-8,' + encodeURIComponent(csv);
    window.location.href = url;
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Bulk Insert</h1>
          <p className="page-sub">
            Load many applications from a spreadsheet. The first sheet is read; columns are matched by
            header name, so their order does not matter.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Upload workbook</h3></div>
        <div className="card-body">
          <div className="filter-grid">
            <div className="field">
              <label className="field-label" htmlFor="bulk-file">Spreadsheet (.xlsx)</label>
              <input id="bulk-file" className="inp" type="file" accept=".xlsx"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              <span className="field-hint">
                {file ? file.name : 'First sheet only. Header row required.'}
              </span>
            </div>
            <div className="filter-actions">
              <button className="btn primary" onClick={upload} disabled={busy || !file}>
                <Icon name="upload" /> {busy ? 'Inserting…' : 'Upload and insert'}
              </button>
              <button className="btn secondary" onClick={downloadTemplate}>
                <Icon name="download" /> Template headers
              </button>
            </div>
          </div>
        </div>
      </div>

      {error && <div className="alert error" role="alert">{error}</div>}

      {result && (
        <div className="card">
          <div className="card-head"><h3>Result</h3></div>
          <div className="card-body">
            <p className="muted-note">
              Inserted <strong>{result.inserted}</strong> row{result.inserted === 1 ? '' : 's'}
              {result.skipped > 0 && <> · skipped <strong>{result.skipped}</strong></>}
            </p>
            {result.details.length > 0 && (
              <ul className="issue-list">
                {result.details.map((d, i) => <li key={i}>{d}</li>)}
              </ul>
            )}
          </div>
        </div>
      )}

      <div className="card">
        <div className="card-head">
          <h3>Expected columns</h3>
          <span className="card-head-note">{COLUMNS.length} columns</span>
        </div>
        <div className="card-body">
          <ol className="col-list">
            {COLUMNS.map((c) => <li key={c}>{c}</li>)}
          </ol>
        </div>
      </div>
    </div>
  );
}