import { FormEvent, useCallback, useEffect, useRef, useState } from 'react';
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

/// Latest pull attempt for a PAN, from POST /api/cibil/check and the 409
/// DUPLICATE_PULL body. A previous attempt this month — success OR failure —
/// freezes the PAN; only an admin may re-pull. errorMessage is sent to admins only.
interface PullAttemptInfo {
  logId: number;
  panNo: string;
  reportType: string;
  status: 'OK' | 'FAILED' | 'PENDING' | string;
  source: string;
  requestedBy: string | null;
  pulledDate: string;
  errorMessage: string | null;
}

interface CheckResponse {
  /** A report for this PAN exists this month (pulled by ANY user) — Generate will
      serve it from cache. Informational: it does NOT mean the caller pulled it. */
  exists: boolean;
  report: CibilReportInfo | null;
  lastAttempt: PullAttemptInfo | null;
  /** A previous attempt by THIS USER this month (any status) blocks a fresh pull.
      Scoped server-side per user — a colleague's failed attempt never blocks us. */
  blocked: boolean;
  /** The signed-in user already pulled this PAN this month (directly or from cache).
      Only then is the "already generated — download?" popup shown: another user's
      pull must not interrupt a first-time puller with a popup meant for its owner. */
  pulledByMe: boolean;
  canRePull: boolean;
  message: string | null;
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
  /** OK | FAILED | PENDING. A FAILED row has no reportId, so it cannot be downloaded. */
  status: string;
  /** Vendor failure text — the server sends it to admins only. */
  errorMessage: string | null;
  locationName: string | null;
  centerManager: string | null;
  fileName: string | null;
  pullDate: string;
  pullTime: string;
}

/** Canonical PAN shape — 5 letters, 4 digits, 1 letter. Input is uppercased first. */
const PAN_RE = /^[A-Z]{5}[0-9]{4}[A-Z]$/;

/** Ledger status -> badge class. FAILED gets its own colour so a failed pull can be
    told apart from a successful one at a glance in "My Reports". */
const statusBadge = (s: string) =>
  s === 'OK' ? 'badge green' : s === 'PENDING' ? 'badge pending' : 'badge red';

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
  /* `disabled={reqSaving}` alone does NOT prevent a double submit. setState is not
     applied synchronously, so a second click landing in the same frame still reads
     reqSaving === false and fires a second POST. A ref flips immediately, which is
     what actually closes the window. (submit() guards this the same way with
     `if (loading) return` for Enter-to-submit.) */
  const reqSavingRef = useRef(false);
  const [panOnFile, setPanOnFile] = useState(false);
  /* Server-declared duplicate-pull freeze for the PAN currently in the form: a
     previous attempt THIS month (success or failure) blocks fresh pulls, and only
     an admin re-pull may proceed. Carries the server's message — which includes the
     failure reason when the caller is an admin. */
  const [blocked, setBlocked] = useState<{ message: string; attempt: PullAttemptInfo | null } | null>(null);
  /* Inline PAN-format error, written when the PAN field loses focus. Validation lives
     on blur rather than on the Pull button, so a bad PAN is reported the moment the
     user finishes typing instead of after a click that never reaches the server. */
  const [panError, setPanError] = useState<string | null>(null);
  /* Persistent Pull lock. Latched the instant a pull is initiated and deliberately NOT
     cleared when the API answers — success and failure both leave the button disabled,
     so the same PAN can never be fired twice by accident. Only Refresh releases it. */
  const [pullInitiated, setPullInitiated] = useState(false);
  /* React state is not applied synchronously, so `disabled={pullInitiated}` alone would
     still let a second click in the same frame slip through. The ref flips before any
     await, which is what actually closes the window (same reasoning as reqSavingRef). */
  const pullInitiatedRef = useRef(false);
  /* Last completed /api/cibil/check, keyed by PAN + reportType. The blur handler reuses
     it rather than posting twice when the change-effect just ran for the same value. It
     is dropped the moment a pull starts, because a pull is exactly what makes it stale. */
  const checkCacheRef = useRef<{ key: string; data: CheckResponse } | null>(null);

  const pendingRequest = myRequests.find((r) => r.status === 'Pending' && r.requestMonth === thisMonthKey());

  // "My Reports" lists every attempt, successes included — the chip must not call a
  // failed attempt a pull, so successes and failures are counted separately.
  const failedPulls = myReports.filter((r) => r.status !== 'OK').length;
  const okPulls = myReports.length - failedPulls;

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

  /** Read — or reuse the just-read — server state for a PAN: does a report exist, and
      is the PAN frozen this month? Shared by the change-effect and the blur handler. */
  const runCheck = async (pan: string, reportType: string): Promise<CheckResponse> => {
    const key = `${pan}|${reportType}`;
    const hit = checkCacheRef.current;
    if (hit && hit.key === key) return hit.data;
    const data = await api.post<CheckResponse>('/api/cibil/check', { pan, reportType });
    checkCacheRef.current = { key, data };
    return data;
  };

  const applyCheck = (d: CheckResponse) => {
    setBlocked(d.blocked
      ? { message: d.message || 'This PAN has already been attempted this month.', attempt: d.lastAttempt }
      : null);
    setPanOnFile(d.exists);
  };

  // Freeze pre-check: tell the user the PAN is already attempted/frozen BEFORE they
  // click Generate (a failed attempt leaves no report, so the old code only ever
  // discovered this after burning another vendor credit). Also keeps panOnFile in
  // step so the admin re-pull button appears as soon as the PAN is on file.
  useEffect(() => {
    const pan = form.panCard.trim().toUpperCase();
    if (!PAN_RE.test(pan)) {
      setBlocked(null);
      setPanOnFile(false);
      return;
    }
    let cancelled = false;
    runCheck(pan, form.reportType)
      .then((d) => { if (!cancelled) applyCheck(d); })
      .catch(() => { /* pre-check is advisory — Generate re-validates server-side */ });
    return () => { cancelled = true; };
  }, [form.panCard, form.reportType]);

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

  // PAN validation runs HERE — when the user finishes typing — not on the Pull button.
  // Bad format stops at an inline error and goes no further; a PAN this USER already
  // pulled this month offers the cached file instead of pulling again; a clean PAN —
  // including one a colleague pulled — simply leaves the normal pull flow ready for
  // the Generate button (the server serves it from cache and counts one pull).
  const handlePanBlur = async () => {
    const pan = form.panCard.trim().toUpperCase();
    if (!pan) {
      setPanError('PAN is mandatory.');
      return;
    }
    if (!PAN_RE.test(pan)) {
      setPanError('Enter a valid PAN (e.g. AHAPN0789J)');
      return;
    }
    setPanError(null);
    try {
      const d = await runCheck(pan, form.reportType);
      applyCheck(d);
      // Popup is per user (pulledByMe): only the caller's own pull this month offers
      // the file back. A report someone else pulled is still served from cache by
      // Generate — it just doesn't show a dialog meant for the other user, and the
      // pull is counted for THIS user when they press Generate.
      if (d.exists && d.report && d.pulledByMe) {
        const ok = await confirmAction({
          title: 'CIBIL report already exists',
          text: 'CIBIL for this PAN is already generated. Do you want to download the file?',
          confirmText: 'Download',
          cancelText: 'Cancel',
        });
        // Cancel changes nothing: no pull, no state move.
        if (ok) await showPdf(d.report.reportId, d.report.fileName, true);
      }
    } catch {
      /* pre-check is advisory — the server re-validates when a pull actually runs */
    }
  };

  // The only release valve for the persistent Pull lock. Everything the lock touched
  // goes back to neutral, and the server is re-read so the freeze banner / admin
  // re-pull button reflect what is true NOW rather than what was true before the pull.
  const refreshForm = () => {
    pullInitiatedRef.current = false;
    setPullInitiated(false);
    checkCacheRef.current = null;
    setPanError(null);
    setMessage(null);
    const pan = form.panCard.trim().toUpperCase();
    if (!PAN_RE.test(pan)) {
      setBlocked(null);
      setPanOnFile(false);
    } else {
      runCheck(pan, form.reportType).then(applyCheck).catch(() => { /* advisory */ });
    }
    refreshQuota();
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (loading) return; // Enter-key resubmit guard (disabled button alone doesn't block this)
    if (pullInitiatedRef.current) return; // persistent lock — only Refresh releases it
    // A disabled <fieldset> stops clicks and typing but NOT Enter-to-submit in every
    // browser, so the out-of-quota case is re-checked here as well.
    if (quotaBlocked) {
      setMessage({ kind: 'err', text: `Monthly pull limit reached (${quota!.used} / ${quota!.effectiveLimit}). Request a limit increase to continue.` });
      setShowRequestPanel(true);
      return;
    }

    // Latch the lock BEFORE the request leaves: the button stays disabled through the
    // response — success or failure — and only the Refresh button clears it.
    pullInitiatedRef.current = true;
    setPullInitiated(true);
    checkCacheRef.current = null; // a pull invalidates the cached pre-check

    setMessage(null);
    setShowRequestPanel(false);

    // PAN format is decided on blur (inline, via handlePanBlur). This is only the
    // backstop for a path that skipped blur, and it reports the same inline error.
    const pan = form.panCard.trim().toUpperCase();
    if (!PAN_RE.test(pan)) {
      setPanError(!pan ? 'PAN is mandatory.' : 'Enter a valid PAN (e.g. AHAPN0789J)');
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
      setBlocked(null);
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
      if (err instanceof ApiError && err.body?.code === 'DUPLICATE_PULL') {
        // Previous attempt this month (any status) — the PAN is frozen. The server's
        // message already carries the admin-only failure reason when we may see it.
        setBlocked({ message: err.message, attempt: err.body.lastAttempt ?? null });
      } else if (err instanceof ApiError && err.body?.code === 'RE-PULL_ADMIN_ONLY') {
        setMessage({ kind: 'err', text: err.message });
      } else if (err instanceof ApiError && err.status === 403) {
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
    if (reqSavingRef.current) return; // re-entry guard: see reqSavingRef
    reqSavingRef.current = true;
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
      reqSavingRef.current = false;
      setReqSaving(false);
    }
  };

  const regenerate = async () => {
    const pan = form.panCard.trim().toUpperCase();
    const ok = await confirmAction({
      title: `Regenerate the report for ${pan}?`,
      text: panOnFile
        ? 'The existing PDF for this month is REPLACED with a fresh pull.\nThis uses one vendor credit.'
        : 'No report exists for this PAN this month — the previous attempt did not produce one.\nThis starts a fresh pull and uses one vendor credit.',
      confirmText: 'Regenerate',
      tone: 'danger',
    });
    if (!ok) return;
    if (pullInitiatedRef.current) return; // persistent lock — only Refresh releases it
    // A re-pull IS a pull: it takes the same lock, so the buttons stay shut afterwards
    // and the refresh hint appears for this path too.
    pullInitiatedRef.current = true;
    setPullInitiated(true);
    checkCacheRef.current = null;
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
      setBlocked(null);
      refreshQuota();
      setMessage({ kind: 'ok', text: res.message });
    } catch (err) {
      if (err instanceof ApiError && err.body?.code === 'DUPLICATE_PULL') {
        // e.g. another re-pull for this PAN is already in flight.
        setBlocked({ message: err.message, attempt: err.body.lastAttempt ?? null });
      } else {
        setMessage({ kind: 'err', text: err instanceof ApiError ? err.message : 'Regeneration failed' });
      }
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

  // Duplicate-pull freeze: a previous attempt for this PAN this month — success or
  // failure — means only an admin re-pull may proceed, so the plain Generate button
  // locks for everyone else instead of letting them burn another vendor credit.
  const freezeBlocked = !!blocked && !isAdmin;

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
              onChange={(e) => {
                setPanError(null); // stale error drops while typing; blur re-validates
                set('panCard', e.target.value.toUpperCase().slice(0, 10));
              }}
              onBlur={handlePanBlur}
              pattern="[A-Za-z]{5}[0-9]{4}[A-Za-z]"
              title="10-character PAN, e.g. AHAPN0789J"
              aria-invalid={!!panError}
              required
            />
            {panError && <span className="field-error" role="alert">{panError}</span>}
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

        {blocked && (
          <div className="alert warn">
            <b>Duplicate pull blocked.</b> {blocked.message}
          </div>
        )}

        <button className="btn primary full" disabled={loading || quotaBlocked || freezeBlocked || pullInitiated}>
          {loading
            ? (<><span className="spinner" aria-hidden /> Generating report — please wait…</>)
            : quotaBlocked ? 'Limit reached — request an increase below'
            : pullInitiated ? 'Pull initiated — click Refresh to enable'
            : freezeBlocked ? 'PAN frozen this month — admin re-pull required'
            : 'Generate Report'}
        </button>
        </fieldset>

        {/* Deliberately OUTSIDE the fieldset: a disabled <fieldset> would disable the
            Refresh button too, and the lock would have no way out. */}
        {pullInitiated && (
          <div className="alert warn pull-refresh" role="status">
            <span>Click on Refresh to enable the button.</span>
            <button type="button" className="btn secondary small" onClick={refreshForm}>
              <Icon name="refresh" size={14} /> Refresh
            </button>
          </div>
        )}

        {isAdmin && (panOnFile || blocked) && (
          <button type="button" className="btn warn full" onClick={regenerate} disabled={loading || pullInitiated}>
            <Icon name="edit" size={16} />{' '}
            {panOnFile
              ? 'Regenerate (Admin) — replaces the saved PDF'
              : 'Re-pull (Admin) — retry this month\u2019s failed pull'}
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
              {/* type="submit" is what lets Enter in the reason field reach this;
                  disabling the button stops the click, and reqSavingRef stops the
                  second POST. */}
              <button type="submit" className="btn primary" disabled={reqSaving || !reqLimit}>
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
            <span className="scope-chip">
              {failedPulls > 0
                ? `${okPulls} pull${okPulls === 1 ? '' : 's'} · ${failedPulls} failed`
                : `${myReports.length} pull${myReports.length === 1 ? '' : 's'}`}
            </span>
          </div>
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>PAN</th>
                <th>Type</th>
                <th>Source</th>
                <th>Status</th>
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
                  <td>
                    <span className={statusBadge(r.status)}>{r.status}</span>
                    {r.errorMessage && (
                      <div className="row-reason" title="Vendor's failure reason for this attempt">
                        {r.errorMessage}
                      </div>
                    )}
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
