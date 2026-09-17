import { useState } from 'react';
import type { SyncRecordingResponse } from '../lib/api';
import { getRecordingById, updateRecording } from '../lib/audioDb';
import type { AudioRecording } from '../lib/audioDb';
import { applyCorrection, autoSync, summarizeSync } from '../lib/syncQueue';
import { Button } from './Button';

interface ConfirmCardProps {
  /** Playback source for the captured clip (object URL from useVoiceRecorder). */
  audioUrl: string | null;
  /** The persisted recording row — drives status + pre-filled fields. */
  rec: AudioRecording | null;
  /** True while a save/discard operation is in flight — disables actions. */
  confirming: boolean;
  /** Queue error to surface (e.g. when a save fails). */
  error: string | null;
  /**
   * Called after a sync/correction lands a new row state — the parent
   * re-fetches the held row (same id) and refreshes the attention badge.
   */
  onSyncComplete: () => void;
  /** Re-record / discard: deletes the held row and starts the mic fresh. */
  onDiscard: () => void;
}

const INTEGER_HINT = 'Enter a whole number ≥ 1.';

/**
 * Correction/review surface for a recording that came back with a problem:
 * - `'needs-attention'` → "Fix & send": `applyCorrection` (JSON body, no file —
 *   the server reuses the stored blob + transcript). 409 is treated as "already
 *   saved — nothing to correct"; any other error flips the row to `'failed'`.
 * - `'failed'` → "Retry sync": re-`api.syncRecording` (same multipart, same
 *   idempotency key — a lost-response retry is safe: the server answers Replay
 *   instead of double-writing).
 *
 * Fields pre-fill from the last result's parsed values (the transcript-derived
 * parse) so the worker validates — and corrects — what Whisper heard. Playback
 * and the transcript string are shown alongside. Re-record stays for all
 * outcomes.
 */
export function ConfirmCard({
  audioUrl,
  rec,
  confirming,
  error,
  onSyncComplete,
  onDiscard,
}: ConfirmCardProps) {
  const status = rec?.status ?? 'pending';
  const lastResult = rec?.lastResult;

  // Pre-fill from the last /sync or /correct response's parsed fields: a
  // needs-attention clip opens with the transcript-derived values editable.
  const [plot, setPlot] = useState(() =>
    String(lastResult?.parsedPlot ?? ''),
  );
  const [subsample, setSubsample] = useState(() =>
    String(lastResult?.parsedSubsample ?? ''),
  );
  const [value, setValue] = useState<number | null>(
    lastResult?.parsedValue ?? null,
  );

  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [result, setResult] = useState<SyncRecordingResponse | null>(null);

  const plotNumber = Number(plot);
  const subsampleNumber = Number(subsample);
  const plotValid = Number.isInteger(plotNumber) && plotNumber >= 1;
  const subsampleValid =
    Number.isInteger(subsampleNumber) && subsampleNumber >= 1;
  // Value chips are 0–10 by construction, so `value` is always in range here.
  const valueValid = value === null || (Number.isInteger(value) && value >= 0 && value <= 10);

  const plotEmpty = plot.trim() === '';
  const subsampleEmpty = subsample.trim() === '';

  // Only flag a field once something invalid has been typed into it.
  const showPlotError = !plotEmpty && !plotValid;
  const showSubsampleError = !subsampleEmpty && !subsampleValid;

  // The card is the correction surface for needs-attention + failed only.
  if (status !== 'needs-attention' && status !== 'failed') {
    return null;
  }

  // Fix & send is a full confirmation: all three fields required and valid.
  const canFix =
    status === 'needs-attention' &&
    !plotEmpty &&
    !subsampleEmpty &&
    value !== null &&
    plotValid &&
    subsampleValid &&
    !sending &&
    !confirming;

  // Retry may re-send the row exactly as-is (empty → null → transcript-
  // authoritative on the wire), or carry typed edits as confirmed fields.
  const canRetry =
    status === 'failed' &&
    (plotEmpty || plotValid) &&
    (subsampleEmpty || subsampleValid) &&
    valueValid &&
    !sending &&
    !confirming;

  /** Fix & send — human correction via the JSON /correct endpoint. */
  async function handleFix() {
    if (!canFix || rec === null) return;
    setSending(true);
    setSendError(null);
    setResult(null);
    try {
      const outcome = await applyCorrection(rec.id, {
        plot: plotNumber,
        subsample: subsampleNumber,
        value,
        traitOrdinal: rec.traitOrdinal,
      });
      if (outcome.kind === 'success') {
        setResult(outcome.result);
      } else if (outcome.kind === 'already-saved') {
        setSendError('Already saved — nothing to correct');
      } else {
        setSendError(outcome.message);
      }
    } finally {
      setSending(false);
      onSyncComplete();
    }
  }

  /** Retry sync — re-posts the same multipart payload (idempotency-safe). */
  async function handleRetry() {
    if (!canRetry || rec === null) return;
    setSending(true);
    setSendError(null);
    setResult(null);
    try {
      // Re-fetch the latest row so we never send stale data.
      const fresh = (await getRecordingById(rec.id).catch(() => null)) ?? rec;
      // Persist any edited fields so the re-send carries the confirmed values;
      // untouched fields stay null → transcript-authoritative on the wire.
      const edited: AudioRecording = {
        ...fresh,
        plot: plotEmpty ? fresh.plot : plotNumber,
        subsample: subsampleEmpty ? fresh.subsample : subsampleNumber,
        value: value === null ? fresh.value : value,
      };
      await updateRecording(edited).catch(() => {
        // Non-critical — the send below reads from the row.
      });

      const ok = await autoSync(edited.id);
      const updated = await getRecordingById(edited.id).catch(() => null);
      if (ok && updated?.lastResult) {
        setResult(updated.lastResult);
      } else {
        setSendError(updated?.lastError ?? 'Sync failed — try again.');
      }
    } finally {
      setSending(false);
      onSyncComplete();
    }
  }

  const transcript = lastResult?.transcript.trim() ?? '';

  return (
    <section className="card confirm-card">
      <h2 className="heading">
        {status === 'failed' ? 'Sync failed' : 'Check rating'}
      </h2>

      {audioUrl ? (
        <audio
          className="confirm-card__playback"
          controls
          src={audioUrl}
          preload="metadata"
          aria-label="Recorded voice note"
        />
      ) : (
        <p className="muted confirm-card__playback-hint" role="status">
          Playback unavailable for this clip.
        </p>
      )}

      {/* What Whisper heard — the worker validates the transcript, not a guess. */}
      {transcript !== '' ? (
        <p className="muted confirm-card__transcript" role="status">
          Whisper heard: «{transcript}»
        </p>
      ) : (
        <p className="muted confirm-card__transcript" role="status">
          No transcript — the server could not transcribe this clip. Check the
          audio before sending.
        </p>
      )}

      <div className="field">
        <label className="field__label" htmlFor="confirm-plot">
          Plot
        </label>
        <input
          id="confirm-plot"
          className={[
            'input',
            'confirm-card__field',
            showPlotError ? 'input--error' : '',
          ]
            .filter(Boolean)
            .join(' ')}
          inputMode="numeric"
          autoComplete="off"
          placeholder="e.g. 12"
          value={plot}
          onChange={(e) => setPlot(e.target.value)}
        />
        {showPlotError && (
          <p className="confirm-card__field-error" role="alert">
            {INTEGER_HINT}
          </p>
        )}
      </div>

      <div className="field">
        <label className="field__label" htmlFor="confirm-subsample">
          Subsample
        </label>
        <input
          id="confirm-subsample"
          className={[
            'input',
            'confirm-card__field',
            showSubsampleError ? 'input--error' : '',
          ]
            .filter(Boolean)
            .join(' ')}
          inputMode="numeric"
          autoComplete="off"
          placeholder="e.g. 2"
          value={subsample}
          onChange={(e) => setSubsample(e.target.value)}
        />
        {showSubsampleError && (
          <p className="confirm-card__field-error" role="alert">
            {INTEGER_HINT}
          </p>
        )}
      </div>

      <div className="field">
        <span className="field__label" id="confirm-value-label">
          Value
        </span>
        <div
          className="value-chips"
          role="group"
          aria-labelledby="confirm-value-label"
        >
          {Array.from({ length: 11 }, (_, option) => option).map((option) => (
            <button
              key={option}
              type="button"
              className={`value-chips__chip${value === option ? ' value-chips__chip--active' : ''}`}
              aria-pressed={value === option}
              onClick={() => setValue(option)}
            >
              {option}
            </button>
          ))}
        </div>
        {value === null && (
          <p className="muted value-chips__hint">Tap a score 0–10 to select it.</p>
        )}
      </div>

      {/* Queue-level error (save/discard) */}
      {error && (
        <p className="error-text confirm-card__error" role="alert">
          {error}
        </p>
      )}

      {/* Sync / correction error */}
      {sendError && (
        <p className="error-text confirm-card__error" role="alert">
          {sendError}
        </p>
      )}

      {/* Sync / correction result banner */}
      {result && (() => {
        const { lines, variant } = summarizeSync(result);
        const className =
          variant === 'success'
            ? 'success-text'
            : variant === 'error'
              ? 'error-text'
              : 'warning-text';
        return (
          <div className={className} role="status">
            {lines.map((line, i) => (
              <p key={i}>{line}</p>
            ))}
          </div>
        );
      })()}

      <div className="confirm-card__actions">
        {status === 'needs-attention' && (
          <Button
            className="confirm-card__confirm"
            onClick={() => void handleFix()}
            disabled={!canFix}
          >
            {sending ? 'Sending…' : 'Fix & send'}
          </Button>
        )}

        {status === 'failed' && (
          <Button
            className="confirm-card__confirm"
            onClick={() => void handleRetry()}
            disabled={!canRetry}
          >
            {sending ? 'Sending…' : 'Retry sync'}
          </Button>
        )}

        <Button
          variant="secondary"
          onClick={onDiscard}
          disabled={confirming || sending}
        >
          Re-record
        </Button>
      </div>
    </section>
  );
}