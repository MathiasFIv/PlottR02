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
import { recordingsNeedingAttentionCountAtom } from '../store/atoms';

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
 * Sends one recording to the backend: row → 'syncing' → outcome.
 *
 * - 2xx: `needsReview` → 'needs-attention' (correction surface), else 'synced';
 *   `syncAttempts` increments and the response is persisted as `lastResult`.
 * - error (network or ApiError): 'failed' + `lastError`; retried by the same
 *   call — the idempotency key makes a lost-response replay safe (server
 *   answers Replay instead of double-writing).
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

    let result: SyncRecordingResponse;
    try {
      await updateRecording({ ...rec, status: 'syncing' });
      result = await api.syncRecording(rec);
    } catch (err) {
      const message =
        err instanceof Error ? err.message : 'Sync failed unexpectedly';
      await updateRecording({
        ...rec,
        status: 'failed',
        lastError: message,
      }).catch(() => {
        // IndexedDB write failure — non-critical; badge may be stale.
      });
      await refreshAttentionCount();
      return false;
    }

    await updateRecording({
      ...rec,
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
      break;
    }
    case 'Flagged': {
      const reasons: string[] = [];
      if (!res.plotExists) reasons.push('plot not found');
      if (res.missingSubsampleIndices.length > 0) reasons.push('subsample missing');
      if (res.writtenCells.length === 0 && res.skippedPresentCells.length === 0) reasons.push('value out of range');
      lines.push(`Not written — ${reasons.join(' / ') || 'unknown reason'}`);
      break;
    }
    case 'Replay':
      lines.push('This recording was already sent.');
      break;
    default:
      lines.push('Received by the server.');
      break;
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
 */
export async function applyCorrection(
  recordingId: string,
  fields: CorrectRecordingRequest,
): Promise<CorrectionOutcome> {
  try {
    const result = await api.correctRecording(recordingId, fields);
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
    return { kind: 'failed', message };
  }
}