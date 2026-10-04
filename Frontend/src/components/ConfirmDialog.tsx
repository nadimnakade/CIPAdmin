import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { confirmStore } from '../lib/confirm';
import Icon from './Icon';

/**
 * SweetAlert-style confirm dialog, bound to the promise returned by
 * confirmAction(). Replaces window.confirm() everywhere in the app.
 *
 * Deliberate choices:
 *  - Escape and backdrop click both CANCEL, never confirm. Destructive actions
 *    must never be one stray keypress away.
 *  - For a 'danger' dialog the cancel button takes initial focus, so the
 *    dangerous button is not the one a stray Enter lands on.
 *  - Text is rendered as plain lines (split on \n) — never dangerouslySetInnerHTML,
 *    since messages are built from employee names and filenames.
 */
export default function ConfirmDialog() {
  // Must subscribe: confirmAction() is called from event handlers deep in a page,
  // so this component has no other reason to re-render when a request arrives.
  const request = useSyncExternalStore(confirmStore.subscribe, confirmStore.getRequest);
  const [shown, setShown] = useState(false);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);

  // Mount animation: let the element paint before flipping to the visible state.
  useEffect(() => {
    if (!request) return;
    setShown(false);
    const id = window.setTimeout(() => setShown(true), 10);
    (request.tone === 'danger' ? cancelRef.current : confirmRef.current)?.focus();
    return () => window.clearTimeout(id);
  }, [request]);

  useEffect(() => {
    if (!request) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        confirmStore.settle(false);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [request]);

  if (!request) return null;

  const lines = request.text ? request.text.split('\n').filter(Boolean) : [];

  return (
    <div
      className={'modal-backdrop confirm-backdrop' + (shown ? ' in' : '')}
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="confirm-title"
      aria-describedby={lines.length ? 'confirm-text' : undefined}
      onClick={(e) => {
        if (e.target === e.currentTarget) confirmStore.settle(false);
      }}
    >
      <div className={'modal confirm-modal confirm-' + request.tone}>
        <div className="confirm-icon">
          <Icon name={request.tone === 'danger' ? 'trash' : 'help'} size={22} />
        </div>
        <h3 id="confirm-title" className="confirm-title">
          {request.title}
        </h3>
        {lines.length > 0 && (
          <div id="confirm-text" className="confirm-text">
            {lines.map((line, i) => (
              <p key={i}>{line}</p>
            ))}
          </div>
        )}
        <div className="modal-actions confirm-actions">
          <button
            type="button"
            className="btn"
            ref={cancelRef}
            onClick={() => confirmStore.settle(false)}
          >
            {request.cancelText}
          </button>
          <button
            type="button"
            className={'btn ' + (request.tone === 'danger' ? 'confirm-danger-btn' : 'primary')}
            ref={confirmRef}
            onClick={() => confirmStore.settle(true)}
          >
            {request.confirmText}
          </button>
        </div>
      </div>
    </div>
  );
}