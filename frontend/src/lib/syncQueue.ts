/**
 * Voice-to-Excel auto-sync engine (framework-free — no React dependency).
 *
 * Owns the send pipeline for captured clips: pending → syncing → outcome
 * (synced / needs-attention / failed), plus the startup sweep and the
 * attention-count refresh. Importable from both the React hook (useVoiceQueue)
 * and vitest unit tests.
 *
 * The wire contract is PLAN §6 / §7: `POST /api/Recordings/sync` is the only
 * send path (idempotency-keyed on clientRecordingId → Replay on lost-response
 * retries); `POST /api/Recordings/{id}/correct` is the human correction with a
 * JSON body and no file. `needsReview` on a result opens the correction surface.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { ApiError, api } from './api';
import type { CorrectRecordingRequest, SyncRecordingResponse } from './api';
import {
  getCountByStatus,
  getRecordingById,
  getRecordingsByStatus,
  updateRecording,
} from './audioDb';
import { nextTick } from './sheetRefreshTick';
import { resolveNextTarget } from './nextTarget';
import { advanceSheetTarget } from './sheetTarget';
import { recordingsNeedingAttentionCountAtom, sheetRefreshTickAtom } from '../store/atoms';

/** Ids with a send currently in flight — guards sweep + UI double-sends. */
const inFlight = new Set<string>();

/**
 * Recomputes the attention-count atom: status 'needs-attention' (correction
 * surface open) + 'failed' (must be retried). Non-critical when the IndexedDB
 * read fails — the badge can go stale until the next refresh, matching the
 * "badge may be stale" behaviour of the old flow (PLAN §7).
 */
export async function refreshAttentionCount(): Promise<void> {
  try {
    const [attention, failed] = await Promise.all([
      getCountByStatus('needs-attention'),
      getCountByStatus('failed'),
    ]);
    getDefaultStore().set(recordingsNeedingAttentionCountAtom, attention + failed);
  } catch {
    // IndexedDB read failure — non-critical; refreshed on the next event.
  }
}

/**
 * Signals a mounted sheet host to re-check the grid — call ONLY where the
 * server was actually contacted for a send attempt (a tick is a *check*, not
 * a claim: the sheet's `changed:false` fast path answers without touching the
 * payload atoms, so a no-op outcome can never fabricate a change). A null or
 * empty trial id has no sheet to refresh, so it never bumps. Same non-React
 * write path as `refreshAttentionCount` above — `getDefaultStore()`.
 */
function bumpSheetRefresh(trialId: string | null): void {
  if (trialId === null || trialId === '') return;
  const store = getDefaultStore();
  store.set(sheetRefreshTickAtom, nextTick(store.get(sheetRefreshTickAtom), trialId));
}

/**
 * Sends one recording to the backend: row → 'syncing' → outcome.
 *
 * - 2xx: `needsReview` → 'needs-attention' (correction surface), else 'synced';
 *   `syncAttempts` increments and the response is persisted as `lastResult`.
 * - error (network or ApiError): 'failed' + `lastError`; retried by the same
 *   call — the idempotency key makes a lost-response replay safe (server
 *   answers Replay instead of double-writing).
 *
 * Every dispatched send bumps the sheet-refresh signal — in BOTH the resolve
 * and the reject branch — so a mounted sheet host re-checks the grid once per
 * server contact.
 *
 * Returns false when the row is missing, already in flight, or the send failed.
 */
export async function autoSync(recordingId: string): Promise<boolean> {
  const id = recordingId;
  if (inFlight.has(id)) return false;
  inFlight.add(id);
  try {
    const rec = await getRecordingById(id);
    if (rec === null) return false;

    /**
     * First-send snapshot (done ONCE, not on retries): for a values-only clip
     * whose row has no snapshot yet (`undefined` = not yet snapshotted, per
     * `AudioRecording.defaultTarget`), freeze the file-derived next target
     * (`resolveNextTarget`: last-written pair + the sheet payload's rows[])
     * onto the row before the wire. Retries then replay the target the clip
     * was INTENDED for, even when other clips have since advanced the live
     * memory — a frozen `null` stays null too (an absent key is the ONLY
     * thing that re-resolves). `defaultTarget` is persisted with the row (the
     * `status: 'syncing'` update below carries it), so a failure keeps the
     * snapshot for the fix path. `null` snapshots are also persisted — the
     * read was done.
     */
    const sendRec =
      !('defaultTarget' in rec) && rec.plot == null && rec.subsample == null
        ? { ...rec, defaultTarget: resolveNextTarget(rec.trialId ?? '') }
        : rec;

    let result: SyncRecordingResponse;
    try {
      await updateRecording({ ...sendRec, status: 'syncing' });
      try {
        result = await api.syncRecording(sendRec);
      } finally {
        // The server was contacted for this send attempt — tick in BOTH the
        // resolve and the reject branch (a lost-response-after-commit failure
        // can still have written cells, and a `changed:false` answer is a
        // check, not a claim). The local early returns above never reach this
        // line, and nothing ticks before the request is dispatched.
        bumpSheetRefresh(sendRec.trialId);
      }
    } catch (err) {
      const message =
        err instanceof Error ? err.message : 'Sync failed unexpectedly';
      await updateRecording({
        ...sendRec,
        status: 'failed',
        lastError: message,
      }).catch(() => {
        // IndexedDB write failure — non-critical; badge may be stale.
      });
      await refreshAttentionCount();
      return false;
    }

    // Remember the resolved write target ("plot/subsample default memory"):
    // the store now holds the LAST-WRITTEN pair as-is, and the next
    // values-only clip continues at the row that follows it in the sheet
    // payload (derived at read time — `lib/nextTarget.ts`), so the run can
    // cross plot boundaries and never invents a pair the file lacks.
    // Flagged/SkippedPresent/Replay results do NOT move it — the operator
    // gets a correction surface, not a silent continuation.
    // `advanceSheetTarget` = `rememberWrittenTarget` (persistence)
    // + the last-written atom (N3), seeded with the trait column.
    if (result.status === 'Written') {
      advanceSheetTarget(sendRec.trialId ?? '', {
        plot: result.plot,
        subsample: result.subsample,
        traitOrdinal: result.traitOrdinal,
      });
    }

    await updateRecording({
      ...sendRec,
      status: result.needsReview ? 'needs-attention' : 'synced',
      syncAttempts: (rec.syncAttempts ?? 0) + 1,
      lastError: undefined,
      lastResult: result,
    }).catch(() => {
      // IndexedDB write failure after a successful send — non-critical; the
      // server write already committed (PLAN §7).
    });
    await refreshAttentionCount();
    return true;
  } finally {
    inFlight.delete(id);
  }
}

/**
 * Sequentially auto-sends any rows in 'pending' (crashed mid-send from an old
 * session) or 'ready' (legacy confirmed-but-unsent rows) — one request per
 * clip, matching the "one request per clip" reality of the workbook-output
 * chaining. The in-flight set prevents double-sends from competing paths.
 */
export async function sweepPendingSyncs(): Promise<void> {
  const [pending, ready] = await Promise.all([
    getRecordingsByStatus('pending'),
    getRecordingsByStatus('ready'),
  ]);
  for (const rec of [...pending, ...ready]) {
    await autoSync(rec.id);
  }
  await refreshAttentionCount();
}

/** Outcome banner content from a /sync or /correct result. */
export interface SyncSummary {
  lines: string[];
  variant: 'success' | 'warning' | 'error';
}

/**
 * Renders the queue outcome into user-facing banner lines:
 * - Written → "Saved to workbook (<cells>)"
 * - SkippedPresent → "Already filled — not overwritten (<cell>)"
 * - Flagged → reasons (plot not found / subsample missing / value out of range)
 * - Replay → "This recording was already sent."
 */
export function summarizeSync(res: SyncRecordingResponse): SyncSummary {
  const lines: string[] = [];

  switch (res.status) {
    case 'Written': {
      const cells = res.writtenCells.map((c) => c.cellAddress).join(', ');
      lines.push(`Saved to workbook (${cells})`);
      break;
    }
    case 'SkippedPresent': {
      const first = res.skippedPresentCells[0]?.cellAddress ?? 'cell';
      lines.push(`Already filled — not overwritten (${first})`);
      if (res.needsReview) {
        lines.push('Recording still needs review — nothing was overwritten.');
      }
      break;
    }
    case 'Flagged': {
      const reasons: string[] = [];
      if (!res.plotExists) reasons.push('plot not found');
      if (res.missingSubsampleIndices.length > 0) reasons.push('subsample missing');
      if (res.outOfTraitRangeValueCount > 0) {
        reasons.push(
          `${res.outOfTraitRangeValueCount} value${res.outOfTraitRangeValueCount === 1 ? '' : 's'} didn't fit`,
        );
      }
      if (res.writtenCells.length > 0) {
        const cells = res.writtenCells.map((c) => c.cellAddress).join(', ');
        lines.push(
          `Saved ${res.writtenCells.length} of ${res.writtenCells.length + res.outOfTraitRangeValueCount} (${cells})`,
        );
        if (reasons.length > 0) {
          lines.push(`Partially written — ${reasons.join(' / ')}`);
        }
      } else {
        lines.push(`Not written — ${reasons.join(' / ') || 'unknown reason'}`);
      }
      break;
    }
    case 'Replay':
      lines.push('This recording was already sent.');
      break;
    default:
      lines.push('Received by the server.');
      break;
  }

  // Default-target feedback: when the write target (plot and/or subsample) came
  // from the remembered default, tell the operator the resolved target so they
  // can keep the run going. Kept after the written-cell summary above and
  // before the transcript preview below.
  if (res.usedDefaultTarget && res.plot != null && res.subsample != null) {
    lines.push(
      `Used default target — Plot ${res.plot}, Subsample ${res.subsample}`,
    );
  }

  if (res.needsReview && res.transcript.trim() !== '') {
    lines.push(`Check the transcript — Whisper heard: \u00AB${res.transcript.trim()}\u00BB`);
  }

  const variant =
    res.status === 'Written' || res.status === 'SkippedPresent'
      ? 'success'
      : res.status === 'Flagged'
        ? 'error'
        : 'warning';

  return { lines, variant };
}

/** What happened to the recording after a human correction. */
export type CorrectionOutcome =
  | { kind: 'success'; result: SyncRecordingResponse }
  | { kind: 'already-saved' }
  | { kind: 'failed'; message: string };

/**
 * Applies a confirmed correction to a needs-attention recording (Fix & send).
 * JSON body, no file — the server reuses the stored blob + transcript.
 * Row + badge updates mirror autoSync (updateRecording + refresh count):
 * - 200 → row 'synced' + `lastResult`;
 * - 409 (row already terminal Written/SkippedPresent) → row 'synced' with
 *   lastError "Already saved — nothing to correct";
 * - other → row 'failed' with the surfaced message.
 *
 * All three exits bump the sheet-refresh signal (the request was dispatched in
 * every one of them): 200 uses the result's trial id, 409 and error re-read the
 * recording's trial id.
 */
export async function applyCorrection(
  recordingId: string,
  fields: CorrectRecordingRequest,
): Promise<CorrectionOutcome> {
  try {
    const result = await api.correctRecording(recordingId, fields);

    // A human-confirmed correction that lands a Written write also records
    // the last-written target — the next values-only clip continues from the
    // row after it. Same seam as autoSync: persistence first, then the atom.
    if (result.status === 'Written') {
      advanceSheetTarget(result.trialId, {
        plot: result.plot,
        subsample: result.subsample,
        traitOrdinal: result.traitOrdinal,
      });
    }

    const rec = await getRecordingById(recordingId);
    if (rec !== null) {
      await updateRecording({
        ...rec,
        status: 'synced',
        lastError: undefined,
        lastResult: result,
      }).catch(() => {
        // IndexedDB write failure — non-critical; badge may be stale.
      });
    }
    await refreshAttentionCount();
    // 200 → the RESULT carries the trial that was actually corrected.
    bumpSheetRefresh(result.trialId);
    return { kind: 'success', result };
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) {
      const rec = await getRecordingById(recordingId);
      if (rec !== null) {
        await updateRecording({
          ...rec,
          status: 'synced',
          lastError: 'Already saved — nothing to correct',
        }).catch(() => {
          // Non-critical — refresh below still keeps the badge honest.
        });
      }
      await refreshAttentionCount();
      // 409 → re-read the recording's own trial id (no result body).
      bumpSheetRefresh(rec?.trialId ?? null);
      return { kind: 'already-saved' };
    }
    const message =
      err instanceof Error ? err.message : 'Correction failed';
    const rec = await getRecordingById(recordingId);
    if (rec !== null) {
      await updateRecording({
        ...rec,
        status: 'failed',
        lastError: message,
      }).catch(() => {
        // Non-critical.
      });
    }
    await refreshAttentionCount();
    // Error exit → same re-read: the request was dispatched before it failed.
    bumpSheetRefresh(rec?.trialId ?? null);
    return { kind: 'failed', message };
  }
}
