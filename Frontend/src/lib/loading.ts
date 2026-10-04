/*
   Global loading state.

   Every HTTP call in the app goes through `trackedFetch`, which bumps a counter
   here. The top progress bar reads the counter, so it reflects real network
   activity rather than each page's own `loading` flag — pages that forget to
   set one still light the bar up.

   The counter (not a boolean) matters: several pages fire four or five requests
   at once on mount, and a boolean would hide the bar as soon as the first one
   settled.
*/

type Listener = () => void;

let pending = 0;
const listeners = new Set<Listener>();

/** Calls made while the counter is already above zero are ignored, so a failed
 *  request that never resolved can't strand the bar on screen. */
function emit() {
  for (const l of Array.from(listeners)) l();
}

export function startLoading() {
  pending += 1;
  emit();
}

export function stopLoading() {
  pending = Math.max(0, pending - 1);
  emit();
}

export function getPending(): number {
  return pending;
}

export function subscribeLoading(fn: Listener): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

/** fetch() with the global progress bar wired in. Every request in the app should
 *  use this instead of the bare global so the bar can never miss a call. */
export function trackedFetch(input: string, init?: RequestInit): Promise<Response> {
  startLoading();
  let res: Promise<Response>;
  try {
    res = fetch(input, init);
  } catch (e) {
    // fetch() only throws synchronously on a malformed URL / bad init.
    stopLoading();
    throw e;
  }
  return res.then(
    (r) => {
      stopLoading();
      return r;
    },
    (e) => {
      stopLoading();
      throw e;
    },
  );
}

/**
 * A promise that never rejects and resolves after `ms`. Used by the bar so a
 * very fast request still produces a visible sweep instead of a 1-frame flicker.
 */
export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}