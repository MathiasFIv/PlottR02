import { describe, expect, it } from 'vitest';
import type { SheetRow } from '../types';
import type { SheetCellTap } from './sheetAnchor';
import type { CellEditResult } from './api';
import {
  applyCellEdit,
  classifyCellEditResult,
  editCellSelection,
  parseCellDraft,
  sameSheetCellEdit,
} from './sheetCellEdit';
import type { SheetCellEdit } from './sheetCellEdit';

const rows: SheetRow[] = [
  { row: 22, plot: 101, subsample: 1, values: [3, null, 0] },
  { row: 23, plot: 101, subsample: 2, values: [null, 5, null] },
  { row: 24, plot: 102, subsample: 1, values: [7, 8, 9] },
];

describe('parseCellDraft', () => {
  it('accepts whole numbers in range, including 0 and both bounds', () => {
    expect(parseCellDraft('0')).toBe(0);
    expect(parseCellDraft('5')).toBe(5);
    expect(parseCellDraft('10')).toBe(10);
    expect(parseCellDraft('007')).toBe(7);
  });

  it('trims surrounding whitespace', () => {
    expect(parseCellDraft('  4 ')).toBe(4);
  });

  it('rejects empty/blank drafts (missing value is never defaulted to 0)', () => {
    expect(parseCellDraft('')).toBeNull();
    expect(parseCellDraft('   ')).toBeNull();
  });

  it('rejects out-of-range values', () => {
    expect(parseCellDraft('11')).toBeNull();
    expect(parseCellDraft('-1')).toBeNull();
  });

  it('rejects non-whole and non-numeric drafts', () => {
    expect(parseCellDraft('1.5')).toBeNull();
    expect(parseCellDraft('x')).toBeNull();
    expect(parseCellDraft('4x')).toBeNull();
    expect(parseCellDraft('+4')).toBeNull();
    expect(parseCellDraft('1e2')).toBeNull();
  });
});

describe('editCellSelection', () => {
  it('maps a tapped cell to its row cross-ref + column', () => {
    const tap: SheetCellTap = { plot: 101, subsample: 2, valuesIndex: 1 };
    expect(editCellSelection(tap, rows)).toEqual({ row: 23, valuesIndex: 1 });
  });

  it('returns null for no edit', () => {
    expect(editCellSelection(null, rows)).toBeNull();
  });

  it('returns null when the row is not in this payload (identity, not Row)', () => {
    expect(
      editCellSelection({ plot: 999, subsample: 1, valuesIndex: 0 }, rows),
    ).toBeNull();
  });
});

describe('applyCellEdit', () => {
  it('sets the addressed cell and returns a NEW rows array', () => {
    const next = applyCellEdit(
      rows,
      { plot: 101, subsample: 2, valuesIndex: 2 },
      4,
    );
    expect(next).not.toBe(rows);
    expect(next[1].values).toEqual([null, 5, 4]);
    // Untouched rows/arrays keep their identity (cheap re-render).
    expect(next[0]).toBe(rows[0]);
    expect(next[2]).toBe(rows[2]);
  });

  it('does not mutate the original row or its values array', () => {
    applyCellEdit(rows, { plot: 101, subsample: 1, valuesIndex: 0 }, 9);
    expect(rows[0].values).toEqual([3, null, 0]);
  });

  it('returns the SAME reference when the value is unchanged (no-op)', () => {
    const next = applyCellEdit(
      rows,
      { plot: 101, subsample: 1, valuesIndex: 0 },
      3,
    );
    expect(next).toBe(rows);
  });

  it('returns the SAME reference when the row is not present', () => {
    const next = applyCellEdit(
      rows,
      { plot: 999, subsample: 1, valuesIndex: 0 },
      3,
    );
    expect(next).toBe(rows);
  });

  it('can overwrite a distinct existing value', () => {
    const next = applyCellEdit(
      rows,
      { plot: 102, subsample: 1, valuesIndex: 0 },
      1,
    );
    expect(next[2].values).toEqual([1, 8, 9]);
  });
});

describe('sameSheetCellEdit', () => {
  const base: SheetCellEdit = {
    trialId: 'trial-1',
    plot: 101,
    subsample: 2,
    valuesIndex: 1,
    value: '5',
  };

  it('is true for identical descriptors', () => {
    expect(sameSheetCellEdit(base, { ...base })).toBe(true);
  });

  it('is false when the draft changed', () => {
    expect(sameSheetCellEdit(base, { ...base, value: '6' })).toBe(false);
  });

  it('is false when the cell identity changed', () => {
    expect(sameSheetCellEdit(base, { ...base, valuesIndex: 2 })).toBe(false);
    expect(sameSheetCellEdit(base, { ...base, subsample: 1 })).toBe(false);
    expect(sameSheetCellEdit(base, { ...base, plot: 102 })).toBe(false);
    expect(sameSheetCellEdit(base, { ...base, trialId: 'trial-2' })).toBe(false);
  });

  it('handles nulls', () => {
    expect(sameSheetCellEdit(null, null)).toBe(true);
    expect(sameSheetCellEdit(base, null)).toBe(false);
    expect(sameSheetCellEdit(null, base)).toBe(false);
  });
});

describe('classifyCellEditResult', () => {
  const result = (overrides: Partial<CellEditResult>): CellEditResult => ({
    trialId: 'trial-1',
    plot: 101,
    subsample: 2,
    traitOrdinal: 2,
    value: 5,
    written: false,
    skipped: true,
    skippedReason: null,
    priorValue: null,
    cellAddress: 'E23',
    version: 'ver-2',
    fileName: 'trial.xlsx',
    generatedAt: '2026-10-17T12:00:00Z',
    ...overrides,
  });

  it('reads a landed write as `written`', () => {
    expect(
      classifyCellEditResult(
        result({ written: true, skipped: false, skippedReason: null }),
      ),
    ).toEqual({ kind: 'written' });
  });

  it('reads `cell_already_filled` as a conflict carrying the prior value', () => {
    expect(
      classifyCellEditResult(
        result({ skippedReason: 'cell_already_filled', priorValue: 7 }),
      ),
    ).toEqual({ kind: 'conflict', priorValue: 7 });
  });

  it('carries a null prior value through (filled-then-cleared edge)', () => {
    expect(
      classifyCellEditResult(
        result({ skippedReason: 'cell_already_filled', priorValue: null }),
      ),
    ).toEqual({ kind: 'conflict', priorValue: null });
  });

  it('reads a 200 `row_not_found` as its own outcome, not a conflict', () => {
    expect(
      classifyCellEditResult(result({ skippedReason: 'row_not_found' })),
    ).toEqual({ kind: 'row_not_found' });
  });

  it('treats any other non-write as `unexpected`', () => {
    expect(classifyCellEditResult(result({ skippedReason: null }))).toEqual({
      kind: 'unexpected',
    });
  });

  it('prefers `written` over a stale skippedReason', () => {
    expect(
      classifyCellEditResult(
        result({ written: true, skippedReason: 'cell_already_filled' }),
      ),
    ).toEqual({ kind: 'written' });
  });
});
