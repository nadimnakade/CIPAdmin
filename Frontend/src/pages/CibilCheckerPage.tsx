import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, fetchReportPdf, ApiError, getUser } from '../api';
import Icon from '../components/Icon';

interface CibilReportInfo {
  reportId: number;
  panNo: string;
  applicantName: string | null;
  mobile: string | null;
  reportType: string;
  fileName: string;
  fileSize: number | null;
  pulledByName: string | null;
  pulledDate: string;
  source: string;
}

interface QuotaInfo {
  used: number;
  limit: number;
  remaining: number;
  effectiveLimit: number;
  approvedLimit: string | null;
}

interface LimitRequestInfo {
  requestId: number;
  requestMonth: string;
  currentCount: number;
  requestedLimit: number;
  reason: string | null;
  status: 'Pending' | 'Approved' | 'Rejected';
  approvedLimit: number | null;
  decidedByName: string | null;
  createdDate: string;
}

interface GenerateResponse {
  source: 'CACHE' | 'API' | 'REGEN' | 'LEGACY';
  report: CibilReportInfo;
  downloadUrl: string;
  message: string;
  quota?: QuotaInfo | null;
}

const thisMonthKey = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
};

export default function CibilCheckerPage() {
  const user = getUser();
  const isAdmin = user?.roleId === 1 || user?.roleId === 2;

  const [form, setForm] = useState({
    name: '',
    mobile: '',
    panCard: '',
    gender: 'male',
    reportType: 'cibil',
    consent: 'Y',
  });
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'warn' | 'err'; text: string } | null>(null);
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [pdfName, setPdfName] = useState<string | null>(null);

  const [quota, setQuota] = useState<QuotaInfo | null>(null);
  const [myRequests, setMyRequests] = useState<LimitRequestInfo[]>([]);
  const [showRequestPanel, setShowRequestPanel] = useState(false);
  const [reqLimit, setReqLimit] = useState('');
  const [reqReason, setReqReason] = useState('');
  const [reqError, setReqError] = useState('');
  const [reqSaving, setReqSaving] = useState(false);
  const [panOnFile, setPanOnFile] = useState(false);

  const pendingRequest = myRequests.find((r) => r.status === 'Pending' && r.requestMonth === thisMonthKey());

  const refreshQuota = useCallback(() => {
    api.get<QuotaInfo>('/api/cibil/quota').then(setQuota).catch(() => {});
    api.get<LimitRequestInfo[]>('/api/cibil/my-limit-requests').then(setMyRequests).catch(() => {});
  }, []);

  useEffect(() => { refreshQuota(); }, [refreshQuota]);

  // Surface the limit-request panel automatically when this month's pulls are used up.
  useEffect(() => {
    if (quota && quota.remaining <= 0) setShowRequestPanel(true);
  }, [quota]);

  useEffect(() => {
    return () => {
      if (pdfUrl) URL.revokeObjectURL(pdfUrl);
    };
  }, [pdfUrl]);

  const set = (k: string, v: string) => setForm((f) => ({ ...f, [k]: v }));

  const showPdf = async (reportId: number, fileName: string, autoDownload: boolean) => {
    const blob = await fetchReportPdf(reportId);
    if (pdfUrl) URL.revokeObjectURL(pdfUrl);
    const url = URL.createObjectURL(blob);
    setPdfUrl(url);
    setPdfName(fileName);
    if (autoDownload) {
      setTimeout(() => {
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        a.click();
      }, 500);
    }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (loading) return; // Enter-key resubmit guard (disabled button alone doesn't block this)
    setMessage(null);
    setShowRequestPanel(false);

    // Validate BEFORE entering the loading state so errors appear instantly.
    const pan = form.panCard.trim().toUpperCase();
    if (!pan) {
      setMessage({ kind: 'err', text: 'PAN is mandatory.' });
      return;
    }
    if (!/^[A-Z]{5}[0-9]{4}[A-Z]$/.test(pan)) {
      setMessage({ kind: 'err', text: 'Enter a valid PAN (e.g. AHAPN0789J).' });
      return;
    }

    setLoading(true);
    try {
      const res = await api.post<GenerateResponse>(`/api/cibil/generate`, {
        name: form.name.trim(),
        mobile: form.mobile.trim(),
        panCard: pan,
        gender: form.gender,
        reportType: form.reportType,
        consent: form.consent,
      });

      await showPdf(res.report.reportId, res.report.fileName, false);
      setPanOnFile(res.source === 'CACHE' || res.source === 'LEGACY');
      if (res.quota) setQuota(res.quota);

      if (res.source === 'CACHE' || res.source === 'LEGACY') {
        setMessage({ kind: 'ok', text: res.message });
      } else if (res.source === 'REGEN') {
        setMessage({ kind: 'ok', text: res.message });
      } else {
        setMessage({ kind: 'ok', text: `Report generated & saved as ${res.report.fileName}` });
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        // Quota exceeded — surface quota info, the existing PAN PDF (if any) and the request panel.
        if (err.body?.quota) setQuota(err.body.quota);
        if (err.body?.existingReport) {
          await showPdf(err.body.existingReport.reportId, err.body.existingReport.fileName, false);
          setPanOnFile(true);
        }
        setMessage({ kind: 'warn', text: err.message });
        setShowRequestPanel(true);
      } else {
        setMessage({ kind: 'err', text: err instanceof ApiError ? err.message : 'Failed to fetch report' });
      }
    } finally {
      setLoading(false);
    }
  };

  const submitLimitRequest = async (e: FormEvent) => {
    e.preventDefault();
    setReqSaving(true);
    setReqError('');
    try {
      await api.post('/api/cibil/limit-request', {
        requestedLimit: Number(reqLimit),
        reason: reqReason.trim() || null,
      });
      setShowRequestPanel(false);
      setReqLimit('');
      setReqReason('');
      setMessage({ kind: 'ok', text: 'Limit request submitted — the administrator will review it.' });
      refreshQuota();
    } catch (err) {
      setReqError(err instanceof ApiError ? err.message : 'Failed to submit request');
    } finally {
      setReqSaving(false);
    }
  };

  const regenerate = async () => {
    if (!confirm(`Regenerate the CIBIL report for ${form.panCard.trim().toUpperCase()}?\n\nThe existing PDF for this month will be REPLACED with a fresh pull (uses one vendor credit).`)) return;
    setMessage(null);
    setLoading(true);
    try {
      const pan = form.panCard.trim().toUpperCase();
      const res = await api.post<GenerateResponse>(`/api/cibil/generate?regenerate=true`, {
        name: form.name.trim(),
        mobile: form.mobile.trim(),
        panCard: pan,
        gender: form.gender,
        reportType: form.reportType,
        consent: form.consent,
      });
      await showPdf(res.report.reportId, res.report.fileName, false);
      if (res.quota) setQuota(res.quota);
      setPanOnFile(true);
      setMessage({ kind: 'ok', text: res.message });
    } catch (err) {
      setMessage({ kind: 'err', text: err instanceof ApiError ? err.message : 'Regeneration failed' });
    } finally {
      setLoading(false);
    }
  };

  const quotaPct = quota && quota.effectiveLimit > 0 ? Math.min(100, Math.round((quota.used / quota.effectiveLimit) * 100)) : 0;

  return (
    <div className="page page-narrow">
      <div className="center-title">
        <h1>CIBIL Report Fetcher</h1>
        <p className="page-sub">Enter details below to generate a CIBIL PDF report</p>
      </div>

      {quota && (
        <div className="quota-chip card">
          <div className="quota-text">
            <span className="quota-count"><b>{quota.used}</b> / {quota.effectiveLimit}</span>
            <span className="quota-label">pulls used this month{quota.approvedLimit ? ` (limit raised to ${quota.approvedLimit} by admin)` : ''}</span>
          </div>
          <div className="quota-bar"><div className={'quota-fill' + (quotaPct >= 100 ? ' full' : quotaPct >= 80 ? ' high' : '')} style={{ transform: `scaleX(${quotaPct / 100})` }} /></div>
          {pendingRequest ? (
            <span className="badge pending">Limit request pending: {pendingRequest.requestedLimit}</span>
          ) : (
            <button type="button" className="btn secondary small" onClick={() => setShowRequestPanel(true)}>
              <Icon name="bell" size={14} /> Request Limit Increase
            </button>
          )}
        </div>
      )}

      <form className="card form-card" onSubmit={submit}>
        {/* Fields lock while a pull is in flight — no edits or re-submits mid-generation. */}
        <fieldset className="form-fields" disabled={loading}>
        <label className="field">
          <span className="field-label">FULL NAME</span>
          <input
            placeholder="Enter full name"
            value={form.name}
            onChange={(e) => set('name', e.target.value)}
            required
          />
        </label>

        <div className="field-row">
          <label className="field">
            <span className="field-label">MOBILE</span>
            <input
              placeholder="Enter mobile number"
              value={form.mobile}
              onChange={(e) => set('mobile', e.target.value.replace(/\D/g, '').slice(0, 10))}
              required
            />
          </label>
          <label className="field">
            <span className="field-label">PAN CARD</span>
            <input
              placeholder="Enter PAN number"
              value={form.panCard}
              onChange={(e) => set('panCard', e.target.value.toUpperCase().slice(0, 10))}
              pattern="[A-Za-z]{5}[0-9]{4}[A-Za-z]"
              title="10-character PAN, e.g. AHAPN0789J"
              required
            />
          </label>
        </div>

        <div className="field-row">
          <label className="field">
            <span className="field-label">GENDER</span>
            <select value={form.gender} onChange={(e) => set('gender', e.target.value)}>
              <option value="male">Male</option>
              <option value="female">Female</option>
            </select>
          </label>
          <label className="field">
            <span className="field-label">REPORT TYPE</span>
            <select value={form.reportType} onChange={(e) => set('reportType', e.target.value)}>
              <option value="cibil">CIBIL</option>
              <option value="experian">Experian</option>
              <option value="equifax">Equifax</option>
              <option value="crif">CRIF</option>
            </select>
          </label>
        </div>

        <label className="field">
          <span className="field-label">CONSENT</span>
          <select value={form.consent} onChange={(e) => set('consent', e.target.value)}>
            <option value="Y">Yes (Y)</option>
            <option value="N">No (N)</option>
          </select>
        </label>

        {message && <div className={`alert ${message.kind}`}>{message.text}</div>}

        <button className="btn primary full" disabled={loading}>
          {loading ? (<><span className="spinner" aria-hidden /> Generating report — please wait…</>) : 'Generate Report'}
        </button>
        </fieldset>

        {isAdmin && panOnFile && (
          <button type="button" className="btn warn full" onClick={regenerate} disabled={loading}>
            <Icon name="edit" size={16} /> Regenerate (Admin) — replaces the saved PDF
          </button>
        )}
      </form>

      {showRequestPanel && (
        <div className="card limit-request-panel">
          <div className="card-head">
            <h3>Request a higher monthly limit</h3>
          </div>
          {pendingRequest ? (
            <div className="alert ok">
              Your request for <b>{pendingRequest.requestedLimit} pulls</b> is pending admin approval.
            </div>
          ) : (
            <form onSubmit={submitLimitRequest}>
              <div className="field-row">
                <label className="field">
                  <span className="field-label">REQUESTED LIMIT</span>
                  <input
                    type="number"
                    min={1}
                    max={100000}
                    placeholder="e.g. 100"
                    value={reqLimit}
                    onChange={(e) => setReqLimit(e.target.value)}
                    required
                  />
                </label>
                <label className="field">
                  <span className="field-label">REASON</span>
                  <input
                    placeholder="Why do you need more pulls?"
                    value={reqReason}
                    onChange={(e) => setReqReason(e.target.value)}
                  />
                </label>
              </div>
              {reqError && <div className="alert error">{reqError}</div>}
              <button className="btn primary" disabled={reqSaving || !reqLimit}>
                {reqSaving ? 'Submitting…' : 'Submit Request to Administrator'}
              </button>
            </form>
          )}
        </div>
      )}

      {myRequests.filter((r) => r.requestMonth === thisMonthKey() && r.status !== 'Pending').length > 0 && (
        <div className="card my-requests">
          <div className="card-head"><h3>Your limit requests this month</h3></div>
          <table className="data-table">
            <thead>
              <tr><th>Requested</th><th>Status</th><th>Approved</th><th>Decided by</th></tr>
            </thead>
            <tbody>
              {myRequests.filter((r) => r.requestMonth === thisMonthKey() && r.status !== 'Pending').map((r) => (
                <tr key={r.requestId}>
                  <td>{r.requestedLimit}</td>
                  <td><span className={'badge ' + (r.status === 'Approved' ? 'green' : 'gray')}>{r.status}</span></td>
                  <td>{r.approvedLimit ?? '—'}</td>
                  <td>{r.decidedByName || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {pdfUrl && (
        <div className="card pdf-card">
          <div className="card-head">
            <h3>Report Preview {pdfName ? `— ${pdfName}` : ''}</h3>
            <a className="btn secondary" href={pdfUrl} download={pdfName || 'report.pdf'}>
              Download PDF
            </a>
          </div>
          <iframe title="CIBIL report" src={pdfUrl} className="pdf-frame" />
        </div>
      )}
    </div>
  );
}
