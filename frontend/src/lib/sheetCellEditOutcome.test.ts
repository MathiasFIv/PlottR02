import { describe, expect, it } from 'vitest';
import type { CellEditOutcome, SheetCellEdit } from './sheetCellEdit';
import { decideSubmitEdit, planCellEditOutcome, shouldApplyOutcome } from './sheetCellEditOutcome';
import type { KeyboardSheetCellEdit } from './sheetCellEditOutcome';

const edit = (
  overrides: Partial<KeyboardSheetCellEdit> = {},
): KeyboardSheetCellEdit => ({
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
  valuesIndex: 1,
  mode: 'keyboard',
  value: '5',
  ...overrides,
});

const voiceArm: SheetCellEdit = {
  trialId: 'trial-1',
  plot: 101,
  subsample: 2,
  valuesIndex: 1,
  mode: 'voice',
};

const written: CellEditOutcome = { kind: 'written' };
const rowNotFound: CellEditOutcome = { kind: 'row_not_found' };
const unexpected: CellEditOutcome = { kind: 'unexpected' };

describe('decideSubmitEdit', () => {
  const base = {
    trialId: 'trial-1',
    target: edit(),
    draft: '5',
    inFlight: null,
  } as const;

  it('submits a valid draft, carrying the parsed value AND overwrite: true', () => {
    // The ONE remaining commit path always overwrites — pinned here so the
    // hook can never send `overwrite: false` by accident.
    expect(decideSubmitEdit({ ...base })).toEqual({
      kind: 'submit',
      target: edit(),
      value: 5,
      overwrite: true,
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

  it('skips a VOICE arm (a voice write is not a draft commit)', () => {
    expect(
      decideSubmitEdit({ ...base, target: voiceArm, draft: '' }),
    ).toEqual({ kind: 'skip', reason: 'no_edit' });
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
        inFlight: edit(),
      }),
    ).toEqual({ kind: 'submit', target: edit({ plot: 102 }), value: 5, overwrite: true });
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

  it('is false once the same cell flipped to a voice arm (mode changed)', () => {
    expect(shouldApplyOutcome(edit(), voiceArm)).toBe(false);
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
    expect(planCellEditOutcome(unexpected, edit(), edit({ plot: 999 }))).toEqual({
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
    expect(planCellEditOutcome(unexpected, edit(), edit())).toEqual({
      reconcile: false,
      apply: true,
    });
  });
});
