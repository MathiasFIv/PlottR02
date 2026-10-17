import { describe, expect, it } from 'vitest';
import type { SheetRow } from '../types';
import type { SheetCellTap } from './sheetAnchor';
import type { CellEditResult, VoiceCellEditResult } from './api';
import {
  applyCellEdit,
  classifyCellEditResult,
  classifyVoiceCellEditResult,
  editCellSelection,
  parseCellDraft,
  resolveArmMode,
  sameSheetCellEdit,
  shouldFallbackToKeyboard,
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
  const keyboard: SheetCellEdit = {
    trialId: 'trial-1',
    plot: 101,
    subsample: 2,
    valuesIndex: 1,
    mode: 'keyboard',
    value: '5',
  };
  const voice: SheetCellEdit = {
    trialId: 'trial-1',
    plot: 101,
    subsample: 2,
    valuesIndex: 1,
    mode: 'voice',
  };

  it('is true for identical keyboard descriptors', () => {
    expect(sameSheetCellEdit(keyboard, { ...keyboard })).toBe(true);
  });

  it('is true for identical voice arms (no draft to compare)', () => {
    expect(sameSheetCellEdit(voice, { ...voice })).toBe(true);
  });

  it('is false when the keyboard draft changed', () => {
    expect(sameSheetCellEdit(keyboard, { ...keyboard, value: '6' })).toBe(false);
  });

  it('is false when the capture mode differs on the same cell', () => {
    // A voice arm flipped to keyboard (or vice-versa) is a DIFFERENT edit.
    expect(sameSheetCellEdit(voice, keyboard)).toBe(false);
    expect(sameSheetCellEdit(keyboard, voice)).toBe(false);
  });

  it('is false when the cell identity changed', () => {
    expect(sameSheetCellEdit(keyboard, { ...keyboard, valuesIndex: 2 })).toBe(
      false,
    );
    expect(sameSheetCellEdit(keyboard, { ...keyboard, subsample: 1 })).toBe(
      false,
    );
    expect(sameSheetCellEdit(keyboard, { ...keyboard, plot: 102 })).toBe(false);
    expect(sameSheetCellEdit(keyboard, { ...keyboard, trialId: 'trial-2' })).toBe(
      false,
    );
  });

  it('handles nulls', () => {
    expect(sameSheetCellEdit(null, null)).toBe(true);
    expect(sameSheetCellEdit(keyboard, null)).toBe(false);
    expect(sameSheetCellEdit(null, keyboard)).toBe(false);
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

  it('reads a 200 `row_not_found` as its own outcome', () => {
    expect(
      classifyCellEditResult(result({ skippedReason: 'row_not_found' })),
    ).toEqual({ kind: 'row_not_found' });
  });

  it('reads `cell_already_filled` as `unexpected` (no conflict path any more)', () => {
    // Regression: the keyboard commit always overwrites, so a filled-cell skip
    // is a contract surprise, NOT an offer to overwrite.
    expect(
      classifyCellEditResult(
        result({ skippedReason: 'cell_already_filled', priorValue: 7 }),
      ),
    ).toEqual({ kind: 'unexpected' });
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

describe('classifyVoiceCellEditResult', () => {
  const result = (
    overrides: Partial<VoiceCellEditResult>,
  ): VoiceCellEditResult => ({
    trialId: 'trial-1',
    plot: 101,
    subsample: 2,
    traitOrdinal: 2,
    value: null,
    status: 'unexpected',
    written: false,
    skipped: true,
    skippedReason: null,
    priorValue: null,
    cellAddress: 'E23',
    version: 'ver-2',
    fileName: 'trial.xlsx',
    transcript: '',
    generatedAt: '2026-10-17T12:00:00Z',
    ...overrides,
  });

  it('reads `written` with its value and prior value', () => {
    expect(
      classifyVoiceCellEditResult(
        result({ status: 'written', written: true, value: 5, priorValue: 3 }),
      ),
    ).toEqual({ kind: 'written', value: 5, priorValue: 3 });
  });

  it('carries a null prior value through (empty-cell edge)', () => {
    expect(
      classifyVoiceCellEditResult(
        result({ status: 'written', written: true, value: 0, priorValue: null }),
      ),
    ).toEqual({ kind: 'written', value: 0, priorValue: null });
  });

  it('falls back to 0 when a `written` body omits the value', () => {
    // Unreachable server-side (a landed write always carries the value), but
    // pins the non-null contract without a cast.
    expect(
      classifyVoiceCellEditResult(
        result({ status: 'written', written: true, value: null }),
      ),
    ).toEqual({ kind: 'written', value: 0, priorValue: null });
  });

  it('reads `not_understood`', () => {
    expect(
      classifyVoiceCellEditResult(result({ status: 'not_understood' })),
    ).toEqual({ kind: 'not_understood' });
  });

  it('reads `row_not_found`', () => {
    expect(
      classifyVoiceCellEditResult(result({ status: 'row_not_found' })),
    ).toEqual({ kind: 'row_not_found' });
  });

  it('reads `cell_already_filled` with its prior value', () => {
    expect(
      classifyVoiceCellEditResult(
        result({
          status: 'cell_already_filled',
          skippedReason: 'cell_already_filled',
          priorValue: 7,
        }),
      ),
    ).toEqual({ kind: 'cell_already_filled', priorValue: 7 });
  });

  it('carries a null prior value through `cell_already_filled`', () => {
    expect(
      classifyVoiceCellEditResult(
        result({ status: 'cell_already_filled', priorValue: null }),
      ),
    ).toEqual({ kind: 'cell_already_filled', priorValue: null });
  });

  it('reads any other status as `unexpected`', () => {
    expect(
      classifyVoiceCellEditResult(result({ status: 'unexpected' })),
    ).toEqual({ kind: 'unexpected' });
  });
});

describe('resolveArmMode', () => {
  it('arms for voice when the recorder is usable', () => {
    expect(resolveArmMode(true)).toBe('voice');
  });

  it('falls back to keyboard when it is not', () => {
    expect(resolveArmMode(false)).toBe('keyboard');
  });
});

describe('shouldFallbackToKeyboard', () => {
  it('falls back for an unsupported or denied mic', () => {
    expect(shouldFallbackToKeyboard('unsupported')).toBe(true);
    expect(shouldFallbackToKeyboard('denied')).toBe(true);
  });

  it('does NOT fall back while the mic is still promptable or granted', () => {
    expect(shouldFallbackToKeyboard('prompt')).toBe(false);
    expect(shouldFallbackToKeyboard('granted')).toBe(false);
  });
});
