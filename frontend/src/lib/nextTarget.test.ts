import { beforeEach, describe, expect, it } from 'vitest';
import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom, trialSheetAtom } from '../store/atoms';
import type { SheetRow, TrialSheet } from '../types';
import { writeDefaultTarget } from './defaultTarget';
import { deriveEndOfSheet, deriveNextTarget, resolveNextTarget, rowsForTrial } from './nextTarget';
import { sheetSnapshotKey, writeSheetSnapshot } from './sheetSnapshot';

const TRIAL = 'trial-1';
const OTHER_TRIAL = 'trial-2';

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

/** A storage that always blows up — must degrade to null, never throw. */
function brokenStorage(): StorageStub {
  return {
    getItem: () => {
      throw new Error('storage unavailable');
    },
    setItem: () => {
      throw new Error('storage unavailable');
    },
  };
}

/**
 * Workbook-shaped rows: 4 subsamples per plot in WORKBOOK order (the real
 * file is 101 1 … 101 4, 102 1 … 414 4). Excel `row` values are deliberately
 * NON-monotonic (they step down by 37 and wrap) so any `row`-arithmetic
 * match or ordering assumption fails loudly.
 */
const plots = [101, 102, 103, 414];
const rows: SheetRow[] = plots.flatMap((plot, p) =>
  [1, 2, 3, 4].map((subsample, s) => ({
    // Scrambled cross-refs: neither the array index nor `index + 22`.
    row: 245 - (((p * 4 + s) * 37) % 200),
    plot,
    subsample,
    values: [subsample === 4 ? 0 : null, null],
  })),
);

function sheetWith(payloadRows: SheetRow[] | null, trialId = TRIAL): TrialSheet {
  return {
    trialId,
    version: 'v1',
    changed: true,
    fileName: '4 subsamples.xlsx',
    generatedAt: '2026-10-06T09:45:13.402Z',
    metadata: null,
    // `readSheetSnapshot` structurally requires BOTH arrays — `columns` may
    // be empty here (never rendered) but not null, or the snapshot is void.
    columns: [],
    rows: payloadRows,
  };
}

beforeEach(() => {
  // The atoms are shared through getDefaultStore() across the suite.
  getDefaultStore().set(trialSheetAtom, null);
  getDefaultStore().set(sheetLastWrittenAtom, null);
});

describe('deriveNextTarget', () => {
  it('101 4 → 102 1: the step crosses the plot boundary (auto-continue)', () => {
    expect(deriveNextTarget(rows, { plot: 101, subsample: 4 })).toEqual({
      plot: 102,
      subsample: 1,
    });
    // …and a mid-plot step stays within the plot.
    expect(deriveNextTarget(rows, { plot: 101, subsample: 1 })).toEqual({
      plot: 101,
      subsample: 2,
    });
  });

  it('the last row of the sheet (414 4) derives null — no phantom pair', () => {
    expect(rows[rows.length - 1].plot).toBe(414);
    expect(rows[rows.length - 1].subsample).toBe(4);
    expect(deriveNextTarget(rows, { plot: 414, subsample: 4 })).toBeNull();
    // One step earlier still resolves — the null comes from being LAST, not
    // from the pair itself.
    expect(deriveNextTarget(rows, { plot: 414, subsample: 3 })).toEqual({
      plot: 414,
      subsample: 4,
    });
  });

  it('returns null when `from` is not in rows, rows are empty, or from is null', () => {
    // Legacy/stale/foreign pair (the old `+1` could store 101 5 — never a row).
    expect(deriveNextTarget(rows, { plot: 101, subsample: 5 })).toBeNull();
    expect(deriveNextTarget(rows, { plot: 999, subsample: 1 })).toBeNull();
    expect(deriveNextTarget([], { plot: 101, subsample: 1 })).toBeNull();
    expect(deriveNextTarget(rows, null)).toBeNull();
    expect(deriveNextTarget([], null)).toBeNull();
  });

  it('ARRAY order decides — never the Excel `row` cross-reference', () => {
    // Same pairs as the fixture, but the Excel `row` values are shuffled and
    // non-monotonic while the array order stays workbook order: the derivation
    // must follow the array (101 1 → 101 2), not row + 1 / row ordering.
    const shuffledExcelRows: SheetRow[] = [
      { row: 99, plot: 101, subsample: 1, values: [null] },
      { row: 12, plot: 101, subsample: 2, values: [null] },
      { row: 57, plot: 101, subsample: 3, values: [null] },
    ];
    expect(deriveNextTarget(shuffledExcelRows, { plot: 101, subsample: 1 })).toEqual({
      plot: 101,
      subsample: 2,
    });
    // If `SheetRow.row` decided, 99 + 1 would land nowhere / on a wrong pair.
    expect(
      deriveNextTarget(shuffledExcelRows, { plot: 101, subsample: 2 }),
    ).toEqual({ plot: 101, subsample: 3 });

    // Reverse the ARRAY (workbook order now ENDS at 101 1): the same pairs
    // derive different neighbours — the array alone decides.
    const reversed = [...shuffledExcelRows].reverse();
    expect(deriveNextTarget(reversed, { plot: 101, subsample: 2 })).toEqual({
      plot: 101,
      subsample: 1,
    });
    // 101 1 is now the LAST element (it was first) → nothing follows it …
    expect(deriveNextTarget(reversed, { plot: 101, subsample: 1 })).toBeNull();
    // … while 101 3 (now first) steps to 101 2, not to some `row` neighbour.
    expect(deriveNextTarget(reversed, { plot: 101, subsample: 3 })).toEqual({
      plot: 101,
      subsample: 2,
    });
  });

  it('uneven subsample counts still advance correctly (no per-plot constant)', () => {
    const uneven: SheetRow[] = [
      { row: 40, plot: 7, subsample: 1, values: [] },
      { row: 41, plot: 7, subsample: 2, values: [] },
      { row: 42, plot: 7, subsample: 3, values: [] }, // plot 7 ends at 3 …
      { row: 43, plot: 8, subsample: 1, values: [] }, // … so this is the step
      { row: 44, plot: 8, subsample: 2, values: [] },
      { row: 45, plot: 8, subsample: 3, values: [] },
      { row: 46, plot: 8, subsample: 4, values: [] },
      { row: 47, plot: 8, subsample: 5, values: [] }, // plot 8 runs to 5
      { row: 48, plot: 9, subsample: 1, values: [] },
    ];
    expect(deriveNextTarget(uneven, { plot: 7, subsample: 3 })).toEqual({
      plot: 8,
      subsample: 1,
    });
    expect(deriveNextTarget(uneven, { plot: 8, subsample: 4 })).toEqual({
      plot: 8,
      subsample: 5,
    });
    expect(deriveNextTarget(uneven, { plot: 8, subsample: 5 })).toEqual({
      plot: 9,
      subsample: 1,
    });
    expect(deriveNextTarget(uneven, { plot: 9, subsample: 1 })).toBeNull();
  });

  it('Rule 2: changing a row\'s values[] between null and 0 changes nothing', () => {
    const base: SheetRow[] = [
      { row: 22, plot: 101, subsample: 1, values: [null, 0, null] },
      { row: 23, plot: 101, subsample: 2, values: [null, null, null] },
      { row: 24, plot: 101, subsample: 3, values: [null, null, null] },
    ];
    const flipped: SheetRow[] = base.map((r) => ({
      ...r,
      values: r.values.map((v) => (v === null ? 0 : null)),
    }));
    const from = { plot: 101, subsample: 1 };
    expect(deriveNextTarget(base, from)).toEqual({ plot: 101, subsample: 2 });
    expect(deriveNextTarget(flipped, from)).toEqual(deriveNextTarget(base, from));
    // The value channel is never read — even an all-0 payload derives the same.
    expect(
      deriveNextTarget(
        base.map((r) => ({ ...r, values: [0, 0, 0] })),
        from,
      ),
    ).toEqual({ plot: 101, subsample: 2 });
  });
});

describe('rowsForTrial', () => {
  it('prefers the live payload atom when it holds rows for THIS trial', () => {
    const storage = memoryStorage();
    // A snapshot for the same trial with DIFFERENT rows must lose to the atom.
    writeSheetSnapshot(TRIAL, sheetWith([
      { row: 1, plot: 555, subsample: 1, values: [] },
    ]), 'network', storage);
    getDefaultStore().set(trialSheetAtom, sheetWith(rows));

    expect(rowsForTrial(TRIAL, storage)).toBe(rows);
  });

  it('falls back to the snapshot when the atom is empty or has null rows', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheetWith(rows), 'network', storage);

    // No atom at all → the snapshot is the only source.
    expect(rowsForTrial(TRIAL, storage)).toEqual(rows);

    // Atom present but with `rows: null` (fast-path placeholder) → snapshot.
    getDefaultStore().set(trialSheetAtom, sheetWith(null));
    expect(rowsForTrial(TRIAL, storage)).toEqual(rows);
  });

  it('ignores a payload atom belonging to ANOTHER trial', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheetWith(rows), 'network', storage);
    getDefaultStore().set(trialSheetAtom, sheetWith(
      [{ row: 1, plot: 555, subsample: 1, values: [] }],
      OTHER_TRIAL,
    ));

    const found = rowsForTrial(TRIAL, storage);
    expect(found).toEqual(rows);
    expect(found).not.toContainEqual({ row: 1, plot: 555, subsample: 1, values: [] });
  });

  it('returns null when neither atom nor snapshot has rows for the trial', () => {
    expect(rowsForTrial(TRIAL, memoryStorage())).toBeNull();
  });
});

describe('resolveNextTarget', () => {
  it('atom payload → derives next from the atom rows', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 4 }, storage);
    getDefaultStore().set(trialSheetAtom, sheetWith(rows));

    expect(resolveNextTarget(TRIAL, storage)).toEqual({ plot: 102, subsample: 1 });
  });

  it('snapshot → derives next from the persisted payload when no atom is loaded', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 103, subsample: 4 }, storage);
    writeSheetSnapshot(TRIAL, sheetWith(rows), 'network', storage);

    expect(resolveNextTarget(TRIAL, storage)).toEqual({ plot: 414, subsample: 1 });
  });

  it('neither atom nor snapshot → null (rows never invented)', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 4 }, storage);

    expect(resolveNextTarget(TRIAL, storage)).toBeNull();
  });

  it('a foreign-trial atom AND a foreign-trial snapshot are both ignored', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 4 }, storage);
    // Snapshot stored under THIS trial's key but with a foreign envelope —
    // readSheetSnapshot's cross-trial guard must reject it.
    storage.map.set(
      sheetSnapshotKey(TRIAL),
      JSON.stringify({
        trialId: OTHER_TRIAL,
        savedAt: '2026-10-06T09:45:13.402Z',
        source: 'network',
        sheet: sheetWith(rows),
      }),
    );
    getDefaultStore().set(trialSheetAtom, sheetWith(rows, OTHER_TRIAL));

    expect(resolveNextTarget(TRIAL, storage)).toBeNull();
  });

  it('nothing remembered (from === null) → null even with rows available', () => {
    const storage = memoryStorage();
    writeSheetSnapshot(TRIAL, sheetWith(rows), 'network', storage);

    expect(resolveNextTarget(TRIAL, storage)).toBeNull();
  });

  it('a last-written pair not present in the sheet → null (never claimed)', () => {
    const storage = memoryStorage();
    // The pre-fix store could hold the phantom 101 5.
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 5 }, storage);
    writeSheetSnapshot(TRIAL, sheetWith(rows), 'network', storage);

    expect(resolveNextTarget(TRIAL, storage)).toBeNull();
  });

  it('a throwing storage degrades to null — never throws', () => {
    getDefaultStore().set(trialSheetAtom, null);
    expect(() => resolveNextTarget(TRIAL, brokenStorage())).not.toThrow();
    expect(resolveNextTarget(TRIAL, brokenStorage())).toBeNull();
    // Same with the atom loaded but storage dead: the read of the stored
    // last-written pair fails → null, no crash.
    getDefaultStore().set(trialSheetAtom, sheetWith(rows));
    expect(resolveNextTarget(TRIAL, brokenStorage())).toBeNull();
  });

  it('is Node-safe: the default (non-DOM) storage path never throws', () => {
    expect(resolveNextTarget(TRIAL)).toBeNull();
    expect(resolveNextTarget('')).toBeNull();
  });
});


describe('deriveEndOfSheet', () => {
  it('returns lastWritten when it is the last row in rows', () => {
    const lastWritten = { plot: 414, subsample: 4 };
    expect(deriveEndOfSheet(rows, lastWritten)).toEqual(lastWritten);
  });

  it('returns null when lastWritten is not the last row', () => {
    expect(deriveEndOfSheet(rows, { plot: 101, subsample: 1 })).toBeNull();
    expect(deriveEndOfSheet(rows, { plot: 414, subsample: 3 })).toBeNull();
  });

  it('returns null when lastWritten is not present in rows', () => {
    expect(deriveEndOfSheet(rows, { plot: 101, subsample: 5 })).toBeNull();
    expect(deriveEndOfSheet(rows, { plot: 999, subsample: 1 })).toBeNull();
  });

  it('returns null when rows is null', () => {
    expect(deriveEndOfSheet(null, { plot: 101, subsample: 1 })).toBeNull();
  });

  it('returns null when lastWritten is null', () => {
    expect(deriveEndOfSheet(rows, null)).toBeNull();
  });
});
