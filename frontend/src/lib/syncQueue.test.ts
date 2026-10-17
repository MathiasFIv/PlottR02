import 'fake-indexeddb/auto';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Shared in-memory storage for the default-target store, exposed through
 * vi.hoisted so both the module mocks below and the test bodies can see it.
 */
const { defaultTargetStorage, defaultTargetStorageStub, realSyncRecordingHolder } =
  vi.hoisted(() => {
    const map = new Map<string, string>();
    return {
      defaultTargetStorage: map,
      defaultTargetStorageStub: {
        getItem: (key: string) => map.get(key) ?? null,
        setItem: (key: string, value: string) => {
          map.set(key, value);
        },
      },
      realSyncRecordingHolder: { current: undefined as unknown },
    };
  });

/**
 * Route the default-target store through the fake storage: both the engine
 * (`rememberWrittenTarget`) and the real api.syncRecording /
 * `resolveNextTarget` (`readDefaultTarget`, bare call → this default) share
 * one injected memory store, keyed per trial. Every function is trial-scoped
 * now (`plottR-default-target:<trialId>`), so the wrapper passes the trialId
 * straight through.
 */
vi.mock('./defaultTarget', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./defaultTarget')>();
  return {
    ...actual,
    readDefaultTarget: (
      trialId: string,
      storage?: Pick<Storage, 'getItem' | 'setItem'>,
    ) => actual.readDefaultTarget(trialId, storage ?? defaultTargetStorageStub),
    writeDefaultTarget: (
      trialId: string,
      target: { plot: number; subsample: number; blockFull: boolean },
      storage?: Pick<Storage, 'getItem' | 'setItem'>,
    ) =>
      actual.writeDefaultTarget(
        trialId,
        target,
        storage ?? defaultTargetStorageStub,
      ),
    rememberWrittenTarget: (
      trialId: string,
      result: {
        plot?: number | null;
        subsample?: number | null;
        blockFull?: boolean;
      },
      storage?: Pick<Storage, 'getItem' | 'setItem'>,
    ) =>
      actual.rememberWrittenTarget(
        trialId,
        result,
        storage ?? defaultTargetStorageStub,
      ),
  };
});

// Mock the API transport BEFORE anything imports api.ts: swap only the two
// recording endpoints for vi.fn() stubs, keep the real ApiError class so the
// engine's `instanceof` 409 branch and the status checks under test are real.
// The real syncRecording is captured so one test can exercise the actual
// FormData construction (defaultPlot/defaultSubsample) end-to-end.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  realSyncRecordingHolder.current = actual.api.syncRecording;
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
import { readDefaultTarget, writeDefaultTarget } from './defaultTarget';
import { resolveNextTarget } from './nextTarget';
import {
  recordingsNeedingAttentionCountAtom,
  sheetRefreshTickAtom,
  sheetLastWrittenAtom,
  sheetSelectedAtom,
  trialSheetAtom,
} from '../store/atoms';
import type { SheetLastWritten, SheetRefreshTick, SheetSelected } from '../store/atoms';
import {
  applyCorrection,
  autoSync,
  refreshAttentionCount,
  summarizeSync,
  sweepPendingSyncs,
} from './syncQueue';
import type { SheetColumn, SheetRow, TrialSheet } from '../types';

const syncMock = vi.mocked(api.syncRecording);
const correctMock = vi.mocked(api.correctRecording);

/**
 * Workbook-order rows for trial-1: 4 subsamples per plot (the real file's
 * shape, 101 1 … 101 4, 102 1 … 102 4). The next target is derived from
 * THESE rows (`resolveNextTarget`), so the suite pins the file-derived
 * behaviour: a full block advances to the next subsample of the SAME plot and
 * STOPS at the plot edge (never a cross-plot wrap); the store itself only ever
 * holds the last-written pair + `blockFull`.
 */
const sheetRows: SheetRow[] = [101, 102].flatMap((plot) =>
  [1, 2, 3, 4].map((subsample, i) => ({
    row: 22 + (plot - 101) * 4 + i,
    plot,
    subsample,
    values: [null],
  })),
);

/**
 * The 9-column, part-major `columns` the API sends (cols C..K, ordinals 1..9),
 * grouped into three 3-wide category blocks: LEAF1 [1..3], LEAF2 [4..6],
 * LEAF3 [7..9]. `advanceSheetTarget` judges `blockFull` against THESE runs, so
 * a one-cell write leaves the block unfilled (STAY) and a full three-cell
 * write advances to the next subsample.
 */
const sheetColumns: SheetColumn[] = [
  { traitOrdinal: 1, column: 'C', pestCode: 'SEPTTR', partRated: 'LEAF1', label: 'SEPTTR × LEAF1' },
  { traitOrdinal: 2, column: 'D', pestCode: 'ERYSGR', partRated: 'LEAF1', label: 'ERYSGR × LEAF1' },
  { traitOrdinal: 3, column: 'E', pestCode: 'PUCCST', partRated: 'LEAF1', label: 'PUCCST × LEAF1' },
  { traitOrdinal: 4, column: 'F', pestCode: 'SEPTTR', partRated: 'LEAF2', label: 'SEPTTR × LEAF2' },
  { traitOrdinal: 5, column: 'G', pestCode: 'ERYSGR', partRated: 'LEAF2', label: 'ERYSGR × LEAF2' },
  { traitOrdinal: 6, column: 'H', pestCode: 'PUCCST', partRated: 'LEAF2', label: 'PUCCST × LEAF2' },
  { traitOrdinal: 7, column: 'I', pestCode: 'SEPTTR', partRated: 'LEAF3', label: 'SEPTTR × LEAF3' },
  { traitOrdinal: 8, column: 'J', pestCode: 'ERYSGR', partRated: 'LEAF3', label: 'ERYSGR × LEAF3' },
  { traitOrdinal: 9, column: 'K', pestCode: 'PUCCST', partRated: 'LEAF3', label: 'PUCCST × LEAF3' },
];

const trialSheet: TrialSheet = {
  trialId: 'trial-1',
  version: 'v1',
  changed: true,
  fileName: '4 subsamples.xlsx',
  generatedAt: '2026-09-16T12:00:00Z',
  metadata: null,
  columns: sheetColumns,
  rows: sheetRows,
};

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
    usedDefaultTarget: false,
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

/** Reads the live sheet-refresh tick atom (engine bumps it on a server contact). */
function sheetTick(): SheetRefreshTick | null {
  return getDefaultStore().get(sheetRefreshTickAtom);
}

/** Reads the live last-written target atom (N3 — engine writes it). */
function currentCell(): SheetLastWritten | null {
  return getDefaultStore().get(sheetLastWrittenAtom);
}

beforeEach(async () => {
  syncMock.mockReset();
  correctMock.mockReset();
  defaultTargetStorage.clear();
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
  getDefaultStore().set(sheetRefreshTickAtom, null);
  getDefaultStore().set(sheetLastWrittenAtom, null);
  // The write anchor is UI state (a tap) — every test starts with none armed,
  // so a leak from the previous case can never seed a values-only send.
  getDefaultStore().set(sheetSelectedAtom, null);
  // The next-target resolution is payload-first: seed trial-1's rows so
  // `resolveNextTarget` derives from a real workbook order (the hosts read
  // the same atom; `clearSheetState` never touches it).
  getDefaultStore().set(trialSheetAtom, trialSheet);
});

// Tests that drive the real api.syncRecording stub fetch + localStorage; undo
// those global stubs unconditionally so they can't leak into the next test.
afterEach(() => {
  vi.unstubAllGlobals();
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

describe('sheet refresh tick (recording-driven live refresh)', () => {
  it('bumps after a 2xx sync that wrote cells', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock.mockResolvedValue(syncResponse()); // Written, one cell

    expect(await autoSync(rec.id)).toBe(true);

    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('bumps after a 2xx sync that wrote ZERO cells — the tick is a check, not a claim', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock.mockResolvedValue(
      syncResponse({
        status: 'SkippedPresent',
        writtenCells: [],
        skippedPresentCells: [
          { plot: 101, subsample: 3, traitOrdinal: 1, value: 7, cellAddress: 'C24' },
        ],
      }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('bumps after a FAILED sync — a lost-response-after-commit still contacted the server', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    syncMock.mockRejectedValue(
      new ApiError(500, 'API request failed (500): /api/Recordings/sync'),
    );

    expect(await autoSync(rec.id)).toBe(false);

    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('does NOT bump for an unknown recording id — the server is never contacted', async () => {
    expect(await autoSync('missing-1')).toBe(false);

    expect(syncMock).not.toHaveBeenCalled();
    expect(sheetTick()).toBeNull();
  });

  it('does NOT bump when the in-flight guard short-circuits the second call', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    syncMock.mockImplementation(() => gate.then(() => syncResponse()));

    const first = autoSync(rec.id);
    // The guarded call returns false without a transport call of its own, and
    // the first send has no outcome yet (its gate is still closed) → no tick.
    expect(await autoSync(rec.id)).toBe(false);
    expect(sheetTick()).toBeNull();

    release();
    expect(await first).toBe(true);
    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('does NOT bump when the send record has no trial id — there is no sheet to refresh', async () => {
    const rec = makeRec({ trialId: null });
    await saveRecording(rec);
    syncMock.mockResolvedValue(syncResponse());

    // The server WAS contacted, but the bump reads the send record's trial id,
    // and null/empty never signals a host.
    expect(await autoSync(rec.id)).toBe(true);
    expect(syncMock).toHaveBeenCalledTimes(1);
    expect(sheetTick()).toBeNull();
  });

  it('bumps after correction 200 with the RESULT trial id', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    // Deliberately not the row's own 'trial-1', so the assertion proves the
    // 200 exit reads the trial id off the result body.
    correctMock.mockResolvedValue(
      syncResponse({
        status: 'Written',
        wasTranscriptOnly: false,
        trialId: 'trial-9',
      }),
    );

    const outcome = await applyCorrection(rec.id, {
      plot: 12,
      subsample: 1,
      value: 8,
    });

    expect(outcome.kind).toBe('success');
    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-9' });
  });

  it('bumps after correction 409 with the re-read recording trial id', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    correctMock.mockRejectedValue(new ApiError(409, 'Row already in terminal state'));

    const outcome = await applyCorrection(rec.id, {
      plot: 101,
      subsample: 3,
      value: 7,
    });

    expect(outcome.kind).toBe('already-saved');
    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });

  it('bumps after a FAILED correction — the request was dispatched', async () => {
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
    expect(sheetTick()).toEqual({ seq: 1, trialId: 'trial-1' });
  });
});

describe('current cell target (N3 highlight atom)', () => {
  it('records the written pair + blockFull after a Written sync — an unfilled block STAYS', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    // Written 101/3, ONE cell of the 3-wide LEAF1 block → blockFull false.
    syncMock.mockResolvedValue(syncResponse());

    expect(await autoSync(rec.id)).toBe(true);

    // Persistence holds the written pair verbatim + the flag: NO subsample+1.
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
    // … and the atom mirrors it, tagged with the trial + the flag.
    expect(currentCell()).toEqual({
      trialId: 'trial-1',
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
    // The NEXT target derives from the payload's rows[] at read time: the
    // block is unfilled, so the run STAYS on 101 3 (writes its next empty
    // cell) — it does NOT advance to 101 4.
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 3 });
  });

  it('advances to the next subsample once the category block is FULL', async () => {
    const rec = makeRec();
    await saveRecording(rec);
    // All three LEAF1 cells of row 101/3 written → blockFull true.
    syncMock.mockResolvedValue(
      syncResponse({
        writtenCells: [
          { plot: 101, subsample: 3, traitOrdinal: 1, value: 5, cellAddress: 'C24' },
          { plot: 101, subsample: 3, traitOrdinal: 2, value: 6, cellAddress: 'D24' },
          { plot: 101, subsample: 3, traitOrdinal: 3, value: 7, cellAddress: 'E24' },
        ],
      }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
    expect(currentCell()).toEqual({
      trialId: 'trial-1',
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
    // Full block → advance to the next subsample of the SAME plot.
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 4 });
  });

  it('does NOT touch the target atom on a non-Written result', async () => {
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

    expect(readDefaultTarget('trial-1')).toBeNull();
    expect(currentCell()).toBeNull();
  });

  it('advances the target atom after a Written correction under the RESULT trial id', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    // Deliberately not the row's own 'trial-1': the result body decides.
    correctMock.mockResolvedValue(
      syncResponse({ status: 'Written', wasTranscriptOnly: false, trialId: 'trial-9' }),
    );

    const outcome = await applyCorrection(rec.id, {
      plot: 12,
      subsample: 1,
      value: 8,
    });
    expect(outcome.kind).toBe('success');

    // trial-9 has NO columns in the payload (the atom holds trial-1), so
    // `columnsForTrial` returns null → the block is treated as unfilled.
    expect(readDefaultTarget('trial-9')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
    expect(readDefaultTarget('trial-1')).toBeNull();
    expect(currentCell()).toEqual({
      trialId: 'trial-9',
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
    // Foreign trial: trial-1's payload never seeds trial-9's next target.
    expect(resolveNextTarget('trial-9')).toBeNull();
  });
});

describe('write anchor (tap-to-anchor on a grid row)', () => {
  /** A tap on a cell: plot 102, subsample 2, column index 2 (ordinal 3). */
  function tappedCell(overrides: Partial<SheetSelected> = {}): SheetSelected {
    return {
      trialId: 'trial-1',
      plot: 102,
      subsample: 2,
      valuesIndex: 2,
      ...overrides,
    };
  }

  /** The record handed to api.syncRecording for send #`index` (throws if none). */
  function sentRecord(index = 0): AudioRecording {
    const call = syncMock.mock.calls[index];
    if (call === undefined) throw new Error(`no send captured at index ${index}`);
    return call[0];
  }

  /**
   * REAL `api.syncRecording` (FormData built for real) + a fake fetch, so a
   * test can assert the ACTUAL multipart field list — the record-level
   * assertions above only see the object handed to the mocked API.
   */
  function stubAnchorWire(): ReturnType<typeof vi.fn> {
    syncMock.mockImplementation(realSyncRecordingHolder.current as typeof syncMock);
    const fetchStub = vi.fn(
      async (_url: string, init?: RequestInit) => {
        const defaults =
          init?.body instanceof FormData &&
          Number(init.body.get('defaultPlot')) > 0
            ? {
                plot: Number(init.body.get('defaultPlot')),
                subsample: Number(init.body.get('defaultSubsample')),
              }
            : null;
        return new Response(
          JSON.stringify(
            syncResponse(
              defaults === null
                ? { writtenCells: [] }
                : { ...defaults, usedDefaultTarget: true, writtenCells: [] },
            ),
          ),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      },
    );
    vi.stubGlobal('fetch', fetchStub);
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: () => {},
      removeItem: () => {},
      clear: () => {},
      key: () => null,
      length: 0,
    } satisfies Storage);
    return fetchStub;
  }

  it('the tapped COLUMN never reaches the multipart wire (real FormData, end-to-end)', async () => {
    const fetchStub = stubAnchorWire();
    // Values-only clip with NO traitOrdinal of its own — so anything
    // column-shaped on the wire could only have leaked from the tap.
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    // The tap carries valuesIndex 2 (see tappedCell) …
    getDefaultStore().set(sheetSelectedAtom, tappedCell());

    expect(await autoSync(rec.id)).toBe(true);

    const body = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    // … the ROW rides out as the defaults …
    expect(body.get('defaultPlot')).toBe('102');
    expect(body.get('defaultSubsample')).toBe('2');
    // … and NO column coord of any name does: exact multipart field set —
    // a leaked traitOrdinal/valuesIndex/column would fail this.
    expect([...new Set(body.keys())].sort()).toEqual(
      [
        'clientRecordingId',
        'createdAt',
        'defaultPlot',
        'defaultSubsample',
        'file',
        'plot',
        'subsample',
        'trialId',
        'value',
      ].sort(),
    );
    expect(body.get('traitOrdinal')).toBeNull();
    expect(body.get('valuesIndex')).toBeNull();
    expect(body.get('column')).toBeNull();
  });

  it('wins over resolveNextTarget, sends the tapped ROW, and keeps the recorder\'s own traitOrdinal', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5, traitOrdinal: 2 });
    await saveRecording(rec);
    // Live memory says the file-derived next target is 101/3 …
    writeDefaultTarget('trial-1', { plot: 101, subsample: 2, blockFull: true });
    // … but the operator tapped the 102/2 row instead.
    getDefaultStore().set(sheetSelectedAtom, tappedCell());
    // SkippedPresent: nothing is WRITTEN, so the last-written memory must not
    // move and the assertions below read the state the tap was frozen against.
    syncMock.mockResolvedValue(
      syncResponse({ status: 'SkippedPresent', writtenCells: [] }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    // The anchor's pair went out — NOT the fallback 101/3. The tap is ROW-only:
    // it never overrides the recorder's own trait selector (stays 2).
    const sent = sentRecord();
    expect(sent.defaultTarget).toEqual({ plot: 102, subsample: 2 });
    expect(sent.traitOrdinal).toBe(2);
    // A tap is an INTENT: it never advances `sheetLastWrittenAtom` /
    // the persisted pair — auto-continue still derives 101/3.
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 2,
      blockFull: true,
    });
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 3 });
    expect(currentCell()).toBeNull();
    // The freeze is persisted with the row, ready for the retry path.
    expect((await getRecordingById(rec.id))?.defaultTarget).toEqual({
      plot: 102,
      subsample: 2,
    });
  });

  it("ignores another trial's tap and falls back to resolveNextTarget", async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5, traitOrdinal: 2 });
    await saveRecording(rec);
    writeDefaultTarget('trial-1', { plot: 101, subsample: 2, blockFull: true });
    // The SAME coordinates, but tapped on a different trial's grid.
    getDefaultStore().set(
      sheetSelectedAtom,
      tappedCell({ trialId: 'trial-2', plot: 105, subsample: 1 }),
    );
    syncMock.mockResolvedValue(
      syncResponse({ status: 'SkippedPresent', writtenCells: [] }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    const sent = sentRecord();
    expect(sent.defaultTarget).toEqual({ plot: 101, subsample: 3 });
    // The foreign tap did NOT override the recorder's trait selector either.
    expect(sent.traitOrdinal).toBe(2);
  });

  it('retry replays the frozen anchor after the live anchor changes', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    getDefaultStore().set(sheetSelectedAtom, tappedCell());
    syncMock.mockRejectedValueOnce(new ApiError(500, 'boom'));

    expect(await autoSync(rec.id)).toBe(false);
    // The first send froze the tap onto the row before the wire failed.
    expect((await getRecordingById(rec.id))?.defaultTarget).toEqual({
      plot: 102,
      subsample: 2,
    });

    // Meanwhile the operator re-taps elsewhere (or clears the anchor).
    getDefaultStore().set(
      sheetSelectedAtom,
      tappedCell({ plot: 105, subsample: 1 }),
    );
    syncMock.mockResolvedValueOnce(
      syncResponse({ status: 'SkippedPresent', writtenCells: [] }),
    );

    expect(await autoSync(rec.id)).toBe(true);
    const retry = sentRecord(1);
    expect(retry.defaultTarget).toEqual({ plot: 102, subsample: 2 });
    // The rec carries no traitOrdinal of its own, and the row-only tap adds
    // none — the frozen record ships `undefined`.
    expect(retry.traitOrdinal).toBeUndefined();
  });

  it('a frozen null stays off the wire even when an anchor is live', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5, defaultTarget: null });
    await saveRecording(rec);
    // Live memory AND a live anchor both exist — the row's freeze predates them.
    writeDefaultTarget('trial-1', { plot: 101, subsample: 3, blockFull: true });
    getDefaultStore().set(sheetSelectedAtom, tappedCell());
    syncMock.mockResolvedValue(syncResponse());

    expect(await autoSync(rec.id)).toBe(true);

    const sent = sentRecord();
    // Key PRESENT with null → lib/api.ts's `'defaultTarget' in rec` gate
    // ships no defaultPlot/defaultSubsample, and the anchor never revives it.
    expect('defaultTarget' in sent).toBe(true);
    expect(sent.defaultTarget ?? null).toBeNull();
    expect(sent.traitOrdinal).toBeUndefined();
  });

  it('an explicit spoken plot/subsample suppresses the anchor entirely', async () => {
    const rec = makeRec({ plot: 12, subsample: 1, value: 5, traitOrdinal: 3 });
    await saveRecording(rec);
    getDefaultStore().set(sheetSelectedAtom, tappedCell());
    syncMock.mockResolvedValue(syncResponse({ wasTranscriptOnly: false }));

    expect(await autoSync(rec.id)).toBe(true);

    const sent = sentRecord();
    // No snapshot branch at all: no defaultTarget key, the spoken pair stays,
    // and the recorder's own trait selector is untouched by the tap.
    expect('defaultTarget' in sent).toBe(false);
    expect(sent.plot).toBe(12);
    expect(sent.subsample).toBe(1);
    expect(sent.traitOrdinal).toBe(3);
  });

  it('clears the live anchor after the Written write it drove (one-shot)', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    getDefaultStore().set(sheetSelectedAtom, tappedCell());
    // The write lands on the tapped row (102/2) → the anchor is consumed.
    syncMock.mockResolvedValue(
      syncResponse({
        status: 'Written',
        plot: 102,
        subsample: 2,
        writtenCells: [
          { plot: 102, subsample: 2, traitOrdinal: 1, value: 5, cellAddress: 'C26' },
        ],
      }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    // The tap armed exactly one run: the live anchor is released.
    expect(getDefaultStore().get(sheetSelectedAtom)).toBeNull();
    // The write itself still moved the last-written memory (pair as written).
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 102,
      subsample: 2,
      blockFull: false,
    });
  });

  it('keeps a NEWER live anchor when the Written pair no longer matches', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    getDefaultStore().set(sheetSelectedAtom, tappedCell());

    // Hold the first send in flight so the operator can re-tap mid-send.
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    syncMock.mockImplementation(() =>
      gate.then(() =>
        syncResponse({
          status: 'Written',
          plot: 102,
          subsample: 2,
          writtenCells: [
            { plot: 102, subsample: 2, traitOrdinal: 1, value: 5, cellAddress: 'C26' },
          ],
        }),
      ),
    );

    const send = autoSync(rec.id);
    await waitForStatus(rec.id, 'syncing');
    // Meanwhile the operator re-taps elsewhere: a NEWER intent replaces the
    // one the in-flight send already froze.
    const newer = tappedCell({ plot: 105, subsample: 1 });
    getDefaultStore().set(sheetSelectedAtom, newer);
    release();
    expect(await send).toBe(true);

    // The write the FIRST tap drove still moved the memory …
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 102,
      subsample: 2,
      blockFull: false,
    });
    // … but the newer tap is NOT swallowed: it stays armed for its own run.
    expect(getDefaultStore().get(sheetSelectedAtom)).toEqual(newer);
  });

  it('keeps the live anchor when the send FAILS', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    const tap = tappedCell();
    getDefaultStore().set(sheetSelectedAtom, tap);
    syncMock.mockRejectedValue(new ApiError(500, 'boom'));

    expect(await autoSync(rec.id)).toBe(false);

    // A failed send never reaches the Written branch → the tap is not
    // consumed (the operator can retry the same row or re-tap).
    expect(getDefaultStore().get(sheetSelectedAtom)).toEqual(tap);
  });

  it('keeps the live anchor on a non-Written result', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(rec);
    const tap = tappedCell();
    getDefaultStore().set(sheetSelectedAtom, tap);
    // SkippedPresent: nothing was WRITTEN → the tap still arms nothing else.
    syncMock.mockResolvedValue(
      syncResponse({ status: 'SkippedPresent', writtenCells: [] }),
    );

    expect(await autoSync(rec.id)).toBe(true);

    expect(getDefaultStore().get(sheetSelectedAtom)).toEqual(tap);
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
    // (d) Each replayed send went over the wire, so each ticked the sheet
    // signal — the seq grows, one refresh check per server contact.
    expect(sheetTick()).toEqual({ seq: 2, trialId: 'trial-1' });
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

  it('adds the used-default-target line when the server applied the remembered target', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'Written',
        usedDefaultTarget: true,
        needsReview: true,
        transcript: 'values 4 2 6',
      }),
    );
    const text = lines.join(' ');
    expect(text).toContain('Used default target — Plot 101, Subsample 3');
    // Kept after the written-cell summary and before the transcript preview.
    expect(text.indexOf('Used default target')).toBeLessThan(
      text.indexOf('Whisper heard'),
    );
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

  it('surfaces the transcription failure reason and an error variant when the transcript is empty', () => {
    const { lines, variant } = summarizeSync(
      syncResponse({
        status: 'Received',
        transcript: '',
        transcriptionError: 'Whisper model failed to load',
      }),
    );
    const text = lines.join(' ');
    expect(text).toContain('Transcription failed: Whisper model failed to load');
    expect(variant).toBe('error');
  });
});

describe('default target memory', () => {
  /** Minimal localStorage stand-in so the real api transport (tokens) works. */
  const localStorageStub: Storage = {
    getItem: () => null,
    setItem: () => {},
    removeItem: () => {},
    clear: () => {},
    key: () => null,
    length: 0,
  };

  /** The default pair a clip actually shipped (meaningful for values-only clips). */
  function sentDefaults(
    init?: RequestInit,
  ): { plot: number; subsample: number } | null {
    if (!init?.body || !(init.body instanceof FormData)) return null;
    const plot = Number(init.body.get('defaultPlot'));
    const subsample = Number(init.body.get('defaultSubsample'));
    if (plot < 1 || subsample < 1) return null; // absent or non-numeric
    return { plot, subsample };
  }

  /** The 3 written cells that FILL one 3-wide category block (LEAF1). */
  function fullBlock(plot: number, subsample: number) {
    return [
      { plot, subsample, traitOrdinal: 1, value: 4, cellAddress: 'C24' },
      { plot, subsample, traitOrdinal: 2, value: 5, cellAddress: 'D24' },
      { plot, subsample, traitOrdinal: 3, value: 6, cellAddress: 'E24' },
    ];
  }

  /**
   * Real syncRecording + fake transport: the fetch stub ECHOES the default
   * pair the client shipped back as a Written resolve with usedDefaultTarget
   * true — mirroring the real server's /sync substitution. The default
   * response writes the FULL 3-wide block (so the run advances); pass
   * `respond` to shape a different result (e.g. a one-cell write → STAY).
   * Returns the stub.
   */
  function stubRealTransport(
    respond: (
      defaults: { plot: number; subsample: number } | null,
    ) => SyncRecordingResponse = (defaults) =>
      syncResponse(
        defaults === null
          ? { writtenCells: fullBlock(101, 3) }
          : {
              ...defaults,
              usedDefaultTarget: true,
              writtenCells: fullBlock(defaults.plot, defaults.subsample),
            },
      ),
  ): ReturnType<typeof vi.fn> {
    syncMock.mockImplementation(
      realSyncRecordingHolder.current as typeof syncMock,
    );
    const fetchStub = vi.fn(async (_url: string, init?: RequestInit) => {
      const result = respond(sentDefaults(init));
      return new Response(JSON.stringify(result), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    });
    vi.stubGlobal('fetch', fetchStub);
    vi.stubGlobal('localStorage', localStorageStub);
    return fetchStub;
  }

  it('advances the remembered target after a FULL-block Written sync, sending it as defaults on the next clip', async () => {
    const fetchStub = stubRealTransport();

    // First clip is values-only (no plot/subsample) with nothing remembered
    // yet → it ships NO defaults and the stub resolves Written 101/3 filling
    // the whole LEAF1 block → the store (keyed by this trial) records 101/3
    // AS WRITTEN with blockFull true (no + 1).
    const first = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(first);
    expect(await autoSync(first.id)).toBe(true);
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });

    // Second values-only clip: the file-derived NEXT target (the block is now
    // full → advance to 101/4 from the payload's rows[]) goes on the wire;
    // the stub echoes the shipped pair back as Written 101/4 with
    // usedDefaultTarget true — exactly what the server resolves to.
    const second = makeRec({ plot: null, subsample: null, value: 6 });
    await saveRecording(second);
    expect(await autoSync(second.id)).toBe(true);

    const sentBody = fetchStub.mock.calls[1]?.[1]?.body;
    expect(sentBody).toBeInstanceOf(FormData);
    const sent = sentBody as FormData;
    expect(sent.get('defaultPlot')).toBe('101');
    expect(sent.get('defaultSubsample')).toBe('4');
    // Explicit fields stay empty — the defaults supply the target.
    expect(sent.get('plot')).toBe('');
    expect(sent.get('subsample')).toBe('');

    // W3: the banner reflects the echoed resolve end-to-end (lastResult).
    const synced = await getRecordingById(second.id);
    expect(synced?.lastResult?.usedDefaultTarget).toBe(true);
    expect(
      summarizeSync(synced?.lastResult as SyncRecordingResponse).lines,
    ).toContain('Used default target — Plot 101, Subsample 4');
  });

  it('STAYS on the same subsample when the block is only partly written, resending it as defaults', async () => {
    // The server wrote ONE cell of the 3-wide LEAF1 block → not full → the run
    // must continue on the SAME subsample, so the next values-only clip ships
    // 101/3 again (the server appends to that row's next empty block cell).
    const fetchStub = stubRealTransport(() => syncResponse());

    const first = makeRec({ plot: null, subsample: null, value: 5 });
    await saveRecording(first);
    expect(await autoSync(first.id)).toBe(true);
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 3 });

    const second = makeRec({ plot: null, subsample: null, value: 6 });
    await saveRecording(second);
    expect(await autoSync(second.id)).toBe(true);

    const sent = fetchStub.mock.calls[1]?.[1]?.body as FormData;
    expect(sent.get('defaultPlot')).toBe('101');
    expect(sent.get('defaultSubsample')).toBe('3');
  });

  it('never ships a default plot when the clip carries an explicit plot', async () => {
    const fetchStub = stubRealTransport();

    // Explicit plot said; subsample not said. The partial pair must NOT pull
    // defaults — the snapshot condition requires both fields absent.
    const rec = makeRec({ plot: 12, subsample: null, value: 5 });
    await saveRecording(rec);
    expect(await autoSync(rec.id)).toBe(true);

    const wire = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    expect(wire).toBeInstanceOf(FormData);
    expect(wire.get('plot')).toBe('12');
    expect(wire.get('subsample')).toBe('');
    expect(wire.get('defaultPlot')).toBeNull();
    expect(wire.get('defaultSubsample')).toBeNull();
  });

  it('sweep chain: two pending values-only clips advance subsample-by-subsample (full blocks)', async () => {
    const fetchStub = stubRealTransport();

    // Seed the last-written pair from a prior run: Plot 101, Sub 2 with a
    // FULL block — so the file-derived next target is Sub 3.
    writeDefaultTarget('trial-1', { plot: 101, subsample: 2, blockFull: true });

    // Two pending values-only clips in the SAME trial, order fixed by the
    // primary-key tiebreak of the status index ('sweep-1' before 'sweep-2').
    // Each snapshots the file-derived next target left by the previous one —
    // Sub 3, then Sub 4 (the stub writes a full block each time → advance).
    const first = makeRec({
      id: 'sweep-1',
      plot: null,
      subsample: null,
      value: 7,
    });
    const second = makeRec({
      id: 'sweep-2',
      plot: null,
      subsample: null,
      value: 9,
    });
    await saveRecording(first);
    await saveRecording(second);

    await sweepPendingSyncs();

    const firstWire = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    const secondWire = fetchStub.mock.calls[1]?.[1]?.body as FormData;
    expect(firstWire.get('defaultPlot')).toBe('101');
    expect(firstWire.get('defaultSubsample')).toBe('3');
    expect(secondWire.get('defaultPlot')).toBe('101');
    expect(secondWire.get('defaultSubsample')).toBe('4');
    // Written twice (2 → 3 → 4, full blocks) — the store only ever records
    // what was written; the queue drained.
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 4,
      blockFull: true,
    });
    expect(await getRecordingsByStatus('pending')).toHaveLength(0);
  });

  it('retry replays the frozen first-send snapshot, not the advanced live memory', async () => {
    const fetchStub = stubRealTransport();
    // First send of the values-only clip fails (500) after the snapshot froze.
    fetchStub.mockResolvedValueOnce(new Response('boom', { status: 500 }));
    // Last written 101/2 (full block) → the file-derived next target is 101/3.
    writeDefaultTarget('trial-1', { plot: 101, subsample: 2, blockFull: true });

    const first = makeRec({ id: 'w1-a', plot: null, subsample: null, value: 5 });
    await saveRecording(first);
    expect(await autoSync(first.id)).toBe(false);
    // The freeze is persisted with the row — 101/3, derived from the payload.
    expect((await getRecordingById(first.id))?.defaultTarget).toEqual({
      plot: 101,
      subsample: 3,
    });

    // Meanwhile a later clip writes at 101/3 (full block) and moves the live
    // pair on to 101/3.
    const later = makeRec({
      id: 'w1-b',
      plot: null,
      subsample: null,
      value: 6,
    });
    await saveRecording(later);
    expect(await autoSync(later.id)).toBe(true);
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
    // Live memory has moved PAST the frozen value: the next derive is 101/4.
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 4 });

    // The retry (call index 2: two sends) must ship THIS row's frozen 101/3,
    // not the now-current 101/4.
    expect(await autoSync(first.id)).toBe(true);
    const retryWire = fetchStub.mock.calls[2]?.[1]?.body as FormData;
    expect(retryWire.get('defaultPlot')).toBe('101');
    expect(retryWire.get('defaultSubsample')).toBe('3');
  });

  it('a frozen null stays null on the wire — a retry never re-targets', async () => {
    const fetchStub = stubRealTransport();
    // The row was snapshotted at first send with NOTHING remembered →
    // `defaultTarget: null` (checked, found nothing), persisted with the row.
    const rec = makeRec({
      plot: null,
      subsample: null,
      value: 5,
      defaultTarget: null,
    });
    await saveRecording(rec);
    // Live memory now HAS a target — but the row's freeze predates it.
    writeDefaultTarget('trial-1', { plot: 101, subsample: 3, blockFull: true });

    expect(await autoSync(rec.id)).toBe(true);

    // Key PRESENT (frozen null) → no default fields go out, even though the
    // live file-derived next target would resolve to 101/4.
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 4 });
    const wire = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    expect(wire).toBeInstanceOf(FormData);
    expect(wire.get('defaultPlot')).toBeNull();
    expect(wire.get('defaultSubsample')).toBeNull();
  });

  it('advances the remembered target after a Written correction (full block)', async () => {
    const rec = makeRec({ status: 'needs-attention' });
    await saveRecording(rec);
    correctMock.mockResolvedValue(
      syncResponse({
        status: 'Written',
        wasTranscriptOnly: false,
        writtenCells: fullBlock(101, 3),
      }),
    );

    const outcome = await applyCorrection(rec.id, {
      plot: 12,
      subsample: 1,
      value: 8,
    });

    expect(outcome.kind).toBe('success');
    // The corrected result resolved to 101/3 with a FULL block → the store
    // records 101/3 (blockFull true); the next values-only clip derives
    // Sub 4 from the payload's rows[].
    expect(readDefaultTarget('trial-1')).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
    expect(resolveNextTarget('trial-1')).toEqual({ plot: 101, subsample: 4 });
  });

  it('does not advance on a Flagged sync result', async () => {
    const rec = makeRec({ plot: null, subsample: null, value: 5 });
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
    expect(readDefaultTarget('trial-1')).toBeNull();
  });

  it('omits traitOrdinal from FormData when not provided (backward-compatibility)', async () => {
    const fetchStub = stubRealTransport();
    const rec = makeRec({ plot: 101, subsample: 3, value: 7 });
    await saveRecording(rec);
    expect(await autoSync(rec.id)).toBe(true);

    const wire = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    expect(wire).toBeInstanceOf(FormData);
    expect(wire.get('traitOrdinal')).toBeNull();
  });

  it('sends traitOrdinal when provided', async () => {
    const fetchStub = stubRealTransport();
    const rec = makeRec({ plot: 101, subsample: 3, value: 7, traitOrdinal: 5 });
    await saveRecording(rec);
    expect(await autoSync(rec.id)).toBe(true);

    const wire = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    expect(wire).toBeInstanceOf(FormData);
    expect(wire.get('traitOrdinal')).toBe('5');
  });

  it('does not send traitOrdinal when it is non-positive or non-integer (treated as absent)', async () => {
    const fetchStub = stubRealTransport();
    const rec0 = makeRec({ plot: 101, subsample: 3, value: 7, traitOrdinal: 0 });
    const recNeg = makeRec({
      id: 'rec-neg',
      plot: 101,
      subsample: 3,
      value: 7,
      traitOrdinal: -1,
    });
    const recFloat = makeRec({
      id: 'rec-float',
      plot: 101,
      subsample: 3,
      value: 7,
      traitOrdinal: 2.5,
    });
    await saveRecording(rec0);
    await saveRecording(recNeg);
    await saveRecording(recFloat);
    expect(await autoSync(rec0.id)).toBe(true);
    expect(await autoSync(recNeg.id)).toBe(true);
    expect(await autoSync(recFloat.id)).toBe(true);

    const wire0 = fetchStub.mock.calls[0]?.[1]?.body as FormData;
    const wire1 = fetchStub.mock.calls[1]?.[1]?.body as FormData;
    const wire2 = fetchStub.mock.calls[2]?.[1]?.body as FormData;
    expect(wire0.get('traitOrdinal')).toBeNull();
    expect(wire1.get('traitOrdinal')).toBeNull();
    expect(wire2.get('traitOrdinal')).toBeNull();
  });
});