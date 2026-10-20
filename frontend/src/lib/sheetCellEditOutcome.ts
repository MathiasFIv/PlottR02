/**
 * The DECISIONS behind `hooks/useSheetCellEdit.ts`'s commit flow — pure,
 * Node-safe, no React, no atoms. An edit is "the same edit" only when cell
 * identity, capture mode, and (for keyboard edits) the draft all match. There
 * is no conflict path: every commit overwrites.
 */

import type { CellEditOutcome, SheetCellEdit } from './sheetCellEdit';
import { parseCellDraft, sameSheetCellEdit } from './sheetCellEdit';

/** The only kind of edit the keyboard commit path can submit (it has a draft). */
export type KeyboardSheetCellEdit = Extract<SheetCellEdit, { mode: 'keyboard' }>;

/** `submit` proceeds (always with `overwrite: true`); `invalid` shows the inline error; `skip` has a reason. */
export type SubmitDecision =
  | { kind: 'submit'; target: KeyboardSheetCellEdit; value: number; overwrite: true }
  | { kind: 'invalid' }
  | { kind: 'skip'; reason: 'no_trial' | 'no_edit' | 'in_flight' };

export type SubmitDecisionInput = {
  trialId: string | null;
  target: SheetCellEdit | null;
  draft: string;
  inFlight: SheetCellEdit | null;
};

export function decideSubmitEdit(input: SubmitDecisionInput): SubmitDecision {
  const { trialId, target, draft, inFlight } = input;

  if (trialId === null) return { kind: 'skip', reason: 'no_trial' };
  // A voice arm is not a submit target — its write happens in RecordView's stop branch.
  if (
    target === null ||
    target.trialId !== trialId ||
    target.mode !== 'keyboard'
  ) {
    return { kind: 'skip', reason: 'no_edit' };
  }

  if (sameSheetCellEdit(inFlight, target)) {
    return { kind: 'skip', reason: 'in_flight' };
  }

  const value = parseCellDraft(draft);
  if (value === null) return { kind: 'invalid' };
  return { kind: 'submit', target, value, overwrite: true };
}

/** True only while the exact submitted edit is still the live one. */
export function shouldApplyOutcome(
  submitted: SheetCellEdit,
  current: SheetCellEdit | null,
): boolean {
  return sameSheetCellEdit(current, submitted);
}

/** What to do with a resolved outcome once the store is read again. */
export type CellEditOutcomePlan = {
  /** Paint the optimistic update + bump the refresh signal; true for any landed write, stale or not. */
  reconcile: boolean;
  /** Touch the editor/status; true only while `submitted` is still the live edit. */
  apply: boolean;
};

export function planCellEditOutcome(
  outcome: CellEditOutcome,
  submitted: SheetCellEdit,
  current: SheetCellEdit | null,
): CellEditOutcomePlan {
  return {
    reconcile: outcome.kind === 'written',
    apply: shouldApplyOutcome(submitted, current),
  };
}
