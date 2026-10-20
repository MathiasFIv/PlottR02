/**
 * Inline cell-edit helpers — pure, Node-safe, no React. A cell is addressed by
 * ROW identity (plot + subsample) plus the 0-based `valuesIndex` column, never
 * by the Excel `SheetRow.row` cross-ref nor arithmetic on these numbers.
 */

import type { SheetCellTap } from './sheetAnchor';
import type { SheetRow } from '../types';
import type { CellEditResult, VoiceCellEditResult } from './api';

/** Identifies the ONE cell being edited, in the grid's render shape. */
export type SheetCellEditSelection = { row: number; valuesIndex: number };

/** `voice` arms the recorder; `keyboard` opens the inline input. */
export type SheetCellEditMode = 'voice' | 'keyboard';

/**
 * The live edit descriptor, discriminated on `mode`. A `keyboard` edit keeps
 * its draft as a string so an invalid in-progress entry never reaches the store.
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

/** Accepted value range for a rating cell (`0` is a real value). */
export const CELL_VALUE_MIN = 0;
export const CELL_VALUE_MAX = 10;

/** Parses a draft into a rating value, or null unless it is a whole number in [0, 10]. */
export function parseCellDraft(draft: string): number | null {
  const trimmed = draft.trim();
  if (trimmed === '') return null;
  if (!/^\d+$/.test(trimmed)) return null;
  const value = Number(trimmed);
  if (value < CELL_VALUE_MIN || value > CELL_VALUE_MAX) return null;
  return value;
}

/** Resolves the edit descriptor's row cross-ref + column against the payload. */
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

/** Returns a copy of `rows` with the addressed cell set, or the same reference when nothing changed. */
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
 * Whether two edit descriptors are the SAME edit: cell identity and mode must
 * match; the draft is compared only between two keyboard edits.
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
    (a.mode !== 'keyboard' || b.mode !== 'keyboard' || a.value === b.value)
  );
}

/**
 * What a `CellEditResult` means for the editor. `cell_already_filled` is
 * reported as `unexpected`: the keyboard fallback always commits with
 * `overwrite: true`, so a skip for a filled cell is a contract surprise.
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

/** What a `VoiceCellEditResult` means, keyed off its single `status` field. */
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

/** The capture mode a double-tap arms; `keyboard` is the fallback when voice is unusable. */
export function resolveArmMode(voiceEnabled: boolean): SheetCellEditMode {
  return voiceEnabled ? 'voice' : 'keyboard';
}

/** Whether a live voice arm must fall back to keyboard (`unsupported`/`denied` only; `prompt`/`granted` do not). */
export function shouldFallbackToKeyboard(permissionState: string): boolean {
  return permissionState === 'unsupported' || permissionState === 'denied';
}
