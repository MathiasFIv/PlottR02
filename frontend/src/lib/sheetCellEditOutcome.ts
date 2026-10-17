/**
 * The DECISIONS behind `hooks/useSheetCellEdit.ts`'s commit flow — pure,
 * Node-safe, no React, no atoms.
 *
 * The hook owns the wiring (reads the store, calls `api.editCell`, writes the
 * status atoms) but every branch it takes is decided here, so the tricky parts
 * — the per-edit in-flight dedupe, the stale-outcome guards, the "reconcile a
 * landed write even when the operator moved on" rule, and the 409 → overwrite
 * offer — are pinned by node-only unit tests instead of needing a mounted hook.
 *
 * Identity rules match `lib/sheetCellEdit.ts`: an edit is the same edit only
 * when its cell identity (`trialId` + `plot` + `subsample` + `valuesIndex`) AND
 * its draft string match.
 */

import type { CellEditOutcome, SheetCellEdit } from './sheetCellEdit';
import { parseCellDraft, sameSheetCellEdit } from './sheetCellEdit';

/**
 * What `submit(overwrite)` should do given the current store snapshot.
 *
 * - `submit` — proceed; carries the resolved target and the parsed value.
 * - `invalid` — the draft is not a whole number in range; show the inline error.
 * - `skip` — nothing to do, with the reason kept for readability/tests:
 *   `no_trial` (host has no trial), `no_edit` (no open edit, or it belongs to
 *   another trial), `conflict_pending` (a non-overwrite commit while a 409 offer
 *   is on screen — the banner owns the next decision), `in_flight` (this exact
 *   edit is already saving — dedupe a double commit in one tick).
 */
export type SubmitDecision =
  | { kind: 'submit'; target: SheetCellEdit; value: number }
  | { kind: 'invalid' }
  | {
      kind: 'skip';
      reason: 'no_trial' | 'no_edit' | 'conflict_pending' | 'in_flight';
    };

export type SubmitDecisionInput = {
  /** The mounted trial, or null. */
  trialId: string | null;
  /** The current open edit descriptor (`sheetCellEditAtom`), or null. */
  target: SheetCellEdit | null;
  /** The draft to parse (normally `target.value`). */
  draft: string;
  /** True for the "Overwrite" retry, false for a normal commit. */
  overwrite: boolean;
  /** Whether a 409 offer is currently on screen. */
  conflictPending: boolean;
  /** The edit a request is already in flight for, or null. */
  inFlight: SheetCellEdit | null;
};

export function decideSubmitEdit(input: SubmitDecisionInput): SubmitDecision {
  const { trialId, target, draft, overwrite, conflictPending, inFlight } = input;

  if (trialId === null) return { kind: 'skip', reason: 'no_trial' };
  if (target === null || target.trialId !== trialId) {
    return { kind: 'skip', reason: 'no_edit' };
  }

  // A pending conflict owns the next decision: a non-overwrite commit (the blur
  // caused by clicking a banner button, or a second Enter) is a no-op so it can
  // neither re-send the doomed request nor swallow the click. Only the explicit
  // overwrite retry gets past this.
  if (!overwrite && conflictPending) {
    return { kind: 'skip', reason: 'conflict_pending' };
  }

  // This exact edit is already saving — dedupe a double commit in one tick.
  if (sameSheetCellEdit(inFlight, target)) {
    return { kind: 'skip', reason: 'in_flight' };
  }

  const value = parseCellDraft(draft);
  if (value === null) return { kind: 'invalid' };
  return { kind: 'submit', target, value };
}

/**
 * Whether a late outcome still belongs to the live edit. True only while the
 * exact submitted edit (cell + draft) is still the open one; false once the
 * operator opened a different cell, retyped the same one, or closed the editor.
 * A stale outcome must never close or re-status a NEWER edit.
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

/**
 * The 409 → overwrite offer, derived from an outcome. Set ONLY for a
 * `cell_already_filled` conflict (the server returns that `skippedReason` only
 * when the cell is non-writable: it is non-empty AND not the literal text
 * `"missing"`); every other outcome — including a landed write or a
 * `row_not_found` — yields null, so no overwrite is ever offered spuriously.
 */
export type CellEditConflictOffer = { priorValue: number | null; value: number };

export function resolveConflictRetry(
  outcome: CellEditOutcome,
  value: number,
): CellEditConflictOffer | null {
  if (outcome.kind !== 'conflict') return null;
  return { priorValue: outcome.priorValue, value };
}

/**
 * Human-readable prior value for the conflict banner. A conflict means the cell
 * is NOT writable, i.e. non-empty and not `"missing"`, so `priorValue` is:
 * - non-null → the cell holds that numeric value;
 * - null → the cell holds NON-NUMERIC text (not empty — calling it "empty"
 *   would be false at the exact moment the operator confirms an overwrite).
 */
export function formatConflictPrior(priorValue: number | null): string {
  return priorValue === null ? 'a non-numeric entry' : `value ${priorValue}`;
}
