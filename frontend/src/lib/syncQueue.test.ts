import 'fake-indexeddb/auto';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// Mock the API transport BEFORE anything imports api.ts: swap only the two
// recording endpoints for vi.fn() stubs, keep the real ApiError class so the
// engine's `instanceof` 409 branch and the status checks under test are real.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return {
    ...actual,
    api: {
      ...actual.api,
      syncRecording: vi.fn(),
      correctRecording: vi.fn(),
    },
  };
});

import { getDefaultStore } from 'jotai/vanilla';
import { ApiError, api } from './api';
import type { SyncRecordingResponse } from './api';
import {
  getCountByStatus,
  getRecordingById,
  getRecordingsByStatus,
  newRecording,
  openDb,
  saveRecording,
} from './audioDb';
import type { AudioRecording } from './audioDb';
import { recordingsNeedingAttentionCountAtom } from '../store/atoms';
import {
  applyCorrection,
  autoSync,
  refreshAttentionCount,
  summarizeSync,
  sweepPendingSyncs,
} from './syncQueue';

const syncMock = vi.mocked(api.syncRecording);
const correctMock = vi.mocked(api.correctRecording);

/** Deterministic fixture — explicit id, tiny fake audio blob. */
function makeRec(overrides: Partial<AudioRecording> = {}): AudioRecording {
  return {
    ...newRecording({
      blob: new Blob(['fake-audio-bytes'], { type: 'audio/webm' }),
      mimeType: 'audio/webm',
      trialId: 'trial-1',
    }),
    id: 'rec-' + Math.random().toString(36).slice(2),
    ...overrides,
  };
}

/** A full SyncVoiceRecordingResult with the transcript-authoritative defaults. */
function syncResponse(
  overrides: Partial<SyncRecordingResponse> = {},
): SyncRecordingResponse {
  return {
    clientRecordingId: 'rec-x',
    trialId: 'trial-1',
    status: 'Written',
    plot: 101,
    subsample: 3,
    value: 7,
    parsedPlot: 101,
    parsedSubsample: 3,
    parsedValue: 7,
    plotExists: true,
    traitOrdinal: 1,
    needsReview: false,
    replayed: false,
    wasTranscriptOnly: true,
    submittedAt: '2026-09-16T12:00:00Z',
    transcript: 'plot 101 subsample 3 value 7',
    confidence: 0.9,
    writtenCells: [
      { plot: 101, subsample: 3, traitOrdinal: 1, value: 7, cellAddress: 'C24' },
    ],
    skippedPresentCells: [],
    missingSubsampleIndices: [],
    outOfTraitRangeValueCount: 0,
    ...overrides,
  };
}

/** Polls IndexedDB until the row reaches the expected status (or fails). */
async function waitForStatus(
  id: string,
  status: AudioRecording['status'],
): Promise<void> {
  for (let i = 0; i < 100; i++) {
    const rec = await getRecordingById(id);
    if (rec?.status === status) return;
    await new Promise((r) => setTimeout(r, 5));
  }
  throw new Error(`Row ${id} never reached status '${status}'`);
}

/** Reads the live attention-count atom (engine writes + hook reads it). */
function attentionCount(): number {
  return getDefaultStore().get(recordingsNeedingAttentionCountAtom);
}

beforeEach(async () => {
  syncMock.mockReset();
  correctMock.mockReset();
  // openDb is cached across calls, so clear the store instead of deleting the
  // database (a delete would be blocked by the open connection).
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction('recordings', 'readwrite');
    tx.objectStore('recordings').clear();
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(new Error('Failed to clear recordings store'));
  });
  getDefaultStore().set(recordingsNeedingAttentionCountAtom, 0);
});

describe('autoSync', () => {
  it('sends a saved clip exactly once with the same blob: pending → syncing → synced', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    // Deferred transport so we can observe the intermediate 'syncing' status.
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    syncMock.mockImplementation(() => gate.then(() => syncResponse()));

    const sendPromise = autoSync(rec.id);

    // While the request is in flight the row is 'syncing'.
    await waitForStatus(rec.id, 'syncing');
    release();
    expect(await sendPromise).toBe(true);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('synced');
    expect(found?.syncAttempts).toBe(1);
    expect(found?.lastError).toBeUndefined();
    expect(found?.lastResult?.status).toBe('Written');

    // Exactly one send — and the same clip that was saved went over the wire
    // (IndexedDB structured-clones the blob, so compare content, not identity).
    expect(syncMock).toHaveBeenCalledTimes(1);
    const sent = syncMock.mock.calls[0]?.[0]!;
    expect(sent.blob.type).toBe('audio/webm');
    expect(await sent.blob.text()).toBe('fake-audio-bytes');

    // Index transitions: gone from pending, present in synced.
    expect(await getRecordingsByStatus('pending')).toHaveLength(0);
    expect(await getRecordingsByStatus('synced')).toHaveLength(1);
  });

  it('does not double-send while a sync for the same row is in flight', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    let release!: () => void;
    let released = false;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    syncMock.mockImplementation(() => gate.then(() => syncResponse()));

    const first = autoSync(rec.id);
    // Inflight guard: the second call returns false without a second request.
    // (release() runs even on failure so the first send is never orphaned.)
    try {
      expect(await autoSync(rec.id)).toBe(false);
    } finally {
      if (!released) {
        release();
        released = true;
      }
    }
    // The first send completed its gate and reached the transport once.
    expect(await first).toBe(true);
    expect(syncMock).toHaveBeenCalledTimes(1);
    expect((await getRecordingById(rec.id))?.status).toBe('synced');
  });

  it('flips a needsReview /sync response to needs-attention and counts it', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock.mockResolvedValue(
      syncResponse({
        status: 'Flagged',
        needsReview: true,
        transcript: '',
        writtenCells: [],
      }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('needs-attention');
    expect(found?.lastResult?.needsReview).toBe(true);

    expect(await getCountByStatus('needs-attention')).toBe(1);
    expect(attentionCount()).toBe(1);
  });

  it('marks a sync error as failed with lastError and counts it', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock.mockRejectedValue(
      new ApiError(500, 'API request failed (500): /api/Recordings/sync'),
    );

    expect(await autoSync(rec.id)).toBe(false);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('failed');
    expect(found?.lastError).toContain('500');
    expect(await getCountByStatus('failed')).toBe(1);
    expect(attentionCount()).toBe(1);
  });

  it('retries a failed row back to synced', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock
      .mockRejectedValueOnce(new ApiError(500, 'boom'))
      .mockResolvedValueOnce(syncResponse());

    expect(await autoSync(rec.id)).toBe(false);
    expect((await getRecordingById(rec.id))?.status).toBe('failed');

    expect(await autoSync(rec.id)).toBe(true);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('synced');
    expect(found?.syncAttempts).toBe(1);
    expect(found?.lastResult?.status).toBe('Written');
    expect(syncMock).toHaveBeenCalledTimes(2);
    expect(await getCountByStatus('failed')).toBe(0);
    expect(attentionCount()).toBe(0);
  });

  it('no-ops without a network call for an unknown recordingId', async () => {
    expect(await autoSync('missing-1')).toBe(false);
    expect(syncMock).not.toHaveBeenCalled();
  });
});

describe('applyCorrection', () => {
  it('calls correctRecording with JSON fields and flips the row to synced', async () => {
    const rec = makeRec({
      status: 'needs-attention',
      lastResult: syncResponse({
        status: 'Flagged',
        needsReview: true,
        transcript: '',
      }),
    });
    await saveRecording(rec);
    const corrected = syncResponse({
      status: 'Written',
      wasTranscriptOnly: false,
    });
    correctMock.mockResolvedValue(corrected);

    const outcome = await applyCorrection(rec.id, {
      plot: 12,
      subsample: 1,
      value: 8,
    });

    expect(outcome.kind).toBe('success');
    expect(correctMock).toHaveBeenCalledTimes(1);
    expect(correctMock.mock.calls[0]?.[0]).toBe(rec.id);
    expect(correctMock.mock.calls[0]?.[1]).toEqual({
      plot: 12,
      subsample: 1,
      value: 8,
    });

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('synced');
    expect(found?.lastResult).toEqual(corrected);
    expect(found?.lastError).toBeUndefined();
    expect(attentionCount()).toBe(0);
  });

  it('treats a 409 correction as already saved and flips the row to synced', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    correctMock.mockRejectedValue(new ApiError(409, 'Row already in terminal state'));

    const outcome = await applyCorrection(rec.id, {
      plot: 101,
      subsample: 3,
      value: 7,
    });

    expect(outcome.kind).toBe('already-saved');
    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('synced');
    expect(found?.lastError).toBe('Already saved — nothing to correct');
    expect(attentionCount()).toBe(0);
  });

  it('marks the row failed with the message on a non-409 correction error', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    correctMock.mockRejectedValue(
      new ApiError(404, 'API request failed (404): /api/Recordings/x/correct'),
    );

    const outcome = await applyCorrection(rec.id, {
      plot: 12,
      subsample: 1,
      value: 8,
    });

    expect(outcome.kind).toBe('failed');
    if (outcome.kind === 'failed') {
      expect(outcome.message).toContain('404');
    }
    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('failed');
    expect(found?.lastError).toContain('404');
    expect(attentionCount()).toBe(1);
  });
});

describe('sweepPendingSyncs', () => {
  it('sends a pending row and a legacy ready row, the ready row carrying its confirmed fields', async () => {
    const pending = makeRec();
    const ready = makeRec({ status: 'ready', plot: 12, subsample: 1, value: 9 });
    await saveRecording(pending);
    await saveRecording(ready);
    syncMock.mockResolvedValue(syncResponse({ wasTranscriptOnly: false }));

    await sweepPendingSyncs();

    // Both rows went out, sequentially.
    expect(syncMock).toHaveBeenCalledTimes(2);
    const sentPlots = syncMock.mock.calls.map(([rec]) => rec.plot);
    expect(sentPlots).toContain(12);
    const pendingArg = syncMock.mock.calls.find(([rec]) => rec.id === pending.id)?.[0];
    expect(pendingArg?.plot).toBeNull(); // transcript-authoritative

    expect((await getRecordingById(pending.id))?.status).toBe('synced');
    expect((await getRecordingById(ready.id))?.status).toBe('synced');
    expect(attentionCount()).toBe(0);
  });

  it('is a no-op when no rows need sending', async () => {
    await saveRecording(makeRec({ status: 'synced' }));
    await sweepPendingSyncs();
    expect(syncMock).not.toHaveBeenCalled();
  });
});

describe('refreshAttentionCount', () => {
  it('counts needs-attention + failed rows into the atom', async () => {
    await saveRecording(makeRec({ status: 'needs-attention' }));
    await saveRecording(makeRec({ status: 'needs-attention' }));
    await saveRecording(makeRec({ status: 'failed' }));
    await saveRecording(makeRec({ status: 'synced' }));

    await refreshAttentionCount();

    expect(attentionCount()).toBe(3);
  });
});

describe('summarizeSync', () => {
  it('summarizes Written with the written cells', () => {
    const { lines, variant } = summarizeSync(syncResponse());
    expect(lines.join(' ')).toContain('Saved to workbook (C24)');
    expect(variant).toBe('success');
  });

  it('says "Already filled — not overwritten" for SkippedPresent', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'SkippedPresent',
        writtenCells: [],
        skippedPresentCells: [
          { plot: 101, subsample: 3, traitOrdinal: 1, value: 7, cellAddress: 'C24' },
        ],
      }),
    );
    expect(lines.join(' ')).toContain('Already filled — not overwritten (C24)');
    expect(variant).toBe('success');
  });

  it('adds a still-needs-review line when SkippedPresent has needsReview', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'SkippedPresent',
        needsReview: true,
        transcript: '',
        writtenCells: [],
        skippedPresentCells: [
          { plot: 101, subsample: 3, traitOrdinal: 1, value: 7, cellAddress: 'C22' },
        ],
      }),
    );
    const text = lines.join(' ');
    expect(text).toContain('Already filled — not overwritten (C22)');
    expect(text).toContain('Recording still needs review — nothing was overwritten.');
    expect(variant).toBe('success');
  });

  it('reports a partial write when Flagged with some cells written and some dropped', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'Flagged',
        needsReview: true,
        transcript: '',
        writtenCells: [
          { plot: 22, subsample: 1, traitOrdinal: 1, value: 7, cellAddress: 'F22' },
          { plot: 22, subsample: 2, traitOrdinal: 1, value: 8, cellAddress: 'G22' },
          { plot: 22, subsample: 3, traitOrdinal: 1, value: 9, cellAddress: 'H22' },
        ],
        skippedPresentCells: [],
        missingSubsampleIndices: [],
        outOfTraitRangeValueCount: 1,
      }),
    );
    const text = lines.join(' ');
    expect(text).toContain('Saved 3 of 4 (F22, G22, H22)');
    expect(text).toContain('1 value didn\'t fit');
    expect(variant).toBe('error');
  });

  it('keeps "Not written" with reason text when Flagged writes nothing and drops values', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'Flagged',
        needsReview: true,
        transcript: '',
        writtenCells: [],
        skippedPresentCells: [],
        missingSubsampleIndices: [],
        outOfTraitRangeValueCount: 2,
      }),
    );
    const text = lines.join(' ');
    expect(text).toContain('Not written');
    expect(text).toContain('2 values didn\'t fit');
    expect(variant).toBe('error');
  });

  it('notes the replay for a Replay response', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({ status: 'Replay', replayed: true, wasTranscriptOnly: false }),
    );
    expect(lines.join(' ')).toContain('already sent');
    expect(variant).toBe('warning');
  });
});