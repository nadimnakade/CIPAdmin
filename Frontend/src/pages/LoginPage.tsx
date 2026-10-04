import { FormEvent, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, setAuth, clearAuth, takeSignOutReason, AuthUser, ApiError } from '../api';
import Icon from '../components/Icon';
import './LoginPage.css';

interface UserSessionDto {
  empCode: number;
  username: string;
  empName: string;
  roleId: number;
  roleName: string;
  designationId: number;
  designation: string;
  locationId: number;
  email: string | null;
  isGoogleAuthenticated: boolean;
  isBypass: boolean;
  token: string | null;
  tokenExpiresAt: string | null;
}

interface LoginResponse {
  valid: boolean;
  message: string;
  nextStep: 'done' | 'require_otp' | 'enroll_otp';
  user: UserSessionDto | null;
  otpProvisioningUri: string | null;
  otpSecret: string | null;
}

/** Server's exact no-access text — matched so the login card can render it as a notice, not an error. */
const NO_ACCESS_MSG = "You don't have access to this website. Please contact administrator.";

export default function LoginPage() {
  const navigate = useNavigate();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(true);
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [noAccess, setNoAccess] = useState(false);
  // Set when the app signs the user out on purpose (idle timeout, or a second login
  // elsewhere), so the card can explain itself instead of looking like a random expiry.
  const [signedOut, setSignedOut] = useState('');

  const [step, setStep] = useState<'credentials' | 'otp'>('credentials');
  const [otp, setOtp] = useState('');

  // Portal hand-off: CIP_LogoutController/LogoutAll lands here with ?portalLogout=1
  // after abandoning the portal session — clear any token this app still holds.
  useEffect(() => {
    if (new URLSearchParams(window.location.search).get('portalLogout')) {
      clearAuth();
      window.history.replaceState(null, '', '/login');
    }

    const reason = takeSignOutReason();
    if (reason === 'idle')
      setSignedOut('You were signed out after 15 minutes of inactivity.');
    else if (reason === 'superseded')
      setSignedOut('You were signed out because this account was signed in somewhere else.');
    else if (reason === 'expired')
      setSignedOut('Your session has ended. Please sign in again.');
  }, []);

  // Any typing means the user is back; drop the notice rather than leaving it to go stale.
  useEffect(() => {
    if (signedOut && (username || password)) setSignedOut('');
  }, [username, password, signedOut]);

  const submitCredentials = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setNoAccess(false);
    setLoading(true);
    try {
      const res = await api.post<LoginResponse>('/api/auth/login', {
        username,
        password,
        rememberMe,
      });

      if (!res.valid || !res.user) {
        if (res.message === NO_ACCESS_MSG) {
          setNoAccess(true);
        } else {
          setError(res.message || 'Invalid username or password');
        }
        return;
      }

      if (res.nextStep === 'done') {
        // Bypass mode: token already issued
        setAuth(res.user.token!, res.user as AuthUser);
        navigate('/', { replace: true });
        return;
      }

      // require_otp or enroll_otp → show the OTP step
      setStep('otp');
    } catch (err) {
      if (err instanceof ApiError && err.message === NO_ACCESS_MSG) setNoAccess(true);
      else setError(err instanceof ApiError ? err.message : 'Login failed');
    } finally {
      setLoading(false);
    }
  };

  const submitOtp = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setNoAccess(false);
    setLoading(true);
    try {
      const res = await api.post<LoginResponse>('/api/auth/verify-otp', { username, code: otp });
      if (res.valid && res.user?.token) {
        setAuth(res.user.token, res.user as AuthUser);
        navigate('/', { replace: true });
      } else {
        setError(res.message || 'Invalid or expired OTP');
      }
    } catch (err) {
      if (err instanceof ApiError && err.message === NO_ACCESS_MSG) setNoAccess(true);
      else setError(err instanceof ApiError ? err.message : 'OTP verification failed');
    } finally {
      setLoading(false);
    }
  };

  // One notice slot, shared by both steps, so the three states can never drift
  // apart. The text is exactly what the API / sign-out layer produced.
  const notice = () =>
    noAccess ? (
      <div className="alert warn no-access" role="alert">
        <span className="alert-ico"><Icon name="shield" size={15} /></span>
        <span>{NO_ACCESS_MSG}</span>
      </div>
    ) : error ? (
      <div className="alert error" role="alert">
        <span className="alert-ico"><Icon name="alert" size={15} /></span>
        <span>{error}</span>
      </div>
    ) : signedOut ? (
      <div className="alert warn" role="status">
        <span className="alert-ico"><Icon name="clock" size={15} /></span>
        <span>{signedOut}</span>
      </div>
    ) : null;

  return (
    <div className="login-page">
      {/* Decorative only — the two dot clusters that flank the card. */}
      <span className="login-dots a" aria-hidden="true" />
      <span className="login-dots b" aria-hidden="true" />

      <div className="login-shell">
        <aside className="login-brand">
          <div className="login-brand-top">
            <img
              src={`${import.meta.env.BASE_URL}assets/logo.png`}
              alt="1UP logo"
              className="brand-img login-brand-mark"
            />
            <span className="login-brand-word">
              <span className="login-brand-name">1UP</span>
              <span className="login-brand-tagline">Business Management System</span>
            </span>
          </div>

          <div className="login-brand-copy">
            <h2>Manage Smarter.<em>Grow Faster.</em></h2>
            <p>
              Access your dashboard, manage operations, view reports and stay in
              control &mdash; all in one place.
            </p>
          </div>

          <ul className="login-brand-points">
            <li>
              <span className="bpoint-icon blue"><Icon name="barChart" size={19} /></span>
              <span className="bpoint-text">
                <span className="bpoint-title">Real-time Reports</span>
                <span className="bpoint-sub">Get insights and make better decisions</span>
              </span>
            </li>
            <li>
              <span className="bpoint-icon purple"><Icon name="users" size={19} /></span>
              <span className="bpoint-text">
                <span className="bpoint-title">User Management</span>
                <span className="bpoint-sub">Manage teams and permissions</span>
              </span>
            </li>
            <li>
              <span className="bpoint-icon green"><Icon name="shield" size={19} /></span>
              <span className="bpoint-text">
                <span className="bpoint-title">Secure &amp; Reliable</span>
                <span className="bpoint-sub">Your data is safe with us</span>
              </span>
            </li>
            <li>
              <span className="bpoint-icon amber"><Icon name="settings" size={19} /></span>
              <span className="bpoint-text">
                <span className="bpoint-title">All-in-One Platform</span>
                <span className="bpoint-sub">Everything you need in one system</span>
              </span>
            </li>
          </ul>
        </aside>

        <main className="login-main">
          <div className="login-card">
            <div className="login-logo">
              <img src={`${import.meta.env.BASE_URL}assets/logo.png`} alt="1UP logo" className="brand-img big" />
            </div>

            {step === 'otp' ? (
              <>
                <div className="otp-badge"><Icon name="shield" size={22} /></div>
                <h1 className="login-title">Verify it&rsquo;s you</h1>
                <p className="login-sub">A one-time code keeps the account closed to strangers.</p>
              </>
            ) : (
              <>
                <h1 className="login-title">Welcome Back</h1>
                <p className="login-sub">Sign in to access your 1UP account</p>
              </>
            )}

            {step === 'credentials' ? (
              <form onSubmit={submitCredentials}>
                <div className="field">
                  <label className="field-label" htmlFor="login-username">
                    Username
                  </label>
                  <div className="input-wrap">
                    <Icon name="user" />
                    <input
                      id="login-username"
                      placeholder="Email / Username"
                      value={username}
                      onChange={(e) => setUsername(e.target.value)}
                      autoComplete="username"
                      required
                    />
                  </div>
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="login-password">
                    Password
                  </label>
                  <div className="input-wrap">
                    <Icon name="lock" />
                    <input
                      id="login-password"
                      type={showPassword ? 'text' : 'password'}
                      placeholder="Password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      autoComplete="current-password"
                      required
                    />
                    <button
                      type="button"
                      className="eye-btn"
                      onClick={() => setShowPassword(!showPassword)}
                      aria-label={showPassword ? 'Hide password' : 'Show password'}
                      aria-pressed={showPassword}
                    >
                      <Icon name="eye" size={16} />
                    </button>
                  </div>
                </div>
                <div className="login-row">
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={rememberMe}
                      onChange={(e) => setRememberMe(e.target.checked)}
                    />
                    Remember me
                  </label>
                  <a className="forgot" href="#">
                    Forgot password?
                  </a>
                </div>
                {notice()}
                <button className="btn primary full" disabled={loading}>
                  {loading ? (
                    <>
                      <span className="spinner" aria-hidden="true" />
                      Signing in…
                    </>
                  ) : (
                    <>
                      Sign In
                      <Icon name="chevronRight" size={17} />
                    </>
                  )}
                </button>
              </form>
            ) : (
              <form onSubmit={submitOtp}>
                <p className="otp-note">
                  Enter the 6-digit code from Google Authenticator for <b>{username}</b>
                </p>
                <input
                  className="otp-input"
                  placeholder="000000"
                  inputMode="numeric"
                  maxLength={6}
                  value={otp}
                  onChange={(e) => setOtp(e.target.value.replace(/\D/g, ''))}
                  aria-label="6-digit authentication code"
                  autoFocus
                />
                {notice()}
                <button className="btn primary full" disabled={loading || otp.length !== 6}>
                  {loading ? (
                    <>
                      <span className="spinner" aria-hidden="true" />
                      Verifying…
                    </>
                  ) : (
                    'Verify & Sign In'
                  )}
                </button>
                <button type="button" className="btn link full" onClick={() => setStep('credentials')}>
                  ← Back to sign in
                </button>
              </form>
            )}
          </div>
          <div className="login-footer">© 2025 1UP. All rights reserved.</div>
        </main>
      </div>
    </div>
  );
}
