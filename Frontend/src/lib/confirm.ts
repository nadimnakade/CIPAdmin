/*
   Promise-based confirm dialog — the in-house replacement for window.confirm().

   Call sites become:
       if (!(await confirmAction({ title, text, confirmText, tone: 'danger' }))) return;

   Only one dialog is ever open, so the store holds a single request. Each call
   creates a fresh promise and parks its resolve here; the dialog component settles
   it. A second confirm() while one is open would overwrite the first resolver and
   hang it forever, so the store serialises: a new request is queued and shown only
   after the current one settles.
*/

export type ConfirmTone = 'danger' | 'primary';

export interface ConfirmOptions {
  /** Bold headline. Keep it a statement of the action, not a question. */
  title: string;
  /** Optional body. May contain \n for a line break. */
  text?: string;
  /** Label on the affirmative button. Defaults to "Yes, continue". */
  confirmText?: string;
  /** Label on the dismissive button. Defaults to "Cancel". */
  cancelText?: string;
  /** 'danger' paints the confirm button in stamp red for deletes/irreversible work. */
  tone?: ConfirmTone;
}

type Resolver = (result: boolean) => void;

export interface ConfirmRequest extends Required<Omit<ConfirmOptions, 'text'>> {
  text?: string;
  resolve: Resolver;
}

type Listener = () => void;

let current: ConfirmRequest | null = null;
const queue: ConfirmRequest[] = [];
const listeners = new Set<Listener>();

const emit = () => {
  for (const l of Array.from(listeners)) l();
};

/** Show the queued request, if the dialog slot is free. */
function pump() {
  if (current || !queue.length) return;
  current = queue.shift()!;
  emit();
}

function settle(result: boolean) {
  const active = current;
  current = null;
  emit();
  if (active) active.resolve(result);
  // Let the closing animation finish before the next dialog mounts.
  setTimeout(pump, 180);
}

function subscribe(fn: Listener): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

function getRequest(): ConfirmRequest | null {
  return current;
}

/** Ask the user to confirm. Resolves true for yes, false for cancel. */
export function confirmAction(opts: ConfirmOptions): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    queue.push({
      title: opts.title,
      text: opts.text,
      confirmText: opts.confirmText || 'Yes, continue',
      cancelText: opts.cancelText || 'Cancel',
      tone: opts.tone || 'primary',
      resolve,
    });
    pump();
  });
}

export const confirmStore = { subscribe, getRequest, settle };