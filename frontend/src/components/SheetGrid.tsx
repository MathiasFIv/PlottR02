import { sheetCellState, sheetCellValue } from '../lib/sheetCell';
import { buildSheetHeader } from '../lib/sheetHeader';
import type { SheetHighlight } from '../lib/sheetTarget';
import type { SheetColumn, SheetRow } from '../types';

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
 */
export function SheetGrid({ columns, rows, highlight }: SheetGridProps) {
  const header = buildSheetHeader(columns);

  return (
    <div className="sheet-scroll">
      <table className="sheet-grid" aria-label="Trial ratings sheet">
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
            return (
              <tr
                key={row.row}
                className={hl ? 'sheet-row--current' : undefined}
              >
                {/* Column B composite key — structurally read-only (D5 channel 1). */}
                <th scope="row" className="sheet-grid__key sheet-col--key">
                  {row.plot} {row.subsample}
                </th>
                {columns.map((column, index) => {
                  // Contract invariant: values[i] ↔ columns[i]. `?? null` keeps a
                  // short row blank instead of rendering `undefined`.
                  const value = row.values[index] ?? null;
                  return (
                    <td
                      key={column.traitOrdinal}
                      className={`sheet-col--value ${sheetCellState(value)}${hl?.valuesIndex === index ? ' sheet-cell--current' : ''}`}
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
