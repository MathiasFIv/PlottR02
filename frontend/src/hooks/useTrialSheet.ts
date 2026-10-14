/**
 * Fetch lifecycle for the read-only sheet — used by BOTH hosts
 * (`views/SheetView.tsx` on `/trials/:trialId/sheet` and
 * `components/SheetPanel.tsx` embedded in RecordView). One hook, one payload
 * atom set (+ the signal-only `sheetRefreshTickAtom` above that block), two
 * mutually exclusive hosts (see the atom block in `store/atoms.ts`).
 *
 * **Scope = initial fetch + manual `refresh()` + snapshot hydrate + the
 * recording-driven tick below.** N4's boundary is consciously crossed by
 * EXACTLY ONE atom observer — the `sheetRefreshTickAtom` effect — and still
 * with ZERO timers and ZERO listeners: no polling, no `visibilitychange`, no
 * `navigator.onLine`. The sync engine (`lib/syncQueue.ts`) bumps the tick
 * only when it contacts the server for a send attempt, and this hook answers
 * with a single re-check (the sheet's `changed:false` fast path makes that a
 * check, not a claim). The manual `refresh()` seam is unchanged and remains
 * what N4's poller will call as its tick.
 *
 * Behaviour:
 * - **Hydrate first:** on mount / trial change the persisted snapshot
 *   (`lib/sheetSnapshot.ts`) is shown immediately as *stale* (`source:'cache'`,
 *   status `loading`) until the network answers.
 * - **Failure split:** a failed fetch with a payload on screen ⇒ `offline`
 *   (keep the copy visible); without one ⇒ `error` / `not-found`. The non-ARM
 *   400 stays `error` so hosts can render the readable state.
 * - **Fast path:** a response with `changed:false` (payload fields present but
 *   null, contract A5) leaves the payload atoms untouched — only freshness is
 *   updated, and `source` is promoted to `'network'`: the server has just
 *   confirmed the payload on screen is current, so a snapshot-hydrated copy is
 *   no longer a "saved copy" (otherwise the pill would claim
 *   `live — saved copy · saved <fetch time>` for a time nothing was written).
 *   Never let a fast-path response blank the grid.
 * - **Clearing:** atoms are cleared on unmount AND on `trialId` change, so a
 *   trial switch can never flash the previous workbook. The tick atom is NOT
 *   cleared — it is signal-only, outside the payload block.
 * - **Recording-driven tick:** a fresh `sheetRefreshTickAtom` value targeting
 *   the mounted trial re-uses the exact `load()` path, *superseding* (never
 *   queueing) an in-flight request — `load()` aborts the previous controller
 *   and re-reads `versionRef`, so one fresh request replaces the stale one.
 *   The `lastSeenSeq` ref is SEEDED from the first render's tick, so mount /
 *   StrictMode re-mount never double-fetches; a leftover tick becomes the next
 *   mount's seed instead of a replay; and a tick for another trial is spent on
 *   sight, so switching back can never replay it (the mount fetch covers it).
 * - **StrictMode (`main.tsx:21`):** the double mount costs a duplicate fetch —
 *   accepted. The cleanup aborts the in-flight request and clears state, so
 *   the superseded response can neither land nor leak.
 */

import { useAtom, useAtomValue } from 'jotai';
import { useCallback, useEffect, useRef } from 'react';
import { ApiError, api } from '../lib/api';
import { shouldConsumeTick } from '../lib/sheetRefreshTick';
import { readSheetSnapshot, writeSheetSnapshot } from '../lib/sheetSnapshot';
import { classifySheetFailure } from '../lib/sheetStatus';
import type { TrialSheetSource, TrialSheetStatus } from '../lib/sheetStatus';
import {
  sheetRefreshTickAtom,
  trialSheetAtom,
  trialSheetErrorAtom,
  trialSheetSourceAtom,
  trialSheetStatusAtom,
  trialSheetUpdatedAtAtom,
} from '../store/atoms';
import type { TrialSheet } from '../types';

/** What a host gets back — the atoms, plus the manual refresh seam. */
export type UseTrialSheetResult = {
  /** Last-good payload (network or snapshot), or null while nothing is loaded. */
  sheet: TrialSheet | null;
  status: TrialSheetStatus;
  error: string | null;
  /** ISO of the last successful payload, or the snapshot's `savedAt`. */
  updatedAt: string | null;
  source: TrialSheetSource;
  /** Manual Refresh — the seam N4's poller tick will reuse. */
  refresh: () => void;
  /** True while a fetch is in flight (drives the Refresh button). */
  refreshing: boolean;
};

export function useTrialSheet(
  trialId: string | null,
): UseTrialSheetResult {
  const [sheet, setSheet] = useAtom(trialSheetAtom);
  const [status, setStatus] = useAtom(trialSheetStatusAtom);
  const [error, setError] = useAtom(trialSheetErrorAtom);
  const [updatedAt, setUpdatedAt] = useAtom(trialSheetUpdatedAtAtom);
  const [source, setSource] = useAtom(trialSheetSourceAtom);
  /** Recording-driven refresh signal — observed, never written (signal-only). */
  const tick = useAtomValue(sheetRefreshTickAtom);

  /** In-flight request for the mounted host — aborted on clear/unmount. */
  const controllerRef = useRef<AbortController | null>(null);
  /**
   * Version of the payload currently held. Doubles as (a) the `since` for the
   * next fetch so a manual Refresh can hit the ~100 B fast path, and (b) the
   * "do we have a payload on screen?" flag used to classify failures.
   */
  const versionRef = useRef<string | null>(null);

  /**
   * Last sheet-refresh tick this instance consumed — SEEDED from the first
   * render (`tick?.seq ?? null`), so mount and StrictMode's re-mount see the
   * same seq and skip (no double-fetch), and a tick left over from a previous
   * mount becomes the next mount's seed instead of a replay.
   */
  const lastSeenSeqRef = useRef<number | null>(tick?.seq ?? null);

  /** Unmount / trial-change reset — also aborts whatever is in flight. */
  const clearSheetState = useCallback(() => {
    controllerRef.current?.abort();
    controllerRef.current = null;
    versionRef.current = null;
    setSheet(null);
    setStatus('idle');
    setError(null);
    setUpdatedAt(null);
    setSource('network');
  }, [setSheet, setStatus, setError, setUpdatedAt, setSource]);

  /**
   * One fetch. Shared by the mount effect and the manual `refresh()` so N4's
   * poller can reuse the exact same path later.
   */
  const load = useCallback(
    async (targetTrialId: string, since: string | null): Promise<void> => {
      controllerRef.current?.abort();
      const controller = new AbortController();
      controllerRef.current = controller;

      setStatus('loading');
      setError(null);

      try {
        const result = await api.getTrialSheet(
          targetTrialId,
          since ?? undefined,
          controller.signal,
        );
        if (controller.signal.aborted) return;

        // Contract A5: `changed:false` ships every payload field as null.
        // Leave the payload atoms alone — only freshness moves.
        if (result.changed === false || result.columns === null || result.rows === null) {
          setStatus('live');
          // The server just vouched that the payload on screen is current, so
          // a snapshot-hydrated `cache` source is promoted to `network`. This
          // branch never writes a snapshot, so leaving `source:'cache'` would
          // make the pill cite a save time that was never persisted.
          setSource('network');
          setError(null);
          setUpdatedAt(new Date().toISOString());
          return;
        }

        setSheet(result);
        setSource('network');
        setStatus('live');
        setError(null);
        setUpdatedAt(new Date().toISOString());
        versionRef.current = result.version;
        // Write-through: the last good payload is always persisted so an
        // offline reload can hydrate it (B1).
        writeSheetSnapshot(targetTrialId, result, 'network');
      } catch (err) {
        if (controller.signal.aborted) return;
        const message =
          err instanceof Error ? err.message : 'Could not load the sheet.';
        const errStatus = err instanceof ApiError ? err.status : null;
        setStatus(classifySheetFailure(message, errStatus, versionRef.current !== null));
        setError(message);
      } finally {
        if (controllerRef.current === controller) {
          controllerRef.current = null;
        }
      }
    },
    [setSheet, setStatus, setError, setUpdatedAt, setSource],
  );

  // Mount / trial change: hydrate the snapshot, then hit the network.
  useEffect(() => {
    if (trialId === null) {
      clearSheetState();
      return;
    }

    const snapshot = readSheetSnapshot(trialId);
    if (snapshot !== null) {
      setSheet(snapshot.sheet);
      setSource('cache');
      setUpdatedAt(snapshot.savedAt);
      versionRef.current = snapshot.sheet.version;
    } else {
      setSheet(null);
      setSource('network');
      setUpdatedAt(null);
      versionRef.current = null;
    }
    // Stale until the network answers (even when there is nothing to show yet,
    // "loading" is the honest state — hosts render their loading copy).
    setStatus('loading');
    setError(null);

    void load(trialId, versionRef.current);

    return () => {
      // Runs on unmount AND before the next trial's effect — the previous
      // trial's grid must never be visible for a frame.
      clearSheetState();
    };
  }, [trialId, clearSheetState, load, setSheet, setSource, setUpdatedAt, setStatus, setError]);

  /**
   * Recording-driven refresh — the ONE atom observer that consciously crosses
   * N4's boundary here, still with zero timers/listeners (no polling, no
   * visibility handlers): `lib/syncQueue.ts` bumps the signal whenever it
   * contacts the server for a send attempt, and this effect re-checks the
   * sheet once per fresh tick.
   */
  useEffect(() => {
    if (tick === null) return;
    // Decide from the PRE-consumption state: fresh tick + mounted trial.
    const shouldLoad = shouldConsumeTick(tick, lastSeenSeqRef.current, trialId);
    // Consume BEFORE the guard: a fresh tick is spent even when it targets
    // another trial (or no trial), so switching back can never replay it —
    // that switch's own mount fetch already covers the trial.
    if (tick.seq !== lastSeenSeqRef.current) {
      lastSeenSeqRef.current = tick.seq;
    }
    if (!shouldLoad || trialId === null) return;
    // Supersede, don't queue: `load()` aborts the in-flight controller and
    // re-reads `versionRef`, so one fresh request replaces the stale one —
    // never parallel requests, never a stale `since`.
    void load(trialId, versionRef.current);
  }, [tick, trialId, load]);

  /** Manual Refresh — the seam N4's poller tick will reuse. */
  const refresh = useCallback(() => {
    if (trialId === null) return;
    void load(trialId, versionRef.current);
  }, [trialId, load]);

  return {
    sheet,
    status,
    error,
    updatedAt,
    source,
    refresh,
    /** True while a fetch is in flight (drives the Refresh button). */
    refreshing: status === 'loading',
  };
}
