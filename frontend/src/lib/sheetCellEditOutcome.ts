/**
 * The DECISIONS behind `hooks/useSheetCellEdit.ts`'s commit flow — pure,
 * Node-safe, no React, no atoms.
 *
 * The hook owns the wiring (reads the store, calls `api.editCell`, writes the
 * status atoms) but every branch it takes is decided here, so the tricky parts
 * — the per-edit in-flight dedupe, the stale-outcome guards, and the "reconcile
 * a landed write even when the operator moved on" rule — are pinned by
 * node-only unit tests instead of needing a mounted hook.
 *
 * Identity rules match `lib/sheetCellEdit.ts`: an edit is the same edit only
 * when its cell identity (`trialId` + `plot` + `subsample` + `valuesIndex`),
 * its capture `mode`, and (for keyboard edits) its draft string all match.
 *
 * There is NO conflict path any more: the keyboard fallback commits with
 * `overwrite: true` (a fixed contract carried on the submit decision, so the
 * "always overwrite" rule is unit-tested), and the voice write in RecordView
 * overwrites silently too.
 */

import type { CellEditOutcome, SheetCellEdit } from './sheetCellEdit';
import { parseCellDraft, sameSheetCellEdit } from './sheetCellEdit';

/** The only kind of edit the keyboard commit path can submit (it has a draft). */
export type KeyboardSheetCellEdit = Extract<SheetCellEdit, { mode: 'keyboard' }>;

/**
 * What `submit` should do given the current store snapshot.
 *
 * - `submit` — proceed; carries the resolved keyboard target, the parsed
 *   value, and `overwrite: true` (the single remaining commit always
 *   overwrites — the conflict banner is gone).
 * - `invalid` — the draft is not a whole number in range; show the inline error.
 * - `skip` — nothing to do, with the reason kept for readability/tests:
 *   `no_trial` (host has no trial), `no_edit` (no open edit, it belongs to
 *   another trial, or it is a VOICE arm — a voice write is not a draft
 *   commit), `in_flight` (this exact edit is already saving — dedupe a double
 *   commit in one tick).
 */
export type SubmitDecision =
  | { kind: 'submit'; target: KeyboardSheetCellEdit; value: number; overwrite: true }
  | { kind: 'invalid' }
  | { kind: 'skip'; reason: 'no_trial' | 'no_edit' | 'in_flight' };

export type SubmitDecisionInput = {
  /** The mounted trial, or null. */
  trialId: string | null;
  /** The current open edit descriptor (`sheetCellEditAtom`), or null. */
  target: SheetCellEdit | null;
  /** The draft to parse (the keyboard target's `value`). */
  draft: string;
  /** The edit a request is already in flight for, or null. */
  inFlight: SheetCellEdit | null;
};

export function decideSubmitEdit(input: SubmitDecisionInput): SubmitDecision {
  const { trialId, target, draft, inFlight } = input;

  if (trialId === null) return { kind: 'skip', reason: 'no_trial' };
  // Only a KEYBOARD edit carries a draft to validate and commit; a voice arm
  // is not a submit target (its write happens in RecordView's stop branch).
  if (
    target === null ||
    target.trialId !== trialId ||
    target.mode !== 'keyboard'
  ) {
    return { kind: 'skip', reason: 'no_edit' };
  }

  // This exact edit is already saving — dedupe a double commit in one tick.
  if (sameSheetCellEdit(inFlight, target)) {
    return { kind: 'skip', reason: 'in_flight' };
  }

  const value = parseCellDraft(draft);
  if (value === null) return { kind: 'invalid' };
  // The ONE remaining commit path always overwrites — pinned here so the
  // hook can never send `overwrite: false` by accident.
  return { kind: 'submit', target, value, overwrite: true };
}

/**
 * Whether a late outcome still belongs to the live edit. True only while the
 * exact submitted edit (cell + mode + draft) is still the open one; false once
 * the operator opened a different cell, retyped the same one, flipped the mode,
 * or closed the editor. A stale outcome must never close or re-status a
 * NEWER edit.
 */
export function shouldApplyOutcome(
  submitted: SheetCellEdit,
  current: SheetCellEdit | null,
): boolean {
  return sameSheetCellEdit(current, submitted);
}

/** What to do with a resolved outcome once the store is read again. */
export type CellEditOutcomePlan = {
  /**
   * Paint the optimistic grid update and bump the refresh signal. TRUE only for
   * a landed `written` write — and regardless of staleness: the value IS in the
   * workbook, so it must not be dropped just because the operator moved on.
   */
  reconcile: boolean;
  /** Touch the editor/status. TRUE only while `submitted` is still the live edit. */
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
