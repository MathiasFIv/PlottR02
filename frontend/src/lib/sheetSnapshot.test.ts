import { describe, expect, it } from 'vitest';
import type { TrialSheet } from '../types';
import {
  MAX_SNAPSHOT_BYTES,
  deleteSheetSnapshot,
  readSheetSnapshot,
  sheetSnapshotKey,
  writeSheetSnapshot,
} from './sheetSnapshot';

const TRIAL = 'trial-1';
const OTHER_TRIAL = 'trial-2';

/** Minimal last-good payload — only the fields the envelope has to carry. */
const sheet: TrialSheet = {
  trialId: TRIAL,
  version: 'v1',
  changed: true,
  fileName: '4 subsamples.xlsx',
  generatedAt: '2026-10-07T07:45:13.402Z',
  metadata: null,
  columns: [
    {
      traitOrdinal: 1,
      column: 'C',
      pestCode: 'SEPTTR',
      partRated: 'LEAF1',
      label: 'SEPTTR × LEAF1',
    },
  ],
  rows: [{ row: 22, plot: 101, subsample: 1, values: [0] }],
};

type MemoryStorage = {
  getItem: (key: string) => string | null;
  setItem: (key: string, value: string) => void;
  removeItem?: (key: string) => void;
};

/** Map-backed stub — plain Node, no DOM, no reliance on a global. */
function memoryStorage(): MemoryStorage & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return {
    map,
    getItem: (key) => map.get(key) ?? null,
    setItem: (key, value) => {
      map.set(key, value);
    },
    removeItem: (key) => {
      map.delete(key);
    },
  };
}

describe('sheetSnapshotKey', () => {
  it('namespaces per trial so one trial can never hydrate another', () => {
    expect(sheetSnapshotKey(TRIAL)).toBe(`plottR-sheet-snapshot:${TRIAL}`);
    expect(sheetSnapshotKey(TRIAL)).not.toBe(sheetSnapshotKey(OTHER_TRIAL));
  });
});

describe('writeSheetSnapshot / readSheetSnapshot', () => {
  it('round-trips a written payload for the same trial', () => {
    const storage = memoryStorage();
    expect(writeSheetSnapshot(TRIAL, sheet, 'network', storage)).toBe(true);

    const snapshot = readSheetSnapshot(TRIAL, storage);
    expect(snapshot).not.toBeNull();
    expect(snapshot?.trialId).toBe(TRIAL);
    expect(snapshot?.source).toBe('network');
    expect(snapshot?.sheet).toEqual(sheet);
    // savedAt is stamped at write time — an ISO instant, not the payload's own
    // generatedAt, so the badge can say when the copy was taken.
    expect(Number.isNaN(Date.parse(snapshot?.savedAt ?? ''))).toBe(false);
  });

  it('records where the copy came from (network vs cache)', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheet, 'cache', storage);
    expect(readSheetSnapshot(TRIAL, storage)?.source).toBe('cache');
  });

  it('reads null when the trial has never been stored', () => {
    expect(readSheetSnapshot(TRIAL, memoryStorage())).toBeNull();
  });

  it('reads null for corrupt JSON', () => {
    const storage = memoryStorage();
    storage.map.set(sheetSnapshotKey(TRIAL), '{ not valid json');
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();
  });

  it('rejects a foreign/mismatched envelope even under this trial key', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheet, 'network', storage);
    const raw = storage.map.get(sheetSnapshotKey(TRIAL)) as string;
    // Same bytes parked under another trial's key: the envelope still says
    // trial-1, so trial-2 must refuse to hydrate from it.
    storage.map.set(sheetSnapshotKey(OTHER_TRIAL), raw);
    expect(readSheetSnapshot(OTHER_TRIAL, storage)).toBeNull();
    // …while the rightful owner still reads it.
    expect(readSheetSnapshot(TRIAL, storage)?.sheet).toEqual(sheet);
  });

  it('rejects a structurally broken envelope (missing/invalid fields)', () => {
    const storage = memoryStorage();
    storage.map.set(
      sheetSnapshotKey(TRIAL),
      JSON.stringify({ trialId: TRIAL, savedAt: 'yesterday', source: 'disk', sheet: 1 }),
    );
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();
  });

  it('rejects a sheet with a missing/wrong-typed version or non-array payload', () => {
    // versionRef is seeded from sheet.version; `undefined !== null` is true, so
    // a versionless sheet would make a later failure misclassify as `offline`.
    const storage = memoryStorage();
    const envelope = (sheet: unknown): string =>
      JSON.stringify({
        trialId: TRIAL,
        savedAt: new Date().toISOString(),
        source: 'network',
        sheet,
      });

    storage.map.set(sheetSnapshotKey(TRIAL), envelope({ ...sheet, version: undefined }));
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();

    storage.map.set(sheetSnapshotKey(TRIAL), envelope({ ...sheet, version: 42 }));
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();

    storage.map.set(sheetSnapshotKey(TRIAL), envelope({ ...sheet, rows: 'nope' }));
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();

    // The intact payload still reads back — the guard rejects shape, not data.
    storage.map.set(sheetSnapshotKey(TRIAL), envelope(sheet));
    expect(readSheetSnapshot(TRIAL, storage)?.sheet).toEqual(sheet);
  });

  it('never throws when storage itself throws', () => {
    const throwing: MemoryStorage = {
      getItem: () => {
        throw new Error('private browsing');
      },
      setItem: () => {
        throw new Error('quota exceeded');
      },
    };
    expect(readSheetSnapshot(TRIAL, throwing)).toBeNull();
    expect(writeSheetSnapshot(TRIAL, sheet, 'network', throwing)).toBe(false);
  });
});

describe('snapshot size cap', () => {
  it('refuses to write a payload over the byte cap', () => {
    const storage = memoryStorage();
    const fat: TrialSheet = { ...sheet, version: 'x'.repeat(MAX_SNAPSHOT_BYTES) };
    expect(writeSheetSnapshot(TRIAL, fat, 'network', storage)).toBe(false);
    expect(storage.map.size).toBe(0);
  });

  it('reads back null for an oversize value already in storage', () => {
    const storage = memoryStorage();
    storage.map.set(
      sheetSnapshotKey(TRIAL),
      JSON.stringify({ trialId: TRIAL, savedAt: new Date().toISOString() }) +
        'x'.repeat(MAX_SNAPSHOT_BYTES),
    );
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();
  });

  it('caps bytes, not characters — multibyte content counts as UTF-8', () => {
    const storage = memoryStorage();
    const unicode: TrialSheet = { ...sheet, fileName: 'æøå'.repeat(MAX_SNAPSHOT_BYTES) };
    expect(writeSheetSnapshot(TRIAL, unicode, 'network', storage)).toBe(false);
  });
});

describe('deleteSheetSnapshot', () => {
  it('removes the stored copy', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheet, 'network', storage);
    deleteSheetSnapshot(TRIAL, storage);
    expect(readSheetSnapshot(TRIAL, storage)).toBeNull();
  });

  it('is a no-op without removeItem and when storage throws', () => {
    const readOnly: MemoryStorage = {
      getItem: () => null,
      setItem: () => {},
    };
    expect(() => deleteSheetSnapshot(TRIAL, readOnly)).not.toThrow();
    expect(() =>
      deleteSheetSnapshot(TRIAL, {
        getItem: () => {
          throw new Error('nope');
        },
        setItem: () => {},
      }),
    ).not.toThrow();
  });
});
