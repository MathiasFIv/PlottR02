/**
 * File-derived "next target" — pure, Node-safe, no React. Derives the pair the
 * run continues at from the payload's `rows[]` (workbook order) plus the stored
 * last-written pair and its `blockFull`: not full → STAY on the same row; full
 * → the NEXT row in array order (crossing plot boundaries); true end of sheet
 * → null. Never `subsample + 1`, never Excel-row arithmetic.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { trialSheetAtom } from '../store/atoms';
import type { SheetRow } from '../types';
import { readDefaultTarget } from './defaultTarget';
import type { DefaultTarget } from './defaultTarget';
import { readSheetSnapshot } from './sheetSnapshot';

type TargetStorage = Pick<Storage, 'getItem' | 'setItem'>;

/** `from` not in `rows`, or at the end of the sheet, yields null. */
export function deriveNextTarget(
  rows: SheetRow[],
  from: { plot: number; subsample: number } | null,
  blockFull: boolean,
): DefaultTarget | null {
  if (from === null) return null;
  const index = rows.findIndex(
    (r) => r.plot === from.plot && r.subsample === from.subsample,
  );
  if (index === -1) return null;
  if (!blockFull) return { plot: from.plot, subsample: from.subsample };
  const nextRow = rows[index + 1];
  if (nextRow === undefined) return null;
  return { plot: nextRow.plot, subsample: nextRow.subsample };
}

/** The last-written pair only when it is the array's final row — names "nothing left to write". */
export function deriveEndOfSheet(
  rows: SheetRow[] | null,
  lastWritten: { plot: number; subsample: number } | null,
): { plot: number; subsample: number } | null {
  if (rows === null || lastWritten === null) return null;
  const index = rows.findIndex(
    (r) => r.plot === lastWritten.plot && r.subsample === lastWritten.subsample,
  );
  if (index === -1) return null;
  if (index + 1 < rows.length) return null;
  return lastWritten;
}

/** Live atom's rows when they belong to this trial, else the snapshot, else null. Never throws. */
export function rowsForTrial(
  trialId: string,
  storage?: TargetStorage,
): SheetRow[] | null {
  try {
    const payload = getDefaultStore().get(trialSheetAtom);
    if (payload !== null && payload.trialId === trialId && payload.rows !== null) {
      return payload.rows;
    }
    return readSheetSnapshot(trialId, storage)?.sheet.rows ?? null;
  } catch {
    return null;
  }
}

/** Derives the next target without a payload in hand, collapsing any failure to null. */
export function resolveNextTarget(
  trialId: string,
  storage?: TargetStorage,
): DefaultTarget | null {
  try {
    const memory = readDefaultTarget(trialId, storage);
    return deriveNextTarget(
      rowsForTrial(trialId, storage) ?? [],
      memory,
      memory?.blockFull ?? false,
    );
  } catch {
    return null;
  }
}
