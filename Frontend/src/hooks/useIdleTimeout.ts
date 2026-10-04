import { useEffect, useRef, useState } from 'react';
import { getToken } from '../api';

/** Minutes of inactivity before the client signs itself out. Mirrors Jwt:IdleTimeoutMinutes. */
export const IDLE_MINUTES = 15;
const IDLE_MS = IDLE_MINUTES * 60 * 1000;
/** Warn this long before the deadline so nobody is dropped without notice. */
const WARN_MS = 60 * 1000;

// Any of these means "the user is still here". mousemove alone fires constantly on some
// machines, so it is throttled below rather than trusted as-is.
const ACTIVITY_EVENTS = [
  'mousemove',
  'mousedown',
  'keydown',
  'scroll',
  'touchstart',
  'click',
] as const;

export interface IdleWarning {
  /** Seconds left before automatic sign-out. */
  secondsLeft: number;
  /** Keep the session alive. */
  staySignedIn: () => void;
}

/**
 * Client-side idle timer.
 *
 * The server is the authority (it drops the session row after IdleTimeoutMinutes), but a
 * browser sitting on an untouched tab issues no requests, so it would keep showing a
 * populated screen full of data that is no longer valid. This signs the user out locally at
 * the same deadline and warns a minute beforehand.
 *
 * @param onIdle called when the deadline passes. The caller owns clearing the session and
 *   navigating, so it can record WHY the session ended before the sign-out is announced —
 *   the login screen reads that reason on mount and would otherwise miss it.
 */
export function useIdleTimeout(onIdle: () => void): IdleWarning | null {
  const [warning, setWarning] = useState<IdleWarning | null>(null);

  // Refs, not state: these change on every event and must not re-render or restart the timer.
  const lastActivity = useRef<number>(Date.now());
  const warned = useRef(false);
  const onIdleRef = useRef(onIdle);
  onIdleRef.current = onIdle;

  const reset = () => {
    lastActivity.current = Date.now();
    if (warned.current) {
      warned.current = false;
      setWarning(null);
    }
  };

  useEffect(() => {
    if (!getToken()) return; // logged out (or never signed in) — nothing to time out

    // Throttle mousemove to one update per second; the others are sparse enough to use directly.
    let lastMoveWrite = 0;
    const onActivity = () => {
      const now = Date.now();
      if (now - lastMoveWrite < 1000) return;
      lastMoveWrite = now;
      reset();
    };

    ACTIVITY_EVENTS.forEach((e) => window.addEventListener(e, onActivity, { passive: true }));

    const tick = window.setInterval(() => {
      if (!getToken()) {
        window.clearInterval(tick);
        return;
      }

      const idleFor = Date.now() - lastActivity.current;
      const remaining = IDLE_MS - idleFor;

      if (remaining <= 0) {
        window.clearInterval(tick);
        ACTIVITY_EVENTS.forEach((e) => window.removeEventListener(e, onActivity));
        onIdleRef.current();
        return;
      }

      if (remaining <= WARN_MS) {
        warned.current = true;
        setWarning({
          secondsLeft: Math.max(1, Math.ceil(remaining / 1000)),
          staySignedIn: reset,
        });
      }
    }, 1000);

    return () => {
      window.clearInterval(tick);
      ACTIVITY_EVENTS.forEach((e) => window.removeEventListener(e, onActivity));
    };
  }, []);

  return warning;
}
