import { sheetCellState, sheetCellValue } from '../lib/sheetCell';
import { buildSheetHeader } from '../lib/sheetHeader';
import type { SheetHighlight } from '../lib/sheetTarget';
import type { SheetColumn, SheetRow } from '../types';
import './SheetGrid.css';

type SheetGridProps = {
  columns: SheetColumn[];
  rows: SheetRow[];
  /**
   * N3 — the unified target's current-cell highlight (`lib/sheetTarget.ts`).
   * The hosts derive it from the last-written atom (`sheetLastWrittenAtom`)
   * + the payload on every render — `deriveNextTarget` walks `rows[]` to the
   * next row, `targetHighlight` maps it here; `row` matches the row's
   * `SheetRow.row`, `valuesIndex` is the exact value cell (null = whole-row
   * tint).
   */
  highlight?: SheetHighlight | null;
  /**
   * Write anchor — the TAPPED empty cell, mapped by the hosts through
   * `targetSelection` (`lib/sheetTarget.ts`) against the current payload.
   * Same render shape as `highlight` but always the exact cell. Renders
   * `.sheet-cell--selected` (+ `data-selected="true"`) and the
   * `.sheet-row--selected` row tint — INDEPENDENT of `highlight`, so the
   * anchor (strong accent + pulse) and the auto-continue cursor (soft amber)
   * can coexist and stay visually distinct.
   */
  selected?: { row: number; valuesIndex: number } | null;
  /**
   * Tap-to-anchor: fired for VALUE cells only (never the sticky key column,
   * never headers), carrying everything the host needs to accept or refuse
   * the tap — identity (`plot` + `subsample`), the column
   * (`traitOrdinal`/`valuesIndex`) and the current `value` (a filled cell is
   * REFUSED by the hosts; `null` = empty = anchorable).
   */
  onSelectCell?: (cell: {
    plot: number;
    subsample: number;
    traitOrdinal: number;
    valuesIndex: number;
    value: number | null;
  }) => void;
  /**
   * N3/N4 seam — cell addresses currently flashing. Accepted, NOT rendered
   * (N4 owns `flashCellsAtom` + the `.cell--flash` keyframes).
   */
  flashes?: readonly string[] | null;
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
 * `.sheet-row--current` and, when it carries a `valuesIndex`, that one value
 * cell with `.sheet-cell--current`. Background-only (see `index.css`), so no
 * box model changes and no new horizontal scroll at 360–390 px; the value
 * render path (`sheetCellValue` / `sheetCellState`) is untouched, so a
 * highlighted `0` still renders `0` and an empty cell still renders blank.
 * `flashes` stays accepted-but-unrendered (N4).
 *
 * **Write anchor:** `selected` styles the tapped cell
 * (`.sheet-cell--selected` + `data-selected="true"`, `.sheet-row--selected`
 * row tint) and `onSelectCell` handles taps on VALUE `<td>`s only. Both are
 * derived from the row's `SheetRow` and the column's `SheetColumn` — never
 * from Excel letters or `SheetRow.row` arithmetic — and the anchor channel
 * renders independently of `highlight` (the two can point at different cells
 * at once).
 */
export function SheetGrid({
  columns,
  rows,
  highlight,
  selected,
  onSelectCell,
}: SheetGridProps) {
  const header = buildSheetHeader(columns);

  return (
    <div className="sheet-scroll">
      <table
        className={`sheet-grid${onSelectCell ? ' sheet-grid--selectable' : ''}`}
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
                  // Anchor cell = the tapped row AND the tapped column.
                  const isSelected =
                    sel !== null && sel.valuesIndex === index;
                  return (
                    <td
                      key={column.traitOrdinal}
                      className={`sheet-col--value ${sheetCellState(value)}${hl?.valuesIndex === index ? ' sheet-cell--current' : ''}${isSelected ? ' sheet-cell--selected' : ''}`}
                      data-selected={isSelected ? 'true' : undefined}
                      onClick={
                        onSelectCell !== undefined
                          ? () =>
                              onSelectCell({
                                plot: row.plot,
                                subsample: row.subsample,
                                traitOrdinal: column.traitOrdinal,
                                valuesIndex: index,
                                value,
                              })
                          : undefined
                      }
                    >
                      {sheetCellValue(value)}
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
