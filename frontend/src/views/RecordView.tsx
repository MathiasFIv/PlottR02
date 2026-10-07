import { useAtom, useAtomValue } from 'jotai';
import { useEffect, useRef } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { ConfirmCard } from '../components/ConfirmCard';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { useVoiceQueue } from '../hooks/useVoiceQueue';
import { useVoiceRecorder } from '../hooks/useVoiceRecorder';
import { getRecordingById } from '../lib/audioDb';
import { readDefaultTarget } from '../lib/defaultTarget';
import { summarizeSync } from '../lib/syncQueue';
import { activeTrialIdAtom, currentUserAtom } from '../store/atoms';

/** Formats a whole-second count as m:ss (e.g. 7 → 0:07, 83 → 1:23). */
function formatDuration(totalSeconds: number): string {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/**
 * Field-worker screen: pick a trial, tap to record a voice note. The moment the
 * mic stops the clip auto-saves to IndexedDB and auto-syncs (transcript-
 * authoritative); the confirm card reappears only as a correction surface when
 * the server flags the clip (`needs-attention`) or the sync failed (retry).
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
    audioUrl,
    mimeType,
    permissionState,
    error: recorderError,
    startRecording,
    stopRecording,
    reset,
  } = useVoiceRecorder();
  const {
    attentionCount,
    saving,
    error: queueError,
    pendingRec,
    setPendingRec,
    saveRecording,
    discardRecording,
    autoSync,
    sweepPendingSyncs,
    refreshAttentionCount,
  } = useVoiceQueue();

  /**
   * Blob already handed to the queue — guards against accidental re-saves: one
   * blob / one save / one sync across the whole async chain and re-renders.
   */
  const handledBlobRef = useRef<Blob | null>(null);

  /**
   * Mirror of the active trial for the auto-save effect. Reading it through a
   * ref keeps `activeTrialId` out of that effect's dependency array — a trial
   * change mid-save must not re-run the effect or cancel an in-flight save
   * (which would falsely report "The recording could not be saved." after the
   * write already succeeded).
   */
  const activeTrialIdRef = useRef<string | null>(null);
  useEffect(() => {
    activeTrialIdRef.current = activeTrialId;
  }, [activeTrialId]);

  // Startup sweep: auto-send any crashed-mid-send 'pending' rows and legacy
  // 'ready' rows (idempotent — the engine's in-flight set guards double-sends),
  // then refresh the attention badge.
  useEffect(() => {
    void refreshAttentionCount();
    void sweepPendingSyncs();
  }, [refreshAttentionCount, sweepPendingSyncs]);

  // Default to the first trial once the list arrives.
  const trialSelected =
    activeTrialId !== null &&
    trials.some((t) => t.trialId === activeTrialId);
  useEffect(() => {
    if (!trialSelected && !loading && trials.length > 0) {
      setActiveTrialId(trials[0].trialId);
    }
  }, [trialSelected, loading, trials, setActiveTrialId]);

  // Auto-save the finished clip, then chain the auto-send: hold the row, sync
  // it, and re-read the row so the inline UI reflects the outcome.
  useEffect(() => {
    if (audioBlob === null || isRecording) {
      return;
    }
    if (handledBlobRef.current === audioBlob) {
      return; // already handed off (or failed) — Re-record starts fresh
    }
    // Snapshot the trial selected at capture time (via the mirror ref above,
    // which is why `activeTrialId` is not a dependency here).
    const trialId = activeTrialIdRef.current;
    if (trialId === null) {
      return;
    }
    handledBlobRef.current = audioBlob;

    let cancelled = false;
    (async () => {
      const rec = await saveRecording(audioBlob, mimeType ?? '', trialId);
      if (cancelled || rec === null) {
        return; // save failure → existing error + Re-record affordance
      }
      setPendingRec(rec);
      await autoSync(rec.id);
      if (cancelled) {
        return;
      }
      const updated = await getRecordingById(rec.id);
      if (updated) {
        setPendingRec(updated);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [audioBlob, isRecording, mimeType, saveRecording, autoSync, setPendingRec]);

  /** Clears the held clip + queue guard so a new recording starts clean. */
  function clearHeldClip() {
    handledBlobRef.current = null;
    setPendingRec(null);
    reset();
  }

  async function handleDiscard() {
    if (pendingRec === null) return;
    const ok = await discardRecording(pendingRec.id);
    if (ok) {
      clearHeldClip();
    }
  }

  /** Re-fetch the held row after a card action so the inline UI updates. */
  async function handleSyncComplete() {
    try {
      if (pendingRec === null) return;
      const updated = await getRecordingById(pendingRec.id);
      if (updated) {
        setPendingRec(updated);
      }
    } finally {
      await refreshAttentionCount();
    }
  }

  function handleStart() {
    // Starting over is a discard: delete the saved pending row so tapping the
    // mic (instead of Re-record) can't orphan a 'pending' clip in IndexedDB.
    // Fire-and-forget — the card is cleared regardless, and a failure would
    // leave a row the startup sweep can still pick up.
    if (pendingRec !== null) {
      void discardRecording(pendingRec.id);
    }
    clearHeldClip();
    // Recording errors (permission denied, no mic, recorder failure) are
    // already surfaced via the hook's setError — nothing to catch here.
    void startRecording();
  }

  function handleLogout() {
    logout();
    navigate('/login', { replace: true });
  }

  const micDisabled = permissionState === 'unsupported' || activeTrialId === null;
  const pendingStatus = pendingRec?.status;

  // Remembered write target (keyed by trial) from the last written sync — a
  // "values-only" clip continues the run at this plot/subsample.
  const nextDefaultTarget =
    activeTrialId !== null ? readDefaultTarget(activeTrialId) : null;

  /**
   * Variant-aware color class for the inline outcome banner. The `synced` case
   * with a stale `lastError` (a 409 "already saved" correction) renders as a
   * warning instead of dropping the message.
   */
  function bannerClassName(variant: 'success' | 'warning' | 'error'): string {
    if (variant === 'success') return 'success-text';
    if (variant === 'error') return 'error-text';
    return 'warning-text';
  }

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
          <p className="muted">Recordings are synced under this trial.</p>
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
            Tap to start, tap again to stop. The clip saves and syncs the
            moment the mic stops.
          </p>

          {nextDefaultTarget && (
            <p className="muted record-saved" role="status">
              Next: Plot {nextDefaultTarget.plot} · Sub{' '}
              {nextDefaultTarget.subsample}
            </p>
          )}

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

            {audioBlob !== null && !isRecording && pendingRec === null && (
              saving ? (
                <p className="muted record-saved" role="status">
                  Saving…
                </p>
              ) : (
                <p className="error-text record-saved" role="alert">
                  {queueError ?? 'The recording could not be saved.'}{' '}
                  <button
                    type="button"
                    className="link-button"
                    onClick={clearHeldClip}
                  >
                    Retry — reset and re-record
                  </button>
                </p>
              )
            )}

            {/* Inline sync states under the mic (PLAN §6.2). */}
            {pendingRec !== null && !isRecording && (
              <>
                {(pendingStatus === 'pending' || pendingStatus === 'syncing') && (
                  <p className="muted record-saved" role="status">
                    Syncing…
                  </p>
                )}

                {pendingStatus === 'synced' &&
                  (pendingRec.lastError != null ? (
                    <p
                      className={`warning-text record-saved`}
                      role="status"
                    >
                      {pendingRec.lastError}
                    </p>
                  ) : (
                    pendingRec.lastResult && (() => {
                      const { lines, variant } = summarizeSync(pendingRec.lastResult);
                      return (
                        <div
                          className={`${bannerClassName(variant)} record-saved`}
                          role="status"
                        >
                          {lines.map((line, i) => (
                            <p key={i}>{line}</p>
                          ))}
                        </div>
                      );
                    })()
                  ))}
              </>
            )}
          </div>

          {attentionCount > 0 && (
            <p className="pending-badge" role="status">
              {attentionCount} clip{attentionCount === 1 ? '' : 's'}{' '}
              {attentionCount === 1 ? 'needs' : 'need'} attention
            </p>
          )}
        </section>

        {/* Correction surface: only when the clip needs a human or a retry. */}
        {pendingRec !== null &&
          !isRecording &&
          (pendingStatus === 'needs-attention' || pendingStatus === 'failed') && (
            <ConfirmCard
              audioUrl={audioUrl}
              rec={pendingRec}
              confirming={saving}
              error={queueError}
              onSyncComplete={handleSyncComplete}
              onDiscard={handleDiscard}
            />
          )}
      </main>
    </div>
  );
}