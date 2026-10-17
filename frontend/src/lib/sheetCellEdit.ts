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
import type { CellEditResult, VoiceCellEditResult } from './api';

/** Identifies the ONE cell being edited, in the grid's own render shape. */
export type SheetCellEditSelection = { row: number; valuesIndex: number };

/**
 * How an armed cell edit will be captured: `voice` — the recorder auto-starts
 * and the spoken number is written on stop (RecordView); `keyboard` — the
 * inline number input (the fallback when the mic is unavailable/denied and
 * the always-keyboard standalone `/sheet` route).
 */
export type SheetCellEditMode = 'voice' | 'keyboard';

/**
 * The full edit descriptor held in `sheetCellEditAtom` while an edit is live,
 * as a discriminated union on `mode`:
 *
 * - `voice` — the cell is ARMED for a voice write; no draft exists yet (the
 *   number comes from the transcript). The yellow armed cell is the only
 *   affordance; there is no conflict banner — the write always overwrites.
 * - `keyboard` — the inline input is open; `value` is the DRAFT string
 *   (pre-filled from the current value, `''` when empty). Keeping the draft
 *   as a string lets an in-progress invalid entry (empty, `"11"`) survive a
 *   keystroke without the store ever holding a non-number.
 */
export type SheetCellEdit =
  | {
      trialId: string;
      plot: number;
      subsample: number;
      valuesIndex: number;
      mode: 'voice';
    }
  | {
      trialId: string;
      plot: number;
      subsample: number;
      valuesIndex: number;
      mode: 'keyboard';
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
 * Whether two edit descriptors are the SAME edit: cell identity AND capture
 * `mode` must match, and the draft string is compared only when BOTH are
 * keyboard edits (a voice arm has no draft). A late `editCell` response uses
 * this to drop itself when the operator has, in the meantime, opened a
 * different cell — or retyped the same one — so a stale outcome can never
 * close or overwrite the editor in front of them.
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
    a.mode === b.mode &&
    // A voice arm carries no draft; only two keyboard edits can differ by it.
    (a.mode !== 'keyboard' || b.mode !== 'keyboard' || a.value === b.value)
  );
}

/**
 * What a `CellEditResult` means for the editor, decoupled from the atoms so the
 * backend contract is pinned in one testable place.
 *
 * - `written` — the value landed (200, `written: true`). Close the editor.
 * - `row_not_found` — a 200 with `written: false`, `skippedReason:
 *   "row_not_found"`. NOT a transport error, but still a failure to save: the
 *   operator's row has left the sheet, so surface it as an inline error.
 * - `unexpected` — any other non-write, INCLUDING `cell_already_filled`: the
 *   keyboard fallback now always commits with `overwrite: true`, so a skip
 *   for a filled cell is a contract surprise, not an offer to overwrite (the
 *   409 conflict outcome was removed along with the conflict banner).
 *
 * `written` wins over `skippedReason`, so a hypothetical "written land also
 * reports a skip" can never be misread as a failure.
 */
export type CellEditOutcome =
  | { kind: 'written' }
  | { kind: 'row_not_found' }
  | { kind: 'unexpected' };

export function classifyCellEditResult(
  result: CellEditResult,
): CellEditOutcome {
  if (result.written) return { kind: 'written' };
  if (result.skippedReason === 'row_not_found') return { kind: 'row_not_found' };
  return { kind: 'unexpected' };
}

/**
 * What a `VoiceCellEditResult` means for the armed cell, keyed off its single
 * `status` field — the backend collapses every voice outcome into a 200 body:
 *
 * - `written` — the transcribed number landed (carried for the optimistic
 *   grid update + the prior value for diagnostics).
 * - `not_understood` — no parseable number in 0…10; the operator retries.
 * - `row_not_found` — the addressed row has left the workbook.
 * - `cell_already_filled` — the target held a value and the client asked NOT
 *   to overwrite; `priorValue` carries what is there. (The normal voice flow
 *   always sends `overwrite: true`, so this only surfaces when a caller opts
 *   out.)
 * - `unexpected` — any other status (defensive).
 */
export type VoiceCellEditOutcome =
  | { kind: 'written'; value: number; priorValue: number | null }
  | { kind: 'not_understood' }
  | { kind: 'row_not_found' }
  | { kind: 'cell_already_filled'; priorValue: number | null }
  | { kind: 'unexpected' };

export function classifyVoiceCellEditResult(
  result: VoiceCellEditResult,
): VoiceCellEditOutcome {
  switch (result.status) {
    case 'written':
      return {
        kind: 'written',
        // A landed write always carries the parsed value server-side; `?? 0`
        // is unreachable but keeps the store non-null without a cast.
        value: result.value ?? 0,
        priorValue: result.priorValue,
      };
    case 'not_understood':
      return { kind: 'not_understood' };
    case 'row_not_found':
      return { kind: 'row_not_found' };
    case 'cell_already_filled':
      return { kind: 'cell_already_filled', priorValue: result.priorValue };
    default:
      return { kind: 'unexpected' };
  }
}

/**
 * The capture mode a double-tap arms: `voice` when the recorder is usable
 * (RecordView passes `permissionState !== 'unsupported' && !== 'denied'`),
 * otherwise `keyboard` — the inline input is the fallback for a missing or
 * blocked mic, and the only mode on the standalone `/sheet` route (which has
 * no recorder at all).
 */
export function resolveArmMode(voiceEnabled: boolean): SheetCellEditMode {
  return voiceEnabled ? 'voice' : 'keyboard';
}

/**
 * Whether a live voice arm must be flipped to the keyboard fallback because
 * the permission state cannot record (`'unsupported'` browser APIs or a
 * `'denied'` permission). `'prompt'` and `'granted'` do NOT fall back —
 * the first `getUserMedia` call may still succeed.
 */
export function shouldFallbackToKeyboard(permissionState: string): boolean {
  return permissionState === 'unsupported' || permissionState === 'denied';
}
