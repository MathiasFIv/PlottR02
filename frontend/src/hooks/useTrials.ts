import { useAtom } from 'jotai';
import { useCallback, useEffect } from 'react';
import { ApiError, api } from '../lib/api';
import {
  trialsAtom,
  trialsErrorAtom,
  trialsLoadingAtom,
  trialsUploadingAtom,
  trialsUploadErrorAtom,
} from '../store/atoms';
import type { TrialSummary } from '../types';

/**
 * Data-access hook for the `/api/Trials` resource. Wraps the fetch calls and
 * mirrors their state (list, loading, error, upload) into Jotai atoms.
 */
export function useTrials() {
  const [trials, setTrials] = useAtom(trialsAtom);
  const [loading, setLoading] = useAtom(trialsLoadingAtom);
  const [error, setError] = useAtom(trialsErrorAtom);
  const [uploading, setUploading] = useAtom(trialsUploadingAtom);
  const [uploadError, setUploadError] = useAtom(trialsUploadErrorAtom);

  const fetchTrials = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await api.listTrials();
      setTrials(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load trials');
    } finally {
      setLoading(false);
    }
  }, [setLoading, setError, setTrials]);

  const createTrial = useCallback(
    async (file: File): Promise<TrialSummary | null> => {
      // --- Client-side validation ---
      if (!file.name.toLowerCase().endsWith('.xlsx')) {
        setUploadError('Only .xlsx files are supported.');
        return null;
      }
      if (file.size === 0) {
        setUploadError('The file is empty.');
        return null;
      }
      if (file.size > 50 * 1024 * 1024) {
        setUploadError('File exceeds the 50 MB limit.');
        return null;
      }

      setUploading(true);
      setUploadError(null);
      try {
        const created = await api.createTrial(file);
        // Refresh the list so the new trial appears
        await fetchTrials();
        return created;
      } catch (err) {
        if (err instanceof ApiError) {
          setUploadError(err.message);
        } else {
          setUploadError(
            err instanceof Error ? err.message : 'Upload failed',
          );
        }
        return null;
      } finally {
        setUploading(false);
      }
    },
    [setUploading, setUploadError, fetchTrials],
  );

  // Load the trial list once when the hook is first used.
  useEffect(() => {
    void fetchTrials();
  }, [fetchTrials]);

  return {
    trials,
    loading,
    error,
    uploading,
    uploadError,
    fetchTrials,
    createTrial,
  };
}
