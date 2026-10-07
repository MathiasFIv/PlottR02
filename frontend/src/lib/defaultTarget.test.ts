import { describe, expect, it } from 'vitest';
import {
  advanceDefaultTarget,
  readDefaultTarget,
  writeDefaultTarget,
} from './defaultTarget';

/** Every read/write in this suite targets this trial. */
const TRIAL = 'trial-1';
const OTHER_TRIAL = 'trial-2';
/** Storage key derived for a trial — mirrors the implementation's namespacing. */
const KEY_FOR_TRIAL = `plottR-default-target:${TRIAL}`;

type StorageStub = Pick<Storage, 'getItem' | 'setItem'>;

/** Minimal Map-backed storage stub — no DOM, no reliance on a global. */
function memoryStorage(): StorageStub & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return {
    map,
    getItem: (key) => map.get(key) ?? null,
    setItem: (key, value) => {
      map.set(key, value);
    },
  };
}

describe('defaultTarget', () => {
  it('round-trips a written target through read', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 3 }, storage);
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
    });
  });

  it('namespaces the memory per trial', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 3 }, storage);
    // A different trial must NEVER see this trial's cursor — plot numbers
    // repeat freely across workbooks.
    expect(readDefaultTarget(OTHER_TRIAL, storage)).toBeNull();
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
    });
  });

  it('returns null when nothing was stored', () => {
    expect(readDefaultTarget(TRIAL, memoryStorage())).toBeNull();
  });

  it('returns null for corrupted JSON', () => {
    const storage = memoryStorage();
    storage.map.set(KEY_FOR_TRIAL, '{ not valid json');
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('returns null for a non-object payload', () => {
    const storage = memoryStorage();
    storage.map.set(KEY_FOR_TRIAL, JSON.stringify([101, 3]));
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('returns null for a partially-invalid payload', () => {
    const storage = memoryStorage();
    storage.map.set(KEY_FOR_TRIAL, JSON.stringify({ plot: 101 }));
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('returns null when storage.getItem throws', () => {
    // A broken/denied storage must degrade to "nothing remembered" — reads
    // from the store code path are always try/caught and never crash.
    const broken: StorageStub = {
      getItem: () => {
        throw new Error('storage unavailable');
      },
      setItem: () => {},
    };
    expect(readDefaultTarget(TRIAL, broken)).toBeNull();
  });

  it('ignores non-positive or non-integer values on write', () => {
    const badTargets: Array<unknown> = [
      { plot: 0, subsample: 3 },
      { plot: -1, subsample: 3 },
      { plot: 101, subsample: 0 },
      { plot: 101, subsample: -2 },
      { plot: 1.5, subsample: 3 },
      { plot: 101, subsample: 2.5 },
      { plot: '101', subsample: 3 },
      { plot: 101, subsample: '3' },
    ];
    for (const raw of badTargets) {
      const storage = memoryStorage();
      writeDefaultTarget(
        TRIAL,
        raw as { plot: number; subsample: number },
        storage,
      );
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('advanceDefaultTarget writes subsample + 1', () => {
    const storage = memoryStorage();
    advanceDefaultTarget(TRIAL, { plot: 101, subsample: 3 }, storage);
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 4,
    });
  });

  it('advanceDefaultTarget no-ops unless both fields are positive', () => {
    const badResults: Array<{
      plot?: number | null;
      subsample?: number | null;
    }> = [
      { plot: null, subsample: 3 },
      { plot: 101, subsample: null },
      { plot: 101, subsample: 0 },
      { plot: undefined, subsample: 3 },
      { plot: -1, subsample: 3 },
      { plot: 101, subsample: -1 },
    ];
    for (const result of badResults) {
      const storage = memoryStorage();
      advanceDefaultTarget(TRIAL, result, storage);
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('advanceDefaultTarget no-ops when the result omits the target entirely', () => {
    const storage = memoryStorage();
    advanceDefaultTarget(TRIAL, {}, storage);
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('is Node-safe: reads/writes with no window do not throw', () => {
    // safeStorage() falls back to a throwaway null-store in non-DOM
    // environments.
    expect(readDefaultTarget(TRIAL)).toBeNull();
    expect(() =>
      writeDefaultTarget(TRIAL, { plot: 101, subsample: 3 }),
    ).not.toThrow();
    expect(() =>
      advanceDefaultTarget(TRIAL, { plot: 101, subsample: 3 }),
    ).not.toThrow();
  });
});