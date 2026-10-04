import { useEffect, useState } from 'react';
import { toastStore, type Toast, type ToastKind } from '../lib/toast';
import Icon from './Icon';

const ICON: Record<ToastKind, string> = {
  success: 'check',
  error: 'alert',
  warn: 'alert',
  info: 'clock',
};

const ARIA: Record<ToastKind, 'status' | 'alert'> = {
  success: 'status',
  info: 'status',
  warn: 'alert',
  error: 'alert',
};

/**
 * Bottom-right toast stack. Success messages are transient confirmations
 * ("Saved", "Deleted"); inline error banners are deliberately left alone so an
 * error stays anchored to the field or table that produced it.
 */
export default function ToastStack() {
  const [toasts, setToasts] = useState<Toast[]>(toastStore.getToasts());

  useEffect(() => toastStore.subscribe(() => setToasts(toastStore.getToasts())), []);

  if (!toasts.length) return null;

  return (
    <div className="toast-stack" aria-live="polite" aria-atomic="false">
      {toasts.map((t) => (
        <div
          key={t.id}
          className={`toast toast-${t.kind}`}
          role={ARIA[t.kind]}
          onClick={() => toastStore.dismiss(t.id)}
          title="Dismiss"
        >
          <span className="toast-icon">
            <Icon name={ICON[t.kind]} size={15} />
          </span>
          <div className="toast-body">
            <div className="toast-message">{t.message}</div>
            {t.detail && <div className="toast-detail">{t.detail}</div>}
          </div>
          <button type="button" className="toast-close" aria-label="Dismiss notification">
            <Icon name="close" size={13} />
          </button>
        </div>
      ))}
    </div>
  );
}