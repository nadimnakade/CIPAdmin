import { trackedFetch } from './lib/loading';

const TOKEN_KEY = 'cip_token';
const USER_KEY = 'cip_user';

// Base path of the backend API. In dev the Vite proxy handles '/api'; in the
// deployed build the backend is a separate IIS app under /CIPAdmin_API.
export const API_BASE: string =
  (import.meta as any).env?.VITE_API_BASE || '';

/** Join API_BASE with a path starting with '/api/...'. */
export function apiUrl(path: string): string {
  return API_BASE + path;
}

export interface AuthUser {
  empCode: number;
  username: string;
  empName: string;
  roleId: number;
  roleName: string;
  designationId: number;
  designation: string;
  locationId: number;
  email: string;
  isGoogleAuthenticated: boolean;
  isBypass: boolean;
}

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function getUser(): AuthUser | null {
  const raw = localStorage.getItem(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AuthUser;
  } catch {
    return null;
  }
}

export function setAuth(token: string, user: AuthUser) {
  localStorage.setItem(TOKEN_KEY, token);
  localStorage.setItem(USER_KEY, JSON.stringify(user));
  notifyAuthChange();
}

export function clearAuth() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
  notifyAuthChange();
}

/* --------------------------------------------------------------- auth change */

/** Fired whenever the signed-in user is set or cleared. */
const AUTH_CHANGE_EVENT = 'cip:authchange';

function notifyAuthChange() {
  try {
    window.dispatchEvent(new Event(AUTH_CHANGE_EVENT));
  } catch {
    /* no DOM (tests) — callers re-read getUser() directly */
  }
}

/**
 * Subscribe to sign-in / sign-out.
 *
 * Reading getUser() during render is not enough: React Router re-renders <Routes>
 * on navigation, but the component that owns <Routes> does not re-run, so any
 * `getUser() ? ... : ...` it computed stays stale for the rest of the session.
 * That is what made sign-out bounce the user between /login and / in a loop.
 */
export function subscribeAuth(cb: () => void): () => void {
  window.addEventListener(AUTH_CHANGE_EVENT, cb);
  return () => window.removeEventListener(AUTH_CHANGE_EVENT, cb);
}

/* ------------------------------------------------------------ sign-out reason */

const SIGN_OUT_REASON_KEY = 'cip_signout_reason';

export type SignOutReason = 'idle' | 'superseded' | 'expired';

/** Why the session ended, so the login screen can say something more useful than "expired". */
export function setLastSignOutReason(reason: SignOutReason) {
  try {
    localStorage.setItem(SIGN_OUT_REASON_KEY, reason);
  } catch {
    /* storage full or blocked — the message is cosmetic, so ignore */
  }
}

/** Read and clear the stored reason. Consumed once so a later manual sign-out is not mislabelled. */
export function takeSignOutReason(): SignOutReason | null {
  try {
    const v = localStorage.getItem(SIGN_OUT_REASON_KEY) as SignOutReason | null;
    localStorage.removeItem(SIGN_OUT_REASON_KEY);
    return v;
  } catch {
    return null;
  }
}

/** Map the API's X-Session-End header onto a reason; defaults to a generic expiry. */
function readSignOutReason(res: Response): SignOutReason {
  const header = res.headers?.get?.('X-Session-End');
  if (header === 'idle') return 'idle';
  if (header === 'superseded') return 'superseded';
  return 'expired';
}

/** SSO bridge: if the URL carries ?sso={ticket} (minted by the legacy CIPPortal
 *  after its own login + MFA), exchange it for a JWT before anything else renders.
 *  Returns a cleanup message or null when no ticket was present. */
export async function consumeSsoTicket(): Promise<'ok' | 'fail' | 'none'> {
  const params = new URLSearchParams(window.location.search);
  const ticket = params.get('sso');
  if (!ticket) return 'none';
  // Strip from the address bar immediately so a refresh can't retry a consumed ticket.
  params.delete('sso');
  const clean = window.location.pathname + (params.toString() ? '?' + params.toString() : '') + window.location.hash;
  window.history.replaceState(null, '', clean);
  try {
    const res = await trackedFetch(apiUrl(`/api/auth/sso?ticket=${encodeURIComponent(ticket)}`));
    const data = await res.json().catch(() => null);
    if (res.ok && data?.valid && data?.user?.token) {
      setAuth(data.user.token, data.user);
      return 'ok';
    }
  } catch {
    /* fall through to fail */
  }
  return 'fail';
}

export class ApiError extends Error {
  status: number;
  needsOtp: boolean;
  /** Parsed JSON body of the error response (when it was JSON). */
  body: any;
  constructor(message: string, status: number, needsOtp = false, body: any = null) {
    super(message);
    this.status = status;
    this.needsOtp = needsOtp;
    this.body = body;
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...((options.headers as Record<string, string>) || {}),
  };
  const token = getToken();
  if (token) headers['Authorization'] = `Bearer ${token}`;

  const res = await trackedFetch(apiUrl(path), { ...options, headers });

  if (res.status === 401) {
    // The server's session check distinguishes "you were idle" from "you signed in
    // elsewhere"; both arrive here as a bare 401, so read the reason the API attached.
    const reason = readSignOutReason(res);
    clearAuth(); // also notifies subscribers, so the app re-renders as signed out
    setLastSignOutReason(reason);
    // The reason is carried to the login screen (which explains it in context and is
    // where the user is actually sent). Keep this message short: it is only ever seen
    // in the moment before the route swaps over, and it must not assert a cause the
    // server did not report.
    throw new ApiError(
      reason === 'superseded'
        ? 'You have been signed out because this account was signed in somewhere else.'
        : reason === 'idle'
          ? 'Your session ended after 15 minutes of inactivity.'
          : 'Your session has ended.',
      401,
    );
  }

  const text = await res.text();
  let data: any = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { message: text };
    }
  }

  if (!res.ok) {
    throw new ApiError(data?.message || data?.error || `Request failed (${res.status})`, res.status, false, data);
  }
  return data as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PUT', body: body === undefined ? undefined : JSON.stringify(body) }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  /** Raw fetch with the JWT attached — for multipart uploads and file downloads. */
  download: (path: string, init?: RequestInit) =>
    trackedFetch(apiUrl(path), {
      ...init,
      headers: { ...(init?.headers || {}), Authorization: `Bearer ${getToken()}` },
    }).then(async (res) => {
      if (!res.ok) {
        let msg = `Request failed (${res.status})`;
        try {
          const j = await res.json();
          msg = j.error || j.message || msg;
        } catch { /* not json */ }
        throw new ApiError(msg, res.status, false);
      }
      return res.blob();
    }),
  upload: <T = { ok: boolean; docType: string; fileName: string }>(path: string, form: FormData) =>
    trackedFetch(apiUrl(path), { method: 'POST', headers: { Authorization: `Bearer ${getToken()}` }, body: form }).then(
      async (res) => {
        if (!res.ok) {
          let msg = `Upload failed (${res.status})`;
          try {
            const j = await res.json();
            msg = j.error || j.message || msg;
          } catch { /* not json */ }
          throw new ApiError(msg, res.status, false);
        }
        return res.json() as Promise<T>;
      },
    ),
};

/** Download (or open) a PDF report from the backend, carrying the JWT. */
export async function fetchReportPdf(reportId: number): Promise<Blob> {
  const res = await trackedFetch(apiUrl(`/api/cibil/download/${reportId}`), {
    headers: { Authorization: `Bearer ${getToken()}` },
  });
  if (!res.ok) throw new ApiError('Failed to load PDF', res.status);
  return res.blob();
}

/** True when the signed-in user is admin/super-admin (sees all data). */
export function isAdminUser(): boolean {
  const u = getUser();
  return u?.roleId === 1 || u?.roleId === 2;
}
