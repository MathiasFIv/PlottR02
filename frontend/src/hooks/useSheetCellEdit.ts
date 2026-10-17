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
 * - `sheetCellEditAtom` — the edit descriptor, a union on `mode`: a `voice`
 *   arm (yellow cell, no draft) or a `keyboard` edit (the DRAFT string);
 * - `sheetCellEditStatusAtom` — `saving` / `error` (no conflict state: both
 *   modes overwrite, so the yellow armed cell is the affordance).
 * Both are cleared on unmount / trial change so navigating away never strands
 * an open editor.
 *
 * Capture modes (`lib/sheetCellEdit.ts#resolveArmMode`):
 * - `voice` (RecordView with a usable mic): `onEditCell` only ARMS the cell —
 *   `armed` resolves, `editing` stays null, and RecordView's auto-start effect
 *   starts the recorder; the voice WRITE itself is orchestrated by RecordView's
 *   stop branch (not this hook).
 * - `keyboard` (mic unavailable/denied, or the `/sheet` route which has no
 *   recorder): the inline `SheetCellEditor` opens exactly as before.
 *
 * Commit flow (single entry point `submit`, ALWAYS `overwrite: true`):
 * 1. validate the draft (`parseCellDraft`) — a voice arm is never a submit
 *    target (`decideSubmitEdit` returns `no_edit` for it);
 * 2. `api.editCell(trialId, …, overwrite: true)` — `traitOrdinal = valuesIndex + 1`;
 * 3. classify the 200 body once (`classifyCellEditResult`) — `editCell`
 *    returns it and throws `ApiError` otherwise;
 * 4. reconcile the GRID for a `written` result FIRST, regardless of staleness:
 *    the value did land server-side, so the optimistic cell update and the
 *    refresh signal must not be dropped just because the operator moved on;
 * 5. then, only while this exact edit (cell + mode + draft) is still the open
 *    one (`sameSheetCellEdit`) — clear the editor on success, show the
 *    row-gone / generic message on `row_not_found` / `unexpected`, and surface
 *    thrown `ApiError`s / transport failures inline. A stale outcome never
 *    touches the editor or the status of a NEWER edit.
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
  resolveArmMode,
  sameSheetCellEdit,
} from '../lib/sheetCellEdit';
import type {
  SheetCellEdit,
  SheetCellEditMode,
  SheetCellEditSelection,
} from '../lib/sheetCellEdit';
import {
  decideSubmitEdit,
  planCellEditOutcome,
  shouldApplyOutcome,
} from '../lib/sheetCellEditOutcome';
import { bumpSheetRefresh } from '../lib/sheetRefreshSignal';
import type { SheetCellTap } from '../lib/sheetAnchor';
import {
  sheetCellEditAtom,
  sheetCellEditStatusAtom,
  trialSheetAtom,
} from '../store/atoms';
import type { SheetCellEditStatus } from '../store/atoms';

const IDLE: SheetCellEditStatus = { saving: false, error: null };

const INVALID_DRAFT_ERROR = 'Enter a whole number from 0 to 10.';

export type UseSheetCellEditResult = {
  /**
   * The cell the edit is live on (both modes) — resolved against the payload
   * (row cross-ref + column). Drives the yellow armed cell.
   */
  armed: SheetCellEditSelection | null;
  /**
   * Resolved ONLY for a `keyboard` edit (row cross-ref + column), or null —
   * drives the inline `SheetCellEditor`. A voice arm has no editor.
   */
  editing: SheetCellEditSelection | null;
  /** The live edit's capture mode, or null when nothing is live. */
  mode: SheetCellEditMode | null;
  /** Draft string for the open keyboard editor (`''` for a voice arm). */
  draft: string;
  saving: boolean;
  error: string | null;
  /** Double-tap target — arms the cell (voice) or opens the editor (keyboard). */
  onEditCell: (cell: SheetCellTap) => void;
  onEditDraftChange: (next: string) => void;
  /** Keyboard commit — always `overwrite: true`. */
  onEditCommit: () => void;
  onEditCancel: () => void;
  /** Clears both atoms (used by RecordView after a voice write or a hard reset). */
  clearArm: () => void;
};

export function useSheetCellEdit(
  trialId: string | null,
  options?: { voiceEnabled?: boolean },
): UseSheetCellEditResult {
  const voiceEnabled = options?.voiceEnabled ?? false;
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
  // changes — the descriptor names a cell of ONE trial's grid, so it must never
  // survive into another trial's panel.
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
      const mode = resolveArmMode(voiceEnabled);
      if (mode === 'voice') {
        // Arm only: no draft — the number comes from the transcript, and
        // RecordView's auto-start effect starts the recorder.
        setEdit({
          trialId,
          plot: cell.plot,
          subsample: cell.subsample,
          valuesIndex: cell.valuesIndex,
          mode: 'voice',
        });
      } else {
        // Keyboard fallback: prefill the draft from the cell on screen
        // (`''` when empty — empty ≠ 0).
        const current =
          rows?.find((r) => r.plot === cell.plot && r.subsample === cell.subsample)
            ?.values[cell.valuesIndex] ?? null;
        setEdit({
          trialId,
          plot: cell.plot,
          subsample: cell.subsample,
          valuesIndex: cell.valuesIndex,
          mode: 'keyboard',
          value: current === null ? '' : String(current),
        });
      }
      setStatus(IDLE);
    },
    [trialId, voiceEnabled, rows, setEdit, setStatus],
  );

  const onEditDraftChange = useCallback(
    (next: string) => {
      // A voice arm carries no draft; only a keyboard edit accepts keystrokes.
      setEdit((current) =>
        current === null || current.mode !== 'keyboard'
          ? current
          : { ...current, value: next },
      );
      // A new draft supersedes the last error — re-validated on commit.
      setStatus((current) =>
        current.saving ? current : { saving: false, error: null },
      );
    },
    [setEdit, setStatus],
  );

  const onEditCancel = useCallback(() => {
    setEdit(null);
    setStatus(IDLE);
  }, [setEdit, setStatus]);

  const clearArm = useCallback(() => {
    setEdit(null);
    setStatus(IDLE);
  }, [setEdit, setStatus]);

  const submit = useCallback(async () => {
    const target = store.get(sheetCellEditAtom);

    // Every branch of the commit decision is a pure function so it is unit
    // tested without a mounted hook (see `lib/sheetCellEditOutcome.ts`):
    // no trial / no open KEYBOARD edit / this exact edit already in flight
    // (dedupe a double commit in one tick). The decision carries
    // `overwrite: true` — the single remaining commit always overwrites.
    const decision = decideSubmitEdit({
      trialId,
      target,
      draft: target?.mode === 'keyboard' ? target.value : '',
      inFlight: inFlightRef.current,
    });

    if (decision.kind === 'invalid') {
      setStatus({ saving: false, error: INVALID_DRAFT_ERROR });
      return;
    }
    // `submit` implies a live keyboard target on `trialId`; the explicit null
    // check is only so TypeScript narrows `trialId` below.
    if (decision.kind !== 'submit' || trialId === null) return;

    const { target: submitted, value, overwrite } = decision;

    inFlightRef.current = submitted;
    setStatus({ saving: true, error: null });
    try {
      const result = await api.editCell(trialId, {
        plot: submitted.plot,
        subsample: submitted.subsample,
        traitOrdinal: submitted.valuesIndex + 1,
        value,
        overwrite,
      });

      // Interpret the 200 body (never an ApiError — `editCell` hands it to
      // us) via the one tested mapping in `lib/sheetCellEdit.ts`.
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
        case 'row_not_found':
          setStatus({
            saving: false,
            error: 'That row is no longer in the sheet.',
          });
          return;
        case 'unexpected':
          setStatus({
            saving: false,
            error: 'That cell could not be updated.',
          });
          return;
      }
    } catch (err) {
      if (!shouldApplyOutcome(submitted, store.get(sheetCellEditAtom))) return;
      const message =
        err instanceof Error ? err.message : 'Could not save the value.';
      setStatus({ saving: false, error: message });
    } finally {
      if (sameSheetCellEdit(inFlightRef.current, submitted)) {
        inFlightRef.current = null;
      }
    }
  }, [trialId, store, setEdit, setStatus]);

  // The keyboard commit ALWAYS overwrites — there is no non-overwrite path.
  const onEditCommit = useCallback(() => {
    void submit();
  }, [submit]);

  const armed = editCellSelection(liveEdit, rows ?? []);
  const keyboardEdit = liveEdit?.mode === 'keyboard' ? liveEdit : null;

  return {
    armed,
    editing: editCellSelection(keyboardEdit, rows ?? []),
    mode: liveEdit?.mode ?? null,
    draft: keyboardEdit?.value ?? '',
    saving: status.saving,
    error: status.error,
    onEditCell,
    onEditDraftChange,
    onEditCommit,
    onEditCancel,
    clearArm,
  };
}
