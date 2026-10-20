/**
 * Current-cell / write-anchor helpers. Rows are matched by plot+subsample
 * identity, never the Excel `SheetRow.row` cross-ref.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { sheetLastWrittenAtom, trialSheetAtom } from '../store/atoms';
import type { SheetColumn, SheetRow } from '../types';
import { readDefaultTarget, rememberWrittenTarget } from './defaultTarget';
import { buildSheetBlocks } from './sheetHeader';
import { readSheetSnapshot } from './sheetSnapshot';

/** `row` is the matched row's cross-ref (what `SheetGrid` compares against). */
export type SheetHighlight = { row: number };

/** Resolves the target's row against the payload, or null; never invents a row. */
export function targetHighlight(
  target: { plot: number; subsample: number } | null,
  rows: SheetRow[],
): SheetHighlight | null {
  if (target === null) return null;

  const rowIndex = rows.findIndex(
    (r) => r.plot === target.plot && r.subsample === target.subsample,
  );
  if (rowIndex === -1) return null;
  return { row: rows[rowIndex].row };
}

export type SheetCellSelection = { row: number; valuesIndex: number };

/** Resolves the tapped anchor cell (row cross-ref + column) against the payload. */
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

type WrittenCell = { traitOrdinal: number };

/**
 * True when the highest written traitOrdinal reaches the end of its
 * `buildSheetBlocks` block (consecutive equal `partRated` runs). A block whose
 * last cell was already occupied before this write is misjudged as not-full.
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

/** Live atom's columns when they belong to this trial, else the snapshot; null when neither is trustworthy. */
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

/** Seeds `sheetLastWrittenAtom` from the persisted last-written memory (mount / trial change). */
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
    store.set(sheetLastWrittenAtom, null);
  }
}

function isPositiveInt(n: unknown): n is number {
  return typeof n === 'number' && Number.isInteger(n) && n > 0;
}

/**
 * Records a successful write: computes `blockFull`, persists the pair AS
 * WRITTEN (never +1 — the next target is derived from the payload), then
 * mirrors it into `sheetLastWrittenAtom`.
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
