/**
 * Cell-level render helpers for the sheet grid — the D5 **value channel**
 * (PLAN_2026-10-07 §10 A-8) and the UI half of `design.md` Rule 2.
 *
 * **Rule 2 — empty ≠ 0.** The server sends `null` for an empty/text cell and a
 * real number (including `0`) for a rated cell. Every branch here keys on
 * `value === null` and NEVER on truthiness: `{value || ''}` would render a
 * rated `0` as blank, which is the single most likely defect in this phase
 * (2026-10-06 §9 risk 4). Both halves are pinned by `sheetCell.test.ts`.
 */

/** D5 value-channel class: empty (available) vs filled (rated). */
export type SheetCellState = 'sheet-cell--empty' | 'sheet-cell--filled';

/**
 * The text a cell renders. `null` → `''` (blank); `0` → `'0'`.
 * Numbers are stringified so React never has to guess at `0`.
 */
export function sheetCellValue(value: number | null): string {
  return value === null ? '' : String(value);
}

/**
 * The value-channel CSS class pair for a cell (combined with the structural
 * `sheet-col--value` class by the grid).
 */
export function sheetCellState(value: number | null): SheetCellState {
  return value === null ? 'sheet-cell--empty' : 'sheet-cell--filled';
}
