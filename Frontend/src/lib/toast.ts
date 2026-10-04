/*
   Toasts.

   A tiny pub/sub store rather than context: every caller is an event handler deep
   in a page, and threading a `toast` prop down to reach them would mean editing
   dozens of signatures for no benefit. Components subscribe with useToast().

   Success messages are transient confirmations ("Saved", "Deleted"), which is
   exactly what a toast is for. Errors are NOT routed here — they stay inline
   next to the field or table that caused them, so the user knows where to look.
*/

export type ToastKind = 'success' | 'error' | 'info' | 'warn';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
  /** Optional second line for detail (e.g. why an import failed). */
  detail?: string;
}

type Listener = () => void;

let nextId = 1;
let toasts: Toast[] = [];
const listeners = new Set<Listener>();

const emit = () => {
  for (const l of Array.from(listeners)) l();
};

function subscribe(fn: Listener): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

function getToasts(): Toast[] {
  return toasts;
}

/** How long a toast lingers before it fades on its own. */
const LIFETIME_MS: Record<ToastKind, number> = {
  success: 3200,
  info: 4000,
  warn: 6000,
  error: 7000,
};

/** Cap the stack so a burst of failures can't cover the whole screen. */
const MAX_VISIBLE = 4;

function push(kind: ToastKind, message: string, detail?: string) {
  const toast: Toast = { id: nextId++, kind, message, detail };
  toasts = [...toasts, toast];
  // Drop the oldest rather than the newest: the most recent action is the one
  // the user is still looking at.
  if (toasts.length > MAX_VISIBLE) toasts = toasts.slice(toasts.length - MAX_VISIBLE);
  emit();

  setTimeout(() => dismiss(toast.id), LIFETIME_MS[kind]);
  return toast.id;
}

function dismiss(id: number) {
  const before = toasts.length;
  toasts = toasts.filter((t) => t.id !== id);
  if (toasts.length !== before) emit();
}

function dismissAll() {
  if (!toasts.length) return;
  toasts = [];
  emit();
}

export const toastStore = { subscribe, getToasts, dismiss, dismissAll };

/** Imperative API. Use these directly from event handlers. */
export const toast = {
  success: (message: string, detail?: string) => push('success', message, detail),
  error: (message: string, detail?: string) => push('error', message, detail),
  info: (message: string, detail?: string) => push('info', message, detail),
  warn: (message: string, detail?: string) => push('warn', message, detail),
};