import { useAtom, useSetAtom } from 'jotai';
import { useCallback } from 'react';
import { api } from '../lib/api';
import {
  lastRatingResultAtom,
  ratingCellsAtom,
  ratingReadErrorAtom,
  ratingReadLoadingAtom,
  ratingRecordErrorAtom,
  ratingRecordLoadingAtom,
} from '../store/atoms';
import type {
  RatingCell,
  RecordSubsampleRatingsCommand,
  SubsampleRatingResult,
} from '../types';

/**
 * Data-access hook for the `/api/Ratings` resource. Wraps the fetch calls and
 * mirrors their state (loaded cells, loading, error, last result) into Jotai
 * atoms.
 */
export function useRatings() {
  const [ratingCells, setRatingCells] = useAtom(ratingCellsAtom);
  const [recordLoading, setRecordLoading] = useAtom(ratingRecordLoadingAtom);
  const [recordError, setRecordError] = useAtom(ratingRecordErrorAtom);
  const [readLoading, setReadLoading] = useAtom(ratingReadLoadingAtom);
  const [readError, setReadError] = useAtom(ratingReadErrorAtom);
  const setLastResult = useSetAtom(lastRatingResultAtom);

  const recordRating = useCallback(
    async (
      input: RecordSubsampleRatingsCommand,
    ): Promise<SubsampleRatingResult | null> => {
      setRecordLoading(true);
      setRecordError(null);
      try {
        const result = await api.recordRating(input);
        setLastResult(result);
        return result;
      } catch (err) {
        setRecordError(err instanceof Error ? err.message : 'Failed to record rating');
        return null;
      } finally {
        setRecordLoading(false);
      }
    },
    [setRecordLoading, setRecordError, setLastResult],
  );

  const readPlotColumn = useCallback(
    async (plot: number, traitOrdinal: number): Promise<RatingCell[] | null> => {
      setReadLoading(true);
      setReadError(null);
      try {
        const cells = await api.readPlotColumn(plot, traitOrdinal);
        setRatingCells(cells);
        return cells;
      } catch (err) {
        setReadError(err instanceof Error ? err.message : 'Failed to load ratings');
        return null;
      } finally {
        setReadLoading(false);
      }
    },
    [setReadLoading, setReadError, setRatingCells],
  );

  const clearResult = useCallback(() => {
    setLastResult(null);
  }, [setLastResult]);

  return {
    ratingCells,
    recordLoading,
    recordError,
    readLoading,
    readError,
    recordRating,
    readPlotColumn,
    clearResult,
  };
}