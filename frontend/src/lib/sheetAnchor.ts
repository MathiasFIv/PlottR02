/**
 * Write anchor (tap-to-anchor) — pure, Node-safe, no React. A tap is a
 * ROW-level intent, not a fact: it is trial-scoped, one-shot, and never
 * advances the auto-continue cursor.
 */

import type { SheetSelected } from '../store/atoms';

/** The anchor only when it was tapped on `trialId` — plot numbers repeat across workbooks. */
export function isAnchorForTrial(
  anchor: SheetSelected | null,
  trialId: string | null,
): SheetSelected | null {
  if (anchor === null || trialId === null) return null;
  return anchor.trialId === trialId ? anchor : null;
}

/** U+00B7 MIDDLE DOT, written as an escape so the codepoint is decode-independent. */
const MIDDLE_DOT = '\u00B7';

/** Human-readable anchor for the status line, e.g. `Plot 101 · Sub 2`. */
export function formatAnchor(anchor: SheetSelected): string {
  return [
    'Plot ',
    String(anchor.plot),
    ' ',
    MIDDLE_DOT,
    ' Sub ',
    String(anchor.subsample),
  ].join('');
}

/**
 * Freezes the anchor as `defaultTarget` (never a confirmed plot/subsample):
 * that keeps `usedDefaultTarget` set, which relaxes the confidence review gate.
 * Row-only — no `traitOrdinal` is sent.
 */
export function anchorToWire(anchor: SheetSelected): {
  defaultTarget: { plot: number; subsample: number };
} {
  return {
    defaultTarget: { plot: anchor.plot, subsample: anchor.subsample },
  };
}

/** Tap payload from the grid; `valuesIndex` only drives the highlight, never the wire. */
export type SheetCellTap = {
  plot: number;
  subsample: number;
  valuesIndex: number;
};

/** `clear` when re-tapping the anchored row, else `set` that row as the anchor. */
export type AnchorDecision =
  | { kind: 'clear' }
  | { kind: 'set'; anchor: SheetSelected };

/** Assumes `current` was already trial-filtered via `isAnchorForTrial` by the caller. */
export function decideAnchorTap(
  cell: SheetCellTap,
  current: SheetSelected | null,
  trialId: string,
): AnchorDecision {
  if (
    current !== null &&
    current.plot === cell.plot &&
    current.subsample === cell.subsample
  ) {
    return { kind: 'clear' };
  }
  return {
    kind: 'set',
    anchor: {
      trialId,
      plot: cell.plot,
      subsample: cell.subsample,
      valuesIndex: cell.valuesIndex,
    },
  };
}
