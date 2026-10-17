/**
 * Inline cell-edit helpers — pure, Node-safe, no React.
 *
 * The UI half of the "edit a sheet cell" feature: the double-tap gesture
 * (`lib/cellEditGesture.ts`) hands a tapped cell to the host, which stores an
 * edit descriptor in `store/atoms.ts#sheetCellEditAtom`; these functions
 * resolve that descriptor onto the grid, parse the typed draft, apply the
 * optimistic update, and tell a late async outcome whether the edit it started
 * is still the live one.
 *
 * Identity rules mirror `lib/sheetAnchor.ts` / `lib/sheetTarget.ts`: a cell is
 * addressed by its ROW identity (`plot` + `subsample`) plus the 0-based
 * `valuesIndex` column — never by the Excel `SheetRow.row` cross-ref, and never
 * by arithmetic on those numbers.
 */

import type { SheetCellTap } from './sheetAnchor';
import type { SheetRow } from '../types';
import type { CellEditResult } from './api';

/** Identifies the ONE cell being edited, in the grid's own render shape. */
export type SheetCellEditSelection = { row: number; valuesIndex: number };

/**
 * The full edit descriptor held in `sheetCellEditAtom` while the editor is
 * open: the addressed cell plus the DRAFT string the operator is typing
 * (pre-filled from the current value, `''` when the cell is empty). Keeping
 * the draft as a string lets an in-progress invalid entry (empty, `"11"`)
 * survive a keystroke without the store ever holding a non-number.
 */
export type SheetCellEdit = {
  trialId: string;
  plot: number;
  subsample: number;
  valuesIndex: number;
  value: string;
};

/** The accepted value range for a rating cell (`0` is a real value). */
export const CELL_VALUE_MIN = 0;
export const CELL_VALUE_MAX = 10;

/**
 * Parses the typed draft into a rating value, or `null` when it is not a
 * whole number in `[0, 10]`. Strict on purpose: `""`, `"1.5"`, `"11"`, `"x"`
 * and `"-1"` are all rejected — the caller shows an inline error and keeps the
 * editor open instead of silently writing a wrong value.
 */
export function parseCellDraft(draft: string): number | null {
  const trimmed = draft.trim();
  if (trimmed === '') return null;
  if (!/^\d+$/.test(trimmed)) return null;
  const value = Number(trimmed);
  if (value < CELL_VALUE_MIN || value > CELL_VALUE_MAX) return null;
  return value;
}

/**
 * Resolves the edit descriptor against the payload on screen: the matched
 * row's `row` cross-ref (matched by `plot` + `subsample` identity) plus the
 * edited column, or `null` when there is no edit or its row is not in this
 * payload. Mirrors `lib/sheetTarget.ts#targetSelection`.
 */
export function editCellSelection(
  edit: SheetCellTap | null,
  rows: SheetRow[],
): SheetCellEditSelection | null {
  if (edit === null) return null;

  const row = rows.find(
    (r) => r.plot === edit.plot && r.subsample === edit.subsample,
  );
  if (row === undefined) return null;
  return { row: row.row, valuesIndex: edit.valuesIndex };
}

/**
 * Returns a copy of `rows` with the addressed cell set to `value`. Identity
 * match (never `Row`), so a refetch that re-numbers Excel rows can never move
 * the write. Returns the SAME `rows` reference when nothing changed (row not
 * found, or the cell already holds the value) — callers can cheaply tell
 * "no-op" and skip a state write.
 */
export function applyCellEdit(
  rows: SheetRow[],
  edit: Pick<SheetCellTap, 'plot' | 'subsample' | 'valuesIndex'>,
  value: number,
): SheetRow[] {
  const rowIndex = rows.findIndex(
    (r) => r.plot === edit.plot && r.subsample === edit.subsample,
  );
  if (rowIndex === -1) return rows;

  const row = rows[rowIndex];
  if (row.values[edit.valuesIndex] === value) return rows;

  const values = row.values.slice();
  values[edit.valuesIndex] = value;
  const next = rows.slice();
  next[rowIndex] = { ...row, values };
  return next;
}

/**
 * Whether two edit descriptors are the SAME edit (cell identity AND the draft
 * string). A late `editCell` response uses this to drop itself when the
 * operator has, in the meantime, opened a different cell — or retyped the same
 * one — so a stale outcome can never close or overwrite the editor in front of
 * them.
 */
export function sameSheetCellEdit(
  a: SheetCellEdit | null,
  b: SheetCellEdit | null,
): boolean {
  if (a === null || b === null) return a === b;
  return (
    a.trialId === b.trialId &&
    a.plot === b.plot &&
    a.subsample === b.subsample &&
    a.valuesIndex === b.valuesIndex &&
    a.value === b.value
  );
}

/**
 * What a `CellEditResult` means for the editor, decoupled from the atoms so the
 * backend contract is pinned in one testable place.
 *
 * - `written` — the value landed (200, `written: true`). Close the editor.
 * - `conflict` — the cell was already filled and `overwrite: false` (409, or a
 *   200 with the same `skippedReason`): offer to overwrite with `priorValue`.
 * - `row_not_found` — a 200 with `written: false`, `skippedReason:
 *   "row_not_found"`. NOT a transport error, but still a failure to save: the
 *   operator's row has left the sheet, so surface it as an inline error.
 * - `unexpected` — any other non-write (defensive; a 4xx/4xx surfaces as an
 *   `ApiError` from `editCell`, never here).
 *
 * `written` wins over `skippedReason`, so a hypothetical "written land also
 * reports a skip" can never be misread as a conflict.
 */
export type CellEditOutcome =
  | { kind: 'written' }
  | { kind: 'conflict'; priorValue: number | null }
  | { kind: 'row_not_found' }
  | { kind: 'unexpected' };

export function classifyCellEditResult(
  result: CellEditResult,
): CellEditOutcome {
  if (result.written) return { kind: 'written' };
  if (result.skippedReason === 'cell_already_filled') {
    return { kind: 'conflict', priorValue: result.priorValue };
  }
  if (result.skippedReason === 'row_not_found') return { kind: 'row_not_found' };
  return { kind: 'unexpected' };
}
