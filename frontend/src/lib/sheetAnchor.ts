/**
 * Write anchor (tap-to-anchor) — pure, Node-safe, no React.
 *
 * Tapping an EMPTY cell in the read-only grid (`components/SheetGrid.tsx`)
 * stores a `SheetSelected` anchor (`store/atoms.ts#sheetSelectedAtom`);
 * speaking numbers then fills that cell and the ones to its right, because
 * the sync engine freezes the anchor onto the record at first send
 * (`lib/syncQueue.ts`). The BACKEND already supports this
 * (`WritePlotColumnPreserving` with `scanForFreeCells:true`) — this module is
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
 * - **Clearing is a UI gesture.** `lib/syncQueue.ts` reads the anchor at
 *   first send and freezes it; it never clears it.
 * - **A filled cell is refused, never destructive.** `decideAnchorTap`
 *   answers with an intention only; the hosts apply it, so a stray tap on a
 *   filled cell can never destroy the anchor you already had (owner decision:
 *   refuse = NO state change).
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

/** Human-readable anchor for the status line, e.g. `Plot 101 · Sub 2 · Col 3`. */
export function formatAnchor(anchor: SheetSelected): string {
  return [
    'Plot ',
    String(anchor.plot),
    ' ',
    MIDDLE_DOT,
    ' Sub ',
    String(anchor.subsample),
    ' ',
    MIDDLE_DOT,
    ' Col ',
    String(anchor.traitOrdinal),
  ].join('');
}

/**
 * The shape `lib/syncQueue.ts` freezes onto the record at first send —
 * `{ defaultTarget, traitOrdinal }`, both consumed by `lib/api.ts`'s
 * FormData builder (`defaultPlot`/`defaultSubsample` + `traitOrdinal`).
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
 *
 * `traitOrdinal` is NOT a default: the tapped column is an override, so it
 * rides the record's own `traitOrdinal` field and wins over the recorder's
 * trait selector.
 */
export function anchorToWire(anchor: SheetSelected): {
  defaultTarget: { plot: number; subsample: number };
  traitOrdinal: number;
} {
  return {
    defaultTarget: { plot: anchor.plot, subsample: anchor.subsample },
    traitOrdinal: anchor.traitOrdinal,
  };
}

/**
 * The tap payload `components/SheetGrid.tsx` hands `onSelectCell` — the one
 * shape both hosts (`components/SheetPanel.tsx`, `views/SheetView.tsx`)
 * receive and `decideAnchorTap` consumes. Structurally identical to
 * `SheetGrid`'s inline prop type, so a handler taking this stays assignable.
 */
export type SheetCellTap = {
  plot: number;
  subsample: number;
  traitOrdinal: number;
  valuesIndex: number;
  value: number | null;
};

/**
 * The outcome of one tap, decided purely — the HOST applies it:
 * - `refuse-filled` — the cell already holds a value: leave the anchor
 *   exactly as it is (NO anchor change) and raise the `'cell-filled'`
 *   notice. Clearing on a filled tap was the rejected behaviour: one stray
 *   tap would silently destroy a valid anchor.
 * - `clear` — an empty cell that IS the current anchor: tap it again to
 *   release. Clears the notice (an accepted tap retires it).
 * - `set` — an empty cell that is not the current anchor: make it the
 *   anchor. Also clears the notice.
 */
export type AnchorDecision =
  | { kind: 'refuse-filled' }
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
 * Rules, in order (exactly the closed owner decisions):
 * 1. filled cell → `{ kind: 'refuse-filled' }` — refusal, NOT a clear;
 * 2. empty + `current` matches this cell (plot, subsample, traitOrdinal;
 *    `valuesIndex` is derived from the same column and not compared) →
 *    `{ kind: 'clear' }`;
 * 3. empty otherwise → `{ kind: 'set', anchor: { trialId, …cell coords } }`.
 */
export function decideAnchorTap(
  cell: SheetCellTap,
  current: SheetSelected | null,
  trialId: string,
): AnchorDecision {
  if (cell.value !== null) return { kind: 'refuse-filled' };
  if (
    current !== null &&
    current.plot === cell.plot &&
    current.subsample === cell.subsample &&
    current.traitOrdinal === cell.traitOrdinal
  ) {
    return { kind: 'clear' };
  }
  return {
    kind: 'set',
    anchor: {
      trialId,
      plot: cell.plot,
      subsample: cell.subsample,
      traitOrdinal: cell.traitOrdinal,
      valuesIndex: cell.valuesIndex,
    },
  };
}
