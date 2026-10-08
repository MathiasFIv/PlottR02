/**
 * Current-cell target (N3) — "the cell we are in": where the voice recorder
 * is positioned / will write next, rendered as the read-only grid's
 * current-cell highlight (row tint + the exact value cell).
 *
 * Two halves:
 * - `targetHighlight` — pure: maps a `{plot, subsample, traitOrdinal?}` target
 *   onto the grid's `{row, valuesIndex}` render shape against the CURRENT
 *   payload. The hosts call it during render (never memoized), so a payload
 *   replacement (refresh / snapshot hydrate / fast-path) is re-derived for
 *   free and a route change can never strand a stale highlight.
 * - `advanceSheetTarget` / `rehydrateSheetTarget` — the write-through seam:
 *   `lib/syncQueue.ts` records on `status === 'Written'`, the hosts rehydrate
 *   on mount / trial change. Both move the LAST-WRITTEN pair (never a
 *   computed "+1"): `lib/defaultTarget.ts` owns persistence and
 *   `sheetLastWrittenAtom` mirrors what was just written. The NEXT target —
 *   what the grid actually highlights — is derived from that pair plus the
 *   payload by `deriveNextTarget` (`lib/nextTarget.ts`) in the hosts.
 *
 * Matching rules — both are identity matches, never arithmetic:
 * - **ROW**: `rows.findIndex(r => r.plot === target.plot && r.subsample ===
 *   target.subsample)`. `SheetRow.Row` is only the Excel cross-ref and is
 *   NEVER a match key. The highlight carries the matched row's `row` (the
 *   cross-ref `SheetGrid` compares each `<tr>` against), so the tint lands on
 *   the matched row; not found → `null` (no wrap, no nearest-row invention).
 * - **COLUMN**: `columns.findIndex(c => c.traitOrdinal === target.traitOrdinal)`
 *   — that array index IS `valuesIndex`, because the contract is `values[i]`
 *   belongs to `columns[i]` and `TraitOrdinal` need not equal `index + 1`.
 *   NEVER `ordinal - 1`, NEVER Excel-letter math. `values[]` is never read
 *   here — `lib/sheetCell.ts` owns the value channel (Rule 2: empty ≠ 0).
 *
 * `traitOrdinal` is optional everywhere: absent, unknown, or out-of-range
 * degrades to a whole-row tint (`valuesIndex: null`) instead of throwing.
 *
 * Non-React writes use `getDefaultStore()` — the same precedent as
 * `recordingsNeedingAttentionCountAtom` / `sheetRefreshTickAtom` in
 * `lib/syncQueue.ts`; `main.tsx` mounts a fixed `<Provider
 * store={getDefaultStore()}>`, so those writes reach React.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom } from '../store/atoms';
import type { SheetLastWritten } from '../store/atoms';
import type { SheetColumn, SheetRow } from '../types';
import { readDefaultTarget, rememberWrittenTarget } from './defaultTarget';

/**
 * The grid's render shape for the current cell:
 * - `row` — the `SheetRow.row` of the row to tint (what `SheetGrid` compares
 *   against, `highlight?.row === row.row`);
 * - `valuesIndex` — index into `SheetRow.values` / `columns` for the exact
 *   cell, or `null` for a whole-row tint (no/unknown trait column).
 */
export type SheetHighlight = { row: number; valuesIndex: number | null };

/**
 * Resolves the current cell against the payload on screen, or null when the
 * target is null or its plot+subsample is not in this payload. Pure — never
 * reads `values[]`, never throws, never invents a row.
 */
export function targetHighlight(
  target: { plot: number; subsample: number; traitOrdinal?: number } | null,
  columns: SheetColumn[],
  rows: SheetRow[],
): SheetHighlight | null {
  if (target === null) return null;

  // Row = plot + subsample identity (the Excel `Row` cross-ref is never a key).
  const rowIndex = rows.findIndex(
    (r) => r.plot === target.plot && r.subsample === target.subsample,
  );
  if (rowIndex === -1) return null;
  const row = rows[rowIndex].row;

  // No trait anchor → row tint; an unknown ordinal degrades the same way.
  if (typeof target.traitOrdinal !== 'number') return { row, valuesIndex: null };
  const valuesIndex = columns.findIndex((c) => c.traitOrdinal === target.traitOrdinal);
  if (valuesIndex === -1) return { row, valuesIndex: null };

  return { row, valuesIndex };
}

/**
 * Re-reads the persisted last-written target for a trial into
 * `sheetLastWrittenAtom` (mount / trial change). Missing or unreadable
 * storage → the atom is set to null, so a trial with nothing written can
 * never show a leftover cell. Note the atom is seeded with the pair AS WRITTEN
 * — the hosts derive the highlighted (next) row from it via
 * `deriveNextTarget`, never from it directly.
 * `traitOrdinal` is intentionally absent here: persistence holds the
 * plot/subsample pair only (`DefaultTarget`), so rehydration is a row tint
 * until the next write seeds the column again.
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
        : { trialId, plot: remembered.plot, subsample: remembered.subsample },
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
 * `rememberWrittenTarget` runs FIRST — it owns persistence, so the store
 * holds the written pair exactly as the server resolved it (NO `+ 1`: the
 * next target is derived from the sheet payload at read time). Then the atom
 * mirrors the SAME written pair (`plot`, `subsample`), tagged with this
 * `trialId` and with `traitOrdinal` only when present. Invalid
 * plot/subsample leaves BOTH the store and the atom alone (same no-op rule as
 * `rememberWrittenTarget`).
 */
export function advanceSheetTarget(
  trialId: string,
  result: {
    plot?: number | null;
    subsample?: number | null;
    traitOrdinal?: number | null;
  },
  storage?: Pick<Storage, 'getItem' | 'setItem'>,
): void {
  rememberWrittenTarget(trialId, result, storage);

  const { plot, subsample } = result;
  if (!isPositiveInt(plot) || !isPositiveInt(subsample)) return;

  const written: SheetLastWritten = { trialId, plot, subsample };
  if (typeof result.traitOrdinal === 'number') written.traitOrdinal = result.traitOrdinal;
  getDefaultStore().set(sheetLastWrittenAtom, written);
}
