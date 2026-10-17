import { useAtom, useAtomValue, useStore } from 'jotai';
import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { ConfirmCard } from '../components/ConfirmCard';
import { SheetPanel } from '../components/SheetPanel';
import { TopBar } from '../components/TopBar';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { useVoiceQueue } from '../hooks/useVoiceQueue';
import { useVoiceRecorder } from '../hooks/useVoiceRecorder';
import { api } from '../lib/api';
import { getRecordingById } from '../lib/audioDb';
import { deriveEndOfSheet, deriveNextTarget, resolveNextTarget } from '../lib/nextTarget';
import { formatAnchor, isAnchorForTrial } from '../lib/sheetAnchor';
import {
  applyCellEdit,
  classifyVoiceCellEditResult,
  shouldFallbackToKeyboard,
} from '../lib/sheetCellEdit';
import type { SheetCellEdit } from '../lib/sheetCellEdit';
import { bumpSheetRefresh } from '../lib/sheetRefreshSignal';
import { summarizeSync } from '../lib/syncQueue';
import {
  activeTrialIdAtom,
  sheetCellEditAtom,
  sheetCellEditStatusAtom,
  sheetLastWrittenAtom,
  sheetSelectedAtom,
  trialSheetAtom,
} from '../store/atoms';

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
  const navigate = useNavigate();
  const { logout } = useAuth();
  const { trials, loading, error: trialsError } = useTrials();
  const [activeTrialId, setActiveTrialId] = useAtom(activeTrialIdAtom);
  /** N3 last-written pair — reactive (the sync engine writes it non-React). */
  const lastWrittenRaw = useAtomValue(sheetLastWrittenAtom);
  /** The sheet payload on screen — the rows the next target derives from. */
  const sheetPayload = useAtomValue(trialSheetAtom);
  /**
   * Write anchor (set by the `SheetPanel` grid below, through the same
   * default store). Rendered HERE, next to the mic, so the anchored row is
   * announced even when the grid is scrolled off screen — that is the whole
   * point of the line. Trial-guarded: a tap from another trial never renders
   * for this one.
   */
  const anchorRaw = useAtomValue(sheetSelectedAtom);
  const anchor = isAnchorForTrial(anchorRaw, activeTrialId);

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
   * Non-React store access for the voice cell write: the stop branch reads the
   * armed cell and clears it WITHOUT a render round-trip (the atom may change
   * between the read and the write).
   */
  const store = useStore();

  /**
   * The live VOICE arm: a double-tapped cell on the WILLING trial, waiting for
   * a spoken number. `null` in keyboard mode, for another trial, or when no
   * edit is open. Identity is stable while the atom is unchanged, so the
   * auto-start effect below can compare it by reference.
   */
  const edit = useAtomValue(sheetCellEditAtom);
  const voiceArm =
    edit !== null && edit.mode === 'voice' && edit.trialId === activeTrialId
      ? edit
      : null;
  /**
   * Whether a double-tap should ARM the cell for voice (recorder usable) or
   * fall back to the inline keyboard editor. Passed to the sheet panel; the
   * mirror of `shouldFallbackToKeyboard` but expressed positively here.
   */
  const voiceEnabled =
    permissionState !== 'unsupported' && permissionState !== 'denied';

  /**
   * The arm the auto-start effect has already started a recorder for — a ref,
   * not state, so it never re-renders and re-runs the effect on a loop. It
   * blocks a re-start while the SAME arm is live (e.g. after a failed write,
   * where the arm is kept for a manual mic retry).
   */
  const startedArmRef = useRef<SheetCellEdit | null>(null);

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

  /**
   * Trial change while armed: never strand another trial's arm. The sheet
   * panel's own hook clears on unmount/trial change, but RecordView reads the
   * atom directly, so it clears here too — a switch can never leave a yellow
   * cell (or a primed recorder) pointing at a sheet that is no longer shown.
   */
  useEffect(() => {
    const current = store.get(sheetCellEditAtom);
    if (current !== null && current.trialId !== activeTrialId) {
      store.set(sheetCellEditAtom, null);
      store.set(sheetCellEditStatusAtom, { saving: false, error: null });
    }
  }, [activeTrialId, store]);

  /**
   * Auto-start the recorder for a live voice arm:
   * - a mic that cannot record (`unsupported`/`denied`) flips the arm IN PLACE
   *   to the keyboard fallback, prefilled from the cell on screen;
   * - otherwise the recorder starts exactly once per arm (the `startedArmRef`
   *   guard stops a restart loop after a failed write, when the arm is kept
   *   for a manual mic retry).
   */
  useEffect(() => {
    if (voiceArm === null) {
      startedArmRef.current = null;
      return;
    }
    if (shouldFallbackToKeyboard(permissionState)) {
      const payload =
        sheetPayload !== null && sheetPayload.trialId === voiceArm.trialId
          ? sheetPayload
          : null;
      const current =
        payload?.rows?.find(
          (r) => r.plot === voiceArm.plot && r.subsample === voiceArm.subsample,
        )?.values[voiceArm.valuesIndex] ?? null;
      store.set(sheetCellEditAtom, {
        ...voiceArm,
        mode: 'keyboard',
        value: current === null ? '' : String(current),
      });
      return;
    }
    if (!isRecording && startedArmRef.current !== voiceArm) {
      startedArmRef.current = voiceArm;
      void startRecording();
    }
  }, [
    voiceArm,
    permissionState,
    isRecording,
    startRecording,
    store,
    sheetPayload,
  ]);

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

    // A matching VOICE arm means this clip is a CELL value, not a free-form
    // note: write it straight to the addressed cell and NEVER fall through to
    // the IndexedDB/autoSync path below. Read the atom live (the arm may have
    // been cleared since this effect was scheduled).
    const armed = store.get(sheetCellEditAtom);
    const voiceArmNow =
      armed !== null && armed.mode === 'voice' && armed.trialId === trialId
        ? armed
        : null;

    let cancelled = false;
    (async () => {
      if (voiceArmNow !== null) {
        try {
          const data = await api.editCellFromVoice(trialId, {
            plot: voiceArmNow.plot,
            subsample: voiceArmNow.subsample,
            traitOrdinal: voiceArmNow.valuesIndex + 1,
            overwrite: true,
            audio: audioBlob,
            mimeType: mimeType ?? '',
          });
          if (cancelled) return;
          const outcome = classifyVoiceCellEditResult(data);
          if (outcome.kind === 'written') {
            // Reconcile the grid optimistically (skip the write when the ref
            // is unchanged), signal a re-check, close the arm, clear the clip.
            const payload = store.get(trialSheetAtom);
            if (
              payload !== null &&
              payload.trialId === trialId &&
              payload.rows !== null
            ) {
              const nextRows = applyCellEdit(
                payload.rows,
                voiceArmNow,
                outcome.value,
              );
              if (nextRows !== payload.rows) {
                store.set(trialSheetAtom, { ...payload, rows: nextRows });
              }
            }
            bumpSheetRefresh(trialId);
            store.set(sheetCellEditAtom, null);
            store.set(sheetCellEditStatusAtom, { saving: false, error: null });
            reset();
            return;
          }
          // not_understood / row_not_found / cell_already_filled /
          // unexpected — KEEP the arm so a mic tap retries.
          const message =
            outcome.kind === 'not_understood'
              ? "Couldn't understand a number from 0 to 10 — tap the mic and try again."
              : outcome.kind === 'row_not_found'
                ? 'That row is no longer in the sheet.'
                : outcome.kind === 'cell_already_filled'
                  ? 'That cell already has a value.'
                  : 'That cell could not be updated.';
          store.set(sheetCellEditStatusAtom, { saving: false, error: message });
          reset();
          return;
        } catch (err) {
          if (cancelled) return;
          const message =
            err instanceof Error ? err.message : 'Could not save the value.';
          store.set(sheetCellEditStatusAtom, { saving: false, error: message });
          reset();
          return;
        }
      }

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
  }, [
    audioBlob,
    isRecording,
    mimeType,
    saveRecording,
    autoSync,
    setPendingRec,
    store,
    reset,
  ]);

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
    // A live voice arm took no queue row: this clip is a CELL value, so skip
    // the discard/clear branch entirely and just reset the recorder — a mic
    // tap then starts a fresh take and the stop branch rewrites the cell.
    if (voiceArm !== null) {
      reset();
      void startRecording();
      return;
    }
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

  // Next write target for a "values-only" clip — derived reactively, never a
  // stored "+1": the last-written pair plus its `blockFull` flag (atom,
  // guarded to THIS trial) STAYS on the written row while the block is
  // unfilled and advances to the NEXT row (crossing plot boundaries) once it
  // is full; only the true end of the sheet yields no target. When no payload
  // is loaded yet (sheet still fetching, offline), fall back to the persisted
  // snapshot-derived resolution — same derivation, best rows available.
  const rows =
    activeTrialId !== null && sheetPayload?.trialId === activeTrialId
      ? sheetPayload.rows
      : null;
  const lastWritten =
    activeTrialId !== null && lastWrittenRaw?.trialId === activeTrialId
      ? lastWrittenRaw
      : null;
  const nextTarget =
    activeTrialId === null
      ? null
      : rows !== null
        ? deriveNextTarget(rows, lastWritten, lastWritten?.blockFull ?? false)
        : resolveNextTarget(activeTrialId);
  const endOfSheetTarget = deriveEndOfSheet(rows, lastWritten);

  /**
   * Variant-aware color class for the inline outcome banner. The `synced` case
   * with a stale `lastError` (a 409 "already saved" correction) renders as a
   * warning instead of dropping the message.
   */
  function bannerClassName(variant: 'success' | 'warning' | 'error'): string {
    if (variant === 'success') return 'text-sm text-success mt-0';
    if (variant === 'error') return 'text-sm text-error mt-0';
    return 'text-sm text-warning bg-warning/15 rounded-md px-3 py-2 mt-0';
  }

  return (
    <div>
      <TopBar onLogout={handleLogout} />

      <main className="mx-auto grid max-w-[480px] grid-cols-[minmax(0,1fr)] gap-4 px-4 py-6 sheet:max-w-[1200px] sheet:grid-cols-[minmax(0,480px)_minmax(0,1fr)] sheet:items-start">
        {/* One grid column keeps today's 16px stack exactly as it was; the
            sheet panel below is a SIBLING of this column, not a new card
            inside it (PLAN_2026-10-07 §6.1). */}
        <div className="grid gap-4 min-w-0">
          <section className="card card-border bg-base-100 shadow-sm p-6">
            <h2 className="text-lg font-semibold text-base-content">Trial</h2>
            <div className="mt-3 mb-1">
              <label
                className="block mb-1.5 text-sm font-medium text-base-content/80"
                htmlFor="record-trial"
              >
                Work on
              </label>
              <select
                id="record-trial"
                name="record-trial"
                className="select w-full"
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
            <p className="text-sm text-base-content/70">Recordings are synced under this trial.</p>
          </section>

          {!loading && trials.length === 0 && (
            <p
              className="text-sm text-base-content/70 px-4 py-3 bg-base-100 border border-dashed border-base-300 rounded-lg"
              role="status"
            >
              No trials yet — upload a workbook from the Dashboard and they will
              show up here.
            </p>
          )}
          {trialsError && <p className="text-sm mt-3 text-error">{trialsError}</p>}

          <section className="card card-border bg-base-100 shadow-sm p-6 grid gap-4">
            <h2 className="text-lg font-semibold text-base-content">Voice note</h2>
            <p className="text-sm text-base-content/70">
              Tap to start, tap again to stop. The clip saves and syncs the
              moment the mic stops.
            </p>

            {/* Write anchor status — PERSISTENT and independent of the grid
                below, so it renders even when the sheet is scrolled off
                screen. Same `muted record-saved` + role=status convention as
                the Next / End-of-sheet lines that follow. */}
            {anchor !== null && (
              <p className="text-sm text-base-content/70 mt-0" role="status">
                Anchored: {formatAnchor(anchor)} — speak numbers to fill. Tap
                the row again to clear.
              </p>
            )}

            {/* Voice-arm status — a double-tapped cell is armed and the
                recorder runs: the same `muted + role=status` convention as
                the anchor / next-target lines, and the ONLY on-screen cue
                outside the grid that the next clip writes a cell. */}
            {voiceArm !== null && (
              <p className="text-sm text-warning mt-0" role="status">
                Editing Plot {voiceArm.plot} · Sub {voiceArm.subsample} — speak
                a number, then tap to stop.
              </p>
            )}

            {nextTarget !== null ? (
              <p className="text-sm text-base-content/70 mt-0" role="status">
                Next: Plot {nextTarget.plot} · Sub {nextTarget.subsample}
              </p>
            ) : endOfSheetTarget !== null &&
              activeTrialId !== null &&
              sheetPayload?.trialId === activeTrialId ? (
              <p className="text-sm text-base-content/70 mt-0" role="status">
                End of sheet - last: Plot {endOfSheetTarget.plot} · Sub{' '}
                {endOfSheetTarget.subsample}
              </p>
            ) : null}

            <div className="grid justify-items-center gap-3.5 pt-3 pb-1">
              <button
                type="button"
                className={`w-[88px] h-[88px] rounded-full grid place-items-center transition-colors cursor-pointer disabled:bg-base-300 disabled:text-base-content/40 disabled:shadow-none disabled:cursor-not-allowed${
                  isRecording
                    ? ' bg-error text-error-content motion-safe:animate-rec-pulse'
                    : ' bg-primary text-primary-content shadow-[0_4px_12px_rgba(5,150,105,0.35)] hover:bg-primary/90 active:scale-[0.96]'
                }`}
                onClick={() => (isRecording ? stopRecording() : handleStart())}
                disabled={micDisabled}
                aria-label={isRecording ? 'Stop recording' : 'Start recording'}
                aria-pressed={isRecording}
              >
                <span
                  className={
                    isRecording
                      ? 'w-[22px] h-[22px] rounded-[4px] border-0 bg-error-content'
                      : 'w-[22px] h-[22px] rounded-full border-4 border-current'
                  }
                  aria-hidden="true"
                />
              </button>

              {isRecording && (
                <p className="text-[28px] font-bold tabular-nums text-base-content" role="timer" aria-live="off">
                  {formatDuration(durationSeconds)}
                </p>
              )}

              {permissionState === 'unsupported' && (
                <p className="text-sm text-base-content/70 text-center max-w-[32ch]" role="status">
                  Microphone recording is not supported in this browser — try a
                  recent version of Chrome, Edge, Safari, or Firefox.
                </p>
              )}

              {recorderError && !isRecording && (
                <p className="text-sm text-error text-center mt-0" role="alert">
                  {recorderError}
                </p>
              )}

              {audioBlob !== null && !isRecording && pendingRec === null && (
                saving ? (
                  <p className="text-sm text-base-content/70 mt-0" role="status">
                    Saving…
                  </p>
                ) : (
                  <p className="text-sm text-error mt-0" role="alert">
                    {queueError ?? 'The recording could not be saved.'}{' '}
                    <button
                      type="button"
                      className="link link-primary text-sm font-medium bg-transparent border-0 p-0"
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
                    <p className="text-sm text-base-content/70 mt-0" role="status">
                      Syncing…
                    </p>
                  )}

                  {pendingStatus === 'synced' &&
                    (pendingRec.lastError != null ? (
                      <p
                        className="text-sm text-warning bg-warning/15 rounded-md px-3 py-2 mt-0"
                        role="status"
                      >
                        {pendingRec.lastError}
                      </p>
                    ) : (
                      pendingRec.lastResult && (() => {
                        const { lines, variant } = summarizeSync(pendingRec.lastResult);
                        return (
                          <div
                            className={bannerClassName(variant)}
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
              <p className="badge badge-info justify-self-center" role="status">
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
        </div>

        {/* Sheet panel (N2, PLAN_2026-10-07 §6.1): last child of the record
            screen so the mic + correction surface keep thumb reach; rendered
            only while a trial is active. It keys off `activeTrialId` alone and
            owns its own fetch/snapshot lifecycle — switching trials changes
            the prop, which clears the sheet atoms and refetches. */}
        {activeTrialId !== null && (
          <section className="record-sheet-card card card-border bg-base-100 shadow-sm p-6 min-w-0 sheet:sticky sheet:top-4 sheet:flex sheet:flex-col sheet:max-h-[calc(100vh-2rem)] sheet:overflow-hidden">
            <SheetPanel trialId={activeTrialId} voiceEnabled={voiceEnabled} />
          </section>
        )}
      </main>
    </div>
  );
}