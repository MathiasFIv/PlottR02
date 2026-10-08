import { beforeEach, describe, expect, it } from 'vitest';
import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom } from '../store/atoms';
import type { SheetColumn, SheetRow } from '../types';
import { readDefaultTarget, writeDefaultTarget } from './defaultTarget';
import { deriveNextTarget } from './nextTarget';
import { sheetCellValue } from './sheetCell';
import {
  advanceSheetTarget,
  rehydrateSheetTarget,
  targetHighlight,
} from './sheetTarget';

const TRIAL = 'trial-1';
const OTHER_TRIAL = 'trial-2';

/**
 * Columns are DELIBERATELY shuffled so `traitOrdinal !== index + 1`:
 * the first column carries ordinal 3, ordinal 1 sits at index 1, ordinal 7 at
 * index 2. Any `ordinal - 1` / Excel-letter shortcut therefore lands on the
 * WRONG cell and fails loudly below.
 */
const columns: SheetColumn[] = [
  { traitOrdinal: 3, column: 'C', pestCode: 'PUCCST', partRated: 'LEAF1', label: 'PUCCST × LEAF1' },
  { traitOrdinal: 1, column: 'D', pestCode: 'SEPTTR', partRated: 'LEAF1', label: 'SEPTTR × LEAF1' },
  { traitOrdinal: 7, column: 'E', pestCode: 'ERYSGR', partRated: 'LEAF1', label: 'ERYSGR × LEAF1' },
];

/**
 * Excel `Row` values are shuffled too (57 / 12 / 99 — neither the array index
 * nor `index + 22`, and not in plot order) so a `Row`-based match or a
 * positional assumption lands on the wrong row. `values[1] === 0` in the
 * plot 101 / sub 1 row pins Rule 2 (empty ≠ 0).
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

beforeEach(() => {
  // The atom is shared through getDefaultStore() across the suite.
  getDefaultStore().set(sheetLastWrittenAtom, null);
});

describe('targetHighlight', () => {
  it('(a) resolves the exact cell for a plot + subsample + trait target', () => {
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 1 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: 1 });
  });

  it('(b) matches the row by plot + subsample, never by the Excel Row cross-ref', () => {
    // The plot 101 rows are at array index 1 (Excel Row 12) and 0 (Row 57) —
    // Row is the cross-ref only: matching on it (or on plot alone, which would
    // return the FIRST 101 row) must not decide the row.
    const hit = targetHighlight({ plot: 101, subsample: 1 }, columns, rows);
    expect(hit?.row).toBe(12);
    expect(hit?.row).not.toBe(rows[0].row); // not the plot-only first match
    expect(hit?.row).not.toBe(rows.findIndex((r) => r.plot === 101)); // not the index
    // Subsample 2 resolves to the OTHER 101 row (Excel Row 57, array index 0).
    expect(targetHighlight({ plot: 101, subsample: 2 }, columns, rows)?.row).toBe(57);
  });

  it('(c) a target row missing from the payload returns null — never a wrap', () => {
    expect(targetHighlight({ plot: 101, subsample: 9 }, columns, rows)).toBeNull();
    expect(targetHighlight({ plot: 999, subsample: 1 }, columns, rows)).toBeNull();
  });

  it('(d) no traitOrdinal falls back to a whole-row tint', () => {
    expect(targetHighlight({ plot: 102, subsample: 1 }, columns, rows)).toEqual({
      row: 99,
      valuesIndex: null,
    });
  });

  it('(e) an out-of-range traitOrdinal degrades to a row tint without throwing', () => {
    expect(() =>
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 9 }, columns, rows),
    ).not.toThrow();
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 9 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: null });
    // Ordinal 2 is in NO column of this grid — ordinal − 1 would wrongly land
    // on index 0, so this also pins "identity match, never ordinal − 1".
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 2 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: null });
  });

  it('(f) shuffled columns: valuesIndex is the ARRAY index of the traitOrdinal', () => {
    // First column carries ordinal 3 → index 0 (ordinal − 1 would give 2).
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 3 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: 0 });
    // Ordinal 1 → index 1 (ordinal − 1 would give 0 — the wrong cell).
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 1 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: 1 });
    expect(
      targetHighlight({ plot: 101, subsample: 1, traitOrdinal: 7 }, columns, rows),
    ).toEqual({ row: 12, valuesIndex: 2 });
  });

  it('(g) Rule 2: a highlighted cell holding 0 still highlights AND renders "0"', () => {
    const highlight = targetHighlight(
      { plot: 101, subsample: 1, traitOrdinal: 1 },
      columns,
      rows,
    );
    expect(highlight).toEqual({ row: 12, valuesIndex: 1 });

    // The highlighted cell really is a rated 0 (not an empty cell) …
    const value = rows[1].values[1];
    expect(value).toBe(0);
    // … so the grid renders '0' — never blank — while empty still renders ''.
    expect(sheetCellValue(value)).toBe('0');
    expect(sheetCellValue(null)).toBe('');
  });

  it('(h) a null target highlights nothing', () => {
    expect(targetHighlight(null, columns, rows)).toBeNull();
  });
});

describe('advanceSheetTarget / rehydrateSheetTarget', () => {
  it('(i) writes through storage + the atom AS WRITTEN (no + 1), and rehydrating ANOTHER trial clears it', () => {
    const storage = memoryStorage();

    advanceSheetTarget(TRIAL, { plot: 101, subsample: 3, traitOrdinal: 5 }, storage);

    // Persistence holds the written pair verbatim — never subsample + 1: the
    // NEXT pair is derived from the sheet payload (deriveNextTarget), which
    // is the only thing that knows whether a following row exists.
    expect(readDefaultTarget(TRIAL, storage)).toEqual({ plot: 101, subsample: 3 });
    // The atom mirrors that same written pair, tagged with the trial + column.
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 3,
      traitOrdinal: 5,
    });

    // A DIFFERENT trial has nothing remembered → atom null, no foreign cell.
    rehydrateSheetTarget(OTHER_TRIAL, storage);
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });

  it('rehydrates this trial as a row tint — traitOrdinal is not persisted', () => {
    const storage = memoryStorage();
    writeDefaultTarget(TRIAL, { plot: 101, subsample: 2 }, storage);

    rehydrateSheetTarget(TRIAL, storage);

    expect(getDefaultStore().get(sheetLastWrittenAtom)).toEqual({
      trialId: TRIAL,
      plot: 101,
      subsample: 2,
    });
    // The atom itself is the LAST-WRITTEN pair (identity-highlightable) …
    expect(
      targetHighlight(getDefaultStore().get(sheetLastWrittenAtom), columns, rows),
    ).toEqual({ row: 57, valuesIndex: null });
  });

  it('rehydrating a trial with nothing stored clears the atom', () => {
    const storage = memoryStorage();
    advanceSheetTarget(TRIAL, { plot: 101, subsample: 3 }, storage);
    expect(getDefaultStore().get(sheetLastWrittenAtom)).not.toBeNull();

    rehydrateSheetTarget(OTHER_TRIAL, storage);
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });

  it('mirrors rememberWrittenTarget no-op: an incomplete result moves neither store nor atom', () => {
    const storage = memoryStorage();

    advanceSheetTarget(TRIAL, { plot: null, subsample: 3, traitOrdinal: 1 }, storage);

    expect(readDefaultTarget(TRIAL, storage)).toBeNull();
    expect(getDefaultStore().get(sheetLastWrittenAtom)).toBeNull();
  });
});

describe('deriveNextTarget feeding targetHighlight (the host wiring)', () => {
  it('last-written 101/1 → next is 102/1 → the tint lands on 102/1\'s row', () => {
    // rows[] (array order): [101/2 → Excel Row 57, 101/1 → Row 12, 102/1 → Row 99].
    const lastWritten = { trialId: TRIAL, plot: 101, subsample: 1 };
    const next = deriveNextTarget(rows, lastWritten);
    expect(next).toEqual({ plot: 102, subsample: 1 });
    expect(targetHighlight(next, columns, rows)).toEqual({
      row: 99,
      valuesIndex: null,
    });
  });

  it('the last row of the payload derives null → no highlight at all', () => {
    const lastRow = { trialId: TRIAL, plot: 102, subsample: 1 };
    const next = deriveNextTarget(rows, lastRow);
    expect(next).toBeNull();
    expect(targetHighlight(next, columns, rows)).toBeNull();
  });

  it('ARRAY order decides: 101/2 derives 101/1 (Excel Row 57 → Row 12)', () => {
    // The Excel `row` values are non-monotonic (57 sits BEFORE 12 in the
    // array) and the plot numbers run backwards here — the step forward is
    // the array index, never `row + 1`, never subsample arithmetic.
    const next = deriveNextTarget(
      rows,
      { plot: 101, subsample: 2 },
    );
    expect(next).toEqual({ plot: 101, subsample: 1 });
    expect(targetHighlight(next, columns, rows)).toEqual({
      row: 12,
      valuesIndex: null,
    });
  });

  it('a stale/legacy last-written pair not present in rows derives null (nothing claimed)', () => {
    // The pre-fix store could hold 101/5 — a pair the workbook never had.
    expect(deriveNextTarget(rows, { plot: 101, subsample: 5 })).toBeNull();
    expect(deriveNextTarget(rows, { plot: 999, subsample: 1 })).toBeNull();
    expect(targetHighlight(null, columns, rows)).toBeNull();
  });
});
