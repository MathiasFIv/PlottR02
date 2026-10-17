/**
 * Double-tap-to-edit gesture for the sheet grid — pure, Node-safe, no React.
 *
 * A SINGLE tap on a value cell keeps its existing meaning (tap-to-anchor:
 * `components/SheetGrid.tsx` forwards it to `onSelectCell` →
 * `lib/sheetAnchor.ts#decideAnchorTap`). A SECOND tap on the SAME cell within
 * `DOUBLE_TAP_WINDOW_MS` means "edit this cell", and the grid forwards it to
 * `onEditCell` instead — the anchor path is never called for the edit tap, so
 * the gesture can never double-fire the anchor decision.
 *
 * The tracker is deliberately tiny (`{ lastCell, lastAt }`) and lives in a
 * `useRef` inside `SheetGrid`, so the decision — "select or edit?" — is a pure
 * function of the previous tap, this tap and the clock, and is unit-testable
 * under the node-only house setup (no DOM, no timers).
 *
 * Rules:
 * - a tap on a DIFFERENT cell (or a first tap) is always a `select`, and
 *   becomes the new `lastCell`;
 * - a second tap on the SAME cell within the window is an `edit`, and RESETS
 *   the tracker — so a third tap starts a fresh pair (no triple-tap edit);
 * - a tap after the window is a `select` again (the pair restarts).
 *
 * The first tap of a double-tap intentionally ALSO processes as a `select`
 * first (it is a real single tap), so the operator sees the anchor land before
 * the editor opens — the edit tap is what deviates.
 */

import type { SheetCellTap } from './sheetAnchor';

/**
 * How long two taps on the same cell may be apart and still count as a
 * double-tap. 250 ms is the low end of the usual 250–300 ms double-click
 * budget: long enough for a deliberate second tap on a phone, short enough
 * that two separate single taps are not misread as an edit.
 */
export const DOUBLE_TAP_WINDOW_MS = 250;

/** The last tap seen, plus when it happened (ms, from `Date.now()`). */
export type TapTracker = {
  lastCell: SheetCellTap | null;
  lastAt: number;
};

/** A fresh tracker — "no previous tap". */
export function emptyTapTracker(): TapTracker {
  return { lastCell: null, lastAt: 0 };
}

/** What a tap means: keep anchoring, or open the editor. */
export type CellTapDecision =
  | { kind: 'select'; cell: SheetCellTap }
  | { kind: 'edit'; cell: SheetCellTap };

/**
 * Decides one tap against the previous one, returning BOTH the decision and
 * the tracker the caller must store for the next tap (pure — never mutates the
 * argument). `now` is injected (`Date.now()` at the call site) so the timing
 * rule is testable without a fake clock.
 */
export function decideCellTap(
  tracker: TapTracker,
  cell: SheetCellTap,
  now: number,
): { decision: CellTapDecision; tracker: TapTracker } {
  const previous = tracker.lastCell;
  const elapsed = now - tracker.lastAt;
  const isDoubleTap =
    previous !== null &&
    previous.plot === cell.plot &&
    previous.subsample === cell.subsample &&
    previous.valuesIndex === cell.valuesIndex &&
    elapsed >= 0 &&
    elapsed <= DOUBLE_TAP_WINDOW_MS;

  if (isDoubleTap) {
    // Reset: a third tap must start a fresh pair, never chain an edit.
    return { decision: { kind: 'edit', cell }, tracker: emptyTapTracker() };
  }

  return {
    decision: { kind: 'select', cell },
    tracker: { lastCell: cell, lastAt: now },
  };
}
