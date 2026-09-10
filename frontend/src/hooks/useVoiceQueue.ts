import { useAtom } from 'jotai';
import { useCallback, useState } from 'react';
import {
  getPendingCount,
  newRecording,
  saveRecording as persistRecording,
} from '../lib/audioDb';
import type { AudioRecording } from '../lib/audioDb';
import { pendingRecordingsCountAtom } from '../store/atoms';

/**
 * Local-first queue for voice recordings. New clips are persisted to
 * IndexedDB with status 'pending' (the P1 sync worker picks them up) and the
 * pending-count atom is refreshed so any screen shows a live queue size.
 */
export function useVoiceQueue() {
  const [pendingCount, setPendingCount] = useAtom(pendingRecordingsCountAtom);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /** Re-syncs the pending count from IndexedDB (call after saves/migrations). */
  const refreshPendingCount = useCallback(async () => {
    try {
      const count = await getPendingCount();
      setPendingCount(count);
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : 'Could not read the pending recording queue.',
      );
    }
  }, [setPendingCount]);

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
        const rec = newRecording({ blob, mimeType, trialId, traitOrdinal: 1 });
        await persistRecording(rec);
        await refreshPendingCount();
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
    [refreshPendingCount],
  );

  return {
    pendingCount,
    saving,
    error,
    saveRecording,
    refreshPendingCount,
  };
}