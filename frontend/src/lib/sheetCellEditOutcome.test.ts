import { describe, expect, it } from 'vitest';
import type { CellEditResult } from './api';
import type { CellEditOutcome, SheetCellEdit } from './sheetCellEdit';
import { classifyCellEditResult } from './sheetCellEdit';
import {
  decideSubmitEdit,
  formatConflictPrior,
  planCellEditOutcome,
  resolveConflictRetry,
  shouldApplyOutcome,
} from './sheetCellEditOutcome';

const edit = (overrides: Partial<SheetCellEdit> = {}): SheetCellEdit => ({
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
  valuesIndex: 1,
  value: '5',
  ...overrides,
});

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

const written: CellEditOutcome = { kind: 'written' };
const conflict: CellEditOutcome = { kind: 'conflict', priorValue: 7 };
const rowNotFound: CellEditOutcome = { kind: 'row_not_found' };

describe('decideSubmitEdit', () => {
  const base = {
    trialId: 'trial-1',
    target: edit(),
    draft: '5',
    overwrite: false,
    conflictPending: false,
    inFlight: null,
  } as const;

  it('submits a valid draft and carries the parsed value', () => {
    expect(decideSubmitEdit({ ...base })).toEqual({
      kind: 'submit',
      target: edit(),
      value: 5,
    });
  });

  it('skips when the host has no trial', () => {
    expect(decideSubmitEdit({ ...base, trialId: null })).toEqual({
      kind: 'skip',
      reason: 'no_trial',
    });
  });

  it('skips when there is no open edit, or it belongs to another trial', () => {
    expect(decideSubmitEdit({ ...base, target: null })).toEqual({
      kind: 'skip',
      reason: 'no_edit',
    });
    expect(
      decideSubmitEdit({ ...base, target: edit({ trialId: 'trial-2' }) }),
    ).toEqual({ kind: 'skip', reason: 'no_edit' });
  });

  it('skips a non-overwrite commit while a conflict is pending (banner owns it)', () => {
    expect(decideSubmitEdit({ ...base, conflictPending: true })).toEqual({
      kind: 'skip',
      reason: 'conflict_pending',
    });
  });

  it('lets the explicit overwrite retry through while a conflict is pending', () => {
    expect(
      decideSubmitEdit({ ...base, conflictPending: true, overwrite: true }),
    ).toEqual({ kind: 'submit', target: edit(), value: 5 });
  });

  it('dedupes a second submit of the same in-flight edit (single writer)', () => {
    expect(decideSubmitEdit({ ...base, inFlight: edit() })).toEqual({
      kind: 'skip',
      reason: 'in_flight',
    });
  });

  it('does not dedupe a different cell while another is in flight', () => {
    expect(
      decideSubmitEdit({
        ...base,
        target: edit({ plot: 102 }),
        draft: '5',
        inFlight: edit(),
      }),
    ).toEqual({ kind: 'submit', target: edit({ plot: 102 }), value: 5 });
  });

  it('reports an out-of-range or non-numeric draft as invalid', () => {
    expect(decideSubmitEdit({ ...base, draft: '11' })).toEqual({
      kind: 'invalid',
    });
    expect(decideSubmitEdit({ ...base, draft: '' })).toEqual({ kind: 'invalid' });
    expect(decideSubmitEdit({ ...base, draft: 'x' })).toEqual({ kind: 'invalid' });
  });

  it('dedupes before re-validating the same in-flight draft (single writer)', () => {
    expect(
      decideSubmitEdit({
        ...base,
        target: edit({ value: '11' }),
        draft: '11',
        inFlight: edit({ value: '11' }),
      }),
    ).toEqual({ kind: 'skip', reason: 'in_flight' });
  });
});

describe('shouldApplyOutcome', () => {
  it('is true while the submitted edit is still the live one', () => {
    expect(shouldApplyOutcome(edit(), edit())).toBe(true);
  });

  it('is false after the operator moved to a different cell (stale guard)', () => {
    expect(shouldApplyOutcome(edit(), edit({ valuesIndex: 2 }))).toBe(false);
    expect(shouldApplyOutcome(edit(), edit({ plot: 999 }))).toBe(false);
    expect(shouldApplyOutcome(edit(), edit({ trialId: 'trial-2' }))).toBe(false);
  });

  it('is false after retyping the same cell (draft changed)', () => {
    expect(shouldApplyOutcome(edit(), edit({ value: '6' }))).toBe(false);
  });

  it('is false once the editor is closed', () => {
    expect(shouldApplyOutcome(edit(), null)).toBe(false);
  });
});

describe('planCellEditOutcome', () => {
  it('reconciles AND applies a written result while the edit is current', () => {
    expect(planCellEditOutcome(written, edit(), edit())).toEqual({
      reconcile: true,
      apply: true,
    });
  });

  it('reconciles a written result even when the edit is no longer the active draft', () => {
    expect(planCellEditOutcome(written, edit(), edit({ plot: 999 }))).toEqual({
      reconcile: true,
      apply: false,
    });
    expect(planCellEditOutcome(written, edit(), null)).toEqual({
      reconcile: true,
      apply: false,
    });
  });

  it('drops a stale non-write entirely (never applied to the wrong cell)', () => {
    expect(planCellEditOutcome(conflict, edit(), edit({ plot: 999 }))).toEqual({
      reconcile: false,
      apply: false,
    });
    expect(planCellEditOutcome(rowNotFound, edit(), null)).toEqual({
      reconcile: false,
      apply: false,
    });
  });

  it('applies a current non-write without reconciling the grid', () => {
    expect(planCellEditOutcome(rowNotFound, edit(), edit())).toEqual({
      reconcile: false,
      apply: true,
    });
  });
});

describe('resolveConflictRetry', () => {
  it('sets the overwrite offer only for `cell_already_filled`', () => {
    expect(
      resolveConflictRetry(
        classifyCellEditResult(
          result({ skippedReason: 'cell_already_filled', priorValue: 7 }),
        ),
        5,
      ),
    ).toEqual({ priorValue: 7, value: 5 });
  });

  it('carries a null prior value through (non-numeric cell)', () => {
    expect(
      resolveConflictRetry(
        classifyCellEditResult(
          result({ skippedReason: 'cell_already_filled', priorValue: null }),
        ),
        5,
      ),
    ).toEqual({ priorValue: null, value: 5 });
  });

  it('offers no overwrite for a landed write', () => {
    expect(
      resolveConflictRetry(
        classifyCellEditResult(result({ written: true, skippedReason: null })),
        5,
      ),
    ).toBeNull();
  });

  it('offers no overwrite for `row_not_found` or an unexpected body', () => {
    expect(
      resolveConflictRetry(
        classifyCellEditResult(result({ skippedReason: 'row_not_found' })),
        5,
      ),
    ).toBeNull();
    expect(
      resolveConflictRetry(
        classifyCellEditResult(result({ skippedReason: null })),
        5,
      ),
    ).toBeNull();
  });
});

describe('formatConflictPrior', () => {
  it('renders a numeric prior value as `value N`', () => {
    expect(formatConflictPrior(7)).toBe('value 7');
    expect(formatConflictPrior(0)).toBe('value 0');
  });

  it('describes a null prior value as a non-numeric entry, never "empty"', () => {
    expect(formatConflictPrior(null)).toBe('a non-numeric entry');
  });
});
