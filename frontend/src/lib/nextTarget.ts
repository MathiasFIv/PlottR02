/**
 * File-derived "next target" — pure, Node-safe, no React.
 *
 * The persisted store (`lib/defaultTarget.ts`) holds the LAST WRITTEN
 * plot/subsample pair; the pair the run continues at is derived HERE, at read
 * time, from the sheet payload's `rows[]` (array order = workbook order):
 * `deriveNextTarget` finds the last-written row by identity and returns the
 * row that FOLLOWS it in the array.
 *
 * Why derivation instead of `subsample + 1`: the workbook decides what comes
 * next. Every plot has its own subsample count, so arithmetic invents pairs
 * the file does not contain — the old `+ 1` turned `101 4` (the last
 * subsample of plot 101) into a phantom `101 5` that highlighted nothing,
 * rendered a lie, and went out on the wire. Derivation instead gives
 * `101 4 → 102 1` (the auto-continue decision) and `null` at the true end of
 * the sheet.
 *
 * Matching rules — deliberately narrow:
 * - **Identity only**: `plot` + `subsample` equality against `SheetRow`.
 *   Never `subsample + 1`, never Excel-row arithmetic (`SheetRow.row` is a
 *   cross-reference, NOT an order/match key — array order decides), never a
 *   read of `values[]` (Rule 2: the value channel is `lib/sheetCell.ts`'s).
 * - **`null` means "no derivable next"**: no `from`, `from` not present in
 *   `rows`, empty `rows`, or the last row in the array. A caller that gets
 *   `null` must render NOTHING (never a guessed pair).
 *
 * The resolvers below (`rowsForTrial` / `resolveNextTarget`) exist for
 * contexts that hold no payload in hand (the wire snapshot in
 * `lib/syncQueue.ts` / `lib/api.ts`): they try the live payload atom first,
 * then the persisted snapshot (`lib/sheetSnapshot.ts`), and degrade to
 * `null` on ANY storage/atom failure — they never throw.
 */

import { getDefaultStore } from 'jotai/vanilla';
import { trialSheetAtom } from '../store/atoms';
import type { SheetRow } from '../types';
import { readDefaultTarget } from './defaultTarget';
import type { DefaultTarget } from './defaultTarget';
import { readSheetSnapshot } from './sheetSnapshot';

/** The storage surface the resolvers accept (same seam as `lib/defaultTarget.ts`). */
type TargetStorage = Pick<Storage, 'getItem' | 'setItem'>;

/**
 * The row that follows `from` in `rows[]` order, or null when there is none.
 *
 * Pure: identity match on `plot` + `subsample` only. `from` not in `rows`
 * (legacy/stale/foreign pair) → null; `from === null` → null; the last row of
 * the sheet → null (honest end-of-sheet, never a wrap).
 */
export function deriveNextTarget(
  rows: SheetRow[],
  from: { plot: number; subsample: number } | null,
): DefaultTarget | null {
  if (from === null) return null;
  const index = rows.findIndex(
    (r) => r.plot === from.plot && r.subsample === from.subsample,
  );
  if (index === -1 || index + 1 >= rows.length) return null;
  const nextRow = rows[index + 1];
  return { plot: nextRow.plot, subsample: nextRow.subsample };
}

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

/**
 * The sheet rows to derive against for a trial, or null when none can be
 * trusted: the live payload atom first (only when it holds rows AND belongs
 * to this trial — a payload left behind by another trial must never seed
 * this trial's target), else the persisted snapshot for this trial, else
 * null. Never throws.
 */
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
    return null; // a broken atom/snapshot access degrades to "no payload"
  }
}

/**
 * Next target for a trial without a payload in hand: derive from the best
 * rows available (`rowsForTrial`) and the stored last-written pair. Any
 * failure — missing payload, foreign atom, throwing storage, stale pair —
 * collapses to null. Never throws.
 */
export function resolveNextTarget(
  trialId: string,
  storage?: TargetStorage,
): DefaultTarget | null {
  try {
    return deriveNextTarget(
      rowsForTrial(trialId, storage) ?? [],
      readDefaultTarget(trialId, storage),
    );
  } catch {
    return null;
  }
}
