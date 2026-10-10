import { useAtom, useAtomValue } from 'jotai';
import { useEffect } from 'react';
import { useTrialSheet } from '../hooks/useTrialSheet';
import { deriveEndOfSheet, deriveNextTarget } from '../lib/nextTarget';
import { decideAnchorTap, isAnchorForTrial } from '../lib/sheetAnchor';
import type { SheetCellTap } from '../lib/sheetAnchor';
import { isNonArmSheetError } from '../lib/sheetStatus';
import {
  rehydrateSheetTarget,
  targetHighlight,
  targetSelection,
} from '../lib/sheetTarget';
import {
  sheetAnchorNoticeAtom,
  sheetLastWrittenAtom,
  sheetSelectedAtom,
} from '../store/atoms';
import { SheetGrid } from './SheetGrid';
import { SheetMetaStrip } from './SheetMetaStrip';
import { SheetStates } from './SheetStates';
import { SheetStatusPill } from './SheetStatusPill';

type SheetPanelProps = {
  trialId: string;
};

/**
 * The RecordView embed wrapper (D2 / §6): the sheet is visible *while*
 * recording. It owns its whole lifecycle — status pill, metadata strip and
 * the grid — from the `trialId` prop alone, so it never reaches into
 * RecordView's state; switching trials in the picker changes the prop, which
 * clears the sheet atoms, refetches and rehydrates the snapshot.
 *
 * **N2 shows ALL rows** (deliberate transitional state — it validates the
 * phone layout one phase early). N3 narrows this to the focused view
 * (active plot ± 1) and adds the `[This plot | All rows]` toggle: that phase
 * only has to swap the `rows` passed to `SheetGrid` below.
 *
 * The attention badge stays in the mic card; this panel adds nothing but its
 * own status pill, so the two never compete.
 *
 * The N3 current-cell highlight is derived DURING RENDER from
 * `sheetLastWrittenAtom` (the last-written pair, trial-guarded) + the payload
 * on screen: `deriveNextTarget` walks `rows[]` to the row AFTER the written
 * one (crossing plot boundaries, `null` at the end of the sheet) and
 * `targetHighlight` maps it onto the grid, so a refresh or snapshot hydrate
 * re-derives it for free and the persisted pair is re-seeded by the effect
 * below (mount / trial change).
 *
 * **Write anchor (tap-to-anchor):** the grid is tappable through
 * `onSelectCell` below. An EMPTY cell becomes the anchor
 * (`sheetSelectedAtom`, trial-guarded with `isAnchorForTrial` and mapped
 * through `targetSelection` — the same identity rules as the highlight); a
 * FILLED cell is REFUSED with NO state change — the existing anchor (if any)
 * is left exactly as it was and the `'cell-filled'` notice is raised for the
 * mic area (`views/RecordView.tsx` renders it — this panel is
 * a sibling of the mic card, so it never renders the status lines itself).
 * Tapping the currently-selected cell again clears the anchor. The anchor
 * atom is NOT cleared by this component's trial change either — that is the
 * point of declaring it outside the payload block.
 */
export function SheetPanel({ trialId }: SheetPanelProps) {
  const { sheet, status, source, error, updatedAt } = useTrialSheet(trialId);
  /** N3 last-written pair — read live; written by the sync engine (non-React set). */
  const target = useAtomValue(sheetLastWrittenAtom);
  /** Only THIS trial's pair may tint this trial's grid (plot numbers repeat). */
  const forTrial = target?.trialId === trialId ? target : null;
  /** Write anchor — read AND written here (tap gesture); syncQueue only reads it. */
  const [anchor, setAnchor] = useAtom(sheetSelectedAtom);
  /** Tap feedback raised for RecordView's mic area to render. */
  const [, setAnchorNotice] = useAtom(sheetAnchorNoticeAtom);
  /** Only THIS trial's tap may anchor this trial's grid. */
  const forTrialAnchor = isAnchorForTrial(anchor, trialId);

  // Seed / re-seed from the persisted last-written target on mount
  // and on trial change (the atom survives route changes; storage is the
  // source of truth after a reload).
  useEffect(() => {
    rehydrateSheetTarget(trialId);
  }, [trialId]);

  const columns = sheet?.columns ?? null;
  const rows = sheet?.trialId === trialId ? sheet.rows : null;
  // A non-ARM 400 wins over the copy: the server says the sheet is gone.
  // So does a `not-found`: a stale snapshot behind a 404 must render the
  // not-found state, not a grid under a "not found" pill.
  const hasPayload =
    columns !== null &&
    rows !== null &&
    status !== 'not-found' &&
    !isNonArmSheetError(error);

  // Next cell = the row after the last-written one in `rows[]` order
  // (payload-derived — never arithmetic, never a guessed pair).
  const next = rows !== null ? deriveNextTarget(rows, forTrial) : null;
  const endOfSheet = deriveEndOfSheet(rows, forTrial);

  /**
   * Tap-to-anchor. The DECISION is shared with the `views/SheetView.tsx`
   * host in `lib/sheetAnchor.ts#decideAnchorTap` (one grid, two hosts — no
   * duplicated rules); this host only applies it:
   * - FILLED cell → refuse with NO anchor change, plus the `'cell-filled'`
   *   notice (a stray tap must never destroy a valid existing anchor);
   * - empty cell → anchor this trial's tap (it overrides the recorder's
   *   trait selector — the whole point of tapping a specific cell);
   * - the currently-selected cell tapped AGAIN → clear the anchor.
   * The notice is set on a refusal and cleared on an accepted tap.
   * The tap never touches `sheetLastWrittenAtom`: it is an intent, not a
   * written fact, so `Next:` / auto-continue cannot move on a gesture.
   */
  function handleSelectCell(cell: SheetCellTap): void {
    const decision = decideAnchorTap(cell, forTrialAnchor, trialId);
    if (decision.kind === 'refuse-filled') {
      // Refusal only — `setAnchor` is deliberately NOT called here.
      setAnchorNotice({ trialId, kind: 'cell-filled' });
      return;
    }
    setAnchorNotice(null); // an accepted tap retires any refusal notice
    setAnchor(decision.kind === 'clear' ? null : decision.anchor);
  }

  return (
    <div className="flex flex-col gap-3 min-w-0 sheet:flex-1 sheet:min-h-0">
      <div className="flex items-center gap-3 flex-wrap">
        <h2 className="text-lg font-semibold text-base-content mr-auto">
          Sheet
        </h2>
        <SheetStatusPill status={status} source={source} savedAt={updatedAt} />
      </div>

      {sheet?.metadata != null && <SheetMetaStrip metadata={sheet.metadata} />}

      <SheetStates
        status={status}
        error={error}
        hasPayload={hasPayload}
        rowCount={hasPayload && rows !== null ? rows.length : null}
      />

      {hasPayload && rows !== null && rows.length > 0 && columns !== null && (
        <SheetGrid
          columns={columns}
          rows={rows}
          highlight={targetHighlight(next, columns, rows)}
          selected={targetSelection(forTrialAnchor, columns, rows)}
          onSelectCell={handleSelectCell}
        />
      )}

      {hasPayload && rows !== null && endOfSheet !== null && (
        <p className="text-sm text-base-content/70" role="status">
          End of sheet — last: Plot {endOfSheet.plot} · Sub {endOfSheet.subsample}
        </p>
      )}
    </div>
  );
}
