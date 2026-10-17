import { beforeEach, describe, expect, it } from 'vitest';
import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom, trialSheetAtom } from '../store/atoms';
import type { SheetColumn, SheetRow, TrialSheet } from '../types';
import { readDefaultTarget, writeDefaultTarget } from './defaultTarget';
import { deriveNextTarget } from './nextTarget';
import { writeSheetSnapshot } from './sheetSnapshot';
import {
  advanceSheetTarget,
  deriveBlockFull,
  rehydrateSheetTarget,
  targetHighlight,
  targetSelection,
} from './sheetTarget';

const TRIAL = 'trial-1';
const OTHER_TRIAL = 'trial-2';

/**
 * The 9-column, part-major `columns` the API sends (cols C..K, ordinals 1..9),
 * grouped into three 3-wide category blocks: LEAF1 [1..3], LEAF2 [4..6],
 * LEAF3 [7..9]. `blockFull` is judged against THESE runs.
 */
const columns: SheetColumn[] = [
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

/**
 * Excel `Row` values are shuffled (57 / 12 / 99 — neither the array index nor
 * `index + 22`, and not in plot order) so a `Row`-based match or a positional
 * assumption lands on the wrong row. `values[1] === 0` in the 101/1 row pins
 * Rule 2 (empty ≠ 0 — and the highlight ignores values entirely).
 */
const rows: SheetRow[] = [
  { row: 57, plot: 101, subsample: 2, values: [1, null, 0] },
  { row: 12, plot: 101, subsample: 1, values: [null, 0, 5] },
  { row: 99, plot: 102, subsample: 1, values: [4, 6, null] },
];

type StorageStub = Pick<Storage, 'getItem' | 'setItem'>;

/** Minimal Map-backed storage stub — mirrors `defaultTarget.test.ts`. */
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

/** A payload carrying `columns` (block-full needs a trial to trust). */
function payloadFor(
  trialId: string,
  payloadColumns: SheetColumn[] | null = columns,
): TrialSheet {
  return {
    trialId,
    version: 'v1',
    changed: true,
    fileName: '4 subsamples.xlsx',
    generatedAt: '2026-10-06T09:45:13.402Z',
    metadata: null,
    columns: payloadColumns,
    rows,
  };
}

beforeEach(() => {
  // Both atoms are shared through getDefaultStore() across the suite.
  getDefaultStore().set(sheetLastWrittenAtom, null);
  getDefaultStore().set(trialSheetAtom, null);
});

describe('targetHighlight (row-only)', () => {
  it('(a) resolves the ROW for a plot + subsample target', () => {
    expect(targetHighlight({ plot: 101, subsample: 1 }, rows)).toEqual({ row: 12 });
    expect(targetHighlight({ plot: 101, subsample: 2 }, rows)).toEqual({ row: 57 });
    expect(targetHighlight({ plot: 102, subsample: 1 }, rows)).toEqual({ row: 99 });
  });

  it('(b) matches by plot + subsample, never by the Excel Row cross-ref or index', () => {
    const hit = targetHighlight({ plot: 101, subsample: 1 }, rows);
    // The 101/1 row is at array index 1 (Excel Row 12); a plot-only match
    // (rows[0], Row 57) or the index (1) must not decide the row.
    expect(hit?.row).toBe(12);
    expect(hit?.row).not.toBe(rows[0].row); // not the plot-only first match
    expect(hit?.row).not.toBe(0); // not the array index
  });

  it('(c) a target row missing from the payload returns null — never a wrap', () => {
    expect(targetHighlight({ plot: 101, subsample: 9 }, rows)).toBeNull();
    expect(targetHighlight({ plot: 999, subsample: 1 }, rows)).toBeNull();
  });

  it('(d) a null target highlights nothing', () => {
    expect(targetHighlight(null, rows)).toBeNull();
  });

  it('(e) row-only shape: the highlight carries `row` and NOTHING else', () => {
    const hit = targetHighlight({ plot: 101, subsample: 1 }, rows);
    expect(hit).not.toBeNull();
    expect(Object.keys(hit as object)).toEqual(['row']);
  });

  it('(f) Rule 2: an all-null / all-zero values[] derives the SAME row', () => {
    const base: SheetRow[] = [
      { row: 22, plot: 101, subsample: 1, values: [null, 0, null] },
    ];
    const zeros: SheetRow[] = [
      { row: 22, plot: 101, subsample: 1, values: [0, 0, 0] },
    ];
    expect(targetHighlight({ plot: 101, subsample: 1 }, base)).toEqual({ row: 22 });
    expect(targetHighlight({ plot: 101, subsample: 1 }, zeros)).toEqual({ row: 22 });
  });
});

describe('targetSelection (tapped anchor cell)', () => {
  it('(a) resolves the ROW for plot + subsample and carries the tapped valuesIndex', () => {
    expect(targetSelection({ plot: 101, subsample: 1, valuesIndex: 2 }, rows)).toEqual({
      row: 12,
      valuesIndex: 2,
    });
    expect(targetSelection({ plot: 102, subsample: 1, valuesIndex: 0 }, rows)).toEqual({
      row: 99,
      valuesIndex: 0,
    });
  });

  it('(b) matches by plot + subsample, never by the Excel Row cross-ref or index', () => {
    const hit = targetSelection({ plot: 101, subsample: 1, valuesIndex: 1 }, rows);
    // The 101/1 row is at array index 1 (Excel Row 12); a plot-only match
    // (rows[0], Row 57) or the index (1) must not decide the row.
    expect(hit?.row).toBe(12);
    expect(hit?.row).not.toBe(rows[0].row);
    expect(hit?.row).not.toBe(0);
  });

  it('(c) a null anchor highlights nothing', () => {
    expect(targetSelection(null, rows)).toBeNull();
  });

  it('(d) an anchor row missing from the payload returns null — never a wrap', () => {
    expect(targetSelection({ plot: 101, subsample: 9, valuesIndex: 2 }, rows)).toBeNull();
    expect(targetSelection({ plot: 999, subsample: 1, valuesIndex: 0 }, rows)).toBeNull();
  });

  it('(e) shape: exactly `row` + `valuesIndex`, nothing else', () => {
    const hit = targetSelection({ plot: 101, subsample: 2, valuesIndex: 2 }, rows);
    expect(hit).not.toBeNull();
    expect(Object.keys(hit as object)).toEqual(['row', 'valuesIndex']);
  });
});

describe('deriveBlockFull (the STAY/advance switch)', () => {
  it('is true when the highest written ordinal reaches its block END', () => {
    expect(deriveBlockFull([{ traitOrdinal: 1 }, { traitOrdinal: 2 }, { traitOrdinal: 3 }], columns)).toBe(true);
    expect(deriveBlockFull([{ traitOrdinal: 4 }, { traitOrdinal: 5 }, { traitOrdinal: 6 }], columns)).toBe(true);
    expect(deriveBlockFull([{ traitOrdinal: 7 }, { traitOrdinal: 8 }, { traitOrdinal: 9 }], columns)).toBe(true);
    // The LAST ordinal of a block alone is enough (max reaches the end).
    expect(deriveBlockFull([{ traitOrdinal: 3 }], columns)).toBe(true);
  });

  it('is false while the block still has empty cells after the write', () => {
    expect(deriveBlockFull([{ traitOrdinal: 1 }, { traitOrdinal: 2 }], columns)).toBe(false);
    expect(deriveBlockFull([{ traitOrdinal: 4 }, { traitOrdinal: 5 }], columns)).toBe(false);
    expect(deriveBlockFull([{ traitOrdinal: 1 }], columns)).toBe(false);
  });

  it('judges by the MAX ordinal — a partial next block is NOT full', () => {
    // LEAF1 (1..3) is full, but the highest write is 4 (mid-LEAF2) → not full.
    expect(
      deriveBlockFull(
        [{ traitOrdinal: 1 }, { traitOrdinal: 2 }, { traitOrdinal: 3 }, { traitOrdinal: 4 }],
        columns,
      ),
    ).toBe(false);
  });

  it('is false for no cells, no columns, or an ordinal outside every block', () => {
    expect(deriveBlockFull([], columns)).toBe(false);
    expect(deriveBlockFull([{ traitOrdinal: 1 }], [])).toBe(false);
    // 10 is past the last block (end 9) — no block claims it → conservative false.
    expect(deriveBlockFull([{ traitOrdinal: 10 }], columns)).toBe(false);
    // Non-positive / non-integer ordinals are filtered out entirely.
    expect(deriveBlockFull([{ traitOrdinal: 0 }], columns)).toBe(false);
    expect(deriveBlockFull([{ traitOrdinal: 2.5 }], columns)).toBe(false);
  });

  it('never hardcodes a 3-wide block: a 2-wide part is full at its own end', () => {
    const narrow: SheetColumn[] = [
      { traitOrdinal: 1, column: 'C', pestCode: 'A', partRated: 'LEAF1', label: 'A × LEAF1' },
      { traitOrdinal: 2, column: 'D', pestCode: 'B', partRated: 'LEAF1', label: 'B × LEAF1' },
      { traitOrdinal: 3, column: 'E', pestCode: 'C', partRated: 'LEAF2', label: 'C × LEAF2' },
    ];
    expect(deriveBlockFull([{ traitOrdinal: 1 }, { traitOrdinal: 2 }], narrow)).toBe(true);
    expect(deriveBlockFull([{ traitOrdinal: 1 }], narrow)).toBe(false);
  });
});

describe('advanceSheetTarget / rehydrateSheetTarget', () => {
  it('(i) writes the pair + blockFull through storage and the atom (no + 1)', () => {
    const storage = memoryStorage();
    getDefaultStore().set(trialSheetAtom, payloadFor(TRIAL));

    advanceSheetTarget(
      TRIAL,
      {
        plot: 101,
        subsample: 3,
        writtenCells: [{ traitOrdinal: 1 }, { traitOrdinal: 2 }, { traitOrdinal: 3 }],
      },
      storage,
    );

    // Persistence holds the written pair verbatim — never subsample + 1: the
    // NEXT pair is derived from the sheet payload (deriveNextTarget).
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
    // The atom mirrors that same memory (pair + flag), tagged with the trial.
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
  });

  it('computes blockFull=false when the block is only partly written', () => {
    const storage = memoryStorage();
    getDefaultStore().set(trialSheetAtom, payloadFor(TRIAL));

    advanceSheetTarget(
      TRIAL,
      {
        plot: 101,
        subsample: 2,
        writtenCells: [{ traitOrdinal: 1 }, { traitOrdinal: 2 }],
      },
      storage,
    );

    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 2,
      blockFull: false,
    });
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 2,
      blockFull: false,
    });
  });

  it('degrades to blockFull=false when no columns are trustworthy (conservative STAY)', () => {
    const storage = memoryStorage();
    // No payload atom at all → columnsForTrial returns null.
    advanceSheetTarget(
      TRIAL,
      { plot: 101, subsample: 3, writtenCells: [{ traitOrdinal: 3 }] },
      storage,
    );
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });

    // A payload belonging to ANOTHER trial is never trusted for this one.
    getDefaultStore().set(trialSheetAtom, payloadFor(OTHER_TRIAL));
    advanceSheetTarget(
      TRIAL,
      { plot: 101, subsample: 3, writtenCells: [{ traitOrdinal: 3 }] },
      memoryStorage(),
    );
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
  });

  it('no writtenCells → blockFull=false (nothing filled anything)', () => {
    const storage = memoryStorage();
    getDefaultStore().set(trialSheetAtom, payloadFor(TRIAL));
    advanceSheetTarget(TRIAL, { plot: 101, subsample: 3 }, storage);
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
  });

  it('falls back to the persisted snapshot columns when the live atom is empty (W2)', () => {
    const storage = memoryStorage();
    // No live payload atom (sheet not loaded yet), but a snapshot IS persisted
    // for this trial. Without the fallback the block would be judged unfilled
    // (STAY) and the run would lag one subsample; the snapshot fixes that.
    writeSheetSnapshot(TRIAL, payloadFor(TRIAL), 'network', storage);
    // Precondition: the live atom really is empty (beforeEach reset it).
    expect(getDefaultStore().get(trialSheetAtom)).toBeNull();

    advanceSheetTarget(
      TRIAL,
      {
        plot: 101,
        subsample: 3,
        writtenCells: [
          { traitOrdinal: 1 },
          { traitOrdinal: 2 },
          { traitOrdinal: 3 },
        ],
      },
      storage,
    );

    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
  });

  it('ignores a foreign-trial live atom and trusts the snapshot for THIS trial (W2)', () => {
    const storage = memoryStorage();
    // A leftover payload for ANOTHER trial must never decide this trial's
    // block size — the snapshot is the right source here.
    getDefaultStore().set(trialSheetAtom, payloadFor(OTHER_TRIAL));
    writeSheetSnapshot(TRIAL, payloadFor(TRIAL), 'network', storage);

    advanceSheetTarget(
      TRIAL,
      { plot: 101, subsample: 3, writtenCells: [{ traitOrdinal: 3 }] },
      storage,
    );

    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: true,
    });
  });

  it('still STAYs when neither the atom nor a snapshot has columns (W2 boundary)', () => {
    const storage = memoryStorage();
    advanceSheetTarget(
      TRIAL,
      { plot: 101, subsample: 3, writtenCells: [{ traitOrdinal: 3 }] },
      storage,
    );
    expect(readDefaultTarget(TRIAL, storage)).toEqual({
      plot: 101,
      subsample: 3,
      blockFull: false,
    });
  });

  it('mirrors rememberWrittenTarget no-op: an incomplete result moves neither store nor atom', () => {
    const storage = memoryStorage();
    getDefaultStore().set(trialSheetAtom, payloadFor(TRIAL));

    advanceSheetTarget(
      TRIAL,
      { plot: null, subsample: 3, writtenCells: [{ traitOrdinal: 1 }] },
      storage,
    );

    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });

  it('rehydrates this trial (pair + blockFull) from storage', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 2, blockFull: true }, storage);

    rehydrateSheetTarget(TRIAL, storage);

    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 2,
      blockFull: true,
    });
    // The atom is the LAST-WRITTEN pair (identity-highlightable), not a next.
    expect(
      targetHighlight(getDefaultStore().get(sheetLastWrittenAtom), rows),
    ).toEqual({ row: 57 });
  });

  it('rehydrating a trial with nothing stored (or another trial) clears the atom', () => {
    const storage = memoryStorage();
    advanceSheetTarget(
      TRIAL,
      { plot: 101, subsample: 3, writtenCells: [{ traitOrdinal: 3 }] },
      storage,
    );
    expect(getDefaultStore().get(sheetLastWrittenAtom)).not.toBeNull();

    rehydrateSheetTarget(OTHER_TRIAL, storage);
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });

  it('rehydrating a LEGACY pair (no blockFull in storage) clears the atom', () => {
    const storage = memoryStorage();
    storage.map.set(
      `plottR-default-target:${TRIAL}`,
      JSON.stringify({ plot: 101, subsample: 2 }),
    );
    rehydrateSheetTarget(TRIAL, storage);
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });
});

describe('deriveNextTarget feeding targetHighlight (the host wiring)', () => {
  // rows[] (array order): [101/2 → Excel Row 57, 101/1 → Row 12, 102/1 → Row 99].

  it('a full block advances 101/2 → 101/1 → the tint lands on 101/1\'s row', () => {
    const next = deriveNextTarget(rows, { plot: 101, subsample: 2 }, true);
    expect(next).toEqual({ plot: 101, subsample: 1 });
    expect(targetHighlight(next, rows)).toEqual({ row: 12 });
  });

  it('an unfilled block STAYS on 101/2 → the tint lands on 101/2\'s row', () => {
    const next = deriveNextTarget(rows, { plot: 101, subsample: 2 }, false);
    expect(next).toEqual({ plot: 101, subsample: 2 });
    expect(targetHighlight(next, rows)).toEqual({ row: 57 });
  });

  it('a full block at a plot edge ROLLS OVER to the next plot → highlight follows', () => {
    // 101/1 is the last 101 row in array order (index 1); the next row is
    // 102/1 — rollover reverses the old plot-boundary stop, so it is targeted.
    const next = deriveNextTarget(rows, { plot: 101, subsample: 1 }, true);
    expect(next).toEqual({ plot: 102, subsample: 1 });
    expect(targetHighlight(next, rows)).toEqual({ row: 99 });
  });

  it('a stale/legacy last-written pair not present in rows derives null (nothing claimed)', () => {
    // The pre-fix store could hold 101/5 — a pair the workbook never had.
    expect(deriveNextTarget(rows, { plot: 101, subsample: 5 }, true)).toBeNull();
    expect(deriveNextTarget(rows, { plot: 999, subsample: 1 }, true)).toBeNull();
    expect(targetHighlight(null, rows)).toBeNull();
  });
});
