/**
 * Inline cell-edit host logic — shared by BOTH sheet hosts
 * (`components/SheetPanel.tsx` and `views/SheetView.tsx`).
 *
 * Owns everything about the double-tap editor EXCEPT the grid render (which
 * stays in `components/SheetGrid.tsx`) and the pure rules (which live in
 * `lib/sheetCellEdit.ts` / `lib/cellEditGesture.ts` / `lib/sheetRefreshSignal.ts`
 * and are unit-tested under the node-only house setup). No JSX here, so it can
 * live in a `.ts` file.
 *
 * State, all in `store/atoms.ts`:
 * - `sheetCellEditAtom` — the open cell + the DRAFT string;
 * - `sheetCellEditStatusAtom` — `saving` / `error` / `conflict` (the 409 offer).
 * Both are cleared on unmount / trial change so navigating away never strands
 * an open editor.
 *
 * Commit flow (single entry point `submit(overwrite)`):
 * 1. no-op when a conflict is pending and this is not the overwrite attempt —
 *    the banner owns the next decision, and a blur-commit (clicking a banner
 *    button blurs the input) must not race the button's own handler;
 * 2. validate the draft (`parseCellDraft`); invalid → inline error, keep open;
 * 3. `api.editCell(trialId, …)` — `traitOrdinal = valuesIndex + 1`;
 * 4. classify the 200/409 body once (`classifyCellEditResult`) — `editCell`
 *    returns it for both statuses and throws `ApiError` otherwise;
 * 5. reconcile the GRID for a `written` result FIRST, regardless of staleness:
 *    the value did land server-side, so the optimistic cell update and the
 *    refresh signal must not be dropped just because the operator moved on;
 * 6. then, only while this exact edit (cell + draft) is still the open one
 *    (`sameSheetCellEdit`) — clear the editor on success, offer the overwrite
 *    on `cell_already_filled`, show the row-gone message on `row_not_found`,
 *    and surface thrown `ApiError`s / transport failures inline. A stale
 *    outcome never touches the editor or the status of a NEWER edit.
 *
 * The in-flight guard is PER-EDIT (not a global lock): an operator who opens a
 * second cell while the first is still saving is not silently ignored, and the
 * same edit committed twice in one tick (Enter then the blur it causes) is
 * deduped.
 */

import { useAtomValue, useSetAtom, useStore } from 'jotai';
import { useCallback, useEffect, useRef } from 'react';
import { api } from '../lib/api';
import {
  applyCellEdit,
  classifyCellEditResult,
  editCellSelection,
  sameSheetCellEdit,
} from '../lib/sheetCellEdit';
import type { SheetCellEdit, SheetCellEditSelection } from '../lib/sheetCellEdit';
import {
  decideSubmitEdit,
  planCellEditOutcome,
  resolveConflictRetry,
  shouldApplyOutcome,
} from '../lib/sheetCellEditOutcome';
import { bumpSheetRefresh } from '../lib/sheetRefreshSignal';
import type { SheetCellTap } from '../lib/sheetAnchor';
import {
  sheetCellEditAtom,
  sheetCellEditStatusAtom,
  trialSheetAtom,
} from '../store/atoms';
import type { SheetCellEditConflict, SheetCellEditStatus } from '../store/atoms';

const IDLE: SheetCellEditStatus = { saving: false, error: null, conflict: null };

const INVALID_DRAFT_ERROR = 'Enter a whole number from 0 to 10.';

export type UseSheetCellEditResult = {
  /** Resolved editor cell (row cross-ref + column), or null when closed. */
  editing: SheetCellEditSelection | null;
  /** Draft string for the open editor. */
  draft: string;
  saving: boolean;
  error: string | null;
  conflict: SheetCellEditConflict | null;
  /** Double-tap target — prefills the draft from the cell on screen. */
  onEditCell: (cell: SheetCellTap) => void;
  onEditDraftChange: (next: string) => void;
  onEditCommit: () => void;
  onEditCancel: () => void;
  onConfirmOverwrite: () => void;
  onDismissConflict: () => void;
};

export function useSheetCellEdit(trialId: string | null): UseSheetCellEditResult {
  const store = useStore();
  const edit = useAtomValue(sheetCellEditAtom);
  const status = useAtomValue(sheetCellEditStatusAtom);
  const setEdit = useSetAtom(sheetCellEditAtom);
  const setStatus = useSetAtom(sheetCellEditStatusAtom);
  const sheet = useAtomValue(trialSheetAtom);

  /** The exact edit a request is in flight for — dedupes a double commit. */
  const inFlightRef = useRef<SheetCellEdit | null>(null);

  /** Only the mounted trial's edit is live; a cross-trial edit is closed. */
  const liveEdit = edit !== null && edit.trialId === trialId ? edit : null;
  const rows = sheet !== null && sheet.trialId === trialId ? sheet.rows : null;

  // Close the editor (and its status) whenever the host unmounts or the trial
  // changes — the draft names a cell of ONE trial's grid, so it must never
  // survive into another trial's panel. Also drops a stranded conflict.
  useEffect(() => {
    return () => {
      // Also drop the in-flight marker: the edit it referred to is gone (the
      // editor is closed and its status reset), so a re-open of the same cell
      // with the same draft must not be deduped against an already-finished
      // request.
      inFlightRef.current = null;
      setEdit(null);
      setStatus(IDLE);
    };
  }, [trialId, setEdit, setStatus]);

  const onEditCell = useCallback(
    (cell: SheetCellTap) => {
      if (trialId === null) return;
      const current =
        rows?.find((r) => r.plot === cell.plot && r.subsample === cell.subsample)
          ?.values[cell.valuesIndex] ?? null;
      setEdit({
        trialId,
        plot: cell.plot,
        subsample: cell.subsample,
        valuesIndex: cell.valuesIndex,
        // Prefill from the cell on screen; `''` when empty (empty ≠ 0).
        value: current === null ? '' : String(current),
      });
      setStatus(IDLE);
    },
    [trialId, rows, setEdit, setStatus],
  );

  const onEditDraftChange = useCallback(
    (next: string) => {
      setEdit((current) => (current === null ? current : { ...current, value: next }));
      // A new draft supersedes the last error/conflict — re-validated on commit.
      setStatus((current) =>
        current.saving ? current : { saving: false, error: null, conflict: null },
      );
    },
    [setEdit, setStatus],
  );

  const onEditCancel = useCallback(() => {
    setEdit(null);
    setStatus(IDLE);
  }, [setEdit, setStatus]);

  /** Same as cancel: "keep existing" closes the editor without writing. */
  const onDismissConflict = onEditCancel;

  const submit = useCallback(
    async (overwrite: boolean) => {
      const target = store.get(sheetCellEditAtom);

      // Every branch of the commit decision is a pure function so it is unit
      // tested without a mounted hook (see `lib/sheetCellEditOutcome.ts`):
      // no trial / no open edit / a pending conflict owning the next decision
      // (a blur-commit caused by clicking a banner button must be a no-op) /
      // this exact edit already in flight (dedupe a double commit in one tick).
      const decision = decideSubmitEdit({
        trialId,
        target,
        draft: target?.value ?? '',
        overwrite,
        conflictPending: store.get(sheetCellEditStatusAtom).conflict !== null,
        inFlight: inFlightRef.current,
      });

      if (decision.kind === 'invalid') {
        setStatus({ saving: false, error: INVALID_DRAFT_ERROR, conflict: null });
        return;
      }
      // `submit` implies a live target on `trialId`; the explicit null check is
      // only so TypeScript narrows `trialId` below.
      if (decision.kind !== 'submit' || trialId === null) return;

      const { target: submitted, value } = decision;

      inFlightRef.current = submitted;
      setStatus({ saving: true, error: null, conflict: null });
      try {
        const result = await api.editCell(trialId, {
          plot: submitted.plot,
          subsample: submitted.subsample,
          traitOrdinal: submitted.valuesIndex + 1,
          value,
          overwrite,
        });

        // Interpret the 200/409 body (never an ApiError — `editCell` hands us
        // both statuses) via the one tested mapping in `lib/sheetCellEdit.ts`.
        const outcome = classifyCellEditResult(result);

        // Decide what this outcome may touch: reconcile the GRID for a landed
        // write even when the operator has moved on (the value IS in the
        // workbook, so paint it and re-poll), but only apply to the editor
        // while this exact edit is still the live one.
        const plan = planCellEditOutcome(
          outcome,
          submitted,
          store.get(sheetCellEditAtom),
        );

        if (plan.reconcile) {
          const payload = store.get(trialSheetAtom);
          if (payload !== null && payload.trialId === trialId && payload.rows !== null) {
            const nextRows = applyCellEdit(payload.rows, submitted, value);
            // `applyCellEdit` returns the SAME ref when nothing changed — skip
            // the write (and its re-render) in that case.
            if (nextRows !== payload.rows) {
              store.set(trialSheetAtom, { ...payload, rows: nextRows });
            }
          }
          bumpSheetRefresh(trialId);
        }

        // A late outcome must never close or re-status a NEWER edit.
        if (!plan.apply) return;

        switch (outcome.kind) {
          case 'written':
            setEdit(null);
            setStatus(IDLE);
            return;
          case 'conflict':
            setStatus({
              saving: false,
              error: null,
              conflict: resolveConflictRetry(outcome, value),
            });
            return;
          case 'row_not_found':
            setStatus({
              saving: false,
              error: 'That row is no longer in the sheet.',
              conflict: null,
            });
            return;
          case 'unexpected':
            setStatus({
              saving: false,
              error: 'That cell could not be updated.',
              conflict: null,
            });
            return;
        }
      } catch (err) {
        if (!shouldApplyOutcome(submitted, store.get(sheetCellEditAtom))) return;
        const message =
          err instanceof Error ? err.message : 'Could not save the value.';
        setStatus({ saving: false, error: message, conflict: null });
      } finally {
        if (sameSheetCellEdit(inFlightRef.current, submitted)) {
          inFlightRef.current = null;
        }
      }
    },
    [trialId, store, setEdit, setStatus],
  );

  const onEditCommit = useCallback(() => {
    void submit(false);
  }, [submit]);

  const onConfirmOverwrite = useCallback(() => {
    void submit(true);
  }, [submit]);

  return {
    editing: editCellSelection(liveEdit, rows ?? []),
    draft: liveEdit?.value ?? '',
    saving: status.saving,
    error: status.error,
    conflict: status.conflict,
    onEditCell,
    onEditDraftChange,
    onEditCommit,
    onEditCancel,
    onConfirmOverwrite,
    onDismissConflict,
  };
}
