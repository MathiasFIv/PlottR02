/**
 * Write anchor (tap-to-anchor) — pure, Node-safe, no React.
 *
 * Tapping a cell in the read-only grid (`components/SheetGrid.tsx`) stores a
 * ROW-level `SheetSelected` anchor (`store/atoms.ts#sheetSelectedAtom`);
 * speaking numbers then fills that row's first empty cell and the ones to its
 * right within the current category block, because the sync engine freezes
 * the anchor onto the record at first send (`lib/syncQueue.ts`) WITHOUT a
 * column pin — the server scans forward from column C (`TraitColumn(1)`), so
 * a tap on ANY cell of a row means "write into this ROW, starting at its
 * first empty cell". The BACKEND already supports this
 * (`AppendToCategoryBlock` scanning for the first free cell) — this module is
 * the client half: the trial guard, the display string, and the wire mapping.
 *
 * Three rules the whole feature hangs on:
 * - **Trial guard first.** Plot numbers repeat freely across workbooks, so an
 *   anchor is only ever usable for the trial it was tapped on
 *   (`isAnchorForTrial`).
 * - **The anchor never advances anything.** It is an INTENT, not a fact:
 *   `sheetLastWrittenAtom` + `deriveNextTarget` stay the only sources of the
 *   auto-continue cursor (`lib/nextTarget.ts`), and this module never writes
 *   to either.
 * - **The anchor is one-shot.** `lib/syncQueue.ts` reads the anchor at first
 *   send and freezes it; after the first successful write the anchor
 *   drove, the engine CLEARS it (a tap arms exactly one run). A tap on the
 *   anchored row before that is still a UI clear gesture.
 */

import type { SheetSelected } from '../store/atoms';

/**
 * The anchor for `trialId`, or null when either side does not match: no
 * anchor, no trial to check against, or an anchor tapped on a DIFFERENT
 * trial (plot numbers repeat, so this is the only safe comparison). The
 * single guard both hosts and the sync engine use before touching the grid.
 */
export function isAnchorForTrial(
  anchor: SheetSelected | null,
  trialId: string | null,
): SheetSelected | null {
  if (anchor === null || trialId === null) return null;
  return anchor.trialId === trialId ? anchor : null;
}

/**
 * U+00B7 MIDDLE DOT — the separator the codebase already uses
 * (`SheetStatusPill`, the `Next:` / `End of sheet` lines). Written as an
 * escape so the output codepoint is identical no matter how the source file
 * is (mis)decoded — never a hyphen, never a mangling-dependent glyph.
 */
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
 * The shape `lib/syncQueue.ts` freezes onto the record at first send —
 * `{ defaultTarget }`, consumed by `lib/api.ts`'s FormData builder
 * (`defaultPlot`/`defaultSubsample`). NO `traitOrdinal` is sent: the tap is
 * row-only, and the server scanning from column C is exactly what makes the
 * write land on the row's first empty cell.
 *
 * WHY the anchor rides `defaultPlot`/`defaultSubsample` (and not a confirmed
 * `plot`/`subsample`):
 * - the default-target chain already means **"use this when the transcript
 *   omits it"**, which is exactly what a tap expresses — "speak numbers, put
 *   them HERE"; an explicitly spoken plot/subsample still wins, because the
 *   whole defaults branch is gated on both fields being absent;
 * - `usedDefaultTarget` (the server's flag for a defaulted resolve) is what
 *   RELAXES the confidence review gate. Sending the tap as a *confirmed*
 *   `plot`/`subsample` pair would claim the operator said it out loud, so a
 *   bare low-confidence `"5, 6"` would be forced into correction review on
 *   every utterance — the exact opposite of the anchor's purpose.
 */
export function anchorToWire(anchor: SheetSelected): {
  defaultTarget: { plot: number; subsample: number };
} {
  return {
    defaultTarget: { plot: anchor.plot, subsample: anchor.subsample },
  };
}

/**
 * The tap payload `components/SheetGrid.tsx` hands `onSelectCell` — the one
 * shape both hosts (`components/SheetPanel.tsx`, `views/SheetView.tsx`)
 * receive and `decideAnchorTap` consumes. The ROW decides the write, so the
 * anchor never pins a column on the wire; `valuesIndex` carries the tapped
 * cell's column purely for the grid's anchor highlight.
 */
export type SheetCellTap = {
  plot: number;
  subsample: number;
  valuesIndex: number;
};

/**
 * The outcome of one tap, decided purely — the HOST applies it:
 * - `clear` — a cell of the CURRENT anchor's row: tap it again to release.
 * - `set` — any other row: make that row the anchor, carrying the tapped
 *   cell's coords so the grid highlights it. Tapping a cell of a fully-filled
 *   row still selects the row (an honest write then reports "already filled"
 *   via the sync banner — the tap is never destructive).
 */
export type AnchorDecision =
  | { kind: 'clear' }
  | { kind: 'set'; anchor: SheetSelected };

/**
 * The whole tap-to-anchor decision in one pure, Node-testable place — the
 * shared core both grid hosts call, so the two can never drift.
 *
 * `current` is the anchor ALREADY filtered by the caller's
 * `isAnchorForTrial(anchor, trialId)` (trial identity is therefore assumed,
 * not re-checked), and `trialId` is the caller's own trial (the `SheetView`
 * host guards `trialId === undefined` before calling).
 *
 * Rules, in order:
 * 1. `current` matches this cell's ROW (plot + subsample) → `{ kind: 'clear' }`
 *    — tapping the anchored row again releases it;
 * 2. otherwise → `{ kind: 'set', anchor: { trialId, plot, subsample, valuesIndex } }` —
 *    the tapped ROW becomes the anchor (filled rows included; the row's
 *    values are deliberately NOT consulted) and the tapped cell's column
 *    rides along so the grid can highlight it.
 */
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
