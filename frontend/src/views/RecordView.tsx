import { useAtom, useAtomValue } from 'jotai';
import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { useVoiceQueue } from '../hooks/useVoiceQueue';
import { useVoiceRecorder } from '../hooks/useVoiceRecorder';
import { activeTrialIdAtom, currentUserAtom } from '../store/atoms';

/** Formats a whole-second count as m:ss (e.g. 7 → 0:07, 83 → 1:23). */
function formatDuration(totalSeconds: number): string {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/**
 * Field-worker screen: pick a trial, hold the mic button to record a voice
 * note, and it lands in the IndexedDB queue (status 'pending') for the P1
 * sync worker. No confirm card / playback yet — that is P1.
 */
export function RecordView() {
  const currentUser = useAtomValue(currentUserAtom);
  const navigate = useNavigate();
  const { logout } = useAuth();
  const { trials, loading, error: trialsError } = useTrials();
  const [activeTrialId, setActiveTrialId] = useAtom(activeTrialIdAtom);

  const {
    isRecording,
    durationSeconds,
    audioBlob,
    mimeType,
    permissionState,
    error: recorderError,
    startRecording,
    stopRecording,
    reset,
  } = useVoiceRecorder();
  const {
    pendingCount,
    saving,
    error: queueError,
    saveRecording,
    refreshPendingCount,
  } = useVoiceQueue();

  const [savedThisClip, setSavedThisClip] = useState(false);
  /** Blob already handed to the queue — guards against accidental re-saves. */
  const handledBlobRef = useRef<Blob | null>(null);

  // Re-sync the queue size when the screen opens.
  useEffect(() => {
    void refreshPendingCount();
  }, [refreshPendingCount]);

  // Default to the first trial once the list arrives.
  const trialSelected =
    activeTrialId !== null &&
    trials.some((t) => t.trialId === activeTrialId);
  useEffect(() => {
    if (!trialSelected && !loading && trials.length > 0) {
      setActiveTrialId(trials[0].trialId);
    }
  }, [trialSelected, loading, trials, setActiveTrialId]);

  // Auto-save the finished clip into the queue exactly once per blob.
  useEffect(() => {
    if (audioBlob === null || isRecording) {
      return;
    }
    if (handledBlobRef.current === audioBlob) {
      return; // already handed off (or failed) — Retry re-records instead
    }
    if (activeTrialId === null) {
      return;
    }
    handledBlobRef.current = audioBlob;

    let cancelled = false;
    (async () => {
      const rec = await saveRecording(audioBlob, mimeType ?? '', activeTrialId);
      if (!cancelled && rec) {
        setSavedThisClip(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [audioBlob, isRecording, mimeType, activeTrialId, saveRecording]);

  function handleLogout() {
    logout();
    navigate('/login', { replace: true });
  }

  function handleStart() {
    setSavedThisClip(false);
    void startRecording();
  }

  const micDisabled = permissionState === 'unsupported' || activeTrialId === null;

  return (
    <div>
      <header className="topbar">
        <span className="topbar__brand">PlottR 0.2</span>
        <div className="topbar__user">
          {currentUser && (
            <span className="topbar__identity">
              Signed in as <strong>{currentUser.name}</strong>
              {currentUser.role ? ` (${currentUser.role})` : ''}
            </span>
          )}
          <Link className="link-button" to="/ratings">
            Ratings
          </Link>
          <Link className="link-button" to="/">
            Dashboard
          </Link>
          <Button variant="secondary" onClick={handleLogout}>
            Log out
          </Button>
        </div>
      </header>

      <main className="record-screen">
        <section className="card">
          <h2 className="heading">Trial</h2>
          <div className="field record-trial-field">
            <label className="field__label" htmlFor="record-trial">
              Work on
            </label>
            <select
              id="record-trial"
              name="record-trial"
              className="input"
              value={activeTrialId ?? ''}
              onChange={(e) => setActiveTrialId(e.target.value)}
              disabled={loading}
            >
              {activeTrialId === null && (
                <option value="">Select a trial…</option>
              )}
              {trials.map((t) => (
                <option key={t.trialId} value={t.trialId}>
                  {t.fileName || t.externalId}
                </option>
              ))}
            </select>
          </div>
          <p className="muted">Recordings are queued under this trial.</p>
        </section>

        {!loading && trials.length === 0 && (
          <p className="muted record-no-trials" role="status">
            No trials yet — upload a workbook from the Dashboard and they will
            show up here.
          </p>
        )}
        {trialsError && <p className="error-text">{trialsError}</p>}

        <section className="card record-mic-card">
          <h2 className="heading">Voice note</h2>
          <p className="muted">
            Hold nothing — tap to start, tap again to stop. The clip is saved
            on this device for a later sync.
          </p>

          <div className="record-mic-area">
            <button
              type="button"
              className={`mic-button${isRecording ? ' mic-button--recording' : ''}`}
              onClick={() => (isRecording ? stopRecording() : handleStart())}
              disabled={micDisabled}
              aria-label={isRecording ? 'Stop recording' : 'Start recording'}
              aria-pressed={isRecording}
            >
              <span className="mic-button__glyph" aria-hidden="true" />
            </button>

            {isRecording && (
              <p className="record-duration" role="timer" aria-live="off">
                {formatDuration(durationSeconds)}
              </p>
            )}

            {permissionState === 'unsupported' && (
              <p className="muted record-unsupported" role="status">
                Microphone recording is not supported in this browser — try a
                recent version of Chrome, Edge, Safari, or Firefox.
              </p>
            )}

            {recorderError && !isRecording && (
              <p className="error-text record-error" role="alert">
                {recorderError}
              </p>
            )}

            {audioBlob !== null && !isRecording && (
              <div className="record-status">
                {saving ? (
                  <p className="muted record-saved" role="status">
                    Saving…
                  </p>
                ) : savedThisClip ? (
                  <p className="success-text record-saved" role="status">
                    Saved ✓
                  </p>
                ) : (
                  <p className="error-text record-saved" role="alert">
                    {queueError ?? 'The recording could not be saved.'}{' '}
                    <button
                      type="button"
                      className="link-button"
                      onClick={reset}
                    >
                      Retry — reset and re-record
                    </button>
                  </p>
                )}
              </div>
            )}
          </div>

          {pendingCount > 0 && (
            <p className="pending-badge" role="status">
              {pendingCount} recording{pendingCount === 1 ? '' : 's'} waiting
              to sync
            </p>
          )}
        </section>
      </main>
    </div>
  );
}