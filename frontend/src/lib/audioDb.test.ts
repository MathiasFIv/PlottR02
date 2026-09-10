import 'fake-indexeddb/auto';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  deleteRecording,
  getPendingCount,
  getRecordingById,
  getRecordingsByStatus,
  newRecording,
  openDb,
  saveRecording,
  updateRecording,
} from './audioDb';
import type { AudioRecording } from './audioDb';

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
    expect(await getPendingCount()).toBe(0);
  });

  it('getPendingCount only counts pending records across mixed statuses', async () => {
    await saveRecording(makeRec());
    await saveRecording(makeRec());
    await saveRecording(makeRec({ status: 'synced' }));
    await saveRecording(makeRec({ status: 'failed' }));

    expect(await getPendingCount()).toBe(2);
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
});