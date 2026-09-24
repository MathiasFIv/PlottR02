import { useAtomValue } from 'jotai';
import { useCallback, useRef, useState } from 'react';
import {
  deleteRecording,
  newRecording,
  saveRecording as persistRecording,
} from '../lib/audioDb';
import type { AudioRecording } from '../lib/audioDb';
import {
  autoSync,
  refreshAttentionCount,
  sweepPendingSyncs,
} from '../lib/syncQueue';
import { recordingsNeedingAttentionCountAtom } from '../store/atoms';

/**
 * Local-first queue for voice recordings. A captured clip is persisted to
 * IndexedDB as 'pending' and auto-synced immediately via `autoSync` — there is
 * no Confirm & queue step any more. The held row (`pendingRec`) tracks the
 * live status (syncing / synced / needs-attention / failed) so RecordView can
 * render inline states; the attention badge counts recordings that need a
 * human (needs-attention + failed), refreshed by the engine after every
 * send/correction outcome.
 */
export function useVoiceQueue() {
  const attentionCount = useAtomValue(
    recordingsNeedingAttentionCountAtom,
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** The clip most recently saved from the mic — drives the inline sync states. */
  const [pendingRec, setPendingRec] = useState<AudioRecording | null>(null);
  /**
   * Synchronous re-entrancy guard for discard. `saving` state commits one
   * render later, so two rapid taps within the same tick could both pass the
   * UI disable — this ref closes that gap (early-return false).
   */
  const opInFlightRef = useRef(false);

  /** Persists a finished clip to IndexedDB as a 'pending' recording. */
  const saveRecording = useCallback(
    async (
      blob: Blob,
      mimeType: string,
      trialId: string | null,
    ): Promise<AudioRecording | null> => {
      setSaving(true);
      setError(null);
      try {
        const rec = newRecording({ blob, mimeType, trialId });
        await persistRecording(rec);
        return rec;
      } catch {
        // Raw IndexedDB errors (e.g. "Transaction failed") mean nothing to the
        // worker — surface a fixed, actionable message instead.
        setError('Could not save the recording. Please try again.');
        return null;
      } finally {
        setSaving(false);
      }
    },
    [],
  );

  /** Deletes a captured clip (Re-record / start-over). */
  const discardRecording = useCallback(
    async (id: string): Promise<boolean> => {
      if (opInFlightRef.current) return false;
      opInFlightRef.current = true;
      setSaving(true);
      setError(null);
      try {
        await deleteRecording(id);
        // A discarded row may have been 'failed' (counted) — keep the badge honest.
        await refreshAttentionCount();
        return true;
      } catch {
        setError('Could not discard the recording. Please try again.');
        return false;
      } finally {
        opInFlightRef.current = false;
        setSaving(false);
      }
    },
    [],
  );

  return {
    attentionCount,
    saving,
    error,
    pendingRec,
    setPendingRec,
    saveRecording,
    discardRecording,
    // Engine functions are module-stable identities — no useCallback needed.
    autoSync,
    sweepPendingSyncs,
    refreshAttentionCount,
  };
}