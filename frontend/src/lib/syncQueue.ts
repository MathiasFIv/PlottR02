/**
 * Voice-to-Excel auto-sync engine (framework-free). Owns the send pipeline for
 * captured clips and the startup sweep; `POST /api/Recordings/sync` is the only
 * send path (idempotency-keyed on clientRecordingId), corrections use
 * `POST /api/Recordings/{id}/correct`.
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
import { bumpSheetRefresh } from './sheetRefreshSignal';
import { resolveNextTarget } from './nextTarget';
import { advanceSheetTarget } from './sheetTarget';
import { anchorToWire, isAnchorForTrial } from './sheetAnchor';
import {
  recordingsNeedingAttentionCountAtom,
  sheetSelectedAtom,
} from '../store/atoms';

/** Ids with a send currently in flight — guards sweep + UI double-sends. */
const inFlight = new Set<string>();

/** 'needs-attention' + 'failed' counts; a failed IndexedDB read leaves the badge stale. */
export async function refreshAttentionCount(): Promise<void> {
  try {
    const [attention, failed] = await Promise.all([
      getCountByStatus('needs-attention'),
      getCountByStatus('failed'),
    ]);
    getDefaultStore().set(recordingsNeedingAttentionCountAtom, attention + failed);
  } catch {
    // Non-critical.
  }
}

/**
 * Sends one recording (row → 'syncing' → outcome). `needsReview` maps to
 * 'needs-attention', else 'synced'; the idempotency key makes a lost-response
 * retry safe (server answers Replay). Bumps the sheet-refresh signal whenever
 * the server was contacted. Returns false when missing, in flight, or failed.
 */
export async function autoSync(recordingId: string): Promise<boolean> {
  const id = recordingId;
  if (inFlight.has(id)) return false;
  inFlight.add(id);
  try {
    const rec = await getRecordingById(id);
    if (rec === null) return false;

    // First-send snapshot: freeze a target before the wire (anchor → derived
    // next target → null), so a retry replays the target the clip was intended
    // for. `defaultTarget` persisted with the row keeps that snapshot.
    const needsSnapshot =
      !('defaultTarget' in rec) && rec.plot == null && rec.subsample == null;
    const anchor = needsSnapshot
      ? isAnchorForTrial(
          getDefaultStore().get(sheetSelectedAtom),
          rec.trialId ?? null,
        )
      : null;
    const sendRec = !needsSnapshot
      ? rec
      : anchor !== null
        ? { ...rec, ...anchorToWire(anchor) }
        : { ...rec, defaultTarget: resolveNextTarget(rec.trialId ?? '') };

    let result: SyncRecordingResponse;
    try {
      await updateRecording({ ...sendRec, status: 'syncing' });
      try {
        result = await api.syncRecording(sendRec);
      } finally {
        // Tick in both branches: a lost-response-after-commit may still have
        // written cells, and changed:false is a check, not a claim.
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
        // Non-critical.
      });
      await refreshAttentionCount();
      return false;
    }

    if (result.status === 'Written') {
      const trialId = sendRec.trialId ?? '';
      advanceSheetTarget(trialId, {
        plot: result.plot,
        subsample: result.subsample,
        writtenCells: result.writtenCells,
      });

      // One-shot anchor consumption: clear only if it still points at the row
      // we just wrote, so a newer tap is not swallowed.
      const liveAnchor = isAnchorForTrial(
        getDefaultStore().get(sheetSelectedAtom),
        trialId,
      );
      if (
        liveAnchor !== null &&
        liveAnchor.plot === result.plot &&
        liveAnchor.subsample === result.subsample
      ) {
        getDefaultStore().set(sheetSelectedAtom, null);
      }
    }

    await updateRecording({
      ...sendRec,
      status: result.needsReview ? 'needs-attention' : 'synced',
      syncAttempts: (rec.syncAttempts ?? 0) + 1,
      lastError: undefined,
      lastResult: result,
    }).catch(() => {
      // Non-critical — the server write already committed.
    });
    await refreshAttentionCount();
    return true;
  } finally {
    inFlight.delete(id);
  }
}

/** Sequentially auto-sends 'pending' (crashed mid-send) and 'ready' (legacy) rows. */
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

/** Renders a sync result into user-facing banner lines + a variant. */
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

  if (res.usedDefaultTarget && res.plot != null && res.subsample != null) {
    lines.push(
      `Used default target — Plot ${res.plot}, Subsample ${res.subsample}`,
    );
  }

  if (res.needsReview && res.transcript.trim() !== '') {
    lines.push(`Check the transcript — Whisper heard: \u00AB${res.transcript.trim()}\u00BB`);
  }

  const transcriptionFailed =
    res.transcript.trim() === '' &&
    (res.transcriptionError ?? '').trim() !== '';
  if (transcriptionFailed) {
    lines.push(`Transcription failed: ${res.transcriptionError}`);
  }

  const variant =
    res.status === 'Written' || res.status === 'SkippedPresent'
      ? 'success'
      : res.status === 'Flagged' || transcriptionFailed
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
 * Applies a confirmed correction (JSON body, no file). 200 → 'synced'; 409
 * (already terminal) → 'synced' with "Already saved"; other → 'failed'. Every
 * exit bumps the sheet-refresh signal: 200 uses the result's trial id, 409 and
 * error re-read the recording's own trial id.
 */
export async function applyCorrection(
  recordingId: string,
  fields: CorrectRecordingRequest,
): Promise<CorrectionOutcome> {
  try {
    const result = await api.correctRecording(recordingId, fields);

    // A landed Written also records the last-written target — but, unlike
    // autoSync, never consumes the write anchor (a correction is not a tap).
    if (result.status === 'Written') {
      advanceSheetTarget(result.trialId, {
        plot: result.plot,
        subsample: result.subsample,
        writtenCells: result.writtenCells,
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
        // Non-critical.
      });
    }
    await refreshAttentionCount();
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
          // Non-critical.
        });
      }
      await refreshAttentionCount();
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
    bumpSheetRefresh(rec?.trialId ?? null);
    return { kind: 'failed', message };
  }
}
