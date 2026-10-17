/**
 * Current-cell target (N3) — "the cell we are in": where the voice recorder
 * is positioned / will write next, rendered as the read-only grid's
 * current-cell highlight (row tint).
 *
 * Three pure helpers (current-cell highlight, tapped-cell highlight,
 * write-through seam):
 * - `targetHighlight` — pure: maps a `{plot, subsample}` target onto the
 *   grid's `{row}` render shape against the CURRENT payload. The hosts call
 *   it during render (never memoized), so a payload replacement (refresh /
 *   snapshot hydrate / fast-path) is re-derived for free and a route change
 *   can never strand a stale highlight. Row-only by design: the write cursor
 *   is a ROW (the tap is row-only and the derive carries no column), so the
 *   exact cell is deliberately not pinned.
 * - `advanceSheetTarget` / `rehydrateSheetTarget` — the write-through seam:
 *   `lib/syncQueue.ts` records on `status === 'Written'`, the hosts rehydrate
 *   on mount / trial change. Both move the LAST-WRITTEN pair plus its
 *   `blockFull` flag (never a computed "+1"): `lib/defaultTarget.ts` owns
 *   persistence and `sheetLastWrittenAtom` mirrors what was just written. The
 *   NEXT target — what the grid actually highlights — is derived from that
 *   memory plus the payload by `deriveNextTarget` (`lib/nextTarget.ts`) in
 *   the hosts.
 *
 * Matching rules — identity matches, never arithmetic:
 * - **ROW**: `rows.findIndex(r => r.plot === target.plot && r.subsample ===
 *   target.subsample)`. `SheetRow.Row` is only the Excel cross-ref and is
 *   NEVER a match key. The highlight carries the matched row's `row` (the
 *   cross-ref `SheetGrid` compares each `<tr>` against), so the tint lands on
 *   the matched row; not found → `null` (no wrap, no nearest-row invention).
 *
 * Non-React writes use `getDefaultStore()` — the same precedent as
 * `recordingsNeedingAttentionCountAtom` / `sheetRefreshTickAtom` in
 * `lib/syncQueue.ts`; `main.tsx` mounts a fixed `<Provider
 * store={getDefaultStore()}>`, so those writes reach React.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom, trialSheetAtom } from '../store/atoms';
import type { SheetColumn, SheetRow } from '../types';
import { readDefaultTarget, rememberWrittenTarget } from './defaultTarget';
import { buildSheetBlocks } from './sheetHeader';
import { readSheetSnapshot } from './sheetSnapshot';

/**
 * The grid's render shape for the current cell / write anchor:
 * `row` — the `SheetRow.row` of the row to tint (what `SheetGrid` compares
 * against, `highlight?.row === row.row`). Row-only: no column pin.
 */
export type SheetHighlight = { row: number };

/**
 * Resolves the target's row against the payload on screen, or null when the
 * target is null or its plot+subsample is not in this payload. Pure — never
 * reads `values[]`, never throws, never invents a row.
 */
export function targetHighlight(
  target: { plot: number; subsample: number } | null,
  rows: SheetRow[],
): SheetHighlight | null {
  if (target === null) return null;

  // Row = plot + subsample identity (the Excel `Row` cross-ref is never a key).
  const rowIndex = rows.findIndex(
    (r) => r.plot === target.plot && r.subsample === target.subsample,
  );
  if (rowIndex === -1) return null;
  return { row: rows[rowIndex].row };
}

/**
 * The grid's render shape for the tapped anchor CELL: `row` — the
 * `SheetRow.row` to tint (matched by plot+subsample identity, exactly like
 * `targetHighlight`) — plus `valuesIndex`, the 0-based column index of the
 * tapped cell (`SheetRow.values[valuesIndex]`), so `SheetGrid` can paint the
 * strong per-cell highlight.
 */
export type SheetCellSelection = { row: number; valuesIndex: number };

/**
 * Resolves the tapped anchor onto the payload on screen: the matched row's
 * `row` cross-ref plus the anchor's `valuesIndex`, or null when there is no
 * anchor or its plot+subsample is not in this payload. Pure — the row is
 * matched by identity (never `Row`), and the cells' own values are never read.
 */
export function targetSelection(
  target: { plot: number; subsample: number; valuesIndex: number } | null,
  rows: SheetRow[],
): SheetCellSelection | null {
  if (target === null) return null;

  const rowIndex = rows.findIndex(
    (r) => r.plot === target.plot && r.subsample === target.subsample,
  );
  if (rowIndex === -1) return null;
  return { row: rows[rowIndex].row, valuesIndex: target.valuesIndex };
}

/** One written cell of a sync result — only the column ordinal matters here. */
type WrittenCell = { traitOrdinal: number };

/**
 * Whether a set of written cells FILLED the category block they landed in:
 * true when the highest written traitOrdinal reaches the END of its block
 * (blocks = consecutive equal `partRated` runs, `buildSheetBlocks` — never a
 * hardcoded 3). False when there is nothing to judge (no cells, no columns,
 * or an ordinal outside every block) — the conservative "not full" keeps the
 * cursor on the row and re-targets the SAME subsample (the next write lands
 * on that row's first empty cell). It is NOT harmless: see the edge below.
 *
 * Judgment is by the MAX written ordinal, which is exact for the normal
 * forward-fill case but has ONE edge: a workbook whose block's LAST cell was
 * already occupied before this write (so the write stops short of it) is
 * misjudged as "not full" → STAY — and because the server appends at the
 * row's next free cell (the NEXT block of the SAME subsample) rather than
 * reporting "already filled", the run silently lags by one subsample. The
 * robust fix is a server-computed `blockFull` on the `Written` response;
 * until then the client derives it from `writtenCells` as best it can.
 *
 * Exported for tests; `advanceSheetTarget` is the production caller.
 */
export function deriveBlockFull(
  writtenCells: readonly WrittenCell[],
  columns: SheetColumn[],
): boolean {
  const ordinals = writtenCells
    .map((cell) => cell.traitOrdinal)
    .filter((n) => Number.isInteger(n) && n > 0);
  if (ordinals.length === 0 || columns.length === 0) return false;
  const maxOrdinal = Math.max(...ordinals);
  const block = buildSheetBlocks(columns).find(
    (b) => maxOrdinal >= b.start && maxOrdinal <= b.end,
  );
  return block !== undefined && maxOrdinal >= block.end;
}

/**
 * The payload's columns for a trial, or null when none can be trusted: the
 * live atom first (only when it holds a payload AND belongs to this trial — a
 * payload left behind by another trial must never decide this trial's block
 * size), else the persisted snapshot for this trial, else null. Mirrors
 * `rowsForTrial` (`lib/nextTarget.ts`): without the snapshot fallback, a write
 * that runs before the payload atom is loaded would judge every block as
 * unfilled (STAY) and the run would lag one subsample. Used only to compute
 * `blockFull` at write time.
 */
function columnsForTrial(
  trialId: string,
  storage?: Pick<Storage, 'getItem' | 'setItem'>,
): SheetColumn[] | null {
  try {
    const payload = getDefaultStore().get(trialSheetAtom);
    if (payload !== null && payload.trialId === trialId && payload.columns !== null) {
      return payload.columns;
    }
    return readSheetSnapshot(trialId, storage)?.sheet.columns ?? null;
  } catch {
    return null;
  }
}

/**
 * Re-reads the persisted last-written memory for a trial into
 * `sheetLastWrittenAtom` (mount / trial change). Missing or unreadable
 * storage → the atom is set to null, so a trial with nothing written can
 * never show a leftover row. Note the atom is seeded with the pair AS
 * WRITTEN plus its `blockFull` flag — the hosts derive the highlighted
 * (next) row from it via `deriveNextTarget`, never from it directly.
 */
export function rehydrateSheetTarget(
  trialId: string,
  storage?: Pick<Storage, 'getItem' | 'setItem'>,
): void {
  const store = getDefaultStore();
  try {
    const remembered = readDefaultTarget(trialId, storage);
    store.set(
      sheetLastWrittenAtom,
      remembered === null
        ? null
        : {
            trialId,
            plot: remembered.plot,
            subsample: remembered.subsample,
            blockFull: remembered.blockFull,
          },
    );
  } catch {
    // Defensive: the reader never throws today, but a highlight must never be
    // the reason a host crashes — degrade to "no current cell".
    store.set(sheetLastWrittenAtom, null);
  }
}

/** Same positive-integer guard `lib/defaultTarget.ts` applies before writing. */
function isPositiveInt(n: unknown): n is number {
  return typeof n === 'number' && Number.isInteger(n) && n > 0;
}

/**
 * Records a successful write (`status === 'Written'`).
 *
 * Computes `blockFull` from the response's `writtenCells` against the live
 * payload's `columns` (trial-guarded; unavailable → false = conservative
 * STAY), then `rememberWrittenTarget` runs FIRST — it owns persistence, so
 * the store holds the written pair plus the flag exactly as computed (NO
 * `+ 1`: the next target is derived from the sheet payload at read time).
 * Then the atom mirrors the SAME memory (`plot`, `subsample`, `blockFull`),
 * tagged with this `trialId`. Invalid plot/subsample leaves BOTH the store
 * and the atom alone (same no-op rule as `rememberWrittenTarget`).
 */
export function advanceSheetTarget(
  trialId: string,
  result: {
    plot?: number | null;
    subsample?: number | null;
    writtenCells?: readonly WrittenCell[];
  },
  storage?: Pick<Storage, 'getItem' | 'setItem'>,
): void {
  const { plot, subsample } = result;
  if (!isPositiveInt(plot) || !isPositiveInt(subsample)) return;

  const blockFull = deriveBlockFull(
    result.writtenCells ?? [],
    columnsForTrial(trialId, storage) ?? [],
  );

  rememberWrittenTarget(trialId, { plot, subsample, blockFull }, storage);
  getDefaultStore().set(sheetLastWrittenAtom, {
    trialId,
    plot,
    subsample,
    blockFull,
  });
}
