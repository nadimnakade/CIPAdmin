import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, fetchReportPdf, ApiError, getUser } from '../api';
import Icon from '../components/Icon';
import { confirmAction } from '../lib/confirm';

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
  /** Server says a new pull would be refused (403) — lock the request form. */
  blocked: boolean;
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

/// One pull event made by the signed-in user — backs the "My Reports" section.
/// Cached and legacy serves appear here too, because they count as pulls.
interface MyPullRow {
  logId: number;
  reportId: number | null;
  panNo: string;
  applicantName: string | null;
  reportType: string;
  source: string;
  locationName: string | null;
  centerManager: string | null;
  fileName: string | null;
  pullDate: string;
  pullTime: string;
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
  const [myReports, setMyReports] = useState<MyPullRow[]>([]);
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
    api.get<{ total: number; rows: MyPullRow[] }>('/api/cibil/my-reports')
      .then((d) => setMyReports(d.rows))
      .catch(() => {});
  }, []);

  useEffect(() => { refreshQuota(); }, [refreshQuota]);

  // An admin can change a limit while this page sits open, and the quota is only ever
  // read on mount — so the "0 / 10" chip would stay stale indefinitely. Re-read it
  // when the tab regains focus and on a slow poll, and refresh the moment an admin
  // raises this user's cap so the form unlocks without a manual reload.
  useEffect(() => {
    const onFocus = () => refreshQuota();
    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onFocus);
    const poll = window.setInterval(() => {
      if (document.visibilityState === 'visible') refreshQuota();
    }, 60_000);
    return () => {
      window.removeEventListener('focus', onFocus);
      document.removeEventListener('visibilitychange', onFocus);
      window.clearInterval(poll);
    };
  }, [refreshQuota]);

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
    // A disabled <fieldset> stops clicks and typing but NOT Enter-to-submit in every
    // browser, so the out-of-quota case is re-checked here as well.
    if (quotaBlocked) {
      setMessage({ kind: 'err', text: `Monthly pull limit reached (${quota!.used} / ${quota!.effectiveLimit}). Request a limit increase to continue.` });
      setShowRequestPanel(true);
      return;
    }
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
      // The response quota already includes this pull (the server logs the event
      // before building it); re-read the ledger so "My Reports" picks up the new row.
      refreshQuota();

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
    const pan = form.panCard.trim().toUpperCase();
    const ok = await confirmAction({
      title: `Regenerate the report for ${pan}?`,
      text: 'The existing PDF for this month is REPLACED with a fresh pull.\nThis uses one vendor credit.',
      confirmText: 'Regenerate',
      tone: 'danger',
    });
    if (!ok) return;
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
      refreshQuota();
      setMessage({ kind: 'ok', text: res.message });
    } catch (err) {
      setMessage({ kind: 'err', text: err instanceof ApiError ? err.message : 'Regeneration failed' });
    } finally {
      setLoading(false);
    }
  };

  const downloadMy = async (reportId: number, fileName: string) => {
    try {
      const blob = await fetchReportPdf(reportId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (e) {
      setMessage({ kind: 'err', text: e instanceof Error ? e.message : 'Download failed' });
    }
  };

  const quotaPct = quota && quota.effectiveLimit > 0 ? Math.min(100, Math.round((quota.used / quota.effectiveLimit) * 100)) : 0;

  // Admins are exempt from the cap. Everyone else is locked out once the server
  // says the allowance is spent, so the form greys out instead of letting them
  // fill it in and then bounce off a 403.
  const quotaBlocked = !!quota && !isAdmin && (quota.blocked || quota.remaining <= 0);

  return (
    <div className="page page-narrow">
      <div className="center-title">
        <h1>CIBIL Report Fetcher</h1>
        <p className="page-sub">Enter details below to generate a CIBIL PDF report</p>
      </div>

      {quota && !isAdmin && (
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

      {quotaBlocked && (
        <div className="alert warn" style={{ marginBottom: 12 }}>
          <b>Monthly pull limit reached ({quota!.used} / {quota!.effectiveLimit}).</b>{' '}
          The CIBIL request form is disabled until an admin raises your limit or the
          new month starts. {pendingRequest
            ? <>Your request for <b>{pendingRequest.requestedLimit} pulls</b> is pending approval.</>
            : 'Use “Request Limit Increase” below to ask for more.'}
        </div>
      )}

      <form className="card form-card" onSubmit={submit}>
        {/* Fields lock while a pull is in flight, and stay locked all month once the
            allowance is spent — no edits or re-submits mid-generation. */}
        <fieldset className="form-fields" disabled={loading || quotaBlocked}>
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

        <button className="btn primary full" disabled={loading || quotaBlocked}>
          {loading
            ? (<><span className="spinner" aria-hidden /> Generating report — please wait…</>)
            : quotaBlocked ? 'Limit reached — request an increase below' : 'Generate Report'}
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

      {myReports.length > 0 && (
        <div className="card my-reports">
          <div className="card-head">
            <h3>My Reports</h3>
            <span className="scope-chip">{myReports.length} pull{myReports.length === 1 ? '' : 's'}</span>
          </div>
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>PAN</th>
                <th>Type</th>
                <th>Source</th>
                <th>Location</th>
                <th>CIBIL Pull Date</th>
                <th>CIBIL Pull Time</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {myReports.map((r) => (
                <tr key={r.logId}>
                  <td>{r.applicantName || '—'}</td>
                  <td className="mono">{r.panNo}</td>
                  <td><span className="badge blue">{r.reportType}</span></td>
                  <td>
                    <span className={'badge ' + (r.source === 'API' || r.source === 'REGEN' ? 'green' : 'gray')}>
                      {r.source}
                    </span>
                  </td>
                  <td>{r.locationName || '—'}</td>
                  <td>{r.pullDate}</td>
                  <td className="mono">{r.pullTime}</td>
                  <td>
                    {r.reportId ? (
                      <button
                        className="btn small"
                        onClick={() => downloadMy(r.reportId as number, r.fileName || `${r.panNo}.pdf`)}
                      >
                        Download
                      </button>
                    ) : (
                      <span className="empty">—</span>
                    )}
                  </td>
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
