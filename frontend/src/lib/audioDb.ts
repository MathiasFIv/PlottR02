/**
 * Raw IndexedDB wrapper for voice recordings (no library).
 *
 * DB `plottR-audio` (version 1) holds a single object store `recordings`
 * (keyPath `id`) with a secondary index on `status` so the sync worker can
 * page through recordings by state without scanning the whole store.
 *
 * Recording blobs are stored in IndexedDB directly (structured-cloned), so
 * recordings survive page refreshes and are available offline.
 */

import type { SyncRecordingResponse } from './api';

/**
 * Lifecycle of a captured clip (doc PLAN §6.1):
 * - `pending`            saved, auto-send scheduled (transient)
 * - `ready`              LEGACY — old confirmed-but-unsent rows; swept
 *                        (auto-sent) on startup, never newly written
 * - `syncing`            upload in flight
 * - `synced`             server returned Written / SkippedPresent / Replay
 *                        with needsReview false
 * - `needs-attention`    server returned needsReview → correction surface
 * - `failed`             network/server error → retry affordance
 */
export type RecordingStatus =
  | 'pending'
  | 'ready'
  | 'syncing'
  | 'synced'
  | 'needs-attention'
  | 'failed';

export interface AudioRecording {
  id: string; // crypto.randomUUID()
  blob: Blob;
  mimeType: string;
  createdAt: string; // ISO string
  trialId: string | null;
  plot: number | null; // confirmed by a correction (or legacy 'ready'); null = transcript-authoritative
  subsample: number | null; // ditto
  value: number | null; // ditto
  status: RecordingStatus;
  syncAttempts: number;
  lastError?: string;
  /** Last /sync or /correct response — persists the outcome across reloads so
   *  the correction surface reappears. Type-only import: no runtime cycle. */
  lastResult?: SyncRecordingResponse;
}

const DB_NAME = 'plottR-audio';
const DB_VERSION = 1;
const STORE_NAME = 'recordings';
const STATUS_INDEX_NAME = 'status';

/** Cached open-DB promise so repeated calls reuse one connection. */
let dbPromise: Promise<IDBDatabase> | null = null;

/** Opens (and on first run, upgrades) the `plottR-audio` database. */
export function openDb(): Promise<IDBDatabase> {
  if (dbPromise) return dbPromise;

  dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
    if (typeof indexedDB === 'undefined') {
      reject(new Error('IndexedDB is not available in this browser.'));
      return;
    }

    const request = indexedDB.open(DB_NAME, DB_VERSION);

    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        const store = db.createObjectStore(STORE_NAME, { keyPath: 'id' });
        store.createIndex(STATUS_INDEX_NAME, 'status', { unique: false });
      }
    };

    request.onsuccess = () => {
      const db = request.result;

      // Never cache a dead connection: the browser can close the DB on its own
      // (storage pressure, tab eviction) and another tab can request a version
      // upgrade. Drop the cached promise either way so the next call re-opens.
      db.onclose = () => {
        dbPromise = null;
      };
      db.onversionchange = () => {
        db.close();
        dbPromise = null;
      };

      resolve(db);
    };

    request.onerror = () => {
      dbPromise = null;
      reject(
        new Error(
          `Failed to open IndexedDB: ${request.error?.message ?? 'unknown error'}`,
        ),
      );
    };

    request.onblocked = () => {
      dbPromise = null;
      reject(new Error('IndexedDB open request was blocked by another connection.'));
    };
  });

  return dbPromise;
}

/** Resolves with the result of a single IDB request, or rejects on error. */
function requestResult<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () =>
      reject(
        new Error(
          `IndexedDB request failed: ${request.error?.message ?? 'unknown error'}`,
        ),
      );
  });
}

/** Resolves when the transaction commits, or rejects on error/abort. */
function transactionDone(tx: IDBTransaction): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () =>
      reject(
        new Error(
          `IndexedDB transaction failed: ${tx.error?.message ?? 'unknown error'}`,
        ),
      );
    tx.onabort = () =>
      reject(
        new Error(
          `IndexedDB transaction aborted: ${tx.error?.message ?? 'unknown error'}`,
        ),
      );
  });
}

/**
 * Factory for a fresh recording. Generates the id + createdAt and applies the
 * start-of-life defaults (status 'pending', syncAttempts 0, null override
 * fields — plot/subsample/value are transcript-authoritative until a human
 * correction on the needs-attention card).
 */
export function newRecording(input: {
  blob: Blob;
  mimeType: string;
  trialId: string | null;
}): AudioRecording {
  return {
    id: crypto.randomUUID(),
    blob: input.blob,
    mimeType: input.mimeType,
    createdAt: new Date().toISOString(),
    trialId: input.trialId,
    plot: null,
    subsample: null,
    value: null,
    status: 'pending',
    syncAttempts: 0,
  };
}

/** Persists (inserts or replaces) a recording in the store. */
export async function saveRecording(rec: AudioRecording): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  tx.objectStore(STORE_NAME).put(rec);
  await transactionDone(tx);
}

/** Reads a recording by id, or null when it does not exist. */
export async function getRecordingById(
  id: string,
): Promise<AudioRecording | null> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readonly');
  const request = tx.objectStore(STORE_NAME).get(id);
  const rec = await requestResult(request);
  return (rec as AudioRecording | undefined) ?? null;
}

/** Returns all recordings currently in the given status. */
export async function getRecordingsByStatus(
  status: RecordingStatus,
): Promise<AudioRecording[]> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readonly');
  const index = tx.objectStore(STORE_NAME).index(STATUS_INDEX_NAME);
  const recs = await requestResult(index.getAll(status));
  return recs as AudioRecording[];
}

/** Persists changes to an existing recording (status, overrides, attempts). */
export async function updateRecording(rec: AudioRecording): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  tx.objectStore(STORE_NAME).put(rec);
  await transactionDone(tx);
}

/** Deletes a recording by id. Resolves even when the id was not present. */
export async function deleteRecording(id: string): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  tx.objectStore(STORE_NAME).delete(id);
  await transactionDone(tx);
}

/** Number of recordings currently in the given status. */
export async function getCountByStatus(
  status: RecordingStatus,
): Promise<number> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readonly');
  const index = tx.objectStore(STORE_NAME).index(STATUS_INDEX_NAME);
  const count = await requestResult(index.count(status));
  return count;
}