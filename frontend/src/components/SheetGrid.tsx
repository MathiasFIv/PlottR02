import { useRef } from 'react';
import { sheetCellState, sheetCellValue } from '../lib/sheetCell';
import {
  decideCellTap,
  emptyTapTracker,
} from '../lib/cellEditGesture';
import type { TapTracker } from '../lib/cellEditGesture';
import { buildSheetHeader } from '../lib/sheetHeader';
import type { SheetCellTap } from '../lib/sheetAnchor';
import type { SheetCellEditSelection } from '../lib/sheetCellEdit';
import type { SheetCellSelection, SheetHighlight } from '../lib/sheetTarget';
import type { SheetColumn, SheetRow } from '../types';
import { SheetCellEditor } from './SheetCellEditor';
import './SheetGrid.css';

type SheetGridProps = {
  columns: SheetColumn[];
  rows: SheetRow[];
  /**
   * N3 — the current-cell highlight (`lib/sheetTarget.ts`). The hosts derive
   * it from the last-written atom (`sheetLastWrittenAtom`) + the payload on
   * every render — `deriveNextTarget` picks the row (STAY on this row while
   * its block is unfilled; once full, advance to the NEXT row, crossing plot
   * boundaries, with only the true end of the sheet yielding null),
   * `targetHighlight` maps it here; `row` matches the row's `SheetRow.row`.
   * Row-only: the write cursor is a row, so no column is pinned.
   */
  highlight?: SheetHighlight | null;
  /**
   * Write anchor — the TAPPED cell, mapped by the hosts through
   * `targetSelection` against the current payload. Renders the
   * `.sheet-row--selected` row tint AND a stronger `.sheet-cell--selected`
   * highlight on the tapped column (`valuesIndex`). INDEPENDENT of
   * `highlight`, so the anchor (primary accent) and the auto-continue cursor
   * (soft amber) can coexist and stay visually distinct.
   */
  selected?: SheetCellSelection | null;
  /**
   * Tap-to-anchor: fired for VALUE cells only (never the sticky key column,
   * never headers), carrying the row identity (`plot` + `subsample`) plus the
   * tapped column as its 0-based `valuesIndex`. The WRITE is still row-only —
   * the server scans forward from column C — the column index only drives the
   * cell highlight.
   */
  onSelectCell?: (cell: SheetCellTap) => void;
  /**
   * N3/N4 seam — cell addresses currently flashing. Accepted, NOT rendered
   * (N4 owns `flashCellsAtom` + the `.cell--flash` keyframes).
   */
  flashes?: readonly string[] | null;
  /**
   * Double-tap-to-edit: fired when a SECOND tap lands on the same value cell
   * within `DOUBLE_TAP_WINDOW_MS` (`lib/cellEditGesture.ts`). The anchor path
   * (`onSelectCell`) still runs for the FIRST tap; this fires only for the
   * edit tap, so the two gestures never double-fire. Omit to keep the grid
   * strictly tap-to-anchor.
   */
  onEditCell?: (cell: SheetCellTap) => void;
  /**
   * The cell the inline editor is open on (resolved by the host hook against
   * the current payload), or null. Matched to a cell by the row's `SheetRow`
   * identity (`row` cross-ref) + `valuesIndex`, exactly like `selected`.
   */
  editing?: SheetCellEditSelection | null;
  /** Draft text for the open editor (controlled). */
  editDraft?: string;
  /** True while the edit is being saved — disables the input. */
  editSaving?: boolean;
  /** Fired on every keystroke in the open editor. */
  onEditDraftChange?: (next: string) => void;
  /** Fired on Enter and on blur — the host validates, saves and dedupes. */
  onEditCommit?: () => void;
  /** Fired on Escape — the host closes the editor without saving. */
  onEditCancel?: () => void;
};

/**
 * The read-only sheet grid — ONE component, two hosts (the embedded
 * `SheetPanel` and the `/trials/:trialId/sheet` route).
 *
 * Grid decision (PLAN_2026-10-06 §5, carried forward): a **plain semantic
 * `<table>`** with a sticky two-tier header and a sticky key column. No
 * virtualization and no new dependency — the region is hard-capped at 224 rows
 * by `C22:K245`, so AG Grid's differentiators buy nothing while its ~100–150 kB
 * would be precached by Workbox for every field worker.
 *
 * Layout (all CSS, no JS scroll listeners — sticky only):
 * - tier 1 `<thead>` row = Part Rated runs (`colSpan` derived from the file);
 * - tier 2 `<thead>` row = Pest Code, one cell per column;
 * - sticky corner cell (`Plot (Sub)`) sits at a higher z-index than the other
 *   header cells (PLAN §11 risk 6);
 * - sticky key column = column B (`plot subsample`); column A never ships in
 *   the payload, so it is never rendered.
 *
 * **D5 — both visual channels at once (§10 A-8):**
 * - *structural* → `.sheet-col--key` (muted/locked) on the key column vs
 *   `.sheet-col--value` (normal) on C:K;
 * - *value* → `.sheet-cell--empty` (blank + subtle "available" dot) when
 *   `value === null`, `.sheet-cell--filled` (solid) otherwise.
 *
 * **Rule 2 (empty ≠ 0):** the render branch lives in `lib/sheetCell.ts` and
 * keys on `value === null` — never on truthiness — so a rated `0` renders as
 * `0` and an empty cell never renders as `0`.
 *
 * **N3 current cell:** `highlight` tints the matched `<tr>` with
 * `.sheet-row--current`. Background-only (see `index.css`), so no box model
 * changes and no new horizontal scroll at 360–390 px; the value render path
 * (`sheetCellValue` / `sheetCellState`) is untouched, so a highlighted `0`
 * still renders `0` and an empty cell still renders blank. `flashes` stays
 * accepted-but-unrendered (N4).
 *
 * **Write anchor:** `selected` styles the tapped row (`.sheet-row--selected`
 * row tint) plus the tapped CELL (`.sheet-cell--selected`, a stronger accent
 * so it reads over the row wash) and `onSelectCell` handles taps on VALUE
 * `<td>`s only. Both are derived from the row's `SheetRow` identity (`plot` +
 * `subsample`) — never from Excel letters or `SheetRow.row` arithmetic — and
 * the anchor channel renders independently of `highlight` (the two can point
 * at different rows at once).
 */
export function SheetGrid({
  columns,
  rows,
  highlight,
  selected,
  onSelectCell,
  onEditCell,
  editing,
  editDraft,
  editSaving,
  onEditDraftChange,
  onEditCommit,
  onEditCancel,
}: SheetGridProps) {
  const header = buildSheetHeader(columns);

  // Double-tap tracker — a ref, so a tape-check never re-renders the grid on
  // its own (only the edit atom change does). Reset on every edit.
  const tapTrackerRef = useRef<TapTracker>(emptyTapTracker());

  // A value cell is interactive when EITHER handler is wired. With no edit
  // handler the tap stays exactly as before (immediate anchor); with one, the
  // gesture decides select-vs-edit.
  const valueCellsInteractive = onSelectCell !== undefined || onEditCell !== undefined;

  // The inline input needs ALL three handlers; a half-wired host falls back to
  // a plain read-only cell rather than a dead editor.
  const editorWired =
    onEditDraftChange !== undefined &&
    onEditCommit !== undefined &&
    onEditCancel !== undefined;

  // Bundle them once (narrowed), so the per-cell render stays a plain
  // `object | null` check instead of re-testing optional props inside a loop.
  const editorHandlers = editorWired
    ? { onChange: onEditDraftChange, onCommit: onEditCommit, onCancel: onEditCancel }
    : null;

  function handleValueCellTap(cell: SheetCellTap, now: number): void {
    if (onEditCell === undefined) {
      onSelectCell?.(cell);
      return;
    }
    const { decision, tracker } = decideCellTap(
      tapTrackerRef.current,
      cell,
      now,
    );
    tapTrackerRef.current = tracker;
    if (decision.kind === 'edit') {
      onEditCell(decision.cell);
    } else {
      onSelectCell?.(cell);
    }
  }

  return (
    <div className="sheet-scroll">
      <table
        className={`sheet-grid${valueCellsInteractive ? ' sheet-grid--selectable' : ''}`}
        aria-label="Trial ratings sheet"
      >
        <thead>
          <tr>
            {/* Corner: sticky in both directions, z-index above the other header cells. */}
            <th
              rowSpan={2}
              scope="col"
              className="sheet-col--key sheet-head--corner"
            >
              Plot (Sub)
            </th>
            {header.runs.map((run, index) => (
              <th
                key={`${run.partRated ?? ''}-${index}`}
                colSpan={run.span}
                scope="colgroup"
                className="sheet-head--part"
              >
                {run.label}
              </th>
            ))}
          </tr>
          <tr>
            {header.pests.map((pest) => (
              <th
                key={pest.traitOrdinal}
                scope="col"
                className="sheet-head--pest"
                title={`Column ${pest.column}`}
              >
                {pest.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            // N3 current cell — match on the row identity the grid renders
            // (`SheetRow.row`); the highlight itself was resolved from
            // plot + subsample in `lib/sheetTarget.ts`.
            const hl = highlight?.row === row.row ? highlight : null;
            // Write anchor — SAME identity rule as `hl`, independently: the
            // two channels can point at different cells at once, so neither
            // suppresses the other.
            const sel = selected?.row === row.row ? selected : null;
            const rowClassName = [
              hl !== null ? 'sheet-row--current' : null,
              sel !== null ? 'sheet-row--selected' : null,
            ]
              .filter((name) => name !== null)
              .join(' ');
            return (
              <tr
                key={row.row}
                className={rowClassName === '' ? undefined : rowClassName}
              >
                {/* Column B composite key — structurally read-only (D5 channel 1). */}
                <th scope="row" className="sheet-grid__key sheet-col--key">
                  {row.plot} {row.subsample}
                </th>
                {columns.map((column, index) => {
                  // Contract invariant: values[i] ↔ columns[i]. `?? null` keeps a
                  // short row blank instead of rendering `undefined`.
                  const value = row.values[index] ?? null;
                  // The tapped cell (only inside the selected row): the same
                  // `valuesIndex` the host resolved via `targetSelection`.
                  const cellSelected = sel !== null && sel.valuesIndex === index;
                  // The open editor targets this cell (row cross-ref + column,
                  // the same identity rule the host used to resolve it).
                  const isEditing =
                    editorWired &&
                    editing != null &&
                    editing.row === row.row &&
                    editing.valuesIndex === index;
                  const cellClassName = [
                    'sheet-col--value',
                    sheetCellState(value),
                    cellSelected ? 'sheet-cell--selected' : null,
                    isEditing ? 'sheet-cell--editing' : null,
                  ]
                    .filter((name) => name !== null)
                    .join(' ');
                  const cell: SheetCellTap = {
                    plot: row.plot,
                    subsample: row.subsample,
                    valuesIndex: index,
                  };
                  return (
                    <td
                      key={column.traitOrdinal}
                      className={cellClassName}
                      data-selected={cellSelected ? 'true' : undefined}
                      data-editing={isEditing ? 'true' : undefined}
                      // No tap handling while THIS cell is open for editing —
                      // the input owns the pointer, and a tap inside it must
                      // not restart the gesture on the cell underneath.
                      onClick={
                        valueCellsInteractive && !isEditing
                          ? (event) => handleValueCellTap(cell, event.timeStamp)
                          : undefined
                      }
                    >
                      {isEditing && editorHandlers !== null ? (
                        <SheetCellEditor
                          value={editDraft ?? ''}
                          onChange={editorHandlers.onChange}
                          onCommit={editorHandlers.onCommit}
                          onCancel={editorHandlers.onCancel}
                          disabled={editSaving ?? false}
                          label={`Edit plot ${row.plot} subsample ${row.subsample}, column ${column.column}`}
                        />
                      ) : (
                        sheetCellValue(value)
                      )}
                    </td>
                  );
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
