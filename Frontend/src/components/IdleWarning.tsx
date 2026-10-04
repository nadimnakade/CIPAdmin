import { IdleWarning } from '../hooks/useIdleTimeout';
import Icon from './Icon';

/**
 * Countdown shown one minute before the idle timeout fires. Dismissal is not offered:
 * the only two honest options are "stay signed in" or "sign out now", and the second is
 * already one click away in the user menu.
 */
export default function IdleWarningDialog({ warning }: { warning: IdleWarning | null }) {
  if (!warning) return null;

  return (
    <div className="modal-backdrop" role="alertdialog" aria-modal="true" aria-labelledby="idle-warn-title">
      <div className="modal" style={{ maxWidth: 420 }}>
        <div className="modal-head">
          <h3 id="idle-warn-title">
            <Icon name="clock" size={16} /> Still there?
          </h3>
        </div>
        <div style={{ padding: '4px 0 8px' }}>
          <p style={{ margin: '0 0 12px' }}>
            You have been idle for a while. For security, your session closes automatically after 15
            minutes of inactivity.
          </p>
          <p style={{ margin: 0, fontWeight: 600 }}>
            Signing out in {warning.secondsLeft} second{warning.secondsLeft === 1 ? '' : 's'}…
          </p>
        </div>
        <div className="modal-actions">
          <button type="button" className="btn" onClick={warning.staySignedIn}>
            Stay signed in
          </button>
        </div>
      </div>
    </div>
  );
}
