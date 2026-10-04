import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { subscribeLoading, getPending } from '../lib/loading';

/**
 * Indeterminate top progress bar, driven by the global request counter.
 *
 * Two details stop it feeling twitchy:
 *  - it waits APPEAR_DELAY_MS before appearing, so a fast response never produces
 *    a one-frame flash of colour across the topbar;
 *  - after the last request settles it plays a short "catch-up" sweep and fades,
 *    rather than vanishing the instant the network goes quiet.
 *
 * Mounted once at the app root so it covers every screen, including the login
 * page (which sits outside the Layout shell).
 */
const APPEAR_DELAY_MS = 140;
const EXIT_MS = 420;

export default function TopProgressBar() {
  // Subscribe to the counter's VALUE, not to its change events. The bar must be a
  // pure function of "is work outstanding right now", so that a request which
  // starts and settles inside the appear-delay can never strand it on screen.
  const pending = useSyncExternalStore(subscribeLoading, getPending);
  const [visible, setVisible] = useState(false);
  const [exiting, setExiting] = useState(false);
  const appearTimer = useRef<number | undefined>(undefined);
  const exitTimer = useRef<number | undefined>(undefined);

  useEffect(() => {
    const clearAppear = () => {
      if (appearTimer.current !== undefined) window.clearTimeout(appearTimer.current);
      appearTimer.current = undefined;
    };
    const clearExit = () => {
      if (exitTimer.current !== undefined) window.clearTimeout(exitTimer.current);
      exitTimer.current = undefined;
    };

    if (pending > 0) {
      // Work in flight. Cancel any pending exit and (re)start the appear delay
      // only if the bar is not already on screen.
      clearExit();
      setExiting(false);
      if (!visible) {
        clearAppear();
        appearTimer.current = window.setTimeout(() => setVisible(true), APPEAR_DELAY_MS);
      }
    } else if (visible) {
      // Nothing outstanding and the bar is up: sweep to the edge, then fade.
      clearAppear();
      setExiting(true);
      clearExit();
      exitTimer.current = window.setTimeout(() => {
        setVisible(false);
        setExiting(false);
      }, EXIT_MS);
    } else {
      // Settled before the bar ever appeared (fast request, or a fast failure).
      // Drop the pending appear timer so it can never fire into a stale state.
      clearAppear();
      clearExit();
    }

    return () => {
      // Cancel on every re-run/unmount so no timer survives its conditions.
      clearAppear();
      clearExit();
    };
  }, [pending, visible]);

  if (!visible) return null;

  return (
    <div className={'top-progress' + (exiting ? ' exiting' : '')} role="progressbar" aria-label="Loading">
      <div className="top-progress-bar" />
    </div>
  );
}