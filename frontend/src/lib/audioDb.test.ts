import 'fake-indexeddb/auto';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  deleteRecording,
  getCountByStatus,
  getRecordingById,
  getRecordingsByStatus,
  newRecording,
  openDb,
  saveRecording,
  updateRecording,
} from './audioDb';
import type { AudioRecording } from './audioDb';
import type { SyncRecordingResponse } from './api';

/** Deterministic fixture — explicit id, tiny fake audio blob. */
function makeRec(overrides: Partial<AudioRecording> = {}): AudioRecording {
  return {
    ...newRecording({
      blob: new Blob(['fake-audio-bytes'], { type: 'audio/webm' }),
      mimeType: 'audio/webm',
      trialId: 'trial-1',
      traitOrdinal: 1,
    }),
    id: 'rec-' + Math.random().toString(36).slice(2),
    ...overrides,
  };
}

/** A full SyncVoiceRecordingResult so lastResult round-trips under strict typing. */
function syncResponse(): SyncRecordingResponse {
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
    writtenCells: [{ plot: 101, subsample: 3, traitOrdinal: 1, value: 7, cellAddress: 'C24' }],
    skippedPresentCells: [],
    missingSubsampleIndices: [],
  };
}

beforeEach(async () => {
  // openDb is cached across calls, so clear the store instead of deleting the
  // database (a delete would be blocked by the open connection).
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction('recordings', 'readwrite');
    tx.objectStore('recordings').clear();
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(new Error('Failed to clear recordings store'));
  });
});

describe('audioDb', () => {
  it('newRecording applies pending defaults and generated fields', () => {
    const rec = newRecording({
      blob: new Blob(['x'], { type: 'audio/mp4' }),
      mimeType: 'audio/mp4',
      trialId: 'trial-9',
      traitOrdinal: 1,
    });

    expect(rec.id).toBeTruthy();
    expect(rec.createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    expect(rec.mimeType).toBe('audio/mp4');
    expect(rec.trialId).toBe('trial-9');
    expect(rec.traitOrdinal).toBe(1);
    expect(rec.status).toBe('pending');
    expect(rec.syncAttempts).toBe(0);
    expect(rec.plot).toBeNull();
    expect(rec.subsample).toBeNull();
    expect(rec.value).toBeNull();
    expect(rec.lastError).toBeUndefined();
  });

  it('round-trips a saved recording through getById (blob included)', async () => {
    const rec = makeRec();

    await saveRecording(rec);

    const found = await getRecordingById(rec.id);
    expect(found).not.toBeNull();
    expect(found?.trialId).toBe('trial-1');
    expect(found?.status).toBe('pending');
    expect(found?.blob.type).toBe('audio/webm');
    expect(await found?.blob.text()).toBe('fake-audio-bytes');
  });

  it('updateRecording persists a status change', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    const synced = { ...rec, status: 'synced' as const };
    await updateRecording(synced);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('synced');

    // Gone from the pending index after the status flip.
    const pending = await getRecordingsByStatus('pending');
    expect(pending).toHaveLength(0);
    const syncedNow = await getRecordingsByStatus('synced');
    expect(syncedNow.map((r) => r.id)).toContain(rec.id);
  });

  it('deleteRecording removes the record', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    await deleteRecording(rec.id);

    expect(await getRecordingById(rec.id)).toBeNull();
    expect(await getCountByStatus('pending')).toBe(0);
  });

  it('getCountByStatus counts records in the given status across mixed statuses', async () => {
    await saveRecording(makeRec());
    await saveRecording(makeRec());
    await saveRecording(makeRec({ status: 'synced' }));
    await saveRecording(makeRec({ status: 'failed' }));

    expect(await getCountByStatus('pending')).toBe(2);
    expect(await getCountByStatus('ready')).toBe(0);
  });

  it('getRecordingsByStatus returns only records in that status', async () => {
    await saveRecording(makeRec({ id: 'pending-1' }));
    await saveRecording(makeRec({ id: 'pending-2' }));
    await saveRecording(makeRec({ id: 'ready-1', status: 'ready' }));
    await saveRecording(makeRec({ id: 'failed-1', status: 'failed' }));

    const pending = await getRecordingsByStatus('pending');
    expect(pending.map((r) => r.id).sort()).toEqual(['pending-1', 'pending-2']);

    const failed = await getRecordingsByStatus('failed');
    expect(failed.map((r) => r.id)).toEqual(['failed-1']);
  });

  it('V2E: sync lifecycle pending → syncing → synced moves the row across indexes', async () => {
    const rec = makeRec();
    await saveRecording(rec);

    expect(await getRecordingsByStatus('pending')).toHaveLength(1);

    // The engine marks the row syncing before the request, then synced after.
    await updateRecording({ ...rec, status: 'syncing' });
    expect(await getRecordingsByStatus('pending')).toHaveLength(0);
    expect(await getRecordingsByStatus('syncing')).toHaveLength(1);

    await updateRecording({ ...rec, status: 'synced', syncAttempts: 1 });
    expect(await getRecordingsByStatus('syncing')).toHaveLength(0);
    expect(await getRecordingsByStatus('synced')).toHaveLength(1);
    expect(await getCountByStatus('pending')).toBe(0);
  });

  it('round-trips lastResult (SyncRecordingResponse) and needs-attention counts', async () => {
    const result = syncResponse();
    const rec = makeRec({
      status: 'needs-attention',
      lastResult: result,
      syncAttempts: 1,
    });
    await saveRecording(rec);

    const found = await getRecordingById(rec.id);
    expect(found?.status).toBe('needs-attention');
    expect(found?.lastResult?.status).toBe('Written');
    expect(found?.lastResult?.writtenCells?.[0]?.cellAddress).toBe('C24');
    expect(await getCountByStatus('needs-attention')).toBe(1);

    // Pretend a correction succeeded: row → synced, result stays intact.
    await updateRecording({ ...rec, status: 'synced' });
    expect(await getCountByStatus('needs-attention')).toBe(0);
  });
});