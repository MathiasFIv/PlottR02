/**
 * File-derived "next target" — pure, Node-safe, no React.
 *
 * The persisted store (`lib/defaultTarget.ts`) holds the LAST WRITTEN
 * plot/subsample pair plus `blockFull` (whether that write filled the
 * category block it landed in); the pair the run continues at is derived
 * HERE, at read time, from the sheet payload's `rows[]` (array order =
 * workbook order):
 *
 * - **Block not full → STAY**: return the last-written row itself, so the
 *   next values-only clip appends to the SAME subsample's block (the server
 *   writes into the first empty cell of the row's current block).
 * - **Block full → advance within the plot**: return the NEXT row in
 *   `rows[]` order, but ONLY when it belongs to the SAME plot. A plot end
 *   (next row is a different plot, or there is no next row) yields `null` —
 *   no cross-plot wrap, no invented pair. The caller renders nothing.
 *
 * Why derivation instead of `subsample + 1`: the workbook decides what comes
 * next. Every plot has its own subsample count, so arithmetic invents pairs
 * the file does not contain — the old `+ 1` turned `101 4` (the last
 * subsample of plot 101) into a phantom `101 5` that highlighted nothing,
 * rendered a lie, and went out on the wire. Derivation instead gives
 * `101 1 → 101 2` (the by-subsample advance) and `null` at the end of a
 * plot.
 *
 * Matching rules — deliberately narrow:
 * - **Identity only**: `plot` + `subsample` equality against `SheetRow`.
 *   Never `subsample + 1`, never Excel-row arithmetic (`SheetRow.row` is a
 *   cross-reference, NOT an order/match key — array order decides), never a
 *   read of `values[]` (Rule 2: the value channel is `lib/sheetCell.ts`'s).
 * - **`null` means "no derivable next"**: no `from`, `from` not present in
 *   `rows`, empty `rows`, or the end of a plot. A caller that gets `null`
 *   must render NOTHING (never a guessed pair).
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
 * The row the run continues at, or null when there is none.
 *
 * Pure: identity match on `plot` + `subsample` only.
 * - `from` not in `rows` (legacy/stale/foreign pair) → null;
 * - `from === null` → null;
 * - `blockFull` false → STAY, returning `from` itself (the next values-only
 *   clip appends to the same subsample's block);
 * - `blockFull` true → the next row in array order ONLY when it shares
 *   `from`'s plot; a plot end or end of sheet → null (honest stop, never a
 *   wrap).
 */
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
  if (nextRow === undefined || nextRow.plot !== from.plot) return null;
  return { plot: nextRow.plot, subsample: nextRow.subsample };
}

/**
 * The last-written pair when it is the TRUE last row of the sheet (array
 * end), else null. `deriveNextTarget` already yields `null` at a mid-array
 * plot end too (no next row in the same plot), but this marker fires ONLY at
 * the array end — a mid-array plot end is simply "no next", not "end of
 * sheet".
 */
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
 * The last-written pair when it sits at the END of its PLOT (block full) and
 * the run therefore has NOTHING LEFT TO WRITE — else null.
 *
 * `deriveNextTarget` already returns `null` at a plot boundary (no wrap, no
 * invented pair), but `null` alone is silent: the operator cannot tell "the
 * block is still open, keep talking" (STAY) from "this plot is done". This
 * marker makes the honest stop visible: `blockFull` true AND no next row in
 * the SAME plot (array end, or the next row is a different plot).
 *
 * A plot end that is ALSO the true sheet end returns the pair here too — the
 * caller renders "End of sheet" (`deriveEndOfSheet`) FIRST and suppresses this
 * one, so the two never compete.
 */
export function deriveEndOfPlot(
  rows: SheetRow[] | null,
  lastWritten: { plot: number; subsample: number } | null,
  blockFull: boolean,
): { plot: number; subsample: number } | null {
  if (rows === null || lastWritten === null || !blockFull) return null;
  const index = rows.findIndex(
    (r) => r.plot === lastWritten.plot && r.subsample === lastWritten.subsample,
  );
  if (index === -1) return null;
  const nextRow = rows[index + 1];
  if (nextRow !== undefined && nextRow.plot === lastWritten.plot) return null;
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
 * rows available (`rowsForTrial`) and the stored last-written memory (pair +
 * `blockFull`). Any failure — missing payload, foreign atom, throwing
 * storage, stale pair — collapses to null. Never throws.
 */
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
