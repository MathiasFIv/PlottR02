import { describe, expect, it } from 'vitest';
import {
  readDefaultTarget,
  rememberWrittenTarget,
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
  it('round-trips a written target (pair + blockFull) through read', () => {
    const storage = memoryStorage();
    writeDefaultTarget(
      TRIAL,
      { plot: 101, subsample: 3, blockFull: true },
      storage,
    );
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });

    // The flag is data, not a default: a STAY (unfilled block) round-trips too.
    writeDefaultTarget(
      TRIAL,
      { plot: 101, subsample: 3, blockFull: false },
      storage,
    );
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
  });

  it('namespaces the memory per trial', () => {
    const storage = memoryStorage();
    writeDefaultTarget(
      TRIAL,
      { plot: 101, subsample: 3, blockFull: true },
      storage,
    );
    // A different trial must NEVER see this trial's cursor — plot numbers
    // repeat freely across workbooks.
    expect(readDefaultTarget(OTHER_TRIAL, storage)).toBeNull();
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
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
    storage.map.set(KEY_FOR_TRIAL, JSON.stringify([101, 3, true]));
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('returns null for a partially-invalid payload', () => {
    const storage = memoryStorage();
    storage.map.set(KEY_FOR_TRIAL, JSON.stringify({ plot: 101 }));
    storage.map.set(
      `${KEY_FOR_TRIAL}-noSub`,
      JSON.stringify({ plot: 101, blockFull: true }),
    );
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    expect(readDefaultTarget(`${TRIAL}-noSub`, storage)).toBeNull();
  });

  it('reads a LEGACY pair (no blockFull) as null — no flag is ever guessed', () => {
    // Pre-fix store contents: { plot, subsample } with no blockFull. It must
    // read as "nothing remembered" (never invent a STAY/advance decision),
    // which in turn makes `resolveNextTarget` derive no target.
    const storage = memoryStorage();
    storage.map.set(KEY_FOR_TRIAL, JSON.stringify({ plot: 101, subsample: 3 }));
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('reads a non-boolean blockFull as null', () => {
    const storage = memoryStorage();
    storage.map.set(
      KEY_FOR_TRIAL,
      JSON.stringify({ plot: 101, subsample: 3, blockFull: 'yes' }),
    );
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
      { plot: 0, subsample: 3, blockFull: true },
      { plot: -1, subsample: 3, blockFull: true },
      { plot: 101, subsample: 0, blockFull: true },
      { plot: 101, subsample: -2, blockFull: true },
      { plot: 1.5, subsample: 3, blockFull: true },
      { plot: 101, subsample: 2.5, blockFull: true },
      { plot: '101', subsample: 3, blockFull: true },
      { plot: 101, subsample: '3', blockFull: true },
    ];
    for (const raw of badTargets) {
      const storage = memoryStorage();
      writeDefaultTarget(
        TRIAL,
        raw as { plot: number; subsample: number; blockFull: boolean },
        storage,
      );
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('ignores a write whose blockFull is missing or not a boolean', () => {
    const badTargets: Array<unknown> = [
      { plot: 101, subsample: 3 },
      { plot: 101, subsample: 3, blockFull: 'true' },
      { plot: 101, subsample: 3, blockFull: null },
      { plot: 101, subsample: 3, blockFull: 1 },
    ];
    for (const raw of badTargets) {
      const storage = memoryStorage();
      writeDefaultTarget(
        TRIAL,
        raw as { plot: number; subsample: number; blockFull: boolean },
        storage,
      );
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('rememberWrittenTarget stores the pair + flag AS WRITTEN — never subsample + 1', () => {
    const storage = memoryStorage();
    rememberWrittenTarget(
      TRIAL,
      { plot: 101, subsample: 4, blockFull: true },
      storage,
    );
    // The workbook decides what comes next (deriveNextTarget walks rows[]);
    // the store only remembers the last write, so 101/4 stays 101/4 — the old
    // `+ 1` here invented a nonexistent 101/5 at the end of a plot.
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 4,
      blockFull: true,
    });
    // A mid-plot write with an UNFILLED block is stored verbatim too — no
    // increment, and the flag rides along.
    rememberWrittenTarget(
      TRIAL,
      { plot: 101, subsample: 3, blockFull: false },
      storage,
    );
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
  });

  it('rememberWrittenTarget no-ops unless both fields are positive', () => {
    const badResults: Array<{
      plot?: number | null;
      subsample?: number | null;
      blockFull?: boolean;
    }> = [
      { plot: null, subsample: 3, blockFull: true },
      { plot: 101, subsample: null, blockFull: true },
      { plot: 101, subsample: 0, blockFull: true },
      { plot: undefined, subsample: 3, blockFull: true },
      { plot: -1, subsample: 3, blockFull: true },
      { plot: 101, subsample: -1, blockFull: true },
      { plot: 1.5, subsample: 3, blockFull: true },
      { plot: 101, subsample: 2.5, blockFull: true },
    ];
    for (const result of badResults) {
      const storage = memoryStorage();
      rememberWrittenTarget(TRIAL, result, storage);
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('rememberWrittenTarget no-ops when blockFull is missing or not a boolean', () => {
    const badResults: Array<{
      plot?: number | null;
      subsample?: number | null;
      blockFull?: boolean | string;
    }> = [
      { plot: 101, subsample: 3 },
      { plot: 101, subsample: 3, blockFull: 'true' },
    ];
    for (const result of badResults) {
      const storage = memoryStorage();
      rememberWrittenTarget(
        TRIAL,
        result as { plot: number; subsample: number; blockFull: boolean },
        storage,
      );
      expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    }
  });

  it('rememberWrittenTarget no-ops when the result omits the target entirely', () => {
    const storage = memoryStorage();
    rememberWrittenTarget(TRIAL, {}, storage);
    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
  });

  it('is Node-safe: reads/writes with no window do not throw', () => {
    // safeStorage() falls back to a throwaway null-store in non-DOM
    // environments.
    expect(readDefaultTarget(TRIAL)).toBeNull();
    expect(() =>
      writeDefaultTarget(TRIAL, { plot: 101, subsample: 3, blockFull: true }),
    ).not.toThrow();
    expect(() =>
      rememberWrittenTarget(TRIAL, {
        plot: 101,
        subsample: 3,
        blockFull: false,
      }),
    ).not.toThrow();
  });
});
